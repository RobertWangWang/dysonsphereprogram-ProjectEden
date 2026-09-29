"""重建 32 位 ChaCha20 标量/SSSE3/XOP，应用已核验 DIR32 重定位后完整字节比较。"""
import argparse
import hashlib
import json
import os
import shutil
import struct
import subprocess
from pathlib import Path
from capstone import Cs, CS_ARCH_X86, CS_MODE_32
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import coff, pe_read, require

SHA='c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1'
COMMIT='b2758a2292aceda93e9f44c219b94fe21bb9a650'
ENTRY=0x1001c780
CAPABILITIES=0x10ea4990
INPUTS={
    'chacha-x86.pl':'4d09c59ef1bf92dec9b15a58070172308633381caf3c0e48d67a1926c89c2cef',
    'x86asm.pl':'0b0e8c1aaac5cab5126dce2798a964ab86a366ce8efd9a98fea8ded296216c80',
    'x86nasm.pl':'2d64ebec48cb21bab16cecc4d8852ad73c290809100466a7f2bb41cae5f5dc8d',
}

def pe_relocations(game):
    pe=struct.unpack_from('<I',game,60)[0];opt=pe+24
    require(struct.unpack_from('<H',game,opt)[0]==0x10b,'Expected PE32')
    image=struct.unpack_from('<I',game,opt+28)[0]
    rva,size=struct.unpack_from('<II',game,opt+96+5*8)
    raw=pe_read(game,image+rva,size);pos=0;result={}
    while pos<len(raw):
        page,count=struct.unpack_from('<II',raw,pos)
        require(count>=8 and count%2==0 and pos+count<=len(raw),'Invalid relocation block')
        for off in range(pos+8,pos+count,2):
            word=struct.unpack_from('<H',raw,off)[0]
            if word>>12:result[image+page+(word&4095)]=word>>12
        pos+=count
    sections=[]
    n=struct.unpack_from('<H',game,pe+6)[0];optional=struct.unpack_from('<H',game,pe+20)[0]
    for i in range(n):
        h=opt+optional+i*40
        virtual,rva,raw_size=struct.unpack_from('<III',game,h+8);flags=struct.unpack_from('<I',game,h+36)[0]
        if image+rva<=CAPABILITIES and CAPABILITIES+16<=image+rva+max(virtual,raw_size):
            sections.append(dict(name=game[h:h+8].rstrip(b'\0').decode(),writable=bool(flags&0x80000000)))
    require(len(sections)==1 and sections[0]['writable'],'Capability storage not in writable image section')
    return result,sections[0]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('reference','perl','nasm'):parser.add_argument('--'+name,type=Path,required=True)
    args=parser.parse_args();inv=read_json(GENERATED/'native/inventory.json')
    module='DSPGAME_Data/Plugins/x86_64/rail_api.dll';game_path=Path(inv['game_directory'])/module
    require(digest(game_path)==SHA,'Game baseline changed');game=game_path.read_bytes()
    root=GENERATED/'native'/module.replace('/','__')/'upstream-openssl/chacha32'
    root.mkdir(parents=True,exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    for name,sha in INPUTS.items():require(digest(args.reference/name)==sha,'Upstream input changed: '+name)
    for name in [*INPUTS,'LICENSE']:shutil.copyfile(args.reference/name,root/name)
    env=os.environ.copy();env['PATH']=str(args.nasm.resolve().parent)+os.pathsep+env.get('PATH','')
    subprocess.run([str(args.perl.resolve()),'./chacha-x86.pl','win32n','-DOPENSSL_IA32_SSE2','chacha-x86.asm'],cwd=root,env=env,check=True,capture_output=True)
    subprocess.run([str(args.nasm.resolve()),'-f','win32','chacha-x86.asm','-o','chacha-x86.obj'],cwd=root,check=True,capture_output=True)
    obj=(root/'chacha-x86.obj').read_bytes();sections,symbols=coff(obj,expected_machine=0x14c)
    names={name:(value,section) for name,value,section in symbols.values()}
    require(names['_ChaCha20_ctr32']==(0,1) and names['_ChaCha20_ssse3']==(0x500,1)
            and names['_ChaCha20_xop']==(0x800,1) and names['L$ssse3_data']==(0x740,1),'Unexpected routine/constant layout')
    name,code,reloc,count=sections[0]
    require(name=='.text' and len(code)==4209 and count==1,'Unexpected text size or relocation count')
    offset,symbol,kind=struct.unpack_from('<IIH',obj,reloc)
    require(offset==0x18 and kind==6 and symbols[symbol]==('_OPENSSL_ia32cap_P',16,0),'Unexpected COFF relocation')
    require(struct.unpack_from('<I',code,offset)[0]==0,'Unexpected relocation addend')
    relocations,storage=pe_relocations(game)
    require({a:k for a,k in relocations.items() if ENTRY<=a<ENTRY+len(code)}=={ENTRY+offset:3},'Game HIGHLOW fixup set differs')
    original=pe_read(game,ENTRY,len(code))
    require(struct.unpack_from('<I',original,offset)[0]==CAPABILITIES,'Capability address differs')
    struct.pack_into('<I',code,offset,CAPABILITIES)
    require(bytes(code)==original,'Complete relocated byte comparison failed')
    decoder=Cs(CS_ARCH_X86,CS_MODE_32);decoder.detail=True
    first=list(decoder.disasm(original[:0x740],ENTRY));last=list(decoder.disasm(original[0x800:],ENTRY+0x800))
    require(sum(i.size for i in first)==0x740 and sum(i.size for i in last)==len(code)-0x800,'Incomplete code decoding')
    instructions=first+last;xop=[i for i in instructions if i.mnemonic=='vprotd']
    require(xop and any(i.address==0x1001d168 for i in xop),'Original XOP gap not covered')
    negatives=[]
    for label,pos in [('capability-relocation',offset),('constant-data',0x740),('xop-rotation',xop[0].address-ENTRY+xop[0].size-1)]:
        changed=bytearray(code);changed[pos]^=1
        require(bytes(changed)!=original,'Mutation accepted');negatives.append(dict(kind=label,offset=pos,rejected=True))
    def listing(items):return ''.join(f'{i.address:x}  {i.bytes.hex()}  {i.mnemonic} {i.op_str}\n' for i in items)
    data=''.join(f'{ENTRY+p:x}  {original[p:p+16].hex()}  DB '+','.join(f'0x{x:02x}' for x in original[p:p+16])+' ; constant/static data\n' for p in range(0x740,0x800,16))
    (root/'game.asm').write_text(listing(first)+data+listing(last),encoding='utf-8')
    (root/'relocated.bin').write_bytes(code)
    report=dict(status='byte-exact-source-matched',source_sha256=SHA,module=module,name='ChaCha20_ctr32',
        address=f'{ENTRY:x}',body_entry=f'{ENTRY:x}',end_exclusive=f'{ENTRY+len(code):x}',assembly_file='chacha-x86.asm',
        aliases=['ChaCha20_ssse3','ChaCha20_xop','1001cc80','1001cc84','1001cf80','1001cf84'],
        upstream_commit=COMMIT,upstream_url=f'https://github.com/openssl/openssl/blob/{COMMIT}/crypto/chacha/asm/chacha-x86.pl',
        game_bytes=len(code),rebuilt_bytes=len(code),constant_bytes=192,instruction_count=len(instructions),xop_instructions=len(xop),
        code_sha256=hashlib.sha256(original).hexdigest(),negative_cases=negatives,
        relocation=dict(object_type='IMAGE_REL_I386_DIR32',game_type='IMAGE_REL_BASED_HIGHLOW',offset=offset,
                        symbol='_OPENSSL_ia32cap_P',target=f'{CAPABILITIES:x}',storage=storage),
        limitation='Complete object text including 192 bytes static data matches after one verified external-symbol relocation. Three upstream entry points share one Ghidra function record; no new C count or whole-library identity claim.',
        nasm_sha256=digest(args.nasm),perl_sha256=digest(args.perl))
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print(f'Verified {len(code)} exact bytes, {len(instructions)} instructions, {len(xop)} XOP rotations and one DIR32/HIGHLOW relocation')

if __name__=='__main__':main()
