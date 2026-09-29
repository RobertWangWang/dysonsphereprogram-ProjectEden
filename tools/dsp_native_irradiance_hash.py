"""恢复辐照度输入的 32 位逐字哈希和四字块折叠语义，并与实际指令抽样比较。"""
import hashlib
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

MASK=0xffffffff
HASH=0x1813f10f0
FOLD=0x1813bf290


def validate_cached(folder,sha):
    root=folder/'irradiance-hash'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Hash evidence baseline changed')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Hash evidence changed')
    return read_json(root/'report.json')


def word_hash(words,seed):
    def rot(x,n):return ((x<<n)|(x>>(32-n)))&MASK
    a=b=c=(0xdeadbeef+4*len(words)+seed)&MASK
    pos=0;remaining=len(words)
    while remaining>3:
        a=(a+words[pos])&MASK;b=(b+words[pos+1])&MASK;c=(c+words[pos+2])&MASK
        a=((a-c)&MASK)^rot(c,4);c=(c+b)&MASK
        b=((b-a)&MASK)^rot(a,6);a=(a+c)&MASK
        c=((c-b)&MASK)^rot(b,8);b=(b+a)&MASK
        a=((a-c)&MASK)^rot(c,16);c=(c+b)&MASK
        b=((b-a)&MASK)^rot(a,19);a=(a+c)&MASK
        c=((c-b)&MASK)^rot(b,4);b=(b+a)&MASK
        remaining-=3;pos+=3
    if remaining==0:return c
    if remaining>=3:c=(c+words[pos+2])&MASK
    if remaining>=2:b=(b+words[pos+1])&MASK
    a=(a+words[pos])&MASK
    c=((c^b)-rot(b,14))&MASK;a=((a^c)-rot(c,11))&MASK
    b=((b^a)-rot(a,25))&MASK;c=((c^b)-rot(b,16))&MASK
    a=((a^c)-rot(c,4))&MASK;b=((b^a)-rot(a,14))&MASK
    return ((c^b)-rot(b,24))&MASK


def main():
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    from unicorn import x86_const as r
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    folder=GENERATED/'native/UnityPlayer.dll';game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game changed');game=game_path.read_bytes()
    functions={int(f['address'],16):f for f in read_json(folder/'functions.json')['functions']}
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);artifacts={};ranges={}
    for address in (HASH,FOLD):
        raw=pe_read(game,address,functions[address]['size']);ins=list(decoder.disasm(raw,address))
        require(sum(i.size for i in ins)==len(raw),'Incomplete hash decoding')
        ranges[address]=(address,address+len(raw))
        artifacts[f'{address:x}.asm']=''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in ins)
    fallback=struct.unpack('<IIII',pe_read(game,0x181c32128,16))
    m=Uc(UC_ARCH_X86,UC_MODE_64)
    for page in (HASH&~4095,FOLD&~4095,0x181c32000,0x181c41000,0x181814000,0x2000000,0x3000000,0x4000000):m.mem_map(page,4096)
    for address,(start,end) in ranges.items():m.mem_write(address,pe_read(game,address,end-start))
    m.mem_write(0x181c32128,pe_read(game,0x181c32128,16));m.mem_write(0x181c41228,(0x123456).to_bytes(8,'little'));m.mem_write(0x181814290,b'\xc3')
    stack=0x3000808;m.mem_write(stack,(0x4000000).to_bytes(8,'little'));state={}
    def visit(uc,pc,size,_):
        if pc==0x4000000:state['result']=uc.reg_read(r.UC_X86_REG_EAX);uc.emu_stop()
        elif pc==0x181814290:require(uc.reg_read(r.UC_X86_REG_RCX)==0x123456,'Fold cookie differs')
        else:require(any(a<=pc<b for a,b in ranges.values()),'Execution escaped hash functions')
    def write(uc,access,address,size,value,_):require(0x3000000<=address and address+size<=0x3001000,'Hash writes outside stack')
    m.hook_add(UC_HOOK_CODE,visit);m.hook_add(UC_HOOK_MEM_WRITE,write)
    def execute(entry,count,seed):
        state['result']=None
        for reg,val in ((r.UC_X86_REG_RCX,0x2000000),(r.UC_X86_REG_RDX,count&((1<<64)-1)),(r.UC_X86_REG_R8,seed),(r.UC_X86_REG_RSP,stack),(r.UC_X86_REG_EFLAGS,2)):m.reg_write(reg,val)
        saved=[r.UC_X86_REG_RBX,r.UC_X86_REG_RBP,r.UC_X86_REG_RSI,r.UC_X86_REG_RDI,r.UC_X86_REG_R12,r.UC_X86_REG_R13,r.UC_X86_REG_R14,r.UC_X86_REG_R15]
        for n,reg in enumerate(saved):m.reg_write(reg,0x77770000+n)
        m.emu_start(entry,0,count=100000)
        require(m.reg_read(r.UC_X86_REG_RSP)==stack+8,'Hash stack differs')
        require(all(m.reg_read(reg)==0x77770000+n for n,reg in enumerate(saved)),'Hash nonvolatile register differs')
        return state['result']
    cases=[]
    for n in list(range(66))+[127,128,129,255,256]:
        for seed in (0,1,0xffffffff,0xfafafafa):
            for pattern in range(3):
                words=[0 if pattern==0 else MASK if pattern==1 else ((i*0x9e3779b9)^(i<<13)^seed)&MASK for i in range(n)]
                if words:m.mem_write(0x2000000,struct.pack('<'+'I'*n,*words))
                result=execute(HASH,n,seed);require(result==word_hash(words,seed),'Word hash model differs')
                cases.append(dict(kind='words',count=n,seed=seed,pattern=pattern,result=result))
    for count in (-1,0,1,2,3,4,8):
        for seed in (0,1,0xffffffff,0xfafafafa):
            for pattern in range(3):
                expected=seed
                for i in range(max(count,0)):
                    null=pattern==1 or (pattern==2 and i%2==0)
                    ptr=0 if null else 0x2000100+i*16
                    words=[(i*0x10203+j*0x112233+seed)&MASK for j in range(4)]
                    m.mem_write(0x2000000+i*8,ptr.to_bytes(8,'little'))
                    if ptr:m.mem_write(ptr,struct.pack('<IIII',*words))
                    expected=word_hash(fallback if null else words,expected)
                result=execute(FOLD,count,seed);require(result==expected,'Folded hash model differs')
                cases.append(dict(kind='fold',count=count,seed=seed,pattern=pattern,result=result))
    root=folder/'irradiance-hash';root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    for name,text in artifacts.items():(root/name).write_text(text,encoding='utf-8')
    report=dict(status='sampled-word-hash-and-fold-verified',source_sha256=GAME_SHA,word_hash=f'{HASH:x}',fold=f'{FOLD:x}',
        fallback_words=list(fallback),cases=cases,case_count=len(cases),model_sha256=digest(Path(__file__)),
        limitation='Sampled synthetic inputs, original integer instructions and real nested hash call; only cookie check is a return stub. No whole-function lighting equivalence, collision-freedom or upstream source identity claim.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print(f'{len(cases)} word-hash and four-word folding cases passed; fallback words {fallback}')


if __name__=='__main__':main()
