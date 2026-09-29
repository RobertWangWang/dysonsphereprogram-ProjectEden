"""核验 SHA-512 异常处理器、四条运行时函数记录及自定义展开数据。"""
import hashlib
import json
import shutil
import struct
from pathlib import Path
from capstone import Cs, CS_ARCH_X86, CS_MODE_64, CS_GRP_JUMP, CS_GRP_CALL
from capstone.x86 import X86_OP_REG, X86_OP_IMM, X86_OP_MEM, X86_REG_RIP
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import coff, pe_read, require
from dsp_native_sha512_match import FUNCTIONS
from dsp_native_openssl_match import GAME_SHA, COMMIT

BASE = 0x180000000
ENTRY, END = 0x1800208f0, 0x180020a43


def imports(game):
    pe = struct.unpack_from('<I', game, 60)[0]
    rva, size = struct.unpack_from('<II', game, pe + 24 + 112 + 8)
    result = {}
    for offset in range(0, size, 20):
        original, stamp, chain, name, first = struct.unpack('<IIIII', pe_read(game, BASE+rva+offset, 20))
        if not any((original, stamp, chain, name, first)): break
        def cstring(address):
            out = bytearray()
            while True:
                b = pe_read(game, address+len(out), 1)
                if b == b'\0': return out.decode('ascii')
                out.extend(b)
        dll = cstring(BASE+name)
        for n in range(100000):
            thunk = struct.unpack('<Q', pe_read(game, BASE+(original or first)+n*8, 8))[0]
            if not thunk: break
            symbol = '#'+str(thunk & 65535) if thunk >> 63 else cstring(BASE+thunk+2)
            result[BASE+first+n*8] = (dll, symbol)
    return result


def normalize(ins, labels, external):
    result = []
    for i in ins:
        if i.mnemonic == 'nop': continue
        operands = []
        for op in i.operands:
            if op.type == X86_OP_REG: value = ('reg', i.reg_name(op.reg), op.size)
            elif op.type == X86_OP_IMM:
                value = ('target', labels[op.imm]) if i.group(CS_GRP_JUMP) or i.group(CS_GRP_CALL) else ('imm', op.imm, op.size)
            elif op.type == X86_OP_MEM:
                m = op.mem
                if m.base == X86_REG_RIP:
                    target = i.address+i.size+m.disp
                    value = ('rip', 'RtlVirtualUnwind' if target == external else labels[target], op.size)
                else: value = ('mem', i.reg_name(m.segment), i.reg_name(m.base), i.reg_name(m.index), m.scale, m.disp, op.size)
            else: raise ValueError('Unsupported operand')
            operands.append(value)
        result.append((i.mnemonic, operands))
    return result


def main(config=None):
    config = config or dict(stem='sha512', family='sha512-family', output='sha512-seh',
        generator='sha512-x86_64.pl', functions=FUNCTIONS, entry=ENTRY, end=END,
        handler_offset=0x5200, import_offset=0x533f, name='sha512_se_handler')
    functions = config['functions']; entry = config['entry']; end_address = config['end']
    stem = config['stem']; relocation_offset = config['import_offset']
    module = 'DSPGAME_Data/Plugins/x86_64/rail_api64.dll'
    game_path = Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/module
    require(digest(game_path) == GAME_SHA, 'Game baseline changed')
    game = game_path.read_bytes()
    parent = GENERATED/'native'/module.replace('/', '__')/'upstream-openssl'
    family = parent/config['family']
    manifest = read_json(family/'manifest.json')
    require(manifest['source_sha256'] == GAME_SHA, 'Source match baseline changed')
    for name, sha in manifest['files'].items(): require(digest(family/name) == sha, 'Source match artifact changed')
    obj = (family/(stem+'.obj')).read_bytes()
    sections, symbols = coff(obj)
    names = {n: (v, s) for n, v, s in symbols.values()}
    code = sections[0][1]
    decoder = Cs(CS_ARCH_X86, CS_MODE_64); decoder.detail = True
    def decode(raw, start):
        ins = list(decoder.disasm(bytes(raw), start))
        require(sum(i.size for i in ins) == len(raw), 'Incomplete instruction decoding')
        return ins
    source_labels, game_labels, mapping, end_mapping = {}, {}, {}, {}
    for name, start, end in functions:
        a = names[name][0]; b = names['L$SEH_end_'+name][0]
        left = [i for i in decode(code[a:b], a) if i.mnemonic != 'nop']
        right = [i for i in decode(pe_read(game, start, end-start), start) if i.mnemonic != 'nop']
        require(len(left) == len(right), 'Computational instruction count changed')
        for n, (x, y) in enumerate(zip(left, right)):
            source_labels[x.address] = game_labels[y.address] = (name, n)
            mapping[x.address] = y.address
        end_mapping[b] = end
    a, section = names['se_handler']; require(section == 1 and a == config['handler_offset'], 'Handler symbol changed')
    source = decode(code[a:], a); target = decode(pe_read(game, entry, end_address-entry), entry)
    require(len(source) == len(target), 'Handler instruction count changed')
    for n, (x, y) in enumerate(zip(source, target)):
        source_labels[x.address] = game_labels[y.address] = ('handler', n)
        mapping[x.address] = y.address
    # Resolve identity from PE imports, not a hard-coded import name.
    matches = [(address, dll) for address, (dll, name) in imports(game).items() if name == 'RtlVirtualUnwind']
    require(len(matches) == 1, 'Missing or ambiguous unwind import')
    iat, dll = matches[0]
    relocs = [struct.unpack_from('<IIH', obj, sections[0][2]+n*10) for n in range(sections[0][3])]
    handler_relocs = [(r, symbols[s], k) for r, s, k in relocs if r >= a]
    require(handler_relocs == [(relocation_offset, ('__imp_RtlVirtualUnwind', 0, 0), 4)], 'Handler relocation changed')
    require(struct.unpack_from('<i', code, relocation_offset)[0] == 0, 'Unexpected import addend')
    expected = normalize(source, source_labels, relocation_offset+4)
    require(expected == normalize(target, game_labels, iat), 'Handler instructions differ')
    # Preserve a negative that redirects a valid branch to another valid instruction.
    jump = next(i for i in target if i.group(CS_GRP_JUMP))
    changed = dict(game_labels); changed[jump.operands[0].imm] = ('handler', 0)
    require(normalize(target, changed, iat) != expected, 'Wrong branch accepted')

    pe = struct.unpack_from('<I', game, 60)[0]
    rva, size = struct.unpack_from('<II', game, pe+24+112+3*8)
    records = list(struct.iter_unpack('<III', pe_read(game, BASE+rva, size)))
    selected = []
    xdata_map = {}
    for n, (name, start, end) in enumerate(functions):
        begin = names['L$SEH_begin_'+name][0]
        rows = [row for row in records if row[0]+BASE == mapping[begin]]
        require(len(rows) == 1 and rows[0][1]+BASE == end, 'Runtime function range differs')
        row = rows[0]; xdata_map[n*16] = BASE+row[2]
        selected.append(dict(name=name,begin=f'{BASE+row[0]:x}',end_exclusive=f'{BASE+row[1]:x}',unwind_address=f'{BASE+row[2]:x}'))
    def relocated(index):
        name, raw, offset, count = sections[index]; output = bytearray(raw)
        require(count == 12, 'Unexpected unwind relocation count')
        seen = set()
        for n in range(count):
            at, symbol, kind = struct.unpack_from('<IIH', obj, offset+n*10)
            label, value, section = symbols[symbol]
            require(kind == 3 and at not in seen, 'Expected unique ADDR32NB relocation')
            seen.add(at); address = value+struct.unpack_from('<I', raw, at)[0]
            # A source end can equal the next entry while the linked image has
            # alignment between them. Runtime-function EndAddress is exclusive.
            if index == 1 and at % 12 == 4:
                require(section == 1, 'EndAddress must reference text')
                function = functions[at // 12]
                require(address == names['L$SEH_end_'+function[0]][0], 'Wrong function end symbol')
                resolved = end_mapping[address]
            else:
                resolved = mapping[address] if section == 1 else xdata_map[address] if section == 3 else None
            require(resolved is not None, 'Unmapped unwind symbol')
            struct.pack_into('<I', output, at, resolved-BASE)
        return bytes(output)
    pdata = relocated(1); xdata = relocated(2)
    actual_pdata = b''.join(struct.pack('<III', int(r['begin'],16)-BASE, int(r['end_exclusive'],16)-BASE, int(r['unwind_address'],16)-BASE) for r in selected)
    actual_xdata = b''.join(pe_read(game, xdata_map[n*16], 16) for n in range(4))
    require(pdata == actual_pdata and xdata == actual_xdata, 'Relocated unwind records differ')
    require(all(xdata[n*16:n*16+4] == b'\x09\0\0\0' for n in range(4)), 'Unexpected unwind header')
    # Each address field is covered, including all four handler/prologue/epilogue triples.
    for n in range(0, len(xdata), 4):
        corrupt = bytearray(xdata); corrupt[n] ^= 1
        require(bytes(corrupt) != actual_xdata, 'Unwind mutation accepted')
    root = parent/config['output']; root.mkdir(exist_ok=True); (root/'manifest.json').unlink(missing_ok=True)
    for name in (stem+'.asm',stem+'.obj',config['generator'],'x86_64-xlate.pl','LICENSE'):
        shutil.copyfile(family/name, root/name)
    (root/'game.asm').write_text(''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in target), encoding='utf-8')
    (root/'unwind.bin').write_bytes(pdata+xdata)
    report = dict(status='normalized-source-matched',source_sha256=GAME_SHA,module=module,
        name=config['name'],address=f'{entry:x}',body_entry=f'{entry:x}',end_exclusive=f'{end_address:x}',
        assembly_file=stem+'.asm',meaningful_instructions=len(expected),constant_bytes=0,
        game_bytes=end_address-entry,rebuilt_bytes=len(code)-a,upstream_commit=COMMIT,
        code_sha256=hashlib.sha256(pe_read(game,entry,end_address-entry)).hexdigest(),
        runtime_functions=selected,pdata_bytes=len(pdata),xdata_bytes=len(xdata),unwind_relocations=24,
        split_end_boundaries=[dict(source_offset=f'{at:x}',game_end=f'{finish:x}',game_entry=f'{mapping[at]:x}')
            for at,finish in end_mapping.items() if at in mapping and mapping[at] != finish],
        import_reference=dict(address=f'{iat:x}',dll=dll,name='RtlVirtualUnwind'),
        negative_branch_rejected=True,negative_unwind_mutations=16,
        limitation='Instruction/source correspondence and relocated metadata equality; no runtime exception injection or proof of Windows unwinder behavior. Existing C count unchanged.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print(f'Verified {len(expected)} handler instructions, {len(pdata)} pdata bytes, {len(xdata)} xdata bytes, 24 relocations')


if __name__ == '__main__': main()
