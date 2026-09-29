"""Independently check Mono's expanded body and its static control-flow boundaries."""
import collections
import json
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_select import selected_folder
from dsp_native_mono_dispatch import validate_cached as validate_dispatch


def validate_cached(folder,sha):
    root=folder/'mono-body-evidence'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Mono body source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Mono body evidence differs')
    result=read_json(root/'report.json')
    for name,value in result['dependencies'].items():require(digest(folder/name)==value,'Mono body dependency differs')
    return result


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64,CS_GRP_JUMP,CS_GRP_CALL,CS_GRP_RET
    from capstone.x86 import X86_OP_IMM
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='mono-2.0-bdwgc.dll')
    folder=selected_folder(GENERATED/'native',module);source=Path(inv['game_directory'])/module['path'];require(digest(source)==module['sha256'],'Source changed');game=source.read_bytes()
    attempt=read_json(folder/'switch-normalize/report.json');previous=read_json(folder/'switch-repair/report.json')
    require(attempt['source_sha256']==module['sha256'] and attempt.get('program_changes_rolled_back') and attempt['status']!='decompiling','Export not terminal')
    require(attempt['ranges']==previous['ranges'] and attempt['instructions']==previous['instructions'],'Expanded body changed')
    require(validate_dispatch(folder,module['sha256']),'Missing dispatch evidence')
    lo,hi=0x1802b5740,0x180312691;raw=pe_read(game,lo,hi-lo+1);cs=Cs(CS_ARCH_X86,CS_MODE_64);cs.detail=True
    ins={};seen=set()
    for line in (folder/'switch-normalize/mono_method_to_ir.asm').read_text().splitlines():
        address,hexbytes,_=line.split(' ',2);pc=int(address,16);data=bytes.fromhex(hexbytes)
        require(lo<=pc and pc+len(data)-1<=hi and raw[pc-lo:pc-lo+len(data)]==data,'Exported bytes differ')
        decoded=list(cs.disasm(data,pc));require(len(decoded)==1 and decoded[0].size==len(data),'Instruction boundary differs')
        span=set(range(pc,pc+len(data)));require(not seen&span,'Instructions overlap');seen.update(span);ins[pc]=decoded[0]
    expected=set()
    for start,end in attempt['ranges']:expected.update(range(int(start,16),int(end,16)+1))
    require(seen==expected and len(ins)==attempt['instructions'] and len(seen)==attempt['new_body_bytes'],'Body coverage differs')
    switches={0x1802bc991:[int(x,16) for x in attempt['table_targets']],0x1802ebae3:[int(x,16) for x in attempt['nested_table_targets']]}
    successors={};edges=[];calls=collections.Counter();indirect_calls=[];returns=[];traps=[];unresolved=[]
    for pc,i in ins.items():
        nxt=pc+i.size;dest=[]
        if i.group(CS_GRP_RET):returns.append(f'{pc:x}')
        elif i.mnemonic in ('int3','ud2','hlt'):traps.append(f'{pc:x}')
        elif i.group(CS_GRP_JUMP):
            if i.operands[0].type==X86_OP_IMM:dest.append(i.operands[0].imm)
            elif pc in switches:dest.extend(switches[pc])
            else:unresolved.append(f'{pc:x}')
            if i.mnemonic not in ('jmp','ljmp'):dest.append(nxt)
        else:
            if i.group(CS_GRP_CALL):
                if i.operands[0].type==X86_OP_IMM:calls[i.operands[0].imm]+=1
                else:indirect_calls.append(dict(address=f'{pc:x}',operand=i.op_str))
            dest.append(nxt)
        successors[pc]=set(dest)
        for target in set(dest):
            if target not in ins:edges.append(dict(source=f'{pc:x}',target=f'{target:x}',instruction=i.mnemonic+' '+i.op_str))
    reached=set();pending=[lo]
    while pending:
        pc=pending.pop()
        if pc in reached or pc not in ins:continue
        reached.add(pc);pending.extend(successors[pc]-reached)
    names={int(f['address'],16):f['name'] for f in read_json(folder/'functions.json')['functions']}
    for edge in edges:
        caller=ins[int(edge['source'],16)]
        if caller.group(CS_GRP_CALL) and caller.operands[0].type==X86_OP_IMM:
            callee=caller.operands[0].imm;edge['callee']=f'{callee:x}';edge['callee_name']=names.get(callee)
        target=int(edge['target'],16);context=pe_read(game,target,32)
        edge['following_bytes_hex']=context.hex()
        edge['following_decode']=[dict(address=f'{i.address:x}',instruction=i.mnemonic+' '+i.op_str) for i in cs.disasm(context,target)]
    call_rows=[dict(address=f'{a:x}',name=names.get(a),sites=n) for a,n in sorted(calls.items())]
    result=dict(source_sha256=module['sha256'],address=f'{lo:x}',body_bytes=len(seen),instructions=len(ins),static_reachable_instructions=len(reached),
                static_unreachable=[f'{p:x}' for p in sorted(set(ins)-reached)],out_of_body_edges=edges,unresolved_indirect_jumps=unresolved,
                direct_call_sites=sum(calls.values()),direct_call_targets=len(calls),indirect_call_sites=indirect_calls,return_sites=returns,trap_sites=traps,
                decompile_status=attempt['status'],decompile_message=attempt.get('message'),c_present=(folder/'switch-normalize/mono_method_to_ir.c').exists(),
                dependencies={n:digest(folder/n) for n in ('switch-normalize/report.json','switch-normalize/mono_method_to_ir.asm','switch-repair/report.json','mono-dispatch-behavior/manifest.json')},
                limitation='Static graph follows both conditional branches, assumes calls return, and uses verified switch targets. Reachability is not runtime feasibility or whole-function correctness. Out-of-body edges retained for review; C availability is not semantic completion.')
    root=folder/'mono-body-evidence';root.mkdir(exist_ok=True);(root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(root/'calls.json').write_text(json.dumps(call_rows,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={n:digest(root/n) for n in ('report.json','calls.json')}),indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k not in ('static_unreachable','indirect_call_sites','dependencies','return_sites','trap_sites','out_of_body_edges')}|dict(out_of_body_edges=len(edges),static_unreachable_count=len(result['static_unreachable']))))


if __name__=='__main__':main()
