"""按序列化子程序索引解析 Unity 2022 图形程序块，导出 GLSL/Metal 与 Vulkan 原始载荷。"""
from collections import Counter
import argparse
import json
from pathlib import Path
import struct
import subprocess
from urllib.parse import quote
from dsp_knowledge import KB, GENERATED, digest, read_json

# Unity ShaderCompilerPlatform -> ShaderGpuProgramType. Only non-D3D targets here.
PLATFORMS = {5: {5}, 9: {3, 4}, 14: {23, 24}, 15: {6, 7, 8}, 18: {25}}


def references(tree):
    if isinstance(tree, dict):
        if 'm_BlobIndex' in tree and 'm_GpuProgramType' in tree:
            yield tree['m_BlobIndex'], tree['m_GpuProgramType']
        for value in tree.values():
            yield from references(value)
    elif isinstance(tree, list):
        for value in tree:
            yield from references(value)


def program_data(data):
    """Read program-only entry; never interpret parameter entries as shader code."""
    position = 0

    def integer():
        nonlocal position
        if position + 4 > len(data):
            raise ValueError('Truncated entry')
        value = struct.unpack_from('<I', data, position)[0]
        position += 4
        return value

    def array():
        nonlocal position
        length = integer()
        end = position + length
        if end > len(data):
            raise ValueError('Array outside entry')
        result = data[position:end]
        position = (end + 3) & ~3
        return result

    if integer() != 202012090:
        raise ValueError('Unsupported program version')
    kind = integer()
    stats = [integer() for _ in range(4)]
    count = integer()
    if count > len(data) // 4:
        raise ValueError('Invalid keyword count')
    keywords = [array().decode('utf-8') for _ in range(count)]
    code = array()
    source_map = integer()
    count = integer()
    if count > len(data) // 8:
        raise ValueError('Invalid channel count')
    channels = [(integer(), integer()) for _ in range(count)]
    if position != len(data):
        raise ValueError(f'Entry not fully consumed: {position}/{len(data)}')
    return code, dict(program_type=kind, stats=stats, keywords=keywords,
                      source_map=source_map, channels=channels)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--smolv-decoder', type=Path)
    parser.add_argument('--spirv-dis', type=Path)
    args = parser.parse_args()
    if bool(args.smolv_decoder) != bool(args.spirv_dis):
        parser.error('--smolv-decoder and --spirv-dis must be supplied together')
    base = GENERATED / 'shaders'
    index = read_json(base / 'index.json')
    baseline = read_json(base / 'manifest.json')
    output = GENERATED / 'graphics-platforms'
    output.mkdir(exist_ok=True)
    marker = output / 'manifest.json'
    marker.unlink(missing_ok=True)
    records = []
    for obj in index['objects']:
        if obj['type'] != 'Shader':
            continue
        folder = base / obj['source'] / str(obj['path_id'])
        metadata = folder / 'metadata.json'
        tree = read_json(metadata)
        if not any(p in PLATFORMS for p in tree.get('platforms', [])):
            continue
        if digest(metadata) != baseline['files'][metadata.relative_to(base).as_posix()]:
            raise ValueError('Metadata hash mismatch')
        refs = sorted(set(references(tree['m_ParsedForm'])))
        blob_index = 0
        for pi, platform in enumerate(tree['platforms']):
            segments = []
            for unused in tree['offsets'][pi]:
                path = folder / f'blob{blob_index}.bin'
                if digest(path) != baseline['files'][path.relative_to(base).as_posix()]:
                    raise ValueError('Blob hash mismatch')
                segments.append(path.read_bytes())
                blob_index += 1
            if platform not in PLATFORMS:
                continue
            count = struct.unpack_from('<I', segments[0])[0]
            if 4 + count * 12 > len(segments[0]):
                raise ValueError('Invalid entry table')
            for entry, kind in refs:
                if kind not in PLATFORMS[platform]:
                    continue
                if entry >= count:
                    raise ValueError('Reference outside entry table')
                offset, length, segment = struct.unpack_from('<III', segments[0], 4 + entry * 12)
                if segment >= len(segments) or offset + length > len(segments[segment]):
                    raise ValueError('Entry outside segment')
                code, details = program_data(segments[segment][offset:offset+length])
                if details['program_type'] != kind:
                    raise ValueError('Program type disagrees with serialized reference')
                dest = output / obj['source'] / str(obj['path_id'])
                dest.mkdir(parents=True, exist_ok=True)
                stem = f'p{platform}-e{entry}'
                raw = dest / (stem + '.bin')
                raw.write_bytes(code)
                record = dict(source=obj['source'], path_id=obj['path_id'], name=obj['name'],
                              platform=platform, entry=entry, segment=segment, offset=offset,
                              raw=raw.relative_to(output).as_posix(), **details)
                if platform in (5, 9, 15):
                    code.rstrip(b'\0').decode('utf-8', errors='strict')
                    if b'#version' not in code:
                        raise ValueError('GLSL version directive missing')
                    text = dest / (stem + '.glsl')
                    text.write_bytes(code)
                    record.update(format='GLSL', file=text.relative_to(output).as_posix())
                elif platform == 14:
                    magic, start = struct.unpack_from('<II', code)
                    if magic != 0xf00dcafe or start >= len(code):
                        raise ValueError('Unsupported Metal header')
                    end = code.index(b'\0', start)
                    record['entry_point'] = code[start:end].decode('utf-8')
                    source = code[end+1:]
                    source.rstrip(b'\0').decode('utf-8', errors='strict')
                    if b'metal' not in source:
                        raise ValueError('Metal source marker missing')
                    text = dest / (stem + '.metal')
                    text.write_bytes(source)
                    record.update(format='Metal', file=text.relative_to(output).as_posix())
                else:
                    if not args.smolv_decoder:
                        record.update(format='Vulkan-payload', pending='SMOL-V snippets require decoding')
                    else:
                        snippets = []
                        spans = []
                        for slot in range(6):
                            start, size = struct.unpack_from('<II', code, 4 + slot * 8)
                            if size == 0:
                                if start != 0:
                                    raise ValueError('Nonzero offset for empty snippet')
                                continue
                            if start < 52 or start + size > len(code) or code[start:start+4] != b'LOMS':
                                raise ValueError('Invalid SMOL-V snippet')
                            if any(start < end and start + size > begin for begin, end in spans):
                                raise ValueError('Overlapping snippets')
                            spans.append((start, start + size))
                            packed = dest / f'{stem}-s{slot}.smolv'
                            packed.write_bytes(code[start:start+size])
                            spv = dest / f'{stem}-s{slot}.spv'
                            asm = dest / f'{stem}-s{slot}.spvasm'
                            subprocess.run([str(args.smolv_decoder), str(packed), str(spv)], check=True)
                            subprocess.run([str(args.spirv_dis), str(spv), '-o', str(asm)], check=True)
                            snippets.append(dict(slot=slot, offset=start, size=size,
                                                 binary=spv.relative_to(output).as_posix(),
                                                 assembly=asm.relative_to(output).as_posix()))
                        if not snippets:
                            raise ValueError('Vulkan program has no snippets')
                        record.update(format='SPIR-V', snippets=snippets, file=snippets[0]['assembly'])
                records.append(record)
    report = dict(shader_manifest_sha256=digest(base / 'manifest.json'), programs=records)
    if args.smolv_decoder:
        report['tools'] = dict(smolv_decoder_sha256=digest(args.smolv_decoder), spirv_dis_sha256=digest(args.spirv_dis))
    (output / 'index.json').write_text(json.dumps(report, ensure_ascii=True, indent=2), encoding='utf-8')
    marker.write_text(json.dumps(dict(files={p.relative_to(output).as_posix(): digest(p) for p in output.rglob('*') if p.is_file() and p != marker}), indent=2), encoding='utf-8')
    counts = Counter(r['format'] for r in records)
    body = '# 图形 shader 的其他平台程序\n\n'
    snippet_count = sum(len(r.get('snippets', [])) for r in records)
    body += f"按元数据引用恢复 {counts['GLSL']} 段 GLSL、{counts['Metal']} 段 Metal 文本；{counts['SPIR-V']} 段 Vulkan 载荷已解码为 {snippet_count} 个 SPIR-V 阶段程序；仍待解码载荷 {counts['Vulkan-payload']} 段。\n\n"
    body += '每个程序核验索引、片段边界、202012090 版本、程序类型、关键词与输入通道，并严格消费整个子程序条目。参数条目与程序条目分开；此工具不声称解析了全部参数布局。实现参考 [AssetRipper 程序布局](https://github.com/AssetRipper/AssetRipper/blob/612d389/Source/AssetRipper.Export.Modules.Shader/ShaderBlob/ShaderSubProgram.cs) 与 [Metal 导出器](https://github.com/AssetRipper/ShaderRecoveryPlugin/blob/da11bd56bc4b149f63ee6c55520c328489d07055/ShaderTextRestorer/Exporters/ShaderMetalExporter.cs)。\n\n'
    body += '运行：`python -X utf8 tools/dsp_graphics_platforms.py --smolv-decoder 路径/dsp_smolv_decode.exe --spirv-dis 路径/spirv-dis.exe`。解码器以本仓库 tools/dsp_smolv_decode.cpp 与 [smol-v](https://github.com/aras-p/smol-v/tree/55000efe742f56d8b51223b9ea7775a8f0501881) 构建；每个解码结果重新编码再解码，验证 SPIR-V 字节一致，之后由 Khronos spirv-dis 读取。依赖基础 shader 导出；源元数据、程序块和输出哈希均记录在清单。每个 Vulkan 载荷的全部阶段文件见 index.json 的 snippets；下表链接该载荷的首个阶段。\n\n'
    body += '| Shader | 平台 | 条目 | 格式 | 结果 |\n|---|---:|---:|---|---|\n'
    for r in records:
        link = quote('generated/graphics-platforms/' + r.get('file', r['raw']))
        body += f"| {r['name']} | {r['platform']} | {r['entry']} | {r['format']} | [查看]({link}) |\n"
    (KB / 'graphics-platforms.md').write_text(body, encoding='utf-8')
    print(dict(counts))


if __name__ == '__main__':
    main()
