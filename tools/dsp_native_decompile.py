"""使用 Ghidra headless 反编译清单中的原生文件，不启动游戏。"""
import argparse
import json
import os
import re
from pathlib import Path
import subprocess

from dsp_knowledge import ROOT, GENERATED, digest, read_json
from dsp_pdb_identity import verify_pdb


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ghidra', required=True, type=Path)
    parser.add_argument('--java-home', required=True, type=Path)
    parser.add_argument('--module', help='只处理指定文件名；默认处理全部安装原生文件')
    parser.add_argument('--heap', default='4G', help='Ghidra Java 堆上限，例如 4G 或 32G')
    parser.add_argument('--pdb', type=Path, help='对指定模块加载事先核验 GUID/age 的 PDB')
    parser.add_argument('--attempt', help='独立尝试名称；保存到新目录和新 Ghidra 项目，保留旧结果')
    parser.add_argument('--max-cpu', type=int, default=4)
    parser.add_argument('--analysis-timeout', type=int, default=1800)
    args = parser.parse_args()
    if args.attempt and not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,63}', args.attempt):
        parser.error('--attempt 只允许小写字母、数字和连字符，最多 64 字符')
    if args.max_cpu < 1 or args.analysis_timeout < 1:
        parser.error('CPU 数和分析超时必须为正数')
    base = GENERATED / 'native'
    inventory = read_json(base / 'inventory.json')
    environment = os.environ.copy()
    environment['JAVA_HOME'] = str(args.java_home)
    environment['GHIDRA_HEADLESS_MAXMEM'] = args.heap
    launcher = args.ghidra / 'support/analyzeHeadless.bat'
    if not launcher.is_file() or not (args.java_home / 'bin/javac.exe').is_file():
        raise ValueError('需要 Ghidra 和包含 javac.exe 的 JDK')
    project = base / 'projects'
    project.mkdir(exist_ok=True)
    script_hash = digest(ROOT / 'tools/DspExportNative.java')
    selected = [r for r in inventory['files'] if not args.module or Path(r['path']).name == args.module]
    if not selected:
        raise ValueError('清单中没有匹配模块')
    if args.pdb and len(selected) != 1:
        raise ValueError('--pdb 必须与唯一 --module 一起使用')
    # 小模块先出结果；大模块最后，便于断点恢复。
    for record in sorted(selected, key=lambda r: r['bytes']):
        source = Path(inventory['game_directory']) / record['path']
        if digest(source) != record['sha256']:
            raise ValueError('原生文件已变化，请重新生成清单：' + str(source))
        output_name = record['output'] + ('--' + args.attempt if args.attempt else '')
        output = base / output_name
        output.mkdir(exist_ok=True)
        marker = output / 'decompile-manifest.json'
        old = read_json(marker) if marker.exists() else {}
        pdb_hash = digest(args.pdb) if args.pdb else None
        if args.pdb:
            verify_pdb(args.pdb, source)
        if (old.get('source_sha256') == record['sha256'] and old.get('export_script_sha256') == script_hash
                and old.get('pdb_sha256') == pdb_hash
                and old.get('ghidra') == args.ghidra.name
                and old.get('files') and all((output / n).is_file() and digest(output / n) == h for n, h in old['files'].items())):
            print('复用已导出结果：' + source.name, flush=True)
            continue
        marker.unlink(missing_ok=True)
        (output / 'functions.json').unlink(missing_ok=True)
        print('Ghidra 分析与逐函数导出：' + source.name, flush=True)
        pdb_args = ['-preScript', 'DspLoadPdb.java', str(args.pdb.resolve())] if args.pdb else []
        with (output / 'headless.log').open('wb') as log:
            subprocess.run([str(launcher), str(project), output_name, '-import', str(source),
                            '-overwrite', '-max-cpu', str(args.max_cpu), '-analysisTimeoutPerFile', str(args.analysis_timeout),
                            '-scriptPath', str(ROOT / 'tools'), *pdb_args, '-postScript', 'DspExportNative.java', str(output)],
                           env=environment, stdout=log, stderr=subprocess.STDOUT, check=True)
        report = read_json(output / 'functions.json')
        log_text = (output / 'headless.log').read_text(encoding='utf-8', errors='replace')
        if 'OutOfMemoryError' in log_text or 'Analysis timed out' in log_text:
            raise ValueError('分析内存不足或超时，不能写完成标记：' + source.name)
        if report['sha256'].lower() != record['sha256'].lower() or digest(source) != record['sha256']:
            raise ValueError('Ghidra 与磁盘文件哈希不匹配：' + source.name)
        files = ['functions.json', 'headless.log'] + [f['file'] for f in report['functions'] if 'file' in f]
        manifest = {'source_sha256': record['sha256'], 'export_script_sha256': script_hash,
                    'pdb_sha256': pdb_hash,
                    'attempt': args.attempt, 'heap': args.heap, 'max_cpu': args.max_cpu,
                    'analysis_timeout': args.analysis_timeout,
                    'ghidra': args.ghidra.name, 'discovered': report['discovered'],
                    'decompiled': report['decompiled'], 'failed': report['failed'],
                    'files': {n: digest(output / n) for n in files}}
        marker.write_text(json.dumps(manifest, indent=2), encoding='utf-8')
        print(f"已导出 {source.name}: discovered={report['discovered']}, decompiled={report['decompiled']}, failed={report['failed']}", flush=True)


if __name__ == '__main__':
    main()
