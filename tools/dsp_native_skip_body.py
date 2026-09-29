"""Validate recovered 10630d30 body and exhaust finite classification inputs offline."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require


def validate_cached(folder, sha):
    root = folder / 'skip-body-repair'
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json')
    require(marker['source_sha256'] == sha, 'Skip source differs')
    for name, value in marker['files'].items():
        require(digest(root / name) == value, 'Skip evidence differs')
    return read_json(root / 'verification.json')


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32, CS_GRP_JUMP, CS_GRP_RET
    from capstone.x86 import X86_OP_IMM
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
    from unicorn.x86_const import UC_X86_REG_ESP, UC_X86_REG_EAX, UC_X86_REG_ECX, UC_X86_REG_EFLAGS, UC_X86_REG_EBX, UC_X86_REG_EBP, UC_X86_REG_ESI, UC_X86_REG_EDI
    inv = read_json(GENERATED / 'native/inventory.json')
    module = next(m for m in inv['files'] if Path(m['path']).name == 'rail_api.dll')
    folder = GENERATED / 'native' / module['output']; root = folder / 'skip-body-repair'
    path = Path(inv['game_directory']) / module['path']
    report = read_json(root / 'report.json')
    require(digest(path) == report['source_sha256'] == module['sha256'] and report['program_changes_rolled_back'], 'Source/export differs')
    game = path.read_bytes(); cs = Cs(CS_ARCH_X86, CS_MODE_32); cs.detail = True
    instructions = {}; covered = set()
    for line in (root / '10630d30.asm').read_text().splitlines():
        address, data, _ = line.split(' ', 2); pc = int(address, 16); raw = bytes.fromhex(data)
        require(pe_read(game, pc, len(raw)) == raw, 'Bytes differ')
        decoded = list(cs.disasm(raw, pc)); require(len(decoded) == 1 and decoded[0].size == len(raw), 'Boundary differs')
        span = set(range(pc, pc + len(raw))); require(not covered & span, 'Overlap')
        covered |= span; instructions[pc] = decoded[0]
    require(covered == set(range(0x10630d30, 0x10630d78)) and len(instructions) == report['instruction_count'] == 22, 'Incomplete body')
    table = struct.unpack('<2I', pe_read(game, 0x10630d78, 8)); indices = pe_read(game, 0x10630d80, 40)
    require(table == (0x10630d77, 0x10630d77) and set(indices) == {0, 1}, 'Table differs')
    reached = set(); pending = [0x10630d30]
    while pending:
        pc = pending.pop()
        if pc in reached: continue
        require(pc in instructions, 'Flow escapes body'); reached.add(pc); ins = instructions[pc]
        if ins.group(CS_GRP_RET): continue
        if ins.group(CS_GRP_JUMP):
            if ins.operands[0].type == X86_OP_IMM: pending.append(ins.operands[0].imm)
            else:
                require(pc == 0x10630d70, 'Unknown indirect branch'); pending.extend(table)
            if ins.mnemonic == 'jmp': continue
        pending.append(pc + ins.size)
    require(reached == set(instructions), 'Unreachable instructions')
    uc = Uc(UC_ARCH_X86, UC_MODE_32)
    uc.mem_map(0x10630000, 0x2000); uc.mem_write(0x10630d30, pe_read(game, 0x10630d30, 120))
    for base in (0x200000, 0x300000, 0x400000, 0x500000): uc.mem_map(base, 0x1000)
    sentinel = 0x500000; stack = 0x400800; obj = 0x200000; data = 0x300000
    uc.mem_write(sentinel, b'\xf4'); uc.mem_write(stack, struct.pack('<III', sentinel, obj, data))
    visited = set(); writes = []; hit = [False]
    def code_hook(machine, address, size, unused):
        if address == sentinel: hit[0] = True; machine.emu_stop()
        else: visited.add(address)
    uc.hook_add(UC_HOOK_CODE, code_hook)
    uc.hook_add(UC_HOOK_MEM_WRITE, lambda machine, access, address, size, value, unused: writes.append((address, size)))
    preserved = (UC_X86_REG_EBX, UC_X86_REG_EBP, UC_X86_REG_ESI, UC_X86_REG_EDI)
    cases = 0
    def run(payload, classification):
        nonlocal cases
        require(len(classification) == 256 and len(payload) <= 512, 'Bad test input')
        uc.mem_write(obj + 0x4c, classification); uc.mem_write(data, payload)
        uc.reg_write(UC_X86_REG_ESP, stack); uc.reg_write(UC_X86_REG_ECX, 0xdeadbeef); uc.reg_write(UC_X86_REG_EFLAGS, 2)
        for reg in preserved: uc.reg_write(reg, 0x12340000 + reg)
        expected = 0
        while payload[expected + 1] == 0 and classification[payload[expected]] in (9, 10, 21): expected += 2
        hit[0] = False; writes.clear(); uc.emu_start(0x10630d30, 0, count=4096)
        require(hit[0] and uc.reg_read(UC_X86_REG_EAX) == data + expected, 'Return mismatch')
        require(uc.reg_read(UC_X86_REG_ESP) == stack + 4 and not writes, 'Stack/write mismatch')
        require(all(uc.reg_read(r) == 0x12340000 + r for r in preserved), 'Preserved register mismatch')
        cases += 1
    # All 65,536 two-byte units with one nonzero-high-byte terminator.
    fixed = bytes(range(256))
    for value in range(65536): run(struct.pack('<H', value) + b'\x00\x01', fixed)
    # Every possible low byte under every classification byte.
    for kind in range(256):
        classification = bytes([kind]) * 256
        for low in range(256): run(bytes([low, 0, 0, 1]), classification)
    # Repeated accepted units, all three accepted classes, varied stopping high byte.
    for length in range(128):
        run(bytes([9, 0, 10, 0, 21, 0]) * (length // 3) + bytes([9, 0]) * (length % 3) + b'\x00\xff', fixed)
    require(visited == set(instructions), 'Dynamic instruction coverage incomplete')
    # Negative control: a wrong stride must fail the first accepted-unit case.
    uc.mem_write(0x10630d58, b'\x04'); uc.ctl_remove_cache(0x10630d30, 0x10630d78)
    caught = False
    try: run(b'\x09\x00\x00\x01\x00\x01', fixed)
    except (ValueError, AssertionError): caught = True
    require(caught, 'Mutation escaped detector')
    result = dict(address='10630d30', file='10630d30.c', body_bytes=72, instructions=22, cases=cases,
                  table_targets=[f'{v:x}' for v in table], table_indices=list(indices), negative_control_caught=caught,
                  semantics='Advance by two while high byte is zero and low-byte class at object+0x4c is 9, 10 or 21. Return first other unit address.',
                  limitation='Finite offline original-instruction tests; readable terminated buffers assumed. No end-pointer check exists. Ghidra C type inference is not a compiled equivalence proof.')
    (root / 'verification.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    files = ['report.json', '10630d30.c', '10630d30.asm', 'verification.json']
    (root / 'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'], files={n:digest(root/n) for n in files}), indent=2), encoding='utf-8')
    print(json.dumps(result))


if __name__ == '__main__': main()
