"""Independently check bytes and operand evidence for unresolved relocation entries."""
import collections
import json
from pathlib import Path

from capstone import Cs, CS_ARCH_X86, CS_MODE_32, CS_GRP_JUMP
from capstone.x86 import X86_OP_MEM, X86_OP_IMM
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require
from dsp_native_select import selected_folder


def main():
    root = GENERATED / 'native'
    inventory = read_json(root / 'inventory.json')
    decoder = Cs(CS_ARCH_X86, CS_MODE_32)
    decoder.detail = True
    totals = collections.Counter()
    for module in inventory['files']:
        folder = selected_folder(root, module)
        source = folder / 'relocation-callbacks/failure-audit.json'
        if not source.exists():
            continue
        data = (Path(inventory['game_directory']) / module['path']).read_bytes()
        require(digest(Path(inventory['game_directory']) / module['path']) == module['sha256'], 'Game changed')
        audit = read_json(source)
        require(audit['complete'] and audit['source_sha256'] == module['sha256'], 'Audit incomplete or wrong source')
        report = read_json(folder / 'relocation-callbacks/report.json')
        expected = {r['address'] for r in report['functions'] if r['status'] not in ('decompiled', 'overlaps-existing-function')}
        require(len(audit['entries']) == len(expected) and {r['address'] for r in audit['entries']} == expected, 'Audit scope mismatch')
        rows, counts = [], collections.Counter()
        for row in audit['entries']:
            address = int(row['address'], 16)
            raw = bytes.fromhex(row['bytes_hex'])
            require(pe_read(data, address, len(raw)) == raw, 'Target bytes differ')
            proofs = []
            for ref in row['references']:
                if 'bytes_hex' not in ref:
                    continue
                site = int(ref['from'], 16)
                code = bytes.fromhex(ref['bytes_hex'])
                require(pe_read(data, site, len(code)) == code, 'Reference bytes differ')
                instructions = list(decoder.disasm(code, site))
                require(len(instructions) == 1 and instructions[0].size == len(code), 'Reference instruction mismatch')
                ins = instructions[0]
                for operand in ins.operands:
                    if operand.type == X86_OP_MEM and operand.mem.disp & 0xffffffff == address:
                        kind = 'memory-operand'
                        if ins.group(CS_GRP_JUMP) and operand.mem.base == 0 and operand.mem.index and operand.mem.scale == 4:
                            kind = 'indexed-jump-table'
                        elif ins.mnemonic == 'movzx' and operand.size == 1 and operand.mem.base:
                            kind = 'byte-index-load'
                        proofs.append(dict(site=ref['from'], kind=kind, instruction=ins.mnemonic + ' ' + ins.op_str, bytes_hex=ref['bytes_hex']))
                    elif operand.type == X86_OP_IMM and operand.imm & 0xffffffff == address:
                        proofs.append(dict(site=ref['from'], kind='immediate-address', instruction=ins.mnemonic + ' ' + ins.op_str, bytes_hex=ref['bytes_hex']))
            kinds = {p['kind'] for p in proofs}
            if row['unit_kind'] != 'data':
                category = 'code-decompile-failed'
            elif 'indexed-jump-table' in kinds:
                category = 'data-indexed-jump-table'
            elif 'byte-index-load' in kinds:
                category = 'data-byte-index-table'
            elif 'memory-operand' in kinds:
                category = 'data-other-memory-reference'
            elif 'immediate-address' in kinds:
                category = 'data-address-reference'
            else:
                category = 'data-without-decoded-reference'
            counts[category] += 1
            rows.append(dict(address=row['address'], category=category, proofs=proofs, unit_text=row.get('unit_text')))
        totals.update(counts)
        output = folder / 'relocation-failure-triage'
        output.mkdir(exist_ok=True)
        result = dict(source_sha256=module['sha256'], counts=dict(counts), entries=rows,
                      dependencies={'relocation-callbacks/failure-audit.json': digest(source), 'relocation-callbacks/report.json': digest(folder / 'relocation-callbacks/report.json')},
                      limitation='Data classifications use original Ghidra listing plus independently decoded references. An executable section does not imply every target is code. Table length, bounds and target semantics are not established here. Historical export failures are retained.')
        (output / 'report.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
        (output / 'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'], files={'report.json': digest(output / 'report.json')}), indent=2), encoding='utf-8')
        print(json.dumps(dict(module=module['path'], counts=dict(counts))))
    print(json.dumps(dict(totals=dict(totals))))


if __name__ == '__main__':
    main()
