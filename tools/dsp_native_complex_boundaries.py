"""Verify original references for ten complex prefixes and probe two frame-dependent tails."""
import collections
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_known_boundaries import validate_cached as validate_boundaries


def validate_cached(folder,sha):
    root=folder/'complex-boundary-evidence'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Complex source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Complex evidence differs')
    report=read_json(root/'report.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Complex dependency differs')
    return report


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32,CS_GRP_CALL,CS_GRP_JUMP
    from capstone.x86 import X86_OP_IMM,X86_OP_REG,X86_REG_EAX
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json')
    for name,source,end,callee in [('rail_api.dll',0x1060f285,0x1060f2a2,0x1061335e),('rail_wrapper.dll',0x1001b6f8,0x1001b715,0x10019d3f)]:
        module=next(m for m in inv['files'] if Path(m['path']).name==name);folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Game changed');game=path.read_bytes()
        prior=validate_boundaries(folder,module['sha256']);edges=[r for r in prior['entries'] if r['category']=='other-context-required']
        refs=read_json(folder/'known-boundary-audit/other-refs.json');require(refs['source_sha256']==module['sha256'],'Reference source differs');by_addr={r['address']:r for r in refs['tables']}
        require(set(by_addr)=={r[k] for r in edges for k in ('source','target')},'Reference scope differs');cs=Cs(CS_ARCH_X86,CS_MODE_32);cs.detail=True
        rows=[];counts=collections.Counter()
        for edge in edges:
            observations={}
            for role in ('source','target'):
                address=int(edge[role],16);observation=by_addr[edge[role]];verified=[]
                if 'target_bytes' in observation:require(pe_read(game,address,len(bytes.fromhex(observation['target_bytes']))).hex()==observation['target_bytes'],'Target bytes differ')
                for ref in observation['references']:
                    require('bytes' in ref,'Noninstruction reference requires separate review');raw=bytes.fromhex(ref['bytes']);pc=int(ref['from'],16);require(pe_read(game,pc,len(raw))==raw,'Reference bytes differ')
                    decoded=list(cs.disasm(raw,pc));require(len(decoded)==1 and decoded[0].size==len(raw),'Reference decode differs');i=decoded[0]
                    if i.group(CS_GRP_CALL) or i.group(CS_GRP_JUMP):
                        require(i.operands[0].type==X86_OP_IMM and i.operands[0].imm==address,'Control reference differs');kind='call' if i.group(CS_GRP_CALL) else 'jump'
                    else:
                        require(i.mnemonic=='mov' and i.operands[0].type==X86_OP_REG and i.operands[0].reg==X86_REG_EAX and i.operands[1].type==X86_OP_IMM and i.operands[1].imm==address,'Unexpected address reference');kind='load-address-into-eax'
                    verified.append(dict(site=ref['from'],owner=ref.get('function'),actual_kind=kind,ghidra_kind=ref['type'],bytes_hex=raw.hex()));counts[kind]+=1
                    if kind=='jump' and 'CALL' in ref['type']:counts['jump-labeled-call']+=1
                observations[role]=verified
            target_kinds={r['actual_kind'] for r in observations['target']}
            category='address-materialized-with-shared-jump-target' if observations['source'] and target_kinds=={'jump'} else 'frame-dependent-called-tail'
            if category=='frame-dependent-called-tail':require(not observations['source'] and target_kinds=={'call'} and int(edge['source'],16)==source,'Unexpected residual category')
            rows.append(dict(source=edge['source'],target=edge['target'],category=category,references=observations,
                             related_functions=sorted({r['owner'] for group in observations.values() for r in group if r['owner']})))
        # Execute the two contiguous 30-byte fragments with an explicitly supplied EBP frame.
        raw=pe_read(game,source,end-source+1);decoded=list(cs.disasm(raw,source));require(sum(i.size for i in decoded)==30 and decoded[-1].mnemonic=='ret','Unexpected fragment')
        uc=Uc(UC_ARCH_X86,UC_MODE_32)
        for page in {source&~4095,callee&~4095,0x200000,0x300000,0x500000}:uc.mem_map(page,4096)
        uc.mem_write(source,raw);uc.mem_write(0x500000,b'\xf4');stack=0x200800;frame=0x300800;visited=set();events=[];writes=[];done=[False]
        def hook(machine,address,size,unused):
            if address==0x500000:done[0]=True;machine.emu_stop();return
            if address==callee:
                sp=machine.reg_read(reg.UC_X86_REG_ESP);ret,arg=struct.unpack('<II',machine.mem_read(sp,8));events.append(arg)
                machine.reg_write(reg.UC_X86_REG_EAX,0x98765432);machine.reg_write(reg.UC_X86_REG_ESP,sp+4);machine.reg_write(reg.UC_X86_REG_EIP,ret);return
            require(source<=address<=end,'Fragment escaped');visited.add(address)
        uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
        cases=0
        def check(flag,low,upper):
            nonlocal cases
            memory=bytearray([0xa5])*256;struct.pack_into('<I',memory,0x88,0x13572468);struct.pack_into('<I',memory,0x54,0x24681357);memory[0x62]=low;memory[0x63]=flag
            expected=bytearray(memory);struct.pack_into('<I',expected,0x48,upper|low)
            uc.mem_write(frame-0x80,bytes(memory));uc.mem_write(stack,struct.pack('<I',0x500000));uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EBP,frame);uc.reg_write(reg.UC_X86_REG_EAX,upper|0x55);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
            events.clear();writes.clear();done[0]=False;uc.emu_start(source,0,count=50)
            require(done[0] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'Fragment return differs')
            require(events==([3] if flag else []),'Conditional helper call differs')
            require(bytes(uc.mem_read(frame-0x80,256))==bytes(expected),'Frame write differs')
            require(uc.reg_read(reg.UC_X86_REG_ESI)==0x13572468 and uc.reg_read(reg.UC_X86_REG_EBX)==0x24681357 and uc.reg_read(reg.UC_X86_REG_EDI)==8 and uc.reg_read(reg.UC_X86_REG_EBP)==frame,'Frame register restore differs')
            require(uc.reg_read(reg.UC_X86_REG_EAX)==(0x98765432 if flag else upper|low),'Partial EAX behavior differs')
            require(all(stack-8<=p and p+n<=stack or p==frame-0x38 and n==4 for p,n in writes),'Unexpected fragment write');cases+=1
        for flag in range(256):
            for low in (0,1,0x80,0xff):
                for upper in (0,0x12345600,0xffffff00):check(flag,low,upper)
        for low in range(256):
            for flag in (0,1):check(flag,low,0xa5a5a500)
        require(visited=={i.address for i in decoded},'Fragment coverage incomplete')
        # Change the low-byte frame load offset to read the adjacent flag instead.
        uc.mem_write(source+11,b'\xe3');uc.ctl_remove_cache(source,end+1);caught=False
        try:check(0,1,0x12345600)
        except ValueError as error:require(str(error)=='Frame write differs','Unexpected mutation failure');caught=True
        require(caught,'Wrong frame byte escaped detector')
        result=dict(source_sha256=module['sha256'],entries=rows,reference_counts=dict(counts),fragment=dict(source=f'{source:x}',end=f'{end:x}',bytes_hex=raw.hex(),instructions=len(decoded),cases=cases,negative_control_caught=caught),
                    dependencies={n:digest(folder/n) for n in ('known-boundary-audit/manifest.json','known-boundary-audit/other-refs.json')},
                    limitation='Reference owners are original Ghidra observations, not proof of a unique enclosing source function. Raw opcodes determine call/jump kind. Fragment tests require a supplied frame and model the conditional helper as returning; exception dispatch, actual helper semantics and whole enclosing functions are unproven.')
        root=folder/'complex-boundary-evidence';root.mkdir(exist_ok=True);(root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
        print(json.dumps(dict(module=name,entries=len(rows),reference_counts=dict(counts),fragment_cases=cases,fragment_instructions=len(decoded))))


if __name__=='__main__':main()
