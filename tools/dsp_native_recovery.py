"""核验失败函数的独立补充输出；不把补充结果当作原始自动分析已经完整。"""
import json
from pathlib import Path
from urllib.parse import quote
from dsp_knowledge import GENERATED, KB, digest, read_json
from dsp_native_select import selected_folder


def main():
    base = GENERATED / 'native'
    inventory = read_json(base / 'inventory.json')
    summaries = []
    for module in inventory['files']:
        folder = selected_folder(base, module)
        module = dict(module, output=folder.name)
        reports = []
        c_only = folder / 'c-only/attempts.json'
        if c_only.exists():
            report = read_json(c_only)
            if not report.get('finished'):
                raise ValueError('C-only export still unfinished')
            if report.get('syntax_tree') is not False:
                raise ValueError('Unexpected recovery mode')
            reports.append((c_only, report['source_sha256'], report['attempts'], 'C-only'))
        for path in sorted((folder / 'untyped').glob('*.json')):
            report = read_json(path)
            if not report.get('program_changes_rolled_back'):
                raise ValueError('Temporary type edits were not rolled back')
            reports.append((path, report['source_sha256'], [report], 'temporary-untyped'))
        if not reports:
            continue
        if digest(Path(inventory['game_directory']) / module['path']) != module['sha256']:
            raise ValueError('Game binary changed')
        original = read_json(folder / 'functions.json')
        if original['sha256'].lower() != module['sha256'].lower():
            raise ValueError('Original report source mismatch')
        failures = {r['address']: r for r in original['functions'] if r['status'] in ('failed', 'timeout')}
        recovered = {}
        tracked = {'functions.json': digest(folder / 'functions.json')}
        for path, sha, attempts, mode in reports:
            if sha.lower() != module['sha256'].lower():
                raise ValueError('Recovery source mismatch')
            tracked[path.relative_to(folder).as_posix()] = digest(path)
            for attempt in attempts:
                address = attempt['address']
                if address not in failures:
                    raise ValueError('Recovery does not correspond to an original failure')
                if attempt['status'] != 'decompiled':
                    continue
                code = path.parent / attempt['file']
                if not code.is_file() or code.stat().st_size == 0:
                    raise ValueError('Missing C output')
                name = code.relative_to(folder).as_posix()
                tracked[name] = digest(code)
                recovered[address] = dict(address=address, name=failures[address]['name'], file=name, mode=mode)
        unresolved = [r for a, r in failures.items() if a not in recovered]
        manifest = dict(source_sha256=module['sha256'], original_export_marker_present=(folder / 'decompile-manifest.json').exists(),
                        recovered=list(recovered.values()), unresolved=unresolved, files=tracked)
        (folder / 'recovery-manifest.json').write_text(json.dumps(manifest, ensure_ascii=True, indent=2), encoding='utf-8')
        summaries.append((module, manifest))
    body = '# 原生失败函数补充恢复\n\n'
    body += '补充输出不覆盖第一次导出，也不修复 OOM 导致的分析缺口。C-only 仅关闭可选 Java 高层语法树转换，仍由 Ghidra 原生后端生成 C；temporary-untyped 临时移除局部类型并将参数/返回类型改为等宽未定义类型，随后回滚。后者保留原签名，但字段类型的解释必须结合汇编。\n\n'
    body += '工具：`DspExportCOnly.java`、`DspRetryUntyped.java`、`python -X utf8 tools/dsp_native_recovery.py`。均在保存的项目上运行，原游戏二进制不修改。补充来源与文件哈希见各模块 recovery-manifest.json。\n\n'
    for module, report in summaries:
        body += f"## {module['path']}\n\n补充恢复 {len(report['recovered'])} 个函数的 C，原失败列表中仍有 {len(report['unresolved'])} 个未恢复 C。\n\n"
        body += '上游汇编源码匹配单独记录于 [覆盖证据](native-edge-cases.md)，不计入本页的 C 恢复数。\n\n'
        body += '| 地址 | 函数 | 方式 | 伪代码 |\n|---|---|---|---|\n'
        for r in report['recovered']:
            link = quote(f"generated/native/{module['output']}/{r['file']}")
            body += f"| {r['address']} | {r['name']} | {r['mode']} | [C]({link}) |\n"
        if report['unresolved']:
            body += '\n剩余：\n\n'
            for r in report['unresolved']:
                body += f"- `{r['address']}` `{r['name']}`：{r['status']}。\n"
        body += '\n'
    (KB / 'native-recovery.md').write_text(body, encoding='utf-8')
    print('Recovery verified:', [(m['path'], len(r['recovered']), len(r['unresolved'])) for m, r in summaries])


if __name__ == '__main__':
    main()
