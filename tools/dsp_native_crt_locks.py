"""Original CRT lock address mapping and init/rollback lifecycle with API models."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_heap_free import imports_at


def validate_cached(folder,sha):
    root=folder/'crt-lock-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Lock source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Lock evidence differs')
    return read_json(root/'report.json')


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes()
    slots={0x10b2a420:'EnterCriticalSection',0x10b2a3f8:'LeaveCriticalSection',0x10b2a424:'DeleteCriticalSection'}
    imports=imports_at(game,set(slots));require(all(imports[a]['name']==name for a,name in slots.items()),'Lock imports differ')
    windows=[(0x106132d5,65),(0x10613316,23),(0x1061332d,49),(0x1061335e,23)]
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    for page in (0x10613000,0x10618000,0x10b2a000,0x10e81000,0x200000,0x500000,0x600000):uc.mem_map(page,4096)
    cs=Cs(CS_ARCH_X86,CS_MODE_32);all_ins=set();evidence=[]
    for a,n in windows:
        raw=pe_read(game,a,n);ins=list(cs.disasm(raw,a));require(sum(i.size for i in ins)==n,'Decode incomplete')
        uc.mem_write(a,raw);all_ins.update(i.address for i in ins);evidence.append(dict(address=f'{a:x}',bytes_hex=raw.hex(),instructions=len(ins)))
    api={0x600000:'enter',0x600010:'leave',0x600020:'delete',0x10618a07:'init'}
    for slot,target in ((0x10b2a420,0x600000),(0x10b2a3f8,0x600010),(0x10b2a424,0x600020)):uc.mem_write(slot,struct.pack('<I',target))
    for a in api:uc.mem_write(a,b'\xc3')
    uc.mem_write(0x500000,b'\xf4');stack=0x200800;table=0x10e81e60;count_address=0x10e81f98
    state,events,writes,visited={},[],[],set()
    saved={reg.UC_X86_REG_EBP:0x12345678,reg.UC_X86_REG_EBX:0x23456789,reg.UC_X86_REG_ESI:0x34567890,reg.UC_X86_REG_EDI:0x45678901}
    def hook(machine,address,n,unused):
        if address==0x500000:state['done']=True;machine.emu_stop();return
        if address in api:
            sp=machine.reg_read(reg.UC_X86_REG_ESP);ret=struct.unpack('<I',machine.mem_read(sp,4))[0];name=api[address];argc=3 if name=='init' else 1
            args=list(struct.unpack('<'+'I'*argc,machine.mem_read(sp+4,argc*4)));events.append([name,args])
            value=state['clobber']
            if name=='init':value=0 if state['attempt']==state['fail'] else state['success'];state['attempt']+=1
            for r,v in ((reg.UC_X86_REG_EAX,value),(reg.UC_X86_REG_ECX,0xaabbccdd),(reg.UC_X86_REG_EDX,0x12345678)):machine.reg_write(r,v)
            machine.reg_write(reg.UC_X86_REG_ESP,sp+4+argc*4);machine.reg_write(reg.UC_X86_REG_EIP,ret);return
        require(address in all_ins,'Unexpected lock instruction');visited.add(address)
    uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n,v&0xffffffff)))
    def execute(entry,arg=0,initial_count=0,fail=13,success=1,clobber=0):
        state.update(done=False,fail=fail,success=success,clobber=clobber,attempt=0);events.clear();writes.clear()
        uc.mem_write(0x10e81e40,bytes([0xa5])*384);uc.mem_write(count_address,struct.pack('<I',initial_count))
        uc.mem_write(stack-128,bytes([0xa5])*160);uc.mem_write(stack,struct.pack('<II',0x500000,arg))
        uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EAX,0x12345678);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        for r,v in saved.items():uc.reg_write(r,v)
        uc.emu_start(entry,0,count=1000)
        require(state['done'] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'Lock stack differs')
        require(all(uc.reg_read(r)==v for r,v in saved.items()),'Lock preserved registers differ')
        expected=bytearray([0xa5])*384;actual_count=struct.unpack('<I',uc.mem_read(count_address,4))[0];struct.pack_into('<I',expected,count_address-0x10e81e40,actual_count)
        require(bytes(uc.mem_read(0x10e81e40,384))==bytes(expected),'Lock global overwrite')
        require(all(n==4 and (stack-64<=p and p+4<=stack or p==count_address) for p,n,v in writes),'Unexpected lock write')
        return actual_count,uc.reg_read(reg.UC_X86_REG_EAX)&255
    wrapper_cases=0
    for entry,name in ((0x10613316,'enter'),(0x1061335e,'leave')):
        for index in list(range(65536))+[0x7fffffff,0x80000000,0xfffffffe,0xffffffff]:
            count,_=execute(entry,index,initial_count=13,clobber=0x87654321)
            require(count==13 and events==[[name,[(table+index*24)&0xffffffff]]],'Lock address differs');wrapper_cases+=1
    lifecycle_cases=0
    for fail in range(14):
        for success in (1,2,0x80000000,0xffffffff):
            for clobber in (0,1,0x87654321):
                count,result=execute(0x106132d5,fail=fail,success=success,clobber=clobber)
                expected=[['init',[table+j*24,4000,0]] for j in range(min(fail+1,13))]
                if fail<13:expected.extend(['delete',[table+j*24]] for j in range(fail-1,-1,-1))
                require(events==expected and count==(13 if fail==13 else 0) and result==int(fail==13),'Initialization/rollback differs')
                changes=[v for p,n,v in writes if p==count_address]
                require(changes==(list(range(1,min(fail,13)+1))+(list(range(fail-1,-1,-1)) if fail<13 else [])),'Count progression differs')
                lifecycle_cases+=1
    cleanup_cases=0
    for count in range(14):
        for clobber in (0,1,0x87654321):
            final,result=execute(0x1061332d,initial_count=count,clobber=clobber)
            require(final==0 and result==1 and events==[['delete',[table+j*24]] for j in range(count-1,-1,-1)],'Cleanup sequence differs');cleanup_cases+=1
    require(visited==all_ins,'Lock instruction coverage incomplete')
    require(pe_read(game,0x1061331e,1)==b'\x18','Mutation location differs')
    uc.mem_write(0x1061331e,b'\x10');uc.ctl_remove_cache(0x10613316,0x1061332d)
    execute(0x10613316,3,initial_count=13);caught=events!=[['enter',[table+3*24]]];require(caught,'Wrong stride undetected')
    report=dict(source_sha256=module['sha256'],addresses=['106132d5','10613316','1061332d','1061335e'],ranges=evidence,
                wrapper_cases=wrapper_cases,lifecycle_cases=lifecycle_cases,cleanup_cases=cleanup_cases,instructions=len(all_ins),negative_control_caught=caught,imports={f'{a:x}':v for a,v in imports.items()},
                semantics='13 critical-section slots, stride 24 at 10e81e60. Init(spin=4000,flags=0), increment successful count; first failure deletes prior successful slots in reverse and count reaches zero. Cleanup deletes count slots in reverse. Init/cleanup booleans are AL, not full EAX. Enter/leave compute address without local index bounds check.',
                limitation='Initialization helper and Windows critical-section APIs are returning models, no actual OS objects or concurrency. Init begins with count zero; cleanup uses counts 0..13. Large wrapper indices test arithmetic only, not valid public inputs or actual dereferences.')
    root=folder/'crt-lock-behavior';root.mkdir(exist_ok=True)
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps(report))


if __name__=='__main__':main()
