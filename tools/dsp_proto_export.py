"""从原版资源解码原型表；根据程序集元数据生成类型树，不执行游戏代码。"""
import argparse
import importlib.metadata
import json
import re
import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
from UnityPy.helpers import TypeTreeHelper
from UnityPy.streams import EndianBinaryWriter
from dsp_knowledge import GENERATED, paths, digest
from dsp_typetree import normalize_nodes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--managed')
    parser.add_argument('--bepinex')
    args = parser.parse_args()
    managed, _ = paths(args)
    source = managed.parent / 'resources.assets'
    source_hash = digest(source)
    assemblies = {p.name: digest(p) for p in sorted(managed.glob('*.dll'))}
    output = GENERATED / 'prototypes'
    output.mkdir(exist_ok=True)
    marker = output / 'manifest.json'
    marker.unlink(missing_ok=True)
    env = UnityPy.load(str(source))
    objects = [o for o in env.objects if o.type.name == 'MonoBehaviour']
    version = objects[0].assets_file.unity_version
    generator = TypeTreeGenerator(version)
    generator.load_local_dll_folder(str(managed))
    records = []
    names = set()
    files = []
    for obj in objects:
        name = obj.peek_name()
        if not name or not re.fullmatch(r'[A-Za-z0-9_]+ProtoSet', name):
            continue
        if name in names:
            raise ValueError('重复原型表名：' + name)
        names.add(name)
        nodes = normalize_nodes(generator.get_nodes_up('Assembly-CSharp', name))
        # check_read=True：类型树必须恰好消费对象全部字节，不能悄悄截断。
        tree = obj.read_typetree(nodes, check_read=True)
        header = obj.read_typetree(check_read=False)
        if any(tree[k] != header[k] for k in ('m_GameObject', 'm_Enabled', 'm_Script', 'm_Name')):
            raise ValueError('生成类型树与 Unity 标准对象头不一致：' + name)
        if tree['m_Name'] != name or not isinstance(tree['dataArray'], list):
            raise ValueError('原型表结构不符合预期：' + name)
        ids = [row['ID'] for row in tree['dataArray']]
        if len(ids) != len(set(ids)):
            raise ValueError('原型 ID 重复：' + name)
        writer = EndianBinaryWriter(endian=obj.reader.endian)
        TypeTreeHelper.write_typetree(tree, nodes, writer, obj.assets_file)
        if writer.bytes != obj.get_raw_data():
            raise ValueError('解码再编码与原始字节不一致：' + name)
        (output / (name + '.json')).write_text(json.dumps(tree, ensure_ascii=False, indent=2), encoding='utf-8')
        (output / (name + '.bin')).write_bytes(obj.get_raw_data())
        (output / (name + '.schema.txt')).write_text(nodes.dump_structure(), encoding='utf-8')
        files.extend(name + ext for ext in ('.json', '.bin', '.schema.txt'))
        records.append({'name': name, 'path_id': obj.path_id, 'bytes': obj.byte_size, 'rows': len(ids), 'roundtrip': 'byte-identical', 'fields': list(tree['dataArray'][0]) if ids else []})
        print(f'{name}: {len(ids)} 条；完整读取 {obj.byte_size} 字节', flush=True)
    if digest(source) != source_hash or assemblies != {p.name: digest(p) for p in sorted(managed.glob('*.dll'))}:
        raise ValueError('导出期间资源或程序集发生变化')
    index = {'source': source.name, 'source_sha256': source_hash, 'unity_version': version,
             'unitypy': UnityPy.__version__, 'typetree_generator': importlib.metadata.version('TypeTreeGeneratorAPI'), 'tables': records}
    (output / 'index.json').write_text(json.dumps(index, ensure_ascii=False, indent=2), encoding='utf-8')
    files.append('index.json')
    manifest = {'source_sha256': source_hash, 'assemblies': assemblies,
                'files': {n: digest(output / n) for n in files}}
    marker.write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print(f'完成：{len(records)} 张表，{sum(r["rows"] for r in records)} 条原型记录。')


if __name__ == '__main__':
    main()
