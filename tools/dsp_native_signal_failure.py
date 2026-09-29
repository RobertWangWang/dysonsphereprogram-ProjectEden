"""Original global signal-registration path with modeled OS/lock/TLS boundaries."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require
from dsp_native_heap_free import imports_at
from dsp_native_signal_registration import validate_cached as validate_registration


def validate_cached(folder, sha):
    root = folder / 'signal-failure-behavior'
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json')
    require(marker['source_sha256'] == sha, 'Signal failure source differs')
    for name, value in marker['files'].items():
        require(digest(root / name) == value, 'Signal failure evidence differs')
    report = read_json(root / 'report.json')
    for name, value in report['dependencies'].items():
        require(digest(folder / name) == value, 'Signal failure dependency differs')
    return report


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv = read_json(GENERATED / 'native/inventory.json')
    module = next(m for m in inv['files'] if Path(m['path']).name == 'rail_api.dll')
    folder = GENERATED / 'native' / module['output']
    path = Path(inv['game_directory']) / module['path']
    require(digest(path) == module['sha256'], 'Source changed')
    require(validate_registration(folder, module['sha256']), 'Registration evidence missing')
    game = path.read_bytes()
    imports = imports_at(game, {0x10b2a218, 0x10b2a394})
    require(imports[0x10b2a218]['name'] == 'SetConsoleCtrlHandler' and imports[0x10b2a394]['name'] == 'GetLastError', 'Imports differ')
    windows = [(0x1060f3c3, 170), (0x1060f476, 9), (0x1060f33c, 6), (0x1060f482, 6),
               (0x1060efd5, 66), (0x1060ef08, 31), (0x1060f03f, 49),
               (0x10603a99, 19), (0x10603aac, 19)]
    uc = Uc(UC_ARCH_X86, UC_MODE_32)
    pages = {0x200000, 0x300000, 0x400000, 0x500000, 0x600000, 0x10613000,
             0x10617000, 0x10e24000, 0x10e47000, 0x10e81000, 0x10b2a000}
    for a, n in windows: pages.update(range(a & ~4095, (a + n + 4095) & ~4095, 4096))
    for page in pages: uc.mem_map(page, 4096)
    cs = Cs(CS_ARCH_X86, CS_MODE_32)
    all_ins, evidence = set(), []
    for a, n in windows:
        raw = pe_read(game, a, n); ins = list(cs.disasm(raw, a))
        require(sum(x.size for x in ins) == n, 'Incomplete decoding')
        uc.mem_write(a, raw); all_ins.update(x.address for x in ins)
        evidence.append(dict(address=f'{a:x}', bytes_hex=raw.hex(), instructions=len(ins)))
    for stop in (0x1060f488, 0x500000): uc.mem_write(stop, b'\xf4')
    for stub in (0x600000, 0x600010, 0x10613316, 0x1061335e, 0x10617acd): uc.mem_write(stub, b'\xc3')
    uc.mem_write(0x10b2a218, struct.pack('<I', 0x600000)); uc.mem_write(0x10b2a394, struct.pack('<I', 0x600010))
    stack, frame, thread = 0x200800, 0x300800, 0x400100
    state, events, writes, visited = {}, [], [], set()
    def hook(machine, address, size, unused):
        if address in (0x1060f488, 0x500000):
            state['done'] = True; machine.emu_stop(); return
        if address in (0x600000, 0x600010, 0x10613316, 0x1061335e, 0x10617acd):
            sp = machine.reg_read(reg.UC_X86_REG_ESP)
            ret = struct.unpack('<I', machine.mem_read(sp, 4))[0]; pop = 0
            if address == 0x600000:
                args = list(struct.unpack('<II', machine.mem_read(sp + 4, 8)))
                events.append(['console', args]); value = state['api']; pop = 8
            elif address == 0x600010: events.append(['last_error']); value = state['error']
            elif address == 0x10617acd: events.append(['tls']); value = thread if state['tls'] else 0
            else:
                arg = struct.unpack('<I', machine.mem_read(sp + 4, 4))[0]
                events.append(['lock' if address == 0x10613316 else 'unlock', arg]); value = 0xdeadbeef
            machine.reg_write(reg.UC_X86_REG_EAX, value)
            machine.reg_write(reg.UC_X86_REG_ECX, 0x76543210); machine.reg_write(reg.UC_X86_REG_EDX, 0xabcdef01)
            machine.reg_write(reg.UC_X86_REG_ESP, sp + 4 + pop); machine.reg_write(reg.UC_X86_REG_EIP, ret); return
        require(address in all_ins, f'Unexpected instruction {address:x}')
        visited.add(address)
    uc.hook_add(UC_HOOK_CODE, hook)
    uc.hook_add(UC_HOOK_MEM_WRITE, lambda m, a, p, n, v, u: writes.append((p, n)))
    slots = {2: 0x10e81d70, 6: 0x10e81d78, 15: 0x10e81d7c, 21: 0x10e81d74, 22: 0x10e81d78}
    def encode(value, cookie):
        n = cookie & 31
        return (((value << n) | (value >> (32 - n if n else 32))) & 0xffffffff) ^ cookie
    def reset(tls, api=1, error=0):
        state.update(done=False, tls=tls, api=api, error=error)
        events.clear(); writes.clear()
        for r, v in ((reg.UC_X86_REG_ESP, stack), (reg.UC_X86_REG_EBP, frame), (reg.UC_X86_REG_EFLAGS, 2)):
            uc.reg_write(r, v)
        uc.mem_write(thread, bytes([0xa5]) * 64)
        uc.mem_write(0x10e47330, bytes([0xa5]) * 48)
    def errors_match(tls, errno=None, doserrno=None):
        for base, size, eno, dos in ((thread, 64, thread + 16, thread + 20), (0x10e47330, 48, 0x10e47340, 0x10e47344)):
            expected = bytearray([0xa5]) * size
            if (base == thread) == bool(tls):
                if errno is not None: struct.pack_into('<I', expected, eno - base, errno)
                if doserrno is not None: struct.pack_into('<I', expected, dos - base, doserrno)
            require(bytes(uc.mem_read(base, size)) == bytes(expected), 'Error storage differs')
    helper_cases = 0
    for signal in list(range(65536)) + [0x7fffffff, 0x80000000, 0xfffffffe, 0xffffffff]:
        for tls in (0, 1):
            reset(tls); uc.mem_write(stack, struct.pack('<II', 0x500000, signal))
            uc.emu_start(0x1060f03f, 0, count=100)
            sets_errno = signal not in (1, 3, 13, 16, 17)
            require(state['done'] and uc.reg_read(reg.UC_X86_REG_EAX) == 0xffffffff and uc.reg_read(reg.UC_X86_REG_ESP) == stack + 4, 'Failure helper result differs')
            require(events == ([['tls']] if sets_errno else []), 'Failure helper events differ')
            errors_match(tls, 22 if sets_errno else None); helper_cases += 1
    path_cases = 0
    def check(signal, flag, api, tls, cookie, old, new, error):
        nonlocal path_cases
        reset(tls, api, error); uc.reg_write(reg.UC_X86_REG_EDI, signal)
        mem = bytearray([0xa5]) * 256
        struct.pack_into('<I', mem, 0x88, signal); struct.pack_into('<I', mem, 0x8c, new)
        uc.mem_write(frame - 128, bytes(mem))
        globals_ = bytearray([0x5a]) * 32; globals_[4] = flag
        chosen = slots[signal]; struct.pack_into('<I', globals_, chosen - 0x10e81d68, encode(old, cookie))
        uc.mem_write(0x10e81d68, bytes(globals_)); uc.mem_write(0x10e24f44, struct.pack('<I', cookie))
        called = signal in (2, 21) and flag == 0; failed = called and api == 0
        expected_events = [['lock', 3]]
        if called: expected_events.append(['console', [0x1060ef27, 1]])
        if failed: expected_events += [['tls'], ['last_error']]
        expected_events.append(['unlock', 3])
        if failed: expected_events.append(['tls'])
        if called and not failed: globals_[4] = 1
        if new != 2: struct.pack_into('<I', globals_, chosen - 0x10e81d68, encode(new, cookie))
        struct.pack_into('<I', mem, 0x58, chosen); struct.pack_into('<I', mem, 0x60, old)
        struct.pack_into('<I', mem, 0x7c, 0xfffffffe); mem[0x67] = int(failed)
        uc.emu_start(0x1060f3c3, 0, count=500)
        require(state['done'] and uc.reg_read(reg.UC_X86_REG_ESP) == stack, 'Registration stop or stack differs')
        require(uc.reg_read(reg.UC_X86_REG_EAX) == (0xffffffff if failed else old), 'Registration result differs')
        require(events == expected_events, 'Registration events differ')
        require(bytes(uc.mem_read(0x10e81d68, 32)) == bytes(globals_), 'Failure-path slot differs')
        require(bytes(uc.mem_read(frame - 128, 256)) == bytes(mem), 'Registration frame differs')
        errors_match(tls, 22 if failed else None, error if failed else None)
        require(uc.reg_read(reg.UC_X86_REG_EBP) == frame and uc.reg_read(reg.UC_X86_REG_EDI) == signal and uc.reg_read(reg.UC_X86_REG_ESI) == old, 'Preserved context differs')
        allowed = {(frame - 4, 4), (frame - 25, 1), (frame - 40, 4), (frame - 32, 4), (chosen, 4), (0x10e81d6c, 1),
                   ((thread + 16 if tls else 0x10e47340), 4), ((thread + 20 if tls else 0x10e47344), 4)}
        require(all((p, n) in allowed or n == 4 and stack - 32 <= p < stack for p, n in writes), 'Unexpected path write')
        path_cases += 1
    for signal in slots:
        for flag in (0, 1, 255):
            for api in (0, 1, 0x80000000):
                for tls in (0, 1):
                    for cookie in (0, 0x12345661, 0xffffffff):
                        for old in (0, 1, 0x601000, 0xffffffff):
                            for new in (0, 1, 2, 0x602000):
                                for error in (0, 5, 0xffffffff): check(signal, flag, api, tls, cookie, old, new, error)
    # Invalid selector return is outside this valid global-signal path; verified separately.
    excluded = {0x1060eff7, 0x1060eff9, 0x1060effa}
    require(visited == all_ins - excluded, 'Unexpected instruction coverage gap')
    require(pe_read(game, 0x1060f418, 1) == b'\x01', 'Mutation location differs')
    uc.mem_write(0x1060f418, b'\x00'); uc.ctl_remove_cache(0x1060f3c3, 0x1060f46d)
    caught = False
    try: check(2, 0, 0, 1, 0x12345661, 0x601000, 0x602000, 5)
    except ValueError as error:
        require(str(error) == 'Registration result differs', 'Unexpected mutation failure'); caught = True
    require(caught, 'Mutation undetected')
    report = dict(source_sha256=module['sha256'], addresses=['1060f2dc', '1060f03f', '10603a99'],
                  ranges=evidence, helper_cases=helper_cases, path_cases=path_cases, instructions=len(all_ins),
                  instructions_visited=len(visited), excluded_instructions=[f'{a:x}' for a in sorted(excluded)],
                  negative_control_caught=caught, dependencies={'signal-registration-behavior/manifest.json': digest(folder / 'signal-registration-behavior/manifest.json')},
                  semantics='Console registration failure records last error, still updates selected encoded slot unless query sentinel 2, unlocks, sets errno=22 and produces FFFFFFFF before SEH epilogue. Successful or skipped registration returns old decoded callback at that boundary. Failure helper preserves errno for signals 1,3,13,16,17.',
                  limitation='Supplied frame, normal returning lock/TLS/OS models. No real OS API, TLS allocation, concurrency, SEH prologue/epilogue, unwind or per-thread signal-table path executed.')
    root = folder / 'signal-failure-behavior'; root.mkdir(exist_ok=True)
    (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    (root / 'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'], files={'report.json': digest(root / 'report.json')}), indent=2), encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__': main()
