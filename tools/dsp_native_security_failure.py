"""Emulate stack-cookie failure reporting with explicit, never executed OS boundaries."""
import itertools
import argparse
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_heap_free import imports_at


def validate_cached(folder,sha,kind='security-failure-behavior'):
    root=folder/kind
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Security failure source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Security failure evidence differs')
    report=read_json(root/'report.json')
    for name,value in report.get('dependencies',{}).items():require(digest(folder/name)==value,'Security dependency differs')
    return report


def validate_compat_cached(folder,sha):
    return validate_cached(folder,sha,'security-failure-compat-behavior')


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--compat',action='store_true');options=parser.parse_args();compat=options.compat
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE
    import unicorn.x86_const as r
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes()
    slots={0x10b2a2dc:'IsProcessorFeaturePresent',0x10b2a46c:'SetUnhandledExceptionFilter',0x10b2a2e0:'UnhandledExceptionFilter',0x10b2a104:'GetCurrentProcess',0x10b2a284:'TerminateProcess'}
    imports=imports_at(game,set(slots));require(all(imports[a]['name']==n for a,n in slots.items()),'Import identity differs')
    pointer=pe_read(game,0x10bb7298,8);require(struct.unpack('<II',pointer)==(0x10e7bd60,0x10e7bdb0),'Exception pointers differ')
    windows=[(0x105d2db8,251),(0x105d2d90,40),(0x10629b08,6)]
    dependencies={}
    if compat:
        from dsp_native_critical_section_compat import validate_cached as validate_wrapper
        require(validate_wrapper(folder,module['sha256']) and validate_cached(folder,module['sha256']),'Missing chain evidence')
        dependencies={name:digest(folder/name) for name in ('critical-section-compat-behavior/manifest.json','security-failure-behavior/manifest.json')}
        windows.extend([(0x10618a07,98),(0x105d29e6,17),(0x100569a0,1)])
        require(imports_at(game,{0x10b2a428})[0x10b2a428]['name']=='InitializeCriticalSectionAndSpinCount','Fallback differs')
        require(struct.unpack('<I',pe_read(game,0x10b2a6e8,4))[0]==0x100569a0,'Guard target differs')
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    for page in (0x105d2000,0x10629000,0x10b2a000,0x10bb7000,0x10e7b000,0x10e24000,0x200000,0x500000,0x600000):uc.mem_map(page,4096)
    if compat:
        for page in (0x10618000,0x10056000):uc.mem_map(page,4096)
    cs=Cs(CS_ARCH_X86,CS_MODE_32);instructions={};evidence=[]
    for a,n in windows:
        raw=pe_read(game,a,n);decoded=list(cs.disasm(raw,a));require(sum(i.size for i in decoded)==n,'Decode incomplete')
        instructions.update({i.address:i for i in decoded});uc.mem_write(a,raw);evidence.append(dict(address=f'{a:x}',bytes_hex=raw.hex(),instructions=len(decoded)))
    uc.mem_write(0x10bb7298,pointer);uc.mem_write(0x500000,b'\xf4')
    if compat:
        uc.mem_write(0x10b2a6e8,struct.pack('<I',0x100569a0));uc.mem_write(0x10b2a428,struct.pack('<I',0x600110))
        for stub in (0x106183ea,0x600100,0x600110):uc.mem_write(stub,b'\xc3')
    apis={}
    for index,(slot,name) in enumerate(slots.items()):
        stub=0x600000+index*16;apis[stub]=name;uc.mem_write(slot,struct.pack('<I',stub));uc.mem_write(stub,b'\xc3')
    state={};events=[];visited=set();snap={}
    def get(a):return struct.unpack('<I',uc.mem_read(a,4))[0]
    def hook(machine,a,n,unused):
        if a==0x500000:state['stop']='returned';machine.emu_stop();return
        if compat and a in (0x106183ea,0x600100,0x600110):
            sp=machine.reg_read(r.UC_X86_REG_ESP);argc=4 if a==0x106183ea else 3 if a==0x600100 else 2
            args=[get(sp+4+i*4) for i in range(argc)];events.append(['resolve' if a==0x106183ea else 'modern' if a==0x600100 else 'fallback',args])
            value=(0x600100 if state['available'] else 0) if a==0x106183ea else state['api_result']
            if a!=0x106183ea and state['corrupt']:uc.mem_write(state['stack']-8,struct.pack('<I',get(state['stack']-8)^1))
            for reg,val in ((r.UC_X86_REG_EAX,value),(r.UC_X86_REG_ECX,0xabcdef01),(r.UC_X86_REG_EDX,0x76543210)):machine.reg_write(reg,val)
            machine.reg_write(r.UC_X86_REG_EIP,get(sp));machine.reg_write(r.UC_X86_REG_ESP,sp+4+(0 if a==0x106183ea else 4*argc));return
        if a in apis:
            name=apis[a];sp=machine.reg_read(r.UC_X86_REG_ESP);argc={'IsProcessorFeaturePresent':1,'SetUnhandledExceptionFilter':1,'UnhandledExceptionFilter':1,'GetCurrentProcess':0,'TerminateProcess':2}[name]
            args=[get(sp+4+4*i) for i in range(argc)];events.append([name,args])
            if name=='TerminateProcess' and state['terminate'] is None:state['stop']='terminate-boundary';machine.emu_stop();return
            value=state['feature'] if name=='IsProcessorFeaturePresent' else state['handle'] if name=='GetCurrentProcess' else state['terminate'] if name=='TerminateProcess' else 0x12345678
            machine.reg_write(r.UC_X86_REG_EAX,value);machine.reg_write(r.UC_X86_REG_ECX,0xabcdef01);machine.reg_write(r.UC_X86_REG_EDX,0x76543210)
            machine.reg_write(r.UC_X86_REG_EIP,get(sp));machine.reg_write(r.UC_X86_REG_ESP,sp+4+argc*4);return
        require(a in instructions,'Security report escaped');visited.add(a)
        if compat and a==0x100569a0:require(machine.reg_read(r.UC_X86_REG_ECX)==0x600100,'Guard target differs')
        if a==0x105d2dcf:
            require(machine.reg_read(r.UC_X86_REG_ECX)==2,'Fastfail reason differs');state['stop']='int29';machine.emu_stop();return
        if a==0x105d2dd1:
            for reg in (r.UC_X86_REG_EAX,r.UC_X86_REG_ECX,r.UC_X86_REG_EDX,r.UC_X86_REG_EFLAGS):snap[reg]=machine.reg_read(reg)
    uc.hook_add(UC_HOOK_CODE,hook)
    def check(feature,terminate,handle,cookie,seed,stack,available=False,corrupt=True,api_result=1):
        state.clear();state.update(feature=feature,terminate=terminate,handle=handle,available=available,corrupt=corrupt,api_result=api_result,stack=stack);events.clear();snap.clear()
        uc.mem_write(0x200000,b'\xa5'*4096);uc.mem_write(0x10e7b000,b'\xa5'*4096)
        uc.mem_write(stack,struct.pack('<II',0x500000,0x13572468));uc.mem_write(0x10e24f40,struct.pack('<II',cookie^0xffffffff,cookie))
        if compat:uc.mem_write(stack,struct.pack('<IIII',0x500000,0x13572468,4000,0x01000000))
        saved={r.UC_X86_REG_EBP:seed,r.UC_X86_REG_EBX:seed^0x11111111,r.UC_X86_REG_ESI:seed^0x22222222,r.UC_X86_REG_EDI:seed^0x33333333}
        for reg,value in saved.items():uc.reg_write(reg,value)
        uc.reg_write(r.UC_X86_REG_ESP,stack);uc.reg_write(r.UC_X86_REG_EFLAGS,2);uc.emu_start(0x10618a07 if compat else 0x105d2db8,0,count=250)
        if compat:require(bytes(uc.mem_read(stack,16))==struct.pack('<IIII',0x500000,0x13572468,4000,0x01000000),'Compatibility caller arguments modified')
        expected=[]
        if compat:
            expected=[['resolve',[20,0x10bb605c,0x10bbd730,0x10bbd738]],['modern',[0x13572468,4000,0x01000000]] if available else ['fallback',[0x13572468,4000]]]
            if not corrupt:
                require(events==expected and state.get('stop')=='returned','Uncorrupted compatibility flow differs')
                require(uc.reg_read(r.UC_X86_REG_EAX)==api_result and uc.reg_read(r.UC_X86_REG_ESP)==stack+16,'Uncorrupted ABI differs')
                require(all(uc.reg_read(reg)==v for reg,v in saved.items()),'Uncorrupted registers differ')
                require(bytes(uc.mem_read(0x10e7b000,4096))==b'\xa5'*4096,'Uncorrupted context modified')
                return 'normal-return'
        expected.append(['IsProcessorFeaturePresent',[23]])
        if feature:
            require(state.get('stop')=='int29' and events==expected,'Fastfail path differs')
            require(bytes(uc.mem_read(0x10e7b000,4096))==b'\xa5'*4096,'Fastfail unexpectedly wrote context')
            return 'int29'
        expected.extend([['SetUnhandledExceptionFilter',[0]],['UnhandledExceptionFilter',[0x10bb7298]],['GetCurrentProcess',[]],['TerminateProcess',[handle,0xc0000409]]])
        require(events==expected,'Security API sequence differs')
        stop='terminate-boundary' if terminate is None else 'returned';require(state.get('stop')==stop,'Security terminal differs')
        reporter_stack=stack-12 if compat else stack;reporter_return=0x10618a63 if compat else 0x500000
        values={0x10e7bd60:0xc0000409,0x10e7bd64:1,0x10e7bd6c:reporter_return,0x10e7bd70:1,0x10e7bd74:2,0x10e7bdb0:0x10001,
                0x10e7be60:snap[r.UC_X86_REG_EAX],0x10e7be5c:snap[r.UC_X86_REG_ECX],0x10e7be58:snap[r.UC_X86_REG_EDX],
                0x10e7be54:saved[r.UC_X86_REG_EBX],0x10e7be50:saved[r.UC_X86_REG_ESI],0x10e7be4c:saved[r.UC_X86_REG_EDI],
                0x10e7be64:stack-4 if compat else seed,0x10e7be68:reporter_return,0x10e7be74:reporter_stack+4,0x10e7be70:snap[r.UC_X86_REG_EFLAGS]}
        require(all(get(a)==v for a,v in values.items()),'Security context differs')
        for addr,reg in ((0x10e7be78,r.UC_X86_REG_SS),(0x10e7be6c,r.UC_X86_REG_CS),(0x10e7be48,r.UC_X86_REG_DS),(0x10e7be44,r.UC_X86_REG_ES),(0x10e7be40,r.UC_X86_REG_FS),(0x10e7be3c,r.UC_X86_REG_GS)):
            require(struct.unpack('<H',uc.mem_read(addr,2))[0]==uc.reg_read(reg),'Segment snapshot differs')
        require(get(reporter_stack-12)==cookie and get(reporter_stack-8)==cookie^0xffffffff,'Local cookie copies differ')
        require(get(stack+4)==0x13572468 and bytes(uc.mem_read(0x200000,64))==b'\xa5'*64,'Stack guard differs')
        if terminate is not None:
            require(uc.reg_read(r.UC_X86_REG_EAX)==terminate and uc.reg_read(r.UC_X86_REG_ESP)==stack+(16 if compat else 4),'Returning model ABI differs')
            require(all(uc.reg_read(reg)==v for reg,v in saved.items()),'Callee saved registers differ')
        return stop
    counts={};cases=0
    for args in itertools.product((0,1,0xffffffff),(None,0,1,0xffffffff),(0,0x700000,0xffffffff),(0,0x12345678,0xffffffff),(0,0x23456789,0xffffffff),(0x200800,0x200c00)):
        for suffix in itertools.product((False,True),(False,True),(0,1,0xffffffff)) if compat else [()]:
            stop=check(*args,*suffix);counts[stop]=counts.get(stop,0)+1;cases+=1
    require(visited==set(instructions),'Security instruction coverage incomplete')
    # Mutate only the synthetic emulator image; source DLL remains untouched.
    uc.mem_write(0x105d2da5,struct.pack('<I',0xc0000408));uc.ctl_remove_cache(0x105d2d90,0x105d2db8);caught=False
    try:check(0,0,0xffffffff,0x12345678,0,0x200800)
    except ValueError as error:require(str(error)=='Security API sequence differs','Unexpected negative failure');caught=True
    require(caught,'Wrong termination code escaped validation')
    result=dict(source_sha256=module['sha256'],addresses=['10618a07'] if compat else ['105d2db8','105d2d90'],ranges=evidence,imports={f'{a:x}':v for a,v in imports.items()},exception_pointers_hex=pointer.hex(),dependencies=dependencies,
                cases=cases,terminals=counts,instructions=len(instructions),negative_control_caught=caught,
                semantics='Feature 23 nonzero reaches INT29 with ECX=2. Otherwise captures context and exception C0000409, resets unhandled filter, calls filter then TerminateProcess(GetCurrentProcess(),C0000409). If the modeled API returns, both original functions restore their frames and RET with its EAX.',
                limitation='Windows APIs are explicit models; no real termination or exception dispatch. INT29 is a stop boundary, not simulated OS handling. Segment values use flat emulator defaults. Return-path reachability under returning models does not claim real self-termination returns; static noreturn annotation is not byte-level proof.')
    if compat:
        result['semantics']+=' Original compatibility wrapper and cookie check connected: matching cookie preserves initialization result; mismatch reaches actual reporter. Under returning termination model, wrapper returns termination result with stdcall 12-byte cleanup; context return PC=10618a63.'
        result['limitation']+=' Resolver and initialization APIs are models; existing cache/lock pipeline evidence remains separate.'
    root=folder/('security-failure-compat-behavior' if compat else 'security-failure-behavior');root.mkdir(exist_ok=True);(root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps(result))


if __name__=='__main__':main()
