"""核验 -1 局部值后续的阈值与比值比较，不推定完整光照业务语义。"""
import json
import math
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

PROFILES=[('1813b1070',0x1813b1674,0x1813b16e4,0x1813b1894,'XMM9'),
          ('1813b5460',0x1813b5a24,0x1813b5a97,0x1813b5ca2,'XMM10'),
          ('1813b9780',0x1813b9c94,0x1813b9d08,0x1813b9dc1,'XMM9')]


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64,CS_GRP_JUMP
    from capstone.x86 import X86_OP_IMM,X86_OP_MEM
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE
    from unicorn import x86_const as r
    folder=GENERATED/'native/UnityPlayer.dll';game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game changed');game=game_path.read_bytes()
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True
    values=[0xff800000,0xbf800000,0x80000000,0,1,0x34000000,0x3f000000,0x3f800000,0x7f7fffff,0x7f800000,0x7fc00001]
    def number(bits):return struct.unpack('<f',struct.pack('<I',bits))[0]
    cases=[];functions=[];artifacts={}
    for name,threshold_start,ratio_start,reset,xmm in PROFILES:
        reset_ins=list(decoder.disasm(pe_read(game,reset,32),reset))
        stores=[i for i in reset_ins if i.mnemonic=='mov' and len(i.operands)==2 and i.operands[0].type==X86_OP_MEM
                and i.operands[0].size==4 and i.operands[1].type==X86_OP_IMM and i.operands[1].imm&0xffffffff==0xbf800000]
        require(len(stores)==1,'Reset block has no unique -1 store')
        branch_records=[]
        for kind,start in [('threshold',threshold_start),('ratio',ratio_start)]:
            ins=[]
            for i in decoder.disasm(pe_read(game,start,40),start):
                ins.append(i)
                if i.group(CS_GRP_JUMP):break
            branch=ins[-1];fall=branch.address+branch.size
            require(ins[0].mnemonic=='comiss' and branch.mnemonic==('jbe' if kind=='threshold' else 'jae') and branch.operands[0].imm==reset,'Threshold branch shape changed')
            code=pe_read(game,start,fall-start);artifacts[f'{name}-{kind}.asm']=''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in ins)
            m=Uc(UC_ARCH_X86,UC_MODE_64)
            for page in sorted({start&~4095,reset&~4095,fall&~4095,0x3000000}):m.mem_map(page,4096)
            m.mem_write(start,code);m.reg_write(r.UC_X86_REG_RBP,0x3000800);m.reg_write(r.UC_X86_REG_RSP,0x3000600)
            stopped=[]
            def visit(uc,pc,size,_):
                if pc in (reset,fall):stopped.append(pc);uc.emu_stop()
            m.hook_add(UC_HOOK_CODE,visit)
            for threshold in values:
                for ratio in (values if kind=='ratio' else [0]):
                    stopped.clear();m.reg_write(getattr(r,'UC_X86_REG_'+xmm),threshold);m.reg_write(r.UC_X86_REG_XMM3,0)
                    m.reg_write(r.UC_X86_REG_XMM1,ratio);m.reg_write(r.UC_X86_REG_MXCSR,0x1f80)
                    m.emu_start(start,0,count=20)
                    t=number(threshold);v=number(ratio)
                    taken=(math.isnan(t) or t<=0) if kind=='threshold' else (not math.isnan(v) and not math.isnan(t) and v>=t)
                    require(stopped==[reset if taken else fall],'Threshold decision differs')
                    cases.append(dict(function=name,kind=kind,threshold=f'{threshold:08x}',ratio=f'{ratio:08x}',reset=taken))
            branch_records.append(dict(kind=kind,start=f'{start:x}',branch=f'{branch.address:x}',fallthrough=f'{fall:x}'))
        functions.append(dict(address=name,branches=branch_records,reset=f'{reset:x}',reset_store=f'{stores[0].address:x}',store_operands=stores[0].op_str))
        artifacts[name+'-reset.asm']=''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in reset_ins)
    root=folder/'irradiance-threshold';root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    for name,text in artifacts.items():(root/name).write_text(text,encoding='utf-8')
    report=dict(status='sampled-threshold-consumption-verified',source_sha256=GAME_SHA,functions=functions,case_count=len(cases),cases=cases,
        limitation='Comparison fragments with supplied XMM values and MXCSR=0x1F80; ratio computation, intervening dataflow, full reuse/recompute behavior, other floating-point environments and exceptions are not proven.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print(f'{len(cases)} threshold/ratio branch cases passed; three common reset stores identified')


if __name__=='__main__':main()
