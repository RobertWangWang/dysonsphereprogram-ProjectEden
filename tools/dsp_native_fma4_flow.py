"""保守解释 Unity FMA4 函数 GPR 控制流，核验正常入口下的跳转表地址范围。"""
import collections
import itertools
import json
from pathlib import Path
from capstone import Cs, CS_ARCH_X86, CS_MODE_64, CS_GRP_JUMP, CS_GRP_CALL, CS_GRP_RET
from capstone.x86 import X86_OP_REG, X86_OP_IMM, X86_OP_MEM
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import GAME_SHA, pe_read, require
from dsp_native_fma4 import IMAGE, PROFILES, validate_cached

MAX = (1 << 64) - 1
TOP = (0, MAX)
U32 = (0, (1 << 32) - 1)
REGS = ['rax','rbx','rcx','rdx','rsi','rdi','rbp','rsp'] + [f'r{n}' for n in range(8,16)]
ALIASES = {}
for full, names in zip(REGS[:8], [('eax','ax','al','ah'),('ebx','bx','bl','bh'),('ecx','cx','cl','ch'),('edx','dx','dl','dh'),('esi','si','sil'),('edi','di','dil'),('ebp','bp','bpl'),('esp','sp','spl')]):
    for name in (full, *names): ALIASES[name] = full
for n in range(8,16):
    for suffix in ('','d','w','b'): ALIASES[f'r{n}{suffix}'] = f'r{n}'

def bounds(v):
    return (min(v),max(v)) if isinstance(v, frozenset) else v

def join(a,b):
    if a == b: return a
    if isinstance(a,frozenset) and isinstance(b,frozenset) and len(a|b)<=32: return a|b
    x,y=bounds(a),bounds(b)
    # Widen growing intervals, retaining the useful zero-extension invariant.
    hi=max(x[1],y[1])
    return U32 if hi<=U32[1] else TOP

def crop(v,size):
    if size == 8: return v
    mask=(1<<(size*8))-1
    if isinstance(v,frozenset): return frozenset(x&mask for x in v)
    return v if v[1]<=mask else (0,mask)

def values(v):
    if isinstance(v,frozenset): return v
    return range(v[0],v[1]+1) if v[1]-v[0]<=31 else None

def analyze(code, entry, tables, raw_read):
    decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True
    ins=list(decoder.disasm(code,entry)); nodes={i.address:i for i in ins}
    require(sum(i.size for i in ins)==len(code),'Incomplete decode')
    states={entry:({r:TOP for r in REGS},None)}; queue=collections.deque([entry]); resolved={}; unresolved={}
    edges=set(); visits=0; table_loads={}
    def push(src,dst,state):
        require(dst in nodes,'Control flow escaped code extent')
        edges.add((src,dst))
        old=states.get(dst)
        if old:
            state=({r:join(old[0][r],state[0][r]) for r in REGS}, old[1] if old[1]==state[1] else None)
        if state!=old: states[dst]=state;queue.append(dst)
    while queue:
        address=queue.popleft(); visits+=1
        require(visits<200000,'Dataflow did not converge')
        i=nodes[address]; before,predicate=states[address]; after=before.copy(); ops=i.operands
        reads,writes=i.regs_access()
        written={ALIASES[i.reg_name(r)] for r in writes if i.reg_name(r) in ALIASES}
        for r in written: after[r]=TOP
        if predicate and (predicate[0] in written or any(i.reg_name(r)=='rflags' for r in writes)): predicate=None
        # Capstone names this register eflags on x86-64.
        if any(i.reg_name(r)=='eflags' for r in writes): predicate=None
        dest=ALIASES.get(i.reg_name(ops[0].reg)) if ops and ops[0].type==X86_OP_REG else None
        if dest in written and ops[0].size==4: after[dest]=U32
        def operand(op):
            if op.type==X86_OP_IMM: return frozenset([op.imm&MAX])
            if op.type==X86_OP_REG and i.reg_name(op.reg) in ALIASES: return crop(before[ALIASES[i.reg_name(op.reg)]],op.size)
            return TOP
        if dest and i.mnemonic in ('mov','movabs') and len(ops)==2:
            val=operand(ops[1])
            if ops[1].type==X86_OP_MEM and ops[1].size==4:
                m=ops[1].mem; base=ALIASES.get(i.reg_name(m.base));index=ALIASES.get(i.reg_name(m.index))
                table_loads.pop(address,None)
                if base and index and m.scale==4 and before[base]==frozenset([IMAGE]):
                    table=IMAGE+m.disp; indices=values(before[index])
                    if not m.segment and table in tables and indices is not None and all(0<=v<8 for v in indices):
                        val=frozenset(int.from_bytes(raw_read(table+v*4,4),'little') for v in indices)
                        table_loads[address]=dict(table=f'{table:x}',base_register=base,base=f'{IMAGE:x}',
                                                 index_register=index,index_values=sorted(indices))
            after[dest]=crop(val,ops[0].size) if ops[0].size in (4,8) else TOP
        elif dest and i.mnemonic=='lea' and len(ops)==2 and i.reg_name(ops[1].mem.base)=='rip':
            after[dest]=crop(frozenset([(i.address+i.size+ops[1].mem.disp)&MAX]),ops[0].size)
        elif dest and i.mnemonic=='add' and len(ops)==2:
            a,b=values(operand(ops[0])),values(operand(ops[1]))
            if a is not None and b is not None:
                v=frozenset((x+y)&MAX for x,y in itertools.product(a,b))
                after[dest]=crop(v,ops[0].size) if ops[0].size in (4,8) else TOP
        if i.mnemonic=='cmp' and len(ops)==2 and ops[0].type==X86_OP_REG and ops[0].size==4 and ops[1].type==X86_OP_IMM and ops[1].imm==7:
            predicate=(ALIASES[i.reg_name(ops[0].reg)],7)
        if i.group(CS_GRP_RET): continue
        if i.group(CS_GRP_CALL):
            # Normal Win64 ABI return: volatile GPRs and comparison flags unknown.
            for r in ('rax','rcx','rdx','r8','r9','r10','r11'): after[r]=TOP
            predicate=None
        if i.group(CS_GRP_JUMP):
            if ops[0].type==X86_OP_IMM:
                target=ops[0].imm
                if i.mnemonic=='ja' and predicate and bounds(before[predicate[0]])[1]<=U32[1]:
                    r=predicate[0];lo,hi=bounds(before[r])
                    if hi>7:
                        taken=after.copy();taken[r]=(max(lo,8),hi);push(address,target,(taken,predicate))
                    if lo<=7:
                        after[r]=(lo,min(hi,7));push(address,address+i.size,(after,predicate))
                    continue
                push(address,target,(after,predicate))
                if i.mnemonic=='jmp': continue
            else:
                val=operand(ops[0]); targets=values(val)
                if targets is None or any(t not in nodes for t in targets):
                    unresolved[address]=str(val);resolved.pop(address,None)
                else:
                    unresolved.pop(address,None);resolved[address]=sorted(targets)
                    for target in targets: push(address,target,(after,predicate))
                continue
        push(address,address+i.size,(after,predicate))
    return dict(visits=visits, reachable_instructions=len(states),
                unreachable_instructions=[dict(address=f'{i.address:x}',instruction=i.mnemonic+' '+i.op_str) for i in ins if i.address not in states],
                resolved={f'{a:x}':[f'{t:x}' for t in ts] for a,ts in resolved.items()},
                unresolved={f'{a:x}':v for a,v in unresolved.items()}, edge_count=len(edges),
                table_loads={f'{a:x}':v for a,v in sorted(table_loads.items())})

def main():
    inv=read_json(GENERATED/'native/inventory.json');game_path=Path(inv['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Source changed');game=game_path.read_bytes()
    folder=GENERATED/'native/UnityPlayer.dll';records=validate_cached(folder,GAME_SHA)
    for entry,(end,table,_,jumps) in PROFILES.items():
        root,report=records[f'{entry:x}']
        result=analyze(pe_read(game,entry,end-entry),entry,set(range(table,table+128,32)),lambda a,n:pe_read(game,a,n))
        print(f"{entry:x}: {result['reachable_instructions']} conservatively reachable instructions, {len(result['resolved'])} resolved jumps, {len(result['unresolved'])} unresolved")
        # Only promote a proof if all expected computed jumps were reached and resolved.
        require(not result['unresolved'] and set(result['resolved'])=={f'{j:x}' for j in jumps},'Unresolved indirect flow')
        expected={t['branch']:sorted(set(t['targets'])) for t in report['switch_tables']}
        require(result['resolved']==expected,'Computed targets disagree with table records')
        code=pe_read(game,entry,end-entry)
        decoder=Cs(CS_ARCH_X86,CS_MODE_64);decoder.detail=True
        ins=list(decoder.disasm(code,entry));negatives=[]
        for jump in jumps:
            index=next(n for n,i in enumerate(ins) if i.address==jump)
            guard=next(i for i in reversed(ins[index-10:index]) if i.mnemonic=='cmp' and i.op_str=='eax, 7')
            mutation=bytearray(code);offset=guard.address-entry+guard.size-1
            require(mutation[offset]==7,'Unexpected guard encoding');mutation[offset]=8
            wrong=analyze(bytes(mutation),entry,set(range(table,table+128,32)),lambda a,n:pe_read(game,a,n))
            require(f'{jump:x}' in wrong['unresolved'],'Expanded range guard accepted')
            negatives.append(dict(kind='guard-7-to-8',address=f'{guard.address:x}',rejected_branch=f'{jump:x}'))
        # Two switches use a base established well before the range guard.
        distant={0x1813b5460:(0x1813b5e5e,0x1813b6346),0x1813b9780:(0x1813bbb29,0x1813bbfb8)}
        if entry in distant:
            address,jump=distant[entry];i=next(i for i in ins if i.address==address)
            require(i.mnemonic=='lea' and i.address+i.size+i.operands[1].mem.disp==IMAGE,'Unexpected base LEA')
            mutation=bytearray(code);offset=i.address-entry+i.disp_offset
            disp=int.from_bytes(mutation[offset:offset+4],'little',signed=True)
            mutation[offset:offset+4]=(disp+1).to_bytes(4,'little',signed=True)
            wrong=analyze(bytes(mutation),entry,set(range(table,table+128,32)),lambda a,n:pe_read(game,a,n))
            require(f'{jump:x}' in wrong['unresolved'],'Incorrect distant base accepted')
            negatives.append(dict(kind='image-base-plus-one',address=f'{address:x}',rejected_branch=f'{jump:x}'))
        result.update(status='normal-entry-gpr-flow-verified',source_sha256=GAME_SHA,
                      analyzer_sha256=digest(Path(__file__)),code_sha256=report['code_sha256'],negative_cases=negatives,
                      scope='Conservative GPR interpretation from normal entry; normal Win64 ABI calls return with nonvolatile registers preserved. No exception edges, self-modification, external jumps into the body, or floating-point semantics modeled.')
        path=root/'normal-flow.json';path.write_text(json.dumps(result,indent=2),encoding='utf-8')
        manifest=read_json(root/'manifest.json');manifest['files'][path.name]=digest(path)
        (root/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')

if __name__=='__main__':main()
