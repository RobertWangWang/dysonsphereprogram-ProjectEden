"""Verify all-module relocation discovery/export manifests and list unresolved entries."""
import collections
import json
from pathlib import Path

from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import require
from dsp_native_crt_callbacks import validate_cached
from dsp_native_rail_ssl_switch import validate_cached as validate_ssl


def main():
    root = GENERATED / 'native'
    audit = read_json(root / 'relocation-audit-summary.json')
    inventory = read_json(root / 'inventory.json')
    require(audit['inventory_sha256'] == digest(root / 'inventory.json'), 'Inventory changed')
    sources = {m['path']: m for m in inventory['files']}
    totals = collections.Counter()
    modules, unresolved, retries, enclosing_repairs = [], [], [], []
    dependencies = {'relocation-audit-summary.json': digest(root / 'relocation-audit-summary.json')}
    for module in audit['modules']:
        folder = root / module['output']
        source = sources[module['module']]
        require(module['source_sha256'] == source['sha256'], 'Source selection changed')
        require(digest(Path(inventory['game_directory']) / source['path']) == source['sha256'], 'Game changed')
        discovery = folder / 'relocation-audit'
        require(digest(discovery / 'manifest.json') == module['manifest_sha256'], 'Discovery changed')
        marker = read_json(discovery / 'manifest.json')
        for name, sha in marker['files'].items():
            require(digest(discovery / name) == sha, 'Discovery artifact changed: ' + name)
        export = folder / 'relocation-callbacks'
        if not (export / 'manifest.json').exists():
            modules.append({**module, 'export_status': 'not-exported' if module['missing_candidates'] else 'no-candidates'})
            continue
        marker = read_json(export / 'manifest.json')
        require(marker['source_sha256'] == source['sha256'], 'Export source changed')
        for name, sha in marker['files'].items():
            require(digest(export / name) == sha, 'Export artifact changed: ' + name)
        report = read_json(export / 'report.json')
        verification = read_json(export / 'verification.json')
        require(report['complete'] and report['program_changes_rolled_back'], 'Incomplete export')
        require(verification['original_index_sha256'] == digest(folder / 'functions.json'), 'Base index changed')
        require(verification['input_list_sha256'] == digest(discovery / 'missing-entries.txt'), 'Input changed')
        rows = report['functions']
        expected = set((discovery / 'missing-entries.txt').read_text().split())
        require(len(rows) == len(expected) and {r['address'] for r in rows} == expected, 'Candidate set differs')
        statuses = collections.Counter(r['status'] for r in rows)
        require(dict(statuses) == verification['statuses'], 'Status summary differs')
        totals.update(statuses)
        for key in ('body_bytes', 'instructions', 'bodies_verified', 'functions_with_warnings'):
            totals[key] += verification[key]
        for row in rows:
            if row['status'] not in ('decompiled', 'overlaps-existing-function'):
                unresolved.append({'module': module['module'], **row})
        retry_rows = validate_cached(folder, source['sha256'], kind='relocation-retry')
        if retry_rows:
            failures = {r['address'] for r in rows if r['status'] == 'decompile-failed'}
            require({r['address'] for r in retry_rows} <= failures, 'Retry is not a failed original entry')
            retries.extend({'module': module['module'], 'address': r['address'], 'file': r['file']} for r in retry_rows)
            dependencies[str((folder / 'relocation-retry/manifest.json').relative_to(root)).replace('\\', '/')] = digest(folder / 'relocation-retry/manifest.json')
        ssl = validate_ssl(folder, source['sha256'])
        if ssl:
            failures = {r['address'] for r in rows if r['status'] == 'decompile-failed'}
            require(set(ssl['labels']) <= failures, 'Enclosing repair does not match historical failures')
            enclosing_repairs.extend(dict(module=module['module'], address=label, parent=ssl['address']) for label in ssl['labels'])
            dependencies[str((folder / 'ssl-switch-repair/manifest.json').relative_to(root)).replace('\\', '/')] = digest(folder / 'ssl-switch-repair/manifest.json')
        modules.append({**module, 'export_status': 'verified', 'verification': verification})
        dependencies[str((export / 'manifest.json').relative_to(root)).replace('\\', '/')] = digest(export / 'manifest.json')
    recovered = {(r['module'], r['address']) for r in retries}
    remaining = [r for r in unresolved if (r['module'], r['address']) not in recovered]
    reclassified = {(r['module'], r['address']) for r in enclosing_repairs}
    result = dict(modules=modules, totals=dict(totals), unresolved=unresolved, retry_recovered=retries,
                  remaining_after_retry=remaining, enclosing_function_repairs=enclosing_repairs,
                  remaining_after_reclassification=[r for r in remaining if (r['module'], r['address']) not in reclassified],
                  c_outputs_after_retry=totals['decompiled'] + len(retries), dependencies=dependencies,
                  limitation='Exported entry counts are not counts of proven source functions. Body sums may include shared bytes. Unexported candidates and unresolved semantics remain outside this completion marker.')
    output = root / 'relocation-export-summary.json'
    output.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(dict(totals=dict(totals), historical_unresolved=len(unresolved), retry_recovered=len(retries),
                          remaining_after_retry=len(remaining), c_outputs_after_retry=result['c_outputs_after_retry'], output=str(output))))


if __name__ == '__main__':
    main()
