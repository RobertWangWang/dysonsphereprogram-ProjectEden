"""Validate regenerated copy C, declared ranges, ABI and explicit jump-table evidence."""
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require
from dsp_native_copy_pair import validate_cached as validate_pair


def validate_cached(folder, sha):
    root = folder / 'copy-type-repair'
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json')
    require(marker['source_sha256'] == sha, 'Copy types source differs')
    for name, value in marker['files'].items():
        require(digest(root / name) == value, 'Copy types evidence differs')
    report = read_json(root / 'verification.json')
    for name, value in report['dependencies'].items():
        require(digest(folder / name) == value, 'Copy types dependency differs')
    return report


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    inv = read_json(GENERATED / 'native/inventory.json')
    module = next(m for m in inv['files'] if Path(m['path']).name == 'rail_api.dll')
    folder = GENERATED / 'native' / module['output']; root = folder / 'copy-type-repair'
    path = Path(inv['game_directory']) / module['path']; report = read_json(root / 'report.json')
    require(digest(path) == report['source_sha256'] == module['sha256'] and report['program_changes_rolled_back'], 'Source or transaction differs')
    game = path.read_bytes(); pair = validate_pair(folder, module['sha256']); require(pair, 'Pair evidence missing')
    cs = Cs(CS_ARCH_X86, CS_MODE_32); files = ['report.json', 'verification.json']; rows = []
    require({r['address'] for r in report['functions']} == {'105f9bf0','105fafc0'}, 'Scope differs')
    normalized_ranges = []
    for row in report['functions']:
        address = row['address']; base = int(address, 16); coverage, starts = set(), set()
        declared = set()
        for lo, hi in row['ranges']: declared.update(range(int(lo,16), int(hi,16)+1))
        for line in (root / (address + '.asm')).read_text().splitlines():
            pc, data, _ = line.split(' ', 2); pc = int(pc,16); raw = bytes.fromhex(data)
            require(pe_read(game, pc, len(raw)) == raw, 'Original instruction bytes differ')
            ins = list(cs.disasm(raw, pc)); require(len(ins) == 1 and ins[0].size == len(raw), 'Instruction boundary differs')
            span = set(range(pc, pc+len(raw))); require(not coverage & span, 'Overlapping instructions')
            coverage.update(span); starts.add(pc)
        require(coverage == declared and len(coverage) == 1330 and len(starts) == row['instructions'] == 404, 'Declared body coverage differs')
        # Ensure every positively executed offset has an instruction in the regenerated body.
        executed = {base + off for off in pair['visited_offsets'][address]}
        require(executed == starts, 'Declared instruction coverage incomplete or inconsistent')
        normalized_ranges.append([(int(lo,16)-base, int(hi,16)-base) for lo,hi in row['ranges']])
        targets = list(struct.unpack('<4I', pe_read(game, base+0x264, 16)))
        require(all(t in starts for t in targets) and len(set(targets)) == 4, 'Dispatch targets differ')
        require(pe_read(game, base+0x225, 2) == b'\xff\xe0', 'Indirect branch differs')
        code = (root / (address + '.c')).read_text()
        require('void * __cdecl' in code and 'void *destination,void *source,uint size' in code.replace('\n','').replace('  ',''), 'ABI annotation missing')
        require('return destination;' in code and 'CONCAT44' not in code, 'Return repair missing')
        require('Could not recover jumptable' not in code and 'Treating indirect jump as call' not in code, 'Unresolved dispatch remains')
        require(row['warnings'] == ['                    /* WARNING: Switch is manually overridden */'], 'Unexpected warning scope')
        rows.append(dict(address=address, file=address+'.c', signature=row['signature'], body_bytes=len(coverage), instructions=len(starts),
                         ranges=row['ranges'], executed_instructions=len(executed), jump_targets=[f'{t:x}' for t in targets], warnings=row['warnings']))
        files.extend([address+'.c',address+'.asm'])
    require(normalized_ranges[0] == normalized_ranges[1], 'Relocated body ranges differ')
    result = dict(functions=rows, cases=pair['cases'], dependencies={'copy-pair-behavior/manifest.json':digest(folder/'copy-pair-behavior/manifest.json')},
                  limitation='Evidence-based ABI and explicit four-target jump override. Raw bytes, instruction boundaries, body coverage and original-execution evidence verified. Regenerated C has not been compiled for equivalence; manual-override warnings retained. Original project/index unchanged.')
    (root/'verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={n:digest(root/n) for n in files}),indent=2),encoding='utf-8')
    print(json.dumps(result))


if __name__ == '__main__': main()
