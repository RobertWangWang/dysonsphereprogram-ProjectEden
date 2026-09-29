"""Verify the enclosing SSL switch, correct omitted duplicate case labels, and probe original x86."""
import json
import re
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require

SHA = 'c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1'
PARENT = '105245a0'
LABELS = ('1052489e', '105248a6', '105248ae')


def validate_cached(folder, sha):
    root = folder / 'ssl-switch-repair'
    if not (root / 'manifest.json').exists(): return {}
    manifest = read_json(root / 'manifest.json')
    require(manifest['source_sha256'] == sha, 'SSL repair source differs')
    for name, value in manifest['files'].items(): require(digest(root / name) == value, 'SSL repair artifact differs')
    return read_json(root / 'verification.json')


def c_mapping(code):
    block = code.split('switch(local_1c0) {')[1].split('FUN_1055c510(')[0]
    parts = re.split(r'(default:|case\s+\d+:)', block)
    pending, mapping = [], {}
    for n in range(1, len(parts), 2):
        label = parts[n]; pending.append('default' if label == 'default:' else int(re.search(r'\d+', label)[0]))
        body = parts[n + 1].strip()
        if not body: continue
        if 'FUN_10525f90' in body: action = 'helper'
        elif 'goto switchD_1052478d_caseD_2' in body: action = 'error'
        elif '| 0x1c000000' in body: action = 'or-1c'
        elif '| 0x2000000' in body: action = 'or-02'
        else: raise ValueError('Unrecognized corrected C case')
        for key in pending: mapping[key] = action
        pending = []
    return [mapping.get(i, mapping['default']) for i in range(8)]


def main():
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
    from unicorn.x86_const import UC_X86_REG_EBP, UC_X86_REG_EAX, UC_X86_REG_ECX, UC_X86_REG_ESP, UC_X86_REG_EIP, UC_X86_REG_EFLAGS
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    root = GENERATED / 'native/DSPGAME_Data__Plugins__x86_64__rail_api.dll/ssl-switch-repair'
    game_path = Path(read_json(GENERATED / 'native/inventory.json')['game_directory']) / 'DSPGAME_Data/Plugins/x86_64/rail_api.dll'
    require(digest(game_path) == SHA, 'Game changed'); game = game_path.read_bytes()
    report = read_json(root / 'report.json')
    require(report['source_sha256'] == SHA and report['status'] == 'decompiled' and report['program_changes_rolled_back'], 'Repair export incomplete')
    decoder = Cs(CS_ARCH_X86, CS_MODE_32); seen = set(); count = 0
    for line in (root / (PARENT + '.asm')).read_text().splitlines():
        site, hexbytes, _ = line.split(' ', 2); pc = int(site, 16); raw = bytes.fromhex(hexbytes)
        require(pe_read(game, pc, len(raw)) == raw, 'Body bytes differ')
        decoded = list(decoder.disasm(raw, pc)); require(len(decoded) == 1 and decoded[0].size == len(raw), 'Instruction boundary differs')
        span = set(range(pc, pc + len(raw))); require(not seen.intersection(span), 'Overlapping instructions'); seen |= span; count += 1
    require(seen == set(range(0x105245a0, 0x10524eaa)) and count == report['instruction_count'], 'Incomplete body coverage')
    table1 = list(struct.unpack('<8I', pe_read(game, 0x10524eac, 32)))
    table2 = list(struct.unpack('<8I', pe_read(game, 0x10524ecc, 32)))
    require(table2 == [int(v, 16) for v in report['targets']], 'Repair table mismatch')
    require(set(int(a, 16) for a in LABELS) <= set(table2), 'Failed entries are not switch labels')
    actions = ['or-02', 'or-02', 'error', 'or-1c', 'helper', 'helper', 'helper', 'helper']
    raw_c = (root / (PARENT + '.c')).read_text()
    before = '    case 4:\n      iVar6 = FUN_10525f90'
    after = '    case 4:\n    case 5:\n    case 6:\n    case 7:\n      iVar6 = FUN_10525f90'
    require(raw_c.count(before) == 1, 'Raw C layout changed')
    corrected = raw_c.replace(before, after)
    require(c_mapping(corrected) == actions, 'Corrected C cases differ from machine table')
    require(c_mapping(raw_c) != actions, 'Expected duplicate-label defect no longer present')
    require(c_mapping(corrected.replace('    case 5:\n', '', 1)) != actions, 'Negative case omission was not detected')
    (root / '105245a0.corrected.c').write_text('/* Duplicate labels 5,6,7 restored from original table 10524ecc. Raw Ghidra C retained separately. */\n' + corrected, encoding='utf-8')
    uc = Uc(UC_ARCH_X86, UC_MODE_32); uc.mem_map(0x10524000, 0x2000); uc.mem_map(0x200000, 0x2000)
    uc.mem_write(0x105245a0, pe_read(game, 0x105245a0, 0x94c))
    stops = set(); visited = set()
    def hook(machine, address, size, unused):
        if address in stops: machine.emu_stop()
        else: visited.add(address)
    uc.hook_add(UC_HOOK_CODE, hook)
    cases = 0
    # Probe unsigned guard and first table without executing any external call.
    first_inputs = list(range(32)) + [0x7fffffff, 0x80000000, 0xfffffffe, 0xffffffff]
    for value in first_inputs:
        stops.clear(); stops.update(table1); stops.add(0x10524e7e)
        uc.reg_write(UC_X86_REG_EBP, value); uc.reg_write(UC_X86_REG_EFLAGS, 2)
        uc.emu_start(0x10524784, 0, count=16)
        require(uc.reg_read(UC_X86_REG_EIP) == (table1[value] if value <= 7 else 0x10524e7e), 'First dispatch differs'); cases += 1
    # Probe all byte conditions and all eight second-table slots, including normally excluded 2/3.
    for index in range(8):
        for byte in range(256):
            stops.clear(); stops.update((0x105248cc, 0x105248ae, 0x10524e5a))
            uc.reg_write(UC_X86_REG_ESP, 0x201000); uc.mem_write(0x201014, struct.pack('<I', index))
            uc.reg_write(UC_X86_REG_EAX, byte); uc.reg_write(UC_X86_REG_ECX, 0x80024854); uc.reg_write(UC_X86_REG_EFLAGS, 2)
            uc.emu_start(0x10524885, 0, count=32)
            action = actions[index]; expected_pc = 0x105248ae if action == 'helper' else 0x10524e5a if action == 'error' else 0x105248cc
            mask = 0x80024854 if byte else 0x80024054
            expected_ecx = mask | (0x2000000 if action == 'or-02' else 0x1c000000 if action == 'or-1c' else 0)
            require(uc.reg_read(UC_X86_REG_EIP) == expected_pc and uc.reg_read(UC_X86_REG_ECX) == expected_ecx, 'Second dispatch/flags differ')
            require(bytes(uc.mem_read(0x201010, 4)) == struct.pack('<I', mask) and uc.reg_read(UC_X86_REG_ESP) == 0x201000, 'Stack state differs'); cases += 1
    verification = dict(source_sha256=SHA, address=PARENT, labels=list(LABELS), body_bytes=len(seen), instructions=count,
                        cases=cases, table1=[f'{v:x}' for v in table1], table2=[f'{v:x}' for v in table2], actions=actions,
                        fragment_instructions_visited=len(visited), file='105245a0.corrected.c', negative_missing_case_rejected=True,
                        limitation='Full body bytes and two dispatch fragments verified. Corrected C adds duplicate labels omitted by manual override. Helpers, object layout and whole-function behavior remain unproven; manual override warning retained.')
    (root / 'verification.json').write_text(json.dumps(verification, indent=2), encoding='utf-8')
    names = ('report.json', '105245a0.asm', '105245a0.c', '105245a0.corrected.c', 'verification.json')
    (root / 'manifest.json').write_text(json.dumps(dict(source_sha256=SHA, files={n: digest(root / n) for n in names}), indent=2), encoding='utf-8')
    print(json.dumps(verification))


if __name__ == '__main__': main()
