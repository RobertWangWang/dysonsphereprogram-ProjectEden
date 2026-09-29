"""解码全部序列化 MonoBehaviour/ScriptableObject 实例，保留失败与原始字节。"""
import argparse
import json
from pathlib import Path
import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
from UnityPy.helpers import TypeTreeHelper
from UnityPy.streams import EndianBinaryWriter
from dsp_knowledge import GENERATED, paths, digest, read_json
from dsp_typetree import normalize_nodes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--managed')
    parser.add_argument('--bepinex')
    args = parser.parse_args()
    managed, _ = paths(args)
    assets = managed.parent
    shader_base = GENERATED / 'shaders'
    shader_manifest = read_json(shader_base / 'manifest.json')
    if digest(shader_base / 'index.json') != shader_manifest['files']['index.json']:
        raise ValueError('MonoScript 索引哈希不匹配')
    scripts = {}
    for record in read_json(shader_base / 'index.json')['objects']:
        if record['type'] == 'MonoScript':
            name = f"{record['source']}/{record['path_id']}/metadata.json"
            if digest(shader_base / name) != shader_manifest['files'][name]:
                raise ValueError('MonoScript 数据哈希不匹配：' + name)
            scripts[(Path(record['source']).name, record['path_id'])] = read_json(shader_base / name)
    sources = dict(shader_manifest['sources'])
    for name, sha in sources.items():
        if digest(assets / name) != sha:
            raise ValueError('资产已变化，请先重新导出 shader/MonoScript')
    assemblies = {p.name: digest(p) for p in sorted(managed.glob('*.dll'))}
    output = GENERATED / 'components'
    output.mkdir(exist_ok=True)
    marker = output / 'manifest.json'
    marker.unlink(missing_ok=True)
    records, files = [], []
    generator = None
    node_cache = {}
    for source in sorted(sources):
        env = UnityPy.load(str(assets / source))
        for obj in env.objects:
            if obj.type.name != 'MonoBehaviour':
                continue
            if generator is None:
                generator = TypeTreeGenerator(obj.assets_file.unity_version)
                generator.load_local_dll_folder(str(managed))
            relative = f'{source}/{obj.path_id}'
            folder = output / relative
            folder.mkdir(parents=True, exist_ok=True)
            raw = obj.get_raw_data()
            (folder / 'serialized.bin').write_bytes(raw)
            files.append(relative + '/serialized.bin')
            record = {'source': source, 'path_id': obj.path_id, 'bytes': len(raw), 'status': 'failed'}
            try:
                # 这里只读取基类头以解析脚本引用；这一步不算完整解码成功。
                header = obj.read_typetree(check_read=False)
                script = header['m_Script']
                file_id = script['m_FileID']
                filename = Path(source).name if file_id == 0 else Path(obj.assets_file.externals[file_id-1].path).name
                definition = scripts[(filename, script['m_PathID'])]
                fullname = '.'.join(s for s in (definition['m_Namespace'], definition['m_ClassName']) if s)
                assembly = definition['m_AssemblyName']
                record.update({'name': header['m_Name'], 'class': fullname, 'assembly': assembly})
                key = (assembly, fullname)
                if key not in node_cache:
                    node_cache[key] = normalize_nodes(generator.get_nodes_up(assembly, fullname))
                node = node_cache[key]
                tree = obj.read_typetree(node, check_read=True)
                if any(tree[k] != header[k] for k in ('m_GameObject', 'm_Enabled', 'm_Script', 'm_Name')):
                    raise ValueError('生成类型树与 Unity 标准对象头不一致')
                writer = EndianBinaryWriter(endian=obj.reader.endian)
                TypeTreeHelper.write_typetree(tree, node, writer, obj.assets_file)
                if writer.bytes != raw:
                    raise ValueError('完整解码后字节往返不一致')
                (folder / 'data.json').write_text(json.dumps(tree, ensure_ascii=True, default=lambda v: {'hex': bytes(v).hex()}), encoding='utf-8')
                files.append(relative + '/data.json')
                record['data'] = relative + '/data.json'
                record['status'] = 'byte-identical'
            except Exception as exc:
                record['error'] = str(exc)
            records.append(record)
            if len(records) % 1000 == 0:
                print(f"{len(records)} 个对象，失败 {sum(r['status']=='failed' for r in records)}", flush=True)
    if assemblies != {p.name: digest(p) for p in sorted(managed.glob('*.dll'))} or any(digest(assets / n) != h for n, h in sources.items()):
        raise ValueError('导出期间输入发生变化')
    index = {'sources': sources, 'objects': records}
    (output / 'index.json').write_text(json.dumps(index, ensure_ascii=True, indent=2), encoding='utf-8')
    files.append('index.json')
    marker.write_text(json.dumps({'sources': sources, 'assemblies': assemblies, 'files': {n: digest(output / n) for n in files}}, indent=2), encoding='utf-8')
    print(f"完成扫描：{len(records)} 个组件，失败 {sum(r['status']=='failed' for r in records)}。")


if __name__ == '__main__':
    main()
