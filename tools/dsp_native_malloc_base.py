"""Probe original malloc core/retry loop with explicit HeapAlloc/new-handler/TLS models."""
import json
import argparse
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require
from dsp_native_heap_free import imports_at


def validate_cached(folder, sha, original_handler=False):
    root = folder / ('malloc-handler-chain-behavior' if original_handler else 'malloc-base-behavior')
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json')
    require(marker['source_sha256'] == sha, 'Malloc source differs')
    for name, value in marker['files'].items():
        require(digest(root / name) == value, 'Malloc evidence differs')
    report=read_json(root / 'report.json')
    for name,value in report.get('dependencies',{}).items():require(digest(folder/name)==value,'Malloc dependency differs')
    return report


def validate_chain_cached(folder,sha):return validate_cached(folder,sha,True)


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--original-handler',action='store_true');options=parser.parse_args();chain=options.original_handler
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv = read_json(GENERATED / 'native/inventory.json')
    module = next(m for m in inv['files'] if Path(m['path']).name == 'rail_api.dll')
    folder = GENERATED / 'native' / module['output']; path = Path(inv['game_directory']) / module['path']
    require(digest(path) == module['sha256'], 'Source changed'); game = path.read_bytes()
    api = imports_at(game, {0x10b2a42c})[0x10b2a42c]
    require(api['name'] == 'HeapAlloc', 'Heap import differs')
    windows = [(0x10613c79,78),(0x1060ed47,6),(0x10603aac,19)]
    dependencies={}
    if chain:
        from dsp_native_new_handler import validate_query_cached
        require(validate_cached(folder,module['sha256']) and validate_query_cached(folder,module['sha256']),'Missing chain prerequisites')
        dependencies={name:digest(folder/name) for name in ('malloc-base-behavior/manifest.json','new-handler-query-behavior/manifest.json')}
        windows.extend([(0x1060ed90,68),(0x105d29e6,17),(0x100569a0,1),(0x1060edd4,70),(0x1060ee1d,9),(0x105d42c0,70),(0x105d4306,21)])
        require(struct.unpack('<I',pe_read(game,0x10b2a6e8,4))[0]==0x100569a0,'Initial guard differs')
    uc = Uc(UC_ARCH_X86, UC_MODE_32)
    for page in (0x10613000,0x1060e000,0x10603000,0x10617000,0x10b2a000,0x10e81000,
                 0x10e82000,0x10e47000,0x200000,0x300000,0x500000,0x600000): uc.mem_map(page,4096)
    if chain:
        for page in (0,0x105d2000,0x105d4000,0x10056000,0x10e24000):uc.mem_map(page,4096)
    cs = Cs(CS_ARCH_X86, CS_MODE_32); all_ins, evidence = set(), []
    for a,n in windows:
        raw = pe_read(game,a,n); ins = list(cs.disasm(raw,a)); require(sum(i.size for i in ins)==n,'Decode incomplete')
        uc.mem_write(a,raw); all_ins.update(i.address for i in ins); evidence.append(dict(address=f'{a:x}',bytes_hex=raw.hex(),instructions=len(ins)))
    for stub in ((0x600000,0x600100,0x10613316,0x1061335e,0x10617acd) if chain else (0x600000,0x1060ed90,0x10617acd)): uc.mem_write(stub,b'\xc3')
    if chain:uc.mem_write(0x10b2a6e8,struct.pack('<I',0x100569a0))
    uc.mem_write(0x500000,b'\xf4'); uc.mem_write(0x10b2a42c,struct.pack('<I',0x600000))
    stack,thread = 0x200800,0x300100
    state,events,writes,visited = {},[],[],set()
    preserved = {reg.UC_X86_REG_EBP:0xa1a2a3a4,reg.UC_X86_REG_ESI:0xb1b2b3b4,reg.UC_X86_REG_EDI:0xc1c2c3c4,reg.UC_X86_REG_EBX:0xd1d2d3d4}
    def code(machine,address,n,unused):
        if address==0x500000: state['done']=True; machine.emu_stop(); return
        if chain and address in (0x10613316,0x1061335e):
            sp=machine.reg_read(reg.UC_X86_REG_ESP);ret,arg=struct.unpack('<II',machine.mem_read(sp,8))
            head=struct.unpack('<I',machine.mem_read(0,4))[0]
            require(head==state['handler_stack']-36 and struct.unpack('<I',machine.mem_read(head,4))[0]==0xffffffff,'Malloc query SEH chain differs')
            events.append(['lock' if address==0x10613316 else 'unlock',arg])
            for r,v in ((reg.UC_X86_REG_EAX,0xdeadbeef),(reg.UC_X86_REG_ECX,0x12345678),(reg.UC_X86_REG_EDX,0x87654321)):machine.reg_write(r,v)
            machine.reg_write(reg.UC_X86_REG_ESP,sp+4);machine.reg_write(reg.UC_X86_REG_EIP,ret);return
        if address in ((0x600000,0x600100,0x10617acd) if chain else (0x600000,0x1060ed90,0x10617acd)):
            sp=machine.reg_read(reg.UC_X86_REG_ESP); ret=struct.unpack('<I',machine.mem_read(sp,4))[0]; pop=0
            if address==0x600000:
                args=list(struct.unpack('<III',machine.mem_read(sp+4,12))); events.append(['heap',args]); pop=12
                index=state['heap_index']; require(index<len(state['heap_results']),'Unexpected extra allocation')
                value=state['heap_results'][index];state['heap_index']+=1
            elif address in (0x1060ed90,0x600100):
                arg=struct.unpack('<I',machine.mem_read(sp+4,4))[0];events.append(['callback' if chain else 'new_handler',arg])
                if chain:require(struct.unpack('<I',machine.mem_read(0,4))[0]==0xffffffff,'SEH chain not restored before handler')
                index=state['handler_index'];require(index<len(state['handlers']),'Unexpected extra handler')
                value,mode=state['handlers'][index];state['handler_index']+=1
                if mode is not None:machine.mem_write(0x10e81d64,struct.pack('<I',mode))
            else:events.append(['tls']);value=thread if state['tls'] else 0
            machine.reg_write(reg.UC_X86_REG_EAX,value);machine.reg_write(reg.UC_X86_REG_ECX,0x12345678);machine.reg_write(reg.UC_X86_REG_EDX,0x87654321)
            machine.reg_write(reg.UC_X86_REG_ESP,sp+4+pop);machine.reg_write(reg.UC_X86_REG_EIP,ret);return
        require(address in all_ins,'Unexpected code');visited.add(address)
        if chain and address==0x1060ed90:
            state['handler_stack']=machine.reg_read(reg.UC_X86_REG_ESP);events.append(['new_handler',struct.unpack('<I',machine.mem_read(state['handler_stack']+4,4))[0]])
        if chain and address==0x100569a0:require(machine.reg_read(reg.UC_X86_REG_ECX)==0x600100,'Handler guard target differs')
    uc.hook_add(UC_HOOK_CODE,code);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
    scenarios = [([0x700123],[]),([0],[(0,None)]),([0,0x700123],[(1,None)]),
                 ([0,0,0x700123],[(1,None),(1,None)]),([0,0x700123],[(0xffffffff,None)]),
                 ([0,0],[(1,0)]),([0,0],[(1,None),(0,None)]),([0,0x700123],[(1,0)])]
    cases=0
    def check(size,mode,tls,heap,scenario,present=True,cookie=0):
        nonlocal cases
        results,handlers=scenario
        state.update(done=False,heap_results=results,handlers=handlers,heap_index=0,handler_index=0,tls=tls)
        events.clear();writes.clear()
        uc.mem_write(stack,struct.pack('<II',0x500000,size));uc.mem_write(0x10e81d64,struct.pack('<I',mode));uc.mem_write(0x10e823cc,struct.pack('<I',heap))
        uc.mem_write(thread,bytes([0xa5])*64);uc.mem_write(0x10e47330,bytes([0xa5])*48)
        if chain:
            uc.mem_write(0,struct.pack('<I',0xffffffff));uc.mem_write(0x10e24f44,struct.pack('<I',cookie))
            pointer=0x600100 if present else 0;rotation=cookie&31
            encoded=(((pointer<<rotation)|(pointer>>(32-rotation if rotation else 32)))&0xffffffff)^cookie
            uc.mem_write(0x10e81d68,struct.pack('<I',encoded))
        uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        for r,v in preserved.items():uc.reg_write(r,v)
        expected=[]; result=0; current_mode=mode; h=0; q=0
        if size<=0xffffffe0:
            while True:
                expected.append(['heap',[heap,0,max(size,1)]]);result=results[h];h+=1
                if result or not current_mode:break
                expected.append(['new_handler',max(size,1)])
                if chain:
                    expected.extend([['lock',0],['unlock',0]])
                    if not present:break
                    expected.append(['callback',max(size,1)])
                value,change=handlers[q];q+=1
                if change is not None:current_mode=change
                if not value:break
        if not result:expected.append(['tls'])
        uc.emu_start(0x10613c79,0,count=1000)
        require(state['done'] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'Malloc return stack differs')
        require(uc.reg_read(reg.UC_X86_REG_EAX)==result,'Malloc result differs')
        require(events==expected and state['heap_index']==h and state['handler_index']==q,'Malloc event sequence differs')
        require(all(uc.reg_read(r)==v for r,v in preserved.items()),'Malloc preserved registers differ')
        for base,n,offset in ((thread,64,16),(0x10e47330,48,16)):
            memory=bytearray([0xa5])*n
            if not result and ((base==thread)==bool(tls)):struct.pack_into('<I',memory,offset,12)
            require(bytes(uc.mem_read(base,n))==bytes(memory),'Malloc errno differs')
        require(struct.unpack('<I',uc.mem_read(0x10e81d64,4))[0]==current_mode,'Retry mode differs')
        require(all(n==4 and (stack-(160 if chain else 64)<=p<stack or p==(thread+16 if tls else 0x10e47340) or chain and p==0) for p,n in writes),'Unexpected malloc write')
        if chain:
            require(struct.unpack('<I',uc.mem_read(0,4))[0]==0xffffffff,'Final SEH chain differs')
            require(struct.unpack('<I',uc.mem_read(0x10e81d68,4))[0]==encoded,'Handler slot changed')
        cases+=1
    sizes=list(range(65))+[127,128,144,255,256,4096,0x7fffffff,0x80000000,0xffffffdf,0xffffffe0,0xffffffe1,0xfffffffe,0xffffffff]
    for size in sizes:
        for mode in (0,1,0xffffffff):
            for tls in (0,1):
                for heap in (0x12345678,0xffffffff):
                    for scenario in scenarios:
                        for present,cookie in ([(p,c) for p in (False,True) for c in (0,0x12345678,0xffffffff)] if chain else [(True,0)]):check(size,mode,tls,heap,scenario,present,cookie)
    missing=all_ins-visited
    require(missing==({0x105d29f1} if chain else set()),'Malloc instruction coverage differs')
    require(pe_read(game,0x10613c8b,1)==b'\x46','Mutation site differs')
    uc.mem_write(0x10613c8b,b'\x4e');uc.ctl_remove_cache(0x10613c79,0x10613cc7);caught=False
    try:check(0,0,1,0x12345678,scenarios[0])
    except ValueError as error:require(str(error)=='Malloc event sequence differs','Unexpected mutation failure');caught=True
    require(caught,'Wrong zero-size adjustment undetected')
    negative_controls=['zero-size INC changed to DEC']
    if chain:
        uc.mem_write(0x10613c8b,b'\x46');uc.ctl_remove_cache(0x10613c79,0x10613cc7)
        require(pe_read(game,0x1060edc0,1)==b'\x40','Handler mutation site differs')
        uc.mem_write(0x1060edc0,b'\x90');uc.ctl_remove_cache(0x1060ed90,0x1060edd4);handler_caught=False
        try:check(144,1,1,0x12345678,scenarios[2],True,0x12345678)
        except ValueError as error:require(str(error)=='Malloc result differs','Unexpected handler mutation failure');handler_caught=True
        require(handler_caught,'Wrong handler normalization undetected');negative_controls.append('handler success INC changed to NOP')
    report=dict(source_sha256=module['sha256'],addresses=['10613c79','1060ed47'],ranges=evidence,cases=cases,instructions=len(all_ins),
                visited_instructions=len(visited),unvisited=[f'{a:x}' for a in sorted(missing)],dependencies=dependencies,
                negative_control_caught=caught,negative_controls=negative_controls,heap_import=api,
                semantics='Requests above FFFFFFE0 fail with errno 12 without HeapAlloc. Zero requests become one. HeapAlloc(heap,0,size) success returns pointer; failure retries only when global mode is nonzero and callnewh returns nonzero. Mode is read again after each failed allocation. Final failure stores errno 12 through original TLS/fallback selection.',
                limitation='HeapAlloc, callnewh and TLS provider are explicit returning models. No real allocation, callback implementation, exceptions, concurrency or infinite retry behavior executed. Finite bounded result sequences, including simulated huge requests.')
    if chain:
        report['semantics']+=' Actual callnewh, encoded handler query, normal SEH prolog/epilog and cookie check are connected. Null handler suppresses retry; nonzero callback results normalize to 1. Query lock 0 is released and FS chain restored before callback; callback mode changes are reread on the next allocation failure.'
        report['limitation']='HeapAlloc, TLS provider, lock/unlock and user callback implementations are explicit returning models. Flat FS base zero is synthetic. Cookie matches only; failure jump is unvisited. Exceptions, real locking/concurrency and infinite retries not executed.'
    root=folder/('malloc-handler-chain-behavior' if chain else 'malloc-base-behavior');root.mkdir(exist_ok=True)
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps(report))


if __name__=='__main__':main()
