"""联合核验五个 64 位 ChaCha20 过程及跨过程分支，不忽略 CPU 能力变量引用。"""
import argparse
import hashlib
import json
import os
import shutil
import struct
import subprocess
from pathlib import Path
from capstone import Cs,CS_ARCH_X86,CS_MODE_64,CS_GRP_JUMP,CS_GRP_CALL
from capstone.x86 import X86_OP_REG,X86_OP_IMM,X86_OP_MEM,X86_REG_RIP
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import coff,pe_read,require
from dsp_native_openssl_match import GAME_SHA,COMMIT,INPUTS

FUNCTIONS=[('ChaCha20_ctr32',0x18003d2c0,0x18003d671),
           ('ChaCha20_ssse3',0x18003d680,0x18003d8e8),
           ('ChaCha20_4x',0x18003d900,0x18003e39b),
           ('ChaCha20_4xop',0x18003e3a0,0x18003eb76),
           ('ChaCha20_8x',0x18003eb80,0x18003f775)]
CONSTANT=0x18003d1c0
CPU_BASE=0x181273990

def normalized(ins,const,external,const_size=192,external_addend=4):
    meaningful=[i for i in ins if i.mnemonic!='nop'];starts={i.address:n for n,i in enumerate(meaningful)}
    for i in reversed(ins):
        if i.mnemonic=='nop':starts[i.address]=starts.get(i.address+i.size,-1)
    result=[];external_refs=[]
    for i in meaningful:
        operands=[]
        for op in i.operands:
            if op.type==X86_OP_MEM and op.mem.base==X86_REG_RIP:
                target=i.address+i.size+op.mem.disp
                if target==external:
                    value=('external','OPENSSL_ia32cap_P',external_addend,op.size);external_refs.append(i.address)
                else:
                    require(0<=target-const and target-const+op.size<=const_size,'RIP reference outside verified data')
                    value=('constant',target-const,op.size)
            elif op.type==X86_OP_REG:value=('reg',i.reg_name(op.reg),op.size)
            elif op.type==X86_OP_IMM:
                if i.group(CS_GRP_JUMP) or i.group(CS_GRP_CALL):
                    require(op.imm in starts and starts[op.imm]>=0,'Branch escapes jointly verified procedures')
                    value=('code',starts[op.imm])
                else:value=('imm',op.imm,op.size)
            elif op.type==X86_OP_MEM:
                m=op.mem;value=('mem',i.reg_name(m.segment),i.reg_name(m.base),i.reg_name(m.index),m.scale,m.disp,op.size)
            else:raise ValueError('Unexpected operand kind')
            operands.append(value)
        result.append((i.mnemonic,operands))
    require(len(external_refs)==1,'Unexpected CPU reference count')
    return result,external_refs[0]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('reference','perl','nasm'):parser.add_argument('--'+name,type=Path,required=True)
    args=parser.parse_args();module='DSPGAME_Data/Plugins/x86_64/rail_api64.dll'
    inv=read_json(GENERATED/'native/inventory.json');game_path=Path(inv['game_directory'])/module
    require(digest(game_path)==GAME_SHA,'Game baseline changed');game=game_path.read_bytes()
    root=GENERATED/'native'/module.replace('/','__')/'upstream-openssl/chacha64-family'
    root.mkdir(parents=True,exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    for name in ('chacha-x86_64.pl','x86_64-xlate.pl'):
        require(digest(args.reference/name)==INPUTS[name],'Upstream source changed');shutil.copyfile(args.reference/name,root/name)
    shutil.copyfile(args.reference/'LICENSE',root/'LICENSE')
    env=os.environ.copy();env['PATH']=str(args.nasm.resolve().parent)+os.pathsep+env.get('PATH','')
    subprocess.run([str(args.perl.resolve()),'chacha-x86_64.pl','nasm','chacha.asm'],cwd=root,env=env,check=True,capture_output=True)
    subprocess.run([str(args.nasm.resolve()),'-f','win64','chacha.asm','-o','chacha.obj'],cwd=root,check=True,capture_output=True)
    obj=(root/'chacha.obj').read_bytes();sections,symbols=coff(obj);names={n:(v,s) for n,v,s in symbols.values()}
    name,code,offset,count=sections[0]
    require(name=='.text' and len(code)==9821 and count==1,'Unexpected source object layout')
    reloc,symbol,kind=struct.unpack_from('<IIH',obj,offset)
    require(reloc==0x12b and kind==4 and symbols[symbol]==('OPENSSL_ia32cap_P',0,0),'Unexpected REL32 symbol')
    require(struct.unpack_from('<i',code,reloc)[0]==4,'Unexpected CPU capability addend')
    require(bytes(code[:192])==pe_read(game,CONSTANT,192),'Constants differ')
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True
    source=[];target=[];routines=[]
    for name,start,end in FUNCTIONS:
        a,section=names[name];b,endsection=names['L$SEH_end_'+name]
        require(section==endsection==1,'Procedure not in text')
        raw=pe_read(game,start,end-start);original=list(decoder.disasm(raw,start));rebuilt=list(decoder.disasm(bytes(code[a:b]),a))
        require(sum(i.size for i in original)==len(raw) and sum(i.size for i in rebuilt)==b-a,'Incomplete decoding')
        require(sum(i.mnemonic!='nop' for i in original)==sum(i.mnemonic!='nop' for i in rebuilt),'Per-procedure instruction count differs')
        source.extend(rebuilt);target.extend(original)
        routines.append(dict(name=name,address=f'{start:x}',body_entry=f'{start+13:x}',end_exclusive=f'{end:x}',
            source_start=a,source_end=b,game_bytes=len(raw),rebuilt_bytes=b-a,meaningful_instructions=sum(i.mnemonic!='nop' for i in original),
            game_nops=sum(i.mnemonic=='nop' for i in original),rebuilt_nops=sum(i.mnemonic=='nop' for i in rebuilt),code_sha256=hashlib.sha256(raw).hexdigest()))
    a,source_ref=normalized(source,0,reloc+4+4);b,game_ref=normalized(target,CONSTANT,CPU_BASE+4)
    require(a==b and len(a)==1840,'Joint instruction comparison differs')
    require(source_ref==0x128 and game_ref==0x18003d2e8,'Unexpected external reference instruction')
    cross=[]
    for i in target:
        if i.group(CS_GRP_JUMP) or i.group(CS_GRP_CALL):
            require(i.operands[0].type==X86_OP_IMM,'Unexpected indirect branch')
            origin=next(n for n,s,e in FUNCTIONS if s<=i.address<e)
            dest=next(n for n,s,e in FUNCTIONS if s<=i.operands[0].imm<e)
            if origin!=dest:cross.append(dict(address=f'{i.address:x}',target=f'{i.operands[0].imm:x}',source=origin,destination=dest))
    # Redirect a cross-procedure branch to another legal instruction; comparison must reject it.
    first=cross[0];original=next(i for i in target if i.address==int(first['address'],16))
    mutated=bytearray(original.bytes);position=original.imm_offset
    require(original.imm_size==4,'Expected rel32 branch')
    struct.pack_into('<i',mutated,position,FUNCTIONS[1][1]-original.address-original.size)
    replacement=list(decoder.disasm(bytes(mutated),original.address));require(len(replacement)==1,'Bad negative decode')
    negative=[replacement[0] if i.address==original.address else i for i in target]
    changed,_=normalized(negative,CONSTANT,CPU_BASE+4);require(changed!=a,'Redirected branch accepted')
    def listing(ins):return ''.join(f'{i.address:x}  {i.bytes.hex()}  {i.mnemonic} {i.op_str}\n' for i in ins)
    (root/'game.asm').write_text(listing(target),encoding='utf-8');(root/'rebuilt.asm').write_text(listing(source),encoding='utf-8')
    report=dict(status='normalized-source-matched',source_sha256=GAME_SHA,module=module,name='ChaCha20_ctr32',
        address=routines[0]['address'],body_entry=routines[0]['body_entry'],assembly_file='chacha.asm',routines=routines,
        supersedes=['chacha4-xop'],meaningful_instructions=len(a),constant_bytes=192,
        constant_sha256=hashlib.sha256(bytes(code[:192])).hexdigest(),upstream_commit=COMMIT,
        upstream_url=f'https://github.com/openssl/openssl/blob/{COMMIT}/crypto/chacha/asm/chacha-x86_64.pl',
        external_reference=dict(symbol='OPENSSL_ia32cap_P',addend=4,bytes=8,game_base=f'{CPU_BASE:x}',object_relocation_offset=reloc),
        cross_procedure_branches=cross,redirected_branch_negative_rejected=True,
        nasm_sha256=digest(args.nasm),perl_sha256=digest(args.perl),
        limitation='Five procedures jointly match after removing decoded NOPs and normalizing verified branch/constant/external-symbol targets. No new C, CPU feature-state proof, whole-library identity, or exception-unwind equivalence claim.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print(f'Verified 5 procedures, {len(a)} non-NOP instructions, {len(cross)} cross-procedure branches and one CPU-symbol reference')

if __name__=='__main__':main()
