"""核验六表修正及正常入口下三个覆盖分支不可达；不证明外部调用副作用。"""
import hashlib
import json
import re
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

ENTRY,END=0x18177f9f0,0x18177fb3c
DEFS=[(0x18177fa36,0x18177fb3c,0),(0x18177fa8b,0x18177fb7c,0),
      (0x18177fac0,0x18177fbbc,0),(0x18177fad6,0x18177fbfc,0x18177fc04),
      (0x18177fae9,0x18177fc14,0),(0x18177faff,0x18177fc54,0x18177fc5c)]
MANUAL={0x18177fa8b,0x18177fad6,0x18177faff}
PREFIX='40534883ec204c634128488bd9418d40ff83f8040f871f010000'

def check_outputs(root,game):
    report=read_json(root/'report.json');text=(root/'18177f9f0.c').read_text(encoding='utf-8')
    require(report['source_sha256']==GAME_SHA and report['status']=='decompiled' and report['program_changes_rolled_back'],'Bad repair provenance')
    require(len(report['tables'])==6,'Missing tables');mappings={}
    for row,(branch,table,index) in zip(report['tables'],DEFS):
        require([int(row[k],16) for k in ('branch','rva_table','byte_index_table')]==[branch,table,index],'Table addresses differ')
        require(row['manual_override']==(branch in MANUAL),'Wrong override scope')
        count=2 if index else 16;targets=struct.unpack('<'+'I'*count,pe_read(game,table,count*4))
        indices=pe_read(game,index,16) if index else range(16)
        mapped=[0x180000000+targets[i] for i in indices]
        require(row['targets_by_input']==[f'{a:x}' for a in mapped],'Table values differ');mappings[branch]=mapped
    warnings=re.findall(r'/\*\s*WARNING:\s*(.*?)\*/',text,re.S)
    require([w.strip() for w in warnings]==['Switch is manually overridden']*3,'Unexpected warning')
    require('halt_baddata' not in text and 'Could not recover jumptable' not in text,'Truncated C')
    require('if (4 < iVar1 - 1U)' in text and 'param_1 + 0x240' in text,'Entry guard missing')
    for case,value in [(1,'4'),(2,'8'),(3,'0xc')]:
        require(re.search(rf'case {case}:\s*iVar2 = {value};\s*break;',text),'Reachable factor differs')
    require(re.search(r'case 4:\s*case 5:\s*iVar2 = 0x10;',text),'Repeated case 5 lost')
    require('iVar2 = iVar2 * *(int *)(param_1 + 0x60);' in text and 'FUN_181835930' in text,'Normal behavior missing')
    return mappings

def verify(folder,game_path):
    root=folder/'quality-repair/18177f9f0/override-unreachable';marker=root/'manifest.json'
    if not marker.exists():return {}
    manifest=read_json(marker);require(manifest['source_sha256']==GAME_SHA and digest(game_path)==GAME_SHA,'Game changed')
    for name,sha in manifest['files'].items():require(digest(root/name)==sha,'Multistage repair artifact changed')
    game=game_path.read_bytes();check_outputs(root,game);proof=read_json(root/'normal-domain-proof.json')
    require(proof['code_sha256']==hashlib.sha256(pe_read(game,ENTRY,END-ENTRY)).hexdigest(),'Code proof baseline differs')
    require(proof['source_sha256']==GAME_SHA and proof['status']=='normal-domain-verified' and proof['case_count']==552
            and proof['excluded_manual_branches']==[f'{a:x}' for a in sorted(MANUAL)],'Domain evidence incomplete')
    return dict(address='18177f9f0',file=(root/'18177f9f0.c').relative_to(folder).as_posix(),
                mode='verified-multistage-switch-repair',case_count=96,report=(root/'report.json').relative_to(folder).as_posix())

def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64,CS_GRP_JUMP,CS_GRP_RET,CS_GRP_CALL
    from capstone.x86 import X86_OP_IMM
    from dsp_native_multistage_probe import run
    inv=read_json(GENERATED/'native/inventory.json');path=Path(inv['game_directory'])/'UnityPlayer.dll'
    require(digest(path)==GAME_SHA,'Game changed');game=path.read_bytes();folder=GENERATED/'native/UnityPlayer.dll'
    root=folder/'quality-repair/18177f9f0/override-unreachable';(root/'manifest.json').unlink(missing_ok=True)
    mappings=check_outputs(root,game);code=pe_read(game,ENTRY,END-ENTRY)
    require(code.startswith(bytes.fromhex(PREFIX)),'Entry predicate changed')
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True;ins=list(decoder.disasm(code,ENTRY));nodes={i.address:i for i in ins}
    require(sum(i.size for i in ins)==len(code),'Incomplete decode')
    require({i.address for i in ins if i.group(CS_GRP_JUMP) and i.operands[0].type!=X86_OP_IMM}==set(mappings),'Unknown indirect branch')
    require(all(t in nodes for targets in mappings.values() for t in targets),'Invalid table target')
    writes={r:[] for r in ('r8','r10')}
    for i in ins:
        for reg in i.regs_access()[1]:
            name=i.reg_name(reg)
            for full in writes:
                if name in (full,full+'d',full+'w',full+'b'):writes[full].append(i.address)
    require(writes=={'r8':[0x18177f9f6,0x18177fb0d],'r10':[0x18177fa24]},'Selector/base write set changed')
    require(pe_read(game,0x18177fa24,7)==bytes.fromhex('4c8d15d50588fe'),'Image base assignment differs')
    require([i.address for i in ins if i.group(CS_GRP_CALL)]==[0x18177fb1a],'Unexpected early external call')
    # The fixed prefix computes uint32(kind-1)<=4, exactly kind in {1,2,3,4,5}.
    # Outside that set it branches directly to zero-return. Within the set,
    # R8 is unchanged through every switch; no call occurs before the switches.
    guards={0x18177fa04,0x18177fa1e,0x18177fab3,0x18177fadc}
    def reachable(kind,stop=None):
        pending=[ENTRY];seen=set()
        while pending:
            pc=pending.pop()
            if pc in seen or pc==stop:continue
            require(pc in nodes,'Flow outside code');seen.add(pc);i=nodes[pc]
            if i.group(CS_GRP_RET):continue
            if pc in mappings:
                successors=[mappings[pc][kind]]
            elif i.group(CS_GRP_JUMP):
                require(i.operands[0].type==X86_OP_IMM,'Unresolved branch');dest=i.operands[0].imm
                if pc in guards:
                    require(i.mnemonic=='ja','Guard condition differs');successors=[pc+i.size]
                else:successors=[dest] if i.mnemonic=='jmp' else [dest,pc+i.size]
            else:successors=[pc+i.size]
            require(all(p>pc for p in successors),'Backward edge could invalidate selector lifetime')
            pending.extend(successors)
        return seen
    traces={}
    for kind in range(1,6):
        seen=reachable(kind);require(not MANUAL&seen,'Manual override reachable from valid selector')
        require(not set(mappings)&reachable(kind,0x18177fa24),'Image base assignment bypassed')
        traces[str(kind)]=dict(instructions=len(seen),switches=[f'{a:x}' for a in sorted(set(mappings)&seen)])
    cases=[run(game,k,s,o,p) for k in list(range(20))+[0x7fffffff,0x80000000,0xffffffff]
           for s in (0,1,7,0xffffffff) for o in (0,4,0xfffffffc) for p in (False,True)]
    try:run(game,5,1,4,True,True)
    except ValueError as error:require(str(error)=='Normal-entry behavior differs','Unexpected negative error')
    else:raise ValueError('Case 5 mutation accepted')
    proof=dict(status='normal-domain-verified',source_sha256=GAME_SHA,code_sha256=hashlib.sha256(code).hexdigest(),
        predicate='uint32(kind - 1) <= 4 iff kind in {1,2,3,4,5}',register_writes={k:[f'{a:x}' for a in v] for k,v in writes.items()},
        traces=traces,excluded_manual_branches=[f'{a:x}' for a in sorted(MANUAL)],case_count=len(cases),cases=cases,
        case5_mutation_rejected=True,scope='Normal entry and normal external return; static selector-domain proof plus sampled CPU behavior. External memory-copy semantics, exceptions, concurrent mutation and full typed-C equivalence are not proven.')
    (root/'normal-domain-proof.json').write_text(json.dumps(proof,indent=2),encoding='utf-8')
    names=['report.json','18177f9f0.c','normal-domain-proof.json']
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={n:digest(root/n) for n in names}),indent=2),encoding='utf-8')
    verify(folder,path);print('Verified 96 table mappings, normal-domain exclusion of 3 overridden branches, and 552 CPU cases')

if __name__=='__main__':main()
