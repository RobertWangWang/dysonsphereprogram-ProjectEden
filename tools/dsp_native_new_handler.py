"""Original new-handler call wrapper and cookie check with explicit query/user callback models."""
import argparse
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require
from dsp_native_new_handler_state import validate_cached as validate_state


def validate_cached(folder, sha, original_query=False):
    root=folder/('new-handler-query-behavior' if original_query else 'new-handler-behavior')
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'New-handler source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'New-handler evidence differs')
    report=read_json(root/'report.json')
    for name,value in report.get('dependencies',{}).items():require(digest(folder/name)==value,'New-handler dependency differs')
    return report


def validate_query_cached(folder,sha):return validate_cached(folder,sha,True)


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    parser=argparse.ArgumentParser();parser.add_argument('--original-query',action='store_true');options=parser.parse_args()
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes()
    require(struct.unpack('<I',pe_read(game,0x10b2a6e8,4))[0]==0x100569a0,'Initial guard pointer differs')
    windows=[(0x1060ed90,68),(0x105d29e6,17),(0x100569a0,1)]
    if options.original_query:
        require(validate_state(folder,module['sha256']),'Query state evidence missing')
        windows.extend([(0x1060edd4,70),(0x1060ee1d,9),(0x105d42c0,70),(0x105d4306,21)])
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    for page in (0,0x1060e000,0x105d2000,0x105d4000,0x10613000,0x10056000,0x10b2a000,0x10e24000,0x10e81000,0x200000,0x500000,0x600000):uc.mem_map(page,4096)
    cs=Cs(CS_ARCH_X86,CS_MODE_32);all_ins=set();evidence=[]
    for a,n in windows:
        raw=pe_read(game,a,n);ins=list(cs.disasm(raw,a));require(sum(i.size for i in ins)==n,'Decode incomplete')
        uc.mem_write(a,raw);all_ins.update(i.address for i in ins);evidence.append(dict(address=f'{a:x}',bytes_hex=raw.hex(),instructions=len(ins)))
    uc.mem_write(0x10b2a6e8,struct.pack('<I',0x100569a0))
    for stop in (0x500000,0x105d2db8):uc.mem_write(stop,b'\xf4')
    for stub in ([0x600000,0x10613316,0x1061335e] if options.original_query else [0x1060edd4,0x600000]):uc.mem_write(stub,b'\xc3')
    stack=0x200800;events=[];writes=[];visited=set();state={}
    saved={reg.UC_X86_REG_EBP:0x12345678,reg.UC_X86_REG_ESI:0x23456789,reg.UC_X86_REG_EBX:0x34567890,reg.UC_X86_REG_EDI:0x45678901}
    def hook(machine,address,n,unused):
        if address in (0x500000,0x105d2db8):state['stop']=address;machine.emu_stop();return
        if options.original_query and address in (0x10613316,0x1061335e):
            sp=machine.reg_read(reg.UC_X86_REG_ESP);ret,arg=struct.unpack('<II',machine.mem_read(sp,8))
            head=struct.unpack('<I',machine.mem_read(0,4))[0]
            require(head==stack-36 and struct.unpack('<I',machine.mem_read(head,4))[0]==0xffffffff,'Nested query SEH frame differs')
            events.append(['lock' if address==0x10613316 else 'unlock',arg])
            for r,v in ((reg.UC_X86_REG_EAX,0xdeadbeef),(reg.UC_X86_REG_ECX,0x12345678),(reg.UC_X86_REG_EDX,0x87654321)):machine.reg_write(r,v)
            machine.reg_write(reg.UC_X86_REG_ESP,sp+4);machine.reg_write(reg.UC_X86_REG_EIP,ret);return
        if address==0x600000 or address==0x1060edd4 and not options.original_query:
            sp=machine.reg_read(reg.UC_X86_REG_ESP);ret=struct.unpack('<I',machine.mem_read(sp,4))[0]
            if address==0x1060edd4:events.append(['query']);value=state['pointer']
            else:
                if options.original_query:require(struct.unpack('<I',machine.mem_read(0,4))[0]==0xffffffff,'Query chain not restored before callback')
                arg=struct.unpack('<I',machine.mem_read(sp+4,4))[0];events.append(['callback',arg]);value=state['result']
                if state['corrupt']:
                    old=struct.unpack('<I',machine.mem_read(stack-8,4))[0];machine.mem_write(stack-8,struct.pack('<I',old^1))
            machine.reg_write(reg.UC_X86_REG_EAX,value);machine.reg_write(reg.UC_X86_REG_ECX,0xaabbccdd);machine.reg_write(reg.UC_X86_REG_EDX,0x87654321)
            machine.reg_write(reg.UC_X86_REG_ESP,sp+4);machine.reg_write(reg.UC_X86_REG_EIP,ret);return
        require(address in all_ins,'Unexpected wrapper instruction');visited.add(address)
        if address==0x1060edd4:events.append(['query'])
        if address==0x100569a0:events.append(['guard',machine.reg_read(reg.UC_X86_REG_ECX)])
        if address==0x105d29e6:events.append(['cookie',machine.reg_read(reg.UC_X86_REG_ECX)])
    uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
    cases=0
    def check(pointer,result,size,cookie,corrupt,initial):
        nonlocal cases
        state.update(pointer=pointer,result=result,corrupt=corrupt,stop=None);events.clear();writes.clear()
        uc.mem_write(stack-128,bytes([0xa5])*160);uc.mem_write(stack,struct.pack('<II',0x500000,size));uc.mem_write(0x10e24f44,struct.pack('<I',cookie))
        if options.original_query:
            rotation=cookie&31;encoded=(((pointer<<rotation)|(pointer>>(32-rotation if rotation else 32)))&0xffffffff)^cookie
            uc.mem_write(0,struct.pack('<I',0xffffffff)+bytes([0xa5])*60)
            uc.mem_write(0x10e81d68,struct.pack('<I',encoded))
        uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2);uc.reg_write(reg.UC_X86_REG_EAX,initial)
        for r,v in saved.items():uc.reg_write(r,v)
        uc.emu_start(0x1060ed90,0,count=300)
        failure=bool(pointer and corrupt);expected=[['query']]
        if options.original_query:expected.extend([['lock',0],['unlock',0]])
        if pointer:expected.extend([['guard',pointer],['callback',size]])
        expected.append(['cookie',cookie^int(failure)])
        require(events==expected,'Handler call order or argument differs')
        require(state['stop']==(0x105d2db8 if failure else 0x500000),'Cookie destination differs')
        require(uc.reg_read(reg.UC_X86_REG_EAX)==int(bool(pointer and result)),'Handler normalized result differs')
        require(uc.reg_read(reg.UC_X86_REG_ESP)==(stack-12 if failure else stack+4),'Handler stack differs')
        for r,v in saved.items():
            require(uc.reg_read(r)==(stack-4 if failure and r==reg.UC_X86_REG_EBP else v),'Handler preserved register differs')
        require(bytes(uc.mem_read(stack,8))==struct.pack('<II',0x500000,size),'Caller arguments changed')
        guard_size=32 if options.original_query else 96
        require(bytes(uc.mem_read(stack-128,guard_size))==bytes([0xa5])*guard_size and bytes(uc.mem_read(stack+8,24))==bytes([0xa5])*24,'Stack guards changed')
        require(all(n==4 and ((stack-(96 if options.original_query else 24)<=p and p+n<=stack) or options.original_query and p==0) for p,n in writes),'Unexpected handler write')
        if options.original_query:
            require(bytes(uc.mem_read(0,64))==struct.pack('<I',0xffffffff)+bytes([0xa5])*60,'Final query chain differs')
            require(bytes(uc.mem_read(0x10e81d68,4))==struct.pack('<I',encoded),'Query changed handler slot')
        cases+=1
    for pointer in (0,0x600000):
        for result in (0,1,2,0x7fffffff,0x80000000,0xffffffff):
            for size in (0,1,144,4096,0xffffffe0,0xffffffff):
                for cookie in (0,1,0x12345678,0x80000000,0xffffffff):
                    for corrupt in (False,True):
                        for initial in (0,1,0xaaaaaaaa,0xffffffff):check(pointer,result,size,cookie,corrupt,initial)
    require(visited==all_ins,'Wrapper/cookie instruction coverage incomplete')
    require(pe_read(game,0x1060edc0,1)==b'\x40','Mutation location differs')
    uc.mem_write(0x1060edc0,b'\x48');uc.ctl_remove_cache(0x1060ed90,0x1060edd4);caught=False
    try:check(0x600000,0xffffffff,144,0x12345678,False,0)
    except ValueError as error:require(str(error)=='Handler normalized result differs','Unexpected mutation failure');caught=True
    require(caught,'Normalization mutation undetected')
    report=dict(source_sha256=module['sha256'],addresses=['1060ed90','105d29e6']+(['1060edd4'] if options.original_query else []),ranges=evidence,cases=cases,instructions=len(all_ins),negative_control_caught=caught,
                original_query=options.original_query,dependencies={'new-handler-state-behavior/manifest.json':digest(folder/'new-handler-state-behavior/manifest.json')} if options.original_query else {},
                semantics='Null queried handler returns 0. Non-null handler receives one size stack argument, caller cleans it, and any nonzero result becomes 1. Matching cookie check preserves EAX; altered saved cookie reaches 105d2db8 before wrapper returns.',
                limitation=('Original query and normal SEH prologue/epilogue execute using synthetic FS:[0]; locks and user handler remain returning models. ' if options.original_query else 'Query and user handler are returning boundary models. ')+'Initial on-disk RET guard executes, runtime guard replacement not modeled. Cookie failure stops at report entry, not real termination. No real OS thread, lock, user callback, exception dispatcher or concurrency.')
    root=folder/('new-handler-query-behavior' if options.original_query else 'new-handler-behavior');root.mkdir(exist_ok=True)
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps(report))


if __name__=='__main__':main()
