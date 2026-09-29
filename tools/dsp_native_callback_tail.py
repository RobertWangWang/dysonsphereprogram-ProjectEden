"""Verify original callback dispatch tail with explicit guard/callback boundary models."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require
from dsp_native_encoded_callback import validate_cached as validate_decode


def validate_cached(folder, sha):
    root = folder / 'callback-tail-behavior'
    if not (root / 'manifest.json').exists():
        return {}
    marker = read_json(root / 'manifest.json')
    require(marker['source_sha256'] == sha, 'Callback tail source differs')
    for name, value in marker['files'].items():
        require(digest(root / name) == value, 'Callback tail evidence differs')
    result = read_json(root / 'report.json')
    for name, value in result['dependencies'].items():
        require(digest(folder / name) == value, 'Callback tail dependency differs')
    return result


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv = read_json(GENERATED / 'native/inventory.json')
    module = next(m for m in inv['files'] if Path(m['path']).name == 'rail_api.dll')
    folder = GENERATED / 'native' / module['output']
    path = Path(inv['game_directory']) / module['path']
    require(digest(path) == module['sha256'], 'Source changed')
    require(validate_decode(folder, module['sha256']), 'Decode evidence missing')
    game = path.read_bytes()
    ranges = [(0x1060efa2, 0x1060efaa), (0x1060efb9, 0x1060efcd)]
    cs = Cs(CS_ARCH_X86, CS_MODE_32)
    expected_instructions = set()
    uc = Uc(UC_ARCH_X86, UC_MODE_32)
    for page in (0x1060e000, 0x10b2a000, 0x200000, 0x600000, 0x601000):
        uc.mem_map(page, 4096)
    raw_ranges = []
    for start, end in ranges:
        raw = pe_read(game, start, end - start)
        ins = list(cs.disasm(raw, start))
        require(sum(i.size for i in ins) == len(raw), 'Tail decode incomplete')
        expected_instructions.update(i.address for i in ins)
        uc.mem_write(start, raw)
        raw_ranges.append(dict(start=f'{start:x}', end_exclusive=f'{end:x}', bytes_hex=raw.hex()))
    stop = 0x1060efcd
    guard = 0x600000
    callback = 0x601000
    stack = 0x200800
    uc.mem_write(stop, b'\xf4')
    uc.mem_write(guard, b'\xc3')
    uc.mem_write(callback, b'\xc3')
    uc.mem_write(0x10b2a6e8, struct.pack('<I', guard))
    visited, events, writes = set(), [], []
    state = {}

    def hook(machine, address, size, unused):
        if address == stop:
            state['done'] = True
            machine.emu_stop()
        elif address in (guard, callback):
            esp = machine.reg_read(reg.UC_X86_REG_ESP)
            ret, arg = struct.unpack('<II', machine.mem_read(esp, 8))
            events.append((address, ret, arg, machine.reg_read(reg.UC_X86_REG_ECX)))
            # Returning boundary model: callee-saved registers and ESP retained.
            machine.reg_write(reg.UC_X86_REG_EAX, state['callee_result'])
            machine.reg_write(reg.UC_X86_REG_EDX, 0x87654321)
        else:
            require(address in expected_instructions, 'Unexpected tail control flow')
            visited.add(address)

    uc.hook_add(UC_HOOK_CODE, hook)
    uc.hook_add(UC_HOOK_MEM_WRITE, lambda m, a, p, n, v, u: writes.append((p, n)))
    cases = 0

    def check(pointer, code, initial_eax, callee_result):
        nonlocal cases
        uc.mem_write(stack - 64, bytes([0xa5]) * 128)
        for register, value in ((reg.UC_X86_REG_ESP, stack), (reg.UC_X86_REG_ESI, pointer),
                                (reg.UC_X86_REG_EDI, code), (reg.UC_X86_REG_EAX, initial_eax),
                                (reg.UC_X86_REG_ECX, 0xabcdef01), (reg.UC_X86_REG_EBP, 0x23456789),
                                (reg.UC_X86_REG_EBX, 0x34567890), (reg.UC_X86_REG_EFLAGS, 2)):
            uc.reg_write(register, value)
        state.update(done=False, callee_result=callee_result)
        events.clear()
        writes.clear()
        uc.emu_start(0x1060efa2, 0, count=80)
        require(state['done'], 'Tail did not reach epilogue boundary')
        require(uc.reg_read(reg.UC_X86_REG_EAX) == int(pointer != 0), 'Tail result differs')
        require(uc.reg_read(reg.UC_X86_REG_ESP) == stack, 'Tail stack differs')
        for register, value in ((reg.UC_X86_REG_ESI, pointer), (reg.UC_X86_REG_EDI, code),
                                (reg.UC_X86_REG_EBP, 0x23456789), (reg.UC_X86_REG_EBX, 0x34567890)):
            require(uc.reg_read(register) == value, 'Preserved register differs')
        expected = bytearray([0xa5]) * 128
        if pointer == callback:
            require(events == [(guard, 0x1060efc7, code, callback),
                               (callback, 0x1060efc9, code, callback)], 'Dispatch order or arguments differ')
            struct.pack_into('<II', expected, 56, 0x1060efc9, code)
            require(writes == [(stack - 4, 4), (stack - 8, 4), (stack - 8, 4)], 'Tail write order differs')
        else:
            require(not events and not writes, 'Sentinel dispatched or wrote memory')
        require(bytes(uc.mem_read(stack - 64, 128)) == bytes(expected), 'Stack memory differs')
        cases += 1

    for pointer in (0, 1, callback):
        for code in (0, 2, 21, 0xffffffff):
            for initial_eax in range(256):
                for callee_result in (0, 1, 0x80000000, 0xffffffff):
                    check(pointer, code, initial_eax, callee_result)
    require(visited == expected_instructions, 'Tail instruction coverage incomplete')
    # Replace INC EAX with DEC EAX: nonzero path must fail the result assertion.
    require(pe_read(game, 0x1060efcc, 1) == b'\x40', 'Negative control location differs')
    uc.mem_write(0x1060efcc, b'\x48')
    uc.ctl_remove_cache(0x1060efa2, stop)
    caught = False
    try:
        check(1, 21, 0, 0)
    except ValueError as error:
        require(str(error) == 'Tail result differs', 'Unexpected mutation failure')
        caught = True
    require(caught, 'Wrong result escaped detector')
    result = dict(source_sha256=module['sha256'], address='1060ef27', ranges=raw_ranges,
                  instructions=len(expected_instructions), cases=cases, negative_control_caught=caught,
                  dependencies={'encoded-callback-behavior/manifest.json': digest(folder / 'encoded-callback-behavior/manifest.json')},
                  semantics='At epilogue boundary EAX is 0 for decoded null, 1 otherwise. Sentinel 1 skips calls. Ordinary callback follows guard with ECX=callback, receives one stack argument from EDI, and its return value is discarded; caller removes argument.',
                  limitation='Original tail only with valid mapped callback and returning guard/callback models preserving callee-saved registers. No actual guard validation, callback implementation, lock, SEH epilogue, unwind, or whole-function ABI proof.')
    root = folder / 'callback-tail-behavior'
    root.mkdir(exist_ok=True)
    (root / 'report.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    (root / 'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'], files={'report.json': digest(root / 'report.json')}), indent=2), encoding='utf-8')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
