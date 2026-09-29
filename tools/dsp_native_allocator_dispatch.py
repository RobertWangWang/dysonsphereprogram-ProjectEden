"""Verify allocator wrapper callback precedence, gate mutation and callback getter."""
import itertools
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require


def validate_cached(folder,sha):
    root=folder/'allocator-dispatch-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Allocator source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Allocator evidence differs')
    return read_json(root/'report.json')


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Game changed');game=path.read_bytes()
    initial=struct.unpack('<4I',pe_read(game,0x10e1d018,16));require(initial==(1,0x104c5f90,0x104c5fe0,0x104c5fc0),'Initial allocator slots changed')
    windows=[(0x104c5f90,47),(0x104c5fe0,164),(0x104c61d0,46)]
    uc=Uc(UC_ARCH_X86,UC_MODE_32);pages={0x10e1d000,0x1060d000,0x10603000,0x200000,0x300000,0x500000,0x600000}
    for start,n in windows:pages.update(range(start&~4095,(start+n+4095)&~4095,4096))
    for page in pages:uc.mem_map(page,4096)
    cs=Cs(CS_ARCH_X86,CS_MODE_32);bodies={};raw_windows=[]
    for start,n in windows:
        raw=pe_read(game,start,n);decoded=list(cs.disasm(raw,start));require(sum(i.size for i in decoded)==n,'Incomplete decode')
        bodies[start]={i.address for i in decoded};raw_windows.append(dict(address=f'{start:x}',bytes_hex=raw.hex()));uc.mem_write(start,raw)
    uc.mem_write(0x500000,b'\xf4');stack=0x200800;out=0x300000;returned=[False];visited=set();events=[];writes=[];boundary_result=[0]
    boundaries={0x1060d1b8:('malloc',1),0x1060dc5d:('realloc',2),0x10603677:('free',1),0x600000:('custom_malloc',3),0x600010:('custom_realloc',4),0x600020:('custom_free',3)}
    preserved={reg.UC_X86_REG_EBX:0xa5a5a5a5,reg.UC_X86_REG_EBP:0xb6b6b6b6,reg.UC_X86_REG_ESI:0xc7c7c7c7,reg.UC_X86_REG_EDI:0xd8d8d8d8}
    def hook(machine,address,size,unused):
        if address==0x500000:returned[0]=True;machine.emu_stop();return
        if address in boundaries:
            name,n=boundaries[address];sp=machine.reg_read(reg.UC_X86_REG_ESP);values=struct.unpack('<'+'I'*(n+1),machine.mem_read(sp,4*(n+1)));events.append([name,list(values[1:])])
            machine.reg_write(reg.UC_X86_REG_EAX,boundary_result[0]);machine.reg_write(reg.UC_X86_REG_ESP,sp+4);machine.reg_write(reg.UC_X86_REG_EIP,values[0]);return
        require(any(start<=address and address+size<=start+n for start,n in windows),'Unexpected execution');visited.add(address)
    uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
    def execute(entry,args,slots,gate,result):
        uc.mem_write(0x10e1d018,struct.pack('<4I',gate,*slots));uc.mem_write(stack,struct.pack('<'+'I'*(len(args)+1),0x500000,*args))
        uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        for r,v in preserved.items():uc.reg_write(r,v)
        returned[0]=False;events.clear();writes.clear();boundary_result[0]=result;uc.emu_start(entry,0,count=200)
        require(returned[0] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'Stack/return differs');require(all(uc.reg_read(r)==v for r,v in preserved.items()),'ABI differs')
    def slots_for(modes):return [0 if mode==0 else initial[i+1] if mode==1 else 0x600000+i*16 for i,mode in enumerate(modes)]
    counts={'malloc':0,'realloc':0,'getter':0}
    def check(modes,pointer,size,result,gate,reallocate):
        slots=slots_for(modes);metadata=[0xabcdef00,0x1234];expected_events=[];expected_gate=gate;expected_return=0
        if reallocate and modes[1]==2:expected_events=[['custom_realloc',[pointer,size,*metadata]]];expected_return=result
        elif not reallocate or pointer==0:
            if modes[0]==2:expected_events=[['custom_malloc',[size,*metadata]]];expected_return=result
            elif size:expected_events=[['malloc',[size]]];expected_gate=0;expected_return=result
        elif size:expected_events=[['realloc',[pointer,size]]];expected_gate=0;expected_return=result
        else:expected_events=[['custom_free',[pointer,*metadata]]] if modes[2]==2 else [['free',[pointer]]]
        execute(0x104c5fe0 if reallocate else 0x104c5f90,[pointer,size,*metadata] if reallocate else [size,*metadata],slots,gate,result)
        require(events==expected_events,'Callback precedence/arguments differ');require(uc.reg_read(reg.UC_X86_REG_EAX)==expected_return,'Return pointer differs')
        require(struct.unpack('<I',uc.mem_read(0x10e1d018,4))[0]==expected_gate,'Gate differs')
        require(bytes(uc.mem_read(0x10e1d01c,12))==struct.pack('<3I',*slots),'Callback slots changed')
        require(all(stack-64<=p and p+n<=stack or p==0x10e1d018 and n==4 for p,n in writes),'Unexpected allocator write')
        counts['realloc' if reallocate else 'malloc']+=1
    for mode,size,result,gate in itertools.product(range(3),(0,1,4096,0xffffffff),(0,0x300800),(0,1,0xdeadbeef)):check((mode,1,1),0,size,result,gate,False)
    for modes,pointer,size,result,gate in itertools.product(itertools.product(range(3),repeat=3),(0,0x300400),(0,1,4096,0xffffffff),(0,0x300800),(0,1,0xdeadbeef)):check(modes,pointer,size,result,gate,True)
    for modes,mask,alias in itertools.product(itertools.product(range(3),repeat=3),range(8),(False,True)):
        slots=slots_for(modes);expected=bytearray([0xa5])*12;args=[out+(0 if alias else i*4) if mask&(1<<i) else 0 for i in range(3)]
        for i,address in enumerate(args):
            if address:struct.pack_into('<I',expected,address-out,slots[i])
        uc.mem_write(out,bytes([0xa5])*12);execute(0x104c61d0,args,slots,1,0)
        require(not events and bytes(uc.mem_read(out,12))==bytes(expected),'Getter output differs')
        require(bytes(uc.mem_read(0x10e1d018,16))==struct.pack('<4I',1,*slots),'Getter mutated globals')
        require(all(out<=p and p+n<=out+12 for p,n in writes),'Getter unexpected write');counts['getter']+=1
    for start,pcs in bodies.items():require(pcs<=visited,f'Coverage incomplete {start:x}')
    uc.mem_write(0x104c5fb2,b'\x01');uc.ctl_remove_cache(0x104c5f90,0x104c5fbf);caught=False
    try:check((1,1,1),0,1,0x300800,1,False)
    except ValueError as error:require(str(error)=='Gate differs','Unexpected negative failure');caught=True
    require(caught,'Gate mutation escaped detector')
    report=dict(source_sha256=module['sha256'],cases=counts,total_cases=sum(counts.values()),initial_gate=initial[0],initial_callbacks=[f'{v:x}' for v in initial[1:]],instruction_counts={f'{k:x}':len(v) for k,v in bodies.items()},loaded_windows=raw_windows,negative_control_caught=caught,
                limitation='Original wrapper/getter instructions with modeled allocator and user callback boundaries. Proves finite dispatch and gate behavior, not actual allocations, callback implementation, runtime setter reachability or meaning of the gate outside these functions.')
    root=folder/'allocator-dispatch-behavior';root.mkdir(exist_ok=True);(root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k!='loaded_windows'}))


if __name__=='__main__':main()
