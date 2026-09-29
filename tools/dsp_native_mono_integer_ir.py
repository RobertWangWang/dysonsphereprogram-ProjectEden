"""Original integer IR case bodies with explicit pool/vreg allocation models."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_select import selected_folder
from dsp_native_mono_constants import validate_cached as validate_loads


def validate_cached(folder,sha):
    root=folder/'mono-integer-ir-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Integer IR source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Integer IR evidence differs')
    report=read_json(root/'report.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Integer IR dependency differs')
    return report


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE
    import unicorn.x86_const as r
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='mono-2.0-bdwgc.dll');folder=selected_folder(GENERATED/'native',module);source=Path(inv['game_directory'])/module['path']
    require(digest(source)==module['sha256'] and validate_loads(folder,module['sha256']),'Source/load evidence differs');game=source.read_bytes()
    specs=[(0x1802ebae5,0x1802ebd2a,1,False),(0x1802ebd2a,0x1802ebf70,1,True),(0x1802ebf70,0x1802ec1b5,2,False),(0x1802ec1b5,0x1802ec3fb,2,True),
           (0x1802ec400,0x1802ec645,4,True),(0x1802ec645,0x1802ec889,4,False),(0x1802ecd60,0x1802ecf91,8,False)]
    uc=Uc(UC_ARCH_X86,UC_MODE_64);cs=Cs(CS_ARCH_X86,CS_MODE_64);starts=set();rows=[]
    for p in (0x1802eb000,0x1802ec000,0x180183000,0x18029d000):uc.mem_map(p,4096)
    uc.mem_map(0x200000,0x3000)
    for p in range(0x400000,0x460000,0x10000):uc.mem_map(p,4096)
    for a,end,width,signed in specs:
        raw=pe_read(game,a,end-a);decoded=list(cs.disasm(raw,a));require(sum(i.size for i in decoded)==len(raw),'IR decode incomplete');starts.update(i.address for i in decoded);uc.mem_write(a,raw)
        rows.append(dict(address=f'{a:x}',bytes_hex=raw.hex(),instructions=len(decoded),width=width,signed=signed,cases=0))
    for a in (0x180183a60,0x18029d500,0x18029d480):uc.mem_write(a,b'\xc3')
    stop=0x1802ecf9b;uc.mem_write(stop,b'\xf4');frame=0x200100;stack=0x202808
    cfg,block,old,node,slot,field=(0x400100,0x410100,0x420100,0x430100,0x440100,0x450100)
    state={};events=[];visited=set()
    def q(a):return struct.unpack('<Q',uc.mem_read(a,8))[0]
    def put(a,value):uc.mem_write(a,struct.pack('<Q',value))
    def hook(m,a,n,u):
        if a==stop:state['done']=True;m.emu_stop();return
        if a in (0x180183a60,0x18029d500,0x18029d480):
            sp=m.reg_read(r.UC_X86_REG_RSP);events.append([f'{a:x}',m.reg_read(r.UC_X86_REG_RCX),m.reg_read(r.UC_X86_REG_RDX)&0xffffffff if a!=0x18029d480 else None])
            value=node if a==0x180183a60 else state['vreg']
            for reg,v in ((r.UC_X86_REG_RAX,value),(r.UC_X86_REG_RCX,0xabcdef01),(r.UC_X86_REG_RDX,0x23456789),(r.UC_X86_REG_R8,0x34567890),(r.UC_X86_REG_R9,0x45678901)):m.reg_write(reg,v)
            m.reg_write(r.UC_X86_REG_RIP,q(sp));m.reg_write(r.UC_X86_REG_RSP,sp+8);return
        require(a in starts,'IR case escaped');visited.add(a)
    uc.hook_add(UC_HOOK_CODE,hook)
    saved={r.UC_X86_REG_RBX:0x12345678,r.UC_X86_REG_RSI:0x23456789,r.UC_X86_REG_RDI:0x34567890,r.UC_X86_REG_R12:0x45678901}
    def check(spec,value,nonempty,vreg,fill,alignment):
        a,end,width,signed=spec;state.clear();state.update(vreg=vreg);events.clear()
        for p in range(0x400000,0x460000,0x10000):uc.mem_write(p,bytes([fill])*4096)
        uc.mem_write(0x200000,b'\xa5'*0x3000);put(frame+0x1af0,cfg);put(frame+8,slot);put(frame+0x790,field+alignment)
        put(cfg+0x10,0x1234567812345678);put(cfg+0x60,block);put(cfg+0x1d8,0x8765432187654321);put(block,old if nonempty else 0);put(block+0x10,old if nonempty else 0);put(old+0x18,0)
        uc.mem_write(field+alignment,value.to_bytes(width,'little'));cfg_before=bytes(uc.mem_read(cfg,0x200))
        uc.reg_write(r.UC_X86_REG_RBP,frame);uc.reg_write(r.UC_X86_REG_RSP,stack)
        for reg,v in saved.items():uc.reg_write(reg,v)
        uc.emu_start(a,0,count=300)
        require(state.get('done'),'IR case did not join')
        require(events==[['180183a60',0x1234567812345678,0x50],['18029d480' if width==8 else '18029d500',cfg,None if width==8 else 1]],'IR helper calls differ')
        expected=bytearray([fill])*0x50;struct.pack_into('<HBBI',expected,0,0x182 if width==8 else 0x181,2 if width==8 else 1,0,vreg)
        for off in (8,12,16):struct.pack_into('<I',expected,off,0xffffffff)
        for off in (0x18,0x20,0x28,0x30):struct.pack_into('<Q',expected,off,0)
        constant=int.from_bytes(value.to_bytes(width,'little'),'little',signed=signed)&0xffffffffffffffff
        struct.pack_into('<Q',expected,0x20,old if nonempty else 0);struct.pack_into('<Q',expected,0x28,constant);struct.pack_into('<Q',expected,0x38,0x8765432187654321)
        require(bytes(uc.mem_read(node,0x50))==bytes(expected),'IR node differs')
        require(q(slot)==node and q(frame+8)==slot+8,'IR evaluation stack differs')
        require(q(block)==node and q(block+0x10)==(old if nonempty else node) and q(old+0x18)==(node if nonempty else 0),'IR list linkage differs')
        require(bytes(uc.mem_read(cfg,0x200))==cfg_before,'Compilation context modified')
        require(uc.reg_read(r.UC_X86_REG_RSP)==stack and uc.reg_read(r.UC_X86_REG_RBP)==frame and all(uc.reg_read(reg)==v for reg,v in saved.items()),'IR frame/registers differ')
        require(bytes(uc.mem_read(node-8,8))+bytes(uc.mem_read(node+0x50,8))==bytes([fill])*16,'IR allocation guards differ')
    for index,spec in enumerate(specs):
        width=spec[2];sign=1<<(width*8-1);mask=(1<<(width*8))-1
        for value in (0,1,sign-1,sign,sign+1,mask-1,mask):
            for nonempty in (False,True):
                for vreg in (0,7,0xffffffff):
                    for fill in (0,0xa5):
                        for alignment in (0,3):check(spec,value,nonempty,vreg,fill,alignment);rows[index]['cases']+=1
    require(visited==starts,'IR instruction coverage incomplete')
    require(pe_read(game,0x1802ebd20,1)==b'\x08','Stack mutation differs');uc.mem_write(0x1802ebd20,b'\x10');uc.ctl_remove_cache(0x1802ebae5,0x1802ebd2a);caught=False
    try:check(specs[0],1,False,7,0,0)
    except ValueError as error:require(str(error)=='IR evaluation stack differs','Unexpected IR negative failure');caught=True
    require(caught,'Wrong stack advance undetected')
    report=dict(source_sha256=module['sha256'],cases=sum(row['cases'] for row in rows),instructions=len(starts),body_bytes=sum(len(bytes.fromhex(row['bytes_hex'])) for row in rows),ranges=rows,negative_control_caught=caught,
                dependencies={'mono-constant-load-behavior/manifest.json':digest(folder/'mono-constant-load-behavior/manifest.json')},
                semantics='Seven integer case bodies allocate 0x50-byte node, initialize opcode/type/register fields, copy current IL pointer, store extended constant, obtain destination vreg, append to empty/nonempty block list and advance evaluation stack by 8; all end at 1802ecf9b.',
                limitation='Pool allocation and vreg helpers are explicit returning models with valid storage. Begins after type dispatch; no eligibility/GC checks, allocation failures, real compiler execution or generated code validation. Limited boundary values, two node initializations, two alignments and list states.')
    out=folder/'mono-integer-ir-behavior';out.mkdir(exist_ok=True);(out/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(out/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(out/'report.json')}),indent=2),encoding='utf-8');print(json.dumps({k:v for k,v in report.items() if k!='ranges'}))


if __name__=='__main__':main()
