"""核验 FMA4 环境设置点的 CRT 表及启动调用链；回调不执行。"""
import hashlib
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

START,END=0x1818910b0,0x181896f08
INITIALIZER=0x180040e90
WALKER=0x181865b2c
CHAIN=[(0x1818141c4,0x181814090),(0x181814090,0x181813ea4),
       (0x181813ea4,0x181813ef4),(0x181813ef4,WALKER)]


def validate_cached(folder,sha):
    root=folder/'fma4-startup'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Startup baseline changed')
    for name,expected in marker['files'].items():require(digest(root/name)==expected,'Startup evidence changed')
    return read_json(root/'report.json')


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64,CS_GRP_CALL,CS_GRP_JUMP
    from capstone.x86 import X86_OP_IMM,X86_OP_MEM,X86_REG_RIP
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE
    from unicorn import x86_const as r
    folder=GENERATED/'native/UnityPlayer.dll'
    game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game changed');game=game_path.read_bytes()
    index=read_json(folder/'functions.json');functions={int(f['address'],16):f for f in index['functions']}
    marker=read_json(folder/'decompile-manifest.json');require(marker['files']['functions.json']==digest(folder/'functions.json'),'Index changed')
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True
    pe=struct.unpack_from('<I',game,60)[0];rva,size=struct.unpack_from('<II',game,pe+24+112+24)
    ranges={0x180000000+a:0x180000000+b for a,b,c in struct.iter_unpack('<III',pe_read(game,0x180000000+rva,size))}
    code={};sources={};edges=[];bounds=[]
    for address in [a for a,b in CHAIN]+[WALKER]:
        require(address in ranges,'Missing startup runtime function range')
        raw=pe_read(game,address,ranges[address]-address);ins=list(decoder.disasm(raw,address))
        require(sum(i.size for i in ins)==len(raw),'Incomplete startup function decode')
        code[address]=raw;sources[f'{address:x}.asm']=''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in ins)
        sources[f'{address:x}.c']=(folder/functions[address]['file']).read_text(encoding='utf-8')
        wanted=dict(CHAIN).get(address)
        for i in ins:
            if (i.group(CS_GRP_CALL) or i.group(CS_GRP_JUMP)) and i.operands[0].type==X86_OP_IMM and i.operands[0].imm==wanted:
                edges.append(dict(source=f'{address:x}',site=f'{i.address:x}',target=f'{wanted:x}',instruction=i.mnemonic))
            if address==0x181813ef4 and i.mnemonic=='lea':
                for op in i.operands:
                    if op.type==X86_OP_MEM and op.mem.base==X86_REG_RIP:
                        value=i.address+i.size+op.mem.disp
                        if value in (START,END):bounds.append(dict(site=f'{i.address:x}',address=f'{value:x}',destination=i.op_str.split(',')[0]))
    require(len(edges)==5 and {(int(e['source'],16),int(e['target'],16)) for e in edges}==set(CHAIN)
        and {b['address'] for b in bounds}=={f'{START:x}',f'{END:x}'},'Startup edges or table bounds changed')
    table=pe_read(game,START,END-START);pointers=[v[0] for v in struct.iter_unpack('<Q',table)]
    slots=[n for n,v in enumerate(pointers) if v==INITIALIZER];require(len(slots)==1,'Missing/duplicate environment initializer')
    m=Uc(UC_ARCH_X86,UC_MODE_64)
    m.mem_map(WALKER&~4095,4096);m.mem_write(WALKER,code[WALKER])
    m.mem_map(START&~4095,0x6000);m.mem_write(START,table)
    for page in (0x3000000,0x4000000):m.mem_map(page,4096)
    api=0x4000000;stop=0x4000100;m.mem_write(api,b'\xc3');m.mem_write(0x1818910a0,api.to_bytes(8,'little'))
    stack=0x3000808;m.mem_write(stack,stop.to_bytes(8,'little'))
    m.reg_write(r.UC_X86_REG_RSP,stack);m.reg_write(r.UC_X86_REG_RCX,START);m.reg_write(r.UC_X86_REG_RDX,END)
    m.reg_write(r.UC_X86_REG_RBX,0x123456);m.reg_write(r.UC_X86_REG_RDI,0xabcdef)
    callbacks=[];returned=[]
    def visit(uc,pc,size,_):
        if pc==api:callbacks.append(uc.reg_read(r.UC_X86_REG_RAX))
        elif pc==stop:returned.append(True);uc.emu_stop()
    m.hook_add(UC_HOOK_CODE,visit);m.emu_start(WALKER,0,count=100000)
    require(callbacks==[v for v in pointers if v],'Initializer order or filtering differs')
    require(returned==[True] and m.reg_read(r.UC_X86_REG_RSP)==stack+8,'Walker return differs')
    require(m.reg_read(r.UC_X86_REG_RBX)==0x123456 and m.reg_read(r.UC_X86_REG_RDI)==0xabcdef,'Walker ABI differs')
    root=folder/'fma4-startup';root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    for name,text in sources.items():(root/name).write_text(text,encoding='utf-8')
    (root/'table.bin').write_bytes(table)
    report=dict(status='startup-table-and-walker-verified',source_sha256=GAME_SHA,edges=edges,table_bound_references=bounds,
        table_start=f'{START:x}',table_end_exclusive=f'{END:x}',table_slots=len(pointers),nonzero_callbacks=len(callbacks),
        initializer=f'{INITIALIZER:x}',initializer_slot=slots[0],initializer_slot_address=f'{START+slots[0]*8:x}',
        callback_order_sha256=hashlib.sha256(b''.join(v.to_bytes(8,'little') for v in callbacks)).hexdigest(),
        limitation='Static startup chain plus real table-walker simulation. Callback dispatcher returns without executing initializers. Process-attach and CRT success conditions are inferred from retained caller code, not executed end to end; loader timing and all possible indirect writes remain unverified.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print(f'{len(pointers)} startup slots; {len(callbacks)} non-null callbacks match in order; environment slot {slots[0]}')


if __name__=='__main__':main()
