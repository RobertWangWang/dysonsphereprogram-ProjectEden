"""Validate the two original mono_method_to_ir switch dispatches, not case semantics."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_select import selected_folder


def validate_cached(folder,sha):
    root=folder/'mono-dispatch-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Mono dispatch source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Mono dispatch evidence differs')
    report=read_json(root/'report.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Mono dispatch dependency differs')
    return report


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as r
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='mono-2.0-bdwgc.dll')
    folder=selected_folder(GENERATED/'native',module);path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Mono source changed');game=path.read_bytes()
    previous=read_json(folder/'switch-repair/report.json');require(previous['source_sha256']==module['sha256'] and previous['program_changes_rolled_back'],'Missing switch evidence')
    configs=[dict(name='main',entry=0x1802bc969,size=42,table=0x180312694,count=328,offset=0xa08,bias=0,default=0x18031131c,key='table_targets'),
             dict(name='nested',entry=0x1802ebaaf,size=54,table=0x180312bb4,count=28,offset=0x8c0,bias=2,default=0x1802ecf91,key='nested_table_targets')]
    cs=Cs(CS_ARCH_X86,CS_MODE_64);results=[]
    for cfg in configs:
        entry=cfg['entry'];raw=pe_read(game,entry,cfg['size']);ins=list(cs.disasm(raw,entry))
        require(sum(i.size for i in ins)==len(raw) and ins[-1].mnemonic=='jmp' and ins[-1].op_str=='rax','Dispatch decode differs')
        expected_first='cmp' if cfg['bias']==0 else 'mov';require(ins[0].mnemonic==expected_first,'Dispatch start differs')
        table=pe_read(game,cfg['table'],cfg['count']*4);targets=[0x180000000+x[0] for x in struct.iter_unpack('<I',table)]
        require([f'{a:x}' for a in targets]==previous[cfg['key']],'Table targets differ')
        uc=Uc(UC_ARCH_X86,UC_MODE_64);pages={entry&~4095,0x200000,0x300000}
        pages.update(range(cfg['table']&~4095,(cfg['table']+len(table)+4095)&~4095,4096))
        endpoints=set(targets)|{cfg['default']};pages.update(a&~4095 for a in endpoints)
        for page in pages:uc.mem_map(page,4096)
        uc.mem_write(entry,raw);uc.mem_write(cfg['table'],table)
        for a in endpoints:uc.mem_write(a,b'\xf4')
        starts={i.address for i in ins};visited=set();state={};writes=[];frame=0x200100
        def hook(machine,a,n,unused):
            if a in endpoints:state['target']=a;machine.emu_stop();return
            require(a in starts,'Dispatch escaped');visited.add(a)
        uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
        def check(value):
            state.clear();writes.clear();uc.mem_write(frame+cfg['offset'],struct.pack('<I',value));uc.reg_write(r.UC_X86_REG_RBP,frame);uc.reg_write(r.UC_X86_REG_RSP,0x300800)
            uc.reg_write(r.UC_X86_REG_RAX,0x123456789abcdef0);uc.reg_write(r.UC_X86_REG_RCX,0xfedcba9876543210);uc.reg_write(r.UC_X86_REG_EFLAGS,2)
            uc.emu_start(entry,0,count=20);index=(value-cfg['bias'])&0xffffffff;target=targets[index] if index<cfg['count'] else cfg['default']
            require(state.get('target')==target,'Dispatch target differs')
            require(struct.unpack('<I',uc.mem_read(frame+cfg['offset'],4))[0]==index,'Dispatch index differs')
            require(writes==([(frame+cfg['offset'],4)] if cfg['bias'] else []),'Dispatch write differs')
            require(uc.reg_read(r.UC_X86_REG_RSP)==0x300800 and uc.reg_read(r.UC_X86_REG_RBP)==frame,'Dispatch frame differs')
            return index<cfg['count']
        inputs=list(range(65536))+[0x7fffffff,0x80000000,0xfffffffd,0xfffffffe,0xffffffff]
        hits=sum(check(x) for x in inputs);require(visited==starts,'Dispatch coverage incomplete')
        if cfg['bias']:
            site=0x1802ebab7;require(pe_read(game,site,1)==b'\x02','Subtract site differs');uc.mem_write(site,b'\x03');probe=2
        else:
            site=0x1802bc96f;require(pe_read(game,site,4)==struct.pack('<I',327),'Upper bound differs');uc.mem_write(site,struct.pack('<I',326));probe=327
        uc.ctl_remove_cache(entry,entry+len(raw));caught=False
        try:check(probe)
        except ValueError as error:require(str(error)=='Dispatch target differs','Unexpected negative failure');caught=True
        require(caught,'Wrong dispatch escaped detector')
        groups={}
        for index,target in enumerate(targets):groups.setdefault(f'{target:x}',[]).append(index+cfg['bias'])
        results.append(dict(name=cfg['name'],entry=f'{entry:x}',bytes_hex=raw.hex(),instructions=len(ins),cases=len(inputs),table_hits=hits,default_hits=len(inputs)-hits,
                            table=f"{cfg['table']:x}",table_bytes_hex=table.hex(),input_bias=cfg['bias'],count=cfg['count'],default=f"{cfg['default']:x}",target_groups=groups,negative_control_caught=caught))
    result=dict(source_sha256=module['sha256'],address='1802b5740',dispatches=results,cases=sum(x['cases'] for x in results),instructions=sum(x['instructions'] for x in results),
                dependencies={'switch-repair/report.json':digest(folder/'switch-repair/report.json')},
                limitation='Only original switch dispatch instructions executed with a synthetic frame. Case bodies, compiler state and whole-function semantics not executed; selector numeric values are not assigned IL opcode names without further evidence. Does not recover whole C or increase function count.')
    root=folder/'mono-dispatch-behavior';root.mkdir(exist_ok=True);(root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    lines=['# mono_method_to_ir switch target groups','', 'Numeric inputs only; shared target does not prove equivalent full case behavior.','']
    for row in results:
        lines.extend([f"## {row['name']}",'', '| Target | Input values |','| --- | --- |'])
        lines.extend(f"| `{target}` | {', '.join(map(str,values))} |" for target,values in row['target_groups'].items());lines.append('')
    (root/'targets.md').write_text('\n'.join(lines),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={n:digest(root/n) for n in ('report.json','targets.md')}),indent=2),encoding='utf-8')
    print(json.dumps(dict(cases=result['cases'],instructions=result['instructions'],dispatches=[{k:v for k,v in row.items() if k not in ('bytes_hex','table_bytes_hex','target_groups')}|{'unique_targets':len(row['target_groups'])} for row in results])))


if __name__=='__main__':main()
