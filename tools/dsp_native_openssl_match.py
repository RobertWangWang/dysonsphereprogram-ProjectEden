"""重建 OpenSSL 1.1.0g XOP 过程，与游戏逐指令规范化比较；不执行游戏。"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
from pathlib import Path
from capstone import *
from capstone.x86 import *
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import coff, pe_read

GAME_SHA = 'e5a7f21572d4face6590d03bf12e94916a3882deac037d5c68e05c95e3bc8f42'
COMMIT = 'b2758a2292aceda93e9f44c219b94fe21bb9a650'
INPUTS = {'sha512-x86_64.pl':'fa5d6b50137c11a436a650fdb97a8f0cc224d6f4596416b4cf3871f7b0365737',
          'x86_64-xlate.pl':'e76a63c4ad52a55aef1e2043a12d76c8bf7122016ab26f1f652ccd99c23ed5a0'}
PROFILES = {
 'sha512-xop': dict(generator='sha512-x86_64.pl', stem='sha512', symbol='sha512_block_data_order_xop',
                    constant_symbol='K512', constant_bias=0, constant_size=0x520, game_constant=0x18001c9c0,
                    entry=0x18001cf40, end=0x18001df3d, count=1086, upstream='crypto/sha/asm/sha512-x86_64.pl'),
 'chacha4-xop': dict(generator='chacha-x86_64.pl', stem='chacha', symbol='ChaCha20_4xop',
                    constant_symbol='L$sigma', constant_bias=-0xa0, constant_size=0xc0, game_constant=0x18003d1c0,
                    entry=0x18003e3a0, end=0x18003eb76, count=388, upstream='crypto/chacha/asm/chacha-x86_64.pl'),
 'aesni-sha256-xop': dict(generator='aesni-sha256-x86_64.pl', stem='aesni-sha256', symbol='aesni_cbc_sha256_enc_xop',
                    constant_symbol='K256', constant_bias=0, constant_size=0x2c0, game_constant=0x18002dfc0,
                    entry=0x18002e280, end=0x18002f35f, count=1152, upstream='crypto/aes/asm/aesni-sha256-x86_64.pl'),
}
INPUTS['chacha-x86_64.pl']='748319e12105b4686da3b83208ff26add86cf5eecfb11d71199ba3707c92b2e4'
INPUTS['aesni-sha256-x86_64.pl']='2c227ce42a68f2b3ae6ff798df2f81b80723028eba2d22d14ec353a68988f9da'
def require(ok, message):
    if not ok:
        raise ValueError(message)

def norm(ins,const,const_size=0x520):
 meaningful=[i for i in ins if i.mnemonic!='nop']; starts={i.address:n for n,i in enumerate(meaningful)}
 for i in reversed(ins):
  if i.mnemonic=='nop': starts[i.address]=starts.get(i.address+i.size,-1)
 out=[]
 for i in meaningful:
  ops=[]
  for op in i.operands:
   if op.type==X86_OP_REG: val=('reg',i.reg_name(op.reg),op.size)
   elif op.type==X86_OP_IMM:
    if i.group(CS_GRP_JUMP) or i.group(CS_GRP_CALL): val=('code',starts[op.imm])
    else:val=('imm',op.imm,op.size)
   elif op.type==X86_OP_MEM:
    m=op.mem
    if m.base==X86_REG_RIP:
     a=i.address+i.size+m.disp-const
     require(0<=a and a+op.size<=const_size, 'RIP target outside verified constants');val=('constant',a,op.size)
    else:val=('mem',i.reg_name(m.segment),i.reg_name(m.base),i.reg_name(m.index),m.scale,m.disp,op.size)
   else:raise ValueError(op.type)
   ops.append(val)
  out.append((i.mnemonic,ops))
 return meaningful,out

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('reference','perl','nasm'):
        parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--routine',choices=PROFILES,default='sha512-xop')
    args=parser.parse_args()
    cfg=PROFILES[args.routine]
    generator=cfg['generator'];stem=cfg['stem'];const_size=cfg['constant_size']
    entry=cfg['entry'];finish=cfg['end'];game_constant=cfg['game_constant']
    inputs={k:INPUTS[k] for k in (generator,'x86_64-xlate.pl')}
    base=GENERATED/'native'
    inventory=read_json(base/'inventory.json')
    module='DSPGAME_Data/Plugins/x86_64/rail_api64.dll'
    game_path=Path(inventory['game_directory'])/module
    require(digest(game_path)==GAME_SHA,'Game baseline changed')
    root=base/module.replace('/','__')/'upstream-openssl'/args.routine
    root.mkdir(parents=True,exist_ok=True)
    (root/'manifest.json').unlink(missing_ok=True)
    for name,sha in inputs.items():
        require(digest(args.reference/name)==sha,'Upstream input mismatch')
    for name in [*inputs,'LICENSE']:
        shutil.copyfile(args.reference/name,root/name)
    env=os.environ.copy();env['PATH']=str(args.nasm.resolve().parent)+os.pathsep+env.get('PATH','')
    subprocess.run([str(args.perl.resolve()),generator,'nasm',stem+'.asm'],cwd=root,env=env,check=True,capture_output=True)
    subprocess.run([str(args.nasm.resolve()),'-f','win64',stem+'.asm','-o',stem+'.obj'],cwd=root,check=True,capture_output=True)
    sections,symbols=coff((root/(stem+'.obj')).read_bytes())
    symbol={name:(value,section) for name,value,section in symbols.values()}
    start,section=symbol[cfg['symbol']];end,endsection=symbol['L$SEH_end_'+cfg['symbol']];constant,csection=symbol[cfg['constant_symbol']];constant+=cfg['constant_bias']
    require(section==endsection==csection and sections[section-1][0]=='.text','Unexpected object layout')
    code=bytes(sections[section-1][1]);game=game_path.read_bytes()
    actual=pe_read(game,entry,finish-entry)
    require(code[constant:constant+const_size]==pe_read(game,game_constant,const_size),'Constants differ')
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True
    original=list(decoder.disasm(actual,entry));rebuilt=list(decoder.disasm(code[start:end],start))
    require(sum(i.size for i in original)==len(actual) and sum(i.size for i in rebuilt)==end-start,'Incomplete decoding')
    a,na=norm(original,game_constant,const_size);b,nb=norm(rebuilt,constant,const_size)
    require(na==nb and len(na)==cfg['count'],'Normalized instructions differ')
    # Both branch destinations and RIP-relative constants are compared, not erased.
    code_mutation=bytearray(actual);code_mutation[0]=0x49
    mutated=list(decoder.disasm(bytes(code_mutation),entry))
    try:
        _,negative=norm(mutated,game_constant,const_size)
        require(negative!=nb,'Negative instruction mutation accepted')
    except (KeyError,ValueError) as error:
        if str(error)=='Negative instruction mutation accepted':raise
    for filename,ins in [('game.asm',original),('rebuilt.asm',rebuilt)]:
        (root/filename).write_text(''.join(f'{i.address:x}  {i.bytes.hex()}  {i.mnemonic} {i.op_str}\n' for i in ins),encoding='utf-8')
    report=dict(status='normalized-source-matched',source_sha256=GAME_SHA,module=module,
                name=cfg['symbol'],address=f'{entry:x}',body_entry=f'{entry+13:x}',end_exclusive=f'{finish:x}',assembly_file=stem+'.asm',
                upstream_commit=COMMIT,upstream_url=f'https://github.com/openssl/openssl/blob/{COMMIT}/'+cfg['upstream'],
                game_bytes=len(actual),rebuilt_bytes=end-start,meaningful_instructions=len(na),
                game_nops=len(original)-len(a),rebuilt_nops=len(rebuilt)-len(b),constant_bytes=const_size,
                constant_sha256=hashlib.sha256(code[constant:constant+const_size]).hexdigest(),
                code_sha256=hashlib.sha256(actual).hexdigest(),negative_instruction_mutation_rejected=True,
                normalization='Only decoded NOPs removed; direct branches map to instruction ordinals; RIP references map to offsets in byte-identical constant block. Mnemonic, registers, widths, memory addressing and other immediates retained.',
                limitation='Not byte-exact; proves normalized instruction correspondence, not generated C equivalence or the whole OpenSSL library identity.',
                nasm_sha256=digest(args.nasm),perl_sha256=digest(args.perl))
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files=files),indent=2),encoding='utf-8')
    print('Verified',len(na),'non-NOP instructions and',const_size,'constant bytes:',root)

if __name__=='__main__':
    main()
