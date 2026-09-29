"""读取游戏安装目录的原生 PE 文件及导入导出表；不执行目标程序。"""
import argparse
import json
import shutil
import struct
import subprocess

from dsp_knowledge import KB, GENERATED, paths, digest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--managed')
    parser.add_argument('--bepinex')
    parser.add_argument('--disassembly', action='store_true', help='同时导出 PE 代码节的原始反汇编')
    args = parser.parse_args()
    managed, _ = paths(args)
    game = managed.parent.parent
    dumpbin = shutil.which('dumpbin')
    if not dumpbin:
        raise ValueError('需要 Visual Studio dumpbin 工具')
    output = GENERATED / 'native'
    output.mkdir(exist_ok=True)
    records = []
    for path in sorted(game.rglob('*')):
        if path.suffix.lower() not in ('.exe', '.dll') or not path.is_file():
            continue
        data = path.read_bytes()
        if data[:2] != b'MZ':
            continue
        pe = struct.unpack_from('<I', data, 0x3c)[0]
        if data[pe:pe+4] != b'PE\0\0':
            continue
        machine = struct.unpack_from('<H', data, pe+4)[0]
        opt = pe + 24
        magic = struct.unpack_from('<H', data, opt)[0]
        directory = opt + (112 if magic == 0x20b else 96)
        clr = struct.unpack_from('<II', data, directory + 14 * 8)
        if clr[0]:
            continue
        relative = path.relative_to(game).as_posix()
        key = relative.replace('/', '__')
        folder = output / key
        folder.mkdir(exist_ok=True)
        sha = digest(path)
        tasks = [('headers', '/headers'), ('imports', '/imports'), ('exports', '/exports')]
        if args.disassembly:
            tasks.append(('disassembly', '/disasm'))
        for name, flag in tasks:
            with (folder / (name + '.txt')).open('wb') as stream:
                subprocess.run([dumpbin, '/nologo', flag, str(path)], stdout=stream, stderr=subprocess.STDOUT, check=True)
        if digest(path) != sha:
            raise ValueError('读取期间文件改变：' + relative)
        records.append({'path': relative, 'output': key, 'machine': hex(machine), 'bytes': len(data), 'sha256': sha,
                        'scope': 'mod-loader' if path.name.lower() == 'winhttp.dll' else 'installed-native'})
    report = {'game_directory': str(game), 'files': records}
    (output / 'inventory.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    text = '# 原生模块清单\n\n'
    text += '由 `python -X utf8 tools/dsp_native_inventory.py` 从安装目录 PE 头生成；没有启动游戏或加载 DLL。托管程序集排除在此表之外。\n\n'
    text += '这是一份实际安装文件清单，不证明每个文件都是游戏原厂文件。根目录 winhttp.dll 单独标为 mod 加载相关，避免当作游戏业务逻辑。导入导出表已保存在本机 generated/native 下；函数伪代码的完成情况需另外检查 Ghidra 输出。\n\n'
    text += '| 文件 | 机器类型 | 字节 | 范围 | SHA-256 |\n|---|---|---:|---|---|\n'
    for row in records:
        text += f"| `{row['path']}` | {row['machine']} | {row['bytes']:,} | {row['scope']} | `{row['sha256']}` |\n"
    (KB / 'native-inventory.md').write_text(text, encoding='utf-8')
    print(f'原生 PE：{len(records)} 个，已导出头部和导入/导出表。')


if __name__ == '__main__':
    main()
