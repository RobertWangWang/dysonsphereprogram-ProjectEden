"""Export matched PDB line/variable metadata for Mono's giant JIT function."""
import argparse
import json
import xml.etree.ElementTree as ET
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_pdb_identity import verify_pdb
from dsp_native_source_match import require
from dsp_native_select import selected_folder
from dsp_native_mono_dispatch import validate_cached as validate_dispatch
from dsp_native_mono_continuations import validate_cached as validate_continuations


def validate_cached(folder,sha):
    root=folder/'mono-pdb-lines'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Mono PDB lines source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Mono PDB line evidence differs')
    report=read_json(root/'report.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Mono PDB line dependency differs')
    return report


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--xml',type=Path,required=True);parser.add_argument('--exporter',type=Path,required=True);args=parser.parse_args()
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='mono-2.0-bdwgc.dll')
    folder=selected_folder(GENERATED/'native',module);pdb=GENERATED/'native/symbols/mono-2.0-bdwgc.pdb';identity=verify_pdb(pdb,Path(inv['game_directory'])/module['path'])
    require(identity['binary_sha256']==module['sha256'],'Binary source changed')
    dispatch=validate_dispatch(folder,module['sha256']);continuations=validate_continuations(folder,module['sha256']);require(dispatch and continuations,'Missing machine evidence')
    function=None;root_attributes=None
    for event,element in ET.iterparse(args.xml,events=('start','end')):
        if event=='start' and element.tag=='pdb':root_attributes=dict(element.attrib)
        if event=='end' and element.tag=='function':
            if element.get('name')=='mono_method_to_ir':function=element;break
            element.clear()
    require(function is not None and root_attributes['guid'].strip('{}').upper()==identity['pdb']['guid'] and int(root_attributes['age'])==identity['pdb']['age'],'XML identity/function differs')
    entry=0x180000000+int(function.get('address'),16);length=int(function.get('length'),16)
    require(entry==0x1802b5740,'PDB function address differs')
    lines=[];variables=[]
    for element in function:
        if element.tag=='line_number':
            row=element.attrib;start=0x180000000+int(row['addr'],16);size=int(row['length'])
            require(entry<=start and start+size<=entry+length,'PDB line outside function record')
            lines.append(dict(address=f'{start:x}',length=size,source=row['source_file'],line_start=int(row['start']),line_end=int(row['end'])))
        elif element.tag=='stack_variable':variables.append(dict(element.attrib))
    covered=set()
    for row in lines:covered.update(range(int(row['address'],16),int(row['address'],16)+row['length']))
    assembled=set();instruction_starts=[]
    for text in (folder/'mono-continuation-evidence/combined.asm').read_text().splitlines():
        address,data,_=text.split(' ',2);pc=int(address,16);instruction_starts.append(pc);assembled.update(range(pc,pc+len(bytes.fromhex(data))))
    mappings=[]
    for table in dispatch['dispatches']:
        for target,values in table['target_groups'].items():
            address=int(target,16);matches=[row for row in lines if int(row['address'],16)<=address<int(row['address'],16)+row['length']]
            mappings.append(dict(table=table['name'],target=target,inputs=values,source_locations=matches))
    parameters=[v for v in variables if v['kind']=='Parameter']
    dependencies={n:digest(folder/n) for n in ('../symbols/mono-2.0-bdwgc.pdb','mono-dispatch-behavior/manifest.json','mono-continuation-evidence/manifest.json')}
    report=dict(source_sha256=module['sha256'],identity=identity,xml_sha256=digest(args.xml),exporter_sha256=digest(args.exporter),function=dict(function.attrib),
                pdb_end_exclusive=f'{entry+length:x}',line_records=len(lines),variable_records=len(variables),parameters=parameters,source_files=sorted({r['source'] for r in lines}),
                mapped_instruction_starts=sum(pc in covered for pc in instruction_starts),instruction_starts=len(instruction_starts),mapped_code_bytes=len(covered&assembled),code_bytes=len(assembled),
                target_groups=len(mappings),mapped_target_groups=sum(bool(m['source_locations']) for m in mappings),dependencies=dependencies,
                limitation='PDB GUID/age and DLL hash matched. PDB source paths and line numbers are build metadata, not recovered source contents or a proven upstream revision. Function length can include trailing tables; do not equate symbol extent with executable bytes. Variable offsets preserved as reported, not reinterpreted as current RSP/RBP offsets.')
    out=folder/'mono-pdb-lines';out.mkdir(exist_ok=True)
    for name,data in (('report.json',report),('lines.json',lines),('variables.json',variables),('switch-lines.json',mappings)):(out/name).write_text(json.dumps(data,indent=2,ensure_ascii=False),encoding='utf-8')
    ET.ElementTree(function).write(out/'function.xml',encoding='utf-8',xml_declaration=True)
    md=['# mono_method_to_ir: matched PDB switch locations','','Build metadata only; source contents and revision have not been recovered.','','| Table | Target | Inputs | Source lines |','| --- | --- | --- | --- |']
    for m in mappings:
        locations='; '.join(f"{r['source']}:{r['line_start']}" for r in m['source_locations']) or 'unmapped'
        md.append(f"| {m['table']} | `{m['target']}` | {', '.join(map(str,m['inputs']))} | {locations} |")
    (out/'switch-lines.md').write_text('\n'.join(md)+'\n',encoding='utf-8')
    files=['report.json','lines.json','variables.json','switch-lines.json','function.xml','switch-lines.md']
    (out/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={n:digest(out/n) for n in files}),indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k not in ('identity','dependencies','parameters')},ensure_ascii=False))


if __name__=='__main__':main()
