"""本机 DSP 反编译知识库：refresh / check / catalog / search；仅读取游戏文件。"""
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
KB = ROOT / 'docs' / 'knowledge'
GENERATED = KB / 'generated'
TYPES = ['LabComponent', 'AssemblerComponent', 'PowerExchangerComponent',
         'StationComponent', 'FactorySystem', 'PlanetTransport', 'PlanetFactory', 'GameLogic',
         'MinerComponent', 'VeinData', 'Cargo', 'CargoContainer', 'CargoPath', 'CargoTraffic',
         'PowerGeneratorComponent', 'PowerSystem', 'RecipeProto', 'RecipeExecuteData',
         'GameHistoryData', 'GameSave', 'StorageComponent', 'GameConfig', 'ItemProto', 'PrefabDesc',
         'InserterComponent', 'PilerComponent', 'SpraycoaterComponent', 'SplitterComponent', 'BeltComponent',
         'UniverseGen', 'StarGen', 'PlanetGen', 'PlanetData', 'StarData', 'GalaxyData',
         'PlanetModelingManager', 'ThemeProto', 'VeinProto', 'PlanetAlgorithm'] + [
             'PlanetAlgorithm' + str(i) for i in range(15)] + [
         'BuildTool', 'BuildTool_Click', 'BuildTool_Path', 'BuildTool_Inserter',
         'BuildTool_BlueprintCopy', 'BuildTool_BlueprintPaste', 'BuildTool_Dismantle',
         'BuildTool_Upgrade', 'BuildTool_Reform', 'BuildTool_Addon', 'BuildPreview',
         'PlayerAction_Build', 'PlayerAction_Inspect', 'BlueprintData', 'BlueprintBuilding',
         'BlueprintArea', 'BlueprintUtils', 'Mecha', 'MechaForge', 'MechaLab']


def digest(path):
    with Path(path).open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


def paths(args):
    props = ROOT / 'DefaultPath.props'
    values = {}
    if props.exists():
        values = {e.tag: e.text for e in ET.parse(props).iter() if e.text and not list(e)}
    managed = args.managed or os.environ.get('PROJECTEDEN_MANAGED')
    if not managed and values.get('GameDir'):
        managed = str(Path(values['GameDir']) / 'DSPGAME_Data' / 'Managed')
    if not managed:
        raise ValueError('请指定 --managed，或配置 DefaultPath.props / PROJECTEDEN_MANAGED')
    bepinex = args.bepinex or os.environ.get('PROJECTEDEN_BEPINEX') or values.get('BepInExDir')
    return Path(managed), Path(bepinex) if bepinex else None


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def refresh(args):
    managed, bepinex = paths(args)
    assembly = managed / 'Assembly-CSharp.dll'
    cecil = bepinex / 'core' / 'Mono.Cecil.dll' if bepinex else None
    ilspy = shutil.which('ilspycmd')
    if not assembly.is_file() or not cecil or not cecil.is_file() or not ilspy:
        raise ValueError('需要游戏 Assembly-CSharp.dll、BepInEx/core/Mono.Cecil.dll 和 ilspycmd')
    GENERATED.mkdir(parents=True, exist_ok=True)
    # 先撤销完成标记；中途失败不能把旧 manifest 误读成新导出成功。
    manifest_path = GENERATED / 'manifest.json'
    previous = read_json(manifest_path) if manifest_path.exists() else {}
    manifest_path.unlink(missing_ok=True)
    source_hash = digest(assembly)
    references = {p.name: digest(p) for p in sorted(managed.glob('*.dll'))}
    version = subprocess.check_output([ilspy, '--version'], encoding='utf-8').strip()
    reusable = (not args.force and previous.get('assembly_sha256') == source_hash
                and previous.get('ilspy') == version and previous.get('references') == references)
    types_path = GENERATED / 'types.json'
    types_path.write_text(json.dumps(TYPES), encoding='utf-8')
    subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                    str(ROOT / 'tools' / 'export_dsp_metadata.ps1'), '-Assembly', str(assembly),
                    '-Cecil', str(cecil), '-OutputDir', str(GENERATED), '-TypesFile', str(types_path)], check=True)
    reused = 0
    for name in TYPES:
        target = GENERATED / (name + '.cs')
        if (reusable and target.is_file()
                and previous.get('files', {}).get(target.name) == digest(target)):
            reused += 1
            continue
        with target.open('wb') as f:
            subprocess.run([ilspy, '--disable-updatecheck', '-r', str(managed), '-t', name, str(assembly)], stdout=f, check=True)
        if not target.stat().st_size:
            raise ValueError('反编译输出为空：' + name)
        print('已导出 C# / IL：' + name, flush=True)
    if digest(assembly) != source_hash:
        raise ValueError('导出期间游戏程序集发生变化，请重新生成')
    if {p.name: digest(p) for p in sorted(managed.glob('*.dll'))} != references:
        raise ValueError('导出期间依赖程序集发生变化，请重新生成')
    symbols = read_json(GENERATED / 'symbols.json')
    output_names = ['symbols.json', 'types.json'] + [n + ext for n in TYPES for ext in ('.cs', '.il')]
    manifest = {'assembly_sha256': source_hash, 'assembly': symbols['assembly'], 'mvid': symbols['mvid'],
                'ilspy': version, 'references': references, 'types': TYPES,
                'files': {n: digest(GENERATED / n) for n in output_names}}
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'完成；复用 {reused} 个有效 C# 缓存；程序集 SHA-256：' + source_hash)


def check(args):
    managed, _ = paths(args)
    manifest = read_json(GENERATED / 'manifest.json')
    actual = digest(managed / 'Assembly-CSharp.dll')
    if actual != manifest['assembly_sha256']:
        raise ValueError('游戏程序集已变化，反编译缓存过期，请 refresh')
    if manifest.get('types') != TYPES:
        raise ValueError('计划导出的类型与缓存不一致，请 refresh')
    if 'references' in manifest and {p.name: digest(p) for p in sorted(managed.glob('*.dll'))} != manifest['references']:
        raise ValueError('依赖程序集已变化，请 refresh')
    for name, expected in manifest['files'].items():
        if digest(GENERATED / name) != expected:
            raise ValueError('生成文件缺失或已改动：' + name)
    baseline = read_json(KB / 'baseline.json')
    if actual != baseline['assembly_sha256']:
        raise ValueError('缓存有效，但人工知识页的核验基线已过期；请重新验证结论后更新 baseline.json')
    print('PASS：当前游戏、反编译缓存与知识页基线一致，所有生成文件哈希匹配。')


def catalog(args):
    manifest = read_json(GENERATED / 'manifest.json')
    symbols_path = GENERATED / 'symbols.json'
    if digest(symbols_path) != manifest['files']['symbols.json']:
        raise ValueError('符号索引已改动，请 refresh')
    types = read_json(symbols_path)['types']
    methods = sum(len(t['methods']) for t in types)
    fields = sum(len(t['fields']) for t in types)
    body = '# 已导出类型目录\n\n'
    body += f'当前覆盖 **{len(types)} 个类型、{methods:,} 个方法、{fields:,} 个字段**。'
    body += '由 `python -X utf8 tools/dsp_knowledge.py catalog` 根据当前符号索引生成。\n\n'
    body += '程序集基线见 [baseline.json](baseline.json)。链接指向本机生成文件，首次使用先运行 `refresh`。\n\n'
    body += '| 类型 | 方法数 | 字段数 | 本机参考 |\n|---|---:|---:|---|\n'
    for t in types:
        name = t['name']
        body += f"| {name} | {len(t['methods'])} | {len(t['fields'])} | [C#](generated/{name}.cs) · [IL](generated/{name}.il) |\n"
    body += '\n类型导出完整，但人工结论只覆盖专题页明确列出的机制。方法数包括构造器和属性访问器；字段数包括常量与编译器生成字段。继承而未重写的方法不重复计入派生类。\n'
    (KB / 'type-catalog.md').write_text(body, encoding='utf-8')
    # 统计数字只自动更新入口，不给任何人工结论重新盖章。
    readme = KB / 'README.md'
    text = readme.read_text(encoding='utf-8')
    text, count = re.subn(r'现覆盖 \*\*\d+ 个类型、[\d,]+ 个方法、[\d,]+ 个字段\*\*',
                         f'现覆盖 **{len(types)} 个类型、{methods:,} 个方法、{fields:,} 个字段**', text)
    if count != 1:
        raise ValueError('目录已生成，但 README 统计标记不唯一，请手动更新入口')
    readme.write_text(text, encoding='utf-8')
    print(f'已更新目录和入口：{len(types)} 个类型，{methods} 个方法，{fields} 个字段。')


def search(args):
    directory = GENERATED / 'full' / 'Assembly-CSharp' if args.full else GENERATED
    symbol_name = 'metadata/symbols.json' if args.full else 'symbols.json'
    manifest = read_json(directory / 'manifest.json')
    symbols_path = directory / symbol_name
    if digest(symbols_path) != manifest['files'][symbol_name]:
        raise ValueError('符号索引已改动，请 refresh')
    q = args.query.casefold()
    hits = 0
    for t in read_json(symbols_path)['types']:
        for field in t['fields']:
            label = t['name'] + '::' + field['name'] + ' : ' + field['type']
            if q in label.casefold():
                print(label)
                hits += 1
        for method in t['methods']:
            if q in method['signature'].casefold():
                print(method['signature'] + ' [' + method['token'] + ']')
                if args.calls:
                    for call in method['calls']:
                        print('  -> ' + call)
                hits += 1
            elif args.callers and any(q in call.casefold() for call in method['calls']):
                print('调用方：' + method['signature'])
                hits += 1
    print(str(hits) + ' 个匹配；仅覆盖已导出的类型。先运行 check 确认游戏版本未变化。')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    for command in ('refresh', 'check'):
        p = sub.add_parser(command)
        p.add_argument('--managed', help='游戏 DSPGAME_Data/Managed 目录')
        p.add_argument('--bepinex', help='BepInEx 目录')
        if command == 'refresh':
            p.add_argument('--force', action='store_true', help='不复用有效的 C# 缓存')
    sub.add_parser('catalog', help='从当前符号索引更新类型目录及入口统计')
    p = sub.add_parser('search')
    p.add_argument('query')
    p.add_argument('--full', action='store_true', help='搜索完整 Assembly-CSharp 索引，包括嵌套类型')
    p.add_argument('--calls', action='store_true', help='显示命中方法引用的方法')
    p.add_argument('--callers', action='store_true', help='同时查找已导出类型中的调用方')
    args = parser.parse_args()
    try:
        {'refresh': refresh, 'check': check, 'search': search, 'catalog': catalog}[args.command](args)
        return 0
    except (OSError, ValueError, subprocess.CalledProcessError) as exc:
        print('ERROR: ' + str(exc), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
