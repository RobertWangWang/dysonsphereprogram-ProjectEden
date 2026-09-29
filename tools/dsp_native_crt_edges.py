"""审计新增 CRT 回调的外部控制流边、IAT 尾跳转及潜在未索引目标。"""
import json
import re
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require


def validate_cached(folder,sha,kind='crt-edges'):
    root=folder/kind
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'CRT edge baseline changed')
    for name,value in marker['files'].items():require(digest(root/name)==value,'CRT edge evidence changed')
    report=read_json(root/'report.json')
    require(report['crt_manifest_sha256']==digest(folder/'crt-callbacks/manifest.json') and report['base_index_sha256']==digest(folder/'functions.json'),'CRT edge source changed')
    if kind=='exit-edges':require(report['exit_manifest_sha256']==digest(folder/'exit-callbacks/manifest.json'),'Exit edge source changed')
    return report


def main():
    import argparse
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--exit-callbacks',action='store_true');args=parser.parse_args()
    callback_kind='exit-callbacks' if args.exit_callbacks else 'crt-callbacks'
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64,CS_GRP_CALL,CS_GRP_JUMP
    from capstone.x86 import X86_OP_IMM,X86_OP_MEM,X86_REG_RIP
    from dsp_native_sha512_unwind import imports
    from dsp_native_crt_callbacks import validate_cached
    folder=GENERATED/'native/UnityPlayer.dll';game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game changed');game=game_path.read_bytes()
    crt_rows=validate_cached(folder,GAME_SHA)
    rows=validate_cached(folder,GAME_SHA,callback_kind);require(len(rows)==(195 if args.exit_callbacks else 2710),'Callback scope changed')
    known={f['address'] for f in read_json(folder/'functions.json')['functions']}|{f['address'] for f in crt_rows+rows}
    imported=imports(game);decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True
    edges=[];unresolved=[]
    for f in rows:
        ranges=[(int(a,16),int(b,16)) for a,b in f['body_ranges_inclusive']]
        for line in (folder/callback_kind/'functions'/(f['address']+'.asm')).read_text().splitlines():
            address,hexbytes,_=line.split(' ',2);raw=bytes.fromhex(hexbytes);pc=int(address,16)
            ins=list(decoder.disasm(raw,pc));require(len(ins)==1 and ins[0].size==len(raw),'Independent instruction boundary differs')
            i=ins[0]
            if not(i.group(CS_GRP_CALL) or i.group(CS_GRP_JUMP)):continue
            op=i.operands[0];row=dict(source=f['address'],site=address,instruction=i.mnemonic)
            if op.type==X86_OP_IMM:
                target=op.imm
                if any(a<=target<=b for a,b in ranges):continue
                row.update(kind='direct',target=f'{target:x}',target_indexed=f'{target:x}' in known);edges.append(row)
            elif op.type==X86_OP_MEM and op.mem.base==X86_REG_RIP:
                slot=i.address+i.size+op.mem.disp
                if slot in imported:
                    dll,name=imported[slot];row.update(kind='import-slot',slot=f'{slot:x}',dll=dll,symbol=name);edges.append(row)
                else:row.update(kind='indirect-memory',slot=f'{slot:x}');unresolved.append(row)
            else:row.update(kind='indirect',operand=i.op_str);unresolved.append(row)
    special=[e for e in edges if e['site']=='180045c6b']
    if not args.exit_callbacks:
        require(len(special)==1 and special[0]['kind']=='import-slot' and special[0]['symbol']=='InitializeSListHead','Tail-import identity differs')
        require(pe_read(game,0x180045c64,7)==bytes.fromhex('488d0d1581c701'),'SList argument setup changed')
    missing=sorted({e['target'] for e in edges if e['kind']=='direct' and not e['target_indexed']})
    root=folder/('exit-edges' if args.exit_callbacks else 'crt-edges');root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    report=dict(status=callback_kind+'-external-edges-audited',source_sha256=GAME_SHA,
        crt_manifest_sha256=digest(folder/'crt-callbacks/manifest.json'),base_index_sha256=digest(folder/'functions.json'),
        edges=edges,unresolved_indirect=unresolved,missing_direct_targets=missing,
        limitation='Static instruction and PE import-slot identities. No DLL loader/API execution or import-hook assumptions verified. Missing exact targets are discovery candidates, possibly interior/shared entries; unknown indirect edges remain.')
    if special:report['slist_initializer']=dict(entry='180045c64',argument='181cbdd80',tail_jump=special[0],stack_adjustment_bytes=0)
    if args.exit_callbacks:
        report['exit_manifest_sha256']=digest(folder/'exit-callbacks/manifest.json')
        warning_sites=[]
        import_wrappers=[]
        for f in rows:
            for warning in f.get('warnings',[]):
                match=re.search(r'Could not recover jumptable at 0x([0-9a-fA-F]+)',warning)
                if match:
                    site=f'{int(match[1],16):x}'
                    matches=[e for e in edges+unresolved if e['source']==f['address'] and e['site']==site]
                    require(len(matches)==1,'Warning site does not have exactly one external edge')
                    warning_sites.append(matches[0])
                    edge=matches[0];entry=int(f['address'],16)
                    require(edge['kind']=='import-slot' and edge['instruction']=='jmp','Unexpected warning control transfer')
                    raw=b''.join(bytes.fromhex(line.split(' ',2)[1]) for line in (folder/callback_kind/'functions'/(f['address']+'.asm')).read_text().splitlines())
                    require(f['body_ranges_inclusive']==[[f['address'],f'{entry+len(raw)-1:x}']],'Wrapper body not contiguous')
                    require(raw==pe_read(game,entry,len(raw)),'Wrapper source bytes differ')
                    wrapper=dict(entry=f['address'],tail_jump=edge,stack_adjustment_bytes=0,conditional=False)
                    if edge['symbol']=='DeleteCriticalSection':
                        require(len(raw)==14 and raw[:3]==bytes.fromhex('488d0d') and raw[7:10]==bytes.fromhex('48ff25'),'Critical-section wrapper shape differs')
                        wrapper.update(argument_kind='global-address',argument_address=f'{entry+7+int.from_bytes(raw[3:7],"little",signed=True):x}')
                    elif edge['symbol']=='CloseHandle':
                        require(len(raw)==20 and raw[:2]==bytes.fromhex('8b05') and raw[6:9]==bytes.fromhex('488b0d') and raw[13:16]==bytes.fromhex('48ff25'),'Handle wrapper shape differs')
                        wrapper.update(argument_kind='global-qword-value',argument_address=f'{entry+13+int.from_bytes(raw[9:13],"little",signed=True):x}',
                            additional_eax_dword_read=f'{entry+6+int.from_bytes(raw[2:6],"little",signed=True):x}')
                    else:raise ValueError('Unexpected cleanup API')
                    import_wrappers.append(wrapper)
        report['jumptable_warning_sites']=warning_sites
        report['import_wrappers']=import_wrappers
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'missing-direct-targets.txt').write_text(''.join(a+'\n' for a in missing),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print(f'{len(edges)} external edges, {len(unresolved)} unresolved indirect edges, {len(missing)} unindexed direct targets')
    if special:print(json.dumps(special[0]))
    if args.exit_callbacks:print(json.dumps(dict(warning_sites=len(warning_sites),warning_imports=sorted({e.get('dll','')+'!'+e.get('symbol','') for e in warning_sites}))))


if __name__=='__main__':main()
