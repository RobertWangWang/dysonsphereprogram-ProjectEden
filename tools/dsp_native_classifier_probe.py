"""Exhaustively verify the scanner's two-byte classifier and actual register effects."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require


def validate_cached(folder, sha):
    root = folder / 'classifier-probe'
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json'); require(marker['source_sha256'] == sha, 'Classifier source differs')
    for name, value in marker['files'].items(): require(digest(root / name) == value, 'Classifier artifact differs')
    return read_json(root / 'report.json')


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    root = GENERATED / 'native/DSPGAME_Data__Plugins__x86_64__rail_api.dll/classifier-probe'; root.mkdir(exist_ok=True)
    path = Path(read_json(GENERATED / 'native/inventory.json')['game_directory']) / 'DSPGAME_Data/Plugins/x86_64/rail_api.dll'
    sha = 'c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1'
    require(digest(path) == sha, 'Game changed'); game = path.read_bytes()
    code = pe_read(game, 0x106392d0, 57); data = pe_read(game, 0x1063930c, 56)
    instructions = list(Cs(CS_ARCH_X86, CS_MODE_32).disasm(code, 0x106392d0))
    require(sum(i.size for i in instructions) == len(code), 'Classifier decode incomplete')
    uc = Uc(UC_ARCH_X86, UC_MODE_32); uc.mem_map(0x10639000, 0x1000); uc.mem_map(0x200000, 0x1000)
    uc.mem_write(0x106392d0, code); uc.mem_write(0x1063930c, data)
    sentinel = 0x10639ff0; stack = 0x200800; visited = set(); writes = []
    uc.mem_write(sentinel, b'\xf4')
    def hook(machine, address, size, unused):
        if address == sentinel: machine.emu_stop()
        else: visited.add(address)
    uc.hook_add(UC_HOOK_CODE, hook); uc.hook_add(UC_HOOK_MEM_WRITE, lambda u,a,p,s,v,x:writes.append((p,s,v)))
    kept = {reg.UC_X86_REG_ECX: 0x12345678, reg.UC_X86_REG_EDX: 0x23456789, reg.UC_X86_REG_EBX: 0x3456789a,
            reg.UC_X86_REG_EBP: 0x456789ab, reg.UC_X86_REG_ESI: 0x56789abc, reg.UC_X86_REG_EDI: 0x6789abcd}
    counts = {}; cases = 0
    for high in range(256):
        for low in range(256):
            args = struct.pack('<III', sentinel, 0xa5a5a500 | high, 0x5a5a5a00 | low)
            uc.mem_write(stack, args); uc.reg_write(reg.UC_X86_REG_ESP, stack); uc.reg_write(reg.UC_X86_REG_EAX, 0xffffffff); uc.reg_write(reg.UC_X86_REG_EFLAGS, 2)
            for register, value in kept.items(): uc.reg_write(register, value)
            try: uc.emu_start(0x106392d0, 0, count=32)
            except Exception as error: raise RuntimeError(f'Classifier {high:02x}/{low:02x}, EIP={uc.reg_read(reg.UC_X86_REG_EIP):x}, ESP={uc.reg_read(reg.UC_X86_REG_ESP):x}') from error
            expected = 7 if 0xd8 <= high <= 0xdb else 8 if 0xdc <= high <= 0xdf else 0 if high == 0xff and low >= 0xfe else 29
            require(uc.reg_read(reg.UC_X86_REG_EAX) == expected and uc.reg_read(reg.UC_X86_REG_EIP) == sentinel, 'Classifier result differs')
            require(uc.reg_read(reg.UC_X86_REG_ESP) == stack + 4 and bytes(uc.mem_read(stack, 12)) == args, 'Stack effect differs')
            require(all(uc.reg_read(register) == value for register, value in kept.items()), 'Preserved register changed')
            counts[str(expected)] = counts.get(str(expected), 0) + 1; cases += 1
    require(not writes and visited == {i.address for i in instructions}, 'Writes or incomplete instruction coverage')
    uc.mem_write(0x106392ee, struct.pack('<I', 9)); uc.mem_write(stack, struct.pack('<III', sentinel, 0xd8, 0))
    uc.ctl_remove_cache(0x106392d0, 0x10639309)
    uc.reg_write(reg.UC_X86_REG_ESP, stack); uc.emu_start(0x106392d0, 0, count=32)
    require(uc.reg_read(reg.UC_X86_REG_EAX) != 7, 'Mutated return constant not detected')
    uc.mem_write(0x106392d0, code)
    report = dict(source_sha256=sha, address='106392d0', code_bytes=len(code), instructions=len(instructions), table_bytes=len(data),
                  cases=cases, result_counts=counts, preserved_registers=['ECX','EDX','EBX','EBP','ESI','EDI'], memory_writes=0,
                  stack_effect='RET pops return address only; caller cleans arguments', negative_return_constant_rejected=True,
                  limitation='Exhaustive for two low-byte arguments with fixed nonzero upper argument bytes. Static instruction coverage and no memory writes support preserved registers; EFLAGS and EAX are modified. Does not prove the caller scanner behavior.')
    (root / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    (root / 'classifier.asm').write_text('\n'.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}' for i in instructions)+'\n', encoding='utf-8')
    (root / 'tables.hex').write_text(data.hex(), encoding='utf-8')
    (root / 'manifest.json').write_text(json.dumps(dict(source_sha256=sha, files={n:digest(root/n) for n in ('report.json','classifier.asm','tables.hex')}),indent=2),encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__':main()
