"""Verify full scanner instruction coverage, static control flow and both table dispatches."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require


def validate_cached(folder, sha):
    root = folder / 'scanner-body-repair'
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json'); require(marker['source_sha256'] == sha, 'Scanner source differs')
    for name, value in marker['files'].items(): require(digest(root / name) == value, 'Scanner evidence differs')
    return read_json(root / 'verification.json')


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32, CS_GRP_JUMP, CS_GRP_CALL, CS_GRP_RET
    from capstone.x86 import X86_OP_IMM
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
    from unicorn.x86_const import UC_X86_REG_EAX, UC_X86_REG_EIP, UC_X86_REG_EFLAGS
    base = GENERATED / 'native/DSPGAME_Data__Plugins__x86_64__rail_api.dll'; root = base / 'scanner-body-repair'
    report = read_json(root / 'report.json')
    path = Path(read_json(GENERATED / 'native/inventory.json')['game_directory']) / 'DSPGAME_Data/Plugins/x86_64/rail_api.dll'
    require(digest(path) == report['source_sha256'] and report['status'] == 'decompiled' and report['program_changes_rolled_back'], 'Source/export mismatch')
    game = path.read_bytes(); decoder = Cs(CS_ARCH_X86, CS_MODE_32); decoder.detail = True
    instructions = {}; seen = set()
    for line in (root / '10632090.asm').read_text().splitlines():
        address, hexbytes, _ = line.split(' ', 2); pc = int(address, 16); raw = bytes.fromhex(hexbytes)
        require(pe_read(game, pc, len(raw)) == raw, 'Scanner bytes differ')
        items = list(decoder.disasm(raw, pc)); require(len(items) == 1 and items[0].size == len(raw), 'Instruction boundary differs')
        span = set(range(pc, pc + len(raw))); require(not seen.intersection(span), 'Instruction overlap'); seen |= span; instructions[pc] = items[0]
    require(seen == set(range(0x10632090, 0x10632223)) and len(instructions) == report['instruction_count'], 'Incomplete scanner body')
    table1 = list(struct.unpack('<11I', pe_read(game, 0x10632224, 44)))
    table2 = list(struct.unpack('<5I', pe_read(game, 0x10632250, 20)))
    indices = list(pe_read(game, 0x10632264, 11)); require(max(indices) < len(table2), 'Index escapes second table')
    mappings = {0x106320e5: table1, 0x106321d9: [table2[n] for n in indices]}
    for row in report['tables']:
        expected = table1 if row['table'] == '10632224' else table2
        require([int(v, 16) for v in row['targets']] == expected, 'Ghidra table differs')
    reached = set(); pending = [0x10632090]; calls = set(); edges = []
    while pending:
        pc = pending.pop()
        if pc in reached: continue
        require(pc in instructions, 'Control flow escapes known instruction boundaries'); reached.add(pc); ins = instructions[pc]
        successors = []
        if ins.group(CS_GRP_RET): pass
        elif ins.group(CS_GRP_JUMP):
            if ins.operands[0].type == X86_OP_IMM: successors.append(ins.operands[0].imm & 0xffffffff)
            else:
                require(pc in mappings, 'Unresolved indirect transfer'); successors.extend(set(mappings[pc]))
            if ins.mnemonic != 'jmp': successors.append(pc + ins.size)
        else:
            if ins.group(CS_GRP_CALL):
                require(ins.operands[0].type == X86_OP_IMM, 'Unresolved indirect call'); calls.add(ins.operands[0].imm & 0xffffffff)
            successors.append(pc + ins.size)
        edges.extend(dict(source=f'{pc:x}', target=f'{dst:x}') for dst in successors); pending.extend(successors)
    require(reached == set(instructions), 'Export includes statically unreachable instructions')
    require(calls == {0x106392d0}, 'Unexpected helper dependency')
    uc = Uc(UC_ARCH_X86, UC_MODE_32); uc.mem_map(0x10632000, 0x1000)
    uc.mem_write(0x10632090, pe_read(game, 0x10632090, 0x1df))
    active = [0, 0]; visited = set()
    def hook(machine, address, size, unused):
        if not active[0] <= address < active[1]: machine.emu_stop()
        else: visited.add(address)
    uc.hook_add(UC_HOOK_CODE, hook); cases = 0
    for start, end, branch, default in [(0x106320dc, 0x106320ec, 0x106320e5, 0x106321ab), (0x106321cd, 0x106321e0, 0x106321d9, 0x106321e9)]:
        for value in list(range(256)) + [0x7fffffff, 0x80000000, 0xfffffffe, 0xffffffff]:
            active[:] = [start, end]; uc.reg_write(UC_X86_REG_EAX, value); uc.reg_write(UC_X86_REG_EFLAGS, 2)
            uc.emu_start(start, 0, count=16)
            require(uc.reg_read(UC_X86_REG_EIP) == (mappings[branch][value] if value <= 10 else default), 'Dispatch differs'); cases += 1
    original = bytes(uc.mem_read(0x10632264, 1)); uc.mem_write(0x10632264, bytes([(indices[0] + 1) % 5]))
    active[:] = [0x106321cd, 0x106321e0]; uc.reg_write(UC_X86_REG_EAX, 0); uc.reg_write(UC_X86_REG_EFLAGS, 2); uc.emu_start(active[0], 0, count=16)
    require(uc.reg_read(UC_X86_REG_EIP) != mappings[0x106321d9][0], 'Wrong byte-index mapping was not detected'); uc.mem_write(0x10632264, original)
    code = (root / '10632090.c').read_text(); require('/* WARNING:' not in code, 'Unexpected C warning')
    result = dict(source_sha256=report['source_sha256'], address='10632090', file='10632090.c', body_bytes=len(seen), instructions=len(instructions),
                  statically_reached_instructions=len(reached), calls=[f'{c:x}' for c in calls], edges=edges,
                  mappings={f'{k:x}': [f'{v:x}' for v in values] for k, values in mappings.items()}, byte_indices=indices,
                  dispatch_cases=cases, dispatch_instructions_visited=len(visited), negative_byte_index_rejected=True,
                  limitation='Full interval and static CFG verified, treating the external helper as returning. Dispatch tests stop at target blocks. Helper register/ABI effects, scanner behavior and inferred C types remain unproven.')
    (root / 'verification.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    names = ('report.json', '10632090.c', '10632090.asm', 'verification.json')
    (root / 'manifest.json').write_text(json.dumps(dict(source_sha256=report['source_sha256'], files={n: digest(root / n) for n in names}), indent=2), encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k not in ('edges', 'mappings')}))


if __name__ == '__main__': main()
