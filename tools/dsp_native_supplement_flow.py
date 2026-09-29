"""Audit all relocation supplement instruction exits, including uncovered fallthroughs."""
import collections
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_select import selected_folder
from dsp_native_source_match import pe_read, require


def main():
    import argparse
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--new-flow',action='store_true');parser.add_argument('--flow-round',type=int,default=1);args=parser.parse_args()
    if args.flow_round<1 or (args.flow_round!=1 and not args.new_flow):parser.error('--flow-round needs --new-flow and a positive round')
    selected_flow='flow-callbacks' if args.flow_round==1 else f'flow-callbacks-{args.flow_round}'
    followup_name='flow-followup-audit' if args.flow_round==1 else f'flow-followup-audit-{args.flow_round}'
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32, CS_MODE_64, CS_GRP_CALL, CS_GRP_JUMP, CS_GRP_RET, CS_GRP_IRET
    from capstone.x86 import X86_OP_IMM
    root = GENERATED / 'native'; inventory = read_json(root/'inventory.json'); summaries=[]
    for module in inventory['files']:
        folder=selected_folder(root,module)
        if not (folder/'relocation-callbacks/manifest.json').exists():continue
        if args.new_flow and not (folder/selected_flow/'manifest.json').exists():continue
        game_path=Path(inventory['game_directory'])/module['path'];require(digest(game_path)==module['sha256'],'Game changed');game=game_path.read_bytes()
        pe=struct.unpack_from('<I',game,60)[0];bits=64 if struct.unpack_from('<H',game,pe+24)[0]==0x20b else 32
        decoder=Cs(CS_ARCH_X86,CS_MODE_64 if bits==64 else CS_MODE_32);decoder.detail=True
        base_marker=read_json(folder/'decompile-manifest.json');require(base_marker['files']['functions.json']==digest(folder/'functions.json'),'Base index changed')
        known={int(f['address'],16) for f in read_json(folder/'functions.json')['functions']};records={};dependencies={'functions.json':digest(folder/'functions.json')}
        kinds=['relocation-callbacks','relocation-retry']
        if args.new_flow:kinds+=['flow-callbacks' if n==1 else f'flow-callbacks-{n}' for n in range(1,args.flow_round+1)]
        for kind in kinds:
            if not (folder/kind/'manifest.json').exists():continue
            marker=read_json(folder/kind/'manifest.json');require(marker['source_sha256']==module['sha256'],'Supplement source changed')
            require(digest(folder/kind/'report.json')==marker['files']['report.json'],'Supplement report changed')
            report=read_json(folder/kind/'report.json');require(report['complete'] and report['program_changes_rolled_back'],'Incomplete supplement')
            dependencies[kind+'/manifest.json']=digest(folder/kind/'manifest.json')
            for f in report['functions']:
                if f['status']=='decompiled':known.add(int(f['address'],16))
                if args.new_flow and kind!=selected_flow:continue
                if 'body_ranges_inclusive' not in f:continue
                path='functions/'+f['address']+'.asm';require(digest(folder/kind/path)==marker['files'][path],'Assembly changed')
                records[f['address']]=(f,folder/kind/path)
        if args.new_flow and (folder/'relocation-operand-audit/manifest.json').exists():
            from dsp_native_relocation_operands import validate_cached as validate_operands
            classified=validate_operands(folder,module['sha256'])
            known.difference_update(int(r['address'],16) for r in classified.get('quarantined_entries',[]))
            dependencies['relocation-operand-audit/manifest.json']=digest(folder/'relocation-operand-audit/manifest.json')
        # Respect the verified body expansion instead of re-reporting the already repaired 43-byte prefix.
        if (folder/'scanner-body-repair/manifest.json').exists():
            from dsp_native_scanner_body import validate_cached
            repair=validate_cached(folder,module['sha256']);entry=repair['address']
            if entry in records:
                f=dict(records[entry][0]);f.update(size=repair['body_bytes'],instruction_count=repair['instructions'],body_ranges_inclusive=[[entry,'10632222']])
                records[entry]=(f,folder/'scanner-body-repair/10632090.asm');dependencies['scanner-body-repair/manifest.json']=digest(folder/'scanner-body-repair/manifest.json')
        edges=[];counts=collections.Counter();missing=set();fallthroughs=set();calls=[];normalizations=[]
        for entry,(f,path) in records.items():
            body=set()
            for lo,hi in f['body_ranges_inclusive']:body.update(range(int(lo,16),int(hi,16)+1))
            decoded=[];seen=set();units=0
            for line in path.read_text().splitlines():
                pc,hexbytes,_=line.split(' ',2);address=int(pc,16);raw=bytes.fromhex(hexbytes)
                require(pe_read(game,address,len(raw))==raw,'Instruction bytes differ')
                ins=list(decoder.disasm(raw,address))
                split_wait=(len(ins)==2 and raw[0]==0x9b and ins[0].bytes==b'\x9b' and ins[0].mnemonic=='wait' and
                            ins[1].address==address+1 and ins[1].mnemonic.startswith('fn') and ins[1].size==len(raw)-1)
                require((len(ins)==1 and ins[0].size==len(raw)) or split_wait,f'Instruction decode differs: {entry} {pc} {hexbytes}')
                if split_wait:normalizations.append(dict(source=entry,site=pc,bytes_hex=hexbytes,kind='combined-x87-wait',decoded=[i.mnemonic+' '+i.op_str for i in ins]))
                span=set(range(address,address+len(raw)));require(span<=body and not seen.intersection(span),'Body overlap/escape');seen|=span;decoded.extend(ins);units+=1
            require(seen==body and units==f['instruction_count'],'Body coverage mismatch');counts['instructions']+=len(decoded);counts['exported_instruction_units']+=units;counts['bodies']+=1
            for ins in decoded:
                outgoing=[]
                if ins.group(CS_GRP_CALL) or ins.group(CS_GRP_JUMP):
                    kind='call' if ins.group(CS_GRP_CALL) else 'jump'
                    if ins.operands[0].type==X86_OP_IMM:
                        target=ins.operands[0].imm & ((1<<bits)-1)
                        if target not in body:outgoing.append(('direct-'+kind,target))
                    else:counts['indirect-'+kind]+=1
                terminal=ins.group(CS_GRP_RET) or ins.group(CS_GRP_IRET) or ins.mnemonic in ('jmp','ljmp','ud2','hlt','int3')
                if not terminal and ins.address+ins.size not in body:outgoing.append(('uncovered-fallthrough',ins.address+ins.size))
                for kind,target in outgoing:
                    row=dict(source=entry,site=f'{ins.address:x}',bytes_hex=ins.bytes.hex(),instruction=ins.mnemonic+' '+ins.op_str,kind=kind,target=f'{target:x}',exact_entry_known=target in known)
                    edges.append(row);counts[kind]+=1
                    if target not in known:missing.add(target)
                    if kind=='uncovered-fallthrough':fallthroughs.add(entry)
                    if target not in known and kind in ('direct-call','direct-jump') and len(ins.bytes)==5 and ins.bytes[0] in (0xe8,0xe9):calls.append(row)
        output=folder/(followup_name if args.new_flow else 'supplement-flow-audit');output.mkdir(exist_ok=True)
        result=dict(source_sha256=module['sha256'],bits=bits,counts=dict(counts),decoder_normalizations=normalizations,edges=edges,missing_exact_entries=[f'{n:x}' for n in sorted(missing)],
                    uncovered_fallthrough_sources=sorted(fallthroughs),direct_entries=calls,dependencies=dependencies,
                    limitation='Byte-verified exports and syntactic exits only. An absent exact entry may be a known function interior/shared block. Uncovered fallthrough indicates an exported body boundary to review, not necessarily missing source code. Indirect targets remain unresolved.')
        (output/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
        (output/'missing-entries.txt').write_text(''.join(f'{n:x}\n' for n in sorted(missing)),encoding='utf-8')
        (output/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={n:digest(output/n) for n in ('report.json','missing-entries.txt')}),indent=2),encoding='utf-8')
        summary=dict(module=module['path'],counts=dict(counts),missing_exact_entries=len(missing),fallthrough_sources=len(fallthroughs),five_byte_direct_candidates=len({r['target'] for r in calls}));summaries.append(summary);print(json.dumps(summary))
    summary_name=('flow-followup-summary.json' if args.flow_round==1 else f'flow-followup-summary-{args.flow_round}.json') if args.new_flow else 'supplement-flow-summary.json'
    (root/summary_name).write_text(json.dumps(dict(modules=summaries),indent=2),encoding='utf-8')


if __name__=='__main__':main()
