"""以 PE DIR64 重定位槽中指向可执行节的地址，枚举未索引代码指针候选。"""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require


def main():
    folder=GENERATED/'native/UnityPlayer.dll';root=folder/'pointer-discovery';root.mkdir(exist_ok=True)
    path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(path)==GAME_SHA,'Game changed');game=path.read_bytes();pe=struct.unpack_from('<I',game,60)[0]
    require(struct.unpack_from('<H',game,pe+24)[0]==0x20b,'Expected PE32+')
    base=struct.unpack_from('<Q',game,pe+48)[0];count=struct.unpack_from('<H',game,pe+6)[0];optional=struct.unpack_from('<H',game,pe+20)[0]
    executable=[]
    for n in range(count):
        at=pe+24+optional+n*40;vsize,rva,rawsize,_=struct.unpack_from('<IIII',game,at+8);flags=struct.unpack_from('<I',game,at+36)[0]
        if flags&0x20000000:executable.append((base+rva,base+rva+min(vsize,rawsize)))
    known={f['address'] for f in read_json(folder/'functions.json')['functions']};dependencies={'functions.json':digest(folder/'functions.json')}
    for kind in ('crt-callbacks','exit-callbacks','reader-table-callbacks'):
        report=read_json(folder/kind/'report.json');marker=read_json(folder/kind/'manifest.json')
        require(marker['source_sha256']==GAME_SHA and digest(folder/kind/'report.json')==marker['files']['report.json'],'Supplement changed')
        known.update(f['address'] for f in report['functions'] if f['status']=='decompiled');dependencies[kind+'/manifest.json']=digest(folder/kind/'manifest.json')
    rva,size=struct.unpack_from('<II',game,pe+24+112+5*8);raw=pe_read(game,base+rva,size)
    cursor=0;entries=[];types={};seen_slots=set()
    while cursor<len(raw):
        page,length=struct.unpack_from('<II',raw,cursor);require(length>=8 and length%2==0 and cursor+length<=len(raw),'Invalid relocation block')
        for packed, in struct.iter_unpack('<H',raw[cursor+8:cursor+length]):
            kind=packed>>12;types[kind]=types.get(kind,0)+1
            if kind!=10:continue
            slot=base+page+(packed&4095);require(slot not in seen_slots,'Duplicate relocation slot');seen_slots.add(slot)
            target=struct.unpack('<Q',pe_read(game,slot,8))[0]
            if any(a<=target<b for a,b in executable):entries.append(dict(slot=f'{slot:x}',target=f'{target:x}',indexed=f'{target:x}' in known))
        cursor+=length
    missing=sorted({r['target'] for r in entries if not r['indexed']})
    report=dict(source_sha256=GAME_SHA,dependencies=dependencies,relocation_directory=dict(address=f'{base+rva:x}',bytes=size,types=types),
        executable_ranges_exclusive=[[f'{a:x}',f'{b:x}'] for a,b in executable],code_pointer_slots=len(entries),unique_targets=len({r['target'] for r in entries}),
        table_entries=[r for r in entries if not r['indexed']],missing_entries=missing,
        limitation='Relocated absolute code pointers are discovery candidates, not proven function starts. Existing body ownership, data, shared/interior labels and decompiler warnings must be checked. Relative pointers and dynamic registrations are outside this pass.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'missing-entries.txt').write_text(''.join(x+'\n' for x in missing),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={n:digest(root/n) for n in ['report.json','missing-entries.txt']}),indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k not in ('table_entries','missing_entries','dependencies')}));print(f'{len(missing)} unindexed unique targets')


if __name__=='__main__':main()
