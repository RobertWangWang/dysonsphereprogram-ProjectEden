"""Execute original read/extend/store slices for Mono readonly-field constants."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_select import selected_folder
from dsp_native_mono_cases import validate_nested_cached


def validate_cached(folder,sha):
    root=folder/'mono-constant-load-behavior'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Mono constant source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Mono constant evidence differs')
    report=read_json(root/'report.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Mono constant dependency differs')
    return report


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_WRITE,UC_HOOK_MEM_READ
    import unicorn.x86_const as r
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='mono-2.0-bdwgc.dll');folder=selected_folder(GENERATED/'native',module)
    source=Path(inv['game_directory'])/module['path'];require(digest(source)==module['sha256'],'Source changed');game=source.read_bytes();require(validate_nested_cached(folder,module['sha256']),'Missing type evidence')
    specs=[(0x1802ebc18,13,1,False,[2,5]),(0x1802ebe5d,14,1,True,[4]),(0x1802ec0a3,13,2,False,[3,7]),(0x1802ec2e8,14,2,True,[6]),
           (0x1802ec533,13,4,True,[8]),(0x1802ec778,12,4,False,[9]),(0x1802ec9bc,13,8,False,[15,24,25,27]),
           (0x1802ecc28,13,8,False,[14,18,20,28,29]),(0x1802eceae,13,8,False,[10,11])]
    uc=Uc(UC_ARCH_X86,UC_MODE_64)
    for page in (0x1802eb000,0x1802ec000,0x200000,0x300000,0x400000):uc.mem_map(page,4096)
    cs=Cs(CS_ARCH_X86,CS_MODE_64);all_starts=set();rows=[];ends=set()
    for a,n,width,signed,types in specs:
        raw=pe_read(game,a,n);decoded=list(cs.disasm(raw,a));require(len(decoded)==3 and sum(i.size for i in decoded)==n,'Load slice boundary differs');uc.mem_write(a,raw);uc.mem_write(a+n,b'\xf4');ends.add(a+n);all_starts.update(i.address for i in decoded)
        rows.append(dict(address=f'{a:x}',bytes_hex=raw.hex(),width=width,signed=signed,types=types,instructions=3,cases=0))
    visited=set();writes=[];reads=[];state={};frame=0x200100;dest=0x400108
    def code(m,a,n,u):
        if a in ends:state['done']=True;m.emu_stop();return
        require(a in all_starts,'Constant slice escaped');visited.add(a)
    uc.hook_add(UC_HOOK_CODE,code);uc.hook_add(UC_HOOK_MEM_WRITE,lambda m,a,p,n,v,u:writes.append((p,n)));uc.hook_add(UC_HOOK_MEM_READ,lambda m,a,p,n,v,u:reads.append((p,n)))
    def check(spec,value,alignment):
        a,n,width,signed,_=spec;ptr=0x300100+alignment;data=value.to_bytes(width,'little');expected=int.from_bytes(data,'little',signed=signed)&0xffffffffffffffff
        uc.mem_write(0x3000f0,b'\xa5'*64);uc.mem_write(ptr,data);uc.mem_write(frame+0x790,struct.pack('<Q',ptr));uc.mem_write(dest-8,b'\xa5'*24)
        uc.reg_write(r.UC_X86_REG_RBP,frame);uc.reg_write(r.UC_X86_REG_RAX,dest);uc.reg_write(r.UC_X86_REG_RCX,0x1122334455667788);uc.reg_write(r.UC_X86_REG_RSP,0x200f00);uc.reg_write(r.UC_X86_REG_EFLAGS,0x8d7)
        reads.clear();writes.clear();state.clear();uc.emu_start(a,0,count=4)
        require(state.get('done') and struct.unpack('<Q',uc.mem_read(dest,8))[0]==expected,'Constant value differs')
        require(uc.reg_read(r.UC_X86_REG_RCX)==expected and uc.reg_read(r.UC_X86_REG_RAX)==dest and uc.reg_read(r.UC_X86_REG_RBP)==frame and uc.reg_read(r.UC_X86_REG_RSP)==0x200f00,'Constant registers differ')
        require(reads==[(frame+0x790,8),(ptr,width)] and writes==[(dest,8)],'Constant memory access differs')
        require(bytes(uc.mem_read(dest-8,8))+bytes(uc.mem_read(dest+8,8))==b'\xa5'*16,'Constant output guards differ')
        require(bytes(uc.mem_read(ptr,width))==data and uc.reg_read(r.UC_X86_REG_EFLAGS)==0x8d7,'Constant input/flags changed')
    for index,spec in enumerate(specs):
        width=spec[2];mask=(1<<(8*width))-1;sign=1<<(8*width-1)
        values=range(1<<(width*8)) if width<=2 else sorted(set(range(256))|{sign-1,sign,sign+1,mask-1,mask,0x7fffffff,0x80000000,0xffffffff})
        for value in values:check(spec,value,0);rows[index]['cases']+=1
        for alignment in range(1,8):
            for value in (0,1,sign-1,sign,sign+1,mask-1,mask):check(spec,value,alignment);rows[index]['cases']+=1
    require(visited==all_starts,'Constant instruction coverage incomplete')
    require(pe_read(game,0x1802ebc20,1)==b'\xb6','Negative site differs');uc.mem_write(0x1802ebc20,b'\xbe');uc.ctl_remove_cache(0x1802ebc18,0x1802ebc25);caught=False
    try:check(specs[0],128,0)
    except ValueError as error:require(str(error)=='Constant value differs','Unexpected mutation error');caught=True
    require(caught,'Wrong signedness undetected')
    report=dict(source_sha256=module['sha256'],cases=sum(row['cases'] for row in rows),instructions=len(all_starts),slices=rows,negative_control_caught=caught,
                dependencies={'mono-type-case-comparison/manifest.json':digest(folder/'mono-type-case-comparison/manifest.json')},
                semantics='Each slice reads the field address from RBP+790, loads 1/2/4/8 bytes with signed or unsigned extension as specified, and writes an eight-byte constant to incoming RAX. 64-bit groups preserve bits. EFLAGS and frame/stack registers are preserved.',
                limitation='Synthetic valid field pointer and prepared destination. Only three-instruction read/extend/store slices executed; allocation, IR opcode, list insertion, branch eligibility and GC checks not executed. Reference-pointer slice assumes the prior branch allowed it. Exhaustive values only for 8/16-bit widths.')
    out=folder/'mono-constant-load-behavior';out.mkdir(exist_ok=True);(out/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8');(out/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(out/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps(report))


if __name__=='__main__':main()
