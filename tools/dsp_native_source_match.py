"""重汇编 libvpx v1.3.0 并逐字节核验 UnityPlayer 的 VP8 滤波函数；不执行游戏代码。"""
import argparse
import hashlib
import json
import shutil
import struct
import subprocess
from pathlib import Path

from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_select import selected_folder

COMMIT = '2e88f2f2ec777259bda1714e72f1ecd2519bceb5'
SOURCE_HASHES = {
    'loopfilter_block_sse2.asm': '3d8149542f60b20ea4a5abcfcd0d5e2493ba65edc02147fa28ddcace3c948e01',
    'vpx_ports/x86_abi_support.asm': '697d1d777c3f77ab6784a3497702b67f7c5591a99ab77d86fc3ff8b78660f777',
}
GAME_SHA = 'b07814a510eea3f8b66a25117ab602237037d74668e6ce1f8de92f60c0ef28c4'
ENTRY, END, CONSTANTS = 0x1800145ca, 0x180015301, 0x181e65b00
FUNCTION = 'vp8_loop_filter_bv_y_sse2'


def require(condition, message):
    if not condition:
        raise ValueError(message)


def coff(data, expected_machine=0x8664):
    machine, count, _, symbol_offset, symbol_count, optional, _ = struct.unpack_from('<HHIIIHH', data)
    require(machine == expected_machine and optional == 0, 'Unexpected COFF machine or optional header')
    sections = []
    for i in range(count):
        header = 20 + i * 40
        name = data[header:header + 8].rstrip(b'\0').decode()
        size, offset, relocations, _, relocation_count = struct.unpack_from('<IIIIH', data, header + 16)
        sections.append((name, bytearray(data[offset:offset + size]), relocations, relocation_count))
    strings = symbol_offset + symbol_count * 18
    symbols, index = {}, 0
    while index < symbol_count:
        offset = symbol_offset + index * 18
        name = data[offset:offset + 8]
        if name[:4] == b'\0' * 4:
            start = strings + struct.unpack_from('<I', name, 4)[0]
            name = data[start:data.index(b'\0', start)]
        value, section = struct.unpack_from('<Ih', data, offset + 8)
        symbols[index] = (name.rstrip(b'\0').decode(), value, section)
        index += 1 + data[offset + 17]
    return sections, symbols


def pe_read(data, address, size):
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    require(data[pe:pe + 4] == b'PE\0\0', 'Invalid PE')
    count = struct.unpack_from('<H', data, pe + 6)[0]
    optional = struct.unpack_from('<H', data, pe + 20)[0]
    magic = struct.unpack_from('<H', data, pe + 24)[0]
    require(magic in (0x10b, 0x20b), 'Unexpected PE optional-header magic')
    image_base = struct.unpack_from('<Q' if magic == 0x20b else '<I', data, pe + 24 + (24 if magic == 0x20b else 28))[0]
    rva = address - image_base
    for i in range(count):
        header = pe + 24 + optional + 40 * i
        _, section_rva, raw_size, raw_offset = struct.unpack_from('<IIII', data, header + 8)
        if section_rva <= rva and rva + size <= section_rva + raw_size:
            return data[raw_offset + rva - section_rva:raw_offset + rva - section_rva + size]
    raise ValueError('Address outside initialized PE data')


def verify_object(game, obj):
    sections, symbols = coff(obj)
    matches = [s for s in symbols.values() if s[0] == FUNCTION]
    require(len(matches) == 1, 'Missing/ambiguous function symbol')
    _, start, section = matches[0]
    require(section > 0, 'Undefined function symbol')
    name, code, relocation_offset, relocation_count = sections[section - 1]
    require(name == '.text' and len(code) - start == END - ENTRY, 'Function extent mismatch')
    code_base = ENTRY - start
    count = 0
    for i in range(relocation_count):
        offset, symbol, kind = struct.unpack_from('<IIH', obj, relocation_offset + 10 * i)
        _, value, target_section = symbols[symbol]
        require(kind == 4 and target_section > 0 and sections[target_section - 1][0] == '.rodata', 'Unexpected relocation')
        addend = struct.unpack_from('<i', code, offset)[0]
        require(0 <= value + addend <= 112, 'Constant outside 128-byte table')
        struct.pack_into('<i', code, offset, CONSTANTS + value + addend - (code_base + offset + 4))
        count += offset >= start
    actual = pe_read(game, ENTRY, END - ENTRY)
    require(bytes(code[start:]) == actual, 'Code bytes differ after REL32 relocation')
    constants = [s[1] for s in sections if s[0] == '.rodata']
    require(len(constants) == 1 and len(constants[0]) == 128, 'Constant extent mismatch')
    require(bytes(constants[0]) == pe_read(game, CONSTANTS, 128), 'Constant bytes differ')
    return dict(code_bytes=len(actual), constant_bytes=128, relocation_count=count,
                code_sha256=hashlib.sha256(actual).hexdigest(),
                constants_sha256=hashlib.sha256(constants[0]).hexdigest())


def validate_cached(folder, game_path):
    path = folder / 'source-match/report.json'
    if not path.exists():
        return {}
    report = read_json(path)
    require(report['source_sha256'] == digest(game_path) == GAME_SHA, 'Source match baseline mismatch')
    require(report['status'] == 'source-matched' and report['address'] == f'{ENTRY:x}'
            and report['name'] == FUNCTION, 'Unexpected source match identity')
    for name, expected in report['files'].items():
        require(digest(path.parent / name) == expected, 'Source artifact hash mismatch: ' + name)
    reference = path.parent / 'libvpx-v1.3.0'
    for name, expected in SOURCE_HASHES.items():
        require(digest(reference / name) == expected, 'Upstream source baseline mismatch')
    require(verify_object(game_path.read_bytes(), (reference / 'loopfilter.obj').read_bytes())
            == report['proof'], 'Source match proof mismatch')
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--nasm', type=Path, required=True)
    parser.add_argument('--reference', type=Path, required=True,
                        help='固定提交的上游文件；包含 LICENSE/PATENTS/AUTHORS')
    args = parser.parse_args()
    base = GENERATED / 'native'
    inventory = read_json(base / 'inventory.json')
    module = next(m for m in inventory['files'] if m['path'] == 'UnityPlayer.dll')
    game_path = Path(inventory['game_directory']) / module['path']
    require(digest(game_path) == module['sha256'] == GAME_SHA, 'Game baseline changed')
    for name, expected in SOURCE_HASHES.items():
        require(digest(args.reference / name) == expected, 'Upstream source hash mismatch: ' + name)
    folder = selected_folder(base, module)
    output = folder / 'source-match'
    output.mkdir(exist_ok=True)
    report_path = output / 'report.json'
    # Remove stale success before any operation that can fail.
    report_path.unlink(missing_ok=True)
    reference = output / 'libvpx-v1.3.0'
    for name in [*SOURCE_HASHES, 'LICENSE', 'PATENTS', 'AUTHORS', 'loopfilter_filters.c']:
        target = reference / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(args.reference / name, target)
    (reference / 'vpx_config.asm').write_text('%define CONFIG_PIC 0\n', encoding='ascii')
    # Explicit initial section is required for NASM's symbols, absent in the old YASM source.
    (reference / 'section.asm').write_text('section .text\n', encoding='ascii')
    command = [str(args.nasm.resolve()), '-f', 'win64', '-p', 'section.asm', '-I', './',
               'loopfilter_block_sse2.asm', '-o', 'loopfilter.obj']
    result = subprocess.run(command, cwd=reference, capture_output=True, text=True, check=True)
    game = game_path.read_bytes()
    proof = verify_object(game, (reference / 'loopfilter.obj').read_bytes())
    require(digest(game_path) == GAME_SHA, 'Game changed during verification')
    files = {p.relative_to(output).as_posix(): digest(p) for p in reference.rglob('*') if p.is_file()}
    report = dict(status='source-matched', source_sha256=GAME_SHA, address=f'{ENTRY:x}',
                  name=FUNCTION, file='libvpx-v1.3.0/loopfilter_block_sse2.asm',
                  signature='void vp8_loop_filter_bv_y_sse2(unsigned char *src_ptr, int src_pixel_step, const char *blimit, const char *limit, const char *thresh)',
                  source_line=279, upstream_commit=COMMIT,
                  upstream_url=f'https://github.com/webmproject/libvpx/blob/{COMMIT}/vp8/common/x86/loopfilter_block_sse2.asm',
                  mode='source-matched-assembly', proof=proof, files=files,
                  nasm_sha256=digest(args.nasm),
                  nasm_version=subprocess.check_output([str(args.nasm.resolve()), '-v'], text=True).strip(),
                  command=command, assembler_output=result.stdout + result.stderr,
                  limitation='Exact relocated machine code and constants; scalar C companion is explanatory, not separately proven equivalent.')
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(proof), report_path)


if __name__ == '__main__':
    main()
