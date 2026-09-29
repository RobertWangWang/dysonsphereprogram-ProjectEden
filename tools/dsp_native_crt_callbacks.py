"""核验新增 CRT 回调导出的入口集合、函数体及原始字节；供查询读取。"""
import collections
import json
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require


def validate_cached(folder,sha,kind='crt-callbacks'):
    root=folder/kind
    if not (root/'manifest.json').exists():return []
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'CRT callback baseline changed')
    for name,value in marker['files'].items():require(digest(root/name)==value,'CRT callback artifact changed: '+name)
    report=read_json(root/'report.json');require(report['complete'] and report['program_changes_rolled_back'],'CRT export incomplete')
    return [{**row,'file':kind+'/'+row['file'],'crt_discovery':True,'callback_kind':kind} for row in report['functions'] if row['status']=='decompiled']


def main():
    import argparse
    parser=argparse.ArgumentParser(description=__doc__);group=parser.add_mutually_exclusive_group();group.add_argument('--exit-callbacks',action='store_true');group.add_argument('--reader-tables',action='store_true');group.add_argument('--pointer-callbacks',action='store_true');group.add_argument('--direct-callbacks',action='store_true');group.add_argument('--relocation-module');parser.add_argument('--retry',action='store_true');parser.add_argument('--flow',action='store_true');parser.add_argument('--flow-round',type=int,default=1);args=parser.parse_args()
    if args.flow_round<1 or (args.flow_round!=1 and not args.flow):parser.error('--flow-round needs --flow and a positive round')
    if args.flow and (not args.relocation_module or args.retry):parser.error('--flow requires --relocation-module and cannot combine with --retry')
    if args.retry and not args.relocation_module:parser.error('--retry requires --relocation-module')
    kind='direct-callbacks' if args.direct_callbacks else 'pointer-callbacks' if args.pointer_callbacks else 'reader-table-callbacks' if args.reader_tables else 'exit-callbacks' if args.exit_callbacks else 'crt-callbacks'
    list_name='pointer-edges/missing-entries.txt' if args.direct_callbacks else 'pointer-discovery/missing-entries.txt' if args.pointer_callbacks else 'reader-table-discovery/missing-entries.txt' if args.reader_tables else 'exit-discovery/missing-entries.txt' if args.exit_callbacks else 'irradiance-globals/missing-callbacks.txt'
    folder=GENERATED/'native/UnityPlayer.dll';root=folder/kind
    game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    source_sha=GAME_SHA
    if args.relocation_module:
        from dsp_native_select import selected_folder
        inventory=read_json(GENERATED/'native/inventory.json');matches=[m for m in inventory['files'] if args.relocation_module in (m['path'],Path(m['path']).name)]
        require(len(matches)==1,'Need unique module');module=matches[0];source_sha=module['sha256'];folder=selected_folder(GENERATED/'native',module)
        kind='relocation-callbacks';root=folder/kind;list_name='relocation-audit/missing-entries.txt';game_path=Path(inventory['game_directory'])/module['path']
        if args.retry:kind='relocation-retry';root=folder/kind;list_name='relocation-retry/entries.txt'
        if args.flow:
            kind='flow-callbacks' if args.flow_round==1 else f'flow-callbacks-{args.flow_round}';root=folder/kind
            audit_kind='supplement-flow-audit' if args.flow_round==1 else 'flow-followup-audit' if args.flow_round==2 else f'flow-followup-audit-{args.flow_round-1}'
            list_name=audit_kind+'/direct-only-entries.txt'
    require(digest(game_path)==source_sha,'Game changed');game=game_path.read_bytes()
    report=read_json(root/'report.json');require(report['complete'] and report['program_changes_rolled_back'],'Export incomplete')
    require(report['source_sha256']==source_sha,'Export baseline changed')
    expected=set((folder/list_name).read_text().split())
    if args.flow:
        evidence=read_json(folder/audit_kind/'report.json')
        require(evidence['source_sha256']==source_sha and {r['target'] for r in evidence['direct_entries']}==expected,'Flow candidate scope differs')
        for edge in evidence['direct_entries']:
            raw=bytes.fromhex(edge['bytes_hex']);site=int(edge['site'],16)
            require(len(raw)==5 and raw[0] in (0xe8,0xe9) and pe_read(game,site,5)==raw and site+5+int.from_bytes(raw[1:],'little',signed=True)==int(edge['target'],16),'Invalid direct branch evidence')
    if args.retry:
        prior=read_json(folder/'relocation-callbacks/report.json')
        failed={r['address'] for r in prior['functions'] if r['status']=='decompile-failed'}
        require(expected<=failed,'Retry input was not a prior C failure')
    rows=report['functions'];require({r['address'] for r in rows}==expected and len(rows)==len(expected),'Entry set differs')
    original={f['address'] for f in read_json(folder/'functions.json')['functions']}
    require(not original.intersection(expected),'Entries no longer new to base index')
    files={'report.json':digest(root/'report.json')};status=collections.Counter();warnings=collections.Counter();total_bytes=0;instructions=0;bodies_verified=0
    for row in rows:
        status[row['status']]+=1
        if row['status']=='decompiled':
            c=root/row['file'];require(c.is_file() and c.stat().st_size>0,'Missing C file');files[row['file']]=digest(c)
        elif 'body_ranges_inclusive' not in row or 'instruction_count' not in row:continue
        asm=root/'functions'/(row['address']+'.asm');seen=set();count=0
        if args.retry:require(digest(asm)==digest(folder/'relocation-callbacks/functions'/(row['address']+'.asm')),'Retry assembly differs from previous export')
        body=set()
        for a,b in row['body_ranges_inclusive']:body.update(range(int(a,16),int(b,16)+1))
        require(len(body)==row['size'] and int(row['address'],16) in body,'Body size or entry differs')
        for line in asm.read_text(encoding='utf-8').splitlines():
            address,hexbytes,_=line.split(' ',2);start=int(address,16);raw=bytes.fromhex(hexbytes)
            require(raw==pe_read(game,start,len(raw)),'Instruction bytes differ from game')
            span=set(range(start,start+len(raw)));require(span<=body and not span.intersection(seen),'Instruction escapes/overlaps body')
            seen|=span;count+=1
        require(seen==body and count==row['instruction_count'],'Incomplete instruction body coverage')
        total_bytes+=len(body);instructions+=count;bodies_verified+=1
        for warning in row.get('warnings',[]):
            category='overlapping-globals' if 'Globals starting' in warning else 'unreachable' if 'unreachable' in warning else 'bad-instruction' if 'Bad instruction' in warning else 'other'
            warnings[category]+=1
        files['functions/'+row['address']+'.asm']=digest(asm)
    summary=dict(source_sha256=source_sha,entry_count=len(rows),statuses=dict(status),functions_with_warnings=sum(bool(r.get('warnings')) for r in rows),
        warning_occurrences=dict(warnings),body_bytes=total_bytes,instructions=instructions,bodies_verified=bodies_verified,
        original_index_sha256=digest(folder/'functions.json'),input_list_sha256=digest(folder/list_name),
        limitation='New exact entries, complete exported instruction bytes and decompiler output verified; function boundaries and warning semantics are not a whole-program equivalence proof. Original Ghidra project unchanged.')
    (root/'verification.json').write_text(json.dumps(summary,indent=2),encoding='utf-8');files['verification.json']=digest(root/'verification.json')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=source_sha,files=files),indent=2),encoding='utf-8')
    print(json.dumps(summary))


if __name__=='__main__':main()
