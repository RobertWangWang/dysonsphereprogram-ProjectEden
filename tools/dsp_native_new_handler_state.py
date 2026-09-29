"""Execute original new-handler query/set including normal x86 SEH prologue/epilogue."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require


def validate_cached(folder,sha):
    root=folder/'new-handler-state-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Handler state source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Handler state evidence differs')
    return read_json(root/'report.json')


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes()
    windows=[(0x1060edd4,70),(0x1060ee1d,9),(0x1060ee26,84),(0x1060ee7d,9),
             (0x1060ef08,31),(0x105d42c0,70),(0x105d4306,21)]
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    # Unicorn's initial 32-bit flat FS base is zero; this page models FS:[0].
    for page in (0,0x1060e000,0x105d4000,0x10613000,0x10e24000,0x10e81000,0x200000,0x500000):uc.mem_map(page,4096)
    cs=Cs(CS_ARCH_X86,CS_MODE_32);all_ins=set();evidence=[]
    for a,n in windows:
        raw=pe_read(game,a,n);ins=list(cs.disasm(raw,a));require(sum(i.size for i in ins)==n,'Decode incomplete')
        uc.mem_write(a,raw);all_ins.update(i.address for i in ins);evidence.append(dict(address=f'{a:x}',bytes_hex=raw.hex(),instructions=len(ins)))
    uc.mem_write(0x500000,b'\xf4')
    for stub in (0x10613316,0x1061335e):uc.mem_write(stub,b'\xc3')
    state,events,writes,visited={},[],[],set()
    saved={reg.UC_X86_REG_EBP:0x12345678,reg.UC_X86_REG_EBX:0x23456789,reg.UC_X86_REG_ESI:0x34567890,reg.UC_X86_REG_EDI:0x45678901}
    def hook(machine,address,n,unused):
        if address==0x500000:state['done']=True;machine.emu_stop();return
        if address in (0x10613316,0x1061335e):
            sp=machine.reg_read(reg.UC_X86_REG_ESP);ret,arg=struct.unpack('<II',machine.mem_read(sp,8))
            head=struct.unpack('<I',machine.mem_read(0,4))[0]
            require(head==state['stack']-20,'Active SEH chain head differs')
            chain,handler,scope,phase=struct.unpack('<IIII',machine.mem_read(head,16))
            require((chain,handler,scope,phase)==(state['chain'],0x105fcea0,state['scope']^state['cookie'],0xfffffffe),'Active SEH frame differs')
            events.append(['lock' if address==0x10613316 else 'unlock',arg])
            for r,v in ((reg.UC_X86_REG_EAX,0xdeadbeef),(reg.UC_X86_REG_ECX,0x76543210),(reg.UC_X86_REG_EDX,0xabcdef01)):machine.reg_write(r,v)
            machine.reg_write(reg.UC_X86_REG_ESP,sp+4);machine.reg_write(reg.UC_X86_REG_EIP,ret);return
        require(address in all_ins,'Unexpected state instruction');visited.add(address)
    uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n,v&((1<<(8*n))-1))))
    def encode(pointer,cookie):
        n=cookie&31
        return (((pointer<<n)|(pointer>>(32-n if n else 32)))&0xffffffff)^cookie
    cases=0
    def check(setting,old,new,cookie,chain,stack):
        nonlocal cases
        state.update(done=False,stack=stack,chain=chain,cookie=cookie,scope=0x10d5f490 if setting else 0x10d5f470)
        events.clear();writes.clear()
        uc.mem_write(0,struct.pack('<I',chain)+bytes([0xa5])*60)
        uc.mem_write(stack-256,bytes([0xa5])*320);uc.mem_write(stack,struct.pack('<II',0x500000,new))
        globals_=bytearray([0xa5])*32;struct.pack_into('<I',globals_,8,encode(old,cookie));expected=bytearray(globals_)
        if setting:struct.pack_into('<I',expected,8,encode(new,cookie))
        uc.mem_write(0x10e81d60,bytes(globals_));uc.mem_write(0x10e24f44,struct.pack('<I',cookie))
        uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        for r,v in saved.items():uc.reg_write(r,v)
        uc.emu_start(0x1060ee26 if setting else 0x1060edd4,0,count=300)
        require(state['done'] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'State return stack differs')
        require(uc.reg_read(reg.UC_X86_REG_EAX)==old,'Previous handler return differs')
        require(all(uc.reg_read(r)==v for r,v in saved.items()),'State callee-saved registers differ')
        require(events==[['lock',0],['unlock',0]],'State lock sequence differs')
        require(bytes(uc.mem_read(0,64))==struct.pack('<I',chain)+bytes([0xa5])*60,'SEH chain restoration differs')
        require(bytes(uc.mem_read(0x10e81d60,32))==bytes(expected),'Handler slot differs')
        require(bytes(uc.mem_read(stack,64))==struct.pack('<II',0x500000,new)+bytes([0xa5])*56,'Caller memory differs')
        require(bytes(uc.mem_read(stack-256,128))==bytes([0xa5])*128,'Lower stack guard differs')
        require([(p,n,v) for p,n,v in writes if p==0]==[(0,4,stack-20),(0,4,chain)],'SEH publication sequence differs')
        require(all(n==4 and (stack-128<=p and p+4<=stack or p==0 or setting and p==0x10e81d68) for p,n,v in writes),'Unexpected state write')
        cases+=1
    for setting in (False,True):
        for rotation in range(32):
            for high in (0,0x12345660,0xffffffe0):
                for old in (0,1,0x600000,0xffffffff):
                    for new in (0,1,0x601000,0xffffffff):
                        for chain in (0,0x12340000,0xffffffff):
                            for stack in (0x200800,0x200c00):check(setting,old,new,high|rotation,chain,stack)
    require(visited==all_ins,'State instruction coverage incomplete')
    require(pe_read(game,0x1060ee62,4)==struct.pack('<I',0x10e81d68),'Mutation location differs')
    uc.mem_write(0x1060ee62,struct.pack('<I',0x10e81d6c));uc.ctl_remove_cache(0x1060ee26,0x1060ee7a);caught=False
    try:check(True,0x600000,0x601000,0x12345661,0xffffffff,0x200800)
    except ValueError as error:require(str(error)=='Handler slot differs','Unexpected mutation failure');caught=True
    require(caught,'Wrong slot undetected')
    report=dict(source_sha256=module['sha256'],addresses=['1060edd4','1060ee26','105d42c0','105d4306'],ranges=evidence,cases=cases,instructions=len(all_ins),negative_control_caught=caught,
                semantics='Query returns decoded prior handler without mutation. Setter returns decoded prior handler and writes ROL32(new,cookie&31) XOR cookie to 10e81d68. Both call lock/unlock(0). Original normal SEH prologue publishes encoded scope frame and epilogue restores previous FS chain and saved registers.',
                limitation='Synthetic FS base-zero page and mapped stack. Lock implementations are returning models. Actual exception dispatcher/unwind, scope-table execution, concurrency and real thread environment are not executed. Normal query/set bodies tested; exceptional cleanup entry prefixes excluded.')
    root=folder/'new-handler-state-behavior';root.mkdir(exist_ok=True)
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps(report))


if __name__=='__main__':main()
