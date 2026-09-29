"""Verify exact recovered native intervals, their tables and static control flow."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require

PROFILES = {
    '104c2d30': (0x104c2e60, 0x104c2d57, 0x104c2e64, 12, 0x104c2e94, 130),
    '1062fb70': (0x1062fc4b, 0x1062fbaf, 0x1062fc4c, 4, 0x1062fc5c, 55),
    '10632ce0': (0x10632d49, 0, 0, 0, 0, 0),
}


def validate_cached(folder, sha):
    root = folder / 'continuation-body-repair'
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json')
    require(marker['source_sha256'] == sha, 'Continuation source differs')
    for name, value in marker['files'].items(): require(digest(root / name) == value, 'Continuation evidence differs')
    return read_json(root / 'verification.json')


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32, CS_GRP_JUMP, CS_GRP_RET, CS_GRP_CALL
    from capstone.x86 import X86_OP_IMM
    inv = read_json(GENERATED / 'native/inventory.json')
    module = next(m for m in inv['files'] if Path(m['path']).name == 'rail_api.dll')
    path = Path(inv['game_directory']) / module['path']; root = GENERATED / 'native' / module['output'] / 'continuation-body-repair'
    report = read_json(root / 'report.json')
    require(digest(path) == report['source_sha256'] == module['sha256'] and report['program_changes_rolled_back'], 'Source/export differs')
    game = path.read_bytes(); cs = Cs(CS_ARCH_X86, CS_MODE_32); cs.detail = True
    results = []; files = ['report.json', 'verification.json']
    require({r['address'] for r in report['functions']} == set(PROFILES), 'Export scope differs')
    for row in report['functions']:
        entry = row['address']; start = int(entry, 16); end, branch, table, count, index, slots = PROFILES[entry]
        require(int(row['end'], 16) == end and row['body_bytes'] == end-start+1, 'Range differs')
        instructions = {}; covered = set()
        for line in (root / (entry + '.asm')).read_text().splitlines():
            address, data, _ = line.split(' ', 2); pc = int(address, 16); raw = bytes.fromhex(data)
            require(pe_read(game, pc, len(raw)) == raw, 'Bytes differ')
            decoded = list(cs.disasm(raw, pc)); require(len(decoded) == 1 and decoded[0].size == len(raw), 'Boundary differs')
            span = set(range(pc, pc + len(raw))); require(not covered & span, 'Overlap')
            covered |= span; instructions[pc] = decoded[0]
        require(covered == set(range(start, end+1)) and len(instructions) == row['instruction_count'], 'Incomplete body')
        targets = struct.unpack('<' + 'I'*count, pe_read(game, table, count*4)) if count else ()
        indices = list(pe_read(game, index, slots)) if slots else []
        require([f'{v:x}' for v in targets] == row['table_targets'] and all(i < count for i in indices), 'Table differs')
        reached = set(); pending = [start]; calls = set()
        while pending:
            pc = pending.pop()
            if pc in reached: continue
            require(pc in instructions, 'Flow escapes body'); reached.add(pc); ins = instructions[pc]
            if ins.group(CS_GRP_RET): continue
            if ins.group(CS_GRP_CALL):
                require(ins.operands[0].type == X86_OP_IMM, 'Unknown indirect call'); calls.add(ins.operands[0].imm)
            if ins.group(CS_GRP_JUMP):
                if ins.operands[0].type == X86_OP_IMM: pending.append(ins.operands[0].imm)
                else:
                    require(pc == branch, 'Unknown indirect branch'); pending.extend(targets[i] for i in indices)
                if ins.mnemonic == 'jmp': continue
            pending.append(pc + ins.size)
        padding = set(instructions) - reached
        require(all(instructions[pc].mnemonic == 'nop' for pc in padding), 'Unreachable non-padding instructions')
        expected_calls = {0x105fb540, 0x104c3040, 0x104c30c0} if entry == '104c2d30' else set()
        require(calls == expected_calls, 'Unexpected external dependency')
        results.append(dict(address=entry, file=entry+'.c', body_bytes=len(covered), instructions=len(instructions),
                            reachable_instructions=len(reached), padding=[f'{pc:x}' for pc in sorted(padding)],
                            calls=[f'{pc:x}' for pc in sorted(calls)], table_targets=[f'{v:x}' for v in targets], indices=indices))
        files.extend([entry+'.asm', entry+'.c'])
    result = dict(functions=results, limitation='Byte coverage and static control flow. External calls assumed to return. ABI types are evidence-based annotations, not recovered original declarations.')
    (root / 'verification.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    (root / 'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'], files={n:digest(root/n) for n in files}), indent=2), encoding='utf-8')
    print(json.dumps(result))


if __name__ == '__main__': main()
