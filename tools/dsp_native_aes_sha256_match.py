"""联合核验 AES/SHA-256 调度与四种实现；保留上游 UD2，不宣称异常路径等价。"""
import argparse
import hashlib
import json
import os
import shutil
import struct
import subprocess
from capstone import Cs,CS_ARCH_X86,CS_MODE_64,CS_GRP_JUMP,CS_GRP_CALL
from capstone.x86 import X86_OP_IMM
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import coff,pe_read,require
from dsp_native_openssl_match import GAME_SHA,COMMIT,INPUTS
from dsp_native_chacha64_match import normalized

FUNCTIONS=[('aesni_cbc_sha256_enc',0x18002df40,0x18002dfa7),
           ('aesni_cbc_sha256_enc_xop',0x18002e280,0x18002f35f),
           ('aesni_cbc_sha256_enc_avx',0x18002f380,0x18003059f),
           ('aesni_cbc_sha256_enc_avx2',0x1800305c0,0x1800320f0),
           ('aesni_cbc_sha256_enc_shaext',0x180032100,0x1800327bc)]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('reference','perl','nasm'):parser.add_argument('--'+name,type=Path,required=True)
    args=parser.parse_args();module='DSPGAME_Data/Plugins/x86_64/rail_api64.dll'
    inv=read_json(GENERATED/'native/inventory.json');game_path=Path(inv['game_directory'])/module
    require(digest(game_path)==GAME_SHA,'Source baseline changed');game=game_path.read_bytes()
    root=GENERATED/'native'/module.replace('/','__')/'upstream-openssl/aesni-sha256-family'
    root.mkdir(parents=True,exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    for name in ('aesni-sha256-x86_64.pl','x86_64-xlate.pl'):
        require(digest(args.reference/name)==INPUTS[name],'Upstream changed');shutil.copyfile(args.reference/name,root/name)
    shutil.copyfile(args.reference/'LICENSE',root/'LICENSE')
    env=os.environ.copy();env['PATH']=str(args.nasm.resolve().parent)+os.pathsep+env.get('PATH','')
    subprocess.run([str(args.perl.resolve()),'aesni-sha256-x86_64.pl','nasm','aesni-sha256.asm'],cwd=root,env=env,check=True,capture_output=True)
    subprocess.run([str(args.nasm.resolve()),'-f','win64','aesni-sha256.asm','-o','aesni-sha256.obj'],cwd=root,check=True,capture_output=True)
    obj=(root/'aesni-sha256.obj').read_bytes();sections,symbols=coff(obj);names={n:(v,s) for n,v,s in symbols.values()}
    name,code,offset,count=sections[0];require(name=='.text' and len(code)==18967 and count==2,'Object layout changed')
    relocations=[struct.unpack_from('<IIH',obj,offset+n*10) for n in range(count)]
    require([(r,symbols[s][0],t) for r,s,t in relocations]==[(0x3,'OPENSSL_ia32cap_P',4),(0x49fb,'__imp_RtlVirtualUnwind',4)],'Relocations changed')
    require(struct.unpack_from('<i',code,0x3)[0]==0 and names['K256']==(0x80,1),'CPU addend or constants changed')
    constants=bytes(code[0x80:0x340]);require(constants==pe_read(game,0x18002dfc0,0x2c0),'Constant block mismatch')
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True
    source=[];target=[];routines=[]
    for name,start,end in FUNCTIONS:
        a,section=names[name];b,endsection=(0x6f,1) if name=='aesni_cbc_sha256_enc' else names['L$SEH_end_'+name];require(section==endsection==1,'Wrong code section')
        raw=pe_read(game,start,end-start);old=list(decoder.disasm(raw,start));new=list(decoder.disasm(bytes(code[a:b]),a))
        require(sum(i.size for i in old)==len(raw) and sum(i.size for i in new)==b-a,'Incomplete decode')
        meaningful=sum(i.mnemonic!='nop' for i in old);require(meaningful==sum(i.mnemonic!='nop' for i in new),'Per-procedure instruction mismatch')
        source.extend(new);target.extend(old)
        routines.append(dict(name=name,address=f'{start:x}',body_entry=f'{start if name==FUNCTIONS[0][0] else start+13:x}',end_exclusive=f'{end:x}',
            game_bytes=len(raw),rebuilt_bytes=b-a,meaningful_instructions=meaningful,
            game_nops=len(old)-meaningful,rebuilt_nops=len(new)-meaningful,
            code_sha256=hashlib.sha256(raw).hexdigest()))
    a,source_ref=normalized(source,0x80,7,0x2c0,0)
    b,game_ref=normalized(target,0x18002dfc0,0x181273990,0x2c0,0)
    require(a==b and len(a)==4314 and source_ref==0 and game_ref==0x18002df40,'Joint instruction mismatch')
    cross=[]
    for i in target:
        if i.group(CS_GRP_JUMP) or i.group(CS_GRP_CALL):
            require(i.operands[0].type==X86_OP_IMM,'Unexpected indirect branch')
            origin=next(n for n,s,e in FUNCTIONS if s<=i.address<e)
            dest=next(n for n,s,e in FUNCTIONS if s<=i.operands[0].imm<e)
            if origin!=dest:cross.append(dict(address=f'{i.address:x}',target=f'{i.operands[0].imm:x}',source=origin,destination=dest))
    require(len(cross)==4,'Unexpected dispatch edge count')
    branch=next(i for i in target if i.address==int(cross[0]['address'],16));raw=bytearray(branch.bytes)
    require(branch.imm_size==4,'Expected rel32 branch');struct.pack_into('<i',raw,branch.imm_offset,FUNCTIONS[1][1]-branch.address-branch.size)
    replacement=list(decoder.disasm(bytes(raw),branch.address));require(len(replacement)==1,'Negative decode failed')
    mutated=[replacement[0] if i.address==branch.address else i for i in target]
    negative,_=normalized(mutated,0x18002dfc0,0x181273990,0x2c0,0);require(negative!=a,'Incorrect shortcut accepted')
    for name,ins in [('game.asm',target),('rebuilt.asm',source)]:
        (root/name).write_text(''.join(f'{i.address:x}  {i.bytes.hex()}  {i.mnemonic} {i.op_str}\n' for i in ins),encoding='utf-8')
    traps=[f'{i.address:x}' for i in target if i.mnemonic=='ud2']
    require(traps==['18002df99','18002dfa3'],'Unexpected intentional trap set')
    report=dict(intentional_ud2=traps,status='normalized-source-matched',source_sha256=GAME_SHA,module=module,name=FUNCTIONS[0][0],
        address=routines[0]['address'],body_entry=routines[0]['body_entry'],assembly_file='aesni-sha256.asm',routines=routines,
        supersedes=['aesni-sha256-xop'],meaningful_instructions=len(a),constant_bytes=len(constants),
        constant_sha256=hashlib.sha256(constants).hexdigest(),upstream_commit=COMMIT,
        upstream_url=f'https://github.com/openssl/openssl/blob/{COMMIT}/crypto/aes/asm/aesni-sha256-x86_64.pl',
        external_reference=dict(symbol='OPENSSL_ia32cap_P',addend=0,game_address='181273990',object_relocation_offset=0x3),
        cross_procedure_branches=cross,redirected_branch_negative_rejected=True,
        excluded='Object exception handler after 0x489c including RtlVirtualUnwind relocation at 0x49fb; .pdata/.xdata; inter-procedure alignment.',
        limitation='Four implementations plus CPU dispatch jointly matched with NOP/address normalization. No exception-unwind, runtime CPU feature-state, whole-library identity or C-equivalence proof.',
        nasm_sha256=digest(args.nasm),perl_sha256=digest(args.perl))
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print('Verified 5 procedures, 4314 non-NOP instructions, 704 static bytes and 4 CPU dispatch branches')

if __name__=='__main__':main()
