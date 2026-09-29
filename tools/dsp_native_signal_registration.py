"""Verify signal slot selection and original encoded registration core, offline."""
import argparse
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require
from dsp_native_heap_free import imports_at


def validate_cached(folder, sha):
    root = folder / 'signal-registration-behavior'
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json')
    require(marker['source_sha256'] == sha, 'Signal source differs')
    for name, value in marker['files'].items():
        require(digest(root / name) == value, 'Signal evidence differs')
    return read_json(root / 'report.json')


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    parser = argparse.ArgumentParser()
    parser.add_argument('--refs', type=Path, required=True)
    args = parser.parse_args()
    inv = read_json(GENERATED / 'native/inventory.json')
    module = next(m for m in inv['files'] if Path(m['path']).name == 'rail_api.dll')
    folder = GENERATED / 'native' / module['output']
    path = Path(inv['game_directory']) / module['path']
    require(digest(path) == module['sha256'], 'Source changed')
    game = path.read_bytes()
    refs = read_json(args.refs)
    require(refs['source_sha256'] == module['sha256'], 'Reference source differs')
    # Check observed instruction bytes; indirect data-flow targets remain Ghidra observations.
    checked_refs = 0
    for table in refs['tables']:
        for ref in table['references']:
            if 'bytes' in ref:
                raw = bytes.fromhex(ref['bytes'])
                require(pe_read(game, int(ref['from'], 16), len(raw)) == raw, 'Reference bytes differ')
                checked_refs += 1
    api = imports_at(game, {0x10b2a218})[0x10b2a218]
    require(api['name'] == 'SetConsoleCtrlHandler', 'Console registration import differs')
    require(pe_read(game, 0x1060f3ee, 13).hex() == '6a016827ef6010ff1518a2b210', 'Registration argument bytes differ')
    guard_slot = struct.unpack('<I', pe_read(game, 0x10b2a6e8, 4))[0]
    require(guard_slot == 0x100569a0 and pe_read(game, guard_slot, 1) == b'\xc3', 'Initial guard target differs')
    windows = [(0x1060efd5, 66), (0x1060ef08, 31), (0x1060f41f, 54)]
    cs = Cs(CS_ARCH_X86, CS_MODE_32)
    uc = Uc(UC_ARCH_X86, UC_MODE_32)
    pages = {0x200000, 0x300000, 0x500000, 0x10e81000, 0x10e24000}
    for start, size in windows:
        pages.update(range(start & ~4095, (start + size + 4095) & ~4095, 4096))
    for page in pages: uc.mem_map(page, 4096)
    evidence, all_ins = [], set()
    for start, size in windows:
        raw = pe_read(game, start, size)
        ins = list(cs.disasm(raw, start))
        require(sum(x.size for x in ins) == size, 'Decode incomplete')
        all_ins.update(x.address for x in ins)
        evidence.append(dict(address=f'{start:x}', bytes_hex=raw.hex(), instructions=len(ins)))
        uc.mem_write(start, raw)
    uc.mem_write(0x500000, b'\xf4')
    uc.mem_write(0x1060f455, b'\xf4')
    stack, frame = 0x200800, 0x300800
    visited, writes = set(), []
    done = [False]
    def hook(machine, address, size, unused):
        if address in (0x500000, 0x1060f455):
            done[0] = True
            machine.emu_stop()
        else:
            require(address in all_ins, 'Execution escaped')
            visited.add(address)
    uc.hook_add(UC_HOOK_CODE, hook)
    uc.hook_add(UC_HOOK_MEM_WRITE, lambda m, a, p, n, v, u: writes.append((p, n)))
    slots = {2: 0x10e81d70, 6: 0x10e81d78, 15: 0x10e81d7c, 21: 0x10e81d74, 22: 0x10e81d78}
    def reset():
        for r, v in ((reg.UC_X86_REG_ESP, stack), (reg.UC_X86_REG_EBP, frame),
                     (reg.UC_X86_REG_EBX, 0x12345678), (reg.UC_X86_REG_ESI, 0x87654321),
                     (reg.UC_X86_REG_EFLAGS, 2)):
            uc.reg_write(r, v)
        writes.clear(); done[0] = False
    def execute(entry):
        uc.emu_start(entry, 0, count=300)
        require(done[0], 'Missing stop')
        require(uc.reg_read(reg.UC_X86_REG_EBP) == frame and uc.reg_read(reg.UC_X86_REG_EBX) == 0x12345678, 'Frame or EBX differs')
    signals = list(range(65536)) + [0x7fffffff, 0x80000000, 0xfffffffe, 0xffffffff]
    for signal in signals:
        reset()
        uc.reg_write(reg.UC_X86_REG_EDI, signal)
        uc.mem_write(stack, struct.pack('<II', 0x500000, signal))
        execute(0x1060efd5)
        require(uc.reg_read(reg.UC_X86_REG_EAX) == slots.get(signal, 0), 'Selected slot differs')
        require(uc.reg_read(reg.UC_X86_REG_ESP) == stack + 4 and uc.reg_read(reg.UC_X86_REG_ESI) == 0x87654321 and uc.reg_read(reg.UC_X86_REG_EDI) == signal, 'Selector ABI differs')
        require(writes == [(stack - 4, 4)], 'Selector writes differ')
    def encode(pointer, cookie):
        n = cookie & 31
        return (((pointer << n) | (pointer >> (32 - n if n else 32))) & 0xffffffff) ^ cookie
    cases = 0
    def check(signal, old, new, cookie):
        nonlocal cases
        reset()
        uc.reg_write(reg.UC_X86_REG_EDI, signal)
        memory = bytearray([0xa5]) * 256
        struct.pack_into('<I', memory, 0x8c, new)
        expected_frame = bytearray(memory)
        chosen = slots.get(signal, 0)
        struct.pack_into('<I', expected_frame, 0x58, chosen)
        globals_ = bytearray([0x5a]) * 32
        if chosen:
            struct.pack_into('<I', globals_, chosen - 0x10e81d68, encode(old, cookie))
            struct.pack_into('<I', expected_frame, 0x60, old)
        expected_globals = bytearray(globals_)
        if chosen and new != 2: struct.pack_into('<I', expected_globals, chosen - 0x10e81d68, encode(new, cookie))
        uc.mem_write(frame - 128, bytes(memory))
        uc.mem_write(0x10e81d68, bytes(globals_))
        uc.mem_write(0x10e24f44, struct.pack('<I', cookie))
        execute(0x1060f41f)
        require(uc.reg_read(reg.UC_X86_REG_ESP) == stack and uc.reg_read(reg.UC_X86_REG_EDI) == signal, 'Registration ABI differs')
        require(bytes(uc.mem_read(frame - 128, 256)) == bytes(expected_frame), 'Registration frame differs')
        require(bytes(uc.mem_read(0x10e81d68, 32)) == bytes(expected_globals), 'Encoded slot differs')
        require(uc.reg_read(reg.UC_X86_REG_ESI) == (old if chosen else 0x87654321), 'Prior callback differs')
        require(all(n == 4 and (stack - 24 <= p < stack or p in (frame - 40, frame - 32, chosen)) for p, n in writes), 'Unexpected registration write')
        cases += 1
    for rotation in range(32):
        for high in (0, 0x12345660, 0xffffffe0):
            for signal in (0, 2, 6, 15, 21, 22, 8, 0xffffffff):
                for old in (0, 1, 2, 0x601000, 0x80000000, 0xffffffff):
                    for new in (0, 1, 2, 0x602000, 0x80000000, 0xffffffff):
                        check(signal, old, new, high | rotation)
    require(visited == all_ins, 'Instruction coverage incomplete')
    require(pe_read(game, 0x1060f446, 1) == b'\x02', 'Mutation location differs')
    uc.mem_write(0x1060f446, b'\x01')
    uc.ctl_remove_cache(0x1060f41f, 0x1060f455)
    caught = False
    try: check(2, 0x601000, 2, 0x12345661)
    except ValueError as error:
        require(str(error) == 'Encoded slot differs', 'Unexpected mutation failure')
        caught = True
    require(caught, 'Mutation undetected')
    report = dict(source_sha256=module['sha256'], addresses=['1060efd5', '1060ef08', '1060f2dc', '1060ef27'],
                  ranges=evidence, selector_cases=len(signals), registration_cases=cases,
                  instructions=len(all_ins), reference_instructions_checked=checked_refs, negative_control_caught=caught,
                  slots={str(k): f'{v:x}' for k, v in slots.items()}, console_import=api,
                  initial_guard_slot='10b2a6e8', initial_guard_target=f'{guard_slot:x}',
                  semantics='Registration core returns prior decoded callback in ESI and frame -32, and stores ROL32(new,cookie&31) XOR cookie unless new==2 (query). Signals 6 and 22 share a slot. Unsupported selector returns null and core skips mutation.',
                  limitation='Core starts after validation, lock and console registration; does not prove API failure, SEH or full signal return ABI. Values tested at this inner core do not imply public API accepts them. Guard RET is on-disk initial state only; loader/runtime replacement not tested.')
    root = folder / 'signal-registration-behavior'; root.mkdir(exist_ok=True)
    (root / 'references.json').write_text(json.dumps(refs, indent=2), encoding='utf-8')
    (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    (root / 'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'], files={name: digest(root / name) for name in ('report.json', 'references.json')}), indent=2), encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__': main()
