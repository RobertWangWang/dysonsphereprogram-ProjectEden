"""Probe original per-thread signal lookup/update with explicit allocation/copy models."""
import argparse
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require


def validate_cached(folder, sha, original_copy=False):
    root = folder / ('signal-thread-copy-behavior' if original_copy else 'signal-thread-behavior')
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json')
    require(marker['source_sha256'] == sha, 'Thread signal source differs')
    for name, value in marker['files'].items():
        require(digest(root / name) == value, 'Thread signal evidence differs')
    return read_json(root / 'report.json')


def validate_copy_cached(folder, sha):
    return validate_cached(folder, sha, True)


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    parser = argparse.ArgumentParser()
    parser.add_argument('--original-copy', action='store_true', help='Execute original copy body; retain TLS/allocation boundary models')
    options = parser.parse_args()
    inv = read_json(GENERATED / 'native/inventory.json')
    module = next(m for m in inv['files'] if Path(m['path']).name == 'rail_api.dll')
    folder = GENERATED / 'native' / module['output']
    path = Path(inv['game_directory']) / module['path']
    require(digest(path) == module['sha256'], 'Source changed'); game = path.read_bytes()
    count, size = struct.unpack('<II', pe_read(game, 0x10bbcf90, 8))
    require((count, size) == (12, 144), 'Original table dimensions differ')
    default = list(struct.iter_unpack('<III', pe_read(game, 0x10bbcf00, size)))
    windows = [(0x1060f342, 129), (0x1060f017, 40)]
    uc = Uc(UC_ARCH_X86, UC_MODE_32)
    pages = {0x1060f000, 0x10617000, 0x10613000, 0x105fa000, 0x105fb000, 0x10bbc000, 0x10e24000, 0x10e7c000,
             0x200000, 0x300000, 0x400000, 0x500000, 0x600000, 0x601000}
    for page in pages: uc.mem_map(page, 4096)
    cs = Cs(CS_ARCH_X86, CS_MODE_32); all_ins, evidence = set(), []
    for a, n in windows:
        raw = pe_read(game, a, n); ins = list(cs.disasm(raw, a))
        require(sum(x.size for x in ins) == n, 'Decode incomplete')
        uc.mem_write(a, raw); all_ins.update(x.address for x in ins)
        evidence.append(dict(address=f'{a:x}', bytes_hex=raw.hex(), instructions=len(ins)))
    for stop in (0x1060f33c, 0x1060f488, 0x500000): uc.mem_write(stop, b'\xf4')
    for stub in (0x10617acd, 0x10613c79): uc.mem_write(stub, b'\xc3')
    if options.original_copy:
        copy_raw = pe_read(game, 0x105fafc0, 0x574)
        uc.mem_write(0x105fafc0, copy_raw)
        evidence.append(dict(address='105fafc0', bytes_hex=copy_raw.hex(), size=len(copy_raw)))
    else: uc.mem_write(0x105fafc0, b'\xc3')
    stack, frame, thread, private, allocated = 0x200800, 0x300800, 0x400100, 0x600100, 0x601100
    state, events, writes, visited, copy_visited = {}, [], [], set(), set()
    def hook(machine, address, length, unused):
        if address in (0x1060f33c, 0x1060f488, 0x500000):
            state['stop'] = address; machine.emu_stop(); return
        if options.original_copy and 0x105fafc0 <= address < 0x105fb534:
            require(address + length <= 0x105fb534, 'Copy escaped original window')
            copy_visited.add(address)
            if address == 0x105fafc0:
                sp = machine.reg_read(reg.UC_X86_REG_ESP)
                call_args = list(struct.unpack('<III', machine.mem_read(sp + 4, 12)))
                require(call_args == [allocated, 0x10bbcf00, state['size']], 'Original copy arguments differ')
                events.append(['copy', call_args])
            return
        if address in (0x10617acd, 0x10613c79, 0x105fafc0):
            sp = machine.reg_read(reg.UC_X86_REG_ESP)
            ret = struct.unpack('<I', machine.mem_read(sp, 4))[0]
            if address == 0x10617acd:
                events.append(['tls']); value = 0 if state['mode'] == 'no_tls' else thread
            elif address == 0x10613c79:
                arg = struct.unpack('<I', machine.mem_read(sp + 4, 4))[0]
                events.append(['alloc', arg]); value = 0 if state['mode'] == 'alloc_fail' else allocated
            else:
                args = list(struct.unpack('<III', machine.mem_read(sp + 4, 12)))
                require(args == [allocated, 0x10bbcf00, state['size']], 'Copy arguments differ')
                events.append(['copy', args]); machine.mem_write(args[0], bytes(machine.mem_read(args[1], args[2])))
                value = args[0]
            machine.reg_write(reg.UC_X86_REG_EAX, value)
            machine.reg_write(reg.UC_X86_REG_ECX, 0xaabbccdd); machine.reg_write(reg.UC_X86_REG_EDX, 0x12345678)
            machine.reg_write(reg.UC_X86_REG_ESP, sp + 4); machine.reg_write(reg.UC_X86_REG_EIP, ret); return
        require(address in all_ins, 'Execution escaped'); visited.add(address)
    uc.hook_add(UC_HOOK_CODE, hook)
    uc.hook_add(UC_HOOK_MEM_WRITE, lambda m, a, p, n, v, u: writes.append((p, n)))
    def run(rows, signal, new, mode, features=(0, 0), alignment=0):
        nonlocal allocated
        allocated = 0x601100 + alignment
        raw = b''.join(struct.pack('<III', *r) for r in rows)
        state.update(mode=mode, size=len(raw), stop=None); events.clear(); writes.clear()
        for r, v in ((reg.UC_X86_REG_ESP, stack), (reg.UC_X86_REG_EBP, frame),
                     (reg.UC_X86_REG_EDI, signal), (reg.UC_X86_REG_EBX, new), (reg.UC_X86_REG_EFLAGS, 2)):
            uc.reg_write(r, v)
        uc.mem_write(0x10bbcf00, bytes([0x5a]) * 144)
        if raw: uc.mem_write(0x10bbcf00, raw)
        uc.mem_write(0x10bbcf90, struct.pack('<II', len(rows), len(raw)))
        uc.mem_write(0x10e24f50, struct.pack('<I', features[0]))
        uc.mem_write(0x10e7c0b0, struct.pack('<I', features[1]))
        for base in (private, allocated): uc.mem_write(base - 16, bytes([0xa5]) * 192)
        if raw: uc.mem_write(private, raw)
        old_thread = private if mode == 'private' else 0x10bbcf00
        thread_mem = bytearray([0xa5]) * 64; struct.pack_into('<I', thread_mem, 0, old_thread)
        uc.mem_write(thread, bytes(thread_mem))
        frame_mem = bytearray([0xa5]) * 256; uc.mem_write(frame - 128, bytes(frame_mem))
        expected_private = bytearray([0xa5]) * 192; expected_private[16:16 + len(raw)] = raw
        expected_alloc = bytearray([0xa5]) * 192
        expected_events = [['tls']]
        if mode in ('alloc_fail', 'copy'):
            expected_events.append(['alloc', len(raw)])
            struct.pack_into('<I', thread_mem, 0, 0 if mode == 'alloc_fail' else allocated)
        if mode == 'copy':
            expected_events.append(['copy', [allocated, 0x10bbcf00, len(raw)]])
            expected_alloc[16:16 + len(raw)] = raw
        table = private if mode == 'private' else allocated
        reached = mode in ('private', 'copy')
        found = next((j for j, row in enumerate(rows) if row[1] == signal), None) if reached else None
        if reached: struct.pack_into('<I', frame_mem, 0x5c, table)
        if found is not None:
            old = rows[found][2]; struct.pack_into('<I', frame_mem, 0x58, old)
            if new != 2:
                j = found
                while j < len(rows) and rows[j][1] == signal:
                    struct.pack_into('<I', expected_private if mode == 'private' else expected_alloc, 16 + j * 12 + 8, new)
                    j += 1
        uc.emu_start(0x1060f342, 0, count=3000)
        require(state['stop'] == (0x1060f488 if found is not None else 0x1060f33c), 'Thread path destination differs')
        if found is not None: require(uc.reg_read(reg.UC_X86_REG_EAX) == rows[found][2], 'Old handler result differs')
        require(events == expected_events, 'Thread events differ')
        require(bytes(uc.mem_read(thread, 64)) == bytes(thread_mem), 'Thread pointer differs')
        require(bytes(uc.mem_read(frame - 128, 256)) == bytes(frame_mem), 'Frame differs')
        require(bytes(uc.mem_read(private - 16, 192)) == bytes(expected_private), 'Private table differs')
        require(bytes(uc.mem_read(allocated - 16, 192)) == bytes(expected_alloc), 'Copied table differs')
        require(bytes(uc.mem_read(0x10bbcf00, 144)) == raw + bytes([0x5a]) * (144 - len(raw)), 'Shared table changed')
        require(uc.reg_read(reg.UC_X86_REG_ESP) == stack and uc.reg_read(reg.UC_X86_REG_EBP) == frame and uc.reg_read(reg.UC_X86_REG_EDI) == signal and uc.reg_read(reg.UC_X86_REG_EBX) == new, 'Thread path ABI differs')
        require(all(n == 4 and (stack - 64 <= p < stack or p in (thread, frame - 40, frame - 36)) or table <= p and p+n <= table + len(raw) for p, n in writes), 'Unexpected thread write')
    # Original layout with finite prior handler values, plus deliberately synthetic layouts.
    layouts = [default]
    for old in (1, 0x601234, 0xffffffff): layouts.append([(r[0], r[1], old ^ j) for j, r in enumerate(default)])
    for signals in ([], [4], [8, 4, 4, 11, 4], [4, 8, 4], [11, 11, 11], [8] * 12):
        layouts.append([(0xc0000000 + j, signal, 0x700000 + j) for j, signal in enumerate(signals)])
    cases = 0
    configs = [(f, a) for f in ((0,0),(2,0),(0,1),(0,2),(2,3)) for a in range(16)] if options.original_copy else [((0,0),0)]
    for features, alignment in configs:
        for rows in layouts:
            for signal in (0, 4, 8, 11, 2, 0xffffffff):
                for new in (0, 1, 2, 0x602000, 0xffffffff):
                    for mode in ('no_tls', 'alloc_fail', 'copy', 'private'):
                        run(rows, signal, new, mode, features, alignment); cases += 1
    require(visited == all_ins, 'Instruction coverage incomplete')
    positive_copy_visited = set(copy_visited)
    require(pe_read(game, 0x1060f397, 1) == b'\x02', 'Mutation location differs')
    uc.mem_write(0x1060f397, b'\x01'); uc.ctl_remove_cache(0x1060f342, 0x1060f3c3)
    caught = False
    try: run(default, 4, 2, 'private')
    except ValueError as error:
        require(str(error) == 'Private table differs', 'Unexpected mutation failure'); caught = True
    require(caught, 'Mutation undetected')
    copy_negative = None
    if options.original_copy:
        uc.mem_write(0x1060f397, b'\x02'); uc.ctl_remove_cache(0x1060f342, 0x1060f3c3)
        require(pe_read(game, 0x105fafc6, 4) == bytes.fromhex('8b4c2414'), 'Copy mutation location differs')
        # Force zero length instead of reading the original argument; copying must matter.
        uc.mem_write(0x105fafc6, bytes.fromhex('31c99090')); uc.ctl_remove_cache(0x105fafc0, 0x105fb534)
        copy_negative = False
        try: run(default, 4, 2, 'copy')
        except ValueError as error:
            require(str(error) == 'Thread path destination differs', 'Unexpected copy mutation failure'); copy_negative = True
        require(copy_negative, 'Empty copy escaped detector')
    report = dict(source_sha256=module['sha256'], addresses=['1060f2dc', '1060f017'] + (['105fafc0'] if options.original_copy else []), ranges=evidence,
                  original_table=default, original_count=count, original_size=size, cases=cases,
                  original_layout_cases=4*6*5*4*len(configs), synthetic_layout_cases=6*6*5*4*len(configs),
                  instructions=len(all_ins), negative_control_caught=caught,
                  copy_negative_control_caught=copy_negative,
                  original_copy=options.original_copy, copy_instructions_visited=len(positive_copy_visited),
                  copy_visited_addresses=[f'{a:x}' for a in sorted(positive_copy_visited)], configurations=len(configs),
                  semantics='Allocate/copy the shared 12-row table on first use, even for query handler 2. Allocation failure stores null in thread table field. Lookup finds first signal; update affects its consecutive matching run only, preserving later separated runs. Returns first prior handler at epilogue boundary.',
                  limitation=('Supplied frame and finite valid non-wrapping table buffers. TLS and malloc are explicit boundary models; ' + ('copy executes original instructions for tested non-overlapping tables of 0..144 bytes, not complete copy-function coverage; ' if options.original_copy else 'copy is modeled; ') + 'no actual allocator, failure helper, SEH or concurrency. Synthetic table/feature cases test inner logic, not real runtime configurations or accepted public inputs.'))
    root = folder / ('signal-thread-copy-behavior' if options.original_copy else 'signal-thread-behavior'); root.mkdir(exist_ok=True)
    (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    (root / 'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'], files={'report.json': digest(root / 'report.json')}), indent=2), encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__': main()
