"""Verify address/control references for 300 inherited-frame prefixes, preserving uncertainty."""
import collections
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_known_boundaries import validate_cached as validate_boundaries


def validate_cached(folder,sha):
    root=folder/'frame-reference-evidence'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Frame source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Frame evidence differs')
    report=read_json(root/'report.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Frame dependency differs')
    return report


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32,CS_GRP_CALL,CS_GRP_JUMP
    from capstone.x86 import X86_OP_IMM,X86_OP_MEM
    inv=read_json(GENERATED/'native/inventory.json')
    for name in ('rail_api.dll','rail_wrapper.dll'):
        module=next(m for m in inv['files'] if Path(m['path']).name==name);folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Game changed');game=path.read_bytes()
        prior=validate_boundaries(folder,module['sha256']);edges=[r for r in prior['entries'] if r['category']=='inherited-frame-prefix']
        refs=read_json(folder/'known-boundary-audit/frame-refs.json');require(refs['source_sha256']==module['sha256'],'Reference source differs');observations={r['address']:r for r in refs['tables']}
        require(set(observations)=={r[k] for r in edges for k in ('source','target')},'Reference scope differs');cs=Cs(CS_ARCH_X86,CS_MODE_32);cs.detail=True
        counts=collections.Counter();verified={};unresolved=[]
        for key,observation in observations.items():
            address=int(key,16);rows=[]
            if observation.get('target_bytes'):
                raw=bytes.fromhex(observation['target_bytes']);require(pe_read(game,address,len(raw))==raw,'Observed target differs')
            for ref in observation['references']:
                pc=int(ref['from'],16);kind=None
                if 'bytes' in ref:
                    raw=bytes.fromhex(ref['bytes']);require(pe_read(game,pc,len(raw))==raw,'Reference bytes differ');decoded=list(cs.disasm(raw,pc));require(len(decoded)==1 and decoded[0].size==len(raw),'Reference decode differs');i=decoded[0]
                    if (i.group(CS_GRP_CALL) or i.group(CS_GRP_JUMP)) and i.operands[0].type==X86_OP_IMM and i.operands[0].imm&0xffffffff==address:kind='direct-call' if i.group(CS_GRP_CALL) else 'direct-jump'
                    elif any(op.type==X86_OP_IMM and op.imm&0xffffffff==address for op in i.operands):kind='immediate-address'
                    elif any(op.type==X86_OP_MEM and not op.mem.base and not op.mem.index and op.mem.disp&0xffffffff==address for op in i.operands):kind='absolute-memory-address'
                    instruction=i.mnemonic+' '+i.op_str
                else:
                    raw=pe_read(game,pc,4);instruction=None
                    if struct.unpack('<I',raw)[0]==address:kind='data-pointer'
                row=dict(site=ref['from'],owner=ref.get('function'),ghidra_kind=ref['type'],actual_kind=kind or 'unresolved-reference-form',bytes_hex=raw.hex(),instruction=instruction)
                rows.append(row);counts[row['actual_kind']]+=1
                if kind=='direct-jump' and 'CALL' in ref['type']:counts['jump-labeled-call']+=1
                if kind is None:unresolved.append(dict(target=key,reference=row))
            verified[key]=rows
        entries=[];edge_counts=collections.Counter()
        for edge in edges:
            source,target=edge['source'],edge['target'];source_refs=verified[source];target_refs=verified[target]
            source_owners=sorted({r['owner'] for r in source_refs if r['owner'] and r['actual_kind']!='unresolved-reference-form'})
            target_owners=sorted({r['owner'] for r in target_refs if r['owner'] and r['actual_kind']!='unresolved-reference-form'})
            related=sorted(set(source_owners+target_owners))
            if related:edge_counts['with-associated-function']+=1
            else:edge_counts['no-associated-function']+=1
            if source_refs:edge_counts['with-source-references']+=1
            else:edge_counts['without-source-references']+=1
            if target_refs:edge_counts['with-target-references']+=1
            else:edge_counts['without-target-references']+=1
            entries.append(dict(source=source,target=target,source_references=source_refs,target_references=target_refs,source_reference_owners=source_owners,target_reference_owners=target_owners,related_functions=related,
                                source_owner=observations[source].get('target_owner'),target_owner=observations[target].get('target_owner')))
        result=dict(source_sha256=module['sha256'],entries=entries,reference_counts=dict(counts),context_counts=dict(edge_counts),unresolved=unresolved,
                    dependencies={n:digest(folder/n) for n in ('known-boundary-audit/manifest.json','known-boundary-audit/frame-refs.json')},
                    limitation='Raw byte/operand verified references and original-project ownership observations only. Associated functions are candidate context, not unique enclosing-function proof. Missing references are not proof of unreachability. Data pointer slots are not yet parsed as exception metadata. No automatic function merges.')
        root=folder/'frame-reference-evidence';root.mkdir(exist_ok=True);(root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
        print(json.dumps(dict(module=name,entries=len(entries),reference_counts=dict(counts),context_counts=dict(edge_counts),unresolved=len(unresolved))))


if __name__=='__main__':main()
