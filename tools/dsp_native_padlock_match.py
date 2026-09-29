"""逐字节核验 OpenSSL PadLock SHA-512 过程，保留未识别指令的原始字节。"""
import argparse
import json
import shutil
import subprocess
from pathlib import Path
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import coff, pe_read
from dsp_native_openssl_match import GAME_SHA, COMMIT, INPUTS, require

SOURCE_SHA = '020e2469de5f1c7543b4b28948636c16656046e263e7e0b4f652e4325d379ff1'
ENTRY, SIZE, OPAQUE_OFFSET = 0x180015e40, 129, 72


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('reference','perl','nasm'):
        parser.add_argument('--'+name, type=Path, required=True)
    args = parser.parse_args()
    base = GENERATED/'native'
    inventory = read_json(base/'inventory.json')
    module = 'DSPGAME_Data/Plugins/x86_64/rail_api64.dll'
    game_path = Path(inventory['game_directory'])/module
    require(digest(game_path) == GAME_SHA, 'Game baseline changed')
    root = base/module.replace('/','__')/'upstream-openssl/padlock-sha512'
    root.mkdir(parents=True, exist_ok=True)
    (root/'manifest.json').unlink(missing_ok=True)
    inputs = {'e_padlock-x86_64.pl':SOURCE_SHA, 'x86_64-xlate.pl':INPUTS['x86_64-xlate.pl']}
    for name, sha in inputs.items():
        require(digest(args.reference/name) == sha, 'Upstream input changed: '+name)
    for name in [*inputs,'LICENSE']:
        shutil.copyfile(args.reference/name, root/name)
    subprocess.run([str(args.perl.resolve()),'e_padlock-x86_64.pl','nasm','padlock.asm'],cwd=root,check=True,capture_output=True)
    subprocess.run([str(args.nasm.resolve()),'-f','win64','padlock.asm','-o','padlock.obj'],cwd=root,check=True,capture_output=True)
    obj = (root/'padlock.obj').read_bytes()
    sections, symbols = coff(obj)
    names = {n:(v,s) for n,v,s in symbols.values()}
    start, section = names['padlock_sha512_blocks']
    end, end_section = names['L$SEH_end_padlock_sha512_blocks']
    require(section == end_section and sections[section-1][0] == '.text', 'Unexpected object layout')
    name, code, relocations, relocation_count = sections[section-1]
    # Any relocation in this procedure would require separate verification.
    import struct
    for i in range(relocation_count):
        offset = struct.unpack_from('<I',obj,relocations+10*i)[0]
        require(not start <= offset < end, 'Unexpected function relocation')
    rebuilt = bytes(code[start:end])
    game = game_path.read_bytes()
    actual = pe_read(game,ENTRY,SIZE)
    require(len(rebuilt) == SIZE and rebuilt == actual, 'Machine-code mismatch')
    require(actual[OPAQUE_OFFSET:OPAQUE_OFFSET+4] == bytes.fromhex('f30fa6e0'), 'Opaque instruction mismatch')
    # Decode only the independently delimited surrounding instructions. Never skip arbitrary bytes.
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    before = list(decoder.disasm(actual[:OPAQUE_OFFSET],ENTRY))
    after = list(decoder.disasm(actual[OPAQUE_OFFSET+4:],ENTRY+OPAQUE_OFFSET+4))
    require(sum(i.size for i in before) == OPAQUE_OFFSET and sum(i.size for i in after) == SIZE-OPAQUE_OFFSET-4,
            'Incomplete surrounding instruction decoding')
    def listing(instructions):
        return ''.join(f'{i.address:x}  {i.bytes.hex()}  {i.mnemonic} {i.op_str}\n' for i in instructions)
    (root/'game.asm').write_text(listing(before)+f'{ENTRY+OPAQUE_OFFSET:x}  f30fa6e0  DB 0xf3,0x0f,0xa6,0xe0 ; exact upstream literal; no invented p-code\n'+listing(after),encoding='utf-8')
    changed = bytearray(actual); changed[OPAQUE_OFFSET+3] ^= 1
    require(bytes(changed) != rebuilt, 'Opaque-byte mutation accepted')
    report = dict(status='byte-exact-source-matched',source_sha256=GAME_SHA,module=module,
                  name='padlock_sha512_blocks',address=f'{ENTRY:x}',body_entry=f'{ENTRY+13:x}',end_exclusive=f'{ENTRY+SIZE:x}',
                  assembly_file='padlock.asm',game_bytes=SIZE,rebuilt_bytes=len(rebuilt),
                  opaque_instruction=dict(address=f'{ENTRY+OPAQUE_OFFSET:x}',bytes='f30fa6e0',representation='upstream DB literal'),
                  upstream_commit=COMMIT,upstream_url=f'https://github.com/openssl/openssl/blob/{COMMIT}/engines/asm/e_padlock-x86_64.pl',
                  negative_opaque_byte_mutation_rejected=True,
                  limitation='Entire procedure bytes match. The special hardware instruction remains a DB literal; no claim of recovered C or complete hardware semantics.',
                  nasm_sha256=digest(args.nasm),perl_sha256=digest(args.perl))
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    files = {p.name:digest(p) for p in root.iterdir() if p.is_file() and p.name!='manifest.json'}
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=GAME_SHA,files=files),indent=2),encoding='utf-8')
    print('Verified all 129 bytes, including previously undecoded instruction; byte-exact-source-matched')


if __name__ == '__main__':
    main()
