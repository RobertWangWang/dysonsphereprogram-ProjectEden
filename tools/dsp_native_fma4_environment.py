"""核验 GEO_DISABLE_FMA4 初始化片段，CRT 环境查询由受控返回值替代。"""
import hashlib
import json
from pathlib import Path
from dsp_knowledge import GENERATED,digest,read_json
from dsp_native_source_match import GAME_SHA,pe_read,require

ENTRY=0x180040e90
GETENV=0x181851040
GLOBAL=0x181c80780
NAME=0x18189c910


def validate_cached(folder,sha):
    root=folder/'fma4-environment'
    if not (root/'manifest.json').exists():return {}
    manifest=read_json(root/'manifest.json');require(manifest['source_sha256']==sha,'Environment evidence baseline changed')
    for name,value in manifest['files'].items():require(digest(root/name)==value,'Environment evidence changed')
    return read_json(root/'report.json')


def main():
    from capstone import Cs,CS_ARCH_X86,CS_MODE_64
    from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE,UC_HOOK_MEM_WRITE
    from unicorn import x86_const as r
    folder=GENERATED/'native/UnityPlayer.dll'
    game_path=Path(read_json(GENERATED/'native/inventory.json')['game_directory'])/'UnityPlayer.dll'
    require(digest(game_path)==GAME_SHA,'Game changed');game=game_path.read_bytes()
    code=pe_read(game,ENTRY,31);name=pe_read(game,NAME,17)
    require(name==b'GEO_DISABLE_FMA4\0','Environment name changed')
    instructions=list(Cs(CS_ARCH_X86,CS_MODE_64).disasm(code,ENTRY))
    require(sum(i.size for i in instructions)==31,'Initializer decode incomplete')
    cases=[]
    for value in (None,b'0\0',b'1\0',b'false\0',b'\0'):
        for previous in (0,1,255):
            m=Uc(UC_ARCH_X86,UC_MODE_64)
            for page in (ENTRY&~4095,GLOBAL&~4095,NAME&~4095,GETENV&~4095,0x2000000,0x3000000,0x4000000):m.mem_map(page,4096)
            m.mem_write(ENTRY,code);m.mem_write(NAME,name);m.mem_write(GETENV,b'\xc3');m.mem_write(GLOBAL,bytes([previous]))
            if value is not None:m.mem_write(0x2000000,value)
            stack=0x3000808;m.mem_write(stack,(0x4000000).to_bytes(8,'little'));m.reg_write(r.UC_X86_REG_RSP,stack)
            calls=[];returned=[];writes=[]
            def visit(uc,pc,size,_):
                if pc==GETENV:
                    calls.append(uc.reg_read(r.UC_X86_REG_RCX))
                    uc.reg_write(r.UC_X86_REG_RAX,0 if value is None else 0x2000000)
                elif pc==0x4000000:returned.append(True);uc.emu_stop()
            def write(uc,access,address,size,v,_):
                if address==GLOBAL:writes.append((size,v))
                else:require(0x3000000<=address and address+size<=0x3001000,'Unexpected initializer write')
            m.hook_add(UC_HOOK_CODE,visit);m.hook_add(UC_HOOK_MEM_WRITE,write);m.emu_start(ENTRY,0,count=30)
            expected=int(value is not None)
            require(calls==[NAME] and writes==[(1,expected)],'Environment query/store differs')
            require(returned==[True] and m.reg_read(r.UC_X86_REG_RSP)==stack+8,'Initializer return differs')
            cases.append(dict(returned_string=None if value is None else value[:-1].decode(),previous=previous,disabled=expected))
    root=folder/'fma4-environment';root.mkdir(exist_ok=True);(root/'manifest.json').unlink(missing_ok=True)
    report=dict(status='environment-initializer-sampled',source_sha256=GAME_SHA,address=f'{ENTRY:x}',getter=f'{GETENV:x}',
        global_address=f'{GLOBAL:x}',environment_name='GEO_DISABLE_FMA4',name_address=f'{NAME:x}',
        code_sha256=hashlib.sha256(code).hexdigest(),cases=cases,
        expression='global_byte = (common_getenv<char>("GEO_DISABLE_FMA4") != NULL)',
        limitation='CRT return values simulated, including a synthetic non-null empty string. No claim that the real CRT returns that value for an empty environment variable; initializer invocation timing and complete environment implementation not verified.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'initializer.asm').write_text(''.join(f'{i.address:x} {i.bytes.hex()} {i.mnemonic} {i.op_str}\n' for i in instructions),encoding='utf-8')
    (root/'getter.c').write_text((folder/'functions/181851040.c').read_text(encoding='utf-8'),encoding='utf-8')
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files={p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}),indent=2),encoding='utf-8')
    print('15 initializer cases passed; non-null getenv return sets disable flag regardless of contents')


if __name__=='__main__':main()
