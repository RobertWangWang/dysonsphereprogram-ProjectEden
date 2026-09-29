"""离线执行实际复制机器码及六表调用者；测试重叠、边界和 REP/SIMD 路径。"""
import hashlib
import json
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

COPY=0x181835930
CALLER=0x18177f9f0
ARENA=0x2000000
SIZE=0x10000
INITIAL=bytes(((i*73)^(i>>3)^(i>>9))&255 for i in range(SIZE))

def validate_cached(folder):
    root=folder/'copy-behavior';marker=root/'manifest.json'
    if not marker.exists():return {}
    manifest=read_json(marker);require(manifest['source_sha256']==GAME_SHA,'Copy probe source mismatch')
    for name,sha in manifest['files'].items():require(digest(root/name)==sha,'Copy probe artifact changed')
    return read_json(root/'report.json')

def run(game,length,src,dst,flags,threshold,caller=None,corrupt=False):
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    from unicorn.x86_const import UC_X86_REG_RAX,UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8,UC_X86_REG_RSP,UC_X86_REG_RSI,UC_X86_REG_RDI,UC_X86_REG_RBX,UC_X86_REG_RBP,UC_X86_REG_R12,UC_X86_REG_R13,UC_X86_REG_R14,UC_X86_REG_R15,UC_X86_REG_EFLAGS
    m=Uc(UC_ARCH_X86,UC_MODE_64)
    for a in (0x181835000,0x18177f000,0x181cbd000,0x181c41000,0x181e76000,0x1000000,0x3000000,0x4000000):m.mem_map(a,0x1000)
    for a in (0x181835000,0x18177f000):m.mem_write(a,pe_read(game,a,0x1000))
    m.mem_write(0x181e76f30,pe_read(game,0x181e76f30,17*4))
    m.mem_write(0x181cbdc60,bytes([flags]));m.mem_write(0x181c41248,threshold.to_bytes(8,'little'))
    if corrupt:
        # Redirect length-one case to the zero-length return.
        m.mem_write(0x181e76f34,pe_read(game,0x181e76f30,4))
    m.mem_map(ARENA,SIZE);m.mem_write(ARENA,INITIAL)
    stack=0x3000808;sentinel=0x4000000;obj=0x1000000
    m.mem_write(stack,sentinel.to_bytes(8,'little'));m.reg_write(UC_X86_REG_RSP,stack)
    m.reg_write(UC_X86_REG_EFLAGS,2) # Win64 calling convention requires DF clear.
    nonvolatile=[UC_X86_REG_RBX,UC_X86_REG_RBP,UC_X86_REG_RSI,UC_X86_REG_RDI,UC_X86_REG_R12,UC_X86_REG_R13,UC_X86_REG_R14,UC_X86_REG_R15]
    for n,reg in enumerate(nonvolatile):m.reg_write(reg,0x76540000+n*0x123)
    active=True;offset=None
    if caller is None:
        entry=COPY;m.reg_write(UC_X86_REG_RCX,ARENA+dst);m.reg_write(UC_X86_REG_RDX,ARENA+src);m.reg_write(UC_X86_REG_R8,length)
    else:
        kind,stride,offset,source_present=caller;entry=CALLER
        active=1<=kind<=5 and source_present and offset!=0
        length=(([4,8,12,16,16][kind-1]*stride)&0xffffffff) if active else 0
        for field,value,n in [(0x28,kind,4),(0x60,stride,4),(0x230,ARENA+dst-offset,8),(0x240,ARENA+src if source_present else 0,8),(0x250,offset,4)]:m.mem_write(obj+field,value.to_bytes(n,'little'))
        m.reg_write(UC_X86_REG_RCX,obj)
    expected=bytearray(INITIAL)
    if active:expected[dst:dst+length]=INITIAL[src:src+length]
    visited=set();returned=[];copy_calls=[]
    def instruction(uc,pc,size,_):
        visited.add(pc)
        if pc==COPY:copy_calls.append([uc.reg_read(r) for r in (UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8)])
        if pc==sentinel:
            returned.append(uc.reg_read(UC_X86_REG_RAX));uc.emu_stop()
    def write(uc,access,address,size,value,_):
        permitted=(ARENA+dst<=address and address+size<=ARENA+dst+length) or (0x3000000<=address and address+size<=0x3001000)
        if caller is not None and active and obj+0x250<=address and address+size<=obj+0x254:permitted=True
        require(permitted,'Write outside destination/stack/offset field')
    m.hook_add(UC_HOOK_CODE,instruction);m.hook_add(UC_HOOK_MEM_WRITE,write)
    m.emu_start(entry,0,count=100000)
    require(bytes(m.mem_read(ARENA,SIZE))==bytes(expected),'Copied bytes differ from overlap-safe snapshot')
    require(returned==([ARENA+dst] if caller is None else [0]),'Return value differs')
    require(m.reg_read(UC_X86_REG_RSP)==stack+8,'Stack not restored')
    require(all(m.reg_read(reg)==0x76540000+n*0x123 for n,reg in enumerate(nonvolatile)),'Nonvolatile register changed')
    if caller is not None:
        require(copy_calls==([[ARENA+dst,ARENA+src,length]] if active else []),'Caller arguments differ')
        require(int.from_bytes(m.mem_read(obj+0x250,4),'little')==(0 if active else offset),'Caller offset update differs')
    return dict(length=length,source_offset=src,destination_offset=dst,flags=flags,threshold=threshold,caller=caller,
                rep_movsb=0x18183591e in visited,backward_path=0x181835c30 in visited,
                result_sha256=hashlib.sha256(expected).hexdigest()),visited

def main():
    import unicorn
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    inv=read_json(GENERATED/'native/inventory.json');game_path=Path(inv['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game changed');game=game_path.read_bytes()
    root=GENERATED/'native/UnityPlayer.dll/copy-behavior';root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    cases=[];covered=set()
    lengths=list(range(34))+[63,64,65,127,128,129,255,256,257,1023,1024,1025,4095,4096,4097]
    for n in lengths:
        for shift in (0,1,7,15):
            src=0x2000+shift
            for dst in (src,src+1,src-1,src+17,src-17,0x8000+shift):
                for flags,threshold in ((0,0),(0,0x100000),(2,0x100000)):
                    row,seen=run(game,n,src,dst,flags,threshold);cases.append(row);covered|=seen
    direct_count=len(cases)
    for kind in range(7):
        for stride in (0,1,3,33,257):
            for offset in (0,4):
                for source_present in (False,True):
                    for flags in (0,2):
                        for dst in (0x2008,0x8003):
                            row,seen=run(game,0,0x2001,dst,flags,0,caller=(kind,stride,offset,source_present));cases.append(row);covered|=seen
    try:run(game,1,0x2000,0x8000,0,0,corrupt=True)
    except ValueError as error:require(str(error)=='Copied bytes differ from overlap-safe snapshot','Unexpected negative error')
    else:raise ValueError('Corrupt small-copy dispatch accepted')
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);instructions=list(decoder.disasm(pe_read(game,COPY,0x3d5),COPY))
    require(sum(i.size for i in instructions)==0x3d5,'Incomplete copy decoding')
    streaming=[i.address for i in instructions if i.mnemonic in ('movntdq','movntps')]
    require(streaming and all(a in covered for a in streaming),'Streaming stores not exercised')
    report=dict(status='sampled-copy-and-caller-verified',source_sha256=GAME_SHA,engine='Unicorn '+unicorn.__version__,
        direct_cases=direct_count,caller_cases=len(cases)-direct_count,cases=cases,
        rep_cases=sum(c['rep_movsb'] for c in cases),backward_cases=sum(c['backward_path'] for c in cases),
        streaming_store_addresses=[f'{a:x}' for a in streaming],visited_addresses=[f'{a:x}' for a in sorted(covered)],
        negative_small_copy_dispatch_rejected=True,
        inputs={f'{a:x}':hashlib.sha256(pe_read(game,a,n)).hexdigest() for a,n in [(COPY,0x3d5),(0x181835910,19),(CALLER,0x14c),(0x181e76f30,68)]},
        limitation='Sampled valid mapped buffers, DF=0, chosen feature flag and threshold values. Real helper instructions execute in emulator, not the game process. No all-length proof, fault behavior, concurrent mutation, or generated-C type equivalence claim.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'copy.asm').write_text(''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in instructions),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={n:digest(root/n) for n in ('report.json','copy.asm')}),indent=2),encoding='utf-8')
    print(f'{direct_count} direct copy cases and {len(cases)-direct_count} caller integration cases passed; REP, backward and streaming paths exercised')

if __name__=='__main__':main()
