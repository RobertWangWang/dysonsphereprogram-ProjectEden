"""Original x86 behavior probes for recovered copy and numeric-reference parser bodies."""
import collections
import json
import random
import struct
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require
from dsp_native_continuation_bodies import validate_cached as validate_bodies


def validate_cached(folder, sha):
    root = folder / 'continuation-behavior'
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json'); require(marker['source_sha256'] == sha, 'Behavior source differs')
    for name, value in marker['files'].items(): require(digest(root / name) == value, 'Behavior evidence differs')
    result = read_json(root / 'report.json')
    for name, value in result['dependencies'].items(): require(digest(folder / name) == value, 'Body dependency differs')
    return result


def main():
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE, UC_HOOK_MEM_WRITE
    import unicorn.x86_const as reg
    inv = read_json(GENERATED / 'native/inventory.json'); module = next(m for m in inv['files'] if Path(m['path']).name == 'rail_api.dll')
    folder = GENERATED / 'native' / module['output']; path = Path(inv['game_directory']) / module['path']
    require(digest(path) == module['sha256'], 'Game changed'); game = path.read_bytes()
    bodies = validate_bodies(folder, module['sha256']); require(bodies, 'Missing verified bodies')
    uc = Uc(UC_ARCH_X86, UC_MODE_32)
    for address, size in [(0x1062f000,0x1000),(0x10632000,0x1000),(0x10bca000,0x1000),(0x200000,0x1000),(0x300000,0x1000),(0x400000,0x1000),(0x500000,0x1000)]: uc.mem_map(address,size)
    uc.mem_write(0x1062fb70,pe_read(game,0x1062fb70,291));uc.mem_write(0x10632ce0,pe_read(game,0x10632ce0,106))
    validity=pe_read(game,0x10bca31c,256);uc.mem_write(0x10bca31c,validity);uc.mem_write(0x500000,b'\xf4')
    stack=0x200800;arena=0x300000;slots=0x400000;sentinel=0x500000
    coverage={'copy':set(),'parser':set()};profile=['copy'];writes=[];returned=[False]
    preserved={reg.UC_X86_REG_EBX:0xa5a5a5a5,reg.UC_X86_REG_EBP:0xb6b6b6b6,reg.UC_X86_REG_ESI:0xc7c7c7c7,reg.UC_X86_REG_EDI:0xd8d8d8d8}
    def code_hook(machine,address,size,unused):
        if address==sentinel:returned[0]=True;machine.emu_stop()
        else:coverage[profile[0]].add(address)
    uc.hook_add(UC_HOOK_CODE,code_hook)
    uc.hook_add(UC_HOOK_MEM_WRITE,lambda machine,access,address,size,value,unused:writes.append((address,size)))
    def execute(entry,args):
        uc.mem_write(stack,struct.pack('<'+'I'*(len(args)+1),sentinel,*args));uc.reg_write(reg.UC_X86_REG_ESP,stack);uc.reg_write(reg.UC_X86_REG_EFLAGS,2)
        for r,v in preserved.items():uc.reg_write(r,v)
        writes.clear();returned[0]=False;uc.emu_start(entry,0,count=30000)
        require(returned[0] and uc.reg_read(reg.UC_X86_REG_ESP)==stack+4,'Return/stack mismatch')
        require(all(uc.reg_read(r)==v for r,v in preserved.items()),'Preserved register mismatch')
        return uc.reg_read(reg.UC_X86_REG_EAX)
    copy_cases=0;copy_examples=[]
    def check_copy(payload,length,capacity,source=128,destination=1024):
        nonlocal copy_cases
        original=bytearray([0xa5])*2048;original[source:source+len(payload)]=payload;expected=bytearray(original)
        stop=source+length
        if length>capacity:
            stop=source+capacity
            while stop>source and expected[stop-1]&0xc0==0x80:stop-=1
        amount=max(0,stop-source)
        for i in range(amount):expected[destination+i]=expected[source+i]
        uc.mem_write(arena,bytes(original));uc.mem_write(slots,struct.pack('<II',arena+source,arena+destination))
        actual=execute(0x10632ce0,[0,slots,arena+source+length,slots+4,arena+destination+capacity])
        require(actual==arena+source+amount,'Copy EAX mismatch')
        require(bytes(uc.mem_read(slots,8))==struct.pack('<II',arena+source+amount,arena+destination+amount),'Copy cursor mismatch')
        require(bytes(uc.mem_read(arena,2048))==bytes(expected),'Copy arena mismatch')
        require(all(stack-16<=a and a+n<=stack or slots<=a and a+n<=slots+8 or arena+destination<=a and a+n<=arena+destination+amount for a,n in writes),'Copy unexpected write')
        copy_cases+=1
        return amount
    rng=random.Random(0x10632ce0)
    for length in range(65):
        payload=bytes(rng.randrange(256) for _ in range(length))
        for capacity in range(65):check_copy(payload,length,capacity)
    for last in range(256):
        for capacity in range(1,9):check_copy(bytes([last])*12,12,capacity)
    for destination in (120,124,128,129,132,256):
        for length in range(33):
            payload=bytes(rng.randrange(256) for _ in range(length))
            for capacity in range(33):check_copy(payload,length,capacity,destination=destination)
    for length in (-8,-1,0,1,8):
        for capacity in (-8,-1,0,1,8):check_copy(bytes(range(32)),length,capacity)
    for payload,capacity in [(b'\xe2\x82\xac',1),(b'\xe2\x82\xac',2),(b'\xe2\x82\xac',3),(b'\x80'*8,5)]:
        amount=check_copy(payload,len(payload),capacity);copy_examples.append(dict(input_hex=payload.hex(),capacity=capacity,copied=amount,output_hex=payload[:amount].hex()))
    # Mutate only the compare immediate, and invalidate translated code before the negative test.
    uc.mem_write(0x10632d18,b'\x00');uc.ctl_remove_cache(0x10632ce0,0x10632d4a)
    negative_copy=False
    try:check_copy(b'\x80'*8,8,5)
    except (ValueError,AssertionError):negative_copy=True
    require(negative_copy,'Wrong continuation comparison escaped detector')
    uc.mem_write(0x10632d18,b'\x80');uc.ctl_remove_cache(0x10632ce0,0x10632d4a)
    profile[0]='parser';parser_cases=0;parser_examples=[];return_counts=collections.Counter()
    def signed(value):return value if value<0x80000000 else value-0x100000000
    def model(values):
        position=2;value=0;hexadecimal=values[position]==ord('x')
        if hexadecimal:position+=1
        while values[position]!=ord(';'):
            word=values[position];digit=word if word<128 else word-256 if word<256 else -1
            if hexadecimal:
                if 48<=digit<=57:value=((value<<4)|(digit-48))&0xffffffff
                elif 65<=digit<=70:value=((value<<4)+digit-55)&0xffffffff
                elif 97<=digit<=102:value=((value<<4)+digit-87)&0xffffffff
            else:value=(value*10+digit-48)&0xffffffff
            if signed(value)>=0x110000:return 0xffffffff
            position+=1
        high=signed(value)>>8
        if 0xd8<=high<=0xdf or (high==0 and validity[value]==0) or value in (0xfffe,0xffff):return 0xffffffff
        return value
    def check_parser(values,example=False):
        nonlocal parser_cases
        require(len(values)<128 and values[-1]==59,'Parser needs readable terminator')
        payload=struct.pack('<'+'H'*len(values),*values);original=payload+bytes(512-len(payload));uc.mem_write(arena,original)
        expected=model(values);actual=execute(0x1062fb70,[0,arena])
        require(actual==expected,'Parser return mismatch')
        require(bytes(uc.mem_read(arena,512))==original and bytes(uc.mem_read(0x10bca31c,256))==validity,'Parser modified data')
        require(all(stack-8<=a and a+n<=stack for a,n in writes),'Parser unexpected write')
        parser_cases+=1;return_counts['rejected' if actual==0xffffffff else 'other-negative' if signed(actual)<0 else 'nonnegative']+=1
        if example:parser_examples.append(dict(units=values,result=signed(actual)))
    # Exhaust every single 16-bit input unit in decimal and hexadecimal modes.
    for word in range(65536):
        check_parser([38,35,word,59]);check_parser([38,35,120,word,59])
    values_to_test=set(range(1024))|set(range(0xd7f0,0xe010))|set(range(0xfff0,0x10010))|set(range(0x10fff0,0x110010))
    for value in sorted(values_to_test):
        for text in ('&#'+str(value)+';','&#x'+format(value,'x')+';','&#x'+format(value,'X')+';'):check_parser(list(map(ord,text)))
    for _ in range(4096):
        digits=[rng.choice((48,49,57,65,70,97,102,71,0,0x80,0x100,0xd800)) for _ in range(rng.randrange(1,12))]
        check_parser([38,35]+([120] if rng.randrange(2) else [])+digits+[59])
    for text in ('&#65;','&#x41;','&#xG41;','&#-;','AB65;','&#55296;','&#65534;','&#1114112;','&#x;'):
        check_parser(list(map(ord,text)),True)
    # Change hex digit shift from 4 to 3; multi-digit case must detect the error.
    uc.mem_write(0x1062fbb8,b'\x03');uc.ctl_remove_cache(0x1062fb70,0x1062fc4c)
    negative_parser=False
    try:check_parser(list(map(ord,'&#x12;')))
    except (ValueError,AssertionError):negative_parser=True
    require(negative_parser,'Wrong parser shift escaped detector')
    uc.mem_write(0x1062fbb8,b'\x04');uc.ctl_remove_cache(0x1062fb70,0x1062fc4c)
    for name,address in [('copy','10632ce0'),('parser','1062fb70')]:
        row=next(r for r in bodies['functions'] if r['address']==address)
        expected={int(line.split(' ',1)[0],16) for line in (folder/'continuation-body-repair'/(address+'.asm')).read_text().splitlines()}-{int(p,16) for p in row['padding']}
        require(coverage[name]==expected,'Incomplete dynamic instruction coverage for '+name)
    root=folder/'continuation-behavior';root.mkdir(exist_ok=True)
    result=dict(source_sha256=module['sha256'],copy=dict(cases=copy_cases,instructions=len(coverage['copy']),examples=copy_examples,negative_control_caught=negative_copy),
                parser=dict(cases=parser_cases,instructions=len(coverage['parser']),examples=parser_examples,returns=dict(return_counts),validity_table_hex=validity.hex(),negative_control_caught=negative_parser),
                dependencies={'continuation-body-repair/manifest.json':digest(folder/'continuation-body-repair/manifest.json')},
                limitation='Finite original-instruction probes versus independently written models. Copy ranges are allocated, small and nonwrapping; overlapping copies use forward-byte behavior. Parser inputs terminate in semicolon. No caller-side validation or full game execution tested; Ghidra C is not compiled here.')
    (root/'report.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=module['sha256'],files={'report.json':digest(root/'report.json')}),indent=2),encoding='utf-8')
    print(json.dumps(result))


if __name__=='__main__':main()
