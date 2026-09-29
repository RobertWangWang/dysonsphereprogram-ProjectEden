"""Resolve four old fallthrough gaps as trap-backed termination paths, with explicit limits."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require

PROFILES={
 'rail_api.dll':dict(abort=0x1060be06,parent=0x10602154,handler=0x1060f070,raise_signal=0x1060f0d4,feature=0x10629b08,report=0x105fdde7,exit=0x1060a2bb,flags=0x10e47350),
 'rail_wrapper.dll':dict(abort=0x100195c4,parent=0x10019588,handler=0x1001b4ec,raise_signal=0x1001b547,feature=0x1001fa68,report=0x100183d7,exit=0x10018a87,flags=0x100ee030),
}


def validate_cached(folder,sha):
    root=folder/'abort-flow-verification'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Abort source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Abort evidence differs')
    result=read_json(root/'report.json')
    for name,value in result['dependencies'].items():require(digest(folder/name)==value,'Fallthrough evidence differs')
    return result


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32,CS_GRP_JUMP,CS_GRP_RET,CS_GRP_CALL
    from capstone.x86 import X86_OP_IMM
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');summaries=[]
    for name,cfg in PROFILES.items():
        module=next(m for m in inv['files'] if Path(m['path']).name==name);folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path']
        require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes();cs=Cs(CS_ARCH_X86,CS_MODE_32);cs.detail=True
        start=cfg['abort'];raw=pe_read(game,start,67);instructions={i.address:i for i in cs.disasm(raw,start)}
        require(sum(i.size for i in instructions.values())==67 and instructions[start+66].mnemonic=='int3','Abort window differs')
        parent_raw=pe_read(game,cfg['parent'],60);parent={i.address:i for i in cs.disasm(parent_raw,cfg['parent'])}
        def parent_reach(entry):
            pending=[entry];seen=set();ends=set()
            while pending:
                pc=pending.pop()
                if pc in seen:continue
                require(pc in parent,'Parent flow escapes');seen.add(pc);i=parent[pc]
                require(not i.group(CS_GRP_RET),'Normal parent flow reaches RET')
                if i.group(CS_GRP_CALL) and i.operands[0].type==X86_OP_IMM and i.operands[0].imm==start:ends.add(pc);continue
                if i.group(CS_GRP_JUMP):
                    require(i.operands[0].type==X86_OP_IMM,'Unexpected indirect parent jump');pending.append(i.operands[0].imm)
                    if i.mnemonic=='jmp':continue
                pending.append(pc+i.size)
            require(ends=={cfg['parent']+54},'Parent does not end at abort call');return sorted(seen)
        normal=parent_reach(cfg['parent']);exception=parent_reach(cfg['parent']+44)
        ownership=read_json(folder/'fallthrough-ownership/report.json');edges=[r for r in ownership['entries'] if r.get('target_kind')=='int3-after-call']
        require(len(edges)==2,'Expected two historical trap edges per module')
        for edge in edges:
            pc=int(edge['site'],16);encoded=pe_read(game,pc,6);target=pc+5+int.from_bytes(encoded[1:5],'little',signed=True)
            require(encoded[0]==0xe8 and encoded[5]==0xcc and target in (start,cfg['parent']),'Historical edge differs')
        uc=Uc(UC_ARCH_X86,UC_MODE_32);pages={0x200000,0x500000,cfg['flags']&~4095}
        for address in [start,*[cfg[k] for k in ('handler','raise_signal','feature','report','exit')]]:pages.add(address&~4095)
        for page in pages:uc.mem_map(page,4096)
        uc.mem_write(start,raw);stack=0x200800;visited=set();events=[];trap=[None];settings=[0,0];counts={'int29':0,'int3':0}
        def hook(machine,address,size,unused):
            if address in [cfg[k] for k in ('handler','raise_signal','feature','report','exit')]:
                key=next(k for k in ('handler','raise_signal','feature','report','exit') if cfg[k]==address);sp=machine.reg_read(reg.UC_X86_REG_ESP)
                argc={'handler':0,'raise_signal':1,'feature':1,'report':3,'exit':1}[key]
                args=list(struct.unpack('<'+'I'*argc,machine.mem_read(sp+4,argc*4))) if argc else []
                events.append([key,args]);ret=struct.unpack('<I',machine.mem_read(sp,4))[0]
                machine.reg_write(reg.UC_X86_REG_EAX,settings[0] if key=='handler' else settings[1] if key=='feature' else 0)
                machine.reg_write(reg.UC_X86_REG_ESP,sp+4+(4 if key=='feature' else 0));machine.reg_write(reg.UC_X86_REG_EIP,ret);return
            require(address in instructions,'Abort escaped instruction window');visited.add(address)
            if instructions[address].mnemonic in ('int','int3'):
                trap[0]='int29' if instructions[address].mnemonic=='int' else 'int3';machine.emu_stop()
        uc.hook_add(UC_HOOK_CODE,hook)
        def check(flags,handler,feature):
            settings[:]=[handler,feature];events.clear();trap[0]=None;uc.mem_write(cfg['flags'],bytes([flags]));uc.mem_write(stack,struct.pack('<I',0x500000));uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
            uc.emu_start(start,0,count=100)
            expected=[['handler',[]]]
            if handler:expected.append(['raise_signal',[22]])
            if flags&2:expected.append(['feature',[23]])
            fast=bool(flags&2 and feature)
            if not fast:
                if flags&2:expected.append(['report',[3,0x40000015,1]])
                expected.append(['exit',[3]])
            require(events==expected and trap[0]==('int29' if fast else 'int3'),'Abort path differs')
            if fast:require(uc.reg_read(reg.UC_X86_REG_ECX)==7,'Fastfail reason differs')
            require(uc.reg_read(reg.UC_X86_REG_ESP)==stack-(0 if fast else 4),'Trap stack differs')
            counts[trap[0]]+=1
        for flags in range(256):
            for handler in (0,1,0xffffffff):
                for feature in (0,1,0xffffffff):check(flags,handler,feature)
        require(visited==set(instructions),'Abort instruction coverage incomplete')
        uc.mem_write(start+23,b'\x04');uc.ctl_remove_cache(start,start+67);caught=False
        try:check(2,0,0)
        except ValueError as error:require(str(error)=='Abort path differs','Unexpected negative error');caught=True
        require(caught,'Wrong abort flag escaped detector')
        result=dict(source_sha256=module['sha256'],abort=f'{start:x}',parent=f"{cfg['parent']:x}",cases=sum(counts.values()),terminals=counts,instructions=len(instructions),
                    bytes_hex=raw.hex(),parent_bytes_hex=parent_raw.hex(),normal_parent_instructions=[f'{p:x}' for p in normal],exception_parent_instructions=[f'{p:x}' for p in exception],
                    historical_edges=edges,negative_control_caught=caught,dependencies={'fallthrough-ownership/report.json':digest(folder/'fallthrough-ownership/report.json')},
                    conclusion='Historical CALL/INT3 edges lead to abort directly or through a parent whose ordinary control-flow ends at abort. Local abort paths have no RET and reach INT29 or INT3 when modeled external calls return.',
                    limitation='External handler/signal/feature/report/exit calls modeled; traps stop execution. Not a proof about exception-handler continuation, unwinding, OS process termination, or arbitrary external callbacks. Do not merge bytes after traps into the preceding function.')
        root=folder/'abort-flow-verification';root.mkdir(exist_ok=True);(root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
        summaries.append(dict(module=name,cases=result['cases'],instructions=len(instructions),terminals=counts));print(json.dumps(summaries[-1]))


if __name__=='__main__':main()
