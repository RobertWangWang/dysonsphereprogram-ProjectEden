"""矩阵研究站该囤多少货——按引擎自己的上限推，不靠手感填。

    python tools/sim_labstock.py

**为什么需要这个脚本。** 1.12.17 及以前 `lab.json` 里那四个存量数是「往大了填」定的
（生产两格各一千万、研究模式每种 25 万），而 25 万正好是那个字段物理上能放的最大值。
后果被玩家逮到了：**堆叠的研究站会把所有科研矩阵全推到最上层**，下面每一层只剩 2 个。
那不是手感问题，是这几个数和引擎的实际消耗率差了四到五个数量级。

这里把消耗率从 IL 里推出来，再反过来问「每个候选上限够撑几秒」。三条事实：

  1. **研究站每 tick 只结算一个配方周期**，无论 assembleSpeed 多大。
     GameLogic 有 _lab_produce_parallel 而没有多周期路径（巨型建筑那套 RunExtraCycles
     不覆盖研究站），所以生产模式的产能恒为 60 个/秒。
  2. **研究模式每 tick 的哈希预算是 `techSpeed + 2`**。
     InternalUpdateResearch @000A: `V_0 = (int)(research_speed + 2f)`，
     而 research_speed 由 FactorySystem.GameTickLabResearchMode @00B3 传的是
     `(float)GameHistoryData.techSpeed`。
  3. **一次哈希吃掉 `matrixPoints[j]` 个放大单位**（@0208 `matrixPoints[j] * V_5`），
     而 matrixServed 是「个数 × 3600」。matrixPoints 由 GameHistoryData.Import @03C0
     填成 `TechProto.ItemPoints[k]`，下标是 `Items[k] - 6001`。
     所以 **每种矩阵每秒吃 `ItemPoints × (techSpeed + 2) / 60` 个**。

顺带记一条这个脚本没法离线回答的：`ItemPoints` 和 `techSpeed` 的真实取值在
resources.assets 和存档里，所以这里按一段范围扫，由 MatrixSurvey 每局实测核对。
"""
import io
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
LAB = os.path.join(HERE, "..", "ProjectEden", "data", "lab.json")

MATRIX_SCALE = 3600      # PlanetFactory.InsertInto: matrixServed[i] += 3600 * itemCount
TICKS = 60               # 一秒 60 tick
CRAFTS_PER_TICK = 1      # 事实 1：引擎硬上限，研究站没有多周期路径

# matrixServed 是 Int32，而向上层搬料会在收货方造成一次二次累加，所以按 2 倍峰值算
HARD_CAP_ITEMS = (2 ** 31 - 1) // MATRIX_SCALE // 2


def load_config():
    """lab.json 带 // 注释键，是合法 JSON，直接读。"""
    raw = io.open(LAB, encoding="utf-8").read()

    return json.loads(raw)


def research_items_per_sec(item_points, tech_speed):
    """研究模式每种矩阵每秒消耗的件数。"""
    hashes_per_tick = int(tech_speed + 2)

    return item_points * hashes_per_tick * TICKS / float(MATRIX_SCALE)


def main():
    out = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", newline="")
    cfg = load_config()
    failures = []

    def check(ok, why):
        if not ok:
            failures.append(why)

    out.write("=== 矩阵研究站存量推导 ===\n\n")

    # ── 研究模式 ────────────────────────────────────────────────────────
    cap = cfg["researchStorage"]
    supply = cfg["supplyMatrixItems"]
    interval = cfg["supplyIntervalTicks"]

    out.write("研究模式：每种矩阵每秒消耗 = ItemPoints × (techSpeed + 2) / 60\n")
    out.write("  （ItemPoints 由科技表给，techSpeed 由研究速度升级给；两者都在存档/资产里，\n")
    out.write("    所以这里扫一段范围，真值由 MatrixSurvey 每局实测）\n\n")
    out.write("  techSpeed |  IP=1 件/秒 |  IP=4 件/秒 | 补料间隔内要吃掉(IP=4) | 上限 %d 够撑\n"
              % cap)
    out.write("  ----------+-------------+-------------+------------------------+-----------\n")

    worst_need = 0.0

    for ts in (1, 10, 100, 1000, 10000):
        r1 = research_items_per_sec(1, ts)
        r4 = research_items_per_sec(4, ts)
        # 两次虚拟补料之间要撑住的量
        need = r4 * interval / float(TICKS)
        worst_need = max(worst_need, need)
        secs = cap / r4 if r4 > 0 else float("inf")
        out.write("  %9d | %11.3f | %11.3f | %22.2f | %8.1f 秒\n" % (ts, r1, r4, need, secs))

    out.write("\n")
    out.write("  补料间隔 %d tick，最苛刻那一行要吃掉 %.2f 件；上限 %d 是它的 %.0f 倍。\n"
              % (interval, worst_need, cap, cap / worst_need))

    # ── 断言以**原版**为锚，不以我编的极端值为锚 ────────────────────────────
    #
    # 上面那张表扫到 techSpeed = 10000 是为了看趋势，但拿它当判据就是「按算得方便的
    # 数量级量」——本仓库在 sim_veinscale 上记过同一条。真正能当锚的只有一个数：
    # **原版自己的上限是 10 个**（UpdateNeedsResearch 的 36000 ÷ 3600），而原版玩家
    # 从来不会因为这个数饿死。所以判据是「相对原版几倍」，倍数本身要说得出理由。
    VANILLA_CAP = 36000 // MATRIX_SCALE

    # 下界：够覆盖一个补料间隔，而且留 8 倍余量。反解出它能撑到多高的 techSpeed，
    # 这样「够不够」变成一句可核对的话而不是一个感觉。
    covered_tech_speed = cap * MATRIX_SCALE / float(4 * interval) - 2
    out.write("  上限 %d 件按 ItemPoints=4 反解：能覆盖到 techSpeed ≈ %.0f 的补料间隔。\n"
              % (cap, covered_tech_speed))

    check(cap >= VANILLA_CAP,
          "researchStorage=%d 低于原版的 %d 个" % (cap, VANILLA_CAP))
    check(covered_tech_speed >= 1000,
          "researchStorage=%d 只覆盖到 techSpeed %.0f，低于 1000（已是原版可达值的百倍以上）"
          % (cap, covered_tech_speed))
    # 上界同样以原版为锚：超过原版 100 倍就不是缓冲了。原版 10 个 → 上限 1000 个。
    check(cap <= VANILLA_CAP * 100,
          "researchStorage=%d 超过原版 %d 个的 100 倍，那是仓库不是缓冲" % (cap, VANILLA_CAP))
    check(cap <= HARD_CAP_ITEMS,
          "researchStorage=%d 超过物理上限 %d（matrixServed 是 Int32，还要按 2 倍峰值算）"
          % (cap, HARD_CAP_ITEMS))
    check(supply <= cap,
          "supplyMatrixItems=%d 大于 researchStorage=%d：取料目标高过容量上限，永远填不满" % (supply, cap))

    # 堆叠：发货方写死留 2 个（IL 0141 的 7200 = 2 × 3600），所以整条链只由收货方的
    # needs 决定停不停。上限越大，「全跑到最上层」就越明显——上层填满之前，下面每层就是 2 个。
    keep_local = 7200 // MATRIX_SCALE
    out.write("\n  堆叠：发货方写死只留 %d 个（IL 0141 的 7200），上层没填满之前下面每层就是这个数。\n"
              % keep_local)
    out.write("  所以「矩阵全跑到最上层」的严重程度 = 上限 ÷ %d = %.0f 倍差距。\n"
              % (keep_local, cap / float(keep_local)))
    out.write("  原版上限是 10 个（UpdateNeedsResearch 的 36000），差距只有 %.0f 倍，所以看不出来。\n"
              % (10 / float(keep_local)))

    # 搬运速率是**速率**，不是容量。两者相等就意味着一 tick 搬空，下层永远停在 2 个。
    rate = cfg.get("researchTransferRate", 0)
    eff_rate = rate if rate > 0 else 10
    out.write("\n  向上层搬运速率：%d 个/tick/格（%d 个/秒）%s\n"
              % (eff_rate, eff_rate * TICKS, "（原版值）" if rate <= 0 else ""))
    check(eff_rate < cap,
          "搬运速率 %d 个/tick 不小于容量上限 %d 个：一 tick 就搬空，下层永远停在 %d 个——"
          "速率和容量是两回事（这正是 1.12.17 及以前那 12 处转译把 36000 换成容量上限造成的）"
          % (eff_rate, cap, keep_local))

    # 搬运速率**不是**消耗的约束，所以不拿消耗去断言它：每一层研究站除了从下层收货，
    # 自己也直接被传送带 / 分拣器 / 虚拟供料喂（那几条都不经过这个速率）。
    # 这里只报个对比，方便看「纯靠堆叠链喂上层」那种极端接法够不够。
    out.write("  （仅供参考：纯靠堆叠链喂上层时，这个速率相当于 ItemPoints=4 下的 techSpeed %.0f）\n"
              % (eff_rate * TICKS * MATRIX_SCALE / float(4 * TICKS) - 2))

    # ── 生产模式 ────────────────────────────────────────────────────────
    a_in = cfg["assembleStorage"]
    a_out = cfg["assembleOutputStorage"]
    batches = cfg["supplyAssembleBatches"]

    out.write("\n生产模式：每 tick 恒定 %d 个配方周期（引擎硬上限，研究站没有多周期路径）\n"
              % CRAFTS_PER_TICK)
    out.write("  = %d 份/秒。所以每个原料格每秒吃 `单份用量 × %d` 个。\n\n"
              % (CRAFTS_PER_TICK * TICKS, CRAFTS_PER_TICK * TICKS))
    out.write("  单份用量 | 每秒吃掉 | 补料间隔内要吃掉 | 上限 %d 够撑\n" % a_in)
    out.write("  ---------+----------+------------------+-----------\n")

    worst_in = 0.0

    for per_craft in (1, 2, 4, 10):
        per_sec = per_craft * CRAFTS_PER_TICK * TICKS
        need = per_sec * interval / float(TICKS)
        worst_in = max(worst_in, need)
        out.write("  %8d | %8d | %16.1f | %8.1f 秒\n" % (per_craft, per_sec, need, a_in / float(per_sec)))

    # 生产模式这一侧不需要猜任何未知量：每 tick 一份是引擎硬上限，单份用量最多 10（放宽估）。
    # 所以「几秒的生产」是个确定的单位，判据直接按秒写。
    IN_MAX_PER_SEC = 10 * CRAFTS_PER_TICK * TICKS      # 单份 10 个的最坏情况
    OUT_MAX_PER_SEC = 2 * CRAFTS_PER_TICK * TICKS      # 引力矩阵单次产 2

    out.write("  补料目标 supplyAssembleBatches = %d 份 = %.1f 秒的满速生产（间隔是 %.2f 秒）。\n"
              % (batches, batches / float(CRAFTS_PER_TICK * TICKS), interval / float(TICKS)))
    out.write("  产物格：每秒最多 %d 个（引力矩阵单次产 2），上限 %d 够撑 %.1f 秒。\n"
              % (OUT_MAX_PER_SEC, a_out, a_out / float(OUT_MAX_PER_SEC)))

    check(a_in >= worst_in * 4,
          "assembleStorage=%d 撑不住补料间隔（最苛刻要 %.1f 件，至少留 4 倍余量）" % (a_in, worst_in))
    check(a_in <= IN_MAX_PER_SEC * 10,
          "assembleStorage=%d 超过 10 秒的满速进料（%d 个/秒），那是仓库不是缓冲"
          % (a_in, IN_MAX_PER_SEC))
    check(batches >= interval * CRAFTS_PER_TICK * 4,
          "supplyAssembleBatches=%d 撑不住补料间隔的 4 倍余量" % batches)
    check(batches <= CRAFTS_PER_TICK * TICKS * 10,
          "supplyAssembleBatches=%d 超过 10 秒的满速生产，取料目标太大会把物流网吸空" % batches)
    check(a_out >= OUT_MAX_PER_SEC,
          "assembleOutputStorage=%d 不到一秒的产量（%d 个/秒）" % (a_out, OUT_MAX_PER_SEC))
    check(a_out <= OUT_MAX_PER_SEC * 10,
          "assembleOutputStorage=%d 超过 10 秒的产量，那是仓库不是缓冲" % a_out)

    # ── 一眼看全：每个上限折算成「几秒」 ────────────────────────────────
    #
    # 这张表是本次修复的由来。上限填大不会报错，它只是让研究站变成一个仓库，
    # 而在堆叠的情况下还会把货全推到最上层。折算成秒之后一眼就能看出哪个数离谱。
    out.write("\n折算成「够撑几秒」：\n")
    out.write("  %-22s %9s  %12s  %s\n" % ("旋钮", "当前值", "够撑", "锚"))

    for name, val, per_sec, anchor in (
            ("researchStorage", cap, research_items_per_sec(4, 1000), "原版 10 个的 %.0f 倍" % (cap / float(VANILLA_CAP))),
            ("supplyMatrixItems", supply, research_items_per_sec(4, 1000), "不超过上限"),
            ("assembleStorage", a_in, IN_MAX_PER_SEC, "%.1f 秒的满速进料" % (a_in / float(IN_MAX_PER_SEC))),
            ("assembleOutputStorage", a_out, OUT_MAX_PER_SEC, "%.1f 秒的产量" % (a_out / float(OUT_MAX_PER_SEC)))):
        out.write("  %-22s %9d  %10.1f 秒  %s\n" % (name, val, val / float(per_sec), anchor))

    # ── 反向对照：这些断言必须能拒掉 1.12.17 的那套数 ────────────────────────
    #
    # 一个从不失败的检查器和没有检查器是一回事。这里把旧值代进同样的判据，
    # 逐条确认它会被拒——否则上面「全部通过」说明不了任何事。
    OLD = {"researchStorage": 250000, "researchTransferRate": 250000,
           "assembleStorage": 10000000, "assembleOutputStorage": 10000000,
           "supplyAssembleBatches": 2000, "supplyMatrixItems": 1000}

    rejected = []

    if OLD["researchStorage"] > VANILLA_CAP * 100:
        rejected.append("researchStorage 25 万超过原版 10 个的 100 倍")
    if OLD["researchTransferRate"] >= OLD["researchStorage"]:
        rejected.append("搬运速率追平容量（一 tick 搬空，下层永远停在 2 个）")
    if OLD["assembleStorage"] > IN_MAX_PER_SEC * 10:
        rejected.append("assembleStorage 一千万超过 10 秒的满速进料")
    if OLD["assembleOutputStorage"] > OUT_MAX_PER_SEC * 10:
        rejected.append("assembleOutputStorage 一千万超过 10 秒的产量")
    if OLD["supplyAssembleBatches"] > CRAFTS_PER_TICK * TICKS * 10:
        rejected.append("supplyAssembleBatches 2000 份超过 10 秒的满速生产")
    if OLD["supplyMatrixItems"] > OLD["researchStorage"]:
        rejected.append("supplyMatrixItems 高过容量上限")

    out.write("\n反向对照（1.12.17 的那套数必须被拒，否则上面的「通过」说明不了任何事）：\n")

    for r in rejected:
        out.write("  拒绝：%s\n" % r)

    check(len(rejected) >= 5,
          "反向对照只拒掉了 %d 条，判据太松——检查器必须能认出旧配置的问题" % len(rejected))

    if failures:
        out.write("\n失败：\n")

        for f in failures:
            out.write("  · %s\n" % f)

        out.flush()

        return 1

    out.write("\n全部通过\n")
    out.flush()

    return 0


sys.exit(main())
