"""Differential-test a compiled reconstruction against original scanner and classifier x86."""
import ctypes
import json
import random
import struct
import subprocess
from pathlib import Path
from dsp_knowledge import GENERATED, digest, read_json
from dsp_native_source_match import pe_read, require


def validate_cached(folder, sha):
    root = folder / 'scanner-reference'
    if not (root / 'manifest.json').exists(): return {}
    marker = read_json(root / 'manifest.json'); require(marker['source_sha256'] == sha, 'Scanner reference source differs')
    for name, value in marker['files'].items(): require(digest(root / name) == value, 'Scanner reference artifact differs')
    report = read_json(root / 'report.json')
    for name, value in report['dependencies'].items(): require(digest(folder / name) == value, 'Scanner reference dependency differs')
    return report


def main():
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE, UC_HOOK_MEM_WRITE, UC_HOOK_MEM_READ
    import unicorn.x86_const as reg
    base = GENERATED / 'native/DSPGAME_Data__Plugins__x86_64__rail_api.dll'; root = base / 'scanner-reference'
    source = Path(__file__).with_name('dsp_scanner_reference.c')
    root.mkdir(exist_ok=True)
    build = ['cl.exe','/nologo','/LD','/O2','/GS-', '/Fo'+str((root/'reference.obj').resolve()), str(source.resolve()),
             '/link','/NOENTRY','/NODEFAULTLIB','/OUT:'+str((root/'reference.dll').resolve()), '/IMPLIB:'+str((root/'reference.lib').resolve())]
    compilation = subprocess.run(build, capture_output=True)
    require(compilation.returncode == 0, 'Reference compilation failed: '+compilation.stdout.decode(errors='replace')+compilation.stderr.decode(errors='replace'))
    game_path = Path(read_json(GENERATED / 'native/inventory.json')['game_directory']) / 'DSPGAME_Data/Plugins/x86_64/rail_api.dll'
    sha = 'c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1'
    require(digest(game_path) == sha, 'Source changed'); game = game_path.read_bytes()
    library = ctypes.CDLL(str((root / 'reference.dll').resolve())); reference = library.dsp_scan
    reference.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]; reference.restype = ctypes.c_int
    uc = Uc(UC_ARCH_X86, UC_MODE_32)
    for address, size in [(0x10632000, 0x1000), (0x10639000, 0x1000), (0x200000, 0x2000), (0x300000, 0x10000), (0x500000, 0x1000)]: uc.mem_map(address, size)
    uc.mem_write(0x10632090, pe_read(game, 0x10632090, 0x1df)); uc.mem_write(0x106392d0, pe_read(game, 0x106392d0, 0x74)); uc.mem_write(0x500000, b'\xf4')
    visited, returns = set(), {}; unexpected_writes = []; overread = [False]; domain = [0, 0]; outside_reads = []
    profile_name = ['synthetic']; coverage_by_profile = {'synthetic': set(), 'static-tables': set()}
    def code_hook(machine, address, size, unused):
        if address == 0x500000: machine.emu_stop()
        else: visited.add(address); coverage_by_profile[profile_name[0]].add(address)
    def write_hook(machine, access, address, size, value, unused):
        if not (0x200000 <= address and address + size <= 0x202000) and not (0x308000 <= address and address + size <= 0x308004): unexpected_writes.append((address, size))
    def read_hook(machine, access, address, size, value, unused):
        if 0x300000 <= address < 0x301000 and not (domain[0] <= address and address + size <= domain[1]):
            overread[0] = True; outside_reads.append(dict(offset=address-domain[0], size=size))
    uc.hook_add(UC_HOOK_CODE, code_hook); uc.hook_add(UC_HOOK_MEM_WRITE, write_hook); uc.hook_add(UC_HOOK_MEM_READ, read_hook)
    preserved = {reg.UC_X86_REG_EBX: 0xa5a5a5a5, reg.UC_X86_REG_EBP: 0xb6b6b6b6, reg.UC_X86_REG_ESI: 0xc7c7c7c7, reg.UC_X86_REG_EDI: 0xd8d8d8d8}
    count = 0; logical_overreads = 0; examples = []; overread_examples = []
    def check(data, table, length=None, offset=0):
        nonlocal count, logical_overreads
        if length is None: length = len(data)
        require(len(table) == 256 and table[0] == 0 and len(data) < 256 and 0 <= length <= len(data), 'Unsafe synthetic case')
        arena = bytes([0]) * offset + data + bytes(512 - offset - len(data))
        input_buffer = ctypes.create_string_buffer(arena); table_buffer = ctypes.create_string_buffer(bytes(table)); output = ctypes.c_void_p(0xdeadbeef)
        c_begin = ctypes.addressof(input_buffer) + offset
        expected_return = reference(ctypes.addressof(table_buffer), c_begin, c_begin + length, ctypes.byref(output))
        expected_output = 0xdeadbeef if output.value == 0xdeadbeef else 0x300000 + offset + output.value - c_begin
        uc.mem_write(0x300000, arena); uc.mem_write(0x30404c, bytes(table)); uc.mem_write(0x308000, struct.pack('<I', 0xdeadbeef))
        args = struct.pack('<IIIII', 0x500000, 0x304000, 0x300000 + offset, 0x300000 + offset + length, 0x308000)
        uc.mem_write(0x200800, args); uc.reg_write(reg.UC_X86_REG_ESP, 0x200800); uc.reg_write(reg.UC_X86_REG_EFLAGS, 2)
        for register, value in preserved.items(): uc.reg_write(register, value)
        domain[:] = [0x300000 + offset, 0x300000 + offset + length]; overread[0] = False; outside_reads.clear()
        uc.emu_start(0x10632090, 0, count=10000)
        require(uc.reg_read(reg.UC_X86_REG_EIP) == 0x500000, 'Original scanner did not return within limit')
        require(uc.reg_read(reg.UC_X86_REG_EAX) == expected_return & 0xffffffff, f'Return differs in case {count}')
        require(bytes(uc.mem_read(0x308000, 4)) == struct.pack('<I', expected_output), f'Output pointer differs in case {count}')
        require(uc.reg_read(reg.UC_X86_REG_ESP) == 0x200804 and all(uc.reg_read(r) == v for r, v in preserved.items()), 'ABI state differs')
        require(not unexpected_writes and bytes(uc.mem_read(0x300000, 512)) == arena and bytes(uc.mem_read(0x30404c, 256)) == bytes(table), 'Unexpected input/table write')
        if overread[0]:
            logical_overreads += 1
            if len(overread_examples) < 10: overread_examples.append(dict(case=count, data_hex=data.hex(), table_hex=bytes(table).hex(), length=length, alignment=offset, result=expected_return, outside_reads=list(outside_reads)))
        if len(examples) < 12 and str(expected_return) not in returns: examples.append(dict(data_hex=data.hex(), length=length, result=expected_return, output_offset=None if expected_output == 0xdeadbeef else expected_output - domain[0]))
        returns[str(expected_return)] = returns.get(str(expected_return), 0) + 1; count += 1
    table = bytearray([29] * 256); table[0] = 0
    for kind in range(256):
        table[65] = kind
        for length in range(9): check(bytes([0,65]) * 4, table, length, length % 2)
    for a in range(256):
        for b in (0, 0x5d, 0xfe, 0xff): check(bytes([a,b,0,0,0,0,0,0]), table)
    for first in range(11):
        for second in range(11):
            table[65] = first; table[66] = second
            for tail in (bytes([0,0]), bytes([0,0x5d,0,0x3e]), bytes([0,0x5d,0,65]), bytes([0xd8,0,0,0])):
                check(bytes([0,65,0,66]) + tail, table)
    table[65] = 4
    for data in (bytes([0,65,0,0x5d,0,0x3e]), bytes([0,65,0,0x5d]), bytes([0,65]), bytes([0,65,0,0x5d,0,65])): check(data, table)
    rng = random.Random(10632090)
    for n in range(3000):
        table = bytearray(rng.randrange(32) for _ in range(256)); table[0] = 0
        data = bytes(rng.choice((0,0,0,65,66,0x5d,0x3e,0xd8,0xdc,0xff)) for _ in range(rng.randrange(1,65)))
        check(data, table, rng.randrange(len(data)+1), n % 2)
    synthetic_cases = count; synthetic_overreads = logical_overreads; profile_name[0] = 'static-tables'
    objects = []; static_return_start = dict(returns)
    for object_base, refs in [(0x10bc9e80, (0x10bc92e0,0x10bc92e4)), (0x10bc9ff0, (0x10bc92c4,0x10bc92c8))]:
        require(struct.unpack('<I',pe_read(game,object_base+8,4))[0] == 0x10632090, 'Static scanner pointer differs')
        for slot in refs: require(struct.unpack('<I',pe_read(game,slot,4))[0] == object_base, 'Static base reference differs')
        table = bytearray(pe_read(game,object_base+0x4c,256)); start_cases = count; start_overreads = logical_overreads
        for value in range(65536): check(bytes([value>>8,value&255]),table)
        static_rng = random.Random(object_base)
        for n in range(4096):
            values = [static_rng.choice((0,9,10,13,32,65,0x3a,0x5d,0x3e,0xd800,0xdc00,0xfffe,0xffff,static_rng.randrange(65536))) for _ in range(static_rng.randrange(1,17))]
            data = b''.join(struct.pack('>H',v) for v in values)
            check(data,table,static_rng.randrange(len(data)+1),n%2)
        for text in ('',']',']]',']]>',']]>A','\r','\r\n','\n','A\r\nB',':','A:B'):
            data = text.encode('utf-16-be')
            for length in range(len(data)+1): check(data,table,length,length%2)
        objects.append(dict(base=f'{object_base:x}', scanner_slot=f'{object_base+8:x}', base_reference_slots=[f'{a:x}' for a in refs],
                            table_address=f'{object_base+0x4c:x}', table_hex=bytes(table).hex(), cases=count-start_cases, logical_end_overreads=logical_overreads-start_overreads))
    body = read_json(base / 'scanner-body-repair/verification.json')
    instruction_addresses = {int(line.split()[0],16) for line in (base/'scanner-body-repair/10632090.asm').read_text().splitlines()}
    report = dict(source_sha256=sha, cases=count, return_counts=returns, examples=examples, logical_end_overread_examples=overread_examples, scanner_instructions_visited=len(visited & instruction_addresses),
                  profiles=dict(synthetic=dict(cases=synthetic_cases,logical_end_overreads=synthetic_overreads),
                                static_tables=dict(cases=count-synthetic_cases,logical_end_overreads=logical_overreads-synthetic_overreads,
                                                   scanner_instructions_visited=len(coverage_by_profile['static-tables']&instruction_addresses),
                                                   return_counts={k:v-static_return_start.get(k,0) for k,v in returns.items()})), static_objects=objects,
                  scanner_instructions_total=body['instructions'], unvisited_scanner_instructions=[f'{a:x}' for a in sorted(instruction_addresses-visited)],
                  logical_end_overread_cases=logical_overreads, source_file=str(source), source_sha256_reference=digest(source), compiled_dll_sha256=digest(root/'reference.dll'),
                  build_command=build, dependencies={n:digest(base/n) for n in ('scanner-body-repair/manifest.json','classifier-probe/manifest.json')},
                  static_table_differences=[dict(byte=i,first=bytes.fromhex(objects[0]['table_hex'])[i],second=bytes.fromhex(objects[1]['table_hex'])[i]) for i in range(256) if bytes.fromhex(objects[0]['table_hex'])[i]!=bytes.fromhex(objects[1]['table_hex'])[i]],
                  limitation='Compiled reconstruction compared to original scanner and real classifier using synthetic and two binary-resident tables, reported separately. Padded allocated arenas and non-wrapping forward ranges only. No logical-end overreads observed in static-table cases; finite evidence does not prove all-input safety, runtime object immutability or actual caller selection.')
    (root/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    (root/'reference.c').write_bytes(source.read_bytes())
    (root/'manifest.json').write_text(json.dumps(dict(source_sha256=sha,files={n:digest(root/n) for n in ('report.json','reference.c','reference.dll')}),indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k not in ('examples','logical_end_overread_examples','build_command','static_objects')}))


if __name__=='__main__':main()
