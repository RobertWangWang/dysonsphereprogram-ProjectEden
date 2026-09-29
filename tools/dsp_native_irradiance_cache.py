"""模拟三个辐照度入口的哈希/浮点缓存更新片段；范围止于更新之后。"""
import json
import hashlib
import math
import struct
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

PROFILES=[('1813b1070',0x1813b11da,0x1813b122e,'R15','RBP',-0x64,'XMM9'),
          ('1813b5460',0x1813b5573,0x1813b55c7,'R15','RBP',-0x48,'XMM10'),
          ('1813b9780',0x1813b984c,0x1813b989d,'RSI','RSP',0x68,'XMM9')]


def validate_cached(folder,sha):
    root=folder/'irradiance-cache'
    if not (root/'manifest.json').exists():return {}
    marker=read_json(root/'manifest.json');require(marker['source_sha256']==sha,'Cache evidence baseline changed')
    for name,value in marker['files'].items():require(digest(root/name)==value,'Cache evidence changed')
    return read_json(root/'report.json')


def main():
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64
    from unicorn import x86_const as r
    folder=GENERATED/'native/UnityPlayer.dll';game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game changed');game=game_path.read_bytes()
    require(pe_read(game,0x181aec8b4,4)==struct.pack('<I',0x34000000),'Threshold changed')
    require(pe_read(game,0x181aed1f0,4)==struct.pack('<I',0xbf800000),'Reset marker changed')
    require(pe_read(game,0x181aef000,16)==struct.pack('<IIII',*[0x7fffffff]*4),'Absolute mask changed')
    bits=[0,0x80000000,1,0x007fffff,0x00800000,0x33ffffff,0x34000000,0x34000001,
          0xb4000000,0x3f800000,0x3f800001,0x3f800002,0xbf800000,0x7f7fffff,0xff7fffff,
          0x7f800000,0xff800000,0x7fc00001]
    def f32(b):return struct.unpack('<f',struct.pack('<I',b))[0]
    def rounded(value):
        try:return struct.unpack('<f',struct.pack('<f',value))[0]
        except OverflowError:return math.copysign(math.inf,value)
    cases=[]
    for name,start,end,cache_reg,local_reg,offset,xmm_reg in PROFILES:
        m=Uc(UC_ARCH_X86,UC_MODE_64)
        for page in (start&~4095,0x181aec000,0x181aed000,0x181aef000,0x2000000,0x2100000,0x3000000):m.mem_map(page,4096)
        m.mem_write(start,pe_read(game,start,end-start))
        for address,size in ((0x181aec8b4,4),(0x181aed1f0,4),(0x181aef000,16)):m.mem_write(address,pe_read(game,address,size))
        for current in bits:
            for previous in bits:
                for hash_changed in (False,True):
                    prior=bytes([0xa5])*4096;m.mem_write(0x2000000,prior);m.mem_write(0x2100000,prior);m.mem_write(0x3000000,prior)
                    m.mem_write(0x200001c,struct.pack('<II',0xabcdef01,previous));m.mem_write(0x210002c,struct.pack('<I',current))
                    for reg,value in ((cache_reg,0x2000000),('R13',0x2100000),('RBX',7),('RBP',0x3000800),('RSP',0x3000600),('RAX',0x12345678 if hash_changed else 0xabcdef01),(xmm_reg,0x3f000000),('MXCSR',0x1f80)):
                        m.reg_write(getattr(r,'UC_X86_REG_'+reg),value)
                    local=(0x3000800 if local_reg=='RBP' else 0x3000600)+offset
                    m.mem_write(local,struct.pack('<I',0x3f000000));m.emu_start(start,end,count=60)
                    delta=abs(rounded(f32(current)-f32(previous)))
                    float_changed=delta>f32(0x34000000)
                    expected_hash=0x12345678 if hash_changed else 0xabcdef01
                    expected_float=current if float_changed else previous
                    expected_marker=0xbf800000 if hash_changed or float_changed else 0x3f000000
                    require(bytes(m.mem_read(0x200001c,8))==struct.pack('<II',expected_hash,expected_float),'Cache update differs')
                    require(bytes(m.mem_read(local,4))==struct.pack('<I',expected_marker),'Local marker differs')
                    require(m.reg_read(getattr(r,'UC_X86_REG_'+xmm_reg))&0xffffffff==expected_marker,'Vector marker differs')
                    # Verify complete cache and input blocks, including untouched neighbours.
                    expected_cache=bytearray(prior);expected_cache[28:36]=struct.pack('<II',expected_hash,expected_float)
                    expected_input=bytearray(prior);expected_input[44:48]=struct.pack('<I',current)
                    require(bytes(m.mem_read(0x2000000,4096))==bytes(expected_cache) and bytes(m.mem_read(0x2100000,4096))==bytes(expected_input),'Unexpected data write')
                    cases.append(dict(function=name,current_bits=f'{current:08x}',previous_bits=f'{previous:08x}',hash_changed=hash_changed,float_changed=float_changed,marker_bits=f'{expected_marker:08x}'))
    root=folder/'irradiance-cache';root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    report=dict(status='sampled-cache-update-verified',source_sha256=GAME_SHA,case_count=len(cases),cases=cases,
        fragments=[dict(function=name,start=f'{start:x}',end_exclusive=f'{end:x}',code_sha256=hashlib.sha256(pe_read(game,start,end-start)).hexdigest()) for name,start,end,*_ in PROFILES],
        threshold_bits='34000000',threshold=2**-23,reset_marker_bits='bf800000',mxcsr='1f80',
        limitation='Post-hash fragments only; finite/quiet-NaN/infinity samples with exceptions masked, round-nearest and no DAZ/FTZ. No full cache policy, unmasked exceptions, signaling NaNs or later marker use proof.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(f'{len(cases)} cache fragment cases passed, including threshold neighbours, signed zero, infinity and quiet NaN')


if __name__=='__main__':main()
