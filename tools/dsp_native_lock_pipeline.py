"""Original lock initialization through compatibility, function and module caches."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_heap_free import imports_at
from dsp_native_crt_locks import validate_cached as validate_locks
from dsp_native_critical_section_compat import validate_cached as validate_compat
from dsp_native_function_cache import validate_cached as validate_functions
from dsp_native_module_cache import validate_cached as validate_modules


def validate_cached(folder,sha):
    root=folder/'lock-pipeline-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Lock pipeline source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Lock pipeline evidence differs')
    report=read_json(root/'report.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Lock pipeline dependency differs')
    return report


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes()
    for validate in (validate_locks,validate_compat,validate_functions,validate_modules):require(validate(folder,module['sha256']),'Pipeline prerequisite missing')
    slots={0x10b2a198:('load',0x600000,'LoadLibraryExW'),0x10b2a394:('error',0x600010,'GetLastError'),
           0x10b2a380:('free',0x600020,'FreeLibrary'),0x10b2a384:('proc',0x600030,'GetProcAddress'),
           0x10b2a428:('fallback',0x600040,'InitializeCriticalSectionAndSpinCount'),0x10b2a424:('delete',0x600050,'DeleteCriticalSection')}
    imports=imports_at(game,set(slots));require(all(imports[a]['name']==v[2] for a,v in slots.items()),'Pipeline imports differ')
    windows=[(0x106132d5,65),(0x1061332d,49),(0x10618a07,98),(0x106183ea,156),
             (0x10618486,123),(0x105fa164,29),(0x105d29e6,17),(0x100569a0,1)]
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    for page in (0x10613000,0x10618000,0x105fa000,0x105d2000,0x10056000,0x10b2a000,0x10bb6000,0x10bba000,0x10bbd000,0x10e24000,0x10e81000,0x10e82000,0x200000,0x500000,0x600000):uc.mem_map(page,4096)
    cs=Cs(CS_ARCH_X86,CS_MODE_32);all_ins=set();evidence=[]
    for a,n in windows:
        raw=pe_read(game,a,n);ins=list(cs.disasm(raw,a));require(sum(i.size for i in ins)==n,'Decode incomplete')
        uc.mem_write(a,raw);all_ins.update(i.address for i in ins);evidence.append(dict(address=f'{a:x}',bytes_hex=raw.hex(),instructions=len(ins)))
    for a,n in ((0x10bbd1c8,80),(0x10bbd730,8),(0x10bb605c,28),(0x10bbab14,96)):uc.mem_write(a,pe_read(game,a,n))
    names={index:struct.unpack('<I',pe_read(game,0x10bbd1c8+index*4,4))[0] for index in (8,18)}
    require(struct.unpack('<I',pe_read(game,0x10b2a6e8,4))[0]==0x100569a0,'Guard slot differs');uc.mem_write(0x10b2a6e8,struct.pack('<I',0x100569a0))
    api={v[1]:v[0] for v in slots.values()};api[0x600080]='modern'
    for a,v in slots.items():uc.mem_write(a,struct.pack('<I',v[1]))
    for a in api:uc.mem_write(a,b'\xc3')
    uc.mem_write(0x500000,b'\xf4');stack=0x200800;table=0x10e81e60;count_address=0x10e81f98;cache=0x10e820d8
    state,events,writes,visited={},[],[],set()
    saved={reg.UC_X86_REG_EBP:0x12345678,reg.UC_X86_REG_EBX:0x23456789,reg.UC_X86_REG_ESI:0x34567890,reg.UC_X86_REG_EDI:0x45678901}
    def hook(machine,address,n,unused):
        if address==0x500000:state['done']=True;machine.emu_stop();return
        if address in api:
            name=api[address];argc={'load':3,'error':0,'free':1,'proc':2,'fallback':2,'delete':1,'modern':3}[name]
            sp=machine.reg_read(reg.UC_X86_REG_ESP);ret=struct.unpack('<I',machine.mem_read(sp,4))[0]
            args=list(struct.unpack('<'+'I'*argc,machine.mem_read(sp+4,argc*4))) if argc else []
            events.append([name,args]);value=0x87654321
            if name=='load':
                index=next((i for i,p in names.items() if p==args[0]),None);require(index is not None and args[1]==0 and args[2] in (0,0x800),'Unexpected module arguments')
                first,second,error=state['modules'][index];value=first if args[2]==0x800 else second;state['last_error']=error
            elif name=='error':value=state['last_error']
            elif name=='proc':value=0x600080 if state['available'] else 0
            elif name in ('modern','fallback'):
                value=0 if state['init_index']==state['fail'] else state['success'];state['init_index']+=1
            elif name=='free':raise ValueError('Unexpected FreeLibrary in sequential cold start')
            for r,v in ((reg.UC_X86_REG_EAX,value),(reg.UC_X86_REG_ECX,0xaabbccdd),(reg.UC_X86_REG_EDX,0x76543210)):machine.reg_write(r,v)
            machine.reg_write(reg.UC_X86_REG_ESP,sp+4+argc*4);machine.reg_write(reg.UC_X86_REG_EIP,ret);return
        require(address in all_ins,'Pipeline escaped');visited.add(address)
    uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
    def encode(value,cookie):
        n=cookie&31
        return (((value<<n)|(value>>(32-n if n else 32)))&0xffffffff)^cookie
    def execute(entry):
        state['done']=False;writes.clear();uc.mem_write(stack-256,bytes([0xa5])*288);uc.mem_write(stack,struct.pack('<I',0x500000));uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        for r,v in saved.items():uc.reg_write(r,v)
        uc.emu_start(entry,0,count=10000)
        require(state['done'] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'Pipeline stack differs')
        require(all(uc.reg_read(r)==v for r,v in saved.items()),'Pipeline saved registers differ')
        require(bytes(uc.mem_read(stack-256,64))==bytes([0xa5])*64 and bytes(uc.mem_read(stack+4,28))==bytes([0xa5])*28,'Stack guards differ')
        allowed=(count_address,cache+8*4,cache+18*4,cache+80+20*4)
        require(all(n==4 and (stack-192<=p and p+4<=stack or p in allowed) for p,n in writes),'Unexpected pipeline write')
        return uc.reg_read(reg.UC_X86_REG_EAX)&255
    h8,h18=0x710000,0x720000
    scenarios=[({8:(h8,0,0),18:(h18,0,0)},True),({8:(h8,0,0),18:(h18,0,0)},False),
               ({8:(0,0,126),18:(h18,0,0)},True),({8:(0,h8,87),18:(h18,0,0)},True),
               ({8:(0,0,126),18:(0,0,126)},True),({8:(0,0,87),18:(0,0,87)},True),
               ({8:(0,0,126),18:(0,h18,87)},True),({8:(0,0,126),18:(h18,0,0)},False)]
    cases=0
    def check(modules,available,fail,success,cookie):
        nonlocal cases
        state.update(modules=modules,available=available,fail=fail,success=success,init_index=0,last_error=0)
        uc.mem_write(0x10e24f44,struct.pack('<I',cookie));uc.mem_write(count_address,bytes(4))
        initial=bytes(80)+b''.join(struct.pack('<I',cookie) for _ in range(64));uc.mem_write(cache,initial);expected_cache=bytearray(initial)
        prefix=[];handle=0
        for index in (8,18):
            first,second,error=modules[index];prefix.append(['load',[names[index],0,0x800]]);handle=first
            if not first:
                prefix.append(['error',[]])
                if error==87:prefix.append(['load',[names[index],0,0]]);handle=second
            struct.pack_into('<I',expected_cache,index*4,handle if handle else 0xffffffff)
            if handle:break
        if handle:prefix.append(['proc',[handle,0x10bb605c]])
        modern=bool(handle and available);struct.pack_into('<I',expected_cache,80+20*4,encode(0x600080 if modern else 0xffffffff,cookie))
        for warm in (False,True):
            events.clear();state['init_index']=0
            result=execute(0x106132d5);expected=[] if warm else list(prefix)
            expected.extend(['modern' if modern else 'fallback',[table+j*24,4000]+([0] if modern else [])] for j in range(min(fail+1,13)))
            if fail<13:expected.extend(['delete',[table+j*24]] for j in range(fail-1,-1,-1))
            require(events==expected,'Pipeline API sequence differs')
            require(result==int(fail==13),'Pipeline initialization result differs')
            require(struct.unpack('<I',uc.mem_read(count_address,4))[0]==(13 if fail==13 else 0),'Initialized count differs')
            require(bytes(uc.mem_read(cache,len(initial)))==bytes(expected_cache),'Pipeline caches differ')
            if fail==13:
                events.clear();require(execute(0x1061332d)==1,'Explicit cleanup result differs')
                require(events==[['delete',[table+j*24]] for j in range(12,-1,-1)],'Explicit cleanup order differs')
            require(struct.unpack('<I',uc.mem_read(count_address,4))[0]==0,'Cleanup count differs');cases+=1
    for modules,available in scenarios:
        for fail in range(14):
            for success in (1,0x80000000,0xffffffff):
                for cookie in (0,0x12345661,0xffffffff):check(modules,available,fail,success,cookie)
    positive_visited=set(visited);positive_cases=cases
    require(pe_read(game,0x106184cc,1)==b'\x57','Mutation site differs')
    uc.mem_write(0x106184cc,b'\x56');uc.ctl_remove_cache(0x10618486,0x10618501);caught=False
    try:check(scenarios[3][0],True,13,1,0x12345661)
    except ValueError as error:require(str(error)=='Pipeline API sequence differs','Unexpected pipeline mutation failure');caught=True
    require(caught,'Fallback mutation escaped pipeline')
    dependency_names=['crt-lock-behavior','critical-section-compat-behavior','function-cache-behavior','module-cache-behavior']
    report=dict(source_sha256=module['sha256'],addresses=['106132d5','10618a07','106183ea','10618486'],ranges=evidence,cases=positive_cases,
                instructions=len(all_ins),instructions_visited=len(positive_visited),unvisited=[f'{a:x}' for a in sorted(all_ins-positive_visited)],negative_control_caught=caught,
                dependencies={name+'/manifest.json':digest(folder/name/'manifest.json') for name in dependency_names},
                semantics='Original init, compatibility selection, function/module caches, encoding, guard and cookie check execute together. Cold resolution occurs once; later slots and a second init cycle use caches. First failing lock initialization rolls back prior slots; successful cycle explicitly cleans all 13 slots.',
                limitation='Only Windows API boundaries modeled; no real OS modules or critical sections, runtime guard replacement, exception reporting or concurrency. Sequential cold/warm scenarios do not execute duplicate-load FreeLibrary or corrupt-cookie paths; unvisited instructions enumerated.')
    root=folder/'lock-pipeline-behavior';root.mkdir(exist_ok=True)
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k!='ranges'}))


if __name__=='__main__':main()
