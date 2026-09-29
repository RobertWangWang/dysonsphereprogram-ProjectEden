"""Validate runtime ABI regeneration and distinguish unresolved C from usable annotations."""
import json
import re
import argparse
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_lock_pipeline import validate_cached as validate_pipeline
from dsp_native_malloc_base import validate_cached as validate_malloc
from dsp_native_critical_section_compat import validate_cached as validate_compat
from dsp_native_security_failure import validate_compat_cached as validate_security_compat


def validate_cached(folder,sha):
    root=folder/'runtime-type-repair'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Runtime types source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Runtime types evidence differs')
    report=read_json(root/'verification.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Runtime type dependency differs')
    return report


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--candidate',action='store_true');options=parser.parse_args()
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];root=folder/('runtime-type-report-candidate' if options.candidate else 'runtime-type-repair');path=Path(inv['game_directory'])/module['path'];report=read_json(root/'report.json')
    require(digest(path)==report['source_sha256']==module['sha256'] and report['program_changes_rolled_back'],'Source/transaction differs');game=path.read_bytes()
    require(validate_pipeline(folder,module['sha256']) and validate_malloc(folder,module['sha256']),'Missing behavior evidence')
    compat=validate_compat(folder,module['sha256'])
    require(compat and compat['negative_control_caught'] and compat['cases']==3840,'Missing compatibility behavior evidence')
    security=validate_security_compat(folder,module['sha256'])
    require(security and security['cases']==7776 and security['negative_control_caught'] and security['terminals']['returned']==972,'Missing security chain evidence')
    require(report.get('security_report_override')=='105d2db8: int cdecl, noreturn=false; retain original RET path under explicit returning termination API models','Security annotation differs')
    require(report.get('compatibility_overrides')==[
        '10618a42: void fastcall guard(target in ECX)',
        '10618a48: int stdcall target(critical_section,spin_count,flags)',
        '105d29e6: inline original cookie check for EAX preservation'], 'Compatibility annotations differ')
    for window in security['ranges']:
        raw=bytes.fromhex(window['bytes_hex'])
        require(pe_read(game,int(window['address'],16),len(raw))==raw,'Compatibility context bytes differ')
    specs={'106132d5':(65,25,'AL:1',0),'10613316':(23,9,'<VOID>',1),'1061332d':(49,18,'AL:1',0),
           '1061335e':(23,9,'<VOID>',1),'10618a07':(98,34,'EAX:4',3),'106183ea':(156,65,'EAX:4',4),
           '10618486':(123,52,'EAX:4',1),'10613c79':(78,32,'EAX:4',1)}
    require({r['address'] for r in report['functions']}==set(specs),'Runtime export scope differs')
    cs=Cs(CS_ARCH_X86,CS_MODE_32);rows=[];files=['report.json','verification.json']
    for row in report['functions']:
        address=row['address'];base=int(address,16);size,count,storage,argc=specs[address];seen=set();starts=set()
        require(row['ranges']==[[address,f'{base+size-1:x}']],'Runtime body ranges differ')
        require(row['return_storage']==storage,'Return register annotation differs')
        convention='__stdcall' if address=='10618a07' else '__cdecl';require(row['convention']==convention,'Calling convention differs')
        sig=row['signature'];args=sig[sig.index('(')+1:sig.rindex(')')]
        require((0 if args=='void' else len(args.split(',')))==argc,'Argument count differs')
        for line in (root/(address+'.asm')).read_text().splitlines():
            pc,data,_=line.split(' ',2);pc=int(pc,16);raw=bytes.fromhex(data);require(pe_read(game,pc,len(raw))==raw,'Runtime bytes differ')
            ins=list(cs.disasm(raw,pc));require(len(ins)==1 and ins[0].size==len(raw),'Instruction differs');span=set(range(pc,pc+len(raw)));require(not seen&span,'Instruction overlap');seen.update(span);starts.add(pc)
        require(seen==set(range(base,base+size)) and len(starts)==count==row['instructions'],'Runtime instruction coverage differs')
        code=(root/(address+'.c')).read_text();conditional=address=='10618a07'
        unresolved=[token for token in ('extraout_','unaff_','unaff ') if token in code]
        require(not unresolved,'Unexpected unresolved register in regenerated C')
        if conditional:
            require('(*(code *)PTR_guard_check_icall_10b2a6e8)(target);' in code,'Guard argument attribution differs')
            require('iVar2 = (*target)(critical_section,spin_count,flags);' in code,'Modern API arguments/result differ')
            require('iVar2 = InitializeCriticalSectionAndSpinCount(critical_section,spin_count);' in code,'Fallback arguments/result differ')
            failure=re.search(r'if \(\(uVar1 \^ \(uint\)&stack0xfffffffc\) != DAT_10e24f44\) \{\s*iVar2 = ___report_gsfailure\(\);\s*\}',code)
            require(failure is not None and 'return iVar2;' in code,'Security return flow differs; re-review required')
            require('Subroutine does not return' not in code,'Conflicting noreturn annotation')
        if storage=='AL:1':require(sig.startswith('bool ') and 'return true;' in code,'Boolean result missing')
        if address=='106132d5':require('return false;' in code,'Init failure result missing')
        if address in ('106183ea','10618486','10613c79'):require(sig.startswith('void * '),'Pointer return missing')
        rows.append(dict(address=address,file=address+'.c',signature=sig,return_storage=storage,convention=convention,body_bytes=size,instructions=count,
                         preferred=True,status='verified-abi-returning-boundary-model' if conditional else 'verified-abi-annotation',unresolved=unresolved,warnings=row['warnings']))
        files.extend([address+'.asm',address+'.c'])
    dependencies=['lock-pipeline-behavior/manifest.json','malloc-base-behavior/manifest.json','critical-section-compat-behavior/manifest.json','security-failure-compat-behavior/manifest.json']
    result=dict(functions=rows,preferred_count=sum(r['preferred'] for r in rows),partial_count=sum(not r['preferred'] for r in rows),
                body_bytes=sum(r['body_bytes'] for r in rows),instructions=sum(r['instructions'] for r in rows),dependencies={n:digest(folder/n) for n in dependencies},
                limitation='Eight regenerated outputs preferred as evidence-based ABI annotations, not compiled equivalents or recovered debug types. Compatibility reporter is int and noreturn=false to retain the original RET path under explicit returning OS models. Real OS termination and exception dispatch remain outside evidence; no claim of real self-termination returning.')
    (root/'verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={n:digest(root/n) for n in files}),indent=2),encoding='utf-8')
    print(json.dumps(result))


if __name__=='__main__':main()
