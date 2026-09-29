"""核验三个属性读取函数的 XMM0 浮点返回、标志和数组路径；虚调用用桩。"""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

PROFILES=[('180b4b7e0',0x180b4b984,12,0x94,True,0x180b4b880),
          ('180b4be10',0x180b4bf5c,12,0x94,False,0x180b4be83),
          ('180b52ee0',0x180b52fd8,7,0xc0,False,0x180b52f2f)]


def validate_cached(folder,sha):
    root=folder/'float-readers'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Float reader baseline changed')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Float reader evidence changed')
    return {r['address']:r for r in read_json(root/'verification.json')['functions']}


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    from unicorn.x86_const import UC_X86_REG_RAX,UC_X86_REG_RBX,UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_RDI,UC_X86_REG_RSP,UC_X86_REG_RIP,UC_X86_REG_XMM0
    folder=GENERATED/'native/UnityPlayer.dll';root=folder/'float-readers'
    path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(path)==GAME_SHA,'Game changed');game=path.read_bytes()
    export=read_json(root/'report.json');require(export['source_sha256']==GAME_SHA and export['program_changes_rolled_back'],'Export mismatch')
    require(pe_read(game,0x181aecba8,4)==bytes.fromhex('0000803f'),'Boolean float constant differs')
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);results=[]
    obj,desc,container,array,vtable,stack,stub=0x400000,0x401000,0x402000,0x403000,0x404000,0x501000,0x600000
    payloads=[0,0x80000000,0x3f800000,0xbf800000,0x7f800000,0xff800000,0x7fc12345,0x7fa12345]
    for key,table,special,flagoff,virtual_container,tail in PROFILES:
        row=next(r for r in export['functions'] if r['address']==key)
        require(len(row['warnings'])==2 and any(f'{tail:x}' in w for w in row['warnings']) and any('Treating indirect jump as call' in w for w in row['warnings']),'Unexpected reader warning set')
        code=(root/row['file']).read_text();require('float FUN_'+key+'(' in code,'Return type is not float')
        require('Unknown calling convention' not in code and 'in_RDX' not in code,'Unresolved reader calling convention')
        raw=pe_read(game,int(key,16),table-int(key,16));ins=list(decoder.disasm(raw,int(key,16)))
        require(sum(i.size for i in ins)==len(raw),'Decode incomplete')
        require(pe_read(game,tail,7)==bytes.fromhex('48ffa000010000'),'Virtual tail opcode differs')
        targets=[0x180000000+x for x in struct.unpack('<'+'I'*special,pe_read(game,table,special*4))]
        require(all(int(key,16)<=t<table for t in targets),'Unexpected switch target')
        (root/(key+'.asm')).write_text(''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in ins),encoding='utf-8')
        uc=Uc(UC_ARCH_X86,UC_MODE_64);page=int(key,16)&~4095;length=((table+special*4-page+4095)//4096)*4096
        uc.mem_map(page,length);uc.mem_write(int(key,16),raw);uc.mem_write(table,pe_read(game,table,special*4))
        uc.mem_map(0x181aec000,4096);uc.mem_write(0x181aecba8,bytes.fromhex('0000803f'))
        uc.mem_map(obj,0x10000);uc.mem_map(0x500000,0x3000);uc.mem_map(stub,4096)
        for off in (0x10,0x20,0x30):uc.mem_write(stub+off,b'\xc3')
        config={};calls=[];writes=[];visited=set();cases=0;rax_mismatches=0
        def hook(machine,address,size,user):
            visited.add(address)
            if address in (stub+0x10,stub+0x20,stub+0x30):
                require(machine.reg_read(UC_X86_REG_RCX)==obj,'Virtual this pointer differs');calls.append(address-stub)
                if address==stub+0x10:machine.reg_write(UC_X86_REG_XMM0,config['payload']);machine.reg_write(UC_X86_REG_RAX,0x123456789abcdef0)
                elif address==stub+0x20:machine.reg_write(UC_X86_REG_RAX,config['boolean'])
                else:machine.reg_write(UC_X86_REG_RAX,container)
        def onwrite(machine,access,address,size,value,user):writes.append((address,size))
        uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,onwrite)
        def run(kind,flags,index,count,payload,boolean):
            nonlocal cases,rax_mismatches
            fixture=bytearray(0x5000)
            def put(address,fmt,*values):struct.pack_into(fmt,fixture,address-obj,*values)
            put(obj,'<Q',vtable);put(desc,'<I',(index<<4)|kind);put(desc+0x18,'<Q',obj)
            put(obj+flagoff,'<B',flags);put(obj+0x58,'<Q',array);put(obj+0x68,'<Q',count)
            put(container,'<Q',array);put(container+0x10,'<Q',count)
            for off in (0x7c,0x80,0x84,0x88,0x8c,0x90):put(obj+off,'<I',payload)
            for n in range(4):put(array+n*8+4,'<I',payload)
            for off,value in [(0x100,stub+0x10),(0x110,stub+0x20),(0x130,stub+0x30)]:put(vtable+off,'<Q',value)
            uc.mem_write(obj,bytes(fixture));uc.mem_write(stack,struct.pack('<Q',stub+0xff0))
            for register,value in [(UC_X86_REG_RCX,0),(UC_X86_REG_RDX,desc),(UC_X86_REG_RSP,stack),(UC_X86_REG_RBX,0x1122334455667788),(UC_X86_REG_RDI,0x8877665544332211),(UC_X86_REG_XMM0,0xdeadbeef)]:uc.reg_write(register,value)
            config.update(payload=payload,boolean=boolean);calls.clear();writes.clear()
            expected_calls=[0x30] if virtual_container else [];expected=0
            if kind==special:expected=payload if index<count else 0
            elif special==12 and kind in (0,1,2,4,5,6):expected=payload
            elif kind==(3 if special==12 else 0):expected=payload;expected_calls.append(0x10)
            elif kind in ((7,8,9) if special==12 else (1,2,3)):
                bit=kind-(7 if special==12 else 1);expected=0x3f800000 if flags&(1<<bit) else 0
            elif kind==(10 if special==12 else 4):expected=0x3f800000 if boolean&255 else 0;expected_calls.append(0x20)
            uc.emu_start(int(key,16),stub+0xff0,count=300)
            require(uc.reg_read(UC_X86_REG_RIP)==stub+0xff0 and uc.reg_read(UC_X86_REG_RSP)==stack+8,'Return/stack mismatch')
            require(uc.reg_read(UC_X86_REG_XMM0)&0xffffffff==expected,'Float return bits differ')
            require(calls==expected_calls,'Virtual call sequence differs')
            require(uc.reg_read(UC_X86_REG_RBX)==0x1122334455667788 and uc.reg_read(UC_X86_REG_RDI)==0x8877665544332211,'Nonvolatile register changed')
            require(bytes(uc.mem_read(obj,len(fixture)))==bytes(fixture),'Input object changed')
            require(all(0x500000<=a and a+n<=0x503000 for a,n in writes),'Non-stack write')
            rax_mismatches+=int(uc.reg_read(UC_X86_REG_RAX)&0xffffffff!=expected);cases+=1
        for kind in range(16):
            for flags in range(256):run(kind,flags,2,4,payloads[flags%len(payloads)],flags%2)
        for index in (0,1,2,3,0xfffffff):
            for count in (0,1,3,4):
                for payload in payloads:run(special,0,index,count,payload,0)
        for boolean in (0,1,2,127,128,255,256,257):run(10 if special==12 else 4,0,0,0,0x3f800000,boolean)
        uc.mem_write(0x181aecba8,bytes.fromhex('00000040'));rejected=False
        try:run(7 if special==12 else 1,1,0,0,0,0)
        except ValueError:rejected=True
        require(rejected,'Changed float constant accepted')
        unvisited=[dict(address=f'{i.address:x}',instruction=i.mnemonic) for i in ins if i.address not in visited]
        require(all(i['instruction']=='nop' for i in unvisited),'Unexecuted non-padding instruction')
        results.append(dict(address=key,file=row['file'],cases=cases,return_type='float',return_register='XMM0.low32',tail_jump=f'{tail:x}',vtable_offset='100',
            rax_not_return_bits_cases=rax_mismatches,mutated_float_constant_rejected=rejected,original_instructions=len(ins),executed_original_instructions=sum(i.address in visited for i in ins),
            targets_by_kind={str(n):f'{t:x}' for n,t in enumerate(targets)},unexecuted_padding=unvisited,selector_low_bits=4,special_array_kind=special,array_stride=8,array_value_offset=4,flag_offset=f'{flagoff:x}',warnings=row['warnings']))
    report=dict(source_sha256=GAME_SHA,functions=results,case_count=sum(r['cases'] for r in results),
        limitation='Complete original reader bodies with synthetic objects and stubbed virtual methods; no concrete vtable class identity or external method semantics established. Float bits, stack, nonvolatile RBX/RDI, calls and write scope checked. Existing indirect-tail warnings retained.')
    (root/'verification.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    names=['report.json','verification.json']+[r['address']+suffix for r in results for suffix in ('.c','.asm')]
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={n:digest(root/n) for n in names}),indent=2),encoding='utf-8')
    print(json.dumps(report))


if __name__=='__main__':main()
