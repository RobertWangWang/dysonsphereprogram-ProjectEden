"""交叉核对 ILSpy 整程序集 IL 与 Cecil 元数据覆盖率，输出完整导出清单。"""
import argparse
import re

from dsp_knowledge import KB, GENERATED, paths, digest, read_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--managed')
    parser.add_argument('--bepinex')
    args = parser.parse_args()
    managed, _ = paths(args)
    assemblies = sorted(managed.glob('*.dll'))
    references = {p.name: digest(p) for p in assemblies}
    totals = dict.fromkeys(('types', 'methods', 'fields', 'method_bodies', 'cs_files'), 0)
    rows = []
    for assembly in assemblies:
        output = GENERATED / 'full' / assembly.stem
        manifest = read_json(output / 'manifest.json')
        if manifest['references'] != references or manifest['assembly_sha256'] != digest(assembly):
            raise ValueError('当前程序集与导出不一致：' + assembly.name)
        for filename, expected in manifest['files'].items():
            if digest(output / filename) != expected:
                raise ValueError('输出校验失败：' + str(output / filename))
        types = read_json(output / 'metadata/symbols.json')['types']
        counts = {'types': len(types), 'methods': sum(len(t['methods']) for t in types),
                  'fields': sum(len(t['fields']) for t in types),
                  'method_bodies': sum(m['has_body'] for t in types for m in t['methods']),
                  'cs_files': len(list((output / 'source').rglob('*.cs')))}
        if counts != manifest['counts']:
            raise ValueError('统计与元数据不一致：' + assembly.name)
        il = (output / 'assembly.il').read_text(encoding='utf-8-sig')
        if len(re.findall(r'^\s*\.method\s', il, re.M)) != counts['methods']:
            raise ValueError('整程序集 IL 方法覆盖不完整：' + assembly.name)
        if len(re.findall(r'^\s*\.class\s', il, re.M)) != counts['types']:
            raise ValueError('整程序集 IL 类型覆盖不完整：' + assembly.name)
        for key in totals:
            totals[key] += counts[key]
        rows.append(f"| {assembly.name} | {counts['types']:,} | {counts['methods']:,} | {counts['method_bodies']:,} | {counts['fields']:,} | {counts['cs_files']:,} | `{manifest['assembly_sha256']}` |")
    body = '# 完整程序集导出核验清单\n\n'
    body += '由 `python -X utf8 tools/dsp_verify_full.py` 在全部检查通过后生成。当前安装目录 Managed 下 '
    body += f'**{len(assemblies)} / {len(assemblies)} 个 DLL** 完成导出；没有按类型抽样。\n\n'
    body += f"合计 {totals['types']:,} 个类型、{totals['methods']:,} 个方法（{totals['method_bodies']:,} 个有托管方法体）、{totals['fields']:,} 个字段、{totals['cs_files']:,} 份 C# 文件。包括游戏代码、Unity、平台 SDK 和 .NET 运行库，不能把总数称为全部都是游戏业务代码。\n\n"
    body += '检查项目：游戏与依赖 SHA-256、所有已记录输出 SHA-256、元数据统计、ILSpy `.class`/`.method` 数量与独立 Cecil 元数据数量逐程序集一致。该覆盖检查不保证 C# 重建语义完全等价，也不意味着原生方法体或 shader 已恢复。\n\n'
    body += '| 程序集 | 类型 | 方法 | 有方法体 | 字段 | C# 文件 | SHA-256 |\n|---|---:|---:|---:|---:|---:|---|\n'
    body += '\n'.join(rows) + '\n'
    (KB / 'assembly-inventory.md').write_text(body, encoding='utf-8')
    print(f'PASS：{len(assemblies)} 个程序集全部哈希、元数据统计与完整 IL 覆盖一致。{totals}')


if __name__ == '__main__':
    main()
