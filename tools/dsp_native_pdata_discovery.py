"""枚举固定 Unity PE 的运行时函数记录，保留展开链以区别分段与新函数。"""
import collections
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require


def verify_coverage():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    folder=GENERATED/'native/UnityPlayer.dll';root=folder/'pdata-discovery'
    source=read_json(root/'records.json');coverage=read_json(root/'coverage.json')
    runtime={r['begin']:r for r in source['records']}
    require(coverage['source_sha256']==GAME_SHA and coverage['input_sha256']==digest(root/'records.json'),'Coverage input mismatch')
    for name,sha in source['dependencies'].items():require(digest(folder/name)==sha,'Index dependency changed')
    path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(path)==GAME_SHA,'Game changed');game=path.read_bytes()
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);counts=collections.Counter();gaps=[];candidates=[]
    for record in coverage['records']:
        a=int(record['begin'],16);b=int(record['end_exclusive'],16)
        require(b-a==record['bytes'],'Runtime size mismatch')
        missing=sum(int(y,16)-int(x,16)+1 for x,y in record['uncovered_ranges_inclusive'])
        require(missing==record['bytes']-record['covered_bytes'],'Coverage byte accounting differs')
        require(missing==sum(record[k] for k in ['uncovered_instruction_bytes','uncovered_defined_data_bytes','uncovered_undefined_bytes']),'Listing byte accounting differs')
        for left,right in record['uncovered_ranges_inclusive']:
            start=int(left,16);end=int(right,16)+1;raw=pe_read(game,start,end-start)
            excluded=set()
            for unit in record['uncovered_defined_units']:
                if unit['kind']=='defined-data':excluded.update(range(max(start,int(unit['begin'],16)),min(end,int(unit['end_inclusive'],16)+1)))
            segments=[];cursor=start
            while cursor<end:
                if cursor in excluded:cursor+=1;continue
                stop=cursor+1
                while stop<end and stop not in excluded:stop+=1
                data=raw[cursor-start:stop-start];ins=list(decoder.disasm(data,cursor))
                padding=sum(i.size for i in ins)==len(data) and all(i.mnemonic in ('nop','int3') for i in ins)
                segments.append(dict(begin=f'{cursor:x}',end_exclusive=f'{stop:x}',padding_only=padding,bytes_hex=data.hex()))
                cursor=stop
            category='defined-data-and-padding' if excluded and all(s['padding_only'] for s in segments) else 'padding-only' if all(s['padding_only'] for s in segments) else 'needs-analysis'
            row=dict(begin=left,end_exclusive=f'{end:x}',runtime_begin=record['begin'],root_begin=runtime[record['begin']]['root_begin'],category=category,bytes_hex=raw.hex(),ghidra_defined_data_bytes=len(excluded),remaining_segments=segments)
            counts[category]+=len(raw);gaps.append(row)
            if category=='needs-analysis':candidates.append(row)
    require(sum(coverage[k] for k in ['fully_covered_records','partially_covered_records','uncovered_records'])==len(source['records']),'Record totals differ')
    report=dict(source_sha256=GAME_SHA,records_sha256=digest(root/'records.json'),coverage_sha256=digest(root/'coverage.json'),
                gap_bytes_by_category=dict(counts),gap_ranges=len(gaps),candidate_ranges=len(candidates),candidate_roots=len({r['root_begin'] for r in candidates}),gaps=gaps,
                limitation='Ghidra defined-data classification and NOP/INT3 byte patterns do not prove runtime reachability or whole-program semantics; mixed/unclassified segments remain explicit candidates.')
    (root/'gap-audit.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'candidates.json').write_text(json.dumps(candidates,indent=2),encoding='utf-8')
    names=['records.json','coverage.json','gap-audit.json','candidates.json']
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={n:digest(root/n) for n in names}),indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k!='gaps'}))


def main():
    folder=GENERATED/'native/UnityPlayer.dll'
    path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(path)==GAME_SHA,'Unity baseline changed');game=path.read_bytes()
    pe=struct.unpack_from('<I',game,60)[0]
    require(struct.unpack_from('<H',game,pe+24)[0]==0x20b,'Expected PE32+')
    base=struct.unpack_from('<Q',game,pe+48)[0]
    rva,size=struct.unpack_from('<II',game,pe+160)
    require(size>0 and size%12==0,'Invalid exception directory')
    entries=list(struct.iter_unpack('<III',pe_read(game,base+rva,size)))
    known={f['address'] for f in read_json(folder/'functions.json')['functions']}
    dependencies={'functions.json':digest(folder/'functions.json')}
    for kind in ['crt-callbacks','exit-callbacks']:
        marker=read_json(folder/kind/'manifest.json');require(marker['source_sha256']==GAME_SHA,'Supplement baseline changed')
        require(digest(folder/kind/'report.json')==marker['files']['report.json'],'Supplement report changed')
        known.update(f['address'] for f in read_json(folder/kind/'report.json')['functions'] if f['status']=='decompiled')
        dependencies[kind+'/manifest.json']=digest(folder/kind/'manifest.json')
    rows=[]
    for n,(a,b,c) in enumerate(entries):
        require(0<a<b and c>0 and (n==0 or entries[n-1][0]<=a),'Invalid or unsorted runtime range')
        header=pe_read(game,base+c,4);version=header[0]&7;flags=header[0]>>3
        require(version in (1,2) and not(flags&4 and flags&3),'Invalid unwind version/flags')
        row=dict(begin=f'{base+a:x}',end_exclusive=f'{base+b:x}',unwind_address=f'{base+c:x}',
                 version=version,flags=flags,exact_entry_indexed=f'{base+a:x}' in known)
        extra=base+c+4+((header[2]+1)&~1)*2
        if flags&4:
            x,y,z=struct.unpack('<III',pe_read(game,extra,12))
            row['chain']=dict(begin=f'{base+x:x}',end_exclusive=f'{base+y:x}',unwind_address=f'{base+z:x}')
        elif flags&3:
            handler=struct.unpack('<I',pe_read(game,extra,4))[0]
            row['handler']=f'{base+handler:x}'
        rows.append(row)
    by_begin={r['begin']:r for r in rows};depths=collections.Counter()
    require(len(by_begin)==len(rows),'Duplicate runtime begin')
    for row in rows:
        current=row;seen=set();depth=0
        while 'chain' in current:
            chain=current['chain'];key=chain['begin'];require(key not in seen,'Cyclic unwind chain');seen.add(key)
            require(key in by_begin and all(by_begin[key][k]==v for k,v in chain.items()),'Chain does not match runtime record')
            current=by_begin[key];depth+=1
        row['root_begin']=current['begin'];row['chain_depth']=depth;depths[depth]+=1
        require(current['exact_entry_indexed'],'Unindexed chain root')
    root=folder/'pdata-discovery';root.mkdir(exist_ok=True)
    report=dict(source_sha256=GAME_SHA,dependencies=dependencies,records=rows,
                summary=dict(records=len(rows),missing_exact_entries=sum(not r['exact_entry_indexed'] for r in rows),flags=dict(collections.Counter(r['flags'] for r in rows)),chain_depths=dict(depths)),
                limitation='Runtime records may be chained fragments inside existing functions. Missing exact entry is not sufficient evidence of a missing function; Ghidra body coverage must be checked. Leaf functions without static unwind records are outside this enumeration.')
    (root/'records.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={'records.json':digest(root/'records.json')}),indent=2),encoding='utf-8')
    print(json.dumps(report['summary']))


if __name__=='__main__':
    import argparse
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--verify-coverage',action='store_true');args=parser.parse_args()
    verify_coverage() if args.verify_coverage else main()
