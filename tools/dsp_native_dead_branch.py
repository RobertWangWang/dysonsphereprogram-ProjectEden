"""证明 Unity 指定函数的八处 unreachable 警告来自不可满足的直接分支。"""
import json
import hashlib
from pathlib import Path
import capstone
from capstone import Cs, CS_ARCH_X86, CS_MODE_64, CS_GRP_JUMP, CS_GRP_CALL, CS_GRP_RET
from capstone.x86 import X86_OP_IMM
import unicorn
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R9
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import GAME_SHA, pe_read

ENTRY, END = 0x180cd2ed0, 0x180cd33a2
DEAD_START, DEAD_END = 0x180cd32d2, 0x180cd3343
PATTERNS = {0x180cd3166: '3bd1410f95c10f8546010000', 0x180cd32b8: '4584c97415'}


def require(ok, message):
    if not ok:
        raise ValueError(message)


def simulate(game, left, right, old_r9, corrupt=False):
    m = Uc(UC_ARCH_X86, UC_MODE_64)
    m.mem_map(0x180cd3000, 0x1000)
    m.mem_write(0x180cd3000, pe_read(game, 0x180cd3000, 0x1000))
    if corrupt:
        m.mem_write(0x180cd316a, b'\x94')  # SETNE -> SETE, emulator memory only.
    m.reg_write(UC_X86_REG_RCX, left)
    m.reg_write(UC_X86_REG_RDX, right)
    m.reg_write(UC_X86_REG_R9, old_r9)
    reached = []
    def hook(uc, pc, size, _):
        if pc in (0x180cd3172, 0x180cd32bd, DEAD_START):
            reached.append(pc)
            uc.emu_stop()
    m.hook_add(UC_HOOK_CODE, hook)
    m.emu_start(0x180cd3166, 0, count=16)
    expected = 0x180cd3172 if left == right else 0x180cd32bd
    require(reached == [expected], 'Unsatisfiable branch reached or comparison changed')
    return dict(ecx=left, edx=right, initial_r9=old_r9, destination=f'{expected:x}')


def main():
    base = GENERATED / 'native'
    inventory = read_json(base / 'inventory.json')
    game_path = Path(inventory['game_directory']) / 'UnityPlayer.dll'
    require(digest(game_path) == GAME_SHA, 'Source changed')
    game = game_path.read_bytes()
    for address, hex_bytes in PATTERNS.items():
        require(pe_read(game, address, len(bytes.fromhex(hex_bytes))) == bytes.fromhex(hex_bytes), 'Predicate bytes differ')
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    decoder.detail = True
    code = pe_read(game, ENTRY, END-ENTRY)
    instructions = list(decoder.disasm(code, ENTRY))
    require(sum(i.size for i in instructions) == len(code), 'Incomplete instruction decoding')
    starts = {i.address for i in instructions}
    edges = []
    indirect = []
    for i in instructions:
        fallthrough = i.address+i.size
        if i.group(CS_GRP_RET):
            continue
        if i.group(CS_GRP_JUMP):
            if i.operands[0].type == X86_OP_IMM:
                edges.append((i.address, i.operands[0].imm))
            else:
                indirect.append(i.address)
                require(i.address == 0x180cd3062, 'Unexpected indirect jump')
                edges.extend((i.address,t) for t in (0x180cd3064,0x180cd31de))
            if i.mnemonic == 'jmp':
                continue
        if i.group(CS_GRP_CALL) and i.operands[0].type == X86_OP_IMM:
            require(not ENTRY <= i.operands[0].imm < END, 'Internal call needs separate coverage')
        if fallthrough < END:
            edges.append((i.address, fallthrough))
    require(indirect == [0x180cd3062], 'Unexpected indirect flow inventory')
    require(all(t in starts for _,t in edges), 'Control flow leaves decoded function')
    incoming = [(s,t) for s,t in edges if DEAD_START <= t < DEAD_END and not DEAD_START <= s < DEAD_END]
    guard_incoming = [(s,t) for s,t in edges if t == 0x180cd32b8]
    require(incoming == [(0x180cd32bb, DEAD_START)], 'Unexpected entry into removed region')
    require(guard_incoming == [(0x180cd316c,0x180cd32b8)], 'Unexpected entry into predicate block')
    values = [0,1,0x7fffffff,0x80000000,0xffffffff]
    cases = [simulate(game,a,b,r9) for a in values for b in values for r9 in (0,0xffffffffffffffff)]
    try:
        simulate(game,0,1,0,True)
    except ValueError as e:
        require(str(e) == 'Unsatisfiable branch reached or comparison changed', 'Unexpected mutation failure')
    else:
        raise ValueError('Predicate mutation not detected')
    root = base / 'UnityPlayer.dll/quality-repair/180cd2ed0'
    report = dict(source_sha256=GAME_SHA, address=f'{ENTRY:x}', code_sha256=hashlib.sha256(code).hexdigest(),
                  decoded_bytes=len(code), instructions=len(instructions),
                  dead_range=[f'{DEAD_START:x}',f'{DEAD_END:x}'],
                  predicate_bytes={f'{a:x}':b for a,b in PATTERNS.items()},
                  incoming=[[f'{a:x}',f'{b:x}'] for a,b in incoming],
                  guard_incoming=[[f'{a:x}',f'{b:x}'] for a,b in guard_incoming],
                  proof='CMP sets ZF; SETNE writes !ZF without changing flags; JNE reaches TEST only with R9B=1; TEST R9B,R9B sets ZF=0, so JE into removed region is never taken.',
                  scope='Intraprocedural normal flow from function entry, known switch destinations and calls returning normally. Not a proof about arbitrary external jumps or whole-function semantics.',
                  capstone=capstone.__version__, unicorn=unicorn.__version__, cases=cases,
                  negative_predicate_mutation_rejected=True)
    (root/'dead-branch-proof.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print('Verified',len(code),'bytes,',len(instructions),'instructions; unique impossible entry; 50 CPU cases and negative mutation passed')


if __name__ == '__main__':
    main()
