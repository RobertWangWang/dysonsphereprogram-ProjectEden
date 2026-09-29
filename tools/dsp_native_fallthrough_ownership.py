"""Validate original-project ownership observations for missing fallthrough targets."""
import collections
import json
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require
from dsp_native_relocation_operands import validate_cached


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    decoder=Cs(CS_ARCH_X86,CS_MODE_32)
    root=GENERATED/'native';inventory=read_json(root/'inventory.json');results=[]
    for name in ('rail_api.dll','rail_wrapper.dll'):
        module=next(m for m in inventory['files'] if Path(m['path']).name==name);folder=root/module['output'];audit=folder/'supplement-flow-audit'
        report=read_json(audit/'report.json');refs=read_json(audit/'fallthrough-refs.json')
        require(refs['source_sha256']==report['source_sha256']==module['sha256'],'Source differs')
        path=Path(inventory['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Game changed');game=path.read_bytes()
        marker=read_json(audit/'manifest.json');require(digest(audit/'report.json')==marker['files']['report.json'],'Audit changed')
        classified=validate_cached(folder,module['sha256']);quarantined={r['address'] for r in classified.get('quarantined_entries',[])}
        edges=[r for r in report['edges'] if r['kind']=='uncovered-fallthrough' and not r['exact_entry_known']]
        by_target={r['address']:r for r in refs['tables']};require(set(by_target)=={r['target'] for r in edges},'Ownership scope differs')
        rows=[];counts=collections.Counter()
        for edge in edges:
            pc=int(edge['site'],16);raw=bytes.fromhex(edge['bytes_hex']);require(pe_read(game,pc,len(raw))==raw and pc+len(raw)==int(edge['target'],16),'Fallthrough evidence differs')
            observation=by_target[edge['target']]
            if observation.get('target_bytes'):
                actual=bytes.fromhex(observation['target_bytes']);require(pe_read(game,int(edge['target'],16),len(actual))==actual,'Target bytes differ')
            status='quarantined-data-source' if edge['source'] in quarantined else 'inside-existing-function' if observation.get('target_owner') else 'not-owned-in-original-project'
            row=dict(source=edge['source'],site=edge['site'],target=edge['target'],status=status,observation=observation)
            if status=='not-owned-in-original-project':
                target=int(edge['target'],16);window=pe_read(game,target,16);decoded=list(decoder.disasm(window,target))
                require(decoded and decoded[0].address==target,'Cannot decode target')
                row['target_window_hex']=window.hex()
                row['first_instruction']=dict(bytes_hex=decoded[0].bytes.hex(),mnemonic=decoded[0].mnemonic,operands=decoded[0].op_str)
                row['target_kind']='int3-after-call' if window[0]==0xcc and raw[0]==0xe8 else 'instruction-continuation-needs-body-review'
                if row['target_kind']=='int3-after-call':row['call_target']=f'{pc+5+int.from_bytes(raw[1:],"little",signed=True):x}'
            rows.append(row);counts[status]+=1
        output=folder/'fallthrough-ownership';output.mkdir(exist_ok=True)
        result=dict(source_sha256=module['sha256'],entries=rows,counts=dict(counts),dependencies={n:digest(folder/n) for n in ('supplement-flow-audit/report.json','supplement-flow-audit/fallthrough-refs.json')},
                    limitation='Original-project ownership and bytes only. Fallthrough after a call assumes that call returns; ownership does not prove reachability or correct source-level function boundaries.')
        if classified:result['dependencies']['relocation-operand-audit/manifest.json']=digest(folder/'relocation-operand-audit/manifest.json')
        (output/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
        (output/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(output/'report.json')}),indent=2),encoding='utf-8')
        results.append(dict(module=name,counts=dict(counts)))
    print(json.dumps(results))


if __name__=='__main__':main()
