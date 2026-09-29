"""按风险分类原生 C 中的 Ghidra 警告，防止把输出成功误认为功能完整恢复。"""
from collections import Counter
import json
from pathlib import Path
import re
from dsp_knowledge import GENERATED, KB, digest, read_json
from dsp_native_select import selected_folder

CATEGORIES = {
    'jump-table': 'Could not recover jumptable',
    'indirect-jump-as-call': 'Treating indirect jump as call',
    'bad-instruction': 'bad instruction',
    'unreachable-block': 'Removing unreachable block',
    'type-propagation': 'Type propagation',
    'overlapping-globals': 'Globals starting',
    'enum-names': 'Enum ',
}


def main():
    base = GENERATED / 'native'
    inventory = read_json(base / 'inventory.json')
    records = []
    modules = []
    totals = Counter()
    for module in inventory['files']:
        folder = selected_folder(base, module)
        manifest = read_json(folder / 'decompile-manifest.json')
        if digest(Path(inventory['game_directory']) / module['path']) != module['sha256'] or manifest['source_sha256'] != module['sha256']:
            raise ValueError('Source changed')
        if digest(folder / 'functions.json') != manifest['files']['functions.json']:
            raise ValueError('Function index changed')
        report = read_json(folder / 'functions.json')
        counts = Counter()
        scanned = 0
        for function in report['functions']:
            if 'file' not in function:
                continue
            path = folder / function['file']
            if digest(path) != manifest['files'][function['file']]:
                raise ValueError('C output changed')
            text = path.read_text(encoding='utf-8')
            scanned += 1
            warnings = [' '.join(m.split()) for m in re.findall(r'/\*\s*WARNING:\s*(.*?)\*/', text, re.S)]
            if not warnings:
                continue
            kinds = set()
            for warning in warnings:
                categories = {k for k, token in CATEGORIES.items() if token.lower() in warning.lower()}
                kinds.update(categories or {'other'})
            counts.update(kinds)
            records.append(dict(module=module['path'], address=function['address'], name=function['name'],
                                thunk=function['thunk'], body_bytes=function['size'], categories=sorted(kinds),
                                file=path.relative_to(base).as_posix(), warnings=warnings))
        totals.update(counts)
        modules.append(dict(module=module['path'], scanned=scanned, categories=dict(counts),
                            manifest_sha256=digest(folder / 'decompile-manifest.json')))
        print(module['path'], scanned, dict(counts), flush=True)
    result = dict(scope='selected baseline C outputs; recovery outputs excluded', modules=modules, functions=records)
    (base / 'quality-audit.json').write_text(json.dumps(result, ensure_ascii=True, indent=2), encoding='utf-8')
    body = '# 原生反编译警告分类\n\n'
    body += f"对选中的基础 C 输出逐文件核对哈希并扫描，共 {sum(m['scanned'] for m in modules):,} 个函数；补充恢复单独报告。以下是带该类警告的函数数，一个函数可同时计入多类。\n\n"
    body += '| 类别 | 函数数 |\n|---|---:|\n'
    for kind, count in totals.most_common():
        body += f'| {kind} | {count:,} |\n'
    body += '\n`jump-table` 与 `indirect-jump-as-call` 需要结合汇编区分未恢复 switch 与正常间接尾调用；不能把每条警告都视为真实缺失，也不能忽略。`bad-instruction` 需核查指令边界和代码/数据判断。枚举重名、全局符号重叠、不可达块等单独列出，不等同于丢失方法。\n\n'
    body += '完整地址、名称、原始警告和 C 路径在 generated/native/quality-audit.json。运行 `python -X utf8 tools/dsp_native_quality.py` 重建。该审计检出风险线索，不证明没有警告的函数必然完整，也不把数值归零作为替代目标。\n'
    body += '\n20 个 bad-instruction 函数的边界证据及独立修正见 [控制流定点核验](native-flow-repairs.md)；修正保留在旁路输出，本页仍统计基础导出。\n'
    (KB / 'native-quality.md').write_text(body, encoding='utf-8')


if __name__ == '__main__':
    main()
