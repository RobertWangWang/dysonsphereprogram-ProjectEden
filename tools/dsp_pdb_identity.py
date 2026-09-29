"""检查 PDB MSF 信息流与 DLL CodeView RSDS GUID/age 是否完全匹配。"""
import argparse
import json
from pathlib import Path
import struct
import uuid
from dsp_knowledge import digest


def pdb_identity(path):
    data = Path(path).read_bytes()
    if not data.startswith(b'Microsoft C/C++ MSF 7.00'):
        raise ValueError('仅支持 MSF 7 PDB')
    page, _, _, size, _, blockmap = struct.unpack_from('<6I', data, 32)
    if page not in (512, 1024, 2048, 4096, 8192, 16384, 32768):
        raise ValueError('异常 PDB 页大小')
    blocks = struct.unpack_from('<' + str((size + page - 1) // page) + 'I', data, blockmap * page)
    directory = b''.join(data[v*page:(v+1)*page] for v in blocks)[:size]
    count = struct.unpack_from('<I', directory)[0]
    sizes = struct.unpack_from('<' + str(count) + 'I', directory, 4)
    offset = 4 + count * 4
    for index, length in enumerate(sizes):
        pages = 0 if length == 0xffffffff else (length + page - 1) // page
        blocks = struct.unpack_from('<' + str(pages) + 'I', directory, offset)
        offset += pages * 4
        if index == 1:
            info = b''.join(data[v*page:(v+1)*page] for v in blocks)[:length]
            return {'guid': str(uuid.UUID(bytes_le=info[12:28])).upper(), 'age': struct.unpack_from('<I', info, 8)[0]}
    raise ValueError('缺少 PDB 信息流')


def pe_identity(path):
    data = Path(path).read_bytes()
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    if data[:2] != b'MZ' or data[pe:pe+4] != b'PE\0\0':
        raise ValueError('不是 PE 文件')
    section_count = struct.unpack_from('<H', data, pe + 6)[0]
    optional_size = struct.unpack_from('<H', data, pe + 20)[0]
    optional = pe + 24
    magic = struct.unpack_from('<H', data, optional)[0]
    directory = optional + (112 if magic == 0x20b else 96)
    debug_rva, debug_size = struct.unpack_from('<II', data, directory + 6 * 8)
    for i in range(section_count):
        section = optional + optional_size + i * 40
        virtual_size, rva, raw_size, raw = struct.unpack_from('<4I', data, section + 8)
        if rva <= debug_rva < rva + max(virtual_size, raw_size):
            start = raw + debug_rva - rva
            for entry in range(start, start + debug_size, 28):
                kind, size, _, pointer = struct.unpack_from('<4I', data, entry + 12)
                if kind == 2 and data[pointer:pointer+4] == b'RSDS':
                    info = data[pointer:pointer+size]
                    return {'guid': str(uuid.UUID(bytes_le=info[4:20])).upper(), 'age': struct.unpack_from('<I', info, 20)[0],
                            'original_path': info[24:].split(b'\0')[0].decode('utf-8', errors='replace')}
    raise ValueError('没有找到 RSDS 调试目录')


def verify_pdb(pdb, binary):
    actual, expected = pdb_identity(pdb), pe_identity(binary)
    if any(actual[k] != expected[k] for k in ('guid', 'age')):
        raise ValueError(f'PDB 不匹配：{actual} != {expected}')
    return {'pdb': actual, 'binary': expected, 'pdb_sha256': digest(pdb), 'binary_sha256': digest(binary)}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('pdb', type=Path)
    parser.add_argument('binary', type=Path)
    args = parser.parse_args()
    print(json.dumps(verify_pdb(args.pdb, args.binary), ensure_ascii=False, indent=2))
