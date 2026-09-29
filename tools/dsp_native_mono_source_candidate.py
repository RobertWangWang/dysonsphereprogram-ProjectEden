"""Verify cached upstream candidate provenance without claiming source equivalence."""
import json
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_select import selected_folder
from dsp_native_source_match import pe_read,require
from dsp_pdb_identity import verify_pdb


def validate_cached(folder,sha):
    root=folder/'mono-source-candidate'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Mono candidate source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Mono source candidate differs')
    return read_json(root/'verification.json')


def main():
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='mono-2.0-bdwgc.dll')
    folder=selected_folder(GENERATED/'native',module);root=folder/'mono-source-candidate';report=read_json(root/'report.json')
    binary=Path(inv['game_directory'])/module['path'];pdb=GENERATED/'native/symbols/mono-2.0-bdwgc.pdb';identity=verify_pdb(pdb,binary)
    require(identity==report['identity'] and identity['binary_sha256']==module['sha256'],'Candidate identity differs')
    for name,info in report['files'].items():
        require(digest(root/name)==info['sha256'],'Candidate download changed')
        require(info['url'].startswith('https://raw.githubusercontent.com/Unity-Technologies/mono/'+report['commit']+'/'),'Candidate URL not pinned')
    require(digest(root/'checksum.tsv')==report['checksum_tsv_sha256'],'DIA evidence changed')
    checksum_rows=[line.split('\t') for line in (root/'checksum.tsv').read_text().splitlines() if line.strip()]
    require(len(checksum_rows)==1 and len(checksum_rows[0])==4 and checksum_rows[0][1:3]==['0',''],'Expected absent source checksum')
    game=binary.read_bytes();values={0x1806b2c34:b'6.13.0\0',0x1806b2be0:b'Visual Studio built mono\0'}
    for address,data in values.items():require(pe_read(game,address,len(data))==data,'Runtime literal changed')
    result=dict(source_sha256=module['sha256'],status='candidate-only',runtime_version='6.13.0',build_label='Visual Studio built mono',
                literal_addresses=[f'{a:x}' for a in values],runtime_build_info_bytes_hex=pe_read(game,0x18029b030,50).hex(),
                source_checksum_type=0,source_checksum_hex='',candidate_commit=report['commit'],candidate_file_sha256=report['files']['method-to-ir.c']['sha256'],
                candidate_lines=len((root/'method-to-ir.c').read_text().splitlines()),
                limitation='Pinned official upstream comparison candidate with source checksum absent in matched PDB. Similar source line locations are not proof of exact build revision or compiler equivalence. Original C recovery count unchanged.')
    (root/'verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    names=['report.json','verification.json','checksum.tsv',*report['files']]
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={n:digest(root/n) for n in names}),indent=2),encoding='utf-8')
    print(json.dumps(result))


if __name__=='__main__':main()
