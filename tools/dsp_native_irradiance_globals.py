"""核验实际 CRT 向量初始化，并审计初始化表中未被 Ghidra 索引的入口。"""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require


def main():
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    from unicorn import x86_const as r
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    folder=GENERATED/'native/UnityPlayer.dll';game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game changed');game=game_path.read_bytes()
    table=pe_read(game,0x1818910b0,0x181896f08-0x1818910b0)
    callbacks=[v[0] for v in struct.iter_unpack('<Q',table)]
    marker=read_json(folder/'decompile-manifest.json');require(marker['files']['functions.json']==digest(folder/'functions.json'),'Function index changed')
    known={int(f['address'],16) for f in read_json(folder/'functions.json')['functions']}
    missing=sorted(set(callbacks)-known-{0})
    root=folder/'irradiance-globals';root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    records=[];decoder=Cs(CS_ARCH_X86,CS_MODE_64)
    for entry,end,target,constant,size in [(0x180042420,0x18004244f,0x181c82010,0x181aeda00,16),(0x1800426e0,0x180042700,0x181c81fb0,0x181aeca60,4)]:
        code=pe_read(game,entry,end-entry);ins=list(decoder.disasm(code,entry))
        require(sum(i.size for i in ins)==len(code),'Initializer decode incomplete')
        source=pe_read(game,constant,size);expected=source if size==16 else source*4
        runs=[]
        for pattern in (0,0xff,0x55):
            m=Uc(UC_ARCH_X86,UC_MODE_64)
            for page in (entry&~4095,target&~4095,constant&~4095,0x3000000,0x4000000):m.mem_map(page,4096)
            m.mem_write(entry,code);m.mem_write(constant,source);m.mem_write(target,bytes([pattern])*16)
            stack=0x3000808;m.mem_write(stack,(0x4000000).to_bytes(8,'little'));m.reg_write(r.UC_X86_REG_RSP,stack)
            writes=[];returned=[]
            def visit(uc,pc,size,_):
                if pc==0x4000000:returned.append(True);uc.emu_stop()
            def write(uc,access,address,size,value,_):
                if target<=address and address+size<=target+16:writes.append((address,size))
                else:require(0x3000000<=address and address+size<=0x3001000,'Unexpected initializer write')
            m.hook_add(UC_HOOK_CODE,visit);m.hook_add(UC_HOOK_MEM_WRITE,write);m.emu_start(entry,0,count=50)
            require(bytes(m.mem_read(target,16))==expected and returned==[True] and m.reg_read(r.UC_X86_REG_RSP)==stack+8,'Vector initializer differs')
            require(sum(n for _,n in writes)==16,'Vector output extent differs');runs.append(pattern)
        (root/f'{entry:x}.asm').write_text(''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in ins),encoding='utf-8')
        records.append(dict(address=f'{entry:x}',end_exclusive=f'{end:x}',target=f'{target:x}',constant=f'{constant:x}',
            output_hex=expected.hex(),output_words=[f'{v:08x}' for v in struct.unpack('<IIII',expected)],
            table_slots=[n for n,v in enumerate(callbacks) if v==entry],previous_byte_patterns=runs,indexed=entry in known))
    report=dict(status='crt-vector-initializers-verified',source_sha256=GAME_SHA,initializers=records,
        index_sha256=digest(folder/'functions.json'),unique_callbacks=len(set(callbacks)-{0}),missing_entry_count=len(missing),
        missing_entries=[f'{a:x}' for a in missing],
        limitation='Proves two initializer instruction outputs and static table membership. Missing entries mean absent exact Ghidra function starts, not necessarily wholly absent bytes or all distinct semantic functions. No full startup execution or proof against later mutation.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'missing-callbacks.txt').write_text(''.join(f'{a:x}\n' for a in missing),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print(f'Two vector initializers verified; {len(missing)} of {len(set(callbacks)-{0})} unique callback starts absent from current index')


if __name__=='__main__':main()
