"""离线模拟两处无就近范围比较的 switch；验证上游赋值约束与原始跳转指令。"""
import json
from pathlib import Path
import unicorn
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import *
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import GAME_SHA, pe_read


def stop_at(machine, start, targets):
    reached = []
    def hook(uc, pc, size, _):
        if pc in targets:
            reached.append(pc)
            uc.emu_stop()
    handle = machine.hook_add(UC_HOOK_CODE, hook)
    try:
        machine.emu_start(start, 0, count=128)
    finally:
        machine.hook_del(handle)
    if len(reached) != 1:
        raise ValueError('Snippet did not stop at expected boundary')
    return reached[0]


def setup(game, page):
    m = Uc(UC_ARCH_X86, UC_MODE_64)
    m.mem_map(page, 0x2000)
    m.mem_write(page, pe_read(game, page, 0x2000))
    m.mem_map(0x181c54000, 0x4000)
    return m


def format_case(game, value, flag, nonzero, corrupt=False):
    m = setup(game, 0x180cd2000)
    descriptor = value if value <= 151 else 0
    m.mem_write(0x181c5401d + descriptor*80, bytes([flag]))
    m.reg_write(UC_X86_REG_RSI, value)
    m.reg_write(UC_X86_REG_RDI, 0)
    stop_at(m, 0x180cd2fb1, [0x180cd2ffa])
    selected = m.reg_read(UC_X86_REG_R8)
    expected = 49 if value <= 49 and ((0x2200000200020 >> value) & 1) else 51 + flag
    if selected != expected or selected not in (49, 51, 52):
        raise ValueError('Format selector domain mismatch')
    # Descriptor-dependent ESI is only tested for zero in the dispatch snippet.
    m.reg_write(UC_X86_REG_RSI, nonzero)
    if corrupt:
        m.mem_write(0x180cd33ac + selected-5, b'\x01')
    reached = stop_at(m, 0x180cd303f, [0x180cd3064, 0x180cd31de])
    if reached != (0x180cd3064 if nonzero else 0x180cd31de):
        raise ValueError('Dispatch target mismatch')
    return dict(input=value, descriptor_flag=flag, nonzero_descriptor=nonzero,
                selected=selected, index=selected-5, destination=f'{reached:x}')


def capability_case(game, flags, nonzero, corrupt=False):
    m = setup(game, 0x180e91000)
    m.mem_map(0x1000000, 0x1000)
    m.mem_write(0x1000174, flags.to_bytes(4, 'little'))
    m.reg_write(UC_X86_REG_RAX, 0x1000000)
    for a in (0x181c5419c, 0x181c5428c):
        m.mem_write(a, nonzero.to_bytes(4, 'little'))
    stop_at(m, 0x180e91cc8, [0x180e91d0f])
    selected = m.reg_read(UC_X86_REG_R9)
    expected = 0 if flags & 0x211 == 0x211 else 3
    if selected != expected:
        raise ValueError('Capability selector domain mismatch')
    m.reg_write(UC_X86_REG_R10, 0x180000000)
    if corrupt:
        m.mem_write(0x180e924e0 + selected, b'\x01')
    reached = stop_at(m, 0x180e91d56, [0x180e91d74, 0x180e91d93])
    if reached != (0x180e91d74 if nonzero else 0x180e91d93):
        raise ValueError('Dispatch target mismatch')
    return dict(flags=flags, nonzero_descriptor=nonzero, index=selected, destination=f'{reached:x}')


def main():
    base = GENERATED / 'native'
    inventory = read_json(base / 'inventory.json')
    game_path = Path(inventory['game_directory']) / 'UnityPlayer.dll'
    if digest(game_path) != GAME_SHA:
        raise ValueError('Source changed')
    game = game_path.read_bytes()
    values = list(range(152)) + [152, 0x7fffffff, 0x80000000, 0xffffffff]
    formats = [format_case(game, value, flag, nonzero)
               for value in values for flag in (0, 1) for nonzero in (0, 1)]
    flags = [(i & 1) | ((i & 2) << 3) | ((i & 4) << 7) for i in range(8)]
    capabilities = [capability_case(game, flag, nonzero) for flag in flags for nonzero in (0, 1)]
    for callback, args in [(format_case, (game, 5, 0, 1)), (capability_case, (game, 0x211, 1))]:
        try:
            callback(*args, corrupt=True)
        except ValueError as e:
            if str(e) != 'Dispatch target mismatch':
                raise
        else:
            raise ValueError('Negative index mutation not detected')
    for address, cases, domain in [('180cd2ed0', formats, [44,46,47]), ('180e91c60', capabilities, [0,3])]:
        root = base / 'UnityPlayer.dll/quality-repair' / address
        root.mkdir(parents=True, exist_ok=True)
        report = dict(source_sha256=GAME_SHA, address=address, engine='Unicorn '+unicorn.__version__,
                      scope='Upstream selector and dispatch snippets, with descriptor values mocked in emulator memory. Not whole-function equivalence.',
                      index_domain=domain, cases=cases, negative_index_mutation_rejected=True)
        (root / 'derived-emulation.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(address, len(cases), 'selector/dispatch cases passed; negative mutation rejected')


if __name__ == '__main__':
    main()
