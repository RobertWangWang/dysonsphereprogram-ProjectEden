"""导出 Unity shader/compute 字节码及 D3D 指令；不将反汇编冒充原始 HLSL。"""
import argparse
import collections
import ctypes
import json
from pathlib import Path
import struct
import UnityPy
from UnityPy.helpers.CompressionHelper import decompress_lz4
from dsp_knowledge import KB, GENERATED, paths, digest


def disassemble(code):
    dll = ctypes.WinDLL('d3dcompiler_47.dll', winmode=0x800)
    function = dll.D3DDisassemble
    function.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint, ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)]
    function.restype = ctypes.c_long
    blob = ctypes.c_void_p()
    data = ctypes.create_string_buffer(code)
    result = function(data, len(code), 0, None, ctypes.byref(blob))
    if result < 0:
        raise ValueError(f'D3DDisassemble HRESULT={result & 0xffffffff:08x}')
    table = ctypes.cast(blob, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    pointer = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(table[3])
    size = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(table[4])
    release = ctypes.WINFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(table[2])
    try:
        return ctypes.string_at(pointer(blob), size(blob)).rstrip(b'\0').decode('utf-8')
    finally:
        release(blob)


def flatten(value):
    if isinstance(value, list):
        for item in value:
            yield from flatten(item)
    else:
        yield value


def dxbc_chunks(data):
    cursor = 0
    while True:
        offset = data.find(b'DXBC', cursor)
        if offset < 0:
            return
        if offset + 32 <= len(data):
            length = struct.unpack_from('<I', data, offset + 24)[0]
            if length >= 32 and offset + length <= len(data):
                yield offset, data[offset:offset+length]
                cursor = offset + length
                continue
        cursor = offset + 4


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--managed')
    parser.add_argument('--bepinex')
    args = parser.parse_args()
    managed, _ = paths(args)
    data_dir = managed.parent
    output = GENERATED / 'shaders'
    output.mkdir(exist_ok=True)
    marker = output / 'manifest.json'
    marker.unlink(missing_ok=True)
    inputs = [p for p in data_dir.iterdir() if p.is_file() and (p.suffix == '.assets' or p.name in ('globalgamemanagers', 'level0'))]
    inputs.extend(p for p in (data_dir / 'Resources').glob('*') if p.is_file() and p.name in ('unity default resources', 'unity_builtin_extra'))
    records, sources = [], {}
    for source in sorted(inputs):
        source_key = source.relative_to(data_dir).as_posix()
        sources[source_key] = digest(source)
        env = UnityPy.load(str(source))
        counts = collections.Counter(o.type.name for o in env.objects)
        print(source_key + ' ' + str({k: counts[k] for k in ('Shader', 'ComputeShader', 'TextAsset', 'MonoScript')}), flush=True)
        for obj in env.objects:
            if obj.type.name not in ('Shader', 'ComputeShader', 'TextAsset', 'MonoScript'):
                continue
            folder = output / source_key / str(obj.path_id)
            folder.mkdir(parents=True, exist_ok=True)
            record = {'source': source_key, 'path_id': obj.path_id, 'type': obj.type.name, 'programs': [], 'errors': []}
            raw = folder / 'serialized.bin'
            raw.write_bytes(obj.get_raw_data())
            record['raw'] = raw.relative_to(output).as_posix()
            try:
                tree = obj.read_typetree()
                record['name'] = tree.get('m_Name') or tree.get('m_ParsedForm', {}).get('m_Name', '')
                (folder / 'metadata.json').write_text(json.dumps(tree, ensure_ascii=True, default=lambda v: {'hex': bytes(v).hex()}), encoding='utf-8')
                if obj.type.name == 'TextAsset':
                    # Typetree string decoding is not lossless for binary TextAssets.
                    # Preserve the exact serialized m_Script bytes independently of JSON.
                    serialized = raw.read_bytes()
                    name_length = struct.unpack_from('<i', serialized, 0)[0]
                    offset = (4 + name_length + 3) & ~3
                    length = struct.unpack_from('<i', serialized, offset)[0]
                    if name_length < 0 or length < 0 or offset + 4 + length > len(serialized):
                        raise ValueError('Unexpected TextAsset serialization layout')
                    payload = folder / 'payload.bin'
                    payload.write_bytes(serialized[offset+4:offset+4+length])
                    record['payload'] = payload.relative_to(output).as_posix()
                blobs = []
                if obj.type.name == 'ComputeShader':
                    for vi, variant in enumerate(tree['variants']):
                        for ki, kernel in enumerate(variant['kernels']):
                            for ui, unique in enumerate(kernel['uniqueVariants']):
                                blobs.append((f'v{vi}-k{ki}-u{ui}', bytes(unique['code']), kernel['name'], unique['threadGroupSize']))
                elif obj.type.name == 'Shader':
                    compressed = bytes(tree.get('compressedBlob', []))
                    spans = zip(flatten(tree.get('offsets', [])), flatten(tree.get('compressedLengths', [])), flatten(tree.get('decompressedLengths', [])))
                    for i, (offset, length, size) in enumerate(spans):
                        blobs.append((f'blob{i}', decompress_lz4(compressed[offset:offset+length], size), '', []))
                for label, blob, kernel, threads in blobs:
                    (folder / (label + '.bin')).write_bytes(blob)
                    chunks = list(dxbc_chunks(blob))
                    if not chunks:
                        record['errors'].append(label + ': no DXBC found; raw blob retained')
                    for number, (offset, code) in enumerate(chunks):
                        name = label + '-' + str(number)
                        binary = folder / (name + '.dxbc')
                        binary.write_bytes(code)
                        program = {'file': binary.relative_to(output).as_posix(), 'kernel': kernel, 'threads': threads, 'blob_offset': offset}
                        try:
                            asm = folder / (name + '.asm')
                            asm.write_text(disassemble(code), encoding='utf-8')
                            program['assembly'] = asm.relative_to(output).as_posix()
                        except Exception as exc:
                            program['error'] = str(exc)
                            record['errors'].append(name + ': ' + str(exc))
                        record['programs'].append(program)
            except Exception as exc:
                record['errors'].append(str(exc))
            records.append(record)
        if digest(source) != sources[source_key]:
            raise ValueError('导出期间资源改变：' + source.name)
    report = {'sources': sources, 'unitypy': UnityPy.__version__, 'objects': records}
    (output / 'index.json').write_text(json.dumps(report, ensure_ascii=True, indent=2), encoding='utf-8')
    manifest = {'sources': sources, 'files': {p.relative_to(output).as_posix(): digest(p) for p in sorted(output.rglob('*')) if p.is_file() and p != marker}}
    marker.write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print(f"完成：{len(records)} 个目标对象，{sum(len(r['programs']) for r in records)} 个 DXBC，{sum(bool(r['errors']) for r in records)} 个对象有待处理问题。")


if __name__ == '__main__':
    main()
