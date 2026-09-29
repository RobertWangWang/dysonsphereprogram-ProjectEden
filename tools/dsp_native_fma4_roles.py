"""保存 FMA4 三过程的辐照度任务调用证据；用途标签不是原始符号恢复。"""
import hashlib
import json
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

ROLES={
    '1813b1070':'方向性辐照度任务分支；两个附加输出指针至少一个为空',
    '1813b5460':'方向性辐照度任务分支；两个附加输出指针均非空',
    '1813b9780':'无方向性输出的辐照度任务分支，亦用于反弹缓冲任务',
}


def validate_cached(folder,sha):
    root=folder/'fma4-roles'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'FMA roles baseline changed')
    for name,expected in marker['files'].items():require(digest(root/name)==expected,'FMA roles evidence changed')
    return read_json(root/'report.json')


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64,CS_GRP_CALL
    from capstone.x86 import X86_OP_IMM,X86_OP_MEM,X86_REG_RIP
    folder=GENERATED/'native/UnityPlayer.dll'
    game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game baseline changed');game=game_path.read_bytes()
    marker=read_json(folder/'decompile-manifest.json')
    require(marker['files']['functions.json']==digest(folder/'functions.json'),'Function index changed')
    functions={f['address']:f for f in read_json(folder/'functions.json')['functions']}
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True
    callers=[];artifacts={}
    for address,label,expected_targets in [
        ('1813c01c0','SolveBounceBufferTask',['1813b9780']),
        ('1813c1f50','SolveIrradianceTask',['1813b5460','1813b1070','1813b9780'])]:
        function=functions[address];start=int(address,16);raw=pe_read(game,start,function['size'])
        ins=list(decoder.disasm(raw,start));require(sum(i.size for i in ins)==len(raw),'Incomplete caller decoding')
        calls=[];strings=[]
        for n,i in enumerate(ins):
            if i.group(CS_GRP_CALL) and i.operands[0].type==X86_OP_IMM:
                target=f'{i.operands[0].imm:x}'
                if target in ROLES:
                    calls.append(dict(address=f'{i.address:x}',target=target,
                        context=[f'{p.address:x} {p.bytes.hex()} {p.mnemonic} {p.op_str}' for p in ins[max(0,n-18):n+3]]))
            if i.mnemonic=='lea':
                for op in i.operands:
                    if op.type==X86_OP_MEM and op.mem.base==X86_REG_RIP:
                        target=i.address+i.size+op.mem.disp
                        try:blob=pe_read(game,target,200).split(b'\0',1)[0];text=blob.decode('ascii')
                        except (ValueError,UnicodeDecodeError):continue
                        if label in text:strings.append(dict(instruction=f'{i.address:x}',address=f'{target:x}',text=text))
        require([c['target'] for c in calls]==expected_targets,'Caller edges changed')
        require(strings,'Task name has no verified RIP-relative string reference')
        artifacts[address+'.asm']=''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in ins)
        artifacts[address+'.c']=(folder/function['file']).read_text(encoding='utf-8')
        callers.append(dict(address=address,label=label,code_bytes=len(raw),code_sha256=hashlib.sha256(raw).hexdigest(),calls=calls,task_strings=strings))
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE
    from unicorn import x86_const as regs
    selection=[]
    raw=pe_read(game,0x1813c20e3,0x1813c2174-0x1813c20e3)
    callsites={i.address:i.operands[0].imm for i in decoder.disasm(raw,0x1813c20e3) if i.group(CS_GRP_CALL)}
    for flags in range(32):
        mode,directional,precomputed,extra_a,extra_b=[bool(flags&(1<<n)) for n in range(5)]
        m=Uc(UC_ARCH_X86,UC_MODE_64)
        for page in (0x1813c2000,0x1000000,0x2000000,0x3000000):m.mem_map(page,4096)
        m.mem_write(0x1813c20e3,raw)
        m.mem_write(0x100003e,int(mode).to_bytes(2,'little'));m.mem_write(0x1000028,bytes([precomputed]))
        for offset,present in ((0x48,directional),(0x50,extra_a),(0x58,extra_b)):
            m.mem_write(0x2000000+offset,int(present).to_bytes(8,'little'))
        m.reg_write(regs.UC_X86_REG_RCX,0x1000000);m.reg_write(regs.UC_X86_REG_RDI,0x2000000)
        m.reg_write(regs.UC_X86_REG_RSP,0x3000800)
        reached=[]
        def visit(uc,pc,size,_):
            if pc in callsites:reached.append(callsites[pc]);uc.emu_stop()
        m.hook_add(UC_HOOK_CODE,visit);m.emu_start(0x1813c20e3,0x1813c2174,count=100)
        if directional:
            expected=0x1813f02d0 if not precomputed else 0x1813c05c0 if not mode else 0x1813b5460 if extra_a and extra_b else 0x1813b1070
        else:expected=0x1813b9780 if mode else 0x1813c1800
        require(reached==[expected],'Output-mode selection differs')
        selection.append(dict(mode=mode,directional=directional,precomputed=precomputed,extra_a=extra_a,extra_b=extra_b,target=f'{expected:x}'))
    root=folder/'fma4-roles';root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    for name,text in artifacts.items():(root/name).write_text(text,encoding='utf-8')
    report=dict(status='caller-and-task-string-evidence',source_sha256=GAME_SHA,roles=ROLES,callers=callers,selector_cases=selection,
        interpretation='Task roles derive from actual direct CALL edges and RIP-relative task-name strings. Output-mode conditions are interpreted from caller instructions and retained decompiler C. Names are descriptive aliases, not recovered exports.',
        limitation='No whole-function equivalence or exact lighting algorithm identity. FMA4 arithmetic, data-structure field names and caller runtime inputs remain incompletely verified.')
    (root/'report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print('Verified 4 direct caller edges and task-name string references for all 3 FMA4 procedures')


if __name__=='__main__':main()
