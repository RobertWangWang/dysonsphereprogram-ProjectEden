"""Compare PDB opcode/type enums and table locations with candidate lexical cases."""
import argparse
import bisect
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_select import selected_folder
from dsp_native_source_match import require
from dsp_native_mono_lines import validate_cached as validate_lines
from dsp_native_mono_source_candidate import validate_cached as validate_candidate
from dsp_native_mono_dispatch import validate_cached as validate_dispatch


def validate_cached(folder,sha,nested=False):
    root=folder/('mono-type-case-comparison' if nested else 'mono-case-comparison')
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Mono case source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Mono case evidence differs')
    report=read_json(root/'report.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Mono case dependency differs')
    return report


def validate_nested_cached(folder,sha):return validate_cached(folder,sha,True)


def parse_cases(source,selector='il_op'):
    # Preserve positions/lines while masking comments and literal contents.
    pattern=r'/\*[\s\S]*?\*/|//[^\n]*|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\''
    clean=re.sub(pattern,lambda m:re.sub(r'[^\n]',' ',m.group()),source)
    clean=re.sub(r'^\s*#[^\n]*',lambda m:re.sub(r'[^\n]',' ',m.group()),clean,flags=re.M)
    candidates=list(re.finditer(r'switch\s*\(\s*'+re.escape(selector)+r'\s*\)\s*\{',clean));require(bool(candidates),'Missing selector switch')
    start=candidates[0].end();depth=1;groups=[];last_label_end=None;end=None
    for m in re.finditer(r'case\s+([A-Za-z_]\w*)\s*:|\bdefault\s*:|[{}]',clean[start:]):
        lo=start+m.start();hi=start+m.end();token=m.group()
        if token=='{':depth+=1;continue
        if token=='}':
            depth-=1
            if depth==0:end=hi;break
            continue
        if depth!=1:continue
        label=m.group(1) or 'default';line=source.count('\n',0,lo)+1
        if last_label_end is None or clean[last_label_end:lo].strip():groups.append(dict(line_start=line,labels=[],label_lines=[]))
        groups[-1]['labels'].append(label);groups[-1]['label_lines'].append(line);last_label_end=hi
    require(end is not None and groups,'Unclosed case switch')
    require(all(start<=m.start()<end for m in candidates[1:]),'Other non-nested il_op switch needs review')
    end_line=source.count('\n',0,end)+1
    for i,group in enumerate(groups):group['line_end']=(groups[i+1]['line_start']-1 if i+1<len(groups) else end_line)
    return groups


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--xml',type=Path,required=True);parser.add_argument('--nested',action='store_true');args=parser.parse_args()
    table_name='nested' if args.nested else 'main';selector='ro_type' if args.nested else 'il_op';enum_name='MonoTypeEnum' if args.nested else 'MonoOpcodeEnum'
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='mono-2.0-bdwgc.dll');folder=selected_folder(GENERATED/'native',module)
    line_report=validate_lines(folder,module['sha256']);candidate=validate_candidate(folder,module['sha256']);dispatch=validate_dispatch(folder,module['sha256'])
    require(line_report and candidate and dispatch,'Missing case prerequisites');require(digest(args.xml)==line_report['xml_sha256'],'XML provenance differs')
    enum=None
    for event,element in ET.iterparse(args.xml,events=('end',)):
        if element.tag=='enum':
            if element.get('name')==enum_name:enum={m.get('name'):int(m.get('value'),0) for m in element};break
            element.clear()
    require(enum is not None,'PDB enum missing')
    source=(folder/'mono-source-candidate/method-to-ir.c').read_text();groups=parse_cases(source,selector);explicit={label for g in groups for label in g['labels'] if label!='default'}
    require(explicit<=set(enum),'Candidate case names absent from PDB enum')
    by_value={}
    for name,value in enum.items():by_value.setdefault(value,[]).append(name)
    locations=read_json(folder/'mono-pdb-lines/switch-lines.json');main=next(r for r in dispatch['dispatches'] if r['name']==table_name)
    def compare(case_groups):
        starts=[g['line_start'] for g in case_groups];rows=[];labels_in_source={label for g in case_groups for label in g['labels'] if label!='default'}
        for target,inputs in main['target_groups'].items():
            mapped=next(r for r in locations if r['table']==table_name and r['target']==target);near=[]
            for loc in mapped['source_locations']:
                n=loc['line_start'];index=bisect.bisect_right(starts,n)-1
                if index>=0 and n<=case_groups[index]['line_end']:near.append(case_groups[index])
            for value in inputs:
                names=by_value.get(value,[]);expected=[n for n in names if n in labels_in_source]
                labels={label for group in near for label in group['labels']}
                match=bool(set(expected)&labels) if expected else 'default' in labels
                rows.append(dict(input=value,pdb_names=names,target=target,pdb_lines=[r['line_start'] for r in mapped['source_locations']],candidate_groups=near,match=match,
                                 basis='explicit-case-label' if expected else 'candidate-default-no-explicit-case'))
        return rows
    rows=compare(groups)
    require(sorted(r['input'] for r in rows)==list(range(main['input_bias'],main['input_bias']+main['count'])),'Table input coverage differs')
    # A changed case name must change the parsed groups, not silently pass as the original.
    before,after,value=('MONO_TYPE_BOOLEAN','MONO_TYPE_CHAR',2) if args.nested else ('MONO_CEE_NOP','MONO_CEE_BREAK',0)
    # Mutate inside the selected switch so earlier unrelated occurrences do not hide the test.
    line=next(g['label_lines'][g['labels'].index(before)] for g in groups if before in g['labels']);mutated_lines=source.splitlines(keepends=True)
    mutated_lines[line-1]=mutated_lines[line-1].replace(before,after);mutated=''.join(mutated_lines)
    require(any(r['input']==value and not r['match'] for r in compare(parse_cases(mutated,selector))),'Wrong case escaped comparison')
    report=dict(source_sha256=module['sha256'],candidate_commit=candidate['candidate_commit'],enum_members=len(enum),candidate_groups=len(groups),candidate_explicit_labels=len(explicit),
                selector=selector,enum_name=enum_name,inputs=len(rows),matched_inputs=sum(r['match'] for r in rows),mismatched_inputs=[r['input'] for r in rows if not r['match']],negative_control_caught=True,
                dependencies={n:digest(folder/n) for n in ('mono-pdb-lines/manifest.json','mono-source-candidate/manifest.json','mono-dispatch-behavior/manifest.json')},
                limitation='Lexical top-level case labels compared with matched-PDB enum values and line locations. Macros and conditional compilation not evaluated. Label/line correspondence is not proof of case-body behavior, exact source revision or compiled equivalence; mismatches retained.')
    out=folder/('mono-type-case-comparison' if args.nested else 'mono-case-comparison');out.mkdir(exist_ok=True)
    enum_file='type-enum.json' if args.nested else 'opcode-enum.json'
    for name,data in (('report.json',report),(enum_file,enum),('candidate-groups.json',groups),('comparisons.json',sorted(rows,key=lambda r:r['input']))):(out/name).write_text(json.dumps(data,indent=2),encoding='utf-8')
    md=[f'# Mono {selector} switch: PDB enum and candidate case labels','','Lexical correspondence only; not verified source equivalence.','','| Input | PDB enum name | Target | PDB lines | Correspondence |','| --- | --- | --- | --- | --- |']
    for r in sorted(rows,key=lambda r:r['input']):md.append(f"| {r['input']} | {', '.join(r['pdb_names'])} | `{r['target']}` | {r['pdb_lines']} | {'match' if r['match'] else 'review'} |")
    (out/'comparisons.md').write_text('\n'.join(md)+'\n',encoding='utf-8');files=['report.json',enum_file,'candidate-groups.json','comparisons.json','comparisons.md']
    (out/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={n:digest(out/n) for n in files}),indent=2),encoding='utf-8');print(json.dumps(report))


if __name__=='__main__':main()
