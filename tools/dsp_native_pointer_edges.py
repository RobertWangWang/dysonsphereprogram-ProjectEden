"""独立审计重定位补充函数的控制转移和缺失直接目标。"""
import collections
import json
import re
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require
from dsp_native_crt_callbacks import validate_cached


def validate_edge_cached(folder,sha):
    root=folder/'pointer-edges'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Pointer edge baseline changed')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Pointer edge artifact changed')
    report=read_json(root/'report.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Pointer edge source changed')
    return report


def main():
    import argparse
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--references',type=Path);args=parser.parse_args()
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64,CS_GRP_CALL,CS_GRP_JUMP
    from capstone.x86 import X86_OP_IMM,X86_OP_MEM,X86_REG_RIP
    from dsp_native_sha512_unwind import imports
    folder=GENERATED/'native/UnityPlayer.dll';root=folder/'pointer-edges';root.mkdir(exist_ok=True)
    path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(path)==GAME_SHA,'Game changed');game=path.read_bytes();known={f['address'] for f in read_json(folder/'functions.json')['functions']}
    dependencies={'functions.json':digest(folder/'functions.json')};rows=[]
    for kind in ('crt-callbacks','exit-callbacks','reader-table-callbacks','pointer-callbacks'):
        supplement=validate_cached(folder,GAME_SHA,kind);known.update(f['address'] for f in supplement);dependencies[kind+'/manifest.json']=digest(folder/kind/'manifest.json')
        if kind=='pointer-callbacks':rows=supplement
    require(len(rows)==4225,'Input scope changed');decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True;imported=imports(game)
    edges=[];all_transfers={};instructions=0
    for f in rows:
        ranges=[(int(a,16),int(b,16)) for a,b in f['body_ranges_inclusive']]
        for line in (folder/'pointer-callbacks/functions'/(f['address']+'.asm')).read_text().splitlines():
            site,hexbytes,_=line.split(' ',2);pc=int(site,16);raw=bytes.fromhex(hexbytes)
            require(pe_read(game,pc,len(raw))==raw,'Source bytes differ');ins=list(decoder.disasm(raw,pc))
            require(len(ins)==1 and ins[0].size==len(raw),'Instruction boundary differs');i=ins[0];instructions+=1
            if not(i.group(CS_GRP_CALL) or i.group(CS_GRP_JUMP)):continue
            op=i.operands[0];row=dict(source=f['address'],site=site,bytes_hex=hexbytes,instruction=i.mnemonic)
            if op.type==X86_OP_IMM:
                row.update(kind='direct',target=f'{op.imm:x}',target_indexed=f'{op.imm:x}' in known,internal=any(a<=op.imm<=b for a,b in ranges))
            elif op.type==X86_OP_MEM and op.mem.base==X86_REG_RIP:
                slot=i.address+i.size+op.mem.disp;row.update(kind='indirect-memory',slot=f'{slot:x}')
                if slot in imported:
                    dll,symbol=imported[slot];row.update(kind='import-slot',dll=dll,symbol=symbol)
            else:row.update(kind='indirect',operand=i.op_str)
            all_transfers[(f['address'],site)]=row
            if not row.get('internal'):edges.append(row)
    warnings=[]
    for f in rows:
        for text in f.get('warnings',[]):
            match=re.search(r'Could not recover jumptable at 0x([0-9a-fA-F]+)',text)
            if match:
                site=f'{int(match[1],16):x}';edge=all_transfers.get((f['address'],site))
                warnings.append(dict(source=f['address'],site=site,edge=edge))
    missing=sorted({e['target'] for e in edges if e['kind']=='direct' and not e['target_indexed']})
    report=dict(source_sha256=GAME_SHA,dependencies=dependencies,instructions=instructions,edges=edges,jumptable_warnings=warnings,
        direct_entries=[e for e in edges if e['kind']=='direct' and not e['target_indexed']],missing_entries=missing,
        edge_counts=dict(collections.Counter(e['kind'] for e in edges)),warning_edge_counts=dict(collections.Counter(w['edge']['kind'] if w['edge'] else 'no-matching-transfer' for w in warnings)),
        limitation='Direct targets and PE import identities verified; register/memory-indirect targets remain unresolved. A virtual-looking operand is not a proven vtable identity. New target addresses may be interior/shared entries; Ghidra ownership must be checked.')
    reference_path=args.references or (root/'references.json' if (root/'references.json').exists() else None)
    if reference_path:
        refs=read_json(reference_path);require(refs['source_sha256']==GAME_SHA,'Reference baseline changed')
        lookup={r['address']:r for r in refs['tables']};shared=[]
        for warning in warnings:
            if warning['edge'] is not None:continue
            item=lookup[warning['site']];raw=bytes.fromhex(item['target_bytes']);pc=int(item['address'],16)
            require(pe_read(game,pc,len(raw))==raw,'Shared warning bytes differ');ins=list(decoder.disasm(raw,pc))
            require(len(ins)==1 and ins[0].size==len(raw) and ins[0].mnemonic=='jmp','Shared warning is not decoded jump')
            incoming=[e for e in edges if e['source']==warning['source'] and e.get('target')==item['target_owner']]
            require(incoming,'No direct edge into warning owner')
            shared.append(dict(source=warning['source'],site=warning['site'],owner=item['target_owner'],instruction=ins[0].mnemonic+' '+ins[0].op_str,incoming=incoming))
        report['shared_warning_origins']=shared
        start=0x1810eb718;raw=pe_read(game,start,27);seq=list(decoder.disasm(raw,start))
        require([i.mnemonic for i in seq]==['lea','mov','call','mov'] and sum(i.size for i in seq)==27,'Dynamic loader sequence differs')
        require(raw[:3]==bytes.fromhex('488d0d') and raw[7:10]==bytes.fromhex('488905') and raw[14:16]==bytes.fromhex('ff15') and raw[20:23]==bytes.fromhex('488905'),'Dynamic loader operands differ')
        name_address=start+7+int.from_bytes(raw[3:7],'little',signed=True)
        require(pe_read(game,name_address,19)==b'wglSwapIntervalEXT\0','Dynamic API name differs')
        import_slot=start+20+int.from_bytes(raw[16:20],'little',signed=True)
        require(imported[import_slot]==('OPENGL32.dll','wglGetProcAddress'),'Dynamic resolver import differs')
        require(start+27+int.from_bytes(raw[23:27],'little',signed=True)==0x181cdc170,'Dynamic destination slot differs')
        require(any(r['from']=='1810eb72c' and r['type']=='WRITE' for r in lookup['181cdc170']['references']),'Missing global write reference')
        report['dynamic_loader']=dict(source='1809bc9b0',tail_site='1809bc9c2',slot='181cdc170',initializer='1810eb670',sequence_start=f'{start:x}',bytes_hex=raw.hex(),name_address=f'{name_address:x}',name='wglSwapIntervalEXT',resolver='OPENGL32.dll!wglGetProcAddress',resolver_import_slot=f'{import_slot:x}',limitation='Identifies the requested symbol and result store on this initialization path; does not prove loading success, actual runtime pointer, or absence of later writes.')
        (root/'references.json').write_text(json.dumps(refs,indent=2),encoding='utf-8')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(root/'missing-entries.txt').write_text(''.join(a+'\n' for a in missing),encoding='utf-8')
    names=['report.json','missing-entries.txt']+(['references.json'] if reference_path else [])
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={n:digest(root/n) for n in names}),indent=2),encoding='utf-8')
    print(json.dumps(dict(instructions=instructions,edges=report['edge_counts'],warning_edges=report['warning_edge_counts'],missing_targets=len(missing),first_missing=missing[:12])))


if __name__=='__main__':main()
