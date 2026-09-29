"""检查三个局部标志条件的零值分支是否能到达 FMA4 指令。"""
import json
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require
from dsp_native_fma4 import PROFILES,validate_cached
from dsp_native_fma4_cpu_probe import GATES


def validate_gate_cached(folder,sha):
    root=folder/'fma4-gate-flow'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Gate flow baseline changed')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Gate flow evidence changed')
    report=read_json(root/'report.json')
    for result in report['functions']:
        require(digest(folder/'fma4-disassembly'/result['address']/'manifest.json')==result['source_manifest_sha256'],'Gate graph source changed')
    return report


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64,CS_GRP_JUMP,CS_GRP_RET,CS_GRP_CALL
    from capstone.x86 import X86_OP_IMM,X86_OP_MEM,X86_REG_RSP
    folder=GENERATED/'native/UnityPlayer.dll'
    game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game changed');game=game_path.read_bytes()
    records=validate_cached(folder,GAME_SHA);decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True
    results=[]
    for name,_,after_gate,offset in GATES:
        start=int(name,16);end=PROFILES[start][0];root,old=records[name]
        flow=read_json(root/'normal-flow.json');require(not flow['unresolved'],'Unresolved source flow')
        ins=list(decoder.disasm(pe_read(game,start,end-start),start));nodes={i.address:i for i in ins};graph={}
        for i in ins:
            next_address=i.address+i.size
            if i.group(CS_GRP_RET):targets=[]
            elif i.group(CS_GRP_JUMP):
                targets=[i.operands[0].imm] if i.operands[0].type==X86_OP_IMM else [int(a,16) for a in flow['resolved'][f'{i.address:x}']]
                if i.mnemonic!='jmp':targets.append(next_address)
            else:targets=[next_address]
            require(all(t in nodes for t in targets),'Graph leaves function body')
            graph[i.address]=targets
        comparisons=[i for i in ins if i.mnemonic=='cmp' and len(i.operands)==2 and i.operands[0].type==X86_OP_MEM
            and i.operands[0].size==1 and i.operands[0].mem.base==X86_REG_RSP and not i.operands[0].mem.index
            and i.operands[0].mem.disp==offset and i.operands[1].type==X86_OP_IMM and i.operands[1].imm==0]
        require(len(comparisons)==1,'Expected one local flag comparison')
        compare=comparisons[0];branch=nodes[compare.address+compare.size];between=[]
        while branch.mnemonic not in ('je','jne'):
            require(not branch.group(CS_GRP_CALL) and not branch.group(CS_GRP_JUMP) and not branch.group(CS_GRP_RET),'Unexpected predicate control transfer')
            require(not any(branch.reg_name(r) in ('eflags','rflags') for r in branch.regs_access()[1]),'Flag predicate overwritten')
            between.append(branch.address);branch=nodes[branch.address+branch.size]
        def reachable(cut=False,omit=None):
            seen=set();todo=[after_gate]
            while todo:
                pc=todo.pop()
                if pc in seen or pc==omit:continue
                seen.add(pc)
                targets=graph[pc]
                if cut and pc==branch.address:targets=[zero]
                todo.extend(targets)
            return seen
        zero=branch.operands[0].imm if branch.mnemonic=='je' else branch.address+branch.size
        # Incoming edges cannot bypass the comparison or replace its flags.
        require(branch.address not in reachable(omit=compare.address),'Comparison does not dominate branch')
        for previous,current in zip([compare.address]+between,between+[branch.address]):
            require({a for a,targets in graph.items() if current in targets}=={previous},'Predicate block has an alternate predecessor')
        fma={i.address for i in ins if i.mnemonic=='vfmaddps'}
        all_reachable=reachable();disabled=reachable(cut=True)
        require(len(fma)==40 and fma<=all_reachable and not fma.intersection(disabled),'Disabled branch can reach FMA4')
        require(not fma.intersection(reachable(omit=branch.address)),'FMA4 can bypass flag branch')
        results.append(dict(address=name,comparison=f'{compare.address:x}',branch=f'{branch.address:x}',zero_successor=f'{zero:x}',
            fma_addresses=[f'{a:x}' for a in sorted(fma)],disabled_reachable_instructions=len(disabled),
            restored_edge_fma_count=len(fma.intersection(all_reachable)),source_manifest_sha256=digest(root/'manifest.json')))
    root=folder/'fma4-gate-flow';root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    report=dict(status='zero-branch-excludes-fma4',source_sha256=GAME_SHA,functions=results,
        scope='Normal post-entry-gate control flow; resolved indirect targets reused from independently checked normal-flow evidence; all other conditional successors retained and calls assumed to return.',
        limitation='Zero result is imposed at every visit to the identified byte comparison. No proof of stack-byte alias immutability, malicious/external writes, exception entry, nonlocal control transfer or floating-point equivalence.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print('All 120 FMA4 instructions excluded by zero comparison outcomes; restoring edges makes all reachable')


if __name__=='__main__':main()
