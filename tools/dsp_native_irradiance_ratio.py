"""恢复阈值筛选的完整 SSE 比值片段；BSS 全局向量作为显式输入。"""
import json
import math
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

PROFILES=[('1813b1070',0x1813b1686,0x1813b16e4),('1813b5460',0x1813b5a39,0x1813b5a97),('1813b9780',0x1813b9ca6,0x1813b9d08)]


def main():
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64
    from unicorn import x86_const as r
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    folder=GENERATED/'native/UnityPlayer.dll';game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game changed');game=game_path.read_bytes()
    def raw_float(x):
        try:return struct.pack('<f',x)
        except OverflowError:return struct.pack('<f',math.copysign(math.inf,x))
    def rounded(x):return struct.unpack('<f',raw_float(x))[0]
    def bits(x):return struct.unpack('<I',raw_float(x))[0]
    def same(actual,expected):
        value=struct.unpack('<f',struct.pack('<I',actual))[0]
        return math.isnan(value) if math.isnan(expected) else actual==bits(expected)
    def div(a,b):
        if math.isnan(a) or math.isnan(b) or (math.isinf(a) and math.isinf(b)) or (a==0 and b==0):return math.nan
        if b==0:return math.copysign(math.inf,math.copysign(1,a)*math.copysign(1,b))
        return rounded(a/b)
    vectors=[(0.,0.,0.,0.),(1.,2.,3.,4.),(-1.,-2.,-3.,-4.),(1.,-1.,1.,-1.),
             (1e30,2e30,3e30,4e30),(1e-40,2e-40,3e-40,4e-40),
             (math.inf,1.,2.,3.),(1.,2.,3.,math.inf),(math.nan,1.,2.,3.),(1.,2.,3.,math.nan)]
    previous_values=[-math.inf,-1.,-0.,0.,1.,math.inf,math.nan]
    biases=[-1.,0.,0.125,1.]
    configs=[((1.,1.,1.,1.),(0xffffffff,)*4),((1.,2.,4.,8.),(0xffffffff,0xffffffff,0xffffffff,0)),
             ((-1.,-0.5,0.25,-2.),(0x7fffffff,)*4)]
    globals_root=folder/'irradiance-globals';globals_manifest=read_json(globals_root/'manifest.json')
    require(globals_manifest['source_sha256']==GAME_SHA,'Global initialization baseline changed')
    for name,sha in globals_manifest['files'].items():require(digest(globals_root/name)==sha,'Global initialization evidence changed')
    globals_report=read_json(globals_root/'report.json')
    outputs={row['target']:bytes.fromhex(row['output_hex']) for row in globals_report['initializers']}
    initial_coefficients=struct.unpack('<ffff',outputs['181c82010'])
    require(b''.join(raw_float(x) for x in initial_coefficients)==outputs['181c82010'],'NaN bit pattern changed during float conversion')
    configs.append((initial_coefficients,struct.unpack('<IIII',outputs['181c81fb0'])))
    cases=[];artifacts={}
    for name,start,end in PROFILES:
        m=Uc(UC_ARCH_X86,UC_MODE_64)
        for page in (start&~4095,0x181c82000,0x181c81000,0x2000000,0x3000000):m.mem_map(page,4096)
        code=pe_read(game,start,end-start);m.mem_write(start,code)
        instructions=list(Cs(CS_ARCH_X86,CS_MODE_64).disasm(code,start));require(sum(i.size for i in instructions)==len(code),'Ratio decode incomplete')
        artifacts[name+'.asm']=''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in instructions)
        for config,(coefficients,masks) in enumerate(configs):
            m.mem_write(0x181c82010,b''.join(raw_float(x) for x in coefficients));m.mem_write(0x181c81fb0,struct.pack('<IIII',*masks))
            weights=[struct.unpack('<f',struct.pack('<I',bits(x)&mask))[0] for x,mask in zip(coefficients,masks)]
            for vector_index,vector in enumerate(vectors):
                v=[rounded(x) for x in vector];packed=b''.join(raw_float(x) for x in v)
                for previous in previous_values:
                    for bias in biases:
                        m.mem_write(0x2000000,raw_float(previous))
                        for reg,value in ((r.UC_X86_REG_RAX,0),(r.UC_X86_REG_RCX,0x2000000),(r.UC_X86_REG_RBP,0x3000800),(r.UC_X86_REG_RSP,0x3000600),
                                          (r.UC_X86_REG_XMM8,int.from_bytes(packed,'little')),(r.UC_X86_REG_XMM3,0),(r.UC_X86_REG_XMM4,bits(bias)),(r.UC_X86_REG_MXCSR,0x1f80)):
                            m.reg_write(reg,value)
                        m.emu_start(start,end,count=60)
                        p=[rounded(a*b) for a,b in zip(v,weights)]
                        reduced=rounded(rounded(p[1]+p[3])+rounded(p[0]+p[2]))
                        denominator=rounded(reduced+bias)
                        numerator=rounded(v[3]+(previous if previous>0 else 0.))
                        ratio=div(numerator,denominator)
                        actual_cache=struct.unpack('<I',bytes(m.mem_read(0x2000000,4)))[0]
                        actual_ratio=m.reg_read(r.UC_X86_REG_XMM1)&0xffffffff
                        require(same(actual_cache,numerator) and same(actual_ratio,ratio),'Ratio model differs')
                        cases.append(dict(function=name,config=config,vector=vector_index,previous_bits=f'{bits(previous):08x}',bias_bits=f'{bits(bias):08x}',cache_bits=f'{actual_cache:08x}',ratio_bits=f'{actual_ratio:08x}'))
    root=folder/'irradiance-ratio';root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    for name,text in artifacts.items():(root/name).write_text(text,encoding='utf-8')
    report=dict(status='sampled-ratio-fragment-verified',source_sha256=GAME_SHA,case_count=len(cases),cases=cases,
        coefficient_address='181c82010',bitmask_address='181c81fb0',mxcsr='1f80',
        actual_initialization_config=3,global_evidence_manifest_sha256=digest(globals_root/'manifest.json'),
        inputs=dict(vectors_bits=[[f'{bits(v):08x}' for v in row] for row in vectors],configs=[dict(coefficients_bits=[f'{bits(v):08x}' for v in c],masks=[f'{v:08x}' for v in masks]) for c,masks in configs]),
        limitation='Explicit synthetic global vectors, cache and XMM inputs; actual BSS initialization and upstream dataflow not verified. Each arithmetic step rounds to binary32. NaN classification compared without requiring payload/sign or MXCSR exception-flag equivalence; default masked environment only.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print(f'{len(cases)} complete SSE ratio fragments match binary32 step model')


if __name__=='__main__':main()
