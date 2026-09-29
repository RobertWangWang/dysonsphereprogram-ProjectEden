"""核验 Unity 函数的两级跳转表修正；不增加基础函数数量。"""
import argparse
import hashlib
import json
import re
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import GAME_SHA, pe_read


PROFILES = {
    '181255510': [(0x1812555d6, 0x1812556f0, 0x1812556f8, 0x1812555bf, [0x1812555e2, 0x1812555d8]),
                  (0x181255669, 0x18125570c, 0x181255714, 0x181255652, [0x181255675, 0x18125566b])],
    '18071abf0': [(0x18071aca0, 0x18071aeec, 0x18071aef4, 0x18071ac80, [0x18071acac, 0x18071acb0])],
    '18071af10': [(0x18071b009, 0x18071b268, 0x18071b270, 0x18071aff2, [0x18071b012, 0x18071b016])],
    '180e91c60': [(0x180e91d72, 0x180e924d8, 0x180e924e0, 0, [0x180e91d74, 0x180e91d93])],
    '180cd2ed0': [(0x180cd3062, 0x180cd33a4, 0x180cd33ac, 0, [0x180cd3064, 0x180cd31de])],
}
GRAPHICS_INDICES = [0,1,0,1,1,1,0,1,1,0,1,0,0,1,0,0,0,1,1,0,0,0,0,0,0,0]
FORMAT_INDICES = [0,0,0,0,1,1,1,1,1,1,1,1,1,1,1,1,0,0,0,0,1,1,1,1,1,1,1,1,1,1,1,1,0,1,1,1,0,1,1,1,0,0,1,0,0,0,0,0]
DEAD_BLOCKS = ['180cd32d2','180cd32e1','180cd32e4','180cd32fc','180cd3304','180cd3307','180cd331f','180cd3337']


def verify(folder, game_path, check_manifest=True, address='181255510'):
    root = folder / 'quality-repair' / address
    path = root / 'report.json'
    if not path.exists():
        return {}
    report = read_json(path)
    if digest(game_path) != GAME_SHA or report['source_sha256'] != GAME_SHA:
        raise ValueError('Unity binary changed')
    if report['address'] != address or report['status'] != 'decompiled' or not report['program_changes_rolled_back']:
        raise ValueError('Repair incomplete or not rolled back')
    game = game_path.read_bytes()
    definitions = PROFILES[address]
    expected_indices = (FORMAT_INDICES if address == '180cd2ed0' else [0]*4 if address == '180e91c60' else
                        [int(i == 12) for i in range(20)] if address == '181255510' else GRAPHICS_INDICES)
    if len(report['tables']) != len(definitions):
        raise ValueError('Unexpected table count')
    for table, (branch, rva, index, guard, targets) in zip(report['tables'], definitions):
        if (table['branch'], table['rva_table'], table['byte_index_table']) != tuple(f'{a:x}' for a in (branch, rva, index)):
            raise ValueError('Table address mismatch')
        opcode = b'\xff\xe1' if guard else b'\xff\xe2'
        if pe_read(game, branch, 2) != opcode or (guard and pe_read(game, guard, 1) != bytes([len(expected_indices)-1])):
            raise ValueError('Instruction guard mismatch')
        actual = [0x180000000 + v for v in struct.unpack('<II', pe_read(game, rva, 8))]
        if actual != targets:
            raise ValueError('RVA destinations differ')
        indices = pe_read(game, index, len(expected_indices))
        if list(indices) != expected_indices:
            raise ValueError('Byte index mapping differs')
        if table['targets_by_input'] != [f'{targets[i]:x}' for i in indices]:
            raise ValueError('Exhaustive case mapping differs')
    code = root / (address + '.c')
    text = code.read_text(encoding='utf-8')
    warnings = [' '.join(w.split()) for w in re.findall(r'/\*\s*WARNING:\s*(.*?)\*/', text, re.S)]
    expected_warnings = ['Switch is manually overridden'] * len(definitions)
    if address == '180cd2ed0':
        expected_warnings = [f'Removing unreachable block (ram,0x000{a})' for a in DEAD_BLOCKS] + expected_warnings
    if warnings != expected_warnings or 'halt_baddata' in text:
        raise ValueError('Unexpected remaining control flow warning')
    for local in (('local_48', 'local_38') if address == '181255510' else ()):
        if not re.search(r'case 0:\s*' + local + r' = 4;\s*break;\s*case 1:\s*' + local + r' = 7;', text):
            raise ValueError('C branch values differ from inspected assembly')
    if address in ('18071abf0', '18071af10'):
        for _, _, _, _, targets in definitions:
            if pe_read(game, targets[0], 2) != b'\xb0\x01' or pe_read(game, targets[1], 2) != b'\x32\xc0':
                raise ValueError('Boolean destination instructions differ')
        match = re.search(r'case 0:\s*(bVar\d+) = true;', text)
        if not match or match.group(1) + ' = false;' not in text:
            raise ValueError('C boolean cases differ')
    if address == '180e91c60':
        emulation = read_json(root / 'derived-emulation.json')
        if (emulation['source_sha256'] != GAME_SHA or emulation['address'] != address
                or emulation['index_domain'] != [0,3] or not emulation['negative_index_mutation_rejected']):
            raise ValueError('Derived-domain evidence mismatch')
        expected_flags = [(i & 1) | ((i & 2) << 3) | ((i & 4) << 7) for i in range(8)]
        expected_cases = [dict(flags=f, nonzero_descriptor=n, index=0 if f == 0x211 else 3,
                               destination='180e91d74' if n else '180e91d93')
                          for f in expected_flags for n in (0,1)]
        if emulation['cases'] != expected_cases:
            raise ValueError('Incomplete selector/dispatch evidence')
        if 'case 0:' not in text or 'FUN_1804cc4b0' not in text:
            raise ValueError('Expected allocation branch missing')
    if address == '180cd2ed0':
        proof = read_json(root / 'dead-branch-proof.json')
        patterns = {'180cd3166':'3bd1410f95c10f8546010000','180cd32b8':'4584c97415'}
        if (proof['source_sha256'] != GAME_SHA or proof['address'] != address
                or proof['decoded_bytes'] != 1234 or proof['instructions'] != 306
                or proof['incoming'] != [['180cd32bb','180cd32d2']]
                or proof['guard_incoming'] != [['180cd316c','180cd32b8']]
                or proof['dead_range'] != ['180cd32d2','180cd3343']
                or proof['predicate_bytes'] != patterns or not proof['negative_predicate_mutation_rejected']):
            raise ValueError('Unreachable branch proof mismatch')
        if hashlib.sha256(pe_read(game, 0x180cd2ed0, 1234)).hexdigest() != proof['code_sha256']:
            raise ValueError('Proven function bytes differ')
        for addr, pattern in patterns.items():
            if pe_read(game, int(addr,16), len(bytes.fromhex(pattern))).hex() != pattern:
                raise ValueError('Predicate instructions changed')
        values = [0,1,0x7fffffff,0x80000000,0xffffffff]
        expected_cases = [dict(ecx=a,edx=b,initial_r9=r9,destination='180cd3172' if a==b else '180cd32bd')
                          for a in values for b in values for r9 in (0,0xffffffffffffffff)]
        if proof['cases'] != expected_cases:
            raise ValueError('Predicate simulation coverage differs')
        emulation = read_json(root / 'derived-emulation.json')
        expected_cases = []
        for value in list(range(152))+[152,0x7fffffff,0x80000000,0xffffffff]:
            for flag in (0,1):
                for nonzero in (0,1):
                    selected = 49 if value <= 49 and (0x2200000200020 >> value)&1 else 51+flag
                    expected_cases.append(dict(input=value,descriptor_flag=flag,nonzero_descriptor=nonzero,
                                               selected=selected,index=selected-5,
                                               destination='180cd3064' if nonzero else '180cd31de'))
        if (emulation['source_sha256'] != GAME_SHA or emulation['address'] != address
                or emulation['index_domain'] != [44,46,47] or emulation['cases'] != expected_cases
                or not emulation['negative_index_mutation_rejected']):
            raise ValueError('Format selector evidence mismatch')
    manifest_path = root / 'manifest.json'
    if check_manifest:
        manifest = read_json(manifest_path)
        if not {'report.json', address + '.c'} <= manifest['files'].keys():
            raise ValueError('Incomplete repair manifest')
        for name, expected in manifest['files'].items():
            if digest(root / name) != expected:
                raise ValueError('Repair artifact changed')
    return dict(address=address, file=code.relative_to(folder).as_posix(),
                mode='verified-two-stage-switch-repair', case_count=3 if address == '180cd2ed0' else 2 if address == '180e91c60' else len(definitions)*len(expected_indices), report=path.relative_to(folder).as_posix())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--address', choices=PROFILES, default='181255510')
    address = parser.parse_args().address
    base = GENERATED / 'native'
    inventory = read_json(base / 'inventory.json')
    folder = base / 'UnityPlayer.dll'
    result = verify(folder, Path(inventory['game_directory']) / 'UnityPlayer.dll', False, address)
    if not result:
        raise ValueError('No repair output')
    root = folder / 'quality-repair' / address
    tracked = ['report.json', address + '.c']
    if (root / 'emulation.json').exists():
        tracked.append('emulation.json')
    if address in ('180e91c60','180cd2ed0'):
        tracked.append('derived-emulation.json')
    if address == '180cd2ed0':
        tracked.append('dead-branch-proof.json')
    report = dict(source_sha256=GAME_SHA, result=result,
                  scope='All bounded byte-index inputs and destination instructions checked. Not a whole-function equivalence proof.',
                  files={n: digest(root / n) for n in tracked})
    (root / 'manifest.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('Verified', result['case_count'], 'case mappings; query replacement ready:', result['address'])


if __name__ == '__main__':
    main()
