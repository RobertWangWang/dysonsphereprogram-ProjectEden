"""Original critical-section compatibility wrapper with resolver/Windows API models."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_heap_free import imports_at


def validate_cached(folder,sha):
    root=folder/'critical-section-compat-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Critical section source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Critical section evidence differs')
    return read_json(root/'report.json')


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes()
    api=imports_at(game,{0x10b2a428})[0x10b2a428];require(api['name']=='InitializeCriticalSectionAndSpinCount','Fallback import differs')
    expected_name=b'InitializeCriticalSectionEx\0'
    name=pe_read(game,0x10bb605c,len(expected_name));require(name==expected_name,'Dynamic API name differs')
    ids=list(struct.unpack('<II',pe_read(game,0x10bbd730,8)));require(ids==[8,18],'Candidate IDs differ')
    require(struct.unpack('<I',pe_read(game,0x10b2a6e8,4))[0]==0x100569a0,'Initial guard differs')
    windows=[(0x10618a07,98),(0x105d29e6,17),(0x100569a0,1)]
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    for page in (0x10618000,0x105d2000,0x10056000,0x10b2a000,0x10e24000,0x200000,0x500000,0x600000):uc.mem_map(page,4096)
    cs=Cs(CS_ARCH_X86,CS_MODE_32);all_ins=set();evidence=[]
    for a,n in windows:
        raw=pe_read(game,a,n);ins=list(cs.disasm(raw,a));require(sum(i.size for i in ins)==n,'Decode incomplete')
        uc.mem_write(a,raw);all_ins.update(i.address for i in ins);evidence.append(dict(address=f'{a:x}',bytes_hex=raw.hex(),instructions=len(ins)))
    uc.mem_write(0x10b2a6e8,struct.pack('<I',0x100569a0));uc.mem_write(0x10b2a428,struct.pack('<I',0x600010))
    for stub in (0x106183ea,0x600000,0x600010):uc.mem_write(stub,b'\xc3')
    for stop in (0x500000,0x105d2db8):uc.mem_write(stop,b'\xf4')
    stack=0x200800;state={};events=[];writes=[];visited=set()
    saved={reg.UC_X86_REG_EBP:0x12345678,reg.UC_X86_REG_EBX:0x23456789,reg.UC_X86_REG_ESI:0x34567890,reg.UC_X86_REG_EDI:0x45678901}
    def hook(machine,address,n,unused):
        if address in (0x500000,0x105d2db8):state['stop']=address;machine.emu_stop();return
        if address in (0x106183ea,0x600000,0x600010):
            sp=machine.reg_read(reg.UC_X86_REG_ESP);ret=struct.unpack('<I',machine.mem_read(sp,4))[0]
            argc=4 if address==0x106183ea else 3 if address==0x600000 else 2
            args=list(struct.unpack('<'+'I'*argc,machine.mem_read(sp+4,argc*4)))
            events.append(['resolve' if address==0x106183ea else 'modern' if address==0x600000 else 'fallback',args])
            if address==0x106183ea:value=0x600000 if state['available'] else 0;pop=0
            else:
                value=state['result'];pop=argc*4
                if state['corrupt']:
                    cookie=struct.unpack('<I',machine.mem_read(stack-8,4))[0];machine.mem_write(stack-8,struct.pack('<I',cookie^1))
            for r,v in ((reg.UC_X86_REG_EAX,value),(reg.UC_X86_REG_ECX,0xabcdef01),(reg.UC_X86_REG_EDX,0x76543210)):machine.reg_write(r,v)
            machine.reg_write(reg.UC_X86_REG_ESP,sp+4+pop);machine.reg_write(reg.UC_X86_REG_EIP,ret);return
        require(address in all_ins,'Unexpected compatibility instruction');visited.add(address)
        if address==0x100569a0:events.append(['guard',machine.reg_read(reg.UC_X86_REG_ECX)])
        if address==0x105d29e6:events.append(['cookie',machine.reg_read(reg.UC_X86_REG_ECX)])
    uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
    cases=0
    def check(available,pointer,spin,flags,result,cookie,corrupt):
        nonlocal cases
        state.update(available=available,result=result,corrupt=corrupt,stop=None);events.clear();writes.clear()
        uc.mem_write(stack-128,bytes([0xa5])*192);args=struct.pack('<IIII',0x500000,pointer,spin,flags);uc.mem_write(stack,args)
        uc.mem_write(0x10e24f44,struct.pack('<I',cookie));uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        for r,v in saved.items():uc.reg_write(r,v)
        uc.emu_start(0x10618a07,0,count=150)
        expected=[['resolve',[20,0x10bb605c,0x10bbd730,0x10bbd738]]]
        if available:expected.extend([['guard',0x600000],['modern',[pointer,spin,flags]]])
        else:expected.append(['fallback',[pointer,spin]])
        expected.append(['cookie',cookie^int(corrupt)])
        require(events==expected,'Compatibility call sequence differs')
        require(state['stop']==(0x105d2db8 if corrupt else 0x500000),'Compatibility return destination differs')
        require(uc.reg_read(reg.UC_X86_REG_EAX)==result,'System result was not preserved')
        require(uc.reg_read(reg.UC_X86_REG_ESP)==(stack-12 if corrupt else stack+16),'Stdcall stack differs')
        require(all(uc.reg_read(r)==(stack-4 if corrupt and r==reg.UC_X86_REG_EBP else v) for r,v in saved.items()),'Preserved registers differ')
        require(bytes(uc.mem_read(stack,16))==args and bytes(uc.mem_read(stack+16,48))==bytes([0xa5])*48 and bytes(uc.mem_read(stack-128,64))==bytes([0xa5])*64,'Caller memory or guards differ')
        require(all(n==4 and stack-36<=p and p+4<=stack for p,n in writes),'Unexpected compatibility write');cases+=1
    for available in (False,True):
        for pointer in (0,0x10e81e60,0x10e81f80,0xffffffff):
            for spin in (0,1,4000,0xffffffff):
                for flags in (0,1,0x01000000,0xffffffff):
                    for result in (0,1,2,0x80000000,0xffffffff):
                        for cookie in (0,0x12345678,0xffffffff):
                            for corrupt in (False,True):check(available,pointer,spin,flags,result,cookie,corrupt)
    require(visited==all_ins,'Compatibility instruction coverage incomplete')
    require(pe_read(game,0x10618a39,1)==b'\x10','Mutation location differs')
    uc.mem_write(0x10618a39,b'\x0c');uc.ctl_remove_cache(0x10618a07,0x10618a69);caught=False
    try:check(True,0x10e81e60,4000,0x01000000,1,0x12345678,False)
    except ValueError as error:require(str(error)=='Compatibility call sequence differs','Unexpected mutation failure');caught=True
    require(caught,'Wrong modern flags undetected')
    report=dict(source_sha256=module['sha256'],addresses=['10618a07'],ranges=evidence,cases=cases,instructions=len(all_ins),negative_control_caught=caught,
                fallback_import=api,resolver_id=20,module_candidates=ids,
                semantics='Resolve InitializeCriticalSectionEx; if found pass all three arguments through guard, else call InitializeCriticalSectionAndSpinCount with first two and omit flags. EAX API result is preserved on matching cookie check; wrapper returns stdcall with 12-byte cleanup.',
                limitation='Resolver and Windows API calls are returning models, including invalid-pointer arithmetic inputs; no real critical sections initialized. Initial RET guard only. Cookie mismatch stops at report entry; resolver caching, OS exceptions, concurrency and runtime guard not executed.')
    root=folder/'critical-section-compat-behavior';root.mkdir(exist_ok=True)
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps(report))


if __name__=='__main__':main()
