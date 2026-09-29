"""Check two relocated copy implementations and emulate overlap/length boundaries."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require


def validate_cached(folder, sha):
    root = folder / 'copy-pair-behavior'
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json')
    require(marker['source_sha256'] == sha, 'Copy pair source differs')
    for name, value in marker['files'].items():
        require(digest(root / name) == value, 'Copy pair evidence differs')
    return read_json(root / 'report.json')


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv = read_json(GENERATED / 'native/inventory.json')
    module = next(m for m in inv['files'] if Path(m['path']).name == 'rail_api.dll')
    folder = GENERATED / 'native' / module['output']
    path = Path(inv['game_directory']) / module['path']
    require(digest(path) == module['sha256'], 'Source changed'); game = path.read_bytes()
    bases = [0x105f9bf0, 0x105fafc0]; size = 0x574
    raw = [pe_read(game, base, size) for base in bases]
    # Four absolute dispatch operands and eight table pointers are the only differences.
    offsets = [0x221, 0x258, 0x25f, 0x264, 0x268, 0x26c, 0x270, 0x30b, 0x310, 0x314, 0x318, 0x31c]
    normalized = [bytearray(blob) for blob in raw]; relocations = []
    for offset in offsets:
        targets = [struct.unpack_from('<I', blob, offset)[0] for blob in raw]
        rel = [target - base for target, base in zip(targets, bases)]
        require(rel[0] == rel[1] and 0 <= rel[0] < size, 'Relocation differs')
        for blob, value in zip(normalized, rel): struct.pack_into('<I', blob, offset, value)
        relocations.append(dict(offset=hex(offset), targets=[f'{x:x}' for x in targets], relative=hex(rel[0])))
    require(normalized[0] == normalized[1], 'Additional byte differences')
    cs = Cs(CS_ARCH_X86, CS_MODE_32)
    for base, blob in zip(bases, raw):
        for offset, prefix in ((0x21e, '8b048d'), (0x255, 'ff2495'), (0x25c, 'ff248d'), (0x308, 'ff2495')):
            ins = list(cs.disasm(blob[offset:offset + 7], base + offset))
            require(len(ins) == 1 and ins[0].size == 7 and blob[offset:offset + 3].hex() == prefix, 'Dispatch operand context differs')
    uc = Uc(UC_ARCH_X86, UC_MODE_32)
    for base, n in ((0x105f9000, 0x3000), (0x10e24000, 4096), (0x10e7c000, 4096),
                    (0x200000, 4096), (0x300000, 0x100000), (0x500000, 4096)):
        uc.mem_map(base, n)
    for base, blob in zip(bases, raw): uc.mem_write(base, blob)
    uc.mem_write(0x500000, b'\xf4')
    stack, area = 0x200800, 0x300000
    state, writes = {}, []
    visited = {base: set() for base in bases}
    def code(machine, address, n, unused):
        if address == 0x500000: state['done'] = True; machine.emu_stop(); return
        base = state['entry']
        require(base <= address and address + n <= base + size, 'Copy escaped original body')
        visited[base].add(address - base)
    uc.hook_add(UC_HOOK_CODE, code)
    uc.hook_add(UC_HOOK_MEM_WRITE, lambda m, a, p, n, v, u: writes.append((p, n)))
    preserved = {reg.UC_X86_REG_EBP:0xa1a2a3a4, reg.UC_X86_REG_EBX:0xb1b2b3b4,
                 reg.UC_X86_REG_ESI:0xc1c2c3c4, reg.UC_X86_REG_EDI:0xd1d2d3d4}
    pattern = bytes(((j * 73 + (j >> 8) * 19 + 11) & 255) for j in range(0x100000))
    cases = 0
    def check(length, alignment, delta, features):
        nonlocal cases
        source = 0x1000 + alignment; dest = source + delta
        extent = 0x10000 if max(source, dest) + length + 32 <= 0x10000 else 0x100000
        require(min(source, dest) >= 32 and max(source, dest) + length + 32 <= extent, 'Invalid test buffer')
        original = pattern[:extent]; expected = bytearray(original)
        expected[dest:dest + length] = original[source:source + length]
        for base in bases:
            state.update(done=False, entry=base); writes.clear()
            uc.mem_write(area, original)
            uc.mem_write(stack, struct.pack('<4I', 0x500000, area + dest, area + source, length))
            uc.mem_write(0x10e24f50, struct.pack('<I', features[0])); uc.mem_write(0x10e7c0b0, struct.pack('<I', features[1]))
            uc.reg_write(reg.UC_X86_REG_ESP, stack); uc.reg_write(reg.UC_X86_REG_EFLAGS, 2)
            for r, v in preserved.items(): uc.reg_write(r, v)
            uc.emu_start(base, 0, count=1000000)
            require(state['done'] and uc.reg_read(reg.UC_X86_REG_ESP) == stack + 4, 'Copy stack/return differs')
            require(uc.reg_read(reg.UC_X86_REG_EAX) == area + dest, 'Copy destination return differs')
            require(all(uc.reg_read(r) == v for r, v in preserved.items()), 'Copy preserved registers differ')
            require(bytes(uc.mem_read(area, extent)) == bytes(expected), 'Copy memory differs')
            require(all(stack - 64 <= p and p + n <= stack or area + dest <= p and p + n <= area + dest + length for p, n in writes), 'Unexpected copy write')
            cases += 1
    configs = ((0,0), (2,0), (0,1), (0,2), (2,3))
    lengths = list(range(33)) + [63,64,65,95,96,97,127,128,129,255,256,257,511,512,513,1023,1024,1025,4095,4096,4097]
    for features in configs:
        for length in lengths:
            for alignment in range(16):
                for delta in (-257,-16,-3,-1,0,1,3,16,257,8192): check(length, alignment, delta, features)
    ordinary_cases = cases
    for features in configs:
        for length in (16383,16384,16385,65535,65536,65537,131071,131072,131073):
            for alignment in (0,1,15):
                for delta in (-1,1,262144): check(length, alignment, delta, features)
    large_cases = cases - ordinary_cases
    # Exercise source/destination alignment differences of 4 and 12 modulo 16.
    # These reach the PALIGNR 4/12 loops absent from the original delta set.
    for features in configs:
        for length in (128,129,143,144,145,159,160,161,191,192,193,255,256,257,511,512,513,4096):
            for alignment in range(16):
                for delta in (-12,-4,8196,8204): check(length, alignment, delta, features)
    alignment_cases = cases - ordinary_cases - large_cases
    positive_visited = {f'{base:x}': sorted(offsets) for base, offsets in visited.items()}
    require(visited[bases[0]] == visited[bases[1]], 'Relocated implementations have different coverage')
    # Force zero copy count in second body and ensure nonempty copy is detected.
    require(raw[1][6:10] == bytes.fromhex('8b4c2414'), 'Mutation location differs')
    uc.mem_write(bases[1] + 6, bytes.fromhex('31c99090')); uc.ctl_remove_cache(bases[1], bases[1] + size)
    positive_cases = cases; caught = False
    try: check(144, 1, 8192, (0,0))
    except ValueError as error:
        require(str(error) == 'Copy memory differs', 'Unexpected mutation failure'); caught = True
    require(caught, 'Wrong copy undetected')
    report = dict(source_sha256=module['sha256'], addresses=[f'{base:x}' for base in bases],
                  size=size, source_bytes=[blob.hex() for blob in raw], relocations=relocations,
                  normalized_equal=True, cases=positive_cases, ordinary_cases=ordinary_cases,
                  large_cases=large_cases, alignment_cases=alignment_cases, features=configs, lengths=lengths,
                  visited_offsets=positive_visited, instructions_visited_each=len(positive_visited[f'{bases[0]:x}']),
                  negative_control_caught=caught,
                  semantics='Both original bodies match byte-for-byte after only 12 checked internal absolute relocations. Tested copies match snapshot/memmove semantics, return destination in EAX and preserve callee-saved registers.',
                  limitation='Finite mapped valid buffers, DF=0 and synthetic feature flags. Not proof for all lengths, wrapping/invalid addresses, runtime feature initialization, every instruction, or source-level function signatures. Table bytes are not counted as instructions.')
    root = folder / 'copy-pair-behavior'; root.mkdir(exist_ok=True)
    (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    (root / 'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'], files={'report.json': digest(root / 'report.json')}), indent=2), encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k not in ('source_bytes','visited_offsets','relocations')}))


if __name__ == '__main__': main()
