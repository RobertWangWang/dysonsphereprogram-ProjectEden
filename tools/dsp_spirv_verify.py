"""核验非 DXBC 补充清单与当前资产，并对全部 SPIR-V 运行 Khronos validator。"""
import argparse
import json
from pathlib import Path
import subprocess
from dsp_knowledge import GENERATED, digest, read_json, paths


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--spirv-val', required=True, type=Path)
    parser.add_argument('--managed')
    parser.add_argument('--bepinex')
    args = parser.parse_args()
    managed, unused = paths(args)
    shader_manifest = GENERATED / 'shaders/manifest.json'
    baseline = read_json(shader_manifest)
    for source, sha in baseline['sources'].items():
        if digest(managed.parent / source) != sha:
            raise ValueError('Game asset changed: ' + source)
    records = []
    for category in ('compute-platforms', 'graphics-platforms'):
        base = GENERATED / category
        manifest = read_json(base / 'manifest.json')
        for filename, sha in manifest['files'].items():
            if digest(base / filename) != sha:
                raise ValueError('Output changed: ' + filename)
        index = read_json(base / 'index.json')
        if index['shader_manifest_sha256'] != digest(shader_manifest):
            raise ValueError('Shader baseline changed')
        for program in index['programs']:
            if program['format'] == 'Vulkan-payload':
                raise ValueError('Unprocessed Vulkan payload')
            if program['format'] != 'SPIR-V':
                continue
            files = [s['binary'] for s in program['snippets']] if 'snippets' in program else [program['file']]
            for filename in files:
                path = base / filename
                result = subprocess.run([str(args.spirv_val), str(path)], capture_output=True, text=True)
                records.append(dict(file=path.relative_to(GENERATED).as_posix(), sha256=digest(path),
                                    exit_code=result.returncode, message=result.stdout + result.stderr))
    report = dict(validator_sha256=digest(args.spirv_val), programs=records)
    (GENERATED / 'spirv-validation.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    failed = [r for r in records if r['exit_code']]
    if failed:
        raise ValueError(f'{len(failed)} SPIR-V validation failures; see spirv-validation.json')
    print(f'PASS: {len(records)} SPIR-V programs; source assets and supplemental output hashes match.')


if __name__ == '__main__':
    main()
