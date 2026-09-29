"""核验四个浮点读取分发表的安装指令及前八槽，发现缺失精确入口。"""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require


def main():
    folder=GENERATED/'native/UnityPlayer.dll';root=folder/'reader-table-discovery';root.mkdir(exist_ok=True)
    path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(path)==GAME_SHA,'Game changed');game=path.read_bytes()
    refs=read_json(folder/'float-readers/table-refs.json');require(refs['source_sha256']==GAME_SHA,'Reference baseline changed')
    known={f['address'] for f in read_json(folder/'functions.json')['functions']}
    for kind in ('crt-callbacks','exit-callbacks'):known.update(f['address'] for f in read_json(folder/kind/'report.json')['functions'])
    entries=[];installations=[]
    for table in refs['tables']:
        base=int(table['address'],16);leas=[r for r in table['references'] if r.get('instruction','').startswith('LEA RAX,')]
        require(len(leas)==1,'Ambiguous table installation');ref=leas[0];pc=int(ref['from'],16)
        raw=pe_read(game,pc,10);require(raw[:3]==bytes.fromhex('488d05') and pc+7+int.from_bytes(raw[3:7],'little',signed=True)==base and raw[7:]==bytes.fromhex('498901'),'LEA / table store differs')
        installations.append(dict(table=table['address'],setup=ref['from'],store=f'{pc+7:x}',function=ref['function'],bytes_hex=raw.hex()))
        for n,target in enumerate(struct.unpack('<8Q',pe_read(game,base,64))):
            require(0x180001000<=target<0x181890000,'Expected code target')
            entries.append(dict(table=table['address'],slot=f'{base+n*8:x}',index=n,target=f'{target:x}',indexed=f'{target:x}' in known))
    missing=sorted({r['target'] for r in entries if not r['indexed']})
    report=dict(source_sha256=GAME_SHA,reference_report_sha256=digest(folder/'float-readers/table-refs.json'),base_index_sha256=digest(folder/'functions.json'),installations=installations,table_entries=entries,missing_entries=missing,
        limitation='Eight-slot windows at four observed installed table bases; not a proof of complete vtable length, class identity, or target object vtable+0x100 implementation. Installation and slot bytes verified independently of C.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'missing-entries.txt').write_text(''.join(x+'\n' for x in missing),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={n:digest(root/n) for n in ['report.json','missing-entries.txt']}),indent=2),encoding='utf-8')
    print(json.dumps(dict(slots=len(entries),missing=missing)))


if __name__=='__main__':main()
