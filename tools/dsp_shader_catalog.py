"""校验 shader 导出、线程组和源码资产哈希，生成中文查询目录。"""
import argparse
import collections
import re
import struct
from urllib.parse import quote
from dsp_knowledge import KB, GENERATED, digest, paths, read_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--managed')
    parser.add_argument('--bepinex')
    parser.add_argument('--allow-pending', action='store_true', help='生成明确列出未解析项的阶段目录，不宣称全部完成')
    args = parser.parse_args()
    managed, _ = paths(args)
    base = GENERATED / 'shaders'
    manifest = read_json(base / 'manifest.json')
    for name, sha in manifest['sources'].items():
        if digest(managed.parent / name) != sha:
            raise ValueError('资源文件已变化：' + name)
    for name, sha in manifest['files'].items():
        if digest(base / name) != sha:
            raise ValueError('导出文件已变化：' + name)
    index = read_json(base / 'index.json')
    counts = collections.Counter(r['type'] for r in index['objects'])
    errors = [r for r in index['objects'] if r['errors']]
    if errors and not args.allow_pending:
        raise ValueError(f'{len(errors)} 个对象仍有导出错误')
    programs = 0
    for record in index['objects']:
        for program in record['programs']:
            code = (base / program['file']).read_bytes()
            if code[:4] != b'DXBC' or struct.unpack_from('<I', code, 24)[0] != len(code):
                raise ValueError('DXBC 长度错误：' + program['file'])
            assembly = (base / program['assembly']).read_text(encoding='utf-8')
            if record['type'] == 'ComputeShader':
                match = re.search(r'dcl_thread_group (\d+), (\d+), (\d+)', assembly)
                if not match or list(map(int, match.groups())) != program['threads']:
                    raise ValueError('compute 元数据与字节码线程组不一致：' + program['file'])
            programs += 1
    body = '# Shader 与 compute 全量目录\n\n'
    body += f"当前 {len(index['sources'])} 个 Unity 序列化文件（含 Resources 内置资产）中提取 {counts['Shader']} 个 Shader、{counts['ComputeShader']} 个 ComputeShader、{counts['MonoScript']} 个 MonoScript 记录及 {counts['TextAsset']} 个 TextAsset，成功反汇编 **{programs:,} 个 DXBC 程序**。核验资产/输出哈希、DXBC 长度、全部 compute 线程组与字节码声明一致。\n\n"
    body += '来源版本为本机资产，哈希在 generated/shaders/manifest.json。工具为 UnityPy ' + index['unitypy'] + ' 与 Windows 系统 D3DDisassemble。asm 是 GPU 指令，metadata.json 是资源结构；均不声称恢复了原始 HLSL。TextAsset 的原始 payload.bin 单独保存，避免二进制数据经过文本解码失真。\n\n'
    if errors:
        body += f'**基础导出范围：{len(errors)} 个对象包含非 DXBC 程序块，此目录只统计 DXBC。** 其他平台的补充解析见 [非 DXBC compute](compute-platforms.md) 和 [图形程序](graphics-platforms.md)；下表保留基础导出发现的全部提示，是否已解析以补充清单为准。\n\n'
        body += '| 对象 | 基础导出待处理项 |\n|---|---|\n'
        for record in errors:
            body += f"| {record['source']} / {record['path_id']} {record['name']} | {'; '.join(record['errors'])} |\n"
        body += '\n'
    body += '重建：用安装 UnityPy 的 Python 执行 `tools/dsp_shader_export.py`，再执行 `tools/dsp_shader_catalog.py`。原始资源、字节码、指令和详细元数据均保留在被 Git 忽略的 generated/shaders。\n\n'
    body += '## Compute kernels\n\n| ComputeShader | Kernel | 线程组 | 指令 |\n|---|---|---|---|\n'
    for record in index['objects']:
        if record['type'] == 'ComputeShader':
            for program in record['programs']:
                body += f"| {record['name']} | {program['kernel']} | {' × '.join(map(str, program['threads']))} | [asm](generated/shaders/{quote(program['assembly'])}) |\n"
    body += '\n## 图形 Shader\n\n| 来源 / Path ID | 名称 | DXBC 数 | 元数据 |\n|---|---|---:|---|\n'
    for record in index['objects']:
        if record['type'] == 'Shader':
            link = quote(f"generated/shaders/{record['source']}/{record['path_id']}/metadata.json")
            body += f"| {record['source']} / {record['path_id']} | {record['name']} | {len(record['programs'])} | [json]({link}) |\n"
    (KB / 'shader-catalog.md').write_text(body, encoding='utf-8')
    print(f'已核验：{programs} 个 DXBC，全部源资产和输出哈希、compute 线程组一致；基础导出待处理对象 {len(errors)} 个。')


if __name__ == '__main__':
    main()
