"""离线核验 WGL 初始化片段的六个符号请求、四个保存槽与十一项能力标志。"""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require
from dsp_native_property_catalog import cstring


def validate_cached(folder,sha):
    root=folder/'wgl-initialization'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'WGL baseline changed')
    for name,value in marker['files'].items():require(digest(root/name)==value,'WGL evidence changed')
    return read_json(root/'report.json')


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    from capstone.x86 import X86_OP_MEM,X86_REG_RIP,X86_REG_RCX,X86_REG_RDX
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    from unicorn.x86_const import UC_X86_REG_RAX,UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_RSP,UC_X86_REG_RIP
    from dsp_native_sha512_unwind import imports
    folder=GENERATED/'native/UnityPlayer.dll';root=folder/'wgl-initialization';root.mkdir(exist_ok=True)
    path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(path)==GAME_SHA,'Game changed');game=path.read_bytes()
    start,end=0x1810eb6c9,0x1810eb85b;raw=pe_read(game,start,end-start)
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True;ins=list(decoder.disasm(raw,start))
    require(sum(i.size for i in ins)==len(raw),'Incomplete source decoding')
    names=[];extensions=[];flags=[];imported=imports(game);resolver_slots=set()
    for i in ins:
        if i.mnemonic=='lea' and i.operands[1].type==X86_OP_MEM and i.operands[1].mem.base==X86_REG_RIP:
            address=i.address+i.size+i.operands[1].mem.disp
            row=dict(setup=f'{i.address:x}',string_address=f'{address:x}',name=cstring(game,address))
            if i.operands[0].reg==X86_REG_RCX:names.append(row)
            elif i.operands[0].reg==X86_REG_RDX:extensions.append(row)
        if i.mnemonic=='setne':
            op=i.operands[0];require(op.type==X86_OP_MEM and op.mem.base==X86_REG_RIP,'Unexpected flag destination');flags.append(i.address+i.size+op.mem.disp)
        if i.mnemonic=='call' and i.operands[0].type==X86_OP_MEM:
            op=i.operands[0];require(op.mem.base==X86_REG_RIP,'Unexpected indirect call');slot=i.address+i.size+op.mem.disp
            require(imported[slot]==('OPENGL32.dll','wglGetProcAddress'),'Unexpected resolver import');resolver_slots.add(slot)
    require(len(names)==6 and len(extensions)==11 and flags==list(range(0x181d482d0,0x181d482db)) and len(resolver_slots)==1,'Initialization shape differs')
    for row,flag in zip(extensions,flags):row['flag_address']=f'{flag:x}'
    uc=Uc(UC_ARCH_X86,UC_MODE_64);mapped=set()
    def page(address):
        base=address&~4095
        if base not in mapped:uc.mem_map(base,4096);mapped.add(base)
    for address in (start,0x181834e78,0x181cdc158,flags[0],0x600000,0x500000,next(iter(resolver_slots))):page(address)
    uc.mem_write(start,raw);uc.mem_write(0x181834e78,b'\xc3');uc.mem_write(0x500000,b'\xc3');uc.mem_write(0x500010,b'\xc3')
    for row in names+extensions:
        address=int(row['string_address'],16);data=row['name'].encode()+b'\0';page(address);page(address+len(data)-1);uc.mem_write(address,data)
    uc.mem_write(next(iter(resolver_slots)),struct.pack('<Q',0x500000))
    by_name={int(r['string_address'],16):n for n,r in enumerate(names)};by_ext={int(r['string_address'],16):n for n,r in enumerate(extensions)}
    config={};requests=[];checks=[];writes=[];visited=set()
    def pointer(n,mask):return (0x500010 if n==0 else 0x510000+n*16) if mask&(1<<n) else 0
    def step(machine,address,size,user):
        visited.add(address)
        if address==0x500000:
            n=by_name[machine.reg_read(UC_X86_REG_RCX)];requests.append(n);machine.reg_write(UC_X86_REG_RAX,pointer(n,config['symbols']))
        elif address==0x500010:machine.reg_write(UC_X86_REG_RAX,0x700000)
        elif address==0x181834e78:
            require(machine.reg_read(UC_X86_REG_RCX)==0x700000,'Capability source pointer differs')
            n=by_ext[machine.reg_read(UC_X86_REG_RDX)];checks.append(n);machine.reg_write(UC_X86_REG_RAX,config['nonnull'] if config['mask']&(1<<n) else 0)
    uc.hook_add(UC_HOOK_CODE,step)
    uc.hook_add(UC_HOOK_MEM_WRITE,lambda machine,access,address,size,value,user:writes.append((address,size)))
    saved={0:0x181cdc160,1:0x181cdc168,4:0x181cdc158,5:0x181cdc170};cases=0
    def run(mask,symbols,pattern,nonnull):
        nonlocal cases
        slots=bytearray([pattern]*4096);flagdata=bytearray([pattern]*4096);expected_slots=bytearray(slots);expected_flags=bytearray(flagdata)
        for n,address in saved.items():struct.pack_into('<Q',expected_slots,address-0x181cdc000,pointer(n,symbols))
        if symbols&1:
            for n,address in enumerate(flags):expected_flags[address-0x181d48000]=int(bool(mask&(1<<n)))
        uc.mem_write(0x181cdc000,bytes(slots));uc.mem_write(0x181d48000,bytes(flagdata));uc.reg_write(UC_X86_REG_RSP,0x600800)
        config.update(mask=mask,symbols=symbols,nonnull=nonnull);requests.clear();checks.clear();writes.clear()
        uc.emu_start(start,end,count=500)
        require(uc.reg_read(UC_X86_REG_RIP)==end and uc.reg_read(UC_X86_REG_RSP)==0x600800,'Fragment exit/stack differs')
        require(requests==list(range(6)) and checks==(list(range(11)) if symbols&1 else []),'Request/check sequence differs')
        require(bytes(uc.mem_read(0x181cdc000,4096))==bytes(expected_slots),'Stored procedure slots differ')
        require(bytes(uc.mem_read(0x181d48000,4096))==bytes(expected_flags),'Flag updates differ')
        require(all((a in saved.values() and size==8) or (a in flags and size==1) or (0x600000<=a and a+size<=0x601000) for a,size in writes),'Unexpected write')
        cases+=1
    for mask in range(2048):
        for pattern,value in ((0,1),(0xa5,0x8000000000000000)):run(mask,63,pattern,value)
    for symbols in range(64):run(0x555,symbols,0x5a,1)
    require(all(i.address in visited for i in ins),'Uncovered initializer instruction')
    # Moving the final flag store one byte backwards must break the object check.
    displacement=int.from_bytes(raw[-4:],'little',signed=True)
    uc.mem_write(end-4,(displacement-1).to_bytes(4,'little',signed=True));rejected=False
    try:run(1<<10,63,0,1)
    except ValueError:rejected=True
    require(rejected,'Mutated flag destination accepted')
    for n,address in saved.items():names[n]['saved_slot']=f'{address:x}'
    for n in (2,3):names[n]['saved_slot']=None
    (root/'initializer.asm').write_text(''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in ins),encoding='utf-8')
    report=dict(source_sha256=GAME_SHA,function='1810eb670',begin=f'{start:x}',end_exclusive=f'{end:x}',bytes=len(raw),instructions=len(ins),cases=cases,mutated_flag_destination_rejected=rejected,symbol_requests=names,capability_checks=extensions,
        resolver_import_slot=f'{next(iter(resolver_slots)):x}',no_get_extensions_procedure_behavior='Four result slots are still assigned, but all eleven flag bytes keep their prior values.',
        limitation='Original machine-code fragment with controlled resolver, extension-provider and search-helper return stubs. No graphics context/API execution, real driver, helper substring semantics, entry preconditions or later mutation verified. Six resolver results vary independently; all 2048 search-result truth masks covered.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={n:digest(root/n) for n in ('report.json','initializer.asm')}),indent=2),encoding='utf-8')
    print(json.dumps(report))


if __name__=='__main__':main()
