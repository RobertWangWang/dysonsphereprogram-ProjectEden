"""模拟 Unity 多级 switch 的正常入口行为；外部复制调用仅记录参数并模拟返回。"""
import json
import struct
from pathlib import Path
import unicorn
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import *
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import GAME_SHA, pe_read


def run(game, kind, stride, offset, has_source, corrupt=False):
    m = Uc(UC_ARCH_X86, UC_MODE_64)
    for base in (0x18177f000, 0x181835000, 0x1000000, 0x2000000, 0x3000000):
        m.mem_map(base, 0x1000)
    m.mem_write(0x18177f000, pe_read(game,0x18177f000,0x1000))
    if corrupt:
        m.mem_write(0x18177fb3c+5*4, (0x177fa38).to_bytes(4,'little'))
    # Stub the one external callee; do not execute its game implementation.
    m.mem_write(0x181835930,b'\xc3')
    m.mem_write(0x2000000,b'\x90')
    obj, stack = 0x1000000, 0x3000808
    source, destination = (0x4000000 if has_source else 0), 0x5000000
    for field,value,size in [(0x28,kind,4),(0x60,stride,4),(0x230,destination,8),(0x240,source,8),(0x250,offset,4)]:
        m.mem_write(obj+field,value.to_bytes(size,'little'))
    m.mem_write(stack,(0x2000000).to_bytes(8,'little'))
    m.reg_write(UC_X86_REG_RSP,stack)
    m.reg_write(UC_X86_REG_RCX,obj)
    calls, returned = [], []
    def hook(uc,pc,size,_):
        if pc == 0x181835930:
            calls.append([uc.reg_read(r) for r in (UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8)])
        if pc == 0x2000000:
            returned.append(uc.reg_read(UC_X86_REG_RAX))
            uc.emu_stop()
    m.hook_add(UC_HOOK_CODE,hook)
    m.emu_start(0x18177f9f0,0,count=256)
    active = 1 <= kind <= 5 and has_source and offset != 0
    expected_calls = [[destination+offset,source,([4,8,12,16,16][kind-1]*stride)&0xffffffff]] if active else []
    after_offset = int.from_bytes(m.mem_read(obj+0x250,4),'little')
    if (returned != [0] or calls != expected_calls or after_offset != (0 if active else offset)
            or m.reg_read(UC_X86_REG_RSP) != stack+8):
        raise ValueError('Normal-entry behavior differs')
    return dict(kind=kind,stride=stride,offset=offset,source_present=has_source,calls=calls,return_value=0,offset_after=after_offset)


def main():
    base = GENERATED/'native'
    inventory = read_json(base/'inventory.json')
    path = Path(inventory['game_directory'])/'UnityPlayer.dll'
    if digest(path) != GAME_SHA:
        raise ValueError('Game baseline changed')
    game = path.read_bytes()
    kinds = list(range(20))+[0x7fffffff,0x80000000,0xffffffff]
    cases = [run(game,k,s,o,p) for k in kinds for s in (0,1,7,0xffffffff)
             for o in (0,4,0xfffffffc) for p in (False,True)]
    try:
        run(game,5,1,4,True,True)
    except ValueError as e:
        if str(e) != 'Normal-entry behavior differs':
            raise
    else:
        raise ValueError('Case-5 mutation was not detected')
    root=base/'UnityPlayer.dll/quality-repair/18177f9f0'
    repair = read_json(root/'report.json')
    definitions = [(0x18177fa36,0x18177fb3c,0),(0x18177fa8b,0x18177fb7c,0),
                   (0x18177fac0,0x18177fbbc,0),(0x18177fad6,0x18177fbfc,0x18177fc04),
                   (0x18177fae9,0x18177fc14,0),(0x18177faff,0x18177fc54,0x18177fc5c)]
    if repair['source_sha256'] != GAME_SHA or not repair['program_changes_rolled_back'] or len(repair['tables']) != 6:
        raise ValueError('Repair provenance mismatch')
    for table,(branch,rva,index) in zip(repair['tables'],definitions):
        if tuple(int(table[k],16) for k in ('branch','rva_table','byte_index_table')) != (branch,rva,index):
            raise ValueError('Table metadata mismatch')
        count = 2 if index else 16
        targets = struct.unpack('<'+'I'*count,pe_read(game,rva,count*4))
        indices = pe_read(game,index,16) if index else range(16)
        expected = [f'{0x180000000+targets[i]:x}' for i in indices]
        if table['targets_by_input'] != expected:
            raise ValueError('Case mapping mismatch')
    code = (root/'18177f9f0.c').read_text(encoding='utf-8')
    if 'case 5:' not in code or 'halt_baddata' in code:
        raise ValueError('Candidate lost case 5 or still truncates bad data')
    report=dict(source_sha256=GAME_SHA,address='18177f9f0',engine='Unicorn '+unicorn.__version__,
                scope='Normal entry; external copy implementation replaced by RET after argument capture. Does not verify copying memory, external side effects or generated C equivalence.',
                cases=cases,negative_case5_mutation_rejected=True,status='candidate-not-promoted',
                raw_table_mappings_verified=96,
                candidate_files={n:digest(root/n) for n in ('report.json','18177f9f0.c')})
    (root/'normal-entry-probe.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(len(cases),'normal-entry cases passed; corrupted case-5 table detected; C remains candidate')


if __name__ == '__main__':
    main()
