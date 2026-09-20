# -*- coding: utf-8 -*-
"""占用调查：模型号 / 建造栏槽位 / 合成面板格位，**把同一个事实的每一种拼法都枚举掉**。

这个脚本存在的理由，是同一个形状在一次会话里栽了三次，每次都是「grep 一种拼法，
把结果当成了完整的占用表」：

  1. 模型号 —— 只认 "modelId"，漏了矿脉的 "veinModelId"，挤掉 14 个号；
  2. 建造栏槽位 —— 只看日志里的「建造栏核对」，而那一行只列 megabuildings.json；
  3. 合成面板格位 —— 只认 "gridIndex"，漏了 megabuildings.json / recipes.json 用的
     "gridRow" + "gridCol"。

三次都不报错：ProtoSlots 会把输的那一方挪走，日志只说「改用 X」——读起来像一次成功的
兜底，实际是一个不稳定的号（CLAUDE.md：**看到那行就该把号钉死**）。

所以判据不是「找一份权威清单」（不存在），而是**枚举每一种会写这个号的写法**。
下面每一类都把已知的全部拼法列出来；出现第四种拼法时往这里加，并同步改 CLAUDE.md
里那张表。

用法：在仓库根目录跑 `python tools/check_slots.py`。
"""
import collections
import io
import json
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

MB = json.load(io.open("ProjectEden/data/megabuildings.json", encoding="utf-8"))
MC = json.load(io.open("ProjectEden/data/machines.json", encoding="utf-8"))
OG = json.load(io.open("ProjectEden/data/ores.json", encoding="utf-8"))
RC = json.load(io.open("ProjectEden/data/recipes.json", encoding="utf-8"))

CAT = MB.get("buildCategory", 12)
PAGE = 3            # 本 mod 的合成面板页号（CommonAPI 在 Awake 分配，实测是 3）
PLACEHOLDER = 1601  # ores.json 写它表示「让解析器挑」，不算真占用
MODEL_CEIL = 727    # ResolveModelId 往下扫的起点，见 CLAUDE.md

_bad = 0


def name_of(d):
    return d.get("displayName") or d.get("name") or "?"


def walk(x, fn):
    if isinstance(x, dict):
        fn(x)
        for v in x.values():
            walk(v, fn)
    elif isinstance(x, list):
        for v in x:
            walk(v, fn)


def survey(title, collect, note=""):
    global _bad
    t = collections.defaultdict(list)
    collect(t)
    dups = {k: v for k, v in t.items() if len(v) > 1}
    print("\n=== %s ===" % title)
    if note:
        print("  " + note)
    if dups:
        _bad += len(dups)
        for k in sorted(dups):
            print("  X %-6s 被 %d 处占用：%s" % (k, len(dups[k]), "、".join(dups[k])))
    else:
        print("  无冲突")
    return t


# ── 1. 模型号：modelId（mega / machines）+ veinModelId（ores）───────────
def models(t):
    for b in MB["buildings"]:
        t[b["modelId"]].append("mega:" + name_of(b))

    def f(d):
        if isinstance(d.get("modelId"), int):
            t[d["modelId"]].append("machine:" + name_of(d))

    walk(MC, f)
    for o in OG["ores"]:
        if isinstance(o.get("veinModelId"), int):
            t[o["veinModelId"]].append("vein:" + o["key"])


mt = survey("模型号（modelId + veinModelId）", models,
            "上限 %d；挤掉谁都不报错，只报「改用 X」" % MODEL_CEIL)
free = [i for i in range(MODEL_CEIL - 67, MODEL_CEIL + 1) if i not in mt]
print("  已占 %d 个，%d..%d 空闲：%s" % (len(mt), MODEL_CEIL - 67, MODEL_CEIL, free))


# ── 2. 建造栏槽位：slot（mega）+ megaTab/buildSlot、buildIndex（machines）──
def slots(t):
    for b in MB["buildings"]:
        t[b["slot"]].append("mega:" + name_of(b))

    def f(d):
        if d.get("megaTab") and isinstance(d.get("buildSlot"), int):
            t[d["buildSlot"]].append("machine:" + name_of(d))
        bi = d.get("buildIndex")
        if isinstance(bi, int) and bi // 100 == CAT:
            t[bi % 100].append("machine:" + name_of(d))

    walk(MC, f)


st = survey("建造栏槽位（第 %d 类）" % CAT, slots,
            "日志的「建造栏核对」只列 megabuildings.json，不能当完整答案")
print("  已占：%s" % sorted(st))
print("  1..36 空闲：%s" % [i for i in range(1, 37) if i not in st])


# ── 3. 合成面板格位：gridIndex（ores/machines）+ gridRow/gridCol（mega/recipes）──
# 物品格位和配方格位是**两张互不相干的网格**（ProtoSlots.GridKind），原版自己就让
# 铁块的物品和铁块的配方共用同一个号。混在一起查会报一堆假阳性，而假阳性和漏报一样
# 会让真冲突看不见，所以这里分开。
def cells(kind):
    def collect(t):
        def f(d, tag):
            gi = d.get("gridIndex")
            if isinstance(gi, int) and gi > 0 and gi != PLACEHOLDER:
                t[gi].append(tag + ":" + name_of(d))
            if isinstance(d.get("gridRow"), int) and isinstance(d.get("gridCol"), int):
                t[PAGE * 1000 + d["gridRow"] * 100 + d["gridCol"]].append(tag + ":" + name_of(d))

        if kind == "item":
            # 巨型建筑的 gridRow/gridCol 同时钉死物品和配方两张网格的格位
            for b in MB["buildings"]:
                t[PAGE * 1000 + b["gridRow"] * 100 + b["gridCol"]].append("mega:" + name_of(b))
            walk(MC, lambda d: f(d, "machines"))
            walk(OG.get("items", []), lambda d: f(d, "ores.items"))
        else:
            for b in MB["buildings"]:
                t[PAGE * 1000 + b["gridRow"] * 100 + b["gridCol"]].append("mega:" + name_of(b))
            # machines.json 的建造配方：显式写了 recipeGridIndex 就用它，没写则**退回这台
            # 机器自己解析出来的物品格位**（MachineRegistry.cs 的 ResolveGridIndex 调用）。
            # 那个退回值是运行时算的，配置里看不见，所以这里只查显式钉死的那些。
            walk(MC, lambda d: t[d["recipeGridIndex"]].append("machines配方:" + name_of(d))
                 if isinstance(d.get("recipeGridIndex"), int) and d["recipeGridIndex"] > 0 else None)
            walk(RC, lambda d: f(d, "recipes"))
            for o in OG["ores"]:
                walk(o.get("recipes", []), lambda d: f(d, "ores.recipes"))
            walk(OG.get("recipes", []), lambda d: f(d, "ores.recipes"))

    return collect


for kind, label in (("item", "物品"), ("recipe", "配方")):
    t = survey("%s格位（gridIndex + gridRow/gridCol）" % label, cells(kind),
               "ores.json 写 %d 或 0 的是「让解析器挑」，不在这张表里" % PLACEHOLDER)
    for row in (1, 2, 3):
        base = PAGE * 1000 + row * 100
        print("  第 %d 页第 %d 行空闲（1..14 列）：%s"
              % (PAGE, row, [c for c in range(1, 15) if base + c not in t]))

print("""
注意：上面的「空闲」只代表**没有别的配置钉在这一格**，不代表你钉过去就一定拿得到。
ores.json 里 gridIndex 写 0 的那批物品由解析器从可见区从头扫空格，而它注册在
machines.json 之前——所以 machines.json 新钉一个号，必须同时确认
MachineRegistry.PreReserveGrids 会把它提前登记掉（它只登记显式钉死的格位）。
这一条是实测出来的：小型速采机钉到一个「空闲」格，照样被自动分配抢走了。""")

print("\n%s" % ("全部无冲突" if _bad == 0 else "共 %d 处冲突，见上" % _bad))
sys.exit(1 if _bad else 0)
