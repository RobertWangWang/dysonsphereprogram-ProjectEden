"""Run original buffer synchronization, original memory move, and command 0x73 together."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require
from dsp_native_continuation_bodies import validate_cached as validate_bodies


def validate_cached(folder,sha):
    root=folder/'buffer-sync-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Sync source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Sync evidence differs')
    result=read_json(root/'report.json')
    for name,value in result['dependencies'].items():require(digest(folder/name)==value,'Sync dependency differs')
    return result


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path']
    require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes();require(validate_bodies(folder,module['sha256']),'Bodies not verified')
    root=folder/'buffer-sync-behavior';root.mkdir(exist_ok=True)
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    for base,size in [(0x104c2000,0x2000),(0x105f9000,0x3000),(0x10e24000,0x1000),(0x10e7c000,0x1000),(0x200000,0x1000),(0x300000,0x4000),(0x500000,0x1000)]:uc.mem_map(base,size)
    windows=[(0x104c2d30,486),(0x104c30c0,73),(0x105f9bf0,0x574),(0x105fb540,346)]
    evidence=[]
    for start,size in windows:
        raw=pe_read(game,start,size);uc.mem_write(start,raw);evidence.append(dict(address=f'{start:x}',size=size,bytes_hex=raw.hex()))
    uc.mem_write(0x500000,b'\xf4')
    cs=Cs(CS_ARCH_X86,CS_MODE_32);helper=list(cs.disasm(pe_read(game,0x104c30c0,73),0x104c30c0))
    require(sum(i.size for i in helper)==73,'Helper decode coverage differs')
    (root/'104c30c0.asm').write_text(''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in helper),encoding='utf-8')
    stack=0x200800;base=0x300000;state=base+0x100;source_record=base+0x200;working_record=base+0x240;argument=base+0x300
    preserved={reg.UC_X86_REG_EBX:0xa1a2a3a4,reg.UC_X86_REG_EBP:0xb1b2b3b4,reg.UC_X86_REG_ESI:0xc1c2c3c4,reg.UC_X86_REG_EDI:0xd1d2d3d4}
    visited=set();writes=[];done=[False];move_calls=[0];sync_calls=[0]
    def code(machine,address,size,unused):
        if address==0x500000:done[0]=True;machine.emu_stop();return
        require(any(start<=address and address+size<=start+n for start,n in windows),f'Unexpected instruction outside original windows: {address:x} size {size}')
        visited.add(address)
        if address==0x105f9bf0:move_calls[0]+=1
        if address==0x104c30c0:sync_calls[0]+=1
    uc.hook_add(UC_HOOK_CODE,code);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
    counts={'direct':0,'command':0,'clear_command':0}
    def check(length,source,destination,features,initialized=True,state_present=True,object_present=True,command=False,arg_present=True):
        original=bytearray((i*37+11)&255 for i in range(0x4000))
        struct.pack_into('<I',original,0xc,int(initialized));struct.pack_into('<I',original,0x20,state if state_present else 0)
        struct.pack_into('<II',original,0x100,source_record,working_record)
        struct.pack_into('<4I',original,0x200,0xabcdef01,base+destination,1024,0)
        struct.pack_into('<4I',original,0x240,length,base+source,1024,0)
        expected=bytearray(original);active=object_present and initialized and state_present and (not command or arg_present)
        should_move=active and source!=destination
        if should_move:
            expected[destination:destination+length]=original[source:source+length]
            struct.pack_into('<I',expected,0x200,length);struct.pack_into('<I',expected,0x244,base+destination)
        if command and arg_present:struct.pack_into('<I',expected,0x300,working_record)
        uc.mem_write(base,bytes(original));uc.mem_write(0x10e24f50,struct.pack('<I',features[0]));uc.mem_write(0x10e7c0b0,struct.pack('<I',features[1]))
        args=[base if object_present else 0]
        if command:args.extend([0x73,0,argument if arg_present else 0])
        uc.mem_write(stack,struct.pack('<'+'I'*(len(args)+1),0x500000,*args));uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        for r,v in preserved.items():uc.reg_write(r,v)
        writes.clear();done[0]=False;move_calls[0]=0;sync_calls[0]=0
        uc.emu_start(0x104c2d30 if command else 0x104c30c0,0,count=30000)
        require(done[0] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'Sync return/stack differs')
        require(uc.reg_read(reg.UC_X86_REG_EAX)==(1 if command else 0),'Sync return value differs')
        require(move_calls[0]==int(should_move) and sync_calls[0]==(int(arg_present) if command else 1),'Sync call count differs')
        require(bytes(uc.mem_read(base,0x4000))==bytes(expected),'Sync memory differs')
        require(all(uc.reg_read(r)==v for r,v in preserved.items()),'Sync preserved register differs')
        require(all(stack-128<=p and p+n<=stack or base<=p and p+n<=base+0x4000 for p,n in writes),'Sync unexpected write')
        counts['command' if command else 'direct']+=1
    lengths=list(range(65))+[95,96,97,127,128,129,255,256,257,511,512,513,1023]
    for features in ((0,0),(2,0),(0,1),(0,2),(2,3)):
        for length in lengths:
            for alignment in range(4):
                source=0x1000+alignment
                for delta in (-64,-3,-1,0,1,3,64,2048):
                    for command in (False,True):check(length,source,source+delta,features,command=command)
    for initialized in (False,True):
        for state_present in (False,True):
            for object_present in (False,True):check(17,0x1000,0x1800,(0,0),initialized,state_present,object_present)
    for initialized in (False,True):
        for arg_present in (False,True):check(17,0x1000,0x1800,(0,0),initialized=initialized,command=True,arg_present=arg_present)
    def check_clear(length,alignment,features,flags,buffer_present=True):
        original=bytearray((i*37+11)&255 for i in range(0x4000));destination=0x1000+alignment
        struct.pack_into('<I',original,0x14,flags);struct.pack_into('<I',original,0x20,state)
        struct.pack_into('<II',original,0x100,source_record,working_record)
        struct.pack_into('<4I',original,0x200,31,base+destination if buffer_present else 0,length,0)
        expected=bytearray(original)
        if buffer_present:
            if flags&0x600:struct.pack_into('<I',expected,0x200,length)
            else:
                expected[destination:destination+length]=bytes(length);struct.pack_into('<I',expected,0x200,0)
            expected[0x240:0x250]=expected[0x200:0x210]
        uc.mem_write(base,bytes(original));uc.mem_write(0x10e24f50,struct.pack('<I',features[0]));uc.mem_write(0x10e7c0b0,struct.pack('<I',features[1]))
        uc.mem_write(stack,struct.pack('<5I',0x500000,base,1,0,0));uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        for r,v in preserved.items():uc.reg_write(r,v)
        writes.clear();done[0]=False;uc.emu_start(0x104c2d30,0,count=30000)
        require(done[0] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4 and uc.reg_read(reg.UC_X86_REG_EAX)==1,'Clear return/stack differs')
        require(bytes(uc.mem_read(base,0x4000))==bytes(expected),'Clear memory differs')
        require(all(uc.reg_read(r)==v for r,v in preserved.items()),'Clear preserved register differs')
        require(all(stack-128<=p and p+n<=stack or base<=p and p+n<=base+0x4000 for p,n in writes),'Clear unexpected write')
        counts['clear_command']+=1
    for features in ((0,0),(2,0),(0,1),(0,2),(2,3)):
        for length in lengths:
            for alignment in range(16):
                for flags in (0,0x200,0x400,0x800):check_clear(length,alignment,features,flags)
    check_clear(17,0,(0,0),0,False)
    require({i.address for i in helper}<=visited,'Helper instruction coverage incomplete')
    uc.mem_write(0x104c3104,b'\x08');uc.ctl_remove_cache(0x104c30c0,0x104c3109)
    negative=False
    try:check(17,0x1000,0x1800,(0,0),command=True)
    except (ValueError,AssertionError) as error:
        require(str(error)=='Sync memory differs','Unexpected negative failure');negative=True
    require(negative,'Wrong synchronized pointer field escaped detector')
    result=dict(source_sha256=module['sha256'],cases=counts,total_cases=sum(counts.values()),helper_instructions=len(helper),
                memory_move_visited_instructions=len([pc for pc in visited if 0x105f9bf0<=pc<0x105f9bf0+0x574]),
                memset_visited_instructions=len([pc for pc in visited if 0x105fb540<=pc<0x105fb69a]),
                instruction_addresses=[f'{pc:x}' for pc in sorted(visited)],feature_pairs=[[0,0],[2,0],[0,1],[0,2],[2,3]],negative_control_caught=negative,
                loaded_windows=evidence,dependencies={'continuation-body-repair/manifest.json':digest(folder/'continuation-body-repair/manifest.json')},
                semantics='If object, initialized flag and state exist and record data pointers differ, move working length bytes to source buffer, copy working length to source record and reset working data pointer. Always return zero. Command 0x73 then writes the working record pointer and returns one. Command 1 uses original memset, or sets record length to capacity when flags 0x600 are set, then copies the 16-byte record.',
                limitation='Original helper, memory-move and memset instructions, no external stubs. Finite allocated nonwrapping buffers; tests exercise selected CPU feature configurations, not all memory-move instructions or real runtime CPU selection. Command 0x72 cleanup and allocation lifetime remain separate.')
    (root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={n:digest(root/n) for n in ('report.json','104c30c0.asm')}),indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k not in ('loaded_windows','instruction_addresses')}))


if __name__=='__main__':main()
