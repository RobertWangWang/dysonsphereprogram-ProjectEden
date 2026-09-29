"""Probe recovered command dispatch with explicit bounded external-helper models."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require
from dsp_native_continuation_bodies import validate_cached as validate_bodies


def validate_cached(folder, sha):
    root=folder/'memory-command-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Command source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Command evidence differs')
    result=read_json(root/'report.json')
    for name,value in result['dependencies'].items():require(digest(folder/name)==value,'Command dependency differs')
    return result


def main():
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path']
    require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes();require(validate_bodies(folder,module['sha256']),'Bodies missing')
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    for address in (0x104c2000,0x104c3000,0x105fb000,0x200000,0x300000,0x500000):uc.mem_map(address,0x1000)
    uc.mem_write(0x104c2d30,pe_read(game,0x104c2d30,486));uc.mem_write(0x500000,b'\xf4')
    stack=0x200800;base=0x300000;state=base+0x100;record=base+0x200;shadow=base+0x240;buffer=base+0x300;argument=base+0x400
    visited=set();calls=[];writes=[];returned=[False];cases=0
    preserved={reg.UC_X86_REG_EBX:0x12345678,reg.UC_X86_REG_EBP:0x87654321,reg.UC_X86_REG_ESI:0x87651234,reg.UC_X86_REG_EDI:0x43215678}
    def hook(machine,address,size,unused):
        if address==0x500000:returned[0]=True;machine.emu_stop();return
        if address in (0x105fb540,0x104c3040,0x104c30c0):
            sp=machine.reg_read(reg.UC_X86_REG_ESP);count={0x105fb540:3,0x104c3040:2,0x104c30c0:1}[address]
            args=struct.unpack('<'+'I'*(count+1),machine.mem_read(sp,4*(count+1)));calls.append([address,list(args[1:])])
            if address==0x105fb540:
                dst,value,n=args[1:];require(dst==buffer and value==0 and n==16,'Unexpected memset contract')
                machine.mem_write(dst,bytes(n))
            # Two opaque object helpers return without mutation in this harness only.
            machine.reg_write(reg.UC_X86_REG_EAX,args[1] if address==0x105fb540 else 0)
            machine.reg_write(reg.UC_X86_REG_ESP,sp+4);machine.reg_write(reg.UC_X86_REG_EIP,args[0]);return
        require(0x104c2d30<=address<=0x104c2e60,'Unexpected execution');visited.add(address)
    uc.hook_add(UC_HOOK_CODE,hook)
    uc.hook_add(UC_HOOK_MEM_WRITE,lambda machine,access,address,size,value,unused:writes.append((address,size)))
    def check(command,flags,buffer_present,length,arg_present):
        nonlocal cases
        original=bytearray([0xa5])*2048
        def put(offset,value):struct.pack_into('<I',original,offset,value)
        put(0x10,0x87654321);put(0x14,flags);put(0x1c,0xdeadbeef);put(0x20,state)
        struct.pack_into('<II',original,0x100,record,shadow)
        struct.pack_into('<4I',original,0x200,11,buffer if buffer_present else 0,16,23)
        struct.pack_into('<4I',original,0x240,length,buffer+4,9,17)
        struct.pack_into('<4I',original,0x400,3,buffer+8,5,19)
        expected=bytearray(original);expected_calls=[];value=0xfedcba98;arg=argument if arg_present else 0;result=1
        def setval(offset,v):struct.pack_into('<I',expected,offset,v)
        if command==1:
            if buffer_present:
                if flags&0x600:setval(0x200,16)
                else:
                    expected_calls.append([0x105fb540,[buffer,0,16]]);expected[0x300:0x310]=bytes(16);setval(0x200,0)
                expected[0x240:0x250]=expected[0x200:0x210]
        elif command==2:result=int(length==0)
        elif command==3:
            result=length
            if arg:setval(0x400,buffer+4)
        elif command==8:result=0x87654321
        elif command==9:setval(0x10,value)
        elif command==10:result=length
        elif command in (11,12):pass
        elif command==0x72:
            require(arg,'This command requires readable argument in harness');expected_calls.append([0x104c3040,[base,0]])
            setval(0x10,value);setval(0x100,arg);expected[0x240:0x250]=expected[0x400:0x410];setval(0x20,state)
        elif command==0x73:
            if arg:expected_calls.append([0x104c30c0,[base]]);setval(0x400,shadow)
        elif command==0x82:setval(0x1c,value)
        else:result=0
        uc.mem_write(base,bytes(original));uc.mem_write(stack,struct.pack('<5I',0x500000,base,command,value,arg))
        uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        for r,v in preserved.items():uc.reg_write(r,v)
        calls.clear();writes.clear();returned[0]=False;uc.emu_start(0x104c2d30,0,count=1000)
        require(returned[0] and uc.reg_read(reg.UC_X86_REG_EAX)==result and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'Command return differs')
        require(calls==expected_calls,'External call contract differs')
        require(bytes(uc.mem_read(base,2048))==bytes(expected),'Command memory differs')
        require(all(uc.reg_read(r)==v for r,v in preserved.items()),'Command ABI differs')
        require(all(stack-64<=a and a+n<=stack or base<=a and a+n<=base+2048 for a,n in writes),'Unexpected command write')
        cases+=1
    for command in list(range(256))+[0x7fffffff,0x80000000,0xfffffffe,0xffffffff]:
        for flags in (0,0x200,0x400,0x600,0x800):
            for buffer_present in (False,True):
                for length in (0,7):
                    for arg_present in (False,True):
                        if command==0x72 and not arg_present:continue
                        check(command,flags,buffer_present,length,arg_present)
    expected_instructions={int(line.split(' ',1)[0],16) for line in (folder/'continuation-body-repair/104c2d30.asm').read_text().splitlines()}
    require(visited==expected_instructions,'Command instruction coverage incomplete')
    uc.mem_write(0x104c2e44,b'\x14');uc.ctl_remove_cache(0x104c2d30,0x104c2e61)
    negative=False
    try:check(9,0,True,7,True)
    except (ValueError,AssertionError):negative=True
    require(negative,'Wrong command field offset escaped detector')
    result=dict(source_sha256=module['sha256'],cases=cases,instructions=len(visited),negative_control_caught=negative,
                external_models={'105fb540':'bounded memset of 16 bytes','104c3040':'no-op returning zero','104c30c0':'no-op returning zero'},
                dependencies={'continuation-body-repair/manifest.json':digest(folder/'continuation-body-repair/manifest.json')},
                limitation='Entire caller instruction body exercised, but external helpers modeled explicitly; their original side effects and end-to-end object lifecycle are unproven. Does not establish full function equivalence.')
    root=folder/'memory-command-behavior';root.mkdir(exist_ok=True)
    (root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps(result))


if __name__=='__main__':main()
