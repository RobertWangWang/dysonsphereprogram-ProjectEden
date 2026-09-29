"""离线执行两个原始 SEH 处理器；系统展开调用仅作为可观测的返回桩。"""
import hashlib
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require

BASE=0x180000000
ARENA=0x2000000
CONTEXT=ARENA+0x1000
DISPATCH=ARENA+0x2000
DATA=ARENA+0x2100
COPY=ARENA+0x3000
FRAME=ARENA+0x5180
HOME=ARENA+0x8000
API=0x4000000
RETURN=0x4000100
SIZE=0x10000


def validate_cached(folder,source_sha):
    root=folder/'seh-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json')
    require(marker['source_sha256']==source_sha,'SEH behavior baseline changed')
    for name,sha in marker['files'].items():require(digest(root/name)==sha,'SEH behavior evidence changed')
    report=read_json(root/'report.json')
    for family,sha in report['source_evidence'].items():
        require(digest(folder/'upstream-openssl'/family/'manifest.json')==sha,'SEH source evidence changed')
    return report


def run(game,report,routine,rip,variant,corrupt=False):
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    from unicorn import x86_const as r
    aes=report['name'].startswith('aesni')
    unwind=pe_read(game,int(routine['unwind_address'],16),16)
    _,_,prologue,epilogue=struct.unpack('<IIII',unwind)
    prologue+=BASE;epilogue+=BASE
    body=prologue<=rip<epilogue
    shaext=aes and 'shaext' in routine['name']
    avx2='avx2' in routine['name']
    raw=bytearray(((i*37+variant*19)^(i>>4)^(i>>12))&255 for i in range(SIZE))
    def put(address,value):struct.pack_into('<Q',raw,address-ARENA,value)
    def get(address):return struct.unpack_from('<Q',raw,address-ARENA)[0]
    put(CONTEXT+120,HOME);put(CONTEXT+152,FRAME if body else HOME);put(CONTEXT+248,rip)
    adjusted=((FRAME & (~1023 if aes else ~2047))+(448 if aes else 1152)) if avx2 else FRAME
    resolved=FRAME+168 if shaext and body else HOME
    # Saved scalar frame points 48 bytes below the restored stack pointer.
    if body and not shaext:put(adjusted+(120 if aes else 152),HOME-48)
    put(HOME-8,0 if variant%2==0 else 0x10000000000)
    for off in (-16,-24,-32,-40,-48):put(HOME+off,0x77770000+variant*100-off)
    put(resolved+8,0x88880000+variant);put(resolved+16,0x99990000+variant)
    put(DISPATCH,rip);put(DISPATCH+8,BASE);put(DISPATCH+16,ARENA+0x2200)
    put(DISPATCH+40,COPY);put(DISPATCH+56,DATA)
    raw[DATA-ARENA:DATA-ARENA+8]=unwind[8:16]
    expected=bytearray(raw)
    def expected_qword(off,value):struct.pack_into('<Q',expected,CONTEXT-ARENA+off,value)
    if body:
        if not shaext:
            for dest,source in [(144,-8),(160,-16),(216,-24),(224,-32),(232,-40),(240,-48)]:expected_qword(dest,get(HOME+source))
        # SHA-512's code compares the restored RBX against its scalar epilogue.
        vector_copy=aes or get(HOME-8)>=0x18001c996
        if vector_copy:
            source=FRAME if shaext else adjusted+(128 if aes else 160)
            count=160 if aes else 96
            expected[CONTEXT-ARENA+512:CONTEXT-ARENA+512+count]=raw[source-ARENA:source-ARENA+count]
    expected_qword(152,resolved);expected_qword(168,get(resolved+16));expected_qword(176,get(resolved+8))
    expected[COPY-ARENA:COPY-ARENA+1232]=expected[CONTEXT-ARENA:CONTEXT-ARENA+1232]
    m=Uc(UC_ARCH_X86,UC_MODE_64)
    entry=int(report['address'],16);end=int(report['end_exclusive'],16);iat=int(report['import_reference']['address'],16)
    for page in sorted({entry&~4095,iat&~4095,API,0x3000000}):m.mem_map(page,4096)
    m.mem_write(entry,pe_read(game,entry,end-entry));m.mem_write(iat,API.to_bytes(8,'little'));m.mem_write(API,b'\xc3')
    if corrupt:
        # Corrupt the actual context-copy count, not the independent expected model.
        code=pe_read(game,entry,end-entry);pattern=b'\xb9\x9a\x00\x00\x00'
        require(code.count(pattern)==1,'Missing context copy count')
        m.mem_write(entry+code.index(pattern)+1,b'\x99')
    m.mem_map(ARENA,SIZE);m.mem_write(ARENA,bytes(raw))
    stack=0x3000808;m.mem_write(stack,RETURN.to_bytes(8,'little'))
    m.reg_write(r.UC_X86_REG_RSP,stack);m.reg_write(r.UC_X86_REG_R8,CONTEXT);m.reg_write(r.UC_X86_REG_R9,DISPATCH)
    regs=[r.UC_X86_REG_RBX,r.UC_X86_REG_RBP,r.UC_X86_REG_RSI,r.UC_X86_REG_RDI,r.UC_X86_REG_R12,r.UC_X86_REG_R13,r.UC_X86_REG_R14,r.UC_X86_REG_R15]
    for n,reg in enumerate(regs):m.reg_write(reg,0xabc00000+n)
    m.reg_write(r.UC_X86_REG_EFLAGS,0x202)
    calls=[];returns=[];visited=set()
    def instruction(uc,pc,size,_):
        visited.add(pc)
        if pc==API:
            sp=uc.reg_read(r.UC_X86_REG_RSP)
            args=[uc.reg_read(reg) for reg in (r.UC_X86_REG_RCX,r.UC_X86_REG_RDX,r.UC_X86_REG_R8,r.UC_X86_REG_R9)]
            args.extend(struct.unpack('<QQQQ',bytes(uc.mem_read(sp+40,32))))
            calls.append(args)
        if pc==RETURN:returns.append(uc.reg_read(r.UC_X86_REG_RAX));uc.emu_stop()
    def write(uc,access,address,size,value,_):
        require(any(a<=address and address+size<=b for a,b in [(CONTEXT,CONTEXT+1232),(COPY,COPY+1232),(0x3000000,0x3001000)]),'Unexpected handler write')
    m.hook_add(UC_HOOK_CODE,instruction);m.hook_add(UC_HOOK_MEM_WRITE,write)
    m.emu_start(entry,0,count=10000)
    require(bytes(m.mem_read(ARENA,SIZE))==bytes(expected),'Context or copied bytes differ')
    require(calls==[[0,BASE,rip,ARENA+0x2200,COPY,DISPATCH+56,DISPATCH+24,0]],'Unwind arguments differ')
    require(returns==[1] and m.reg_read(r.UC_X86_REG_RSP)==stack+8,'Return or stack differs')
    require(all(m.reg_read(reg)==0xabc00000+n for n,reg in enumerate(regs)),'Nonvolatile GPR changed')
    require(m.reg_read(r.UC_X86_REG_EFLAGS)==0x202,'Flags not restored')
    return dict(routine=routine['name'],rip=f'{rip:x}',variant=variant,body=body,shaext=shaext,
                memory_sha256=hashlib.sha256(expected).hexdigest()),visited


def main():
    import unicorn
    module='DSPGAME_Data/Plugins/x86_64/rail_api64.dll'
    folder=GENERATED/'native'/module.replace('/','__')
    game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/module
    game=game_path.read_bytes();sha=digest(game_path);results=[];sources={};coverage={}
    for family in ('sha512-seh','aesni-sha256-seh'):
        root=folder/'upstream-openssl'/family;marker=read_json(root/'manifest.json')
        require(marker['source_sha256']==sha,'Game baseline changed')
        for name,expected in marker['files'].items():require(digest(root/name)==expected,'Evidence changed')
        report=read_json(root/'report.json');sources[family]=digest(root/'manifest.json');seen=set()
        for routine in report['runtime_functions']:
            _,_,pro,epi=struct.unpack('<IIII',pe_read(game,int(routine['unwind_address'],16),16));pro+=BASE;epi+=BASE
            points=sorted({int(routine['begin'],16),pro-1,pro,(pro+epi)//2,epi-1,epi,int(routine['end_exclusive'],16)-1})
            for rip in points:
                for variant in range(4):
                    row,visited=run(game,report,routine,rip,variant);results.append(row);seen|=visited
        try:run(game,report,routine,pro,0,corrupt=True)
        except ValueError as error:require(str(error)=='Context or copied bytes differ','Unexpected negative failure')
        else:raise ValueError('Shortened context copy accepted')
        instructions={int(line.split()[0],16) for line in (root/'game.asm').read_text(encoding='utf-8').splitlines() if line.strip()}
        require(instructions<=seen,'Some handler instructions were not exercised')
        coverage[family]=[f'{a:x}' for a in sorted(seen)]
    root=folder/'seh-behavior';root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    report=dict(status='sampled-seh-context-transfer-verified',source_sha256=sha,engine='Unicorn '+unicorn.__version__,
        source_evidence=sources,cases=results,case_count=len(results),visited_addresses=coverage,short_copy_negatives_rejected=2,
        all_handler_instructions_exercised=True,
        limitation='Synthetic mapped contexts and boundary samples. Original handler instructions execute; RtlVirtualUnwind is a RET stub. No Windows unwinder, real exception injection, dynamic registration, or all-input correctness proof.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=sha,files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(f'{len(results)} handler cases passed; two corrupted context-copy lengths rejected')


if __name__=='__main__':main()
