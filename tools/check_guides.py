# -*- coding: utf-8 -*-
"""特性指南这一对的结构检查：`##` / `###` 计数平行，TOC 锚点全部可解析。

**它存在的直接原因是 CLAUDE.md 里那句话当时是假的。** 那份文件写着「特性指南这一对
至少有结构检查（`##`/`###` 计数平行、TOC 锚点可解析）」，而 pack_release.py 里从来没有
这样一段代码——README 和 manifest 有没有检查是明说的，唯独这一条是凭空的。
本仓库自己的规矩：**一句主张要么兑现，要么撤掉**。这是兑现的那一半。

查三件事：

1. **两份的 `##` / `###` 数目相同。** 章节编号是平行的（一/二/三 ↔ I/II/III），
   所以「见第九节」这样的交叉引用在两边都要解析得出来。数目对不上就是有一边漏了。
2. **TOC 里的每个锚点都指向一个真标题。** 锚点按 GitHub 的规则生成：转小写、
   去掉标点、空格换连字符。中文标题里的 `、：，` 也是标点，一样要去掉。
3. **每个 `##` 标题都在 TOC 里。** 反向的那一半——三十二到三十五这四节当初就是
   加了标题没加 TOC，一直缺到现在才被发现。

用法：在仓库根目录跑 `python tools/check_guides.py`，有问题非零退出。
"""
import io
import re
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

PAIR = [("mod特性.md", "中文"), ("mod_feature.md", "English")]

# GitHub 生成锚点时去掉的标点。中文的顿号、冒号、逗号、书名号都算
PUNCT = re.compile(r"[、，。：；！？（）《》「」“”‘’,.:;!?()\[\]{}<>'\"`~@#$%^&*+=|\\/]")

_bad = 0


def anchor(title):
    """按 GitHub 的规则把标题转成锚点。"""
    a = title.strip().lower()
    a = PUNCT.sub("", a)
    a = a.replace(" ", "-")

    return a


def load(path):
    text = io.open(path, encoding="utf-8").read()
    h2 = re.findall(r"^## (.+)$", text, re.M)
    h3 = re.findall(r"^### (.+)$", text, re.M)
    toc = re.findall(r"^- \[([^\]]+)\]\(#([^)]+)\)", text, re.M)

    return h2, h3, toc


def fail(msg):
    global _bad
    _bad += 1
    print("  X " + msg)


print("=== 特性指南结构检查 ===")

data = {}

for path, label in PAIR:
    h2, h3, toc = load(path)
    data[path] = (h2, h3, toc)
    print("  %-16s ## %d　### %d　TOC %d" % (path, len(h2), len(h3), len(toc)))

(a2, a3, _), (b2, b3, _) = data[PAIR[0][0]], data[PAIR[1][0]]

if len(a2) != len(b2):
    fail("`##` 数目不平行：%s %d vs %s %d —— 有一边漏了一整节"
         % (PAIR[0][0], len(a2), PAIR[1][0], len(b2)))

if len(a3) != len(b3):
    fail("`###` 数目不平行：%s %d vs %s %d —— 有一边漏了一个小节"
         % (PAIR[0][0], len(a3), PAIR[1][0], len(b3)))

for path, label in PAIR:
    h2, _, toc = data[path]
    anchors = set(anchor(t) for t in h2)

    for text, target in toc:
        if target not in anchors:
            fail("%s 的 TOC 条目「%s」指向 #%s，但没有标题生成这个锚点" % (path, text, target))

    listed = set(t for _, t in toc)

    for title in h2:
        if anchor(title) not in listed:
            fail("%s 的标题「%s」不在 TOC 里" % (path, title))

print()
print("全部通过" if _bad == 0 else "共 %d 处问题，见上" % _bad)
sys.exit(1 if _bad else 0)
