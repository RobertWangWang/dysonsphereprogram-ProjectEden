"""在 Unicorn 模拟器中执行图形设备 switch 的原始指令片段，验证 guard/索引/RVA。"""
import json
from pathlib import Path
import unicorn
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RDI, UC_X86_REG_RBX, UC_X86_REG_RIP
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import GAME_SHA, pe_read
from dsp_native_quality_repair import PROFILES, GRAPHICS_INDICES, verify


def run_case(game, address, value, corrupt=False):
    branch, table, index, _, targets = PROFILES[address][0]
    start = 0x18071ac7b if address == '18071abf0' else 0x18071afed
    first = start & ~0xfff
    length = ((index + 26 + 0xfff) & ~0xfff) - first
    machine = Uc(UC_ARCH_X86, UC_MODE_64)
    machine.mem_map(first, length)
    machine.mem_write(first, pe_read(game, first, length))
    if corrupt:
        machine.mem_write(index, bytes([1-GRAPHICS_INDICES[0]]))
    machine.reg_write(UC_X86_REG_RDI, value)
    machine.reg_write(UC_X86_REG_RBX, 0x180000000)
    stopped = []
    def hook(uc, pc, size, _):
        if pc in targets:
            stopped.append(pc)
            uc.emu_stop()
    machine.hook_add(UC_HOOK_CODE, hook)
    machine.emu_start(start, 0, count=32)
    if len(stopped) != 1:
        raise ValueError('Snippet did not reach a verified destination')
    normalized = (value - 2) & 0xffffffff
    expected = targets[GRAPHICS_INDICES[normalized]] if normalized <= 25 else targets[1]
    if stopped[0] != expected:
        raise ValueError('Emulated destination differs from verified map')
    return dict(input=value, normalized=normalized, destination=f'{stopped[0]:x}')


def main():
    base = GENERATED / 'native'
    inventory = read_json(base / 'inventory.json')
    game_path = Path(inventory['game_directory']) / 'UnityPlayer.dll'
    if digest(game_path) != GAME_SHA:
        raise ValueError('Binary baseline changed')
    game = game_path.read_bytes()
    folder = base / 'UnityPlayer.dll'
    inputs = list(range(32)) + [0x7fffffff, 0x80000000, 0xfffffffe, 0xffffffff, 0x100000002]
    for address in ('18071abf0', '18071af10'):
        verify(folder, game_path, address=address)
        cases = [run_case(game, address, value) for value in inputs]
        try:
            run_case(game, address, 2, corrupt=True)
        except ValueError as e:
            if str(e) != 'Emulated destination differs from verified map':
                raise
        else:
            raise ValueError('Negative mutation was not detected')
        report = dict(source_sha256=GAME_SHA, address=address, engine='Unicorn '+unicorn.__version__,
                      scope='Original machine-code switch fragment only; stops before destination body, no game process/DLL execution.',
                      cases=cases, negative_index_mutation_rejected=True)
        (folder / 'quality-repair' / address / 'emulation.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(address, len(cases), 'CPU-emulated cases passed; negative mutation rejected')


if __name__ == '__main__':
    main()
