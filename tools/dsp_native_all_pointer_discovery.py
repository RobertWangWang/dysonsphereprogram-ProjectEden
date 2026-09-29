"""审计全部原生模块的 PE32/PE32+ 重定位代码指针，保留缺失候选。"""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_select import selected_folder


def main():
    basefolder=GENERATED/'native';inventory=read_json(basefolder/'inventory.json');summaries=[]
    for module in inventory['files']:
        folder=selected_folder(basefolder,module);root=folder/'relocation-audit';root.mkdir(exist_ok=True)
        path=Path(inventory['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Game module changed');game=path.read_bytes()
        pe=struct.unpack_from('<I',game,60)[0];magic=struct.unpack_from('<H',game,pe+24)[0];require(magic in (0x10b,0x20b),'Unsupported PE')
        width=8 if magic==0x20b else 4;kind=10 if width==8 else 3
        imagebase=struct.unpack_from('<Q' if width==8 else '<I',game,pe+24+(24 if width==8 else 28))[0]
        count=struct.unpack_from('<H',game,pe+6)[0];optional=struct.unpack_from('<H',game,pe+20)[0];executable=[]
        for n in range(count):
            at=pe+24+optional+n*40;vsize,rva,rawsize,_=struct.unpack_from('<IIII',game,at+8);flags=struct.unpack_from('<I',game,at+36)[0]
            if flags&0x20000000:executable.append((imagebase+rva,imagebase+rva+min(vsize,rawsize)))
        marker=read_json(folder/'decompile-manifest.json');require(marker['source_sha256']==module['sha256'] and marker['files']['functions.json']==digest(folder/'functions.json'),'Base index changed')
        known={int(f['address'],16) for f in read_json(folder/'functions.json')['functions']};dependencies={'functions.json':digest(folder/'functions.json')}
        for supplement in ('crt-callbacks','exit-callbacks','reader-table-callbacks','pointer-callbacks','direct-callbacks'):
            if not (folder/supplement/'manifest.json').exists():continue
            manifest=read_json(folder/supplement/'manifest.json');require(manifest['source_sha256']==module['sha256'] and digest(folder/supplement/'report.json')==manifest['files']['report.json'],'Supplement mismatch')
            known.update(int(f['address'],16) for f in read_json(folder/supplement/'report.json')['functions'] if f['status']=='decompiled');dependencies[supplement+'/manifest.json']=digest(folder/supplement/'manifest.json')
        rva,size=struct.unpack_from('<II',game,pe+24+(112 if width==8 else 96)+40)
        raw=pe_read(game,imagebase+rva,size) if size else b'';cursor=0;entries=[];types={};slots=set()
        while cursor<len(raw):
            page,length=struct.unpack_from('<II',raw,cursor);require(length>=8 and length%2==0 and cursor+length<=len(raw),'Malformed relocation block')
            for packed, in struct.iter_unpack('<H',raw[cursor+8:cursor+length]):
                actual=packed>>12;types[actual]=types.get(actual,0)+1
                if actual!=kind:continue
                slot=imagebase+page+(packed&4095);require(slot not in slots,'Duplicate relocation');slots.add(slot)
                target=int.from_bytes(pe_read(game,slot,width),'little')
                if any(a<=target<b for a,b in executable):entries.append(dict(slot=f'{slot:x}',target=f'{target:x}',indexed=target in known))
            cursor+=length
        missing=sorted({e['target'] for e in entries if not e['indexed']},key=lambda a:int(a,16))
        report=dict(source_sha256=module['sha256'],module=module['path'],pointer_size=width,dependencies=dependencies,relocation_types=types,
            code_pointer_slots=len(entries),unique_code_targets=len({e['target'] for e in entries}),table_entries=[e for e in entries if not e['indexed']],missing_entries=missing,
            limitation='Absolute relocated pointers into initialized executable bytes are candidates only. Existing function ownership, possible data and internal labels need Ghidra review. Relative pointers and dynamic registration remain outside scope.')
        (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(root/'missing-entries.txt').write_text(''.join(a+'\n' for a in missing),encoding='utf-8')
        (root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={n:digest(root/n) for n in ('report.json','missing-entries.txt')}),indent=2),encoding='utf-8')
        row=dict(module=module['path'],output=folder.name,source_sha256=module['sha256'],pointer_size=width,code_pointer_slots=len(entries),unique_code_targets=report['unique_code_targets'],missing_candidates=len(missing),manifest_sha256=digest(root/'manifest.json'));summaries.append(row);print(json.dumps(row))
    (basefolder/'relocation-audit-summary.json').write_text(json.dumps(dict(inventory_sha256=digest(basefolder/'inventory.json'),modules=summaries),indent=2),encoding='utf-8')


if __name__=='__main__':main()
