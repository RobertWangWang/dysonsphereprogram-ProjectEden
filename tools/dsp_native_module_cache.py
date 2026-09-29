"""Original module cache/load fallback with explicit API and interleaving models."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_heap_free import imports_at


def validate_cached(folder,sha):
    root=folder/'module-cache-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Module cache source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Module cache evidence differs')
    return read_json(root/'report.json')


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes()
    imports=imports_at(game,{0x10b2a198,0x10b2a394,0x10b2a380})
    require([imports[a]['name'] for a in (0x10b2a198,0x10b2a394,0x10b2a380)]==['LoadLibraryExW','GetLastError','FreeLibrary'],'Module imports differ')
    names={}
    for index in (0,8,18,19):
        pointer=struct.unpack('<I',pe_read(game,0x10bbd1c8+4*index,4))[0];data=bytearray()
        for offset in range(0,512,2):
            pair=pe_read(game,pointer+offset,2)
            if pair==b'\0\0':break
            data.extend(pair)
        else:raise ValueError('Unterminated module name')
        names[index]=dict(pointer=pointer,name=data.decode('utf-16le'))
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    for page in (0x10618000,0x10b2a000,0x10bbd000,0x10e82000,0x200000,0x500000,0x600000):uc.mem_map(page,4096)
    start=0x10618486;raw=pe_read(game,start,123);uc.mem_write(start,raw)
    cs=Cs(CS_ARCH_X86,CS_MODE_32);ins=list(cs.disasm(raw,start));require(sum(i.size for i in ins)==123,'Decode incomplete');all_ins={i.address for i in ins}
    uc.mem_write(0x10bbd1c8,pe_read(game,0x10bbd1c8,80))
    for slot,stub in ((0x10b2a198,0x600000),(0x10b2a394,0x600010),(0x10b2a380,0x600020)):
        uc.mem_write(slot,struct.pack('<I',stub));uc.mem_write(stub,b'\xc3')
    uc.mem_write(0x500000,b'\xf4');stack=0x200800;cache=0x10e820d8
    state,events,writes,visited={},[],[],set()
    saved={reg.UC_X86_REG_EBP:0x12345678,reg.UC_X86_REG_EBX:0x23456789,reg.UC_X86_REG_ESI:0x34567890,reg.UC_X86_REG_EDI:0x45678901}
    def hook(machine,address,n,unused):
        if address==0x500000:state['done']=True;machine.emu_stop();return
        if address in (0x600000,0x600010,0x600020):
            sp=machine.reg_read(reg.UC_X86_REG_ESP);ret=struct.unpack('<I',machine.mem_read(sp,4))[0]
            if address==0x600000:
                args=list(struct.unpack('<III',machine.mem_read(sp+4,12)));events.append(['load',args]);pop=12
                require(state['attempt']<2,'Extra load attempt');value=state['results'][state['attempt']]
                if state['attempt']==0 and state['interleave'] is not None:
                    machine.mem_write(state['slot'],struct.pack('<I',state['interleave']))
                state['attempt']+=1
            elif address==0x600010:events.append(['error']);value=state['error'];pop=0
            else:
                arg=struct.unpack('<I',machine.mem_read(sp+4,4))[0];events.append(['free',arg]);value=state['free_result'];pop=4
            for r,v in ((reg.UC_X86_REG_EAX,value),(reg.UC_X86_REG_ECX,0xaabbccdd),(reg.UC_X86_REG_EDX,0x87654321)):machine.reg_write(r,v)
            machine.reg_write(reg.UC_X86_REG_ESP,sp+4+pop);machine.reg_write(reg.UC_X86_REG_EIP,ret);return
        require(address in all_ins,'Unexpected module instruction');visited.add(address)
    uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
    cases=0
    def check(index,old,first,second,error,interleave,free_result):
        nonlocal cases
        slot=cache+index*4;name=names[index]['pointer'];memory=bytearray([0xa5])*80;struct.pack_into('<I',memory,index*4,old);uc.mem_write(cache,bytes(memory))
        expected=[];result=0 if old==0xffffffff else old;new=old
        if old==0:
            expected.append(['load',[name,0,0x800]]);result=first
            if not first:
                expected.append(['error'])
                if error==87:expected.append(['load',[name,0,0]]);result=second
            new=result if result else 0xffffffff
            if result and interleave:expected.append(['free',result])
        struct.pack_into('<I',memory,index*4,new)
        for repeat in (False,True):
            state.update(done=False,attempt=0,results=[first,second],error=error,interleave=interleave,slot=slot,free_result=free_result)
            events.clear();writes.clear();uc.mem_write(stack,struct.pack('<II',0x500000,index));uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
            for r,v in saved.items():uc.reg_write(r,v)
            uc.emu_start(start,0,count=150)
            require(state['done'] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'Module return stack differs')
            require(uc.reg_read(reg.UC_X86_REG_EAX)==result,'Module result differs')
            require(events==([] if repeat else expected),'Module API sequence differs')
            require(bytes(uc.mem_read(cache,80))==bytes(memory),'Module cache differs')
            require(all(uc.reg_read(r)==v for r,v in saved.items()),'Module preserved registers differ')
            require([(p,n) for p,n in writes if p==slot]==([(slot,4)] if old==0 and not repeat else []),'Module publication count differs')
            require(all(n==4 and (stack-64<=p and p+4<=stack or p==slot) for p,n in writes),'Unexpected module write');cases+=1
    for index in names:
        for old in (0,0xffffffff,0x710000,0x80000000):
            for first in (0,0x720000,0x80000000):
                for second in (0,0x730000,0x80000000):
                    for error in (0,5,87,126,127,0x80000057,0xffffffff):
                        for interleave in (None,0,0x740000,0xffffffff):
                            for free_result in (0,1,0xffffffff):check(index,old,first,second,error,interleave,free_result)
    require(visited==all_ins,'Module instruction coverage incomplete')
    require(pe_read(game,0x106184cc,1)==b'\x57','Mutation location differs')
    uc.mem_write(0x106184cc,b'\x56');uc.ctl_remove_cache(start,start+123);caught=False
    try:check(18,0,0,0x730000,87,None,1)
    except ValueError as error:require(str(error)=='Module result differs','Unexpected mutation failure');caught=True
    require(caught,'Wrong fallback error undetected')
    report=dict(source_sha256=module['sha256'],addresses=['10618486'],bytes_hex=raw.hex(),cases=cases,instructions=len(all_ins),negative_control_caught=caught,
                module_names={str(k):v for k,v in names.items()},imports={f'{a:x}':v for a,v in imports.items()},
                semantics='Unencoded cache zero is unresolved, FFFFFFFF is failure returning zero. LoadLibraryExW(name,0,0x800); only error 87 retries flags zero. Success/failure publishes with XCHG. Nonzero old value returned by success exchange causes FreeLibrary(new handle), still returning that new handle. Repeats use cache.',
                limitation='OS APIs are returning models. Controlled single-step cache writes during API model test observed exchange/free behavior, not true concurrent correctness or real module reference counts. Only four verified IDs and finite values; no actual loading, unload or OS exceptions.')
    root=folder/'module-cache-behavior';root.mkdir(exist_ok=True)
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps(report))


if __name__=='__main__':main()
