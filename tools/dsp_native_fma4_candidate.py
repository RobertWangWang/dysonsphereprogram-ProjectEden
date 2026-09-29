"""核验 FMA4 C 候选的 120 个不透明操作输入、输出与寄存器别名；不证明浮点语义。"""
import argparse
import copy
import json
import re
from pathlib import Path
from dsp_knowledge import GENERATED, ROOT, digest, read_json
from dsp_native_source_match import GAME_SHA, require, pe_read
from dsp_native_fma4 import validate_cached as validate_assembly

MASK=(1<<64)-1
GPRS={name:n*8 for n,name in enumerate(('rax','rcx','rdx','rbx','rsp','rbp','rsi','rdi'))}
GPRS.update({f'r{n}':0x80+(n-8)*8 for n in range(8,16)})
PATTERN=r'\((unique|register|const), (0x[0-9a-f]+), (\d+)\)'

def linear(constant,terms=()):
    return ('linear',constant&MASK,tuple(sorted((k,v&MASK) for k,v in dict(terms).items() if v&MASK)))

def combine(a,b,multiply=False):
    if isinstance(a,int): a=linear(a)
    if isinstance(b,int): b=linear(b)
    require(a[0]==b[0]=='linear','Nonlinear address expression')
    x,y=dict(a[2]),dict(b[2])
    if multiply:
        require(not y,'Nonconstant scale')
        return linear(a[1]*b[1],[(k,v*b[1]) for k,v in x.items()])
    for k,v in y.items(): x[k]=x.get(k,0)+v
    return linear(a[1]+b[1],x.items())

def verify_pcode(row, instruction):
    from capstone.x86 import X86_OP_REG, X86_OP_MEM
    expected=[]
    for op in instruction.operands[1:]:
        if op.type==X86_OP_REG:
            name=instruction.reg_name(op.reg);require(name.startswith('xmm'),'Unexpected register operand')
            expected.append(('register',0x1200+int(name[3:])*64,16))
        elif op.type==X86_OP_MEM:
            m=op.mem;terms=[]
            require(not m.segment,'Unexpected segment')
            if m.base: terms.append((GPRS[instruction.reg_name(m.base)],1))
            if m.index: terms.append((GPRS[instruction.reg_name(m.index)],m.scale))
            expected.append(('load',linear(m.disp,terms),16))
        else: raise ValueError('Unexpected operand')
    expected.append(('register',0x1094,4))
    expected=tuple(expected);state={};calls=[]
    def piece(value,offset,size):
        if value[0]=='opaque' and offset==0 and size==16:return ('vector',value[1])
        if value[0]=='opaque' and offset==16 and size==4:return ('mxcsr',value[1])
        raise ValueError('Unexpected result slice')
    def read(node):
        space,off,size=node
        if space=='const':return off
        if node in state:return state[node]
        for (s,a,n),value in reversed(list(state.items())):
            if s==space and a<=off and off+size<=a+n:return piece(value,off-a,size)
        if space=='register':
            if size==8 and off in GPRS.values():return linear(0,[(off,1)])
            return ('register',off,size)
        raise ValueError('Read of undefined unique temporary')
    for text in row['pcode']:
        nodes=[(space,int(off,16),int(size)) for space,off,size in re.findall(PATTERN,text)]
        op=text.split(') ',1)[1].split(' ',1)[0];out=nodes[0];args=[read(n) for n in nodes[1:]]
        if op=='INT_ADD': value=combine(*args)
        elif op=='INT_MULT': value=combine(*args,multiply=True)
        elif op=='LOAD': value=('load',args[1],out[2])
        elif op=='CALLOTHER':
            require(tuple(args[1:])==expected,'FMA4 source order or MXCSR input mismatch')
            value=('opaque',tuple(args[1:]));calls.append(value)
        elif op=='COPY':value=args[0]
        elif op=='SUBPIECE':value=piece(args[0],args[1],out[2])
        elif op=='INT_ZEXT':value=('zext',args[0],out[2])
        else:raise ValueError('Unexpected p-code operation '+op)
        state[out]=value
    dest=0x1200+int(instruction.reg_name(instruction.operands[0].reg)[3:])*64
    require(len(calls)==1,'Incorrect opaque operation count')
    require(state.get(('register',dest,16))==('vector',expected),'Incorrect destination vector')
    require(state.get(('register',0x1094,4))==('mxcsr',expected),'Incorrect MXCSR output')
    require(state.get(('register',dest,64))==('zext',('vector',expected),64),'Missing upper-vector clearing')

def validate_cached(root):
    path=root/'ghidra-candidate/manifest.json'
    if not path.exists():return {}
    manifest=read_json(path)
    require(manifest['source_sha256']==GAME_SHA,'Candidate baseline mismatch')
    for name,sha in manifest['files'].items(): require(digest(path.parent/name)==sha,'Candidate artifact changed')
    result=read_json(path.parent/'verification.json')
    require(result['status']=='opaque-fma4-candidate-verified','Candidate not verified')
    return result

def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--sla',type=Path,required=True);p.add_argument('--log',type=Path,required=True)
    args=p.parse_args();inv=read_json(GENERATED/'native/inventory.json')
    game_path=Path(inv['game_directory'])/'UnityPlayer.dll';require(digest(game_path)==GAME_SHA,'Game changed')
    game=game_path.read_bytes();folder=GENERATED/'native/UnityPlayer.dll'
    records=validate_assembly(folder,GAME_SHA);decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True
    for address,(root,assembly) in records.items():
        out=root/'ghidra-candidate';(out/'manifest.json').unlink(missing_ok=True)
        report=read_json(out/'report.json');code=(out/report['file']).read_text(encoding='utf-8')
        require(report['status']=='candidate-decompiled' and report['source_sha256']==GAME_SHA and report['program_changes_rolled_back'],'Bad candidate status')
        require(report['instruction_count']==assembly['instruction_count'] and len(report['fma4'])==40,'Coverage mismatch')
        require(not report['contains_bad_instruction'] and 'halt_baddata' not in code,'Candidate still truncated')
        require(report['opaque_intrinsic_occurrences']==code.count('DSP_FMA4_PS_RESULT_AND_MXCSR')==40,'Missing intrinsic in C')
        require(all(('__security_check_cookie' in s or "Globals starting with" in s) for s in report['warnings']),'Unexpected C warning')
        ins=list(decoder.disasm(pe_read(game,int(address,16),assembly['code_bytes']),int(address,16)))
        fmas={f'{i.address:x}':i for i in ins if i.mnemonic=='vfmaddps'}
        require({r['address'] for r in report['fma4']}==set(fmas),'FMA address set mismatch')
        for row in report['fma4']:
            i=fmas[row['address']];require(bytes.fromhex(row['bytes'])==i.bytes,'FMA bytes differ');verify_pcode(row,i)
        negatives=[]
        for kind in ('remove-upper-clear','wrong-mxcsr-input'):
            row=copy.deepcopy(report['fma4'][0])
            if kind=='remove-upper-clear':row['pcode']=[s for s in row['pcode'] if 'INT_ZEXT' not in s]
            else:row['pcode']=[s.replace('(register, 0x1094, 4)','(register, 0x1090, 4)') if 'CALLOTHER' in s else s for s in row['pcode']]
            try: verify_pcode(row,fmas[row['address']])
            except ValueError as error: negatives.append(dict(kind=kind,error=str(error)))
            else:raise ValueError('P-code mutation accepted')
        result=dict(status='opaque-fma4-candidate-verified',source_sha256=GAME_SHA,address=address,file=report['file'],
                    instruction_count=report['instruction_count'],verified_intrinsics=40,negative_cases=negatives,
                    sla_sha256=digest(args.sla),script_sha256=digest(ROOT/'tools/DspFma4Candidate.java'),
                    sleigh_source_sha256=digest(ROOT/'tools/dsp-fma4.sinc'),verifier_sha256=digest(Path(__file__)),
                    headless_log_sha256=digest(args.log),warnings=report['warnings'],
                    limitation='Operand plumbing and outputs verified against an opaque FMA4/MXCSR contract only; no fused arithmetic implementation, exception model, or complete C equivalence proof.')
        (out/'verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
        (out/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,
            files={f.name:digest(f) for f in out.iterdir() if f.is_file() and f.name!='manifest.json'}),indent=2),encoding='utf-8')
        print(address,'40 intrinsic inputs/outputs verified; 2 p-code mutations rejected')

if __name__=='__main__':main()
