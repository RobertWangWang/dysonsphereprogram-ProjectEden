"""Verify two non-adjacent index bounds using original x86 fragments, without external calls."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require


def validate_cached(folder, sha):
    root = folder / 'nonadjacent-table-bounds'
    if not (root / 'manifest.json').exists(): return {}
    manifest = read_json(root / 'manifest.json')
    require(manifest['source_sha256'] == sha, 'Nonadjacent source differs')
    for name, value in manifest['files'].items(): require(digest(root / name) == value, 'Nonadjacent evidence differs')
    return read_json(root / 'report.json')


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
    from unicorn.x86_const import UC_X86_REG_EAX, UC_X86_REG_ECX, UC_X86_REG_EDI, UC_X86_REG_EBP, UC_X86_REG_ESP, UC_X86_REG_EIP, UC_X86_REG_EFLAGS
    base = GENERATED / 'native/DSPGAME_Data__Plugins__x86_64__rail_api.dll'
    root = base / 'nonadjacent-table-bounds'; root.mkdir(exist_ok=True)
    path = Path(read_json(GENERATED / 'native/inventory.json')['game_directory']) / 'DSPGAME_Data/Plugins/x86_64/rail_api.dll'
    sha = 'c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1'
    require(digest(path) == sha, 'Game changed'); game = path.read_bytes()
    ranges = [(0x10526ecf, 0x10526eee), (0x105557ca, 0x1055583b), (0x105558d6, 0x105558e2)]
    decoder = Cs(CS_ARCH_X86, CS_MODE_32); fragments = []; assembly = []; expected_instructions = set()
    for start, end in ranges:
        raw = pe_read(game, start, end - start); items = list(decoder.disasm(raw, start))
        require(sum(i.size for i in items) == len(raw), 'Incomplete fragment decode')
        expected_instructions.update(i.address for i in items)
        fragments.append(dict(start=f'{start:x}', end_exclusive=f'{end:x}', bytes_hex=raw.hex(), instruction_count=len(items)))
        assembly.extend(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}' for i in items)
    tables = [list(struct.unpack('<31I', pe_read(game, 0x10528544, 124))), list(struct.unpack('<5I', pe_read(game, 0x10555a10, 20)))]
    uc = Uc(UC_ARCH_X86, UC_MODE_32); uc.mem_map(0x10520000, 0x40000); uc.mem_map(0x200000, 0x3000)
    for start, end in ranges: uc.mem_write(start, pe_read(game, start, end - start))
    for table, length in [(0x10528544, 124), (0x10555a10, 20)]: uc.mem_write(table, pe_read(game, table, length))
    active = [0, 0]; visited = set()
    def hook(machine, address, size, unused):
        if not active[0] <= address < active[1]: machine.emu_stop()
        else: visited.add(address)
    uc.hook_add(UC_HOOK_CODE, hook)
    cases = 0
    for value in list(range(64)) + [0x7fffffff, 0x80000000, 0xfffffffe, 0xffffffff]:
        for ecx in (0, 0x200100, 0xfffffff0):
            sentinel = bytes([0xa5]) * 0x1000; uc.mem_write(0x200000, sentinel)
            uc.reg_write(UC_X86_REG_EAX, value); uc.reg_write(UC_X86_REG_ECX, ecx)
            uc.reg_write(UC_X86_REG_EBP, 0x200800); uc.reg_write(UC_X86_REG_EDI, 0x12345678); uc.reg_write(UC_X86_REG_EFLAGS, 2)
            active[:] = ranges[0]; uc.emu_start(active[0], 0, count=32)
            expected = tables[0][value] if value <= 30 else 0x10527a0e
            require(uc.reg_read(UC_X86_REG_EIP) == expected and uc.reg_read(UC_X86_REG_EAX) == value, 'Separated guard dispatch differs')
            memory = bytearray(sentinel)
            if value <= 30:
                struct.pack_into('<I', memory, 0x7d0, (ecx + 0x58) & 0xffffffff)
                struct.pack_into('<I', memory, 0x7c4, (ecx + 0x5c) & 0xffffffff)
            require(bytes(uc.mem_read(0x200000, 0x1000)) == bytes(memory), 'Separated guard writes differ')
            require(uc.reg_read(UC_X86_REG_EDI) == (0xa5a5a5a5 if value <= 30 else 0x12345678), 'EDI state differs'); cases += 1
    # Check all low twelve flag bits, plus two high-bit patterns that should not affect the selector.
    selector_counts = {}; selector_cases = 0
    for flags in range(4096):
        for high in (0, 0xa5a5f000):
            value = flags | high; sentinel = bytearray([0xa5] * 0x1000); struct.pack_into('<I', sentinel, 0x864, value)
            uc.mem_write(0x200000, bytes(sentinel)); uc.reg_write(UC_X86_REG_ESP, 0x200800); uc.reg_write(UC_X86_REG_EFLAGS, 2)
            active[:] = ranges[1]; uc.emu_start(active[0], 0, count=64)
            kind, tag = ((0x1001, 0x12) if flags & 1 else (0x1001, 0x13) if flags & 2 else
                         (0x1001, 0x16) if flags & 0x10 else (0x1001, 0x14) if flags & 4 else
                         (0x1002, 0x1e) if flags & 0x800 else (0x1004, 0x1c) if flags & 0x100 else (0x1000, 0xc))
            struct.pack_into('<I', sentinel, 0x820, kind)
            require(uc.reg_read(UC_X86_REG_EIP) == 0x1055583b and uc.reg_read(UC_X86_REG_EDI) == tag, 'Flag priority differs')
            require(bytes(uc.mem_read(0x200000, 0x1000)) == bytes(sentinel) and uc.reg_read(UC_X86_REG_ESP) == 0x200800, 'Selector memory differs')
            # Feed the produced local value into the later dispatch; intervening calls are not executed.
            uc.reg_write(UC_X86_REG_EAX, kind); active[:] = ranges[2]; uc.emu_start(active[0], 0, count=8)
            require(uc.reg_read(UC_X86_REG_EIP) == tables[1][kind - 0x1000], 'Derived-index dispatch differs')
            selector_counts[str(kind - 0x1000)] = selector_counts.get(str(kind - 0x1000), 0) + 1; selector_cases += 1
    # Check that a wrong table pointer cannot satisfy the known mapping.
    original = pe_read(game, 0x10555a10, 4); uc.mem_write(0x10555a10, struct.pack('<I', tables[1][0] + 1))
    uc.reg_write(UC_X86_REG_EAX, 0x1000); active[:] = ranges[2]; uc.emu_start(active[0], 0, count=8)
    require(uc.reg_read(UC_X86_REG_EIP) != tables[1][0], 'Mutated pointer was not detected'); uc.mem_write(0x10555a10, original)
    require(visited == expected_instructions, 'Fragment instruction coverage incomplete')
    report = dict(source_sha256=sha, fragments=fragments, tables=[dict(address='10528544', owner='10526e50', index_domain='unsigned 0..30', targets=[f'{v:x}' for v in tables[0]], cases=cases),
                  dict(address='10555a10', owner='10555610', index_domain='selector produces {0,1,2,4}', targets=[f'{v:x}' for v in tables[1]], cases=selector_cases)],
                  cases=cases + selector_cases, selector_counts=selector_counts, instructions_visited=len(visited), negative_pointer_mutation_rejected=True,
                  limitation='Original dispatch and selector fragments verified. Selector-to-later-dispatch transfer assumes the produced local value is preserved; intervening external calls and full function behavior were not executed or proven.')
    (root / 'fragments.asm').write_text('\n'.join(assembly) + '\n', encoding='utf-8')
    (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    (root / 'manifest.json').write_text(json.dumps(dict(source_sha256=sha, files={n: digest(root / n) for n in ('report.json', 'fragments.asm')}), indent=2), encoding='utf-8')
    print(json.dumps(dict(cases=report['cases'], selector_counts=selector_counts, instructions_visited=len(visited))))


if __name__ == '__main__': main()
