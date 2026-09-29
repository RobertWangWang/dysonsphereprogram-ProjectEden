"""Probe original encoded callback selection/consumption without invoking callbacks."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_supplement_context import validate_cached as validate_context


def validate_cached(folder,sha):
    root=folder/'encoded-callback-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Callback source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Callback evidence differs')
    result=read_json(root/'report.json')
    for name,value in result['dependencies'].items():require(digest(folder/name)==value,'Callback dependency differs')
    return result


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');m=next(r for r in inv['files'] if Path(r['path']).name=='rail_api.dll');folder=GENERATED/'native'/m['output'];path=Path(inv['game_directory'])/m['path'];require(digest(path)==m['sha256'],'Source changed');game=path.read_bytes();require(validate_context(folder,m['sha256']),'Context missing')
    start=0x1060ef46;end=0x1060ef9d;raw=pe_read(game,start,end-start);cs=Cs(CS_ARCH_X86,CS_MODE_32);instructions=list(cs.disasm(raw,start));require(sum(i.size for i in instructions)==len(raw),'Decode coverage differs')
    uc=Uc(UC_ARCH_X86,UC_MODE_32)
    for page in (0x1060e000,0x10e24000,0x10e81000,0x200000,0x300000):uc.mem_map(page,4096)
    uc.mem_write(start,raw);uc.mem_write(end,b'\xf4');stack=0x200800;frame=0x300800;done=[False];visited=set();writes=[]
    def hook(machine,address,size,unused):
        if address==end:done[0]=True;machine.emu_stop()
        else:require(start<=address<end,'Fragment escaped');visited.add(address)
    uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
    def rol(value,n):return ((value<<n)|(value>>(32-n if n else 32)))&0xffffffff
    cases=0
    def check(cookie,pointer,selector):
        nonlocal cases
        shift=cookie&31;encoded=rol(pointer,shift)^cookie;which=0 if selector==0 else 1;slots=[0xabcdef01,0x23456789];slots[which]=encoded;expected_slots=list(slots)
        if pointer not in (0,1):expected_slots[which]=cookie
        memory=bytearray([0xa5])*256;struct.pack_into('<I',memory,0x88,selector);expected=bytearray(memory);struct.pack_into('<I',expected,0x60,pointer);struct.pack_into('<I',expected,0x5c,2 if which==0 else 21);struct.pack_into('<I',expected,0x7c,0xfffffffe)
        uc.mem_write(frame-0x80,bytes(memory));uc.mem_write(0x10e24f44,struct.pack('<I',cookie));uc.mem_write(0x10e81d70,struct.pack('<II',*slots));uc.reg_write(reg.UC_X86_REG_EBP,frame);uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        writes.clear();done[0]=False;uc.emu_start(start,0,count=100)
        require(done[0] and uc.reg_read(reg.UC_X86_REG_ESP)==stack,'Fragment stack differs')
        require(uc.reg_read(reg.UC_X86_REG_ESI)==pointer,'Decoded pointer differs');require(uc.reg_read(reg.UC_X86_REG_EDI)==(2 if which==0 else 21) and uc.reg_read(reg.UC_X86_REG_EBX)==0x10e81d70+4*which,'Selection differs')
        require(bytes(uc.mem_read(frame-0x80,256))==bytes(expected),'Frame state differs');require(bytes(uc.mem_read(0x10e81d70,8))==struct.pack('<II',*expected_slots),'Slot mutation differs')
        require(all(p==stack-4 and n==4 or p in (frame-0x20,frame-0x24,frame-4,0x10e81d70+which*4) and n==4 for p,n in writes),'Unexpected write');cases+=1
    for shift in range(32):
        for high in (0,0x12345660,0xffffffe0):
            for pointer in (0,1,2,0x600000,0x12345678,0xffffffff):
                for selector in (0,1,0xffffffff):check(high|shift,pointer,selector)
    require(visited=={i.address for i in instructions},'Fragment instruction coverage incomplete');uc.mem_write(0x1060ef78,b'\xc6');uc.ctl_remove_cache(start,end);caught=False
    try:check(1,0x12345678,0)
    except ValueError as error:require(str(error)=='Decoded pointer differs','Unexpected mutation failure');caught=True
    require(caught,'Wrong rotate escaped detector')
    result=dict(source_sha256=m['sha256'],address='1060ef27',fragment_start=f'{start:x}',fragment_end_exclusive=f'{end:x}',bytes_hex=raw.hex(),instructions=len(instructions),cases=cases,negative_control_caught=caught,
                dependencies={'supplement-context-evidence/manifest.json':digest(folder/'supplement-context-evidence/manifest.json')},
                semantics='Select slot 10e81d70/code 2 for selector zero; otherwise slot 10e81d74/code 21. Decode ROR32(encoded XOR cookie,cookie&31). Decoded 0 and 1 are retained; other pointers are replaced by cookie (encoded null) before unlock/callback.',
                limitation='Fragment only: supplied frame, allocated globals, no actual lock, SEH dispatch, guard check or callback execution. Does not prove cookie origin, callback registration or whole-function return type.')
    root=folder/'encoded-callback-behavior';root.mkdir(exist_ok=True);(root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=m['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8');print(json.dumps(result))


if __name__=='__main__':main()
