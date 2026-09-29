"""Original cleanup/cleanse/callback dispatch with recorded allocator boundaries only."""
import itertools
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_continuation_bodies import validate_cached as validate_bodies


def validate_cached(folder,sha):
    root=folder/'buffer-cleanup-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Cleanup source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Cleanup evidence differs')
    result=read_json(root/'report.json')
    for name,value in result['dependencies'].items():require(digest(folder/name)==value,'Cleanup dependency differs')
    return result


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path']
    require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes();require(validate_bodies(folder,module['sha256']),'Missing verified caller')
    windows=[(0x104c2d30,486),(0x104c3040,119),(0x104c5fc0,29),(0x104c6090,70),(0x10529120,38),(0x104f52d0,77),(0x10001360,103)]
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    pages={0x10e1d000,0x10603000,0x200000,0x300000,0x301000,0x500000,0x600000}
    for start,size in windows:pages.update(range(start&~4095,(start+size+4095)&~4095,4096))
    for page in sorted(pages):uc.mem_map(page,4096)
    evidence=[];instructions={};cs=Cs(CS_ARCH_X86,CS_MODE_32)
    for start,size in windows:
        raw=pe_read(game,start,size);uc.mem_write(start,raw);evidence.append(dict(address=f'{start:x}',size=size,bytes_hex=raw.hex()))
        if start!=0x104c2d30:
            decoded=list(cs.disasm(raw,start));require(sum(i.size for i in decoded)==size,'Incomplete helper decode')
            instructions[start]={i.address for i in decoded if i.mnemonic!='nop'}
    uc.mem_write(0x500000,b'\xf4');uc.mem_write(0x600000,b'\xc3');uc.mem_write(0x10603677,b'\xc3')
    stack=0x200800;base=0x300000;state=base+0x100;record=base+0x200;work=base+0x240;replacement=base+0x300;data=base+0x1000
    visited=set();events=[];writes=[];done=[False];cleanse_calls=[0];active_length=[0];active_data=[data]
    preserved={reg.UC_X86_REG_EBX:0xa1a2a3a4,reg.UC_X86_REG_EBP:0xb1b2b3b4,reg.UC_X86_REG_ESI:0xc1c2c3c4,reg.UC_X86_REG_EDI:0xd1d2d3d4}
    def hook(machine,address,size,unused):
        if address==0x500000:done[0]=True;machine.emu_stop();return
        if address in (0x10603677,0x600000):
            sp=machine.reg_read(reg.UC_X86_REG_ESP);n=3 if address==0x600000 else 1
            values=struct.unpack('<'+'I'*(n+1),machine.mem_read(sp,4*(n+1)))
            pointer=values[1];events.append(dict(pointer=pointer,route='custom' if n==3 else 'default',metadata=list(values[2:])))
            if pointer==active_data[0]:require(bytes(machine.mem_read(pointer,active_length[0]))==bytes(active_length[0]),'Data not cleansed before release')
            # Only allocator boundaries are modeled: record event and return, no real heap access.
            machine.reg_write(reg.UC_X86_REG_EAX,0);machine.reg_write(reg.UC_X86_REG_ESP,sp+4);machine.reg_write(reg.UC_X86_REG_EIP,values[0]);return
        require(any(start<=address and address+size<=start+n for start,n in windows),f'Unexpected cleanup execution {address:x}')
        visited.add(address)
        if address==0x10001360:cleanse_calls[0]+=1
    uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
    counts={'cleanup':0,'command_72':0,'cleanse':0,'record_free':0,'release_wrapper':0};examples=[]
    def execute(entry,args,original,expected,expected_events,expected_cleanses,mode,result=None):
        uc.mem_write(base,bytes(original));uc.mem_write(0x10e1d024,struct.pack('<I',{'null':0,'self':0x104c5fc0,'custom':0x600000}[mode]))
        uc.mem_write(stack,struct.pack('<'+'I'*(len(args)+1),0x500000,*args));uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        for r,v in preserved.items():uc.reg_write(r,v)
        events.clear();writes.clear();done[0]=False;cleanse_calls[0]=0;uc.emu_start(entry,0,count=15000)
        require(done[0] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'Cleanup return/stack differs')
        if result is not None:require(uc.reg_read(reg.UC_X86_REG_EAX)==result,'Cleanup return value differs')
        require(events==expected_events,'Release ordering/arguments differ')
        require(cleanse_calls[0]==expected_cleanses,'Cleanser call count differs')
        require(bytes(uc.mem_read(base,8192))==bytes(expected),'Cleanup memory differs')
        require(all(uc.reg_read(r)==v for r,v in preserved.items()),'Cleanup ABI differs')
        require(all(stack-256<=p and p+n<=stack or base<=p and p+n<=base+8192 for p,n in writes),'Unexpected cleanup write')
    def event(pointer,file,line,mode):return dict(pointer=pointer,route='custom' if mode=='custom' else 'default',metadata=[file,line] if mode=='custom' else [])
    def check(mode,flags,record_flags,length,alignment,object_present,close,initialized,state_present,buffer_present,release_all,command=False):
        original=bytearray([0xa5])*8192;payload=data+alignment;active_length[0]=length;active_data[0]=payload
        struct.pack_into('<4I',original,0xc,int(initialized),int(close),flags,0)
        struct.pack_into('<I',original,0x20,state if state_present else 0)
        struct.pack_into('<II',original,0x100,record,work)
        struct.pack_into('<4I',original,0x200,19,payload if buffer_present else 0,length,record_flags)
        struct.pack_into('<4I',original,0x300,11,data+512,32,0)
        expected=bytearray(original);release=[];cleanses=0
        active=object_present and close and initialized and state_present
        if active:
            if flags&0x200:struct.pack_into('<I',expected,0x204,0)
            elif buffer_present:
                expected[0x1000+alignment:0x1000+alignment+length]=bytes(length)
                cleanses=int(length!=0 or record_flags&1!=0)
                release.append(event(payload,0x10b781e0,0x32 if record_flags&1 else 0x34,mode))
            release.append(event(record,0x10b781e0,0x36,mode))
            if release_all:
                release.extend([event(work,0x10b5fb1c,0x99,mode),event(state,0x10b5fb1c,0x9a,mode)])
            struct.pack_into('<I',expected,0x20,0)
        if command:
            struct.pack_into('<I',expected,0x10,7);struct.pack_into('<I',expected,0x100,replacement)
            expected[0x240:0x250]=expected[0x300:0x310];struct.pack_into('<I',expected,0x20,state)
            execute(0x104c2d30,[base,0x72,7,replacement],original,expected,release,cleanses,mode,1)
        else:execute(0x104c3040,[base if object_present else 0,int(release_all)],original,expected,release,cleanses,mode,int(object_present))
        counts['command_72' if command else 'cleanup']+=1
        if mode=='custom' and active and length==17 and alignment==0 and record_flags==0 and not command:
            examples.append(dict(flags=flags,release_all=release_all,events=list(events)))
    for mode,flags,record_flags,length,alignment,release_all in itertools.product(('null','self','custom'),(0,0x200,0x400,0x600),(0,1),(0,1,3,4,7,8,17,64,255),range(4),(False,True)):
        check(mode,flags,record_flags,length,alignment,True,True,True,True,True,release_all)
        if not release_all:check(mode,flags,record_flags,length,alignment,True,True,True,True,True,False,True)
    for mode,object_present,close,initialized,state_present,buffer_present,release_all in itertools.product(('null','self','custom'),*( (False,True),)*6):
        check(mode,0,0,17,0,object_present,close,initialized,state_present,buffer_present,release_all)
    # Direct cleanser spans all byte/dword paths, including zero length and all alignments.
    for length,alignment in itertools.product(range(257),range(4)):
        original=bytearray([0xa5])*8192;expected=bytearray(original);expected[0x1000+alignment:0x1000+alignment+length]=bytes(length)
        execute(0x10001360,[data+alignment,length],original,expected,[],1,'null',0);counts['cleanse']+=1
    # Null record and null public free wrapper have different behavior: guard vs dispatch.
    original=bytearray([0xa5])*8192
    for mode in ('null','self','custom'):
        execute(0x104f52d0,[0],original,original,[],0,mode);counts['record_free']+=1
        execute(0x104c5fc0,[0,0x10b781e0,9],original,original,[event(0,0x10b781e0,9,mode)],0,mode);counts['release_wrapper']+=1
    # Cover the null-pointer guards of both clean/free wrappers directly.
    for entry in (0x104c6090,0x10529120):execute(entry,[0,17,0,0],original,original,[],0,'custom');counts['release_wrapper']+=1
    for start,expected_instructions in instructions.items():require(expected_instructions<=visited,f'Uncovered cleanup instructions {start:x}: {expected_instructions-visited}')
    uc.mem_write(0x104c3065,b'\x04');uc.ctl_remove_cache(0x104c3040,0x104c30b7)
    negative=False
    try:check('custom',0x200,0,17,0,True,True,True,True,True,False)
    except (ValueError,AssertionError) as error:
        require(str(error)=='Release ordering/arguments differ','Unexpected mutation failure');negative=True
    require(negative,'Wrong ownership flag escaped detector')
    result=dict(source_sha256=module['sha256'],cases=counts,total_cases=sum(counts.values()),
                helper_instruction_counts={f'{start:x}':len(v) for start,v in instructions.items()},loaded_windows=evidence,
                examples=examples,negative_control_caught=negative,dependencies={'continuation-body-repair/manifest.json':digest(folder/'continuation-body-repair/manifest.json')},
                limitation='Cleanup, record release, cleanse and callback selection execute original instructions. Default allocator and custom callback boundaries only record frees and return; heap deallocation, callback implementation and allocation lifetime are not simulated or proven. Finite allocated buffers, independent nonaliasing records.')
    root=folder/'buffer-cleanup-behavior';root.mkdir(exist_ok=True)
    (root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k not in ('loaded_windows','examples')}))


if __name__=='__main__':main()
