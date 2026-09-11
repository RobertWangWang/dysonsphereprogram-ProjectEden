# -*- coding: utf-8 -*-
u"""把插件 + preloader 打成一个 r2modman 可直接导入的包。

    python tools\\pack_release.py                     # 用 manifest 里的版本号命名
    python tools\\pack_release.py 伊甸园计划20260911   # 自定义文件名（不含 .zip）

为什么不用 csproj 里那个 PackageForRelease
--------------------------------------------
那个 target 产出的是**扁平布局**（DLL 直接躺在包根），Thunderstore / r2modman 会把它们
全部塞进 BepInEx/plugins/<包名>/。而本 mod 的 preloader **必须落在 BepInEx/patchers/**，
扁平布局表达不了这件事。

放错位置不会报错：BepInEx 不会去 plugins/ 里找 patcher，插件启动后检测到字段没被加宽，
就按降级模式跑（集装回到 255、超过 63 层的增产剂被夹断），日志里写一行就过去了。
所以这里改用 BepInEx/ 目录结构 —— 和 CommonAPI 自己那个 patcher 走的是同一套约定。

发布前务必先跑（顺序不能反）：
    dotnet build -c Release
    dotnet build ProjectEden.Preloader\\ProjectEden.Preloader.csproj -c Release
    powershell -ExecutionPolicy Bypass -File tools\\verify_preloader.ps1 -Config Release
"""
import io
import json
import os
import shutil
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(ROOT, u'dist')
STAGE = os.path.join(DIST, u'_stage')

PLUGIN = os.path.join(ROOT, u'ProjectEden', u'bin', u'Release', u'ProjectEden.dll')
NEWTON = os.path.join(ROOT, u'ProjectEden', u'lib', u'Newtonsoft.Json.dll')
PRELOAD = os.path.join(ROOT, u'ProjectEden.Preloader', u'bin', u'Release', u'ProjectEden.Preloader.dll')

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
    version = manifest[u'version_number']

    name = sys.argv[1] if len(sys.argv) > 1 else u'%s-%s' % (manifest[u'name'], version)
    zip_path = os.path.join(DIST, name + u'.zip')

    # 版本号散在四处，对不上会让测试者装到旧版而毫无提示
    check_versions(version)

    if os.path.isdir(STAGE):
        shutil.rmtree(STAGE)

    os.makedirs(STAGE)

    for src, rel in [
        (os.path.join(ROOT, u'ProjectEden', u'manifest.json'), u'manifest.json'),
        (os.path.join(ROOT, u'ProjectEden', u'icon.png'), u'icon.png'),
        (os.path.join(ROOT, u'ProjectEden', u'CHANGELOG.md'), u'CHANGELOG.md'),
        (os.path.join(ROOT, u'LICENSE'), u'LICENSE'),
        (os.path.join(ROOT, u'mod特性.md'), u'mod特性.md'),
        (os.path.join(ROOT, u'mod_feature.md'), u'mod_feature.md'),
        (PLUGIN, u'BepInEx/plugins/ProjectEden/ProjectEden.dll'),
        (NEWTON, u'BepInEx/plugins/ProjectEden/Newtonsoft.Json.dll'),
        (PRELOAD, u'BepInEx/patchers/ProjectEden/ProjectEden.Preloader.dll'),
    ]:
        dst = os.path.join(STAGE, rel.replace(u'/', os.sep))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)

    # README 在仓库根目录，里面指向 部署.md / CLAUDE.md —— 那两份不进包，链接要去掉，
    # 否则玩家点开是 404。特性文档进包了，平级链接本来就对。
    readme = io.open(os.path.join(ROOT, u'README.md'), encoding='utf-8').read()
    readme = (readme
              .replace(u'**[部署.md](部署.md)**', u'**部署.md**（在源码仓库里）')
              .replace(u'- **[部署.md](部署.md)**', u'- **部署.md**')
              .replace(u'- **[CLAUDE.md](CLAUDE.md)**', u'- **CLAUDE.md**'))
    io.open(os.path.join(STAGE, u'README.md'), 'w', encoding='utf-8').write(readme)

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
            out.write(u'  %-56s %8d\n' % (n, z.getinfo(n).file_size))

        bad = z.testzip()

    out.write(u'  完整性：%s\n' % (u'OK' if bad is None else u'损坏 ' + bad))
    out.write(u'\n提醒：包里的 preloader 必须是 verify_preloader.ps1 -Config Release 校验过的那一个。\n')


def check_versions(version):
    u"""四处版本号必须一致。

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
