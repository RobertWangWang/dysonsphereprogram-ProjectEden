"""审计已匹配的 OpenSSL 计算过程在 PE 异常目录中的覆盖，保留缺失记录。"""
import hashlib
import json
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import coff, pe_read, require
from dsp_native_openssl_match import GAME_SHA


def main():
    module = 'DSPGAME_Data/Plugins/x86_64/rail_api64.dll'
    folder = GENERATED/'native'/module.replace('/', '__')
    game_path = Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/module
    require(digest(game_path) == GAME_SHA, 'Source baseline changed')
    game = game_path.read_bytes(); base = 0x180000000
    pe = struct.unpack_from('<I', game, 60)[0]
    require(struct.unpack_from('<H', game, pe+24)[0] == 0x20b, 'Expected PE32+')
    rva, size = struct.unpack_from('<II', game, pe+24+112+24)
    require(size % 12 == 0 and size > 0, 'Invalid exception directory size')
    raw = pe_read(game, base+rva, size)
    entries = list(struct.iter_unpack('<III', raw))
    require(all(a < b and c for a,b,c in entries), 'Invalid runtime function record')
    results = []; sources = {}
    for family, stem in [('sha512-family','sha512'),('aesni-sha256-family','aesni-sha256'),('chacha64-family','chacha')]:
        root = folder/'upstream-openssl'/family
        marker = read_json(root/'manifest.json')
        require(marker['source_sha256'] == GAME_SHA, 'Family baseline changed')
        for name, sha in marker['files'].items(): require(digest(root/name) == sha, 'Family artifact changed')
        report = read_json(root/'report.json')
        sections, _ = coff((root/(stem+'.obj')).read_bytes())
        sources[family] = dict(manifest_sha256=digest(root/'manifest.json'),sections=[dict(name=n,bytes=len(c),relocations=num) for n,c,o,num in sections])
        for function in report['routines']:
            start = int(function['address'],16); end = int(function['end_exclusive'],16)
            overlaps = [(base+a,base+b,base+c) for a,b,c in entries if base+a < end and base+b > start]
            missing = []; cursor = start
            for a,b,c in sorted(overlaps):
                if a > cursor: missing.append([cursor,min(a,end)])
                cursor = max(cursor,min(b,end))
            if cursor < end: missing.append([cursor,end])
            results.append(dict(family=family,name=function['name'],address=f'{start:x}',end_exclusive=f'{end:x}',
                records=[dict(begin=f'{a:x}',end_exclusive=f'{b:x}',unwind_address=f'{c:x}') for a,b,c in overlaps],
                uncovered_ranges=[[f'{a:x}',f'{b:x}'] for a,b in missing],
                uncovered_bytes=sum(b-a for a,b in missing)))
    chacha = [r for r in results if r['family'] == 'chacha64-family']
    require(len(chacha) == 5 and all(not r['records'] for r in chacha), 'ChaCha coverage baseline changed')
    require([s['name'] for s in sources['chacha64-family']['sections']] == ['.text'], 'ChaCha object sections changed')
    require(sum(len(r['records']) for r in results) == 8, 'Unexpected scoped runtime record count')
    root = folder/'unwind-coverage'; root.mkdir(exist_ok=True); (root/'manifest.json').unlink(missing_ok=True)
    report = dict(status='exception-directory-coverage-audited',source_sha256=GAME_SHA,
        exception_directory=dict(address=f'{base+rva:x}',bytes=size,records=len(entries),sha256=hashlib.sha256(raw).hexdigest()),
        source_families=sources,routines=results,
        limitation='Static PE exception-directory overlap only. Absence does not establish safe or unsafe runtime unwinding; dynamic function-table registration and fault behavior are not audited.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(f'Audited {len(results)} routines against {len(entries)} PE records: five ChaCha routines have no overlapping static entries')


if __name__ == '__main__': main()
