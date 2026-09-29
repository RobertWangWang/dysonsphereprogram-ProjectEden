"""Resolve three original-owner gaps against verified supplemental function bodies."""
import json
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_frame_references import validate_cached as validate_frames


def validate_cached(folder,sha):
    root=folder/'supplement-context-evidence'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Context source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Context evidence differs')
    result=read_json(root/'report.json')
    for name,value in result['dependencies'].items():require(digest(folder/name)==value,'Context dependency differs')
    return result


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    inv=read_json(GENERATED/'native/inventory.json')
    for name in ('rail_api.dll','rail_wrapper.dll'):
        m=next(r for r in inv['files'] if Path(r['path']).name==name);folder=GENERATED/'native'/m['output'];path=Path(inv['game_directory'])/m['path'];require(digest(path)==m['sha256'],'Game changed');game=path.read_bytes()
        frames=validate_frames(folder,m['sha256']);gaps=[r for r in frames['entries'] if not r['related_functions']];deps={'frame-reference-evidence/manifest.json':digest(folder/'frame-reference-evidence/manifest.json')}
        records=[]
        for kind in ('relocation-callbacks','relocation-retry','flow-callbacks','flow-callbacks-2','flow-callbacks-3'):
            if not (folder/kind/'manifest.json').exists():continue
            marker=read_json(folder/kind/'manifest.json');require(marker['source_sha256']==m['sha256'],'Supplement source differs');require(digest(folder/kind/'report.json')==marker['files']['report.json'],'Supplement report differs')
            deps[kind+'/manifest.json']=digest(folder/kind/'manifest.json')
            records.extend((r,kind,marker) for r in read_json(folder/kind/'report.json')['functions'] if 'body_ranges_inclusive' in r)
        cs=Cs(CS_ARCH_X86,CS_MODE_32);rows=[]
        for gap in gaps:
            for ref in gap['target_references']:
                require(ref['owner'] is None and ref['actual_kind']=='direct-call','Unexpected unresolved context');pc=int(ref['site'],16)
                candidates=[(r,k,mark) for r,k,mark in records if any(int(lo,16)<=pc<=int(hi,16) for lo,hi in r['body_ranges_inclusive'])]
                require(len(candidates)==1,'Supplement owner not unique in declared bodies');r,kind,mark=candidates[0];entry=r['address'];asm=kind+'/functions/'+entry+'.asm';cfile=kind+'/functions/'+entry+'.c'
                for file in (asm,cfile):require(digest(folder/file)==mark['files'][file.removeprefix(kind+'/')],'Supplement file differs');deps[file]=digest(folder/file)
                seen=set();site=None;count=0
                for line in (folder/asm).read_text().splitlines():
                    address,data,_=line.split(' ',2);address=int(address,16);raw=bytes.fromhex(data);require(pe_read(game,address,len(raw))==raw,'Instruction bytes differ');decoded=list(cs.disasm(raw,address));require(len(decoded)==1 and decoded[0].size==len(raw),'Decode differs')
                    span=set(range(address,address+len(raw)));require(not seen&span,'Body overlap');seen|=span;count+=1
                    if address==pc:site=decoded[0]
                expected=set()
                for lo,hi in r['body_ranges_inclusive']:expected.update(range(int(lo,16),int(hi,16)+1))
                require(seen==expected and count==r['instruction_count'],'Body coverage differs');require(site and site.bytes.hex()==ref['bytes_hex'] and site.mnemonic=='call','Call site mismatch')
                require(site.bytes[0]==0xe8 and pc+5+int.from_bytes(site.bytes[1:],'little',signed=True)==int(gap['target'],16),'Call target differs')
                rows.append(dict(source=gap['source'],target=gap['target'],call_site=ref['site'],supplement_owner=entry,kind=kind,file=cfile,instructions=count,body_bytes=len(seen),ranges=r['body_ranges_inclusive']))
        root=folder/'supplement-context-evidence';root.mkdir(exist_ok=True);result=dict(source_sha256=m['sha256'],entries=rows,dependencies=deps,limitation='Unique containing body among inspected supplemental exports, not unique source-level ownership or a proof of exception dispatch. Original project observations and function indices remain unchanged.')
        (root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=m['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8');print(json.dumps(dict(module=name,entries=rows)))


if __name__=='__main__':main()
