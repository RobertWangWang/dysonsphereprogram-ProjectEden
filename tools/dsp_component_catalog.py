"""核验脚本组件导出并生成类型导航，可按类名查询实例数据。"""
import argparse
import collections
import json
from urllib.parse import quote
from dsp_knowledge import KB, GENERATED, paths, digest, read_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--managed')
    parser.add_argument('--bepinex')
    parser.add_argument('--query', help='类名/组件名子串；仅查询缓存')
    args = parser.parse_args()
    base = GENERATED / 'components'
    manifest = read_json(base / 'manifest.json')
    if digest(base / 'index.json') != manifest['files']['index.json']:
        raise ValueError('组件索引损坏')
    records = read_json(base / 'index.json')['objects']
    if args.query:
        for r in records:
            if args.query.casefold() in (r.get('class', '') + ' ' + r.get('name', '')).casefold():
                print(json.dumps(r, ensure_ascii=False))
        return
    managed, _ = paths(args)
    if any(digest(managed.parent / n) != h for n, h in manifest['sources'].items()):
        raise ValueError('资源已更新，请重新导出')
    if {p.name: digest(p) for p in managed.glob('*.dll')} != manifest['assemblies']:
        raise ValueError('程序集已更新，请重新导出')
    for name, sha in manifest['files'].items():
        if digest(base / name) != sha:
            raise ValueError('组件输出损坏：' + name)
    if any(r['status'] != 'byte-identical' for r in records):
        raise ValueError('仍有组件未完整解码')
    counts = collections.Counter((r['assembly'], r['class']) for r in records)
    examples = {(r['assembly'], r['class']): r for r in reversed(records)}
    text = '# 序列化脚本组件全量目录\n\n'
    text += f'已从 {len(manifest["sources"])} 个资产文件解码 **{len(records):,} 个脚本组件/ScriptableObject 实例，覆盖 {len(counts)} 种程序集＋类型组合**。失败 0。每个对象都经过完整字节消费、Unity 标准对象头对比、解码再编码逐字节一致检查；源资产、程序集、全部输出哈希均已核验。\n\n'
    text += '这张表统计的是序列化实例，不是代码中的全部类。包含 UI、prefab 配置、原型表和 GUISkin；运行时动态创建/修改的数据不在此范围内。每个类型只列一个示例，全部实例及路径在 generated/components/index.json。\n\n'
    text += '类型树生成器的 string[]/List<string> 外层类型和 m_Enabled 对齐问题由 tools/dsp_typetree.py 修正；不删除字段、不忽略尾部数据。字节往返本身不能证明字段语义，所以同时比对标准对象头，并依据 C# 字段声明区分字符串和字符串数组。\n\n'
    text += '```powershell\npython -X utf8 tools/dsp_component_catalog.py\npython -X utf8 tools/dsp_component_catalog.py --query LODModelDesc\npython -X utf8 tools/dsp_component_catalog.py --query BuiltinConfig\n```\n\n'
    text += '| 程序集 | 类型 | 实例数 | 示例数据 |\n|---|---|---:|---|\n'
    for (assembly, name), count in sorted(counts.items()):
        example = examples[(assembly, name)]
        link = quote('generated/components/' + example['data'])
        text += f'| {assembly} | `{name}` | {count} | [JSON]({link}) |\n'
    (KB / 'component-catalog.md').write_text(text, encoding='utf-8')
    print(f'PASS：{len(records)} 个实例、{len(counts)} 类；全部输入/输出哈希和解码状态已核验。')


if __name__ == '__main__':
    main()
