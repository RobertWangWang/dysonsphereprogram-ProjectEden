# -*- coding: utf-8 -*-
u"""把插件 + preloader 打成一个 Thunderstore / r2modman 都能吃的包。

    python tools\\pack_release.py                     # 用 manifest 里的版本号命名
    python tools\\pack_release.py 伊甸园计划20260911   # 自定义文件名（不含 .zip）

包内布局
--------
    manifest.json  icon.png  README.md  CHANGELOG.md  LICENSE
    mod特性.md  mod_feature.md
    plugins/ProjectEden.dll
    plugins/Newtonsoft.Json.dll
    patchers/ProjectEden.Preloader.dll

**`plugins/` 和 `patchers/` 是平级放在包根的，不要加 `BepInEx/` 前缀。**
这是 DSP 生态的既定约定——CommonAPI 和创世之书的正式包都是这么排的，
r2modman 会把它们分别铺到 `BepInEx/plugins/<包名>/` 和 `BepInEx/patchers/<包名>/`。

为什么 preloader 必须在 `patchers/` 而不是 `plugins/`：BepInEx 不会去 plugins 里找 patcher。
放错了**不会报错**——插件启动后检测到字段没被加宽，就按降级模式跑
（集装回到 255、超过 63 层的增产剂被夹断），日志里写一行就过去了。

为什么不用 csproj 里那个 PackageForRelease
--------------------------------------------
那个 target 把所有 DLL 摊在包根，表达不了「这一个要进 patchers」。留着它没删，
对「只想拿个插件 DLL」的场合还有用；**对外分发一律走这个脚本。**

发布前务必先跑（顺序不能反）：
    dotnet build -c Release
    dotnet build ProjectEden.Preloader\\ProjectEden.Preloader.csproj -c Release
    powershell -ExecutionPolicy Bypass -File tools\\verify_preloader.ps1 -Config Release
"""
import io
import json
import os
import re
import shutil
import struct
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(ROOT, u'dist')
STAGE = os.path.join(DIST, u'_stage')

PLUGIN = os.path.join(ROOT, u'ProjectEden', u'bin', u'Release', u'ProjectEden.dll')
NEWTON = os.path.join(ROOT, u'ProjectEden', u'lib', u'Newtonsoft.Json.dll')
PRELOAD = os.path.join(ROOT, u'ProjectEden.Preloader', u'bin', u'Release', u'ProjectEden.Preloader.dll')

REPO = u'https://github.com/RobertWangWang/dysonsphereprogram-ProjectEden'

out = io.open(1, 'w', encoding='utf-8', closefd=False)


def need(path, hint):
    if os.path.isfile(path):
        return

    raise SystemExit(u'缺少 %s\n  %s' % (path, hint))


def main():
    need(PLUGIN, u'先跑：dotnet build -c Release')
    need(NEWTON, u'ProjectEden/lib/Newtonsoft.Json.dll 应该在仓库里')
    need(PRELOAD, u'先跑：dotnet build ProjectEden.Preloader/ProjectEden.Preloader.csproj -c Release')

    manifest_path = os.path.join(ROOT, u'ProjectEden', u'manifest.json')
    manifest = json.load(io.open(manifest_path, encoding='utf-8'))

    check_manifest(manifest)
    check_versions(manifest[u'version_number'])

    name = sys.argv[1] if len(sys.argv) > 1 else u'%s-%s' % (manifest[u'name'], manifest[u'version_number'])
    zip_path = os.path.join(DIST, name + u'.zip')

    if os.path.isdir(STAGE):
        shutil.rmtree(STAGE)

    os.makedirs(STAGE)

    for src, rel in [
        (manifest_path, u'manifest.json'),
        (os.path.join(ROOT, u'ProjectEden', u'icon.png'), u'icon.png'),
        (os.path.join(ROOT, u'ProjectEden', u'CHANGELOG.md'), u'CHANGELOG.md'),
        (os.path.join(ROOT, u'LICENSE'), u'LICENSE'),
        (os.path.join(ROOT, u'mod特性.md'), u'mod特性.md'),
        (os.path.join(ROOT, u'mod_feature.md'), u'mod_feature.md'),
        (PLUGIN, u'plugins/ProjectEden.dll'),
        (NEWTON, u'plugins/Newtonsoft.Json.dll'),
        (PRELOAD, u'patchers/ProjectEden.Preloader.dll'),
    ]:
        dst = os.path.join(STAGE, rel.replace(u'/', os.sep))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)

    write_readme()

    if os.path.exists(zip_path):
        os.remove(zip_path)

    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED) as z:
        for base, _dirs, files in os.walk(STAGE):
            for f in sorted(files):
                full = os.path.join(base, f)
                z.write(full, os.path.relpath(full, STAGE).replace(os.sep, u'/'))

    shutil.rmtree(STAGE)

    out.write(u'%s  (%.0f KB)\n' % (zip_path, os.path.getsize(zip_path) / 1024.0))

    with zipfile.ZipFile(zip_path) as z:
        for n in z.namelist():
            out.write(u'  %-44s %8d\n' % (n, z.getinfo(n).file_size))

        bad = z.testzip()

    out.write(u'  完整性：%s\n' % (u'OK' if bad is None else u'损坏 ' + bad))
    out.write(u'\n提醒：包里的 preloader 必须是 verify_preloader.ps1 -Config Release 校验过的那一个。\n')


def write_readme():
    u"""README 在仓库根目录，里面指向的开发文档不进包 —— 换成仓库链接，别留 404。"""
    readme = io.open(os.path.join(ROOT, u'README.md'), encoding='utf-8').read()

    for a, b in [(u'[部署.md](部署.md)', u'[部署.md](%s/blob/main/%%E9%%83%%A8%%E7%%BD%%B2.md)' % REPO),
                 (u'[CLAUDE.md](CLAUDE.md)', u'[CLAUDE.md](%s/blob/main/CLAUDE.md)' % REPO)]:
        readme = readme.replace(a, b)

    io.open(os.path.join(STAGE, u'README.md'), 'w', encoding='utf-8').write(readme)


def check_manifest(m):
    u"""Thunderstore 的几条硬规则。

    它们在上传时才报错，而错误信息不总是指得清楚 —— 在这里拦下来便宜得多。
    规则取自 Thunderstore 的包格式要求，数值对照本机缓存里的真包核过。
    """
    problems = []

    if not re.match(r'^[a-zA-Z0-9_]+$', m.get(u'name', u'')):
        problems.append(u'name 只能是字母/数字/下划线（当前：%s）' % m.get(u'name'))

    if not re.match(r'^\d+\.\d+\.\d+$', m.get(u'version_number', u'')):
        problems.append(u'version_number 必须是 x.y.z（当前：%s）' % m.get(u'version_number'))

    desc = m.get(u'description', u'')

    if not desc:
        problems.append(u'description 不能为空')
    elif len(desc) > 250:
        problems.append(u'description 上限 250 字符，当前 %d' % len(desc))

    if not m.get(u'website_url'):
        problems.append(u'website_url 是空的 —— 包页面上就没有源码入口了')

    deps = m.get(u'dependencies')

    if not isinstance(deps, list) or not deps:
        problems.append(u'dependencies 必须是非空数组')
    else:
        for d in deps:
            if not re.match(r'^[\w\-]+-[\w\-]+-\d+\.\d+\.\d+$', d):
                problems.append(u'依赖串格式应为 命名空间-包名-版本：%s' % d)

    icon = os.path.join(ROOT, u'ProjectEden', u'icon.png')

    if not os.path.isfile(icon):
        problems.append(u'缺少 icon.png')
    else:
        b = io.open(icon, 'rb').read(24)
        w, h = struct.unpack('>II', b[16:24])

        if (w, h) != (256, 256):
            problems.append(u'icon.png 必须正好 256x256，当前 %dx%d' % (w, h))

    if not problems:
        return

    raise SystemExit(u'manifest 不符合 Thunderstore 要求：\n  ' + u'\n  '.join(problems))


def check_versions(version):
    u"""四处版本号必须和 manifest 一致。

    改版本时最容易漏掉 Plugin.cs 里那个常量 —— 它是游戏内和日志里显示的那个，
    漏了的话包是新的、日志里却报旧版本，排查时会被带偏。
    """
    places = [
        (os.path.join(ROOT, u'ProjectEden', u'ProjectEden.csproj'), u'<Version>%s</Version>' % version),
        (os.path.join(ROOT, u'ProjectEden.Preloader', u'ProjectEden.Preloader.csproj'), u'<Version>%s</Version>' % version),
        (os.path.join(ROOT, u'ProjectEden', u'src', u'Plugin.cs'), u'VERSION = "%s"' % version),
        (os.path.join(ROOT, u'ProjectEden', u'CHANGELOG.md'), u'## %s' % version),
    ]

    bad = [p for p, token in places if token not in io.open(p, encoding='utf-8').read()]

    if not bad:
        return

    raise SystemExit(u'版本号对不上 manifest 的 %s，下面这些文件没跟上：\n  %s'
                     % (version, u'\n  '.join(bad)))


main()
