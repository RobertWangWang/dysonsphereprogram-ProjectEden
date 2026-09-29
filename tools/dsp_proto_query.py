"""原型数据查询与离线一致性核验；这里是磁盘原版值，不是 mod 运行时值。"""
import argparse
import json
from dsp_knowledge import KB, GENERATED, digest, read_json, paths


def load():
    base = GENERATED / 'prototypes'
    manifest = read_json(base / 'manifest.json')
    for name, sha in manifest['files'].items():
        if digest(base / name) != sha:
            raise ValueError('原型缓存损坏：' + name)
    index = read_json(base / 'index.json')
    return index, {t['name']: read_json(base / (t['name'] + '.json'))['dataArray'] for t in index['tables']}


def verify(index, tables, args):
    managed, _ = paths(args)
    proto_manifest = read_json(GENERATED / 'prototypes/manifest.json')
    if digest(managed.parent / index['source']) != proto_manifest['source_sha256']:
        raise ValueError('游戏资源已变化，请重新导出原型')
    if {p.name: digest(p) for p in sorted(managed.glob('*.dll'))} != proto_manifest['assemblies']:
        raise ValueError('游戏程序集已变化，请重新导出原型')
    full = GENERATED / 'full/Assembly-CSharp'
    manifest = read_json(full / 'manifest.json')
    if manifest['assembly_sha256'] != read_json(GENERATED / 'prototypes/manifest.json')['assemblies']['Assembly-CSharp.dll']:
        raise ValueError('程序集符号与原型导出基线不同')
    if digest(full / 'metadata/symbols.json') != manifest['files']['metadata/symbols.json']:
        raise ValueError('程序集符号索引损坏')
    ldb = next(t for t in read_json(full / 'metadata/symbols.json')['types'] if t['name'] == 'LDB')
    expected = {f['type'] for f in ldb['fields'] if f['type'].endswith('ProtoSet')}
    if set(tables) != expected:
        raise ValueError(f'LDB 表未完全覆盖：缺少 {expected-set(tables)}，多出 {set(tables)-expected}')
    items = {r['ID'] for r in tables['ItemProtoSet']}
    recipes = {r['ID'] for r in tables['RecipeProtoSet']}
    techs = {r['ID'] for r in tables['TechProtoSet']}
    for r in tables['RecipeProtoSet']:
        if len(r['Items']) != len(r['ItemCounts']) or len(r['Results']) != len(r['ResultCounts']):
            raise ValueError('配方 ID/数量数组不匹配：' + str(r['ID']))
        if (set(r['Items']) | set(r['Results'])) - items:
            raise ValueError('配方引用未知物品：' + str(r['ID']))
    for r in tables['TechProtoSet']:
        if set(r['UnlockRecipes']) - recipes or set(r['Items']) - items:
            raise ValueError('科技引用未知配方或物品：' + str(r['ID']))
        if (set(r['PreTechs']) | set(r['PreTechsImplicit'])) - techs:
            raise ValueError('科技前置引用未知科技：' + str(r['ID']))
    body = '# 原版原型表目录\n\n'
    body += f"覆盖 LDB 声明的全部 **{len(tables)} 张表、{sum(len(t) for t in tables.values()):,} 条记录**。资源内 Unity 版本标记为 `{index['unity_version']}`，不是游戏展示版本。\n\n"
    body += '来源为 resources.assets；用 UnityPy ' + index['unitypy'] + ' 与 TypeTreeGeneratorAPI ' + index['typetree_generator'] + ' 根据本机托管程序集的序列化字段生成类型树。每个对象完整读取，并解码后重新编码，与原始对象字节完全一致。另核对 LDB 表覆盖、配方 ID/数量数组与物品引用、科技前置和解锁配方引用。\n\n'
    body += '这些是磁盘原版资产：LDBTool、自定义 JSON、Harmony、科技状态和其他 mod 的修改不包含在内。缓存哈希见 generated/prototypes/manifest.json；游戏升级后重新导出。\n\n'
    body += '| 表 | Path ID | 条数 | 序列化字节 | 本机数据 |\n|---|---:|---:|---:|---|\n'
    for t in index['tables']:
        if t['roundtrip'] != 'byte-identical':
            raise ValueError('缺少字节往返核验：' + t['name'])
        body += f"| {t['name']} | {t['path_id']} | {t['rows']} | {t['bytes']:,} | [JSON](generated/prototypes/{t['name']}.json) |\n"
    body += '\n## 查询\n\n```powershell\npython -X utf8 tools/dsp_proto_query.py 宇宙矩阵 --table RecipeProtoSet\npython -X utf8 tools/dsp_proto_query.py 115 --table RecipeProtoSet\npython -X utf8 tools/dsp_proto_query.py --verify\n```\n\n导出用安装 UnityPy/TypeTreeGeneratorAPI 的 Python 执行 tools/dsp_proto_export.py；查询不需要这两个库。名称按子串匹配；纯数字只匹配 ID。\n'
    (KB / 'prototype-catalog.md').write_text(body, encoding='utf-8')
    print('PASS：25 张 LDB 表覆盖、原型缓存、配方/科技引用及已记录字节往返核验通过。')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('query', nargs='?')
    parser.add_argument('--table', help='例如 RecipeProtoSet；省略则检索全部表')
    parser.add_argument('--verify', action='store_true')
    parser.add_argument('--managed')
    parser.add_argument('--bepinex')
    args = parser.parse_args()
    index, tables = load()
    if args.verify:
        verify(index, tables, args)
    if args.query:
        if args.table and args.table not in tables:
            raise ValueError('未知表：' + args.table)
        hits = 0
        for name, rows in tables.items():
            if args.table and name != args.table:
                continue
            for row in rows:
                match = row['ID'] == int(args.query) if args.query.isdecimal() else args.query.casefold() in row['Name'].casefold()
                if match:
                    print(name + '\n' + json.dumps(row, ensure_ascii=False, indent=2))
                    hits += 1
        print(f'{hits} 条匹配；仅为磁盘原版资产，不包含运行时 mod 修改。')
    elif not args.verify:
        parser.error('需要关键词/ID 或 --verify')


if __name__ == '__main__':
    main()
