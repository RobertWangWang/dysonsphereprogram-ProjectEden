"""从固定 Unity PE 提取 name/descriptor/id 三元组；不把磁盘占位名称当作注册类型。"""
import argparse
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

DESCRIPTORS=[0x181c42110,0x181c42160,0x181c42480]


def cstring(game,address):
    result=bytearray()
    for n in range(256):
        byte=pe_read(game,address+n,1)[0]
        if byte==0:return result.decode('ascii')
        require(32<=byte<127,'Non-printable name');result.append(byte)
    raise ValueError('Name too long')


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--query');parser.add_argument('--references',type=Path);args=parser.parse_args()
    folder=GENERATED/'native/UnityPlayer.dll';root=folder/'property-catalog'
    path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(path)==GAME_SHA,'Game changed');game=path.read_bytes()
    if args.query:
        marker=read_json(root/'manifest.json');require(marker['source_sha256']==GAME_SHA,'Catalog baseline changed')
        for n,sha in marker['files'].items():require(digest(root/n)==sha,'Catalog artifact changed')
        matches=[r for r in read_json(root/'catalog.json')['properties'] if args.query.lower() in r['name'].lower()]
        for row in matches:print(json.dumps(row,ensure_ascii=False))
        print(f'{len(matches)} matches');return
    pe=struct.unpack_from('<I',game,60)[0];count=struct.unpack_from('<H',game,pe+6)[0];optional=struct.unpack_from('<H',game,pe+20)[0]
    sections=[]
    for n in range(count):
        at=pe+24+optional+n*40;_,rva,size,offset=struct.unpack_from('<IIII',game,at+8)
        sections.append((0x180000000+rva,size,offset))
    # Enumerate every initialized descriptor with the same three-pointer marker,
    # instead of limiting discovery to the three original reader return values.
    marker_pattern=struct.pack('<QQQ',0x18193fe28,0x18192fbb0,0x18193fe38)
    discovered=[]
    for base,size,offset in sections:
        raw=game[offset:offset+size];cursor=0
        while True:
            pos=raw.find(marker_pattern,cursor)
            if pos<0:break
            cursor=pos+1;begin=pos-16
            if begin<0 or begin+80>len(raw) or (base+begin)%8:continue
            if raw[begin:begin+16]!=bytes(16):continue
            discovered.append(base+begin)
    require(set(DESCRIPTORS)<=set(discovered),'Original descriptors missing from expanded discovery')
    rows=[];reference_counts={};descriptors=[]
    for address in sorted(set(discovered)):
        slots=[]
        for base,size,offset in sections:
            raw=game[offset:offset+size];cursor=0
            while True:
                pos=raw.find(struct.pack('<Q',address),cursor)
                if pos<0:break
                cursor=pos+1
                if (base+pos)%8:continue
                slots.append(base+pos)
                if pos<8 or pos+16>size:continue
                name_ptr=struct.unpack_from('<Q',raw,pos-8)[0];number=struct.unpack_from('<Q',raw,pos+8)[0]
                if number>0xffffffff:continue
                try:name=cstring(game,name_ptr)
                except (ValueError,UnicodeDecodeError):continue
                if not name or not any(ch.isalpha() for ch in name):continue
                rows.append(dict(record=f'{base+pos-8:x}',name_address=f'{name_ptr:x}',name=name,descriptor=f'{address:x}',id=number,raw_hex=raw[pos-8:pos+16].hex()))
        reference_counts[f'{address:x}']=len(slots)
        raw=pe_read(game,address,80)
        strings={f'{off:x}':cstring(game,struct.unpack_from('<Q',raw,off)[0]) for off in (16,24,32)}
        require(strings=={'10':'[UNREGISTERED]','18':'','20':'undefined'},'Descriptor baseline markers changed')
        descriptors.append(dict(address=f'{address:x}',disk_bytes_hex=raw.hex(),disk_strings=strings,aligned_pointer_slots=[f'{a:x}' for a in slots]))
    rows.sort(key=lambda r:int(r['record'],16));groups=[]
    for row in rows:
        if not groups or int(row['record'],16)!=int(groups[-1][-1]['record'],16)+24:groups.append([])
        groups[-1].append(row)
    summary=[dict(begin=g[0]['record'],records=len(g),ids=[r['id'] for r in g],consecutive_from_zero=[r['id'] for r in g]==list(range(len(g)))) for g in groups]
    root.mkdir(exist_ok=True)
    prior_rows=read_json(root/'catalog.json')['properties'] if (root/'catalog.json').exists() else []
    by_address={r['record']:r for r in rows}
    require(len(by_address)==len(rows),'Duplicate property record')
    require(all(by_address.get(r['record'])==r for r in prior_rows),'Previously catalogued record changed or lost')
    report=dict(source_sha256=GAME_SHA,descriptor_discovery='all aligned zero-prefix descriptor records with the same initial three string pointers',descriptors=descriptors,properties=rows,groups=summary,
        previous_records_preserved=len(prior_rows),
        limitation='Marker-matching descriptor candidates and aligned static name/descriptor/id triples selected by printable ASCII names and uint32 ids. Exact bytes and strings verified, but registration execution, complete table boundaries, field IDs as global IDs, and descriptor runtime names are not established. Descriptors with different initialization patterns are outside this scan.')
    (root/'catalog.json').write_text(json.dumps(report,indent=2,ensure_ascii=False),encoding='utf-8')
    files=['catalog.json']
    reference_path=args.references or (root/'references.json' if (root/'references.json').exists() else None)
    if reference_path:
        refs=read_json(reference_path);require(refs['source_sha256']==GAME_SHA,'Reference source differs')
        require({t['address'] for t in refs['tables']}=={f'{a:x}' for a in DESCRIPTORS}|{f'{a+32:x}' for a in DESCRIPTORS},'Incomplete reference scope')
        for t in refs['tables']:
            for ref in t['references']:
                if 'bytes' in ref:require(pe_read(game,int(ref['from'],16),len(bytes.fromhex(ref['bytes']))).hex()==ref['bytes'],'Reference instruction differs')
        (root/'references.json').write_text(json.dumps(refs,indent=2),encoding='utf-8');files.append('references.json')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={n:digest(root/n) for n in files}),indent=2),encoding='utf-8')
    print(json.dumps(dict(descriptors=len(descriptors),descriptors_with_properties=len({r['descriptor'] for r in rows}),properties=len(rows),groups=len(groups),previous_records_preserved=len(prior_rows),nonsequential_groups=sum(not g['consecutive_from_zero'] for g in summary)),ensure_ascii=False))


if __name__=='__main__':main()
