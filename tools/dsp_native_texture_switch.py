"""核验 180961d40 的类型分派、RVA 表和字段更新；离线执行原始指令片段。"""
import json
import re
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

ADDRESS='180961d40'
TARGETS=[0x180961e6d,0x180961e67,0x180961e74,0x180961e55,0x180961e6d,0x180961e6d,0x180961e5d]


def validate_cached(folder,sha):
    root=folder/'quality-repair'/ADDRESS
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Switch baseline changed')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Switch evidence changed: '+name)
    return read_json(root/'verification.json')


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    from unicorn.x86_const import UC_X86_REG_RBX,UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8,UC_X86_REG_RIP
    folder=GENERATED/'native/UnityPlayer.dll';root=folder/'quality-repair'/ADDRESS
    path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(path)==GAME_SHA,'Game baseline changed');game=path.read_bytes()
    report=read_json(root/'report.json');require(report['status']=='decompiled' and report['program_changes_rolled_back'],'Repair not complete')
    require(report['source_sha256']==GAME_SHA and report['address']==ADDRESS,'Repair identity mismatch')
    table=pe_read(game,0x180961eb0,28)
    require([0x180000000+x for x in struct.unpack('<7I',table)]==TARGETS,'RVA mapping changed')
    require(report['tables'][0]['targets_by_input']==[f'{t:x}' for t in TARGETS],'Ghidra mapping differs')
    require(report['tables'][0]['manual_override'],'Missing explicit switch override')
    code=(root/(ADDRESS+'.c')).read_text(encoding='utf-8')
    warnings=[' '.join(w.split()) for w in re.findall(r'/\*\s*WARNING:\s*(.*?)\*/',code,re.S)]
    require(warnings==['Switch is manually overridden'] and 'halt_baddata' not in code,'Unexpected remaining C warning')
    require(all(f'case {k}:' in code for k in (5,6,7,10)) and '/ 6;' in code,'Recovered C branches absent')
    start,end=0x180961dee,0x180961e74
    raw=pe_read(game,start,end-start);decoder=Cs(CS_ARCH_X86,CS_MODE_64)
    instructions=list(decoder.disasm(raw,start));require(sum(i.size for i in instructions)==len(raw),'Incomplete decoding')
    (root/'selector.asm').write_text(''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in instructions),encoding='utf-8')
    uc=Uc(UC_ARCH_X86,UC_MODE_64);uc.mem_map(0x180961000,0x1000);uc.mem_write(start,raw);uc.mem_write(0x180961eb0,table)
    obj=0x200000;uc.mem_map(obj,0x1000);visited=set();writes=[];branches=[]
    def step(machine,address,size,user):
        visited.add(address)
        if address==0x180961e53:branches.append(machine.reg_read(UC_X86_REG_RCX))
    def write(machine,access,address,size,value,user):writes.append((address,size))
    uc.hook_add(UC_HOOK_CODE,step);uc.hook_add(UC_HOOK_MEM_WRITE,write)
    kinds=list(range(17))+[0x7fffffff,0x80000000,0xfffffffd,0xfffffffe,0xffffffff]
    dimensions=[0,1,5,6,0x2aaaaaaa,0x2aaaaaab,0x7fffffff,0xffffffff]
    levels=[0,1,17,0x80000000,0xffffffff]
    cases=0
    def run(kind,old,level,old17c):
        before=bytearray([0xa5]*512)
        for off,value in [(0x16c,kind),(0x17c,old17c),(0x184,old),(0x1d8,level)]:struct.pack_into('<I',before,off,value)
        expected=bytearray(before)
        if kind in (4,5,8,9,10):struct.pack_into('<I',expected,0x17c,level)
        if kind==10:struct.pack_into('<I',expected,0x184,((old*6)&0xffffffff)//6)
        uc.mem_write(obj,bytes(before));uc.reg_write(UC_X86_REG_RBX,obj);uc.reg_write(UC_X86_REG_RDX,0xdeadbeef);uc.reg_write(UC_X86_REG_R8,0xcafebabe)
        writes.clear();branches.clear();uc.emu_start(start,end,count=100)
        require(uc.reg_read(UC_X86_REG_RIP)==end,'Selector did not reach join')
        require(bytes(uc.mem_read(obj,512))==bytes(expected),'Field update differs')
        require(all(a in (obj+0x17c,obj+0x184) and n==4 for a,n in writes),'Unexpected memory write')
        require(branches==([TARGETS[kind-4]] if 4<=kind<=10 else []),'Dispatch target differs')
    for kind in kinds:
        for old in dimensions:
            for level in levels:
                for old17c in (0,19,0xffffffff):run(kind,old,level,old17c);cases+=1
    # A changed case destination must be detected by both dispatch and field checks.
    uc.mem_write(0x180961eb0,struct.pack('<I',0x961e74))
    rejected=False
    try:run(4,5,17,19)
    except ValueError:rejected=True
    require(rejected,'Mutated table was not rejected')
    verification=dict(source_sha256=GAME_SHA,address=ADDRESS,file=ADDRESS+'.c',cases=cases,
        tested_kinds=kinds,tested_dimensions=dimensions,tested_levels=levels,tested_old17c=[0,19,0xffffffff],
        selector_begin=f'{start:x}',selector_end_exclusive=f'{end:x}',instructions=len(instructions),visited_instructions=len(visited),
        mutated_table_rejected=rejected,targets_by_kind={str(i+4):f'{t:x}' for i,t in enumerate(TARGETS)},
        field_behavior={'4/8/9':'[+17c] = [+1d8]','5':'[+184] written back unchanged; [+17c] = [+1d8]','6':'no field write','7':'[+17c] written back unchanged','10':'[+184] = ((uint32)(old184 * 6)) / 6; [+17c] = [+1d8]','other':'no field write'},
        limitation='Original selector/field-update fragment, not whole-function or texture API execution. Type/field names are not established; uint32 multiplication wraps before division. C retains manual switch override; case labels must be checked against original kind-to-target mapping.')
    require(len(visited)==len(instructions),'Selector instruction coverage incomplete')
    (root/'verification.json').write_text(json.dumps(verification,indent=2),encoding='utf-8')
    names=['report.json',ADDRESS+'.c','selector.asm','verification.json']
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={n:digest(root/n) for n in names}),indent=2),encoding='utf-8')
    print(json.dumps(verification))


if __name__=='__main__':main()
