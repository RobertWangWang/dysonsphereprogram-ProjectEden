"""Recover missing data references from exact relocated instruction operands and bounded tables."""
import collections
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require


def validate_cached(folder, sha):
    root = folder / 'relocation-operand-audit'
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json')
    require(marker['source_sha256'] == sha, 'Operand audit source differs')
    for name, value in marker['files'].items(): require(digest(root / name) == value, 'Operand audit artifact differs')
    result = read_json(root / 'verification.json')
    for name, value in result['dependencies'].items(): require(digest(folder / name) == value, 'Operand audit dependency differs')
    return result


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    from capstone.x86 import X86_OP_REG, X86_OP_MEM, X86_OP_IMM, X86_REG_ESP
    decoder = Cs(CS_ARCH_X86, CS_MODE_32); decoder.detail = True
    base = GENERATED / 'native/DSPGAME_Data__Plugins__x86_64__rail_api.dll'
    root = base / 'relocation-operand-audit'
    audit = read_json(root / 'report.json')
    discovery = read_json(base / 'relocation-audit/report.json')
    triage = read_json(base / 'relocation-failure-triage/report.json')
    inventory = read_json(GENERATED / 'native/inventory.json')
    path = Path(inventory['game_directory']) / 'DSPGAME_Data/Plugins/x86_64/rail_api.dll'
    require(digest(path) == audit['source_sha256'] == discovery['source_sha256'] == triage['source_sha256'], 'Source differs')
    require(audit['complete'], 'Audit incomplete'); game = path.read_bytes()
    expected = {r['address'] for r in triage['entries'] if r['category'] == 'data-without-decoded-reference'}
    require({r['target'] for r in audit['entries']} == expected, 'Target scope differs')
    dependencies = {n: digest(base / n) for n in ('relocation-audit/report.json', 'relocation-failure-triage/report.json', 'relocation-operand-audit/report.json')}
    supplement_root = base / 'relocation-callbacks'
    supplement_marker = read_json(supplement_root / 'manifest.json')
    require(supplement_marker['source_sha256'] == audit['source_sha256'], 'Supplement source differs')
    require(digest(supplement_root / 'report.json') == supplement_marker['files']['report.json'], 'Supplement report changed')
    supplement = read_json(supplement_root / 'report.json')['functions']
    dependencies['relocation-callbacks/manifest.json'] = digest(supplement_root / 'manifest.json')
    supplement_references = 0
    for row in audit['entries']:
        if 'instruction' in row: continue
        slot = int(row['slot'], 16)
        owners = [f for f in supplement if any(int(lo, 16) <= slot <= int(hi, 16) for lo, hi in f.get('body_ranges_inclusive', []))]
        anchored = not owners and row['slot'] == '106320e8' and row['target'] == '10632224'
        if not owners and not anchored: continue
        if anchored:
            owner = next(f for f in supplement if f['address'] == '10632090')
        else:
            require(len(owners) == 1, 'Ambiguous supplementary owner'); owner = owners[0]
        name = 'functions/' + owner['address'] + '.asm'
        require(digest(supplement_root / name) == supplement_marker['files'][name], 'Supplement assembly changed')
        dependencies['relocation-callbacks/' + name] = digest(supplement_root / name)
        instructions = []
        for line in (supplement_root / name).read_text().splitlines():
            address, hexbytes, text = line.split(' ', 2)
            instructions.append(dict(address=address, bytes_hex=hexbytes, text=text))
        if anchored:
            anchor = next(i for i in instructions if i['address'] == '106320ac')
            raw = bytes.fromhex(anchor['bytes_hex'])
            require(pe_read(game, 0x106320ac, len(raw)) == raw, 'Anchor bytes differ')
            branch = list(decoder.disasm(raw, 0x106320ac))
            require(len(branch) == 1 and branch[0].mnemonic == 'je' and branch[0].operands[0].type == X86_OP_IMM and branch[0].operands[0].imm == 0x106320bb, 'Anchor target differs')
            block = pe_read(game, 0x106320bb, 0x106320ec - 0x106320bb)
            decoded = list(decoder.disasm(block, 0x106320bb))
            require(sum(i.size for i in decoded) == len(block), 'Anchored decode incomplete')
            instructions = [dict(address=f'{i.address:x}', bytes_hex=i.bytes.hex(), text=i.mnemonic + ' ' + i.op_str) for i in decoded]
        matches = [n for n, i in enumerate(instructions) if int(i['address'], 16) <= slot < int(i['address'], 16) + len(bytes.fromhex(i['bytes_hex']))]
        require(len(matches) == 1, 'Slot has no unique exported instruction')
        n = matches[0]
        row.update(instruction=instructions[n], before=list(reversed(instructions[max(0, n-6):n])), owner=owner['address'], provenance='verified-direct-edge-anchored-decode' if anchored else 'verified-relocation-callback-assembly')
        if anchored: row['anchor'] = dict(site='106320ac', target='106320bb', bytes_hex=anchor['bytes_hex'], decoded_bytes_hex=block.hex())
        if n + 1 < len(instructions): row['next'] = instructions[n+1]
        supplement_references += 1
    slots = {(r['slot'], r['target']) for r in discovery['table_entries']}
    decoder = Cs(CS_ARCH_X86, CS_MODE_32); decoder.detail = True
    def decode(record):
        pc = int(record['address'], 16); raw = bytes.fromhex(record['bytes_hex'])
        require(pe_read(game, pc, len(raw)) == raw, 'Instruction bytes differ')
        items = list(decoder.disasm(raw, pc))
        require(len(items) == 1 and items[0].size == len(raw), 'Instruction boundary differs')
        return items[0]
    rows, counts = [], collections.Counter()
    for row in audit['entries']:
        target = int(row['target'], 16); slot = int(row['slot'], 16)
        require((row['slot'], row['target']) in slots, 'Slot absent from discovery')
        require(int.from_bytes(pe_read(game, slot, 4), 'little') == target, 'Pointer mismatch')
        if 'instruction' not in row:
            counts['relocation-site-not-decoded'] += 1
            rows.append(dict(target=row['target'], slot=row['slot'], kind='relocation-site-not-decoded'))
            continue
        ins = decode(row['instruction'])
        require(ins.address + ins.disp_offset == slot and ins.disp_size == 4, 'Relocation is not exact displacement')
        memory = [o for o in ins.operands if o.type == X86_OP_MEM and o.mem.disp & 0xffffffff == target]
        require(len(memory) == 1, 'No matching memory operand')
        mem = memory[0]; item = dict(target=row['target'], slot=row['slot'], site=f'{ins.address:x}', owner=row.get('owner'), provenance=row.get('provenance', 'original-ghidra-listing'))
        if 'anchor' in row: item['anchor'] = row['anchor']
        before = [decode(r) for r in row['before']]
        index_register, dispatch, byte_table = None, None, False
        byte_address = target; bound_site = ins.address
        if ins.mnemonic == 'movzx' and mem.size == 1 and mem.mem.index == 0 and ins.operands[0].type == X86_OP_REG:
            index_register = mem.mem.base; dispatch = decode(row['next']); byte_table = True
            require(dispatch.address == ins.address + ins.size and dispatch.mnemonic == 'jmp', 'Non-adjacent dispatch')
            jumpmem = dispatch.operands[0]
            require(jumpmem.type == X86_OP_MEM and jumpmem.mem.base == 0 and jumpmem.mem.scale == 4 and jumpmem.mem.index == ins.operands[0].reg, 'Dispatch does not use loaded index')
            item['kind'] = 'byte-index-table'; table = jumpmem.mem.disp & 0xffffffff
        elif ins.mnemonic == 'jmp' and mem.mem.base == 0 and mem.mem.scale == 4 and mem.mem.index:
            index_register = mem.mem.index; dispatch = ins; table = target; item['kind'] = 'absolute-jump-table'
            if before and before[0].mnemonic == 'movzx':
                load=before[0]
                if (load.address+load.size==ins.address and load.operands[0].type==X86_OP_REG and load.operands[0].reg==index_register and
                    load.operands[1].type==X86_OP_MEM and load.operands[1].size==1 and load.operands[1].mem.index==0):
                    byte_table=True; byte_address=load.operands[1].mem.disp & 0xffffffff; index_register=load.operands[1].mem.base
                    bound_site=load.address; before=before[1:]; item['bound_via_byte_index']=True
        else:
            item['kind'] = 'other-memory-reference'
        if index_register and len(before) >= 2:
            # Permit only contiguous stack loads that provably do not change the index.
            skipped=[]; cursor=bound_site
            while len(before)>=3:
                candidate=before[0]
                if not (candidate.mnemonic=='mov' and candidate.address+candidate.size==cursor and
                        candidate.operands[0].type==X86_OP_REG and candidate.operands[0].reg not in (index_register,X86_REG_ESP) and
                        candidate.operands[1].type==X86_OP_MEM and candidate.operands[1].mem.base==X86_REG_ESP and candidate.operands[1].mem.index==0):break
                skipped.append(f'{candidate.address:x}'); cursor=candidate.address; before=before[1:]
            guard, compare = before[:2]
            bounded = (guard.mnemonic == 'ja' and compare.mnemonic == 'cmp' and
                       compare.address + compare.size == guard.address and guard.address + guard.size == cursor and
                       compare.operands[0].type == X86_OP_REG and compare.operands[0].reg == index_register and
                       compare.operands[1].type == X86_OP_IMM and 0 <= compare.operands[1].imm < 65536)
            if bounded:
                limit = compare.operands[1].imm
                indices = list(pe_read(game, byte_address, limit + 1)) if byte_table else list(range(limit + 1))
                pointers = list(struct.unpack('<' + 'I' * (max(indices) + 1), pe_read(game, table, (max(indices) + 1) * 4)))
                item.update(bound=limit, guard=f'{guard.address:x}', compare=f'{compare.address:x}', default=f'{guard.operands[0].imm & 0xffffffff:x}',
                            dispatch=f'{dispatch.address:x}', pointer_table=f'{table:x}', indices=indices,
                            targets_by_index=[f'{pointers[n]:x}' for n in indices])
                if byte_table:item['byte_index_table']=f'{byte_address:x}'
                if skipped:item['intervening_stack_loads']=list(reversed(skipped))
                counts['bounded_tables'] += 1; counts['bounded_input_slots'] += limit + 1
        counts[item['kind']] += 1; rows.append(item)
    data_ranges=[]
    for row in rows:
        if 'bound' not in row:continue
        if row.get('byte_index_table'):data_ranges.append((int(row['byte_index_table'],16),len(row['indices']),row))
        data_ranges.append((int(row['pointer_table'],16),(max(row['indices'])+1)*4,row))
    quarantined=[]
    for f in supplement:
        if f['status']!='decompiled':continue
        address=int(f['address'],16)
        match=next(((start,size,row) for start,size,row in data_ranges if start<=address<start+size),None)
        if match:
            start,size,row=match
            quarantined.append(dict(address=f['address'],table_start=f'{start:x}',table_size=size,owner=row['owner'],dispatch=row['dispatch'],reason='Exported candidate entry lies inside independently verified table bytes; retain raw output as historical misclassification.'))
    result = dict(source_sha256=audit['source_sha256'], counts=dict(counts), entries=rows, supplement_references=supplement_references, quarantined_entries=quarantined,
                  dependencies=dependencies,
                  limitation='Exact relocation operands and bounded dispatch verified, including byte-index stages and index-preserving stack loads. Bounds describe the normalized input index, not source-level input. Table mapping does not prove caller domains, target bodies or whole-function semantics.')
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
    import unicorn.x86_const as registers
    uc = Uc(UC_ARCH_X86, UC_MODE_32); uc.mem_map(0x10000000, 0x1000000);uc.mem_map(0x200000,0x2000);uc.reg_write(registers.UC_X86_REG_ESP,0x200800)
    active = [0, 0]; cases = 0
    def hook(machine, address, size, unused):
        if not active[0] <= address < active[1]: machine.emu_stop()
    uc.hook_add(UC_HOOK_CODE, hook)
    first_probe = None
    for row in rows:
        if 'bound' not in row: continue
        start = int(row['compare'], 16); dispatch = int(row['dispatch'], 16)
        branch = next(decoder.disasm(pe_read(game, dispatch, 15), dispatch))
        active[:] = [start, dispatch + branch.size]
        uc.mem_write(start, pe_read(game, start, active[1] - start))
        index_base = int(row.get('byte_index_table',row['target']), 16); pointer_base = int(row['pointer_table'], 16)
        if 'byte_index_table' in row: uc.mem_write(index_base, bytes(row['indices']))
        pointer_size = (max(row['indices']) + 1) * 4
        uc.mem_write(pointer_base, pe_read(game, pointer_base, pointer_size))
        compare = next(decoder.disasm(pe_read(game, start, 15), start))
        register = getattr(registers, 'UC_X86_REG_' + compare.reg_name(compare.operands[0].reg).upper())
        for value in list(range(row['bound'] + 1)) + [row['bound'] + 1, 0xffffffff]:
            uc.reg_write(register, value); uc.reg_write(registers.UC_X86_REG_EFLAGS, 2)
            uc.emu_start(start, 0, count=16)
            expected_pc = int(row['targets_by_index'][value] if value <= row['bound'] else row['default'], 16)
            require(uc.reg_read(registers.UC_X86_REG_EIP) == expected_pc, 'Bounded table simulation differs')
            cases += 1
        if first_probe is None: first_probe = (row, start, active[1], register)
    row, start, end, register = first_probe
    active[:] = [start, end]
    pointer_base = int(row['pointer_table'], 16); index_base = int(row.get('byte_index_table',row['target']), 16)
    uc.mem_write(start, pe_read(game, start, end - start))
    if 'byte_index_table' in row: uc.mem_write(index_base, bytes(row['indices']))
    uc.mem_write(pointer_base, pe_read(game, pointer_base, (max(row['indices']) + 1) * 4))
    slot = pointer_base + row['indices'][0] * 4; original = pe_read(game, slot, 4)
    uc.mem_write(slot, struct.pack('<I', int(row['targets_by_index'][0], 16) + 1))
    uc.reg_write(register, 0); uc.reg_write(registers.UC_X86_REG_EFLAGS, 2); uc.emu_start(start, 0, count=16)
    require(uc.reg_read(registers.UC_X86_REG_EIP) != int(row['targets_by_index'][0], 16), 'Negative pointer mutation was not detected')
    uc.mem_write(slot, original)
    result.update(simulation_cases=cases, negative_pointer_mutation_rejected=True,
                  simulation_scope='CMP/JA, optional byte-index load, verified intervening stack loads and indirect JMP; stop before target blocks. Every bounded input and two out-of-range values per table checked.')
    (root / 'verification.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    (root / 'manifest.json').write_text(json.dumps(dict(source_sha256=audit['source_sha256'], files={n: digest(root / n) for n in ('report.json', 'verification.json')}), indent=2), encoding='utf-8')
    print(json.dumps(dict(counts=dict(counts), simulation_cases=cases, unbounded=[r['target'] for r in rows if 'bound' not in r])))


if __name__ == '__main__': main()
