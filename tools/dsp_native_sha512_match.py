"""联合重建并核验 SHA-512 标量、XOP、AVX、AVX2 四过程；异常处理器单独排除。"""
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

FUNCTIONS=[('sha512_block_data_order',0x18001b740,0x18001c9a2),
           ('sha512_block_data_order_xop',0x18001cf40,0x18001df3d),
           ('sha512_block_data_order_avx',0x18001df40,0x18001f14d),
           ('sha512_block_data_order_avx2',0x18001f180,0x1800208f0)]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('reference','perl','nasm'):parser.add_argument('--'+name,type=Path,required=True)
    args=parser.parse_args();module='DSPGAME_Data/Plugins/x86_64/rail_api64.dll'
    inv=read_json(GENERATED/'native/inventory.json');game_path=Path(inv['game_directory'])/module
    require(digest(game_path)==GAME_SHA,'Source baseline changed');game=game_path.read_bytes()
    root=GENERATED/'native'/module.replace('/','__')/'upstream-openssl/sha512-family'
    root.mkdir(parents=True,exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    for name in ('sha512-x86_64.pl','x86_64-xlate.pl'):
        require(digest(args.reference/name)==INPUTS[name],'Upstream changed');shutil.copyfile(args.reference/name,root/name)
    shutil.copyfile(args.reference/'LICENSE',root/'LICENSE')
    env=os.environ.copy();env['PATH']=str(args.nasm.resolve().parent)+os.pathsep+env.get('PATH','')
    subprocess.run([str(args.perl.resolve()),'sha512-x86_64.pl','nasm','sha512.asm'],cwd=root,env=env,check=True,capture_output=True)
    subprocess.run([str(args.nasm.resolve()),'-f','win64','sha512.asm','-o','sha512.obj'],cwd=root,check=True,capture_output=True)
    obj=(root/'sha512.obj').read_bytes();sections,symbols=coff(obj);names={n:(v,s) for n,v,s in symbols.values()}
    name,code,offset,count=sections[0];require(name=='.text' and len(code)==21339 and count==2,'Object layout changed')
    relocations=[struct.unpack_from('<IIH',obj,offset+n*10) for n in range(count)]
    require([(r,symbols[s][0],t) for r,s,t in relocations]==[(0x19,'OPENSSL_ia32cap_P',4),(0x533f,'__imp_RtlVirtualUnwind',4)],'Relocations changed')
    require(struct.unpack_from('<i',code,0x19)[0]==0 and names['K512']==(0x1280,1),'CPU addend or constants changed')
    constants=bytes(code[0x1280:0x17a0]);require(constants==pe_read(game,0x18001c9c0,0x520),'Constant block mismatch')
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True
    source=[];target=[];routines=[]
    for name,start,end in FUNCTIONS:
        a,section=names[name];b,endsection=names['L$SEH_end_'+name];require(section==endsection==1,'Wrong code section')
        raw=pe_read(game,start,end-start);old=list(decoder.disasm(raw,start));new=list(decoder.disasm(bytes(code[a:b]),a))
        require(sum(i.size for i in old)==len(raw) and sum(i.size for i in new)==b-a,'Incomplete decode')
        meaningful=sum(i.mnemonic!='nop' for i in old);require(meaningful==sum(i.mnemonic!='nop' for i in new),'Per-procedure instruction mismatch')
        source.extend(new);target.extend(old)
        routines.append(dict(name=name,address=f'{start:x}',body_entry=f'{start+13:x}',end_exclusive=f'{end:x}',
            game_bytes=len(raw),rebuilt_bytes=b-a,meaningful_instructions=meaningful,
            game_nops=len(old)-meaningful,rebuilt_nops=len(new)-meaningful,
            code_sha256=hashlib.sha256(raw).hexdigest()))
    a,source_ref=normalized(source,0x1280,0x1d,0x520,0)
    b,game_ref=normalized(target,0x18001c9c0,0x181273990,0x520,0)
    require(a==b and len(a)==4910 and source_ref==0x16 and game_ref==0x18001b756,'Joint instruction mismatch')
    cross=[]
    for i in target:
        if i.group(CS_GRP_JUMP) or i.group(CS_GRP_CALL):
            require(i.operands[0].type==X86_OP_IMM,'Unexpected indirect branch')
            origin=next(n for n,s,e in FUNCTIONS if s<=i.address<e)
            dest=next(n for n,s,e in FUNCTIONS if s<=i.operands[0].imm<e)
            if origin!=dest:cross.append(dict(address=f'{i.address:x}',target=f'{i.operands[0].imm:x}',source=origin,destination=dest))
    require(len(cross)==3,'Unexpected dispatch edge count')
    branch=next(i for i in target if i.address==int(cross[0]['address'],16));raw=bytearray(branch.bytes)
    require(branch.imm_size==4,'Expected rel32 branch');struct.pack_into('<i',raw,branch.imm_offset,FUNCTIONS[1][1]-branch.address-branch.size)
    replacement=list(decoder.disasm(bytes(raw),branch.address));require(len(replacement)==1,'Negative decode failed')
    mutated=[replacement[0] if i.address==branch.address else i for i in target]
    negative,_=normalized(mutated,0x18001c9c0,0x181273990,0x520,0);require(negative!=a,'Incorrect shortcut accepted')
    for name,ins in [('game.asm',target),('rebuilt.asm',source)]:
        (root/name).write_text(''.join(f'{i.address:x}  {i.bytes.hex()}  {i.mnemonic} {i.op_str}\n' for i in ins),encoding='utf-8')
    report=dict(status='normalized-source-matched',source_sha256=GAME_SHA,module=module,name=FUNCTIONS[0][0],
        address=routines[0]['address'],body_entry=routines[0]['body_entry'],assembly_file='sha512.asm',routines=routines,
        supersedes=['sha512-xop'],meaningful_instructions=len(a),constant_bytes=len(constants),
        constant_sha256=hashlib.sha256(constants).hexdigest(),upstream_commit=COMMIT,
        upstream_url=f'https://github.com/openssl/openssl/blob/{COMMIT}/crypto/sha/asm/sha512-x86_64.pl',
        external_reference=dict(symbol='OPENSSL_ia32cap_P',addend=0,game_address='181273990',object_relocation_offset=0x19),
        cross_procedure_branches=cross,redirected_branch_negative_rejected=True,
        excluded='Object exception handler after 0x5200 including RtlVirtualUnwind relocation at 0x533f; .pdata/.xdata; inter-procedure alignment.',
        limitation='Four computational procedures and CPU dispatch jointly matched with NOP/address normalization. No exception-unwind, runtime CPU feature-state, whole-library identity or C-equivalence proof.',
        nasm_sha256=digest(args.nasm),perl_sha256=digest(args.perl))
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print('Verified 4 procedures, 4910 non-NOP instructions, 1312 static bytes and 3 CPU dispatch branches')

if __name__=='__main__':main()
