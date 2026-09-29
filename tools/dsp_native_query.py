"""按函数名或地址查询原生 C 或精确匹配的汇编源码；优先使用已核验结果。"""
import argparse
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_select import selected_folder
from dsp_native_source_match import validate_cached
from dsp_native_quality_repair import verify as verify_quality_repair, PROFILES
from dsp_native_fma4 import validate_cached as validate_fma4
from dsp_native_fma4_candidate import validate_cached as validate_fma4_candidate
from dsp_native_multistage_repair import verify as verify_multistage_repair
from dsp_native_copy_probe import validate_cached as validate_copy_probe
from dsp_native_seh_probe import validate_cached as validate_seh_probe
from dsp_native_fma4_roles import validate_cached as validate_fma4_roles
from dsp_native_fma4_cpu_probe import validate_cached as validate_fma4_cpu
from dsp_native_fma4_environment import validate_cached as validate_fma4_environment
from dsp_native_fma4_startup import validate_cached as validate_fma4_startup
from dsp_native_fma4_gate_flow import validate_gate_cached
from dsp_native_irradiance_hash import validate_cached as validate_irradiance_hash
from dsp_native_irradiance_cache import validate_cached as validate_irradiance_cache
from dsp_native_crt_callbacks import validate_cached as validate_crt_callbacks
from dsp_native_crt_edges import validate_cached as validate_crt_edges
from dsp_native_texture_switch import validate_cached as validate_texture_switch
from dsp_native_float_readers import validate_cached as validate_float_readers
from dsp_native_pointer_edges import validate_edge_cached
from dsp_native_wgl_initialization import validate_cached as validate_wgl_initialization
from dsp_native_rail_ssl_switch import validate_cached as validate_rail_ssl_switch
from dsp_native_relocation_operands import validate_cached as validate_relocation_operands
from dsp_native_nonadjacent_tables import validate_cached as validate_nonadjacent_tables
from dsp_native_scanner_body import validate_cached as validate_scanner_body
from dsp_native_skip_body import validate_cached as validate_skip_body
from dsp_native_continuation_bodies import validate_cached as validate_continuation_bodies
from dsp_native_continuation_behavior import validate_cached as validate_continuation_behavior
from dsp_native_memory_command import validate_cached as validate_memory_command
from dsp_native_buffer_sync import validate_cached as validate_buffer_sync
from dsp_native_buffer_cleanup import validate_cached as validate_buffer_cleanup
from dsp_native_heap_free import validate_cached as validate_heap_free
from dsp_native_allocator_dispatch import validate_cached as validate_allocator_dispatch
from dsp_native_allocator_types import validate_cached as validate_allocator_types
from dsp_native_abort_flow import validate_cached as validate_abort_flow
from dsp_native_known_boundaries import validate_cached as validate_known_boundaries
from dsp_native_complex_boundaries import validate_cached as validate_complex_boundaries
from dsp_native_frame_references import validate_cached as validate_frame_references
from dsp_native_supplement_context import validate_cached as validate_supplement_context
from dsp_native_encoded_callback import validate_cached as validate_encoded_callback
from dsp_native_callback_tail import validate_cached as validate_callback_tail
from dsp_native_signal_registration import validate_cached as validate_signal_registration
from dsp_native_signal_failure import validate_cached as validate_signal_failure
from dsp_native_signal_thread import validate_cached as validate_signal_thread
from dsp_native_signal_thread import validate_copy_cached as validate_signal_thread_copy
from dsp_native_copy_pair import validate_cached as validate_copy_pair
from dsp_native_copy_types import validate_cached as validate_copy_types
from dsp_native_malloc_base import validate_cached as validate_malloc_base
from dsp_native_new_handler import validate_cached as validate_new_handler
from dsp_native_new_handler import validate_query_cached as validate_new_handler_query
from dsp_native_new_handler_state import validate_cached as validate_new_handler_state
from dsp_native_crt_locks import validate_cached as validate_crt_locks
from dsp_native_critical_section_compat import validate_cached as validate_critical_section_compat
from dsp_native_function_cache import validate_cached as validate_function_cache
from dsp_native_module_cache import validate_cached as validate_module_cache
from dsp_native_lock_pipeline import validate_cached as validate_lock_pipeline
from dsp_native_runtime_types import validate_cached as validate_runtime_types
from dsp_native_mono_dispatch import validate_cached as validate_mono_dispatch
from dsp_native_mono_body import validate_cached as validate_mono_body
from dsp_native_mono_assertion import validate_cached as validate_mono_assertion
from dsp_native_mono_continuations import validate_cached as validate_mono_continuations
from dsp_native_mono_lines import validate_cached as validate_mono_lines
from dsp_native_mono_source_candidate import validate_cached as validate_mono_source_candidate
from dsp_native_mono_cases import validate_cached as validate_mono_cases
from dsp_native_mono_cases import validate_nested_cached as validate_mono_type_cases
from dsp_native_mono_constants import validate_cached as validate_mono_constants
from dsp_native_mono_integer_ir import validate_cached as validate_mono_integer_ir
from dsp_native_malloc_base import validate_chain_cached as validate_malloc_chain
from dsp_native_security_failure import validate_cached as validate_security_failure
from dsp_native_security_failure import validate_compat_cached as validate_security_compat
from dsp_native_classifier_probe import validate_cached as validate_classifier_probe
from dsp_native_scanner_reference import validate_cached as validate_scanner_reference


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('query')
    parser.add_argument('--module', help='模块文件名，例如 mono-2.0-bdwgc.dll')
    parser.add_argument('--limit', type=int, default=20)
    parser.add_argument('--show', action='store_true', help='显示 C 正文或匹配的上游汇编文件')
    parser.add_argument('--assembly', action='store_true', help='优先显示已核验的完整 FMA4 反汇编补充；配合 --show 输出正文')
    parser.add_argument('--fma4-candidate', action='store_true', help='显示含不透明 FMA4 操作的实验 C 候选，不代表语义恢复完成')
    parser.add_argument('--scanner-reference', action='store_true', help='显示经过整段差分测试的手工扫描器重建 C，非原始 Ghidra 输出')
    args = parser.parse_args()
    if args.assembly and args.fma4_candidate:
        parser.error('--assembly 与 --fma4-candidate 不能同时使用')
    if args.limit < 1:
        parser.error('--limit 必须大于零')
    base = GENERATED / 'native'
    inventory = read_json(base / 'inventory.json')
    found = 0
    for module in inventory['files']:
        if args.module and Path(module['path']).name.lower() != args.module.lower():
            continue
        folder = selected_folder(base, module)
        if digest(Path(inventory['game_directory']) / module['path']) != module['sha256']:
            raise ValueError('Game binary changed')
        manifest = read_json(folder / 'decompile-manifest.json')
        if manifest['source_sha256'] != module['sha256'] or digest(folder / 'functions.json') != manifest['files']['functions.json']:
            raise ValueError('Function index hash mismatch')
        report = read_json(folder / 'functions.json')
        crt_callbacks = validate_crt_callbacks(folder, module['sha256'])
        crt_callbacks += validate_crt_callbacks(folder, module['sha256'], kind='exit-callbacks')
        crt_callbacks += validate_crt_callbacks(folder, module['sha256'], kind='reader-table-callbacks')
        crt_callbacks += validate_crt_callbacks(folder, module['sha256'], kind='pointer-callbacks')
        crt_callbacks += validate_crt_callbacks(folder, module['sha256'], kind='direct-callbacks')
        crt_callbacks += validate_crt_callbacks(folder, module['sha256'], kind='relocation-callbacks')
        crt_callbacks += validate_crt_callbacks(folder, module['sha256'], kind='relocation-retry')
        crt_callbacks += validate_crt_callbacks(folder, module['sha256'], kind='flow-callbacks')
        for flow_marker in sorted(folder.glob('flow-callbacks-*/manifest.json')):
            flow_kind=flow_marker.parent.name;number=flow_kind.removeprefix('flow-callbacks-')
            if number.isdigit() and int(number)>=2:crt_callbacks+=validate_crt_callbacks(folder,module['sha256'],kind=flow_kind)
        crt_edges = validate_crt_edges(folder, module['sha256'])
        exit_edges = validate_crt_edges(folder, module['sha256'], kind='exit-edges')
        exit_wrappers = {r['entry']: r for r in exit_edges.get('import_wrappers', [])}
        float_readers=validate_float_readers(folder,module['sha256'])
        pointer_edges=validate_edge_cached(folder,module['sha256'])
        wgl_initialization=validate_wgl_initialization(folder,module['sha256'])
        rail_ssl=validate_rail_ssl_switch(folder,module['sha256'])
        operand_audit=validate_relocation_operands(folder,module['sha256'])
        quarantined={r['address'] for r in operand_audit.get('quarantined_entries',[])}
        crt_callbacks=[r for r in crt_callbacks if r['address'] not in quarantined]
        nonadjacent=validate_nonadjacent_tables(folder,module['sha256'])
        scanner=validate_scanner_body(folder,module['sha256'])
        skip_body=validate_skip_body(folder,module['sha256'])
        continuation_bodies=validate_continuation_bodies(folder,module['sha256'])
        continuation_behavior=validate_continuation_behavior(folder,module['sha256'])
        memory_command=validate_memory_command(folder,module['sha256'])
        buffer_sync=validate_buffer_sync(folder,module['sha256'])
        buffer_cleanup=validate_buffer_cleanup(folder,module['sha256'])
        heap_free=validate_heap_free(folder,module['sha256'])
        allocator_dispatch=validate_allocator_dispatch(folder,module['sha256'])
        allocator_types=validate_allocator_types(folder,module['sha256'])
        abort_flow=validate_abort_flow(folder,module['sha256'])
        known_boundaries=validate_known_boundaries(folder,module['sha256'])
        complex_boundaries=validate_complex_boundaries(folder,module['sha256'])
        frame_references=validate_frame_references(folder,module['sha256'])
        supplement_context=validate_supplement_context(folder,module['sha256'])
        encoded_callback=validate_encoded_callback(folder,module['sha256'])
        callback_tail=validate_callback_tail(folder,module['sha256'])
        signal_registration=validate_signal_registration(folder,module['sha256'])
        signal_failure=validate_signal_failure(folder,module['sha256'])
        signal_thread=validate_signal_thread(folder,module['sha256'])
        signal_thread_copy=validate_signal_thread_copy(folder,module['sha256'])
        copy_pair=validate_copy_pair(folder,module['sha256'])
        copy_types=validate_copy_types(folder,module['sha256'])
        malloc_base=validate_malloc_base(folder,module['sha256'])
        new_handler=validate_new_handler(folder,module['sha256'])
        new_handler_query=validate_new_handler_query(folder,module['sha256'])
        new_handler_state=validate_new_handler_state(folder,module['sha256'])
        crt_locks=validate_crt_locks(folder,module['sha256'])
        critical_section_compat=validate_critical_section_compat(folder,module['sha256'])
        function_cache=validate_function_cache(folder,module['sha256'])
        module_cache=validate_module_cache(folder,module['sha256'])
        lock_pipeline=validate_lock_pipeline(folder,module['sha256'])
        runtime_types=validate_runtime_types(folder,module['sha256'])
        mono_dispatch=validate_mono_dispatch(folder,module['sha256'])
        mono_body=validate_mono_body(folder,module['sha256'])
        mono_assertion=validate_mono_assertion(folder,module['sha256'])
        mono_continuations=validate_mono_continuations(folder,module['sha256'])
        mono_lines=validate_mono_lines(folder,module['sha256'])
        mono_source_candidate=validate_mono_source_candidate(folder,module['sha256'])
        mono_cases=validate_mono_cases(folder,module['sha256'])
        mono_type_cases=validate_mono_type_cases(folder,module['sha256'])
        mono_constants=validate_mono_constants(folder,module['sha256'])
        mono_integer_ir=validate_mono_integer_ir(folder,module['sha256'])
        malloc_chain=validate_malloc_chain(folder,module['sha256'])
        security_failure=validate_security_failure(folder,module['sha256'])
        security_compat=validate_security_compat(folder,module['sha256'])
        classifier=validate_classifier_probe(folder,module['sha256'])
        scanner_reference=validate_scanner_reference(folder,module['sha256'])
        nonadjacent_tables={r['address']:r for r in nonadjacent.get('tables',[])}
        operand_by_owner={}
        for row in operand_audit.get('entries',[]):
            if row.get('owner'):operand_by_owner.setdefault(row['owner'],[]).append(row)
        crt_hashes = {}
        if crt_callbacks:
            known = {f['address'] for f in report['functions']}
            if known.intersection(f['address'] for f in crt_callbacks):
                raise ValueError('CRT supplement overlaps base function index')
            if len({f['address'] for f in crt_callbacks})!=len(crt_callbacks):
                raise ValueError('Supplement entry sets overlap')
            report['functions'].extend(crt_callbacks)
            for kind in {f['callback_kind'] for f in crt_callbacks}:
                crt_hashes.update({kind + '/' + name: sha for name, sha in read_json(folder / kind / 'manifest.json')['files'].items()})
        unwind_records = {}
        unwind_root = folder / 'unwind-coverage'
        if (unwind_root / 'manifest.json').exists():
            unwind_marker = read_json(unwind_root / 'manifest.json')
            if unwind_marker['source_sha256'] != module['sha256']:
                raise ValueError('Unwind coverage baseline changed')
            for name, sha in unwind_marker['files'].items():
                if digest(unwind_root / name) != sha:
                    raise ValueError('Unwind coverage artifact changed')
            unwind_report = read_json(unwind_root / 'report.json')
            for name, source_family in unwind_report['source_families'].items():
                if digest(folder / 'upstream-openssl' / name / 'manifest.json') != source_family['manifest_sha256']:
                    raise ValueError('Unwind coverage family evidence changed')
            unwind_records = {r['address']: r for r in unwind_report['routines']}
        fma4_records = validate_fma4(folder, module['sha256'])
        fma4_roles = validate_fma4_roles(folder, module['sha256'])
        fma4_cpu = validate_fma4_cpu(folder, module['sha256'])
        fma4_environment = validate_fma4_environment(folder, module['sha256'])
        fma4_startup = validate_fma4_startup(folder, module['sha256'])
        fma4_gate_flow = validate_gate_cached(folder, module['sha256'])
        irradiance_hash = validate_irradiance_hash(folder, module['sha256'])
        irradiance_cache = validate_irradiance_cache(folder, module['sha256'])
        copy_probe = validate_copy_probe(folder)
        seh_probe = validate_seh_probe(folder, module['sha256'])
        recovery_path = folder / 'recovery-manifest.json'
        recovery = read_json(recovery_path) if recovery_path.exists() else {}
        recovered = {r['address']: r for r in recovery.get('recovered', [])}
        if recovered and (recovery['source_sha256'] != module['sha256'] or recovery['files']['functions.json'] != digest(folder / 'functions.json')):
            raise ValueError('Recovery baseline mismatch')
        source_path = folder / 'source-match/report.json'
        source = validate_cached(folder, Path(inventory['game_directory']) / module['path'])
        quality_results = {a: r for a in PROFILES if (r := verify_quality_repair(folder, Path(inventory['game_directory']) / module['path'], address=a))}
        texture_switch=validate_texture_switch(folder,module['sha256'])
        if texture_switch:
            quality_results[texture_switch['address']]={**texture_switch,'mode':'verified-switch-repair','case_count':texture_switch['cases'],'file':'quality-repair/'+texture_switch['address']+'/'+texture_switch['file']}
        multistage = verify_multistage_repair(folder, Path(inventory['game_directory']) / module['path'])
        if multistage:
            quality_results[multistage['address']] = multistage
        openssl_records = []
        for upstream_path in sorted((folder / 'upstream-openssl').glob('*/manifest.json')):
            openssl_root = upstream_path.parent
            upstream_manifest = read_json(upstream_path)
            if upstream_manifest['source_sha256'] != module['sha256']:
                raise ValueError('OpenSSL baseline mismatch')
            for name, sha in upstream_manifest['files'].items():
                if digest(openssl_root / name) != sha:
                    raise ValueError('OpenSSL reference artifact changed')
            openssl = read_json(openssl_root / 'report.json')
            if openssl['status'] not in ('normalized-source-matched','byte-exact-source-matched') or openssl['source_sha256'] != module['sha256']:
                raise ValueError('OpenSSL reference not verified')
            openssl_records.append((openssl_root, openssl))
        superseded = {name for _, record in openssl_records for name in record.get('supersedes', [])}
        expanded_records = []
        for root, record in openssl_records:
            if root.name in superseded:
                continue
            if 'routines' in record:
                for routine in record['routines']:
                    expanded_records.append((root, {**record, 'name': routine['name'], 'address': routine['address'],
                                                    'body_entry': routine['body_entry'], 'query_routine': routine}))
            else:
                expanded_records.append((root, record))
        openssl_records = expanded_records
        indexed_addresses = {function['address'] for function in report['functions']}
        # Verified assembly-only entries may have no Ghidra function at either address.
        # Add query rows in memory; do not alter the C index or its completion count.
        query_functions = list(report['functions'])
        for _, record in openssl_records:
            if not indexed_addresses.intersection((record['address'], record['body_entry'])):
                query_functions.append({'address': record['address'], 'name': record['name']})
                indexed_addresses.add(record['address'])
        for function in query_functions:
            matched = source if source.get('address') == function['address'] else {}
            openssl_root, openssl = next(((r, o) for r, o in openssl_records if function['address'] in (o['address'], o['body_entry'])), (None, {}))
            alias_address = openssl.get('address')
            if alias_address not in indexed_addresses:
                alias_address = openssl.get('body_entry')
            alias = (' '.join([openssl['name'], openssl['address'], *openssl.get('aliases', [])])) if alias_address == function['address'] else ''
            ssl_alias=' '.join(rail_ssl['labels']) if rail_ssl and function['address']==rail_ssl['address'] else ''
            operand_rows=operand_by_owner.get(function['address'],[])
            operand_alias=' '.join(row['target']+' '+row.get('byte_index_table','') for row in operand_rows)
            if scanner and function['address']==scanner['address']:operand_alias+=' 10632224 10632250 10632264'
            if args.query.lower() not in (function['name'] + ' ' + function['address'] + ' ' + matched.get('name', '') + ' ' + alias + ' ' + ssl_alias + ' ' + operand_alias).lower():
                continue
            found += 1
            if found > args.limit:
                continue
            if classifier and function['address'] in ('10632090','106392d0'):
                print(f"  分类辅助函数 106392d0：{classifier['cases']} 个双字节组合已核验；仅返回 EAX，保留 ECX/EDX，无内存写入。当前扫描 C 的 extraout/拼接返回推断仍待修正。")
            for row in operand_rows:
                bound=f"索引 0..{row['bound']} 的映射及越界分派已逐项模拟" if 'bound' in row else '尚未确认紧邻索引边界'
                if row['target'] in nonadjacent_tables:
                    extra=nonadjacent_tables[row['target']]
                    bound=f"已补充非紧邻边界/选择器证据：{extra['index_domain']}，{extra['cases']} 组测试；中间外部调用不在证明范围内"
                print(f"  重定位操作数补充引用：{row['site']} -> 数据表 {row['target']}，{bound}；不代表目标函数语义恢复。")
                if args.query.lower() in quarantined:print('  此查询地址是已验证的表数据；历史误识别 C 已隔离，此处显示实际使用该表的函数。')
                if row.get('provenance')=='verified-direct-edge-anchored-decode' and not scanner:
                    print('  此引用来自已验证直接跳转后的补充解码；10632090 原导出函数体仅 43 字节，完整函数范围仍待修复，不能将 C 正文视为已全量覆盖。')
            for supplemental_context in supplement_context.get('entries',[]):
                if function['address'] in (supplemental_context['source'],supplemental_context['target'],supplemental_context['supplement_owner']):
                    print(f"  补充上下文：{supplemental_context['source']} -> {supplemental_context['target']} 的调用点 {supplemental_context['call_site']} 位于已核验补充体 {supplemental_context['supplement_owner']}；不等于源码唯一归属证明。")
            if encoded_callback and function['address']==encoded_callback['address']:
                print(f"  编码回调片段：{encoded_callback['cases']} 组测试覆盖 {encoded_callback['instructions']} 条原始指令；ROR32(encoded XOR cookie, cookie&31) 解码，普通回调槽先改为编码空值，0/1 哨兵保留。锁、异常与真实回调未执行。")
            if callback_tail and function['address']==callback_tail['address']:
                print(f"  回调执行尾部：{callback_tail['cases']} 组测试覆盖 {callback_tail['instructions']} 条原始指令；0/1 跳过回调，普通回调传入一个 EDI 参数并忽略返回值；到异常尾声前 EAX=(decoded!=0)。guard/回调仅模拟返回，尚非完整函数 ABI 证明。")
            if signal_registration and function['address'] in signal_registration['addresses']:
                print(f"  信号注册核心：选择器 {signal_registration['selector_cases']} 组、编码注册 {signal_registration['registration_cases']} 组原始指令测试；新值 2 只查询，其他值编码写入，6/22 共用槽位。1060ef27 为控制台注册回调；真实 API 与异常路径未执行。")
            if signal_failure and function['address'] in signal_failure['addresses']:
                print(f"  信号注册失败路径：{signal_failure['path_cases']} 组路径和 {signal_failure['helper_cases']} 组错误辅助函数测试；模拟控制台注册失败后仍写槽（查询值 2 除外），保存系统错误，解锁后设 errno=22，尾声前 EAX=FFFFFFFF。OS/锁/TLS 为返回模型，异常路径未执行。")
            if signal_thread and function['address'] in signal_thread['addresses']:
                print(f"  线程信号表：{signal_thread['cases']} 组测试覆盖 {signal_thread['instructions']} 条指令；原表 12 项/144 字节，首次查询也先复制，分配失败写空表指针，更新仅覆盖首个连续匹配组。TLS/分配/复制为边界模型。")
            if signal_thread_copy and function['address'] in signal_thread_copy['addresses']:
                print(f"  原始复制集成：{signal_thread_copy['cases']} 组线程表测试直接执行 105fafc0，覆盖复制体 {signal_thread_copy['copy_instructions_visited']} 条不同指令；TLS/分配仍为模型，限非重叠小表，非整个复制函数证明。")
            if copy_pair and function['address'] in copy_pair['addresses']:
                print(f"  双复制体核验：12 处内部绝对地址归一化后 1396 字节一致；合计 {copy_pair['cases']} 次原始执行通过重叠/对齐/长度边界测试，每体访问 {copy_pair['instructions_visited_each']} 条指令。EAX 返回目标地址；有限有效缓冲区测试，非全域语义证明。")
            if malloc_base and function['address'] in malloc_base['addresses']:
                print(f"  malloc 核心：{malloc_base['cases']} 组测试覆盖 {malloc_base['instructions']} 条指令；零长度改 1，超过 FFFFFFE0 直接失败，失败重读模式并按 new-handler 结果重试，最终 errno=12。HeapAlloc/处理器/TLS 提供器为模型。")
            if new_handler and function['address'] in new_handler['addresses']:
                print(f"  new-handler 调用：{new_handler['cases']} 组测试覆盖 {new_handler['instructions']} 条指令；回调接收 size，非零结果转 1，空回调返回 0；cookie 成功检查保留 EAX，损坏时转失败入口。查询/用户回调为模型，原 C 的 cookie 返回推断不准确。")
            if new_handler_state and function['address'] in new_handler_state['addresses']:
                print(f"  new-handler 状态：{new_handler_state['cases']} 组测试覆盖 {new_handler_state['instructions']} 条指令，包含真实正常 SEH 前后序言；查询/设置均锁 0，返回旧解码值，设置更新编码槽。模拟 FS 环境，锁和异常展开仍未执行。")
            if new_handler_query and function['address'] in new_handler_query['addresses']:
                print(f"  查询/调用联调：{new_handler_query['cases']} 组测试执行 {new_handler_query['instructions']} 条原始指令；真实查询解码、正常 SEH 与调用包装连通，回调前已解锁并恢复异常链。锁/用户回调仍为模型。")
            if crt_locks and function['address'] in crt_locks['addresses']:
                print(f"  CRT 锁：{crt_locks['wrapper_cases']+crt_locks['lifecycle_cases']+crt_locks['cleanup_cases']} 组测试覆盖 {crt_locks['instructions']} 条指令；13 槽、每槽 24 字节，初始化失败逆序清理；初始化/清理结果在 AL。系统 API 为模型，并发未验证。")
            if critical_section_compat and function['address'] in critical_section_compat['addresses']:
                print(f"  临界区兼容：{critical_section_compat['cases']} 组测试覆盖 {critical_section_compat['instructions']} 条指令；Ex 不存在才回退，旧接口省略 flags，保留 API 的 EAX，stdcall 清理 12 字节。原 C 的 void 推断不准确；解析器/系统 API 为模型。")
            if function_cache and function['address'] in function_cache['addresses']:
                print(f"  函数缓存：{function_cache['cases']} 次测试覆盖 {function_cache['instructions']} 条指令；成功/失败均编码缓存，重复调用不查询；首个可用模块缺失符号时不继续查后续模块。模块查询/GetProcAddress 为模型，并发未验证。")
            if module_cache and function['address'] in module_cache['addresses']:
                print(f"  模块缓存：{module_cache['cases']} 次测试覆盖 {module_cache['instructions']} 条指令；加载标志 0x800，仅错误 87 改用零标志重试；句柄/失败哨兵缓存，交换遇旧非零值释放本次句柄。系统 API 为模型，真实引用计数与并发未验证。")
            if lock_pipeline and function['address'] in lock_pipeline['addresses']:
                print(f"  锁初始化整链：{lock_pipeline['cases']} 次初始化测试连接真实兼容分支及双层缓存，执行 {lock_pipeline['instructions_visited']}/{lock_pipeline['instructions']} 条指令；仅 Windows API 使用返回模型。冷/热缓存及逆序回滚通过，未覆盖路径见报告。")
            for frame_context in frame_references.get('entries',[]):
                if function['address'] in (frame_context['source'],frame_context['target']):
                    owners=frame_context['related_functions']
                    print(f"  栈帧上下文引用：{frame_context['source']} -> {frame_context['target']}；已核对 {len(frame_context['source_references'])} 条来源引用、{len(frame_context['target_references'])} 条后续引用。")
                    print('  关联原索引函数：'+(', '.join(owners[:8])+(' 等' if len(owners)>8 else '') if owners else '尚未找到')+'；关联不等于唯一归属，CALL/JMP 按原机器指令核定。')
            for context in complex_boundaries.get('entries',[]):
                if function['address'] in (context['source'],context['target']):
                    print(f"  原始引用上下文：{context['source']} -> {context['target']}，{context['category']}；相关原索引函数：{', '.join(context['related_functions'])}。")
                    print('  引用中的 CALL/JMP 以原机器指令为准；关联函数不等于已证明唯一归属。')
                    if context['category']=='frame-dependent-called-tail':print(f"  连续 30 字节片段已通过 {complex_boundaries['fragment']['cases']} 组测试，依赖传入 EBP 和 EAX 高位；条件辅助调用使用返回模型。")
            for boundary in known_boundaries.get('entries',[]):
                if function['address']==boundary['source']:
                    print(f"  函数体边界：{boundary['source']} 顺序进入已索引入口 {boundary['target']}；分类 {boundary['category']}。")
                    if boundary['category']=='inherited-frame-prefix':print('  此片段直接依赖传入 EBP 栈帧，未自行建立 EBP；不能按独立普通 C 函数直接调用，所属函数及异常上下文仍待恢复。')
                    elif boundary['category']=='verified-terminal-call':print('  边界前调用已有局部终止证据，普通顺序继续只是假设调用返回；异常恢复仍未证明。')
                    else:print('  目标已导出不等于边界正确；上下文和共享代码归属仍需检查。')
                    print('  后续代码：'+str(folder/boundary['target_file']))
            if abort_flow and function['address'] in {abort_flow['abort'],abort_flow['parent'],*[r['source'] for r in abort_flow['historical_edges']]}:
                print(f"  终止路径：{abort_flow['cases']} 组原始局部指令测试覆盖全部 {abort_flow['instructions']} 条 abort 指令；外部调用返回时也进入 INT29/INT3。不证明异常恢复或真实系统终止，不扩展陷阱后的函数体。")
            if buffer_sync and function['address'] in ('104c2d30','104c30c0','105f9bf0','105fb540'):
                print(f"  缓冲区同步与清零联调：{buffer_sync['total_cases']} 组原始指令测试，无外部函数桩；覆盖同步辅助函数全部 {buffer_sync['helper_instructions']} 条指令。分配器、真实用户回调与 CPU 配置来源仍待核查。")
            if buffer_cleanup and function['address'] in ('104c2d30','104c3040','104c5fc0','104c6090','10529120','104f52d0','10001360'):
                print(f"  释放链联调：{buffer_cleanup['total_cases']} 组测试覆盖六个辅助函数全部 144 条非填充指令；真实清零及回调分派已执行，分配器/自定义回调边界仅记录释放事件并返回。")
            if heap_free and function['address'] in ('10603677','10613bf7','10603a33','10603aac','104c5fc0'):
                print(f"  CRT 释放错误路径：{heap_free['translation_cases']} 组错误码映射、{heap_free['free_cases']} 组释放分支测试通过；原始 errno 选择和写入已验证，操作系统堆与线程状态提供者使用显式模型。")
            if allocator_dispatch and function['address'] in ('104c5f90','104c5fe0','104c61d0'):
                print(f"  分配回调分派：{allocator_dispatch['total_cases']} 组测试覆盖全部 85 条指令；自定义回调优先于零长度判断。默认分配/重分配的非零请求清零 10e1d018；堆和用户回调仍使用模型。")
                if function['address']=='104c5f90' and not allocator_types:print('  原始 Ghidra C 的 void 返回推断不准确：EAX 返回默认分配器或回调的结果，零长度默认路径返回 0；类型恢复仍需结合此证据。')
            allocator_type=next((r for r in allocator_types.get('functions',[]) if r['address']==function['address']),None)
            runtime_type=next((r for r in runtime_types.get('functions',[]) if r['address']==function['address']),None)
            if mono_lines and function['address']=='1802b5740':
                if mono_integer_ir:
                    print(f"  整数 IR 分支：{mono_integer_ir['cases']} 组模拟覆盖七段共 {mono_integer_ir['instructions']} 条指令，验证节点字段、空/非空链表连接及求值栈推进；内存池和虚拟寄存器分配为模型。")
                if mono_constants:
                    print(f"  常量读取片段：{mono_constants['cases']} 组模拟验证 9 段、{mono_constants['instructions']} 条原始指令的位宽、符号扩展和 8 字节写入。8/16 位穷举，32/64 位有限边界；不涵盖 IR 分配或 GC 条件。")
                if mono_type_cases:
                    print(f"  嵌套类型分派：{mono_type_cases['matched_inputs']}/{mono_type_cases['inputs']} 项对应 MonoTypeEnum 与候选 ro_type 分支，共 10 组。源码上下文为只读字段常量处理；内部生成行为尚未逐条核验。")
                    print('  '+str(folder/'mono-type-case-comparison/comparisons.md'))
                if mono_cases:
                    print(f"  opcode/case 对照：{mono_cases['matched_inputs']}/{mono_cases['inputs']} 个主表输入与 PDB 枚举、候选源码行内标签对应；仅结构对照，不证明 case 主体或完整源码等价。")
                    print('  '+str(folder/'mono-case-comparison/comparisons.md'))
                print(f"  匹配 PDB：{mono_lines['line_records']} 条行记录、{mono_lines['variable_records']} 条变量记录；全部 {mono_lines['mapped_target_groups']} 个跳转目标分组和 {mono_lines['mapped_instruction_starts']} 个已核验指令起点均有源码行映射。源码正文及版本尚未恢复。")
                print('  分支源码行索引：'+str(folder/'mono-pdb-lines/switch-lines.md'))
                if mono_source_candidate:
                    print('  官方源码比较候选：'+mono_source_candidate['candidate_commit']+'；匹配 PDB 未保存源文件校验值，未证明与本次构建一致，不替代超时结果。')
                    print('  '+str(folder/'mono-source-candidate/method-to-ir.c'))
            if mono_assertion and function['address'] in ('1802b5740','18005c960','18005c8d0'):
                print(f"  Mono 断言边界：{mono_assertion['cases']} 组封装测试与 {mono_assertion['prefix_cases']} 组后继测试通过；日志后调用 RaiseException(E0000001,1,0,0)，返回模型下 RET 可达。六处 xor/test/jne 后继顺序继续；真实异常分派与日志实现尚未执行。")
            if mono_continuations and function['address']=='1802b5740':
                print(f"  条件后继闭包：六处均在 6 字节后接回已知指令；补充 {mono_continuations['new_instructions']} 条、{mono_continuations['new_bytes']} 字节，无新调用或未解析出口。合并汇编含 {mono_continuations['combined_instructions']} 条指令，仍以断言调用返回为前提。")
                if args.assembly:
                    path=folder/'mono-continuation-evidence/combined.asm'
                    print(f"{module['path']} 1802b5740 [conditional-combined-assembly]");print('  '+str(path))
                    if args.show:print(path.read_text(encoding='utf-8'))
                    continue
            if mono_dispatch and function['address']=='1802b5740':
                print(f"  Mono 两级分派：{mono_dispatch['cases']} 组测试覆盖 {mono_dispatch['instructions']} 条原始分派指令；主表输入 0–327，嵌套表输入 2–29。只验证分派，不代表 case 方法体或完整 C 已恢复。")
                print('  分组索引：'+str(folder/'mono-dispatch-behavior/targets.md'))
                if mono_body:
                    print(f"  展开函数体：{mono_body['body_bytes']} 字节、{mono_body['instructions']} 条指令逐项匹配 DLL；静态图发现 {len(mono_body['out_of_body_edges'])} 处断言调用后的体外后继，仍需核验。normalize 导出状态：{mono_body['decompile_status']}，未据此增加 C 恢复数。")
            if malloc_chain and function['address'] in ('10613c79','1060ed90','1060edd4'):
                print(f"  malloc/new-handler 整链：{malloc_chain['cases']} 组测试执行 {malloc_chain['visited_instructions']}/{malloc_chain['instructions']} 条指令，已连接原始处理器查询和 SEH 正常恢复。空处理器结束重试，回调前解锁并恢复异常链；堆、锁、TLS 提供者与用户回调为模型，cookie 失败跳转未覆盖。")
            if security_failure and function['address'] in ('105d2db8','105d2d90','10618a07'):
                print(f"  栈保护失败链：{security_failure['cases']} 组模型测试覆盖全部 {security_failure['instructions']} 条原始指令，核验 INT29/ECX=2、异常记录与终止 API 参数；若终止 API 模型返回，原始 RET 路径可达。真实系统异常处理未执行，不能仅凭 noreturn 标注删除返回分支。")
            if security_compat and function['address']=='10618a07':
                print(f"  兼容初始化到异常报告整链：{security_compat['cases']} 组模型测试覆盖 {security_compat['instructions']} 条指令入口。cookie 匹配时返回初始化 API 结果；不匹配且终止 API 模型返回时，返回终止 API 结果，仍清理 12 字节参数。异常记录 EIP 为 cookie 检查后续地址 10618a63。")
            if runtime_type:
                path=folder/'runtime-type-repair'/runtime_type['file']
                if runtime_type['preferred']:
                    print(f"{module['path']} {function['address']} [verified-runtime-abi-annotation]")
                    print('  '+runtime_type['signature']);print('  '+str(path))
                    print('  参数、返回寄存器与原始函数体已核验；类型来自行为证据，生成 C 尚未做编译等价验证。')
                    if runtime_type['status']=='verified-abi-returning-boundary-model':
                        print('  异常报告使用 int / noreturn=false 分析标注，保留原始 RET 路径；失败分支返回值仅按显式系统 API 返回模型核验，不表示真实进程终止会返回。')
                    if args.show:print(path.read_text(encoding='utf-8'))
                    continue
                print('  ABI 部分恢复：'+runtime_type['signature'])
                print('  候选 C 已恢复正常返回和间接调用参数；extraout_EAX 仅余 cookie 失败分支，异常终止语义尚未核验，暂不作为优先结果：'+str(path))
            if allocator_type:
                path=folder/'allocator-type-repair'/allocator_type['file']
                print(f"{module['path']} {function['address']} [verified-allocator-abi-repair]")
                print('  '+allocator_type['signature']);print('  '+str(path))
                print('  已恢复指针返回、参数与回调类型；原始字节和既有行为证据已核对。保留间接跳转等警告，未做生成 C 的编译等价证明。')
                if args.show:print(path.read_text(encoding='utf-8'))
                continue
            continuation=next((r for r in continuation_bodies.get('functions',[]) if r['address']==function['address']),None)
            copy_type=next((r for r in copy_types.get('functions',[]) if r['address']==function['address']),None)
            if copy_type:
                path=folder/'copy-type-repair'/copy_type['file']
                print(f"{module['path']} {function['address']} [verified-copy-abi-repair]")
                print('  '+copy_type['signature']);print('  '+str(path))
                print('  已恢复指针返回、三个参数和四目标间接分派；原始字节/函数体已核验。保留手动 switch 注释，生成 C 未做编译等价证明。')
                if args.show:print(path.read_text(encoding='utf-8'))
                continue
            if continuation:
                path=folder/'continuation-body-repair'/continuation['file']
                print(f"{module['path']} {function['address']} [verified-continuation-body-repair]")
                print('  '+str(path))
                print(f"  补全函数体 {continuation['body_bytes']} 字节、{continuation['instructions']} 条指令；原字节和静态控制流已核验，ABI 类型为证据注释。")
                if function['address']=='10632ce0' and continuation_behavior:
                    print(f"  {continuation_behavior['copy']['cases']} 组原始指令复制测试通过（含重叠缓冲区）；截断回退可能留下编码首字节，不等于完整 UTF-8 校验。")
                if function['address']=='1062fb70' and continuation_behavior:
                    print(f"  {continuation_behavior['parser']['cases']} 组原始指令解析测试通过；不检查前两个单元，十六进制模式跳过非数字，调用者输入约束待查。")
                if function['address']=='104c2d30' and memory_command:
                    print(f"  早期 {memory_command['cases']} 组命令分派测试覆盖全部调用者指令，三个辅助函数使用显式模型；如有上方联调证据，命令 1 / 0x73 已改用真实指令另行核验。")
                if args.show:print(path.read_text(encoding='utf-8'))
                continue
            if skip_body and function['address']==skip_body['address']:
                path=folder/'skip-body-repair'/skip_body['file']
                print(f"{module['path']} {function['address']} [verified-skip-body-repair]")
                print('  '+str(path))
                print(f"  原 8 字节函数体补全为 72 字节、22 条指令；{skip_body['cases']} 组原始指令测试通过。返回类型按 EAX 指针证据覆盖；要求输入可读且有停止单元。")
                if args.show:print(path.read_text(encoding='utf-8'))
                continue
            if scanner and function['address']==scanner['address']:
                if scanner_reference:
                    print(f"  手工重建参考：{scanner_reference['cases']} 组完整原始代码对照通过，覆盖全部 160 条扫描指令；{scanner_reference['logical_end_overread_cases']} 组人工输入读到逻辑末尾后的已分配填充区，真实输入约束仍待查。")
                    if 'profiles' in scanner_reference:
                        profile=scanner_reference['profiles']['static_tables']
                        print(f"  两张二进制内静态分类表：{profile['cases']} 组对照，观察到 {profile['logical_end_overreads']} 组逻辑末尾后读取；不代表所有运行时配置安全证明。")
                    print('  '+str(folder/'scanner-reference/reference.c')+'（--scanner-reference --show 可显示）')
                if args.scanner_reference and scanner_reference:
                    print(f"{module['path']} {function['address']} [differential-tested-hand-reconstruction]")
                    print('  classification 参数对应原对象 +0x4c；该 C 是手工重建，保留原 Ghidra 文件用于对照。')
                    if args.show:print((folder/'scanner-reference/reference.c').read_text(encoding='utf-8'))
                    continue
                path=folder/'scanner-body-repair'/scanner['file']
                print(f"{module['path']} {function['address']} {function['name']} [verified-scanner-body-repair]")
                print('  '+str(path))
                print(f"  原 43 字节函数体已补全为 {scanner['body_bytes']} 字节、{scanner['instructions']} 条指令；静态控制流和 {scanner['dispatch_cases']} 组分派测试通过。" + ('分类辅助函数的寄存器效果已有独立核验，扫描 C 的类型推断与完整行为仍待验证。' if classifier else '外部分类辅助函数的 ABI 与完整扫描语义仍待核验。'))
                if args.show:print(path.read_text(encoding='utf-8'))
                continue
            if ssl_alias:
                path=folder/'ssl-switch-repair'/rail_ssl['file']
                print(f"{module['path']} {function['address']} {function['name']} [verified-enclosing-switch-repair]")
                print('  '+str(path))
                print(f"  三个历史失败地址属于该函数的跳转表目标；原字节与 {rail_ssl['cases']} 组分派/标志模拟已核验。C 补回重复目标 case 5/6/7，保留手工覆盖提示；外部调用语义未证明。")
                if args.show:print(path.read_text(encoding='utf-8'))
                continue
            if wgl_initialization and function['address']==wgl_initialization['function']:
                print(f"  WGL 初始化片段：6 个符号请求、4 个保存槽、11 个能力标志；{wgl_initialization['cases']} 组原始指令模拟通过。缺少扩展字符串函数时保留标志旧值；外部方法使用桩，未运行图形驱动。")
            if copy_probe and function['address'] in ('181835930','181835910','18177f9f0'):
                print(f"  实际复制指令抽样核验：{copy_probe['direct_cases']:,} 个复制用例及 {copy_probe['caller_cases']} 个调用者联调用例；包含真实复制副作用，非全输入证明。")
                print('  ' + str(folder / 'copy-behavior/report.json'))
            if irradiance_hash and function['address'] in (irradiance_hash['word_hash'],irradiance_hash['fold']):
                print('  辐照度输入哈希：936 个原始指令用例通过；四字块逐块传递初始值，空指针块替换为四个 0xFFFFFFFF。非全输入等价证明。')
            if function['address'] in fma4_records:
                if irradiance_cache:
                    print('  缓存更新片段：1,944 个用例通过；浮点差绝对值严格大于 2^-23 才写回，任一变化将局部值设为 -1。')
                if fma4_gate_flow:
                    print('  控制流证据：局部比较每次取零值分支时，三函数的全部 120 条 FMA4 不可达；未证明栈字节不被别名修改。')
                if fma4_cpu:
                    print('  入口开关：检测结果 bit 7 有效且全局禁用字节为零；8,192 个检测组合、2,304 个开关用例通过，非完整浮点运行证明。')
                if fma4_environment:
                    print('  禁用字节设置点：查询 GEO_DISABLE_FMA4，返回非空指针即禁用；不解析字符串数值。')
                    if fma4_startup:
                        print('  设置点属于 CRT 初始化表（索引 110）；进程附加成功路径的静态调用链和表遍历已核验，未执行完整加载流程。')
                if function['address'] in fma4_roles.get('roles', {}):
                    print('  调用方用途证据：' + fma4_roles['roles'][function['address']] + '；非恢复的原始函数名。')
                fma4_root, fma4 = fma4_records[function['address']]
                print(f"  FMA4 反汇编补充：{fma4['instruction_count']:,} 条指令，{fma4['fma4_count']} 条 FMA4 重汇编字节一致；未证明 C 等价。")
                print('  ' + str(fma4_root / 'game.asm'))
                if 'normal_flow' in fma4:
                    print('  正常入口、Win64 调用约定假设下：4 处间接跳转的基址、索引范围和目标已核验。')
                if args.fma4_candidate:
                    candidate = validate_fma4_candidate(fma4_root)
                    if not candidate:
                        raise ValueError('No verified FMA4 candidate for this function')
                    candidate_path = fma4_root / 'ghidra-candidate' / candidate['file']
                    print(f"{module['path']} {function['address']} {function['name']} [{candidate['status']}]")
                    print('  ' + str(candidate_path))
                    print('  40 处不透明 FMA4/MXCSR 操作；浮点及异常语义未实现，非完整 C 等价结果。')
                    if args.show:
                        print(candidate_path.read_text(encoding='utf-8'))
                    continue
                if args.assembly:
                    print(f"{module['path']} {function['address']} {function['name']} [{fma4['status']}]")
                    if args.show:
                        print((fma4_root / 'game.asm').read_text(encoding='utf-8'))
                    continue
            if openssl and function['address'] in (openssl['address'], openssl['body_entry']):
                print(f"{module['path']} {function['address']} {openssl['name']} [{openssl['status']}]")
                coverage = unwind_records.get(openssl['address'])
                if coverage and not coverage['records']:
                    print('  此过程没有重叠的 PE 静态展开记录；动态注册与运行时异常恢复尚未核验。')
                print('  ' + str(openssl_root / openssl.get('assembly_file', 'sha512.asm')))
                if openssl['status'] == 'byte-exact-source-matched':
                    if 'relocation' in openssl:
                        print(f"  完整 {openssl['game_bytes']:,} 字节在核验外部符号重定位后逐字节匹配，含 {openssl['constant_bytes']} 字节静态数据；非新 C 输出。")
                    else:
                        print(f"  完整 {openssl['game_bytes']} 字节逐字节匹配；特殊指令保留上游 DB 原始字节，非新 C 输出。")
                else:
                    if 'runtime_functions' in openssl:
                        print(f"  另核验 {len(openssl['runtime_functions'])} 条运行时函数记录、{openssl['pdata_bytes'] + openssl['xdata_bytes']} 字节展开数据及 {openssl['unwind_relocations']} 处重定位；未执行异常注入测试。")
                        if seh_probe and openssl_root.name in seh_probe['source_evidence']:
                            print(f"  两个处理器共通过 {seh_probe['case_count']} 个真实指令离线模拟用例；系统展开调用使用返回桩，未验证 Windows 展开行为。")
                    if 'routines' in openssl:
                        print(f"  本入口属于 {len(openssl['routines'])} 个过程的联合源码核验；--show 展示联合范围。")
                    print(f"  {openssl['meaningful_instructions']:,} 条非 NOP 指令及 {openssl['constant_bytes']:,} 字节常量与静态数据匹配；采用地址及编码规范化，非逐字节匹配，非新 C 输出。")
                if args.show:
                    print((openssl_root / 'game.asm').read_text(encoding='utf-8'))
                continue
            if matched:
                print(f"{module['path']} {function['address']} {matched['name']} [{matched['mode']}]")
                print('  ' + matched['signature'])
                path = source_path.parent / matched['file']
                print(f"  {path}:{matched['source_line']}")
                print('  ' + matched['limitation'])
                if args.show:
                    print(path.read_text(encoding='utf-8'))
                continue
            if function['address'] in quality_results:
                quality = quality_results[function['address']]
                print(f"{module['path']} {function['address']} {function['name']} [{quality['mode']}]")
                print('  原始推断签名：' + function['signature'])
                path = folder / quality['file']
                print('  ' + str(path))
                print(f"  已核验 {quality['case_count']} 个输入映射；仍保留手工覆盖提示，不代表全函数等价证明。")
                if args.show:
                    print(path.read_text(encoding='utf-8'))
                continue
            if function['address'] in float_readers:
                reader=float_readers[function['address']];path=folder/'float-readers'/reader['file']
                print(f"{module['path']} {function['address']} {function['name']} [verified-float-return-repair]")
                print('  '+str(path))
                print(f"  返回值修正为 float / XMM0；已核验 {reader['cases']} 组对象/标志/数组输入。vtable+0x100 为间接尾调用，目标实现使用桩，原警告保留。")
                if args.show:print(path.read_text(encoding='utf-8'))
                continue
            result = recovered.get(function['address'], function)
            mode = result.get('mode', function['status'])
            if function.get('crt_discovery'):
                mode = 'flow-target-decompiled' if function['callback_kind'].startswith('flow-callbacks') else {'exit-callbacks':'exit-callback-decompiled','reader-table-callbacks':'reader-table-decompiled','crt-callbacks':'crt-callback-decompiled','pointer-callbacks':'pointer-entry-decompiled','direct-callbacks':'direct-target-decompiled','relocation-callbacks':'relocation-entry-decompiled','relocation-retry':'relocation-retry-decompiled'}[function['callback_kind']]
                print('  代码指针/直接跳转补充发现的入口；函数体覆盖与字节已核验，C 警告及语义仍需审查。' if function['callback_kind'] in ('pointer-callbacks','direct-callbacks','relocation-callbacks','relocation-retry') or function['callback_kind'].startswith('flow-callbacks') else '  注册回调补充发现的入口；汇编覆盖与字节已核验，C 警告及语义仍需审查。')
                for edge in pointer_edges.get('edges',[]):
                    if edge['source']==function['address'] and edge['kind']=='import-slot':
                        print(f"  {edge['site']} 已识别为 PE 导入 {edge['dll']}!{edge['symbol']}；未执行系统 API。")
                loader=pointer_edges.get('dynamic_loader',{})
                if function['address']==loader.get('source'):
                    print('  181cdc170 的已核验初始化路径使用 wglGetProcAddress 请求 wglSwapIntervalEXT；未证明加载成功或运行时指针未被改写。')
                for origin in pointer_edges.get('shared_warning_origins',[]):
                    if origin['source']==function['address']:
                        print(f"  警告 {origin['site']} 属于另一个函数 {origin['owner']}，已核验当前函数到该入口的直接转移；间接目标仍未知。")
                if crt_edges and function['address']==crt_edges['slist_initializer']['entry']:
                    print('  间接跳转已由 PE 导入表识别为 KERNEL32.dll!InitializeSListHead 尾调用，参数为 181cbdd80；原跳转表警告保留。')
                if function['address'] in exit_wrappers:
                    wrapper=exit_wrappers[function['address']];edge=wrapper['tail_jump']
                    argument='全局地址' if wrapper['argument_kind']=='global-address' else '全局地址处的 64 位值'
                    print(f"  已核验无条件尾调用 {edge['dll']}!{edge['symbol']}；RCX 参数为{argument} {wrapper['argument_address']}，未调整栈。原跳转表警告保留，未执行系统 API。")
            print(f"{module['path']} {function['address']} {function['name']} [{mode}]")
            print('  ' + function['signature'])
            if 'file' not in result:
                print('  ' + function.get('message', '').strip())
                continue
            path = folder / result['file']
            hashes = crt_hashes if function.get('crt_discovery') else recovery['files'] if function['address'] in recovered else manifest['files']
            if digest(path) != hashes[result['file']]:
                raise ValueError('C output hash mismatch')
            print('  ' + str(path))
            if args.show:
                print(path.read_text(encoding='utf-8'))
    print(f'命中 {found} 项，显示 {min(found, args.limit)} 项。伪代码仍需结合反汇编核验。')


if __name__ == '__main__':
    main()
