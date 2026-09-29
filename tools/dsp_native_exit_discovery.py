"""从既有 CRT 初始化函数的 atexit 参数中发现遗漏入口，并验证原始指令。"""
import json
import re
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64,CS_GRP_CALL,CS_GRP_JUMP
    from capstone.x86 import X86_OP_IMM,X86_OP_MEM,X86_OP_REG,X86_REG_RIP,X86_REG_RCX
    folder=GENERATED/'native/UnityPlayer.dll';game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game changed');game=game_path.read_bytes()
    fs=read_json(folder/'functions.json')['functions'];known={f['address'] for f in fs}
    known|={f['address'] for f in read_json(folder/'crt-callbacks/report.json')['functions'] if f['status']=='decompiled'}
    callbacks={f'{v[0]:x}' for v in struct.iter_unpack('<Q',pe_read(game,0x1818910b0,0x5e58))}
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True;records=[]
    for f in fs:
        if f['address'] not in callbacks or 'file' not in f:continue
        text=(folder/f['file']).read_text();candidates=set(re.findall(r'atexit\([^;]*?(?:FUN_|LAB_)([0-9a-f]+)',text))-known
        if not candidates:continue
        start=int(f['address'],16);instructions=list(decoder.disasm(pe_read(game,start,f['size']),start))
        for target in sorted(candidates):
            found=[]
            for n,i in enumerate(instructions):
                if not(i.group(CS_GRP_CALL) or i.mnemonic=='jmp') or i.operands[0].type!=X86_OP_IMM or i.operands[0].imm!=0x181813ca4:continue
                for previous in reversed(instructions[:n]):
                    if previous.group(CS_GRP_CALL) or previous.group(CS_GRP_JUMP):break
                    _,writes=previous.regs_access()
                    if any(previous.reg_name(reg) in ('rcx','ecx','cx','cl','ch') for reg in writes):
                        ops=previous.operands
                        if previous.mnemonic=='lea' and len(ops)==2 and ops[0].type==X86_OP_REG and ops[0].reg==X86_REG_RCX and ops[1].type==X86_OP_MEM and ops[1].mem.base==X86_REG_RIP:
                            value=previous.address+previous.size+ops[1].mem.disp
                            if value==int(target,16):found.append(dict(target=target,source=f['address'],setup=f'{previous.address:x}',setup_bytes=previous.bytes.hex(),call=f'{i.address:x}',call_bytes=i.bytes.hex()))
                        break
            require(found,'Missing machine-code registration evidence for '+target)
            records.extend(found)
    missing=sorted({r['target'] for r in records});root=folder/'exit-discovery';root.mkdir(exist_ok=True)
    report=dict(source_sha256=GAME_SHA,registrations=records,missing_entries=missing,
        scope='C references nominate candidates; original LEA RCX and direct atexit CALL/JMP bytes confirm registration paths. Backward slice stops at calls/branches/RCX writes; not a full all-path registration proof.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(root/'missing-entries.txt').write_text(''.join(a+'\n' for a in missing),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={n:digest(root/n) for n in ('report.json','missing-entries.txt')}),indent=2),encoding='utf-8')
    print(f'{len(missing)} missing exit entries verified from {len(records)} registration sites')


if __name__=='__main__':main()
