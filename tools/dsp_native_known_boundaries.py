"""Classify known-entry fallthroughs without asserting independent function boundaries."""
import collections
import json
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_abort_flow import validate_cached as validate_abort


def validate_cached(folder,sha):
    root=folder/'known-boundary-audit'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Boundary source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Boundary report differs')
    report=read_json(root/'report.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Boundary dependency differs')
    return report


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32,CS_GRP_CALL
    from capstone.x86 import X86_OP_MEM,X86_OP_IMM,X86_REG_EBP
    inv=read_json(GENERATED/'native/inventory.json')
    for name in ('rail_api.dll','rail_wrapper.dll'):
        module=next(m for m in inv['files'] if Path(m['path']).name==name);folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path']
        require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes();cs=Cs(CS_ARCH_X86,CS_MODE_32);cs.detail=True
        dependencies={};base_manifest=read_json(folder/'decompile-manifest.json');flow_manifest=read_json(folder/'supplement-flow-audit/manifest.json')
        for filename,expected in [('functions.json',base_manifest['files']['functions.json']),('supplement-flow-audit/report.json',flow_manifest['files']['report.json'])]:
            require(digest(folder/filename)==expected,'Index/audit differs');dependencies[filename]=expected
        base={r['address']:r for r in read_json(folder/'functions.json')['functions']};supp={}
        for kind in ('relocation-callbacks','relocation-retry'):
            if not (folder/kind/'manifest.json').exists():continue
            marker=read_json(folder/kind/'manifest.json');require(marker['source_sha256']==module['sha256'],'Supplement source differs')
            require(digest(folder/kind/'report.json')==marker['files']['report.json'],'Supplement report differs')
            dependencies[kind+'/manifest.json']=digest(folder/kind/'manifest.json')
            for row in read_json(folder/kind/'report.json')['functions']:
                if 'body_ranges_inclusive' in row:supp[row['address']]=(row,kind,marker)
        abort=validate_abort(folder,module['sha256']);require(abort,'Need terminal evidence');dependencies['abort-flow-verification/manifest.json']=digest(folder/'abort-flow-verification/manifest.json')
        edges=[r for r in read_json(folder/'supplement-flow-audit/report.json')['edges'] if r['kind']=='uncovered-fallthrough' and r['exact_entry_known']]
        rows=[];counts=collections.Counter()
        for edge in edges:
            source=edge['source'];target=edge['target'];record,kind,marker=supp[source];asm=kind+'/functions/'+source+'.asm'
            require(digest(folder/asm)==marker['files']['functions/'+source+'.asm'],'Source assembly differs');dependencies[asm]=digest(folder/asm)
            decoded=[];covered=set()
            for line in (folder/asm).read_text().splitlines():
                address,data,_=line.split(' ',2);pc=int(address,16);raw=bytes.fromhex(data);require(pe_read(game,pc,len(raw))==raw,'Source bytes differ')
                instructions=list(cs.disasm(raw,pc));require(len(instructions)==1 and instructions[0].size==len(raw),'Boundary decode differs')
                span=set(range(pc,pc+len(raw)));require(not covered&span,'Overlapping source');covered|=span;decoded.extend(instructions)
            expected=set()
            for lo,hi in record['body_ranges_inclusive']:expected.update(range(int(lo,16),int(hi,16)+1))
            require(covered==expected and len(decoded)==record['instruction_count'],'Source coverage differs')
            last=next(i for i in decoded if i.address==int(edge['site'],16));require(last.bytes.hex()==edge['bytes_hex'] and last.address+last.size==int(target,16),'Fallthrough differs')
            if target in base:
                provenance='original-index';target_record=base[target];cfile=target_record.get('file')
                if cfile:
                    require(digest(folder/cfile)==base_manifest['files'][cfile],'Target C differs');dependencies[cfile]=digest(folder/cfile)
            else:
                require(target in supp,'Known target missing');target_record,target_kind,target_marker=supp[target];provenance='supplement-index';cfile=target_kind+'/functions/'+target+'.c'
                require(digest(folder/cfile)==target_marker['files']['functions/'+target+'.c'],'Target supplement C differs');dependencies[cfile]=digest(folder/cfile)
            ebp_relative=any(op.type==X86_OP_MEM and op.mem.base==X86_REG_EBP for i in decoded for op in i.operands)
            writes_ebp=any(X86_REG_EBP in i.regs_access()[1] for i in decoded)
            simple=all(i.mnemonic in ('mov','or','xor') for i in decoded)
            terminal=last.group(CS_GRP_CALL) and last.operands[0].type==X86_OP_IMM and f'{last.operands[0].imm:x}' in (abort['abort'],abort['parent'])
            category='verified-terminal-call' if terminal else 'inherited-frame-prefix' if simple and ebp_relative and not writes_ebp else 'other-context-required'
            first=min(decoded,key=lambda i:i.address);read_regs=[first.reg_name(r) for r in first.regs_access()[0]]
            rows.append(dict(source=source,target=target,site=edge['site'],category=category,target_provenance=provenance,target_file=cfile,
                             source_instructions=len(decoded),first_instruction=first.mnemonic+' '+first.op_str,entry_register_reads=read_regs,
                             ebp_relative=ebp_relative,writes_ebp=writes_ebp,source_body_ranges=record['body_ranges_inclusive']))
            counts[category]+=1;counts[provenance]+=1
        expected_count=292 if name=='rail_api.dll' else 21;require(len(rows)==expected_count,'Audit scope differs')
        result=dict(source_sha256=module['sha256'],entries=rows,counts=dict(counts),dependencies=dependencies,
                    limitation='Verified source-body bytes, fallthrough edge and target-index/C provenance. Inherited EBP means context is required, not a proof of exception-funclet identity or enclosing function. No automatic body merges or unique-function count changes. Terminal classification inherits the abort model limits.')
        root=folder/'known-boundary-audit';root.mkdir(exist_ok=True);(root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
        print(json.dumps(dict(module=name,entries=len(rows),counts=dict(counts))))


if __name__=='__main__':main()
