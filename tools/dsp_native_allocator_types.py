"""Validate ABI-annotated allocator C against raw bytes and verified dispatch evidence."""
import json
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import pe_read,require
from dsp_native_allocator_dispatch import validate_cached as validate_dispatch
from dsp_native_buffer_cleanup import validate_cached as validate_cleanup


def validate_cached(folder,sha):
    root=folder/'allocator-type-repair'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Allocator types source differs')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Allocator type evidence differs')
    report=read_json(root/'verification.json')
    for name,value in report['dependencies'].items():require(digest(folder/name)==value,'Allocator ABI dependency differs')
    return report


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_32
    inv=read_json(GENERATED/'native/inventory.json');module=next(m for m in inv['files'] if Path(m['path']).name=='rail_api.dll')
    folder=GENERATED/'native'/module['output'];root=folder/'allocator-type-repair';path=Path(inv['game_directory'])/module['path']
    report=read_json(root/'report.json');require(digest(path)==report['source_sha256']==module['sha256'] and report['program_changes_rolled_back'],'Source/transaction differs');game=path.read_bytes()
    dispatch=validate_dispatch(folder,module['sha256']);cleanup=validate_cleanup(folder,module['sha256']);require(dispatch and cleanup,'Missing ABI behavior evidence')
    expected={'104c5f90':(47,15),'104c5fe0':(164,54),'104c5fc0':(29,10),'104c61d0':(46,16)};cs=Cs(CS_ARCH_X86,CS_MODE_32);rows=[];files=['report.json','verification.json']
    require({r['address'] for r in report['functions']}==set(expected),'Export scope differs')
    for row in report['functions']:
        address=row['address'];start=int(address,16);size,count=expected[address];seen=set();actual_count=0
        require(row['ranges']==[[address,f'{start+size-1:x}']],'Function range differs')
        for line in (root/(address+'.asm')).read_text().splitlines():
            pc,data,_=line.split(' ',2);pc=int(pc,16);raw=bytes.fromhex(data);require(pe_read(game,pc,len(raw))==raw,'Raw byte differs')
            decoded=list(cs.disasm(raw,pc));require(len(decoded)==1 and decoded[0].size==len(raw),'Instruction differs')
            span=set(range(pc,pc+len(raw)));require(not seen&span,'Overlapping instruction');seen|=span;actual_count+=1
        require(seen==set(range(start,start+size)) and actual_count==count==row['instructions'],'Body coverage differs')
        code=(root/(address+'.c')).read_text()
        if address in ('104c5f90','104c5fe0'):require('void * __cdecl' in code and 'return pvVar1;' in code,'Pointer return annotation missing')
        if address=='104c5f90':require('(*PTR_FUN_10e1d01c)(size,file,line)' in code,'Malloc callback arguments missing')
        if address=='104c5fe0':require('(*PTR_FUN_10e1d020)(memory,size,file,line)' in code,'Realloc callback arguments missing')
        if address=='104c5fc0':require('(*PTR_FUN_10e1d024)(memory,file,line)' in code,'Free callback arguments missing')
        if address=='104c61d0':require(all(name in code for name in ('dsp_malloc_callback **malloc_out','dsp_realloc_callback **realloc_out','dsp_free_callback **free_out')),'Getter types missing')
        rows.append(dict(address=address,file=address+'.c',body_bytes=size,instructions=count,signature=row['signature'],warnings=row['warnings']));files.extend([address+'.asm',address+'.c'])
    result=dict(functions=rows,dependencies={n:digest(folder/n) for n in ('allocator-dispatch-behavior/manifest.json','buffer-cleanup-behavior/manifest.json')},
                dispatch_cases=dispatch['total_cases'],cleanup_cases=cleanup['total_cases'],
                limitation='C regenerated with evidence-based ABI annotations and typed global callbacks. Bytes and declared signatures checked; existing original-instruction behavior evidence retained. C has not been compiled for whole-function equivalence; raw indirect-jump/global-overlap warnings remain.')
    (root/'verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8');(root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={n:digest(root/n) for n in files}),indent=2),encoding='utf-8')
    print(json.dumps(result))


if __name__=='__main__':main()
