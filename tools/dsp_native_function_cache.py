"""Original dynamic function cache with modeled module lookup and GetProcAddress."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_heap_free import imports_at


def validate_cached(folder,sha):
    root=folder/'function-cache-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Function cache source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Function cache evidence differs')
    return read_json(root/'report.json')


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes()
    api=imports_at(game,{0x10b2a384})[0x10b2a384];require(api['name']=='GetProcAddress','Resolver import differs')
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    for page in (0x10618000,0x105fa000,0x10b2a000,0x10e24000,0x10e82000,0x200000,0x300000,0x500000,0x600000):uc.mem_map(page,4096)
    windows=[(0x106183ea,156),(0x105fa164,29)];all_ins=set();evidence=[];cs=Cs(CS_ARCH_X86,CS_MODE_32)
    for a,n in windows:
        raw=pe_read(game,a,n);ins=list(cs.disasm(raw,a));require(sum(i.size for i in ins)==n,'Decode incomplete')
        uc.mem_write(a,raw);all_ins.update(i.address for i in ins);evidence.append(dict(address=f'{a:x}',bytes_hex=raw.hex(),instructions=len(ins)))
    uc.mem_write(0x10b2a384,struct.pack('<I',0x600000))
    for stub in (0x600000,0x10618486):uc.mem_write(stub,b'\xc3')
    uc.mem_write(0x500000,b'\xf4');stack=0x200800;array=0x300100;name=0x300200;cache=0x10e82128
    uc.mem_write(name,b'InitializeCriticalSectionEx\0')
    state,events,writes,visited={},[],[],set()
    saved={reg.UC_X86_REG_EBP:0x12345678,reg.UC_X86_REG_EBX:0x23456789,reg.UC_X86_REG_ESI:0x34567890,reg.UC_X86_REG_EDI:0x45678901}
    def hook(machine,address,n,unused):
        if address==0x500000:state['done']=True;machine.emu_stop();return
        if address in (0x600000,0x10618486):
            sp=machine.reg_read(reg.UC_X86_REG_ESP);ret=struct.unpack('<I',machine.mem_read(sp,4))[0]
            if address==0x10618486:
                index=state['module_index'];require(index<len(state['modules']),'Extra module lookup')
                arg=struct.unpack('<I',machine.mem_read(sp+4,4))[0];expected,value=state['modules'][index]
                require(arg==expected,'Module ID differs');events.append(['module',arg]);state['module_index']+=1;pop=0
            else:
                args=list(struct.unpack('<II',machine.mem_read(sp+4,8)));events.append(['proc',args]);value=state['proc'];pop=8
            for r,v in ((reg.UC_X86_REG_EAX,value),(reg.UC_X86_REG_ECX,0xaabbccdd),(reg.UC_X86_REG_EDX,0x76543210)):machine.reg_write(r,v)
            machine.reg_write(reg.UC_X86_REG_ESP,sp+4+pop);machine.reg_write(reg.UC_X86_REG_EIP,ret);return
        require(address in all_ins,'Unexpected cache instruction');visited.add(address)
    uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
    def encode(value,cookie):
        n=cookie&31
        return (((value<<n)|(value>>(32-n if n else 32)))&0xffffffff)^cookie
    cases=0
    def check(identifier,old,cookie,modules,proc):
        nonlocal cases
        raw=bytearray([0xa5])*256;offset=identifier*4;struct.pack_into('<I',raw,offset,encode(old,cookie))
        uc.mem_write(cache,bytes(raw));uc.mem_write(0x10e24f44,struct.pack('<I',cookie))
        array_mem=b''.join(struct.pack('<I',item[0]) for item in modules);uc.mem_write(array,bytes([0xa5])*64)
        if array_mem:uc.mem_write(array,array_mem)
        expected_events=[];result=old;new=old
        if old==0xffffffff:result=0
        elif old==0:
            handle=0
            for module_id,value in modules:
                expected_events.append(['module',module_id]);handle=value
                if handle:break
            if handle:expected_events.append(['proc',[handle,name]])
            result=proc if handle else 0;new=result if result else 0xffffffff
        expected=bytearray(raw);struct.pack_into('<I',expected,offset,encode(new,cookie))
        for repeat in (False,True):
            state.update(done=False,modules=modules,module_index=0,proc=proc);events.clear();writes.clear()
            uc.mem_write(stack-128,bytes([0xa5])*160);arguments=struct.pack('<5I',0x500000,identifier,name,array,array+len(array_mem));uc.mem_write(stack,arguments)
            uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
            for r,v in saved.items():uc.reg_write(r,v)
            uc.emu_start(0x106183ea,0,count=300)
            require(state['done'] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'Cache return stack differs')
            require(uc.reg_read(reg.UC_X86_REG_EAX)==result,'Cache result differs')
            require(events==([] if repeat else expected_events),'Lookup sequence differs')
            require(bytes(uc.mem_read(cache,256))==bytes(expected),'Encoded cache differs')
            require(all(uc.reg_read(r)==v for r,v in saved.items()),'Cache preserved registers differ')
            require(bytes(uc.mem_read(stack,20))==arguments,'Cache arguments changed')
            require(bytes(uc.mem_read(array,64))==array_mem+bytes([0xa5])*(64-len(array_mem)),'Module array changed')
            cache_writes=[(p,n) for p,n in writes if p>=cache]
            require(cache_writes==([(cache+offset,4)] if old==0 and not repeat else []),'Cache update count differs')
            require(all(n==4 and (stack-64<=p and p+4<=stack or p==cache+offset) for p,n in writes),'Unexpected cache write')
            cases+=1
    groups=[[],[(8,0)],[(8,0),(18,0)],[(8,0x710000),(18,0x720000)],[(8,0),(18,0x720000)],[(8,0),(18,0),(7,0x730000)]]
    for rotation in range(32):
        for high in (0,0x12345660,0xffffffe0):
            for old in (0,0xffffffff,1,0x700000,0x80000000):
                for modules in groups:
                    for proc in (0,1,0x740000,0x80000000):
                        for identifier in (0,20,31):check(identifier,old,high|rotation,modules,proc)
    require(visited==all_ins,'Cache instruction coverage incomplete')
    require(pe_read(game,0x1061847d,2)==b'\x87\x3b','Mutation location differs')
    uc.mem_write(0x1061847d,b'\x8b\xff');uc.ctl_remove_cache(0x106183ea,0x10618486);caught=False
    try:check(20,0,0x12345661,[],0)
    except ValueError as error:require(str(error)=='Encoded cache differs','Unexpected mutation failure');caught=True
    require(caught,'Missing negative cache update undetected')
    report=dict(source_sha256=module['sha256'],addresses=['106183ea','105fa164'],ranges=evidence,cases=cases,instructions=len(all_ins),negative_control_caught=caught,proc_import=api,
                semantics='Decoded cache zero means unresolved, FFFFFFFF means cached failure returning zero, other values return directly. Module search stops at first nonzero module handle. Exactly one GetProcAddress follows; failure does not try later modules. Success or failure is encoded and published by memory XCHG. A repeat call uses either cache without external lookup.',
                limitation='Module lookup and GetProcAddress are returning models, finite valid ID/array buffers, no actual loading or symbol resolution. Sequential execution checks XCHG effects, not concurrent race/linearizability. Cookie stable within each call; invalid pointers, unbounded IDs and OS errors not validated.')
    root=folder/'function-cache-behavior';root.mkdir(exist_ok=True)
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps(report))


if __name__=='__main__':main()
