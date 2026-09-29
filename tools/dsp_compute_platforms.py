"""提取内置 compute 的 GLSL / SPIR-V；保留独立清单，不掩盖图形 shader 待解析项。"""
import argparse
from collections import Counter
import json
from pathlib import Path
import re
import struct
import subprocess
from urllib.parse import quote
from dsp_knowledge import KB, GENERATED, digest, read_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--spirv-dis', required=True, type=Path)
    args = parser.parse_args()
    base = GENERATED / 'shaders'
    baseline = read_json(base / 'manifest.json')
    index = read_json(base / 'index.json')
    output = GENERATED / 'compute-platforms'
    output.mkdir(exist_ok=True)
    marker = output / 'manifest.json'
    marker.unlink(missing_ok=True)
    records = []
    for obj in index['objects']:
        if obj['type'] != 'ComputeShader':
            continue
        folder = base / obj['source'] / str(obj['path_id'])
        metadata = folder / 'metadata.json'
        relative = metadata.relative_to(base).as_posix()
        if digest(metadata) != baseline['files'][relative]:
            raise ValueError('Metadata hash mismatch: ' + relative)
        tree = read_json(metadata)
        for vi, variant in enumerate(tree['variants']):
            for ki, kernel in enumerate(variant['kernels']):
                for ui, unique in enumerate(kernel['uniqueVariants']):
                    code = bytes(unique['code'])
                    if code.startswith(b'DXBC'):
                        continue
                    dest = output / obj['source'] / str(obj['path_id'])
                    dest.mkdir(parents=True, exist_ok=True)
                    stem = f'v{vi}-k{ki}-u{ui}'
                    record = dict(source=obj['source'], path_id=obj['path_id'], name=obj['name'],
                                  kernel=kernel['name'], renderer=variant['targetRenderer'],
                                  threads=unique['threadGroupSize'], metadata_sha256=digest(metadata))
                    if code.startswith(b'#version'):
                        text = code.rstrip(b'\0').decode('utf-8', errors='strict')
                        dimensions = [int(re.search(r'local_size_' + axis + r'\s*=\s*(\d+)', text)[1]) for axis in 'xyz']
                        if dimensions != record['threads']:
                            raise ValueError('GLSL thread group mismatch')
                        path = dest / (stem + '.glsl')
                        path.write_bytes(code)
                        record.update(format='GLSL', file=path.relative_to(output).as_posix())
                    elif code.startswith(b'\x03\x02\x23\x07'):
                        if len(code) < 20 or len(code) % 4 or struct.unpack_from('<I', code, 16)[0] != 0:
                            raise ValueError('Invalid SPIR-V header')
                        path = dest / (stem + '.spv')
                        path.write_bytes(code)
                        asm = dest / (stem + '.spvasm')
                        subprocess.run([str(args.spirv_dis), str(path), '-o', str(asm)], check=True)
                        text = asm.read_text(encoding='utf-8')
                        dimensions = re.search(r'OpExecutionMode\s+\S+\s+LocalSize\s+(\d+)\s+(\d+)\s+(\d+)', text)
                        if not dimensions or list(map(int, dimensions.groups())) != record['threads']:
                            raise ValueError('SPIR-V thread group mismatch')
                        record.update(format='SPIR-V', file=path.relative_to(output).as_posix(), assembly=asm.relative_to(output).as_posix())
                    else:
                        raise ValueError(f'Unknown compute format: {obj["name"]} {stem}')
                    records.append(record)
    report = dict(shader_manifest_sha256=digest(base / 'manifest.json'),
                  disassembler_sha256=digest(args.spirv_dis), programs=records)
    (output / 'index.json').write_text(json.dumps(report, ensure_ascii=True, indent=2), encoding='utf-8')
    manifest = dict(files={p.relative_to(output).as_posix(): digest(p) for p in output.rglob('*') if p.is_file() and p != marker})
    marker.write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    counts = Counter(r['format'] for r in records)
    body = '# 非 DXBC compute 程序\n\n'
    body += f"已提取 {counts['GLSL']} 段 GLSL、{counts['SPIR-V']} 段 SPIR-V；全部线程组与 ComputeShader 元数据一致。GLSL 保留资源原始字节，SPIR-V 输出二进制与可读指令，不声称恢复原始 HLSL。\n\n"
    body += '使用 [Khronos SPIRV-Tools v2026.3](https://github.com/KhronosGroup/SPIRV-Tools/tree/v2026.3) 构建的 spirv-dis，工具提交 b707790a898e44038547df54580022fc1cf89c3d，SPIRV-Headers 提交 29981f65241605e08b0ede4cfeb999fe3b723c6a。具体工具与输出哈希见 generated/compute-platforms/index.json 和 manifest.json。\n\n'
    body += '重建：`python -X utf8 tools/dsp_compute_platforms.py --spirv-dis 路径/spirv-dis.exe`。依赖基础 shader 导出；独立补充清单不修改基础导出问题记录，图形 shader 的其他平台程序仍需单独解析。\n\n'
    body += '| Shader | Kernel | 平台编号 | 格式 | 线程组 | 结果 |\n|---|---|---:|---|---|---|\n'
    for r in records:
        link = quote('generated/compute-platforms/' + r.get('assembly', r['file']))
        body += f"| {r['name']} | {r['kernel']} | {r['renderer']} | {r['format']} | {' × '.join(map(str, r['threads']))} | [查看]({link}) |\n"
    (KB / 'compute-platforms.md').write_text(body, encoding='utf-8')
    print(f'PASS: {len(records)} non-DXBC compute programs; all thread groups match metadata.')


if __name__ == '__main__':
    main()
