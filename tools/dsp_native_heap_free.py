"""Validate original CRT free/error translation through explicit OS/TLS boundaries."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require


def validate_cached(folder,sha):
    root=folder/'heap-free-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Heap source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Heap evidence differs')
    return read_json(root/'report.json')


def imports_at(game,addresses):
    pe=struct.unpack_from('<I',game,60)[0];optional=pe+24
    require(struct.unpack_from('<H',game,optional)[0]==0x10b,'Expected PE32')
    base=struct.unpack_from('<I',game,optional+28)[0];rva=struct.unpack_from('<I',game,optional+104)[0]
    result={}
    def string(va):
        data=bytearray()
        for offset in range(256):
            value=pe_read(game,va+offset,1)[0]
            if not value:return data.decode('ascii')
            data.append(value)
        raise ValueError('Unterminated import name')
    for n in range(1024):
        lookup,stamp,chain,name,iat=struct.unpack('<5I',pe_read(game,base+rva+n*20,20))
        if not any((lookup,stamp,chain,name,iat)):break
        dll=string(base+name)
        for index in range(65536):
            value=struct.unpack('<I',pe_read(game,base+(lookup or iat)+index*4,4))[0]
            if not value:break
            slot=base+iat+index*4
            if slot in addresses:result[slot]=dict(dll=dll,name=('ordinal:'+str(value&65535)) if value&0x80000000 else string(base+value+2))
    require(set(result)==set(addresses),'Import identity missing')
    return result


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_32,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];path=Path(inv['game_directory'])/module['path'];require(digest(path)==module['sha256'],'Source changed');game=path.read_bytes()
    imports=imports_at(game,{0x10b2a434,0x10b2a394});require(imports[0x10b2a434]['name']=='HeapFree' and imports[0x10b2a394]['name']=='GetLastError','Import differs')
    table=list(struct.iter_unpack('<II',pe_read(game,0x10bbb3c8,45*8)))
    def translate(error):
        for code,value in table:
            if code==error:return value
        if 19<=error<=36:return 13
        if 188<=error<=202:return 8
        return 22
    windows=[(0x10603677,5),(0x10613bf7,58),(0x10603a33,67),(0x10603aac,19)]
    uc=Uc(UC_ARCH_X86,UC_MODE_32);pages={0x10b2a000,0x10bbb000,0x10e82000,0x10e47000,0x10617000,0x200000,0x300000,0x500000,0x600000}
    for start,n in windows:pages.update(range(start&~4095,(start+n+4095)&~4095,4096))
    for page in pages:uc.mem_map(page,4096)
    cs=Cs(CS_ARCH_X86,CS_MODE_32);bodies={};evidence=[]
    for start,n in windows:
        raw=pe_read(game,start,n);decoded=list(cs.disasm(raw,start));require(sum(i.size for i in decoded)==n,'Decode differs')
        bodies[start]={i.address for i in decoded};evidence.append(dict(address=f'{start:x}',bytes_hex=raw.hex()));uc.mem_write(start,raw)
    uc.mem_write(0x10bbb3c8,pe_read(game,0x10bbb3c8,360));uc.mem_write(0x10b2a434,struct.pack('<I',0x600000));uc.mem_write(0x10b2a394,struct.pack('<I',0x600010));uc.mem_write(0x10e823cc,struct.pack('<I',0x12345678));uc.mem_write(0x500000,b'\xf4')
    stack=0x200800;thread=0x300100;fallback=0x10e47340;done=[False];visited=set();events=[];writes=[];config=[1,0,thread]
    preserved={reg.UC_X86_REG_EBX:0xa5a5a5a5,reg.UC_X86_REG_EBP:0xb6b6b6b6,reg.UC_X86_REG_ESI:0xc7c7c7c7,reg.UC_X86_REG_EDI:0xd8d8d8d8}
    def hook(machine,address,size,unused):
        if address==0x500000:done[0]=True;machine.emu_stop();return
        if address in (0x600000,0x600010,0x10617acd):
            sp=machine.reg_read(reg.UC_X86_REG_ESP);ret=struct.unpack('<I',machine.mem_read(sp,4))[0]
            if address==0x600000:
                args=list(struct.unpack('<3I',machine.mem_read(sp+4,12)));events.append(['HeapFree',args]);result=config[0];pop=12
            elif address==0x600010:events.append(['GetLastError',[]]);result=config[1];pop=0
            else:events.append(['thread_state',[]]);result=config[2];pop=0
            machine.reg_write(reg.UC_X86_REG_EAX,result);machine.reg_write(reg.UC_X86_REG_ESP,sp+4+pop);machine.reg_write(reg.UC_X86_REG_EIP,ret);return
        require(any(start<=address and address+size<=start+n for start,n in windows),f'Unexpected code {address:x}');visited.add(address)
    uc.hook_add(UC_HOOK_CODE,hook);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)))
    def execute(entry,arg):
        uc.mem_write(stack,struct.pack('<II',0x500000,arg));uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        for r,v in preserved.items():uc.reg_write(r,v)
        events.clear();writes.clear();done[0]=False;uc.emu_start(entry,0,count=1000)
        require(done[0] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'Return/stack differs');require(all(uc.reg_read(r)==v for r,v in preserved.items()),'ABI differs')
    errors=list(range(65536))+[0x7fffffff,0x80000000,0xfffffffe,0xffffffff]
    for error in errors:
        execute(0x10603a33,error);require(uc.reg_read(reg.UC_X86_REG_EAX)==translate(error),'Error translation differs');require(not events,'Unexpected mapper call')
        require(all(stack-16<=p and p+n<=stack for p,n in writes),'Unexpected mapper write')
    cases=0
    def check(error,success,thread_present,pointer):
        nonlocal cases
        config[:]=[success,error,thread if thread_present else 0]
        for address in (thread+16,fallback):uc.mem_write(address,struct.pack('<I',0xdeadbeef))
        execute(0x10603677,pointer)
        expected=[]
        if pointer:expected.append(['HeapFree',[0x12345678,0,pointer]])
        failed=bool(pointer and success==0)
        if failed:expected.extend([['thread_state',[]],['GetLastError',[]]])
        require(events==expected,'Free call sequence differs')
        chosen=thread+16 if thread_present else fallback
        for address in (thread+16,fallback):
            expected_value=translate(error) if failed and address==chosen else 0xdeadbeef
            require(struct.unpack('<I',uc.mem_read(address,4))[0]==expected_value,'Errno write differs')
        require(all(stack-64<=p and p+n<=stack or failed and p==chosen and n==4 for p,n in writes),'Unexpected free write')
        cases+=1
    probes=sorted({0,1,18,19,36,37,187,188,202,203,0x7fffffff,0x80000000,0xffffffff}|{k for k,v in table})
    for error in probes:
        for success in (0,1,0x80000000):
            for thread_present in (False,True):
                for pointer in (0,0x300800):check(error,success,thread_present,pointer)
    for start,pcs in bodies.items():require(pcs<=visited,f'Coverage incomplete {start:x}')
    uc.mem_write(0x10613c2d,b'\x07');uc.ctl_remove_cache(0x10613bf7,0x10613c31)
    caught=False
    try:check(5,0,True,0x300800)
    except Exception as error:
        # Wrong destination uses preserved EDI, producing an unmapped memory write.
        from unicorn import UcError
        require(isinstance(error,UcError),'Unexpected negative failure');caught=True
    require(caught,'Wrong errno destination escaped detector')
    result=dict(source_sha256=module['sha256'],translation_cases=len(errors),free_cases=cases,table=table,
                imports={f'{k:x}':v for k,v in imports.items()},instruction_counts={f'{k:x}':len(v) for k,v in bodies.items()},
                loaded_windows=evidence,negative_control_caught=caught,
                semantics='Null free does nothing. Nonnull free calls HeapFree(global_heap,0,pointer). Failure obtains errno storage, calls GetLastError, translates via 45-entry table then range fallbacks, and writes errno. TLS absence selects global fallback errno.',
                limitation='Original CRT/free/errno-address/translation instructions; HeapFree, GetLastError and thread-state provider have explicit models. No actual OS heap operation, TLS initialization or real failure behavior is proven.')
    root=folder/'heap-free-behavior';root.mkdir(exist_ok=True)
    (root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k not in ('loaded_windows','table')}))


if __name__=='__main__':main()
