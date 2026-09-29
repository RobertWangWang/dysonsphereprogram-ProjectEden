"""核验已经结束的原生导出；未结束模块明确列为待完成。"""
from dsp_knowledge import KB, GENERATED, digest, read_json
from pathlib import Path
from dsp_native_select import selected_folder
from dsp_native_source_match import validate_cached


def main():
    base = GENERATED / 'native'
    inventory = read_json(base / 'inventory.json')
    rows = []
    finished = total = failed = warning_functions = recovered_total = 0
    source_matched = 0
    for record in inventory['files']:
        if digest(Path(inventory['game_directory']) / record['path']) != record['sha256']:
            raise ValueError('本机原生文件已变化：' + record['path'])
        folder = selected_folder(base, record)
        marker = folder / 'decompile-manifest.json'
        if not marker.exists():
            rows.append(f"| {record['path']} | 尚无完成标记 | — | — | — |")
            continue
        manifest = read_json(marker)
        if manifest['source_sha256'] != record['sha256']:
            raise ValueError('源清单已变化：' + record['path'])
        for filename, sha in manifest['files'].items():
            if digest(folder / filename) != sha:
                raise ValueError('导出文件校验失败：' + filename)
        functions = read_json(folder / 'functions.json')
        original_failed = {f['address'] for f in functions['functions'] if f['status'] in ('failed', 'timeout')}
        recovered = set()
        recovery_path = folder / 'recovery-manifest.json'
        if recovery_path.exists():
            recovery = read_json(recovery_path)
            if recovery['source_sha256'] != record['sha256']:
                raise ValueError('补充恢复来源不一致')
            for filename, sha in recovery['files'].items():
                if digest(folder / filename) != sha:
                    raise ValueError('补充恢复文件校验失败：' + filename)
            original_failed = {f['address'] for f in functions['functions'] if f['status'] in ('failed', 'timeout')}
            recovered = {f['address'] for f in recovery['recovered']}
            if not recovered <= original_failed:
                raise ValueError('补充恢复地址与原失败列表不一致')
            recovered_total += len(recovered)
        source = validate_cached(folder, Path(inventory['game_directory']) / record['path'])
        if source:
            if source['address'] not in original_failed - recovered:
                raise ValueError('源码匹配必须对应尚未恢复 C 的失败函数')
            source_matched += 1
        warnings = sum('WARNING:' in (folder / f['file']).read_text(encoding='utf-8') for f in functions['functions'] if 'file' in f)
        finished += 1
        total += functions['decompiled']
        failed += functions['failed']
        warning_functions += warnings
        rows.append(f"| {record['path']} | 已导出并校验 | {functions['decompiled']:,} / {functions['discovered']:,} | {functions['failed']} | {warnings} |")
    body = '# 原生函数导出进度与质量\n\n'
    body += f'安装清单中的 {finished} / {len(inventory["files"])} 个模块完成逐函数导出，已保存 {total:,} 个函数伪代码，失败 {failed} 个；其中 {warning_functions:,} 个函数含 Ghidra WARNING。\n\n'
    body += f'上面及下表为基础导出数；[独立补充恢复](native-recovery.md) 已核验 {recovered_total} 个原失败函数，合计可查询 {total + recovered_total:,} 个函数伪代码，原失败列表仍有 {failed - recovered_total} 个未恢复 C。恢复方式和原始签名单独保留。\n\n'
    body += f'这些未恢复 C 的函数中，另有 {source_matched} 个通过重新汇编、重定位和完整机器码及常量比较，已匹配上游汇编源码；余下 {failed - recovered_total - source_matched} 个仍待恢复。源码匹配不增加 C 计数，详见 [覆盖证据](native-edge-cases.md)。全库警告另见 [质量审计](native-quality.md)。\n\n'
    body += '这是已识别函数的导出结果，不保证发现了二进制中的每段代码，也不把“返回伪代码”视为控制流/类型恢复正确的证明。跳转表恢复、间接调用等警告要结合原始反汇编核验。无完成标记只表示没有可核验的完整输出，不能单凭标记缺失判断后台进程已停止。\n\n'
    body += '| 模块 | 状态 | 伪代码 / 识别函数 | 失败 | 含警告函数 |\n|---|---|---:|---:|---:|\n'
    body += '\n'.join(rows) + '\n'
    (KB / 'native-progress.md').write_text(body, encoding='utf-8')
    print(f'已核验 {finished}/{len(inventory["files"])} 个模块，{total} 个函数，{failed} 个失败，{warning_functions} 个函数含警告。')


if __name__ == '__main__':
    main()
