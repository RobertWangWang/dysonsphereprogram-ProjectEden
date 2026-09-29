"""从已核验的全量导出生成类型导航；分类仅为名称检索，不代表人工审查。"""
from urllib.parse import quote

from dsp_knowledge import KB, GENERATED, digest, read_json


def category(name):
    simple = name.split('/')[0].split('.')[-1]
    if '.' in name.split('/')[0]:
        return '命名空间 / SDK / 工具'
    for label, prefixes in [
        ('界面与交互', ('UI',)),
        ('黑雾与敌人', ('DF', 'Enemy', 'Hive', 'Hatred', 'Aggress', 'Assault')),
        ('战斗与防御', ('Combat', 'Skill', 'Turret', 'Shield', 'LocalLaser', 'LocalCannon', 'Missile', 'Bomb', 'SpaceLaser', 'SpaceCannon', 'SpaceFleet', 'Fleet', 'Squad', 'Defense', 'Battle', 'Shoot', 'Laser', 'Cannon')),
        ('戴森球与发射', ('Dyson', 'Sail', 'Rocket', 'Ejector', 'Silo')),
        ('建造、蓝图与地基', ('Build', 'Blueprint', 'Prebuild', 'Reform', 'BP', 'Foundation')),
        ('玩家与机甲', ('Player', 'Mecha', 'Forge', 'Package')),
        ('星系、行星与地形', ('Universe', 'Galaxy', 'Star', 'Planet', 'Astro', 'Vein', 'Veget', 'Terrain', 'Height', 'Theme')),
        ('电力与能源', ('Power', 'Energy')),
        ('生产、货物与物流', ('Factory', 'Assembler', 'Lab', 'Miner', 'Fractionator', 'Inserter', 'Cargo', 'Belt', 'Splitter', 'Spraycoater', 'Piler', 'Storage', 'Station', 'Dispenser', 'Courier', 'Ship', 'Drone', 'Logistic', 'Tank')),
        ('原型、配方与数据', ('LDB', 'Proto', 'Prefab', 'Recipe', 'Item', 'Tech', 'Model', 'String', 'Resource')),
        ('游戏生命周期、存档与统计', ('Game', 'Achievement', 'Milestone', 'Production', 'Consumption', 'Performance', 'Property', 'Scenario', 'Mission', 'Journal', 'History', 'Warning', 'Monitor')),
        ('渲染、音效与输入', ('VF', 'GPU', 'Render', 'Audio', 'Camera', 'Mesh', 'Material', 'Anim', 'Input', 'LOD', 'Post', 'Effect')),
    ]:
        if simple.startswith(prefixes):
            return label
    return '其他与基础结构'


def main():
    output = GENERATED / 'full/Assembly-CSharp'
    manifest = read_json(output / 'manifest.json')
    symbols_path = output / 'metadata/symbols.json'
    if digest(symbols_path) != manifest['files']['metadata/symbols.json']:
        raise ValueError('全量符号索引校验失败')
    types = read_json(symbols_path)['types']
    groups = {}
    for t in types:
        groups.setdefault(category(t['name']), []).append(t)
    body = '# 全量类型与功能入口\n\n'
    body += '由 `python -X utf8 tools/dsp_full_catalog.py` 生成。覆盖 Assembly-CSharp 的全部类型定义（含嵌套类型、编译器生成类型及 `<Module>`）；不把继承成员重复计数。\n\n'
    body += '分组只按类型名匹配，用于定位，不是功能正确性或完整人工分析的证明。每个类型链接到精确的 Cecil IL；完整 C# 按顶层类型存放在本机 `generated/full/Assembly-CSharp/source/`。\n\n'
    body += '程序集 SHA-256：`' + manifest['assembly_sha256'] + '`。\n\n'
    for group, entries in groups.items():
        body += f'- {group}：{len(entries)} 个类型\n'
    for group, entries in groups.items():
        body += f'\n## {group}\n\n| 类型 | 基类 | 方法 | 字段 | IL |\n|---|---|---:|---:|---|\n'
        for t in sorted(entries, key=lambda t: t['name']):
            name = t['name'].replace('|', '&#124;')
            base = (t.get('base_type') or '').replace('|', '&#124;')
            link = 'generated/full/Assembly-CSharp/metadata/' + quote(t['il_file'])
            body += f"| `{name}` | `{base}` | {len(t['methods'])} | {len(t['fields'])} | [IL]({link}) |\n"
    (KB / 'full-type-catalog.md').write_text(body, encoding='utf-8')
    print(f'全量类型目录：{len(types)} 个类型，{len(groups)} 个检索分组。')


if __name__ == '__main__':
    main()
