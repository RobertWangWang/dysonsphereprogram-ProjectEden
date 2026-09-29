"""完整导出 DSP 托管程序集；仅写入被忽略的 generated/full 目录。"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys

from dsp_knowledge import ROOT, GENERATED, paths, digest, read_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--managed')
    parser.add_argument('--bepinex')
    parser.add_argument('--all-managed', action='store_true', help='包含 Unity、系统与平台依赖程序集')
    parser.add_argument('--check', action='store_true', help='只校验，不重新导出')
    args = parser.parse_args()
    managed, bepinex = paths(args)
    ilspy = shutil.which('ilspycmd')
    cecil = bepinex / 'core/Mono.Cecil.dll' if bepinex else None
    if not ilspy or not cecil or not cecil.is_file():
        raise ValueError('需要 ilspycmd 和 BepInEx/core/Mono.Cecil.dll')
    references = {p.name: digest(p) for p in sorted(managed.glob('*.dll'))}
    version = subprocess.check_output([ilspy, '--version'], encoding='utf-8').strip()
    assemblies = sorted(managed.glob('*.dll')) if args.all_managed else [managed / 'Assembly-CSharp.dll']
    for assembly in assemblies:
        output = GENERATED / 'full' / assembly.stem
        marker = output / 'manifest.json'
        previous = read_json(marker) if marker.exists() else {}
        valid = (previous.get('references') == references and previous.get('ilspy') == version
                 and previous.get('files') and all((output / n).is_file() and digest(output / n) == h
                         for n, h in previous['files'].items()))
        if valid:
            print('PASS / 复用：' + assembly.name, flush=True)
            continue
        if args.check:
            raise ValueError('缓存缺失、损坏或过期：' + assembly.name)
        output.mkdir(parents=True, exist_ok=True)
        marker.unlink(missing_ok=True)
        source = output / 'source'
        metadata = output / 'metadata'
        # 仅删除本脚本固定输出下的旧项目和旧索引，避免已删除类型混入新版本。
        for stale in (source, metadata):
            if stale.exists():
                if not stale.resolve().is_relative_to((GENERATED / 'full').resolve()):
                    raise ValueError('导出路径不在 generated/full 内')
                shutil.rmtree(stale)
        source.mkdir()
        print('完整 C# 项目：' + assembly.name, flush=True)
        with (output / 'decompiler.log').open('w', encoding='utf-8') as log:
            subprocess.run([ilspy, '--disable-updatecheck', '-r', str(managed), '-p',
                            '-o', str(source), str(assembly)], stdout=log, stderr=subprocess.STDOUT, check=True)
        print('完整 IL：' + assembly.name, flush=True)
        with (output / 'assembly.il').open('wb') as f:
            subprocess.run([ilspy, '--disable-updatecheck', '-il', str(assembly)], stdout=f, check=True)
        metadata.mkdir(exist_ok=True)
        print('全部类型/字段/方法/调用引用：' + assembly.name, flush=True)
        subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                        str(ROOT / 'tools/export_dsp_metadata.ps1'), '-Assembly', str(assembly),
                        '-Cecil', str(cecil), '-OutputDir', str(metadata), '-AllTypes'], check=True)
        symbols = read_json(metadata / 'symbols.json')
        types = symbols['types']
        if {p.name: digest(p) for p in sorted(managed.glob('*.dll'))} != references:
            raise ValueError('导出期间游戏程序集发生变化')
        counts = {'types': len(types), 'methods': sum(len(t['methods']) for t in types),
                  'fields': sum(len(t['fields']) for t in types),
                  'method_bodies': sum(m['has_body'] for t in types for m in t['methods']),
                  'cs_files': len(list(source.rglob('*.cs')))}
        if not counts['cs_files'] or not (output / 'assembly.il').stat().st_size:
            raise ValueError('缺少 C# / IL 输出')
        manifest = {'assembly': symbols['assembly'], 'mvid': symbols['mvid'],
                    'assembly_sha256': digest(assembly), 'ilspy': version, 'references': references,
                    'counts': counts,
                    'files': {p.relative_to(output).as_posix(): digest(p) for p in sorted(output.rglob('*'))
                              if p.is_file() and p != marker}}
        marker.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print('完成：' + assembly.name + ' ' + json.dumps(counts), flush=True)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as exc:
        print('ERROR: ' + str(exc), file=sys.stderr)
        sys.exit(1)
