"""Recover conditional post-assertion closure until known Mono body boundaries."""
import json
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_select import selected_folder
from dsp_native_mono_body import validate_cached as validate_body
from dsp_native_mono_assertion import validate_cached as validate_assertion


def validate_cached(folder,sha):
    root=folder/'mono-continuation-evidence'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Mono continuation source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Mono continuation evidence differs')
    report=read_json(root/'report.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Mono continuation dependency differs')
    return report


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64,CS_GRP_JUMP,CS_GRP_CALL,CS_GRP_RET
    from capstone.x86 import X86_OP_IMM
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='mono-2.0-bdwgc.dll')
    folder=selected_folder(GENERATED/'native',module);path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes()
    body=validate_body(folder,module['sha256']);assertions=validate_assertion(folder,module['sha256']);require(body and assertions,'Missing boundary evidence')
    known=set();occupied=set();original_lines={}
    for line in (folder/'switch-normalize/mono_method_to_ir.asm').read_text().splitlines():
        address,data,_=line.split(' ',2);pc=int(address,16);known.add(pc);occupied.update(range(pc,pc+len(bytes.fromhex(data))));original_lines[pc]=line
    require(len(known)==body['instructions'] and len(occupied)==body['body_bytes'],'Known coverage differs')
    cs=Cs(CS_ARCH_X86,CS_MODE_64);cs.detail=True;new={};newbytes=set();roots=[]
    impossible={int(p['address'],16)+4:int(p['fallthrough'],16) for p in assertions['prefixes']}
    for prefix in assertions['prefixes']:
        entry=int(prefix['address'],16);pending=[entry];seen=set();rejoins=set();escaped=[];calls=[];terminals=[];edges=[]
        while pending:
            pc=pending.pop()
            if pc in known:rejoins.add(pc);continue
            if pc in seen:continue
            require(len(seen)<10000,'Unexpected continuation growth')
            if not 0x1802b5740<=pc<=0x180312691:escaped.append(f'{pc:x}');continue
            require(pc not in occupied,'Continuation enters known instruction interior')
            if pc not in new:
                ins=next(cs.disasm(pe_read(game,pc,min(15,0x180312692-pc)),pc,count=1),None);require(ins is not None,'Undecodable continuation')
                span=set(range(pc,pc+ins.size));require(not(span&occupied or span&newbytes),'Overlapping continuation instruction');newbytes.update(span);new[pc]=ins
            ins=new[pc];seen.add(pc);nxt=pc+ins.size;dest=[]
            if ins.group(CS_GRP_RET) or ins.mnemonic in ('int3','ud2','hlt'):terminals.append(dict(address=f'{pc:x}',instruction=ins.mnemonic))
            elif pc in impossible:
                require(ins.mnemonic=='jne' and nxt==impossible[pc],'Proven prefix changed');dest=[nxt]
            elif ins.group(CS_GRP_JUMP):
                if ins.operands[0].type==X86_OP_IMM:dest.append(ins.operands[0].imm)
                else:escaped.append('indirect:'+f'{pc:x}')
                if ins.mnemonic not in ('jmp','ljmp'):dest.append(nxt)
            else:
                if ins.group(CS_GRP_CALL):calls.append(dict(address=f'{pc:x}',operand=ins.op_str))
                dest.append(nxt)
            for target in dest:edges.append([f'{pc:x}',f'{target:x}'])
            pending.extend(dest)
        roots.append(dict(entry=f'{entry:x}',new_instructions=len(seen),new_bytes=sum(new[p].size for p in seen),rejoins=[f'{p:x}' for p in sorted(rejoins)],escapes=escaped,calls=calls,terminals=terminals,edges=edges))
    rows=[dict(address=f'{pc:x}',bytes_hex=bytes(ins.bytes).hex(),instruction=ins.mnemonic+' '+ins.op_str) for pc,ins in sorted(new.items())]
    result=dict(source_sha256=module['sha256'],address='1802b5740',new_bytes=len(newbytes),new_instructions=len(new),roots=roots,
                combined_body_bytes=body['body_bytes']+len(newbytes),combined_instructions=body['instructions']+len(new),
                dependencies={n:digest(folder/n) for n in ('mono-body-evidence/manifest.json','mono-assertion-behavior/manifest.json')},
                limitation='Conditional static closure if assertion returns. Only six proven xor/test/jne backedges pruned; other branches explored both ways, calls assumed returning. Stops at known instruction starts. Not whole-function execution, independent functions, original Ghidra body edit or complete C recovery.')
    root=folder/'mono-continuation-evidence';root.mkdir(exist_ok=True);(root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(root/'instructions.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
    (root/'continuations.asm').write_text(''.join(f"{row['address']} {row['bytes_hex']} {row['instruction']}\n" for row in rows),encoding='utf-8')
    merged=original_lines|{int(row['address'],16):f"{row['address']} {row['bytes_hex']} {row['instruction']}" for row in rows}
    require(len(merged)==result['combined_instructions'],'Combined assembly overlaps')
    (root/'combined.asm').write_text('\n'.join(merged[pc] for pc in sorted(merged))+'\n',encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={n:digest(root/n) for n in ('report.json','instructions.json','continuations.asm','combined.asm')}),indent=2),encoding='utf-8')
    print(json.dumps(result))


if __name__=='__main__':main()
