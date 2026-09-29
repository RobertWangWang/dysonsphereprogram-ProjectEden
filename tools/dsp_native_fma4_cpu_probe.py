"""模拟原始 CPU 位映射及三处融合运算开关片段，不执行 CPUID 或 FMA4 本机指令。"""
import hashlib
import json
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

ENTRY=0x1813f0490
END=0x1813f0568
GATES=[('1813b1070',0x1813b10e0,0x1813b10f7,0x70),
       ('1813b5460',0x1813b54d3,0x1813b54ea,0x62),
       ('1813b9780',0x1813b97e0,0x1813b97f7,0x32)]


def validate_cached(folder,sha):
    root=folder/'fma4-cpu'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'CPU evidence baseline changed')
    for name,expected in marker['files'].items():require(digest(root/name)==expected,'CPU evidence changed')
    return read_json(root/'report.json')


def main():
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE
    from unicorn import x86_const as r
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    folder=GENERATED/'native/UnityPlayer.dll'
    game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game changed');game=game_path.read_bytes()
    raw=pe_read(game,ENTRY,END-ENTRY);decoder=Cs(CS_ARCH_X86,CS_MODE_64)
    ins=list(decoder.disasm(raw,ENTRY));require(sum(i.size for i in ins)==len(raw),'Incomplete CPU helper decoding')
    require([i.address for i in ins if i.mnemonic=='cpuid']==[0x1813f04b3,0x1813f0530,0x1813f0540],'CPUID sites changed')
    m=Uc(UC_ARCH_X86,UC_MODE_64)
    for page in (ENTRY&~4095,0x181c41000,0x181814000,0x3000000,0x4000000):m.mem_map(page,4096)
    m.mem_write(ENTRY,raw);cookie=0x123456789abcdef
    m.mem_write(0x181c41228,cookie.to_bytes(8,'little'));m.mem_write(0x181814290,b'\xc3')
    stack=0x3000808;stop=0x4000000;m.mem_write(stack,stop.to_bytes(8,'little'))
    state={};visited=set()
    def visit(uc,pc,size,_):
        visited.add(pc)
        if pc in (0x1813f04b3,0x1813f0530,0x1813f0540):
            leaf=uc.reg_read(r.UC_X86_REG_EAX);state['leaves'].append(leaf)
            values=(0,0,state['ecx'],state['edx']) if leaf==1 else ((state['max_leaf'],0,0,0) if leaf==0x80000000 else (0,0,state['extended'],0))
            for reg,value in zip((r.UC_X86_REG_EAX,r.UC_X86_REG_EBX,r.UC_X86_REG_ECX,r.UC_X86_REG_EDX),values):uc.reg_write(reg,value)
            uc.reg_write(r.UC_X86_REG_RIP,pc+size)
        elif pc==0x181814290:
            require(uc.reg_read(r.UC_X86_REG_RCX)==cookie,'Cookie reconstruction differs')
        elif pc==stop:
            state['result']=uc.reg_read(r.UC_X86_REG_EAX);uc.emu_stop()
    m.hook_add(UC_HOOK_CODE,visit)
    digest_cases=hashlib.sha256()
    edx_bits=[23,25,26];ecx_bits=[0,9,19,20,27,28,12,29]
    for flags in range(8192):
        edx=sum(((flags>>n)&1)<<bit for n,bit in enumerate(edx_bits))
        ecx=sum(((flags>>(n+3))&1)<<bit for n,bit in enumerate(ecx_bits))
        extended=((flags>>11)&1)<<16;available=bool(flags&4096)
        state.update(ecx=ecx,edx=edx,extended=extended,max_leaf=0x80000001 if available else 0x80000000,leaves=[],result=None)
        m.reg_write(r.UC_X86_REG_RSP,stack);m.reg_write(r.UC_X86_REG_RBX,0x99887766);m.reg_write(r.UC_X86_REG_EFLAGS,2)
        m.emu_start(ENTRY,0,count=200)
        expected=sum(((edx>>bit)&1)<<n for n,bit in enumerate(edx_bits))
        expected|=sum(((ecx>>bit)&1)<<(n+3) for n,bit in enumerate(ecx_bits[:4]))
        if ecx&(1<<27):
            for bit,out in ((28,8),(12,10),(29,9)):expected|=((ecx>>bit)&1)<<out
            if available and extended&(1<<16):expected|=128
        require(state['result']==expected,'CPU bit mapping differs')
        require(state['leaves']==[1,0x80000000]+([0x80000001] if available else []),'CPUID leaf sequence differs')
        require(m.reg_read(r.UC_X86_REG_RSP)==stack+8 and m.reg_read(r.UC_X86_REG_RBX)==0x99887766,'CPU helper ABI differs')
        digest_cases.update(flags.to_bytes(2,'little')+expected.to_bytes(2,'little'))
    require({i.address for i in ins}<=visited,'CPU helper instruction not exercised')
    gates=[]
    for name,start,end,offset in GATES:
        gate=Uc(UC_ARCH_X86,UC_MODE_64)
        for page in (start&~4095,0x181c80000,0x3000000):gate.mem_map(page,4096)
        code=pe_read(game,start,end-start);gate.mem_write(start,code)
        for mask in range(256):
            for override in (0,1,255):
                gate.mem_write(0x181c80780,bytes([override]));gate.reg_write(r.UC_X86_REG_RAX,mask);gate.reg_write(r.UC_X86_REG_RSP,0x3000800)
                gate.emu_start(start,end,count=20)
                require(gate.mem_read(0x3000800+offset,1)[0]==int(bool(mask&128) and override==0),'FMA gate differs')
        gates.append(dict(address=name,start=f'{start:x}',end_exclusive=f'{end:x}',stack_offset=offset,cases=768,code_sha256=hashlib.sha256(code).hexdigest()))
    root=folder/'fma4-cpu';root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    report=dict(status='cpu-bitmapping-and-entry-gates-verified',source_sha256=GAME_SHA,helper=f'{ENTRY:x}',
        helper_code_sha256=hashlib.sha256(raw).hexdigest(),cpu_cases=8192,cpu_cases_sha256=digest_cases.hexdigest(),gates=gates,
        gate_expression='local_flag = ((helper_result & 0x80) != 0) && (byte_at_181c80780 == 0)',
        bit7_expression='CPUID(1).ECX[27] && max_extended_leaf >= 0x80000001 && CPUID(0x80000001).ECX[16]',
        all_helper_instructions_exercised=True,xgetbv_in_helper=any(i.mnemonic=='xgetbv' for i in ins),
        limitation='Controlled CPUID replies and security-cookie RET stub. Bit combinations and local gate only; no OS vector-state validation, global override initialization, all-input function behavior or FMA arithmetic proof.')
    (root/'helper.asm').write_text(''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in ins),encoding='utf-8')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={n:digest(root/n) for n in ('helper.asm','report.json')}),indent=2),encoding='utf-8')
    print('8192 CPU bit combinations and 2304 entry gate cases passed')


if __name__=='__main__':main()
