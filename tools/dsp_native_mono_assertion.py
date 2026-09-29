"""Probe original Mono assertion wrappers and six excluded fallthrough prefixes."""
import itertools
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_select import selected_folder
from dsp_native_mono_body import validate_cached as validate_body


def validate_cached(folder,sha):
    root=folder/'mono-assertion-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Mono assertion source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Mono assertion evidence differs')
    report=read_json(root/'report.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Mono assertion dependency differs')
    return report


def main():
    from dsp_native_sha512_unwind import imports
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE
    import unicorn.x86_const as r
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='mono-2.0-bdwgc.dll')
    folder=selected_folder(GENERATED/'native',module);path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes()
    body=validate_body(folder,module['sha256']);edges=body['out_of_body_edges'];require(len(edges)==6 and all(e['callee']=='18005c960' for e in edges),'Assertion callers differ')
    iat=0x1805832e0;api=imports(game)[iat];require(api[1]=='RaiseException','Exception import differs')
    fmt=0x180681ba8;message=b'* Assertion: should not be reached at %s:%d\n\0';require(pe_read(game,fmt,len(message))==message,'Assertion format differs')
    failure=0x180750370;cs=Cs(CS_ARCH_X86,CS_MODE_64);cs.detail=True;instructions={};evidence=[]
    uc=Uc(UC_ARCH_X86,UC_MODE_64)
    for p in (0x18005c000,iat&~4095,failure&~4095,0x200000,0x500000,0x600000):uc.mem_map(p,4096)
    for a,n in ((0x18005c960,18),(0x18005c8d0,77)):
        raw=pe_read(game,a,n);decoded=list(cs.disasm(raw,a));require(sum(i.size for i in decoded)==n,'Assertion decode differs');instructions.update({i.address:i for i in decoded});uc.mem_write(a,raw);evidence.append(dict(address=f'{a:x}',bytes_hex=raw.hex(),instructions=len(decoded)))
    uc.mem_write(iat,struct.pack('<Q',0x600000));uc.mem_write(0x600000,b'\xc3');uc.mem_write(0x18005c6c0,b'\xc3');uc.mem_write(0x500000,b'\xf4')
    state={};events=[];visited=set();stack=0x200808
    def q(a):return struct.unpack('<Q',uc.mem_read(a,8))[0]
    def hook(machine,a,n,unused):
        if a==0x500000:state['stop']='returned';machine.emu_stop();return
        if a in (0x18005c6c0,0x600000):
            sp=machine.reg_read(r.UC_X86_REG_RSP);args=[machine.reg_read(reg) for reg in (r.UC_X86_REG_RCX,r.UC_X86_REG_RDX,r.UC_X86_REG_R8,r.UC_X86_REG_R9)]
            events.append(['log' if a==0x18005c6c0 else 'raise',args])
            if a==0x18005c6c0:
                require([q(args[3]+i*8) for i in range(3)]==[state['file'],state['line']&0xffffffff,0x123456789abcdef0],'Assertion variadic values differ');value=state['log_result']
            else:
                if state['raise_result'] is None:state['stop']='raise-boundary';machine.emu_stop();return
                value=state['raise_result']
            for reg,v in ((r.UC_X86_REG_RAX,value),(r.UC_X86_REG_RCX,0xdeadbeef),(r.UC_X86_REG_RDX,0x11223344),(r.UC_X86_REG_R8,0x55667788),(r.UC_X86_REG_R9,0x99aabbcc)):machine.reg_write(reg,v)
            machine.reg_write(r.UC_X86_REG_RIP,q(sp));machine.reg_write(r.UC_X86_REG_RSP,sp+8);return
        require(a in instructions,'Assertion escaped');visited.add(a)
    uc.hook_add(UC_HOOK_CODE,hook)
    def check(file,line,log_result,raise_result):
        state.clear();state.update(file=file,line=line,log_result=log_result,raise_result=raise_result);events.clear();uc.mem_write(0x200000,b'\xa5'*4096);uc.mem_write(stack,struct.pack('<Q',0x500000));uc.mem_write(failure,b'\xa5'*8)
        for reg,value in ((r.UC_X86_REG_RCX,file),(r.UC_X86_REG_RDX,line),(r.UC_X86_REG_R9,0x123456789abcdef0),(r.UC_X86_REG_RSP,stack)):uc.reg_write(reg,value)
        uc.emu_start(0x18005c960,0,count=60)
        require(events==[['log',[0,4,fmt,stack+16]],['raise',[0xe0000001,1,0,0]]],'Assertion call arguments differ')
        require(q(failure)==log_result,'Failure pointer store differs')
        require(state.get('stop')==('raise-boundary' if raise_result is None else 'returned'),'Assertion terminal differs')
        if raise_result is not None:require(uc.reg_read(r.UC_X86_REG_RSP)==stack+8 and uc.reg_read(r.UC_X86_REG_RAX)==raise_result,'Assertion return path differs')
        require(q(stack)==0x500000 and bytes(uc.mem_read(stack+40,32))==b'\xa5'*32,'Assertion caller guards differ')
    cases=0
    for args in itertools.product((0,0x700000,0xffffffffffffffff),(0,1,0xffffffff,0x1234567800000011),(0,0x700123,0xffffffffffffffff),(None,0,1,0xffffffffffffffff)):check(*args);cases+=1
    require(visited==set(instructions),'Assertion coverage incomplete')
    uc.mem_write(0x18005c911,b'\x00');uc.ctl_remove_cache(0x18005c8d0,0x18005c91d);caught=False
    try:check(0,0,0,0)
    except ValueError as error:require(str(error)=='Assertion call arguments differ','Unexpected negative failure');caught=True
    require(caught,'Wrong exception flags undetected')
    prefixes=[];prefix_cases=0
    for edge in edges:
        start=int(edge['target'],16);decoded=list(cs.disasm(pe_read(game,start,16),start))[:3]
        require([i.mnemonic for i in decoded]==['xor','test','jne'] and decoded[0].op_str==decoded[1].op_str=='eax, eax','Unexpected assertion prefix')
        end=decoded[-1].address+decoded[-1].size;back=decoded[-1].operands[0].imm;raw=pe_read(game,start,end-start)
        model=Uc(UC_ARCH_X86,UC_MODE_64)
        for page in {start&~4095,end&~4095,back&~4095}:model.mem_map(page,4096)
        model.mem_write(start,raw);model.mem_write(end,b'\xf4');model.mem_write(back,b'\xf4');observed={}
        def prefix_hook(m,a,n,u):
            if a in (end,back):observed['stop']=a;m.emu_stop()
        model.hook_add(UC_HOOK_CODE,prefix_hook)
        for value,flags in itertools.product((0,1,0x80000000,0xffffffffffffffff),(2,0x42,0x882,0x8c2)):
            observed.clear();model.reg_write(r.UC_X86_REG_RAX,value);model.reg_write(r.UC_X86_REG_EFLAGS,flags);model.emu_start(start,0,count=5)
            require(observed.get('stop')==end and model.reg_read(r.UC_X86_REG_RAX)==0,'Assertion prefix did not fall through');prefix_cases+=1
        prefixes.append(dict(address=f'{start:x}',bytes_hex=raw.hex(),instructions=3,fallthrough=f'{end:x}',never_taken_backedge=f'{back:x}'))
    result=dict(source_sha256=module['sha256'],ranges=evidence,cases=cases,instructions=len(instructions),prefixes=prefixes,prefix_cases=prefix_cases,raise_import=list(api),negative_control_caught=caught,
                dependencies={'mono-body-evidence/manifest.json':digest(folder/'mono-body-evidence/manifest.json')},
                semantics='Original wrappers pass file and uint32 line through a variadic log call, store its returned failure pointer, then RaiseException(E0000001,1,0,0). Under a returning exception API model the wrapper RET is reachable. All six excluded xor/test/jne prefixes set EAX=0 and always fall through.',
                limitation='Logger and RaiseException are explicit models; logger implementation and real exception dispatch not executed. Returned RAX is a machine effect, not a declared C return contract. Prefix feasibility is conditional on reaching it; does not claim real noncontinuable exceptions return or remaining case bodies are recovered.')
    root=folder/'mono-assertion-behavior';root.mkdir(exist_ok=True);(root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps(result))


if __name__=='__main__':main()
