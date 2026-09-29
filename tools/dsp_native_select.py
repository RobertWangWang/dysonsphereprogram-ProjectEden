"""核验后选择独立原生分析结果，保留旧目录；不隐藏函数失败。"""
import argparse
import json
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json


def selected_folder(base, module):
    marker = base / 'selected-exports.json'
    selected = read_json(marker) if marker.exists() else {}
    entry = selected.get(module['path'])
    if not entry:
        return base / module['output']
    name = entry['output']
    if Path(name).name != name or not name.startswith(module['output'] + '--'):
        raise ValueError('Invalid selected output directory')
    folder = base / name
    if entry['source_sha256'] != module['sha256'] or digest(folder / 'decompile-manifest.json') != entry['manifest_sha256']:
        raise ValueError('Selected output baseline changed')
    return folder


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--module', required=True)
    parser.add_argument('--attempt', required=True)
    args = parser.parse_args()
    base = GENERATED / 'native'
    inventory = read_json(base / 'inventory.json')
    matches = [r for r in inventory['files'] if Path(r['path']).name == args.module]
    if len(matches) != 1:
        raise ValueError('Need a unique module')
    module = matches[0]
    name = module['output'] + '--' + args.attempt
    if Path(name).name != name or '/' in name or '\\' in name:
        raise ValueError('Invalid attempt name')
    folder = base / name
    marker = folder / 'decompile-manifest.json'
    report = read_json(marker)
    if digest(Path(inventory['game_directory']) / module['path']) != module['sha256'] or report['source_sha256'] != module['sha256']:
        raise ValueError('Source hash mismatch')
    for filename, sha in report['files'].items():
        if digest(folder / filename) != sha:
            raise ValueError('Output hash mismatch: ' + filename)
    log = (folder / 'headless.log').read_text(encoding='utf-8', errors='replace')
    if 'OutOfMemoryError' in log or 'Analysis timed out' in log or 'REPORT: Analysis succeeded' not in log:
        raise ValueError('Analysis is not complete')
    functions = read_json(folder / 'functions.json')
    if functions['sha256'].lower() != module['sha256'].lower():
        raise ValueError('Function report source mismatch')
    if sum(f['status'] == 'decompiled' for f in functions['functions']) != report['decompiled']:
        raise ValueError('Function count mismatch')
    if any(f['file'] not in report['files'] for f in functions['functions'] if 'file' in f):
        raise ValueError('Function is absent from manifest')
    selected_path = base / 'selected-exports.json'
    selected = read_json(selected_path) if selected_path.exists() else {}
    selected[module['path']] = dict(output=name, source_sha256=module['sha256'], manifest_sha256=digest(marker))
    selected_path.write_text(json.dumps(selected, indent=2), encoding='utf-8')
    print(f"Selected {name}: {report['decompiled']} C outputs, {report['failed']} function failures retained.")


if __name__ == '__main__':
    main()
