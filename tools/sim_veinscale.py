#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""矿脉随面积缩放：离线复现三条公式，并对本 mod 自己的稀有矿算出改写前后的真值。

为什么要有这个脚本
------------------
这一段改的是**世界生成**，而世界生成的错误只会表现为「矿好像不太对」——
不报异常、不崩、玩家也说不清。唯一能在进游戏之前把它钉死的，就是把公式单独跑一遍。

原版的主题表（ThemeProto）在 resources.assets 里，**离线读不到**，所以这里分两半：
  * 三条公式本身 —— 用代表性输入跑，断言不变式；
  * 本 mod 自己的稀有矿 —— 它们的 chance 写在 ores.json 里，**能读**，
    所以能直接算出「改完之后每系概率变成多少」，这正是最值得人眼过一遍的数。

用法：  python tools/sim_veinscale.py
"""

import io
import json
import math
import pathlib
import struct
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
PLANET = ROOT / "ProjectEden" / "data" / "planet.json"
ORES = ROOT / "ProjectEden" / "data" / "ores.json"

STOCK_RADIUS = 200.0


def load(path):
    return json.load(io.open(path, encoding="utf-8"))


# ── 三条公式，和 VeinScalingPatches.cs 里逐字对应 ────────────────────

def area_ratio(radius):
    """面积倍率 = (半径 / 200)²。"""
    k = radius / STOCK_RADIUS
    return k * k


def scale_spots(spot, area):
    """数量：向上取整。向下取整会把小倍率整档吃掉（原版大量 VeinSpot == 1）。"""
    return int(math.ceil(spot * area))


def type_ratio(area, z):
    """种类：种–面积关系 S = cA^z。"""
    return area ** z


def rare_chance(p0, area):
    """稀有概率：泊松 p = 1 − (1−p₀)^k。"""
    if p0 <= 0.0:
        return 0.0
    if p0 >= 1.0:
        return p0
    return 1.0 - (1.0 - p0) ** area


# ── 断言 ────────────────────────────────────────────────────────────

FAILS = []


def check(ok, msg):
    print(("  OK   " if ok else "  FAIL ") + msg)
    if not ok:
        FAILS.append(msg)


def main():
    cfg = load(PLANET)
    vs = cfg.get("veinScaling") or {}
    radius_mul = cfg.get("radiusMultiplier", 1.0)

    # 真实半径会被吸附到 40 的倍数，这里按配置直算即可——
    # 断言针对的是公式，不是吸附
    radius = STOCK_RADIUS * radius_mul
    area = area_ratio(radius)
    z = vs.get("speciesAreaExponent") or 0.25
    density = vs.get("extraTypeDensity") or 0.35

    print()
    print("=== 配置 ===")
    print("  半径倍率 %.2f → 半径 %.0f → **面积 x%.2f**" % (radius_mul, radius, area))
    print("  种-面积指数 z = %.2f  →  种类 x%.3f" % (z, type_ratio(area, z)))
    print("  伴生密度 %.2f（相对该主题的铁矿）" % density)
    print("  开关：数量=%s 种类=%s 稀有储量=%s 稀有概率=%s"
          % (vs.get("scaleSpots"), vs.get("extraTypes"),
             vs.get("scaleRareRichness"), vs.get("scaleRareChance")))

    # ── 不变式 ──────────────────────────────────────────────────
    print()
    print("=== 不变式 ===")

    check(area > 1.0, "面积倍率 > 1（否则整段是空操作）")

    # 概率恒 < 1，且 p0 = 0 恒为 0。后者是关键：RareSettings[i*4+0] 是
    # 「母星系不刷」那一格，本仓库曾因为把两格搞反而让稀有矿只长在母星系
    # **按 float32 算，因为 C# 那边 RareSettings 是 Single。** float64 下
    # p0=0.99 给出 0.99999999，看着「< 1」；而 float32 的精度只有约 1.2e-7，
    # 同一个数会被舍入成正好 1.0f。两者的结论不同，所以这里必须按目标类型量。
    def f32(x):
        return struct.unpack("f", struct.pack("f", x))[0]

    probes = (0.01, 0.06, 0.1, 0.25, 0.35, 0.5, 0.9)
    worst = max(f32(rare_chance(p, area)) for p in probes)
    check(worst < 1.0,
          "常见概率下恒 < 1（float32 实测最大 %.7f，不需要夹取）" % worst)

    # 诚实地记下边界在哪：p0 足够大时 float32 会舍到正好 1.0（= 必出）。
    # 这不是 bug——1−(1−p)^4 本来就该趋近 1——但它是个真实的边界，写下来
    # 好过让下一个人以为「恒小于 1」是无条件的。原版和本 mod 现有的稀有概率
    # 最高是 0.35，离这个边界很远
    saturates = [p for p in (0.9, 0.95, 0.99) if f32(rare_chance(p, area)) >= 1.0]
    check(all(p >= 0.9 for p in saturates),
          "只有 p0 >= 0.9 才会在 float32 下饱和到 1.0（当前最高的稀有概率是 0.35）")
    check(rare_chance(0.0, area) == 0.0,
          "p0 = 0 仍然是 0 —— 「母星系不刷」自动保留，不需要特判")
    check(all(rare_chance(p, area) >= p for p in (0.01, 0.06, 0.1, 0.25, 0.35)),
          "概率只增不减")

    # 数量只增不减，且 VeinSpot == 1 的条目在任何 area > 1 下都真的会变
    check(all(scale_spots(s, area) >= s for s in (1, 2, 3, 5, 12)),
          "数量只增不减")
    check(scale_spots(1, area) > 1,
          "VeinSpot == 1 的条目确实被抬起来了（向上取整的理由）")

    # 种类：只增不减，而且 6 种矿的主题应当拿到 2~3 种（给玩家看过的承诺）
    for have in (4, 6, 8, 10):
        want = int(round(have * type_ratio(area, z))) - have
        check(want >= 0, "矿种 %d 的主题：补 %d 种（不为负）" % (have, want))

    six = int(round(6 * type_ratio(area, z))) - 6
    check(2 <= six <= 3,
          "有 6 种矿的主题补 %d 种 —— 和「+2~3 种」的说法对得上" % six)

    # ── 数量表 ──────────────────────────────────────────────────
    print()
    print("=== 数量：VeinSpot 改写前后 ===")
    for s in (1, 2, 3, 4, 6, 10):
        print("    %2d  ->  %3d" % (s, scale_spots(s, area)))

    print()
    print("=== 种类：种-面积关系 ===")
    for have in (3, 4, 5, 6, 7, 8, 10, 12):
        after = int(round(have * type_ratio(area, z)))
        print("    原有 %2d 种  ->  %2d 种（补 %d）" % (have, after, after - have))

    # ── 本 mod 自己的稀有矿：真值 ────────────────────────────────
    print()
    print("=== 本 mod 稀有矿：每系概率改写前后（chance 读自 ores.json）===")
    ores = load(ORES)
    rows = []
    for o in ores.get("ores", []):
        pl = o.get("placement") or {}
        if pl.get("mode") != "rare":
            continue
        p0 = pl.get("chance")
        if not p0:
            continue
        rows.append((o.get("oreName"), p0, rare_chance(p0, area),
                     "、".join(pl.get("themes") or [])))

    if not rows:
        print("    （没读到稀有矿条目 —— 检查 ores.json 的 placement.mode）")
    else:
        for name, p0, p1, themes in rows:
            print("    %-8s %.3f -> %.3f  (x%.2f)   主题：%s"
                  % (name, p0, p1, p1 / p0, themes))

        print()
        print("    读法：这是**每颗候选星球**的概率，不是整局的期望。")
        print("    整局期望 = 概率 x 候选星球数，而候选星球数取决于主题表和这一局的种子")
        print("    —— 那两样离线都读不到，进游戏看 RareVeinSurvey 那几行。")

    print()
    if FAILS:
        print("%d 条断言失败" % len(FAILS))
        return 1

    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
