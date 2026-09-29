"""导出三处 Unity FMA4 完整反汇编；核验展开链、代码/表边界与指令重汇编。"""
import argparse
import hashlib
import json
import re
import struct
import subprocess
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import GAME_SHA, pe_read, require

IMAGE = 0x180000000
# Code end is exclusive; table start may follow alignment bytes.
PROFILES = {
    0x1813b1070: (0x1813b53dc, 0x1813b53dc, 0x1813b545c,
                  [0x1813b1f35, 0x1813b2bb8, 0x1813b3d13, 0x1813b4a27]),
    0x1813b5460: (0x1813b96f2, 0x1813b96f4, 0x1813b9774,
                  [0x1813b6346, 0x1813b6ff4, 0x1813b7e03, 0x1813b8a64]),
    0x1813b9780: (0x1813bd53c, 0x1813bd53c, 0x1813bd5bc,
                  [0x1813ba3e1, 0x1813baf9c, 0x1813bbfb8, 0x1813bcb8b]),
}


def unwind_groups(data):
    pe = struct.unpack_from('<I', data, 60)[0]
    rva, size = struct.unpack_from('<II', data, pe + 24 + 112 + 3 * 8)
    require(size % 12 == 0, 'Partial runtime function record')
    rows = list(struct.iter_unpack('<III', pe_read(data, IMAGE + rva, size)))
    known = set(rows)

    def root(row, seen):
        require(row not in seen, 'Cyclic unwind chain')
        seen = seen | {row}
        version_flags, _, count, _ = pe_read(data, IMAGE + row[2], 4)
        require(version_flags & 7 == 1, 'Unexpected unwind version')
        flags = version_flags >> 3
        if flags & 4:
            require(not flags & 3, 'Conflicting unwind flags')
            offset = IMAGE + row[2] + 4 + ((count + 1) & ~1) * 2
            parent = struct.unpack('<III', pe_read(data, offset, 12))
            require(parent in known, 'Unwind parent absent from exception directory')
            return root(parent, seen)
        return row

    groups = {a: [] for a in PROFILES}
    for row in rows:
        # All fragments selected by root, rather than just proximity.
        if 0x13b1000 <= row[0] < 0x13bd600:
            entry = root(row, set())[0] + IMAGE
            if entry in groups:
                groups[entry].append(row)
    return groups


def validate_cached(folder, source_sha):
    records = {}
    for path in sorted((folder / 'fma4-disassembly').glob('*/manifest.json')):
        manifest = read_json(path)
        require(source_sha == GAME_SHA == manifest['source_sha256'], 'FMA4 source changed')
        for name, sha in manifest['files'].items():
            require(digest(path.parent / name) == sha, 'FMA4 artifact changed')
        report = read_json(path.parent / 'report.json')
        require(report['status'] == 'verified-disassembly-only' and report['source_sha256'] == source_sha,
                'Unverified FMA4 report')
        flow_path = path.parent / 'normal-flow.json'
        if flow_path.exists():
            require('normal-flow.json' in manifest['files'], 'Unhashed flow supplement')
            flow = read_json(flow_path)
            require(flow['status'] == 'normal-entry-gpr-flow-verified' and flow['source_sha256'] == source_sha
                    and flow['code_sha256'] == report['code_sha256'] and not flow['unresolved'], 'Unverified flow supplement')
            expected = {t['branch']: sorted(set(t['targets'])) for t in report['switch_tables']}
            require(flow['resolved'] == expected and len(flow['table_loads']) == 4, 'Flow table set mismatch')
            report['normal_flow'] = flow
        records[report['address']] = (path.parent, report)
    return records


def dumpbin_fmas(text):
    """Extract instruction and continuation bytes, without treating table data as code."""
    records = {}
    current = None
    for line in text.splitlines():
        match = re.match(r'^\s+([0-9A-F]{16}): ((?:[0-9A-F]{2} )*[0-9A-F]{2})\s{2,}(\S+)(?:\s+(.*))?$', line)
        if match:
            address, raw, mnemonic, operands = match.groups()
            current = int(address, 16) if mnemonic.lower() == 'vfmaddps' else None
            if current is not None:
                records[current] = dict(bytes=bytes.fromhex(raw), operands=operands)
        elif current is not None:
            continuation = re.match(r'^\s+((?:[0-9A-F]{2} )*[0-9A-F]{2})\s*$', line)
            if continuation:
                records[current]['bytes'] += bytes.fromhex(continuation[1])
            else:
                current = None
    return records


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64, CS_GRP_JUMP
    from capstone.x86 import X86_OP_IMM, X86_OP_MEM
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--nasm', type=Path, required=True)
    parser.add_argument('--dumpbin', type=Path, required=True)
    args = parser.parse_args()
    inventory = read_json(GENERATED / 'native/inventory.json')
    game_path = Path(inventory['game_directory']) / 'UnityPlayer.dll'
    require(digest(game_path) == GAME_SHA, 'Unity baseline changed')
    game = game_path.read_bytes()
    groups = unwind_groups(game)
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    decoder.detail = True
    for entry, (end, table_start, extent_end, jumps) in PROFILES.items():
        out = GENERATED / 'native/UnityPlayer.dll/fma4-disassembly' / f'{entry:x}'
        out.mkdir(parents=True, exist_ok=True)
        (out / 'manifest.json').unlink(missing_ok=True)
        rows = sorted(groups[entry])
        require(rows and rows[0][0] + IMAGE == entry and rows[-1][1] + IMAGE == extent_end,
                'Unwind extent changed')
        require(all(a[1] == b[0] for a, b in zip(rows, rows[1:])), 'Unwind extent has a gap')
        code = pe_read(game, entry, end - entry)
        ins = list(decoder.disasm(code, entry))
        require(sum(i.size for i in ins) == len(code), 'Incomplete code decoding')
        by_address = {i.address: i for i in ins}
        indirect = [i.address for i in ins if i.group(CS_GRP_JUMP) and i.operands[0].type != X86_OP_IMM]
        require(indirect == jumps, 'Indirect branch set changed')
        edges = []
        for i in ins:
            if i.group(CS_GRP_JUMP) and i.operands[0].type == X86_OP_IMM:
                require(i.operands[0].imm in by_address, 'Direct branch target is not an instruction')
                edges.append([f'{i.address:x}', f'{i.operands[0].imm:x}'])
        tables = []
        for number, jump in enumerate(jumps):
            table = table_start + number * 32
            targets = [IMAGE + r for r in struct.unpack('<8I', pe_read(game, table, 32))]
            require(all(t in by_address for t in targets), 'Table target is not an instruction')
            # Verify this particular table is referenced near this branch, with EAX <= 7 guard.
            pos = next(n for n, i in enumerate(ins) if i.address == jump)
            window = ins[pos - 10:pos]
            require(any(i.mnemonic == 'cmp' and i.op_str == 'eax, 7' for i in window), 'Guard missing')
            require(any(i.mnemonic == 'ja' for i in window), 'Unsigned range branch missing')
            require(any(i.mnemonic == 'mov' and len(i.operands) == 2 and i.operands[1].type == X86_OP_MEM
                        and i.operands[1].mem.disp == table - IMAGE
                        and i.reg_name(i.operands[1].mem.index) == 'rax' and i.operands[1].mem.scale == 4
                        for i in window), 'Table reference missing')
            tables.append(dict(branch=f'{jump:x}', table=f'{table:x}', targets=[f'{t:x}' for t in targets],
                               guard_context=[f'{i.address:x} {i.mnemonic} {i.op_str}' for i in window]))
        require(table_start + 128 == extent_end, 'Unexpected table extent')
        padding = pe_read(game, end, table_start - end) if table_start > end else b''
        require(padding == (bytes.fromhex('6690') if entry == 0x1813b5460 else b''), 'Unexpected alignment bytes')
        fmas = [i for i in ins if i.mnemonic == 'vfmaddps']
        require(len(fmas) == 40, 'FMA4 count changed')
        dump = subprocess.run([str(args.dumpbin.resolve()), '/nologo', '/disasm',
                               f'/range:0x{entry:x},0x{end-1:x}', str(game_path)],
                              check=True, capture_output=True).stdout.decode('utf-8', errors='replace')
        (out / 'dumpbin.txt').write_text(dump, encoding='utf-8')
        independently_decoded = dumpbin_fmas(dump)
        require(set(independently_decoded) == {i.address for i in fmas}, 'FMA4 decoders disagree on addresses')
        require(all(independently_decoded[i.address]['bytes'] == i.bytes for i in fmas),
                'FMA4 decoders disagree on instruction bytes or boundaries')
        source = 'bits 64\n' + ''.join(i.mnemonic + ' ' + i.op_str.replace('xmmword ptr', 'oword') + '\n' for i in fmas)
        (out / 'fma4-roundtrip.asm').write_text(source, encoding='utf-8')
        subprocess.run([str(args.nasm.resolve()), '-f', 'bin', 'fma4-roundtrip.asm', '-o', 'fma4-roundtrip.bin'],
                       cwd=out, check=True, capture_output=True)
        rebuilt = (out / 'fma4-roundtrip.bin').read_bytes()
        original = b''.join(i.bytes for i in fmas)
        require(rebuilt == original, 'FMA4 roundtrip differs')
        mutation = bytearray(original)
        mutation[len(fmas[0].bytes) - 1] ^= 0x10
        require(bytes(mutation) != rebuilt, 'Negative operand mutation accepted')
        (out / 'game.asm').write_text(''.join(f'{i.address:x}  {i.bytes.hex()}  {i.mnemonic} {i.op_str}\n' for i in ins), encoding='utf-8')
        (out / 'tables.bin').write_bytes(pe_read(game, table_start, 128))
        report = dict(status='verified-disassembly-only', source_sha256=GAME_SHA, address=f'{entry:x}',
                      code_end_exclusive=f'{end:x}', extent_end_exclusive=f'{extent_end:x}', code_bytes=len(code),
                      instruction_count=len(ins), fma4_count=len(fmas), fma4_bytes=len(original),
                      code_sha256=hashlib.sha256(code).hexdigest(), alignment_bytes=padding.hex(),
                      runtime_functions=[dict(start=f'{IMAGE+s:x}', end_exclusive=f'{IMAGE+e:x}', unwind_rva=f'{u:x}') for s,e,u in rows],
                      direct_branch_edges=edges, switch_tables=tables, negative_operand_mutation_rejected=True,
                      nasm_sha256=digest(args.nasm), dumpbin_sha256=digest(args.dumpbin),
                      independent_decoder_fma4_boundaries_verified=len(independently_decoded),
                      limitation='Complete linear code decoding and exact FMA4 instruction reassembly only. Tables have verified references and target boundaries; global dominance, exception paths, algorithm identity, floating-point semantics and C equivalence are not proven.')
        (out / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        (out / 'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,
            files={p.name: digest(p) for p in out.iterdir() if p.is_file() and p.name != 'manifest.json'}), indent=2), encoding='utf-8')
        print(f'{entry:x}: {len(code)} code bytes, {len(ins)} instructions, 40 exact FMA4 roundtrips, 32 table targets')


if __name__ == '__main__':
    main()
