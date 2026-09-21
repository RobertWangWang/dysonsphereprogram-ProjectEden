"""离线复现 AssemblerComponent.InternalUpdate 的时序，验证「分频 + 放行」的净效果。

    python tools/sim_throttle.py

**为什么这个模型值得留在仓库里。** 反物质那四座建筑配了 tickDivider，而上一版只写了
「压」没写「放」——刚攒满的那个周期每次都被抹掉，整条产线一件产物都没有。那个 bug
在这里跑一遍就能看见（周期 0、扣料 1），而修完之后的两条数也是在这里量出来的：

  · 周期数恰好是 tick 数 ÷ 分频数（8/10/35/60 秒配方都一样）；
  · 增产剂的额外产出比例回到 incTableMilli 本身（0.24 对原版 0.2503）——
    第一版直接把增产计时器推到门槛，实测是 1.00，四倍于原版。

原版语义逐条照 IL 抄，偏移写在注释里；两个钩子对应
MegaThrottle.Hold / MegaThrottle.Release。改那两个方法之前先改这里，
**先让它复现出新行为，再去改 C#**——本仓库「变换长出新情况时先长它的检查器」那条。
"""
import io
import sys

SPEED = 100_000_000          # megabuildings.json 的 assemblerSpeed
MK3_MILLI = 0.25             # Cargo.incTableMilli 第 4 档（增产剂 Mk.III）


def run(divider, ticks, release_enabled, spray_milli=0.0, power=1.0,
        time_spend=21_000_000, extra_spend=210_000_000, entity_id=227):
    """跑 ticks 个 tick，返回（结算周期数，额外产出次数，扣料次数）。"""
    time = 0
    extra_time = 0
    replicating = False
    speed_override = SPEED
    extra_speed = 0
    served = 10 ** 9          # 物流站每 tick 补满，不在这里设限
    consumed = 0
    cycles = 0
    extra_cycles = 0

    for tick in range(ticks):
        # ── MegaTick：跑在原版调用**之前** ──────────────────────
        if divider > 1:
            hold = tick % divider != entity_id % divider

            # 两条钩子都先把增产计时器倒回「进度」本身：上一次原版调用在底部
            # （IL 0586）无条件加了一个 extraSpeed，那一笔不是这个周期该得的。
            # 不倒回的话 Release 那一次看到的是「进度 + 一整个 extraSpeed」，
            # 当场越过门槛——实测比例会是 1.00 而不是原版的 0.25。
            if release_enabled and replicating and extra_speed > 0:
                extra_time -= extra_speed
            else:
                extra_time = -extra_speed - 1

            if hold:
                # **前置钩子取消不了它后面那次调用**：压完之后原版照样跑一遍，
                # 所以这里绝不能 continue——那会把「扣料」也一起跳掉，模型就失真了
                time = -speed_override - 1                          # MegaThrottle.Hold
            elif release_enabled and replicating:                   # MegaThrottle.Release
                if time < time_spend:
                    time = time_spend
                if extra_speed > 0 and speed_override > 0:
                    # 一个周期该分到多少增产进度：原版每加一次 time 涨 S、extraTime 涨 E，
                    # 而一个周期只花掉 timeSpend 的 time，所以按比例是 E × timeSpend / S
                    extra_time += extra_speed * time_spend // speed_override

        # ── 原版 AssemblerComponent.InternalUpdate ──────────────
        if power < 0.1:                                             # IL 0000
            continue
        if extra_time >= extra_spend:                               # IL 0022
            extra_cycles += 1
            extra_time -= extra_spend                               # IL 00F3
        if time >= time_spend:                                      # IL 0101
            replicating = False                                     # IL 0127
            extra_speed = 0                                         # IL 034D
            speed_override = SPEED                                  # IL 0355
            cycles += 1
            time -= time_spend                                      # IL 0375
        if not replicating:                                         # IL 0383
            if served < 1:                                          # IL 03D7 够不够
                time = 0
                continue
            served -= 1
            consumed += 1
            extra_speed = int(SPEED * spray_milli * 10 + 0.1)       # IL 04C7
            speed_override = SPEED                                  # IL 04F2
            replicating = True                                      # IL 054E
        if time < time_spend and extra_time < extra_spend:          # IL 055D / 0566
            time += int(power * speed_override)                     # IL 056F
            extra_time += int(power * extra_speed)                  # IL 0586

    return cycles, extra_cycles, consumed


def main():
    out = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", newline="")
    ticks = 7000
    div = 70
    want = ticks // div
    failures = []

    def check(ok, why):
        if not ok:
            failures.append(why)

    out.write("=== 巨型建筑分频节流·离线复现（%d tick，分频 %d）===\n" % (ticks, div))

    c, e, k = run(div, ticks, release_enabled=False)
    out.write("只压不放（1.12.5 及以前）: 周期 %3d  额外 %3d  扣料 %3d   ← 应当是 0 个周期\n"
              % (c, e, k))
    check(c == 0, "只压不放居然结算出了周期，模型和当年的现象对不上")

    c, e, k = run(div, ticks, release_enabled=True)
    out.write("压 + 放（1.12.6 起）     : 周期 %3d  额外 %3d  扣料 %3d   ← 应当是 %d 个周期\n"
              % (c, e, k, want))
    # 扣料比结算多 1 是**原版流水线自己的形状**：任何时刻机器里都压着一份
    # 「已扣料、还没发货」的周期（replicating == true）。不是漏，拆机时会退还。
    check(c == want, "周期数不等于 tick 数 ÷ 分频数")
    check(k == c + 1, "扣料次数不是「结算数 + 在制的那一份」")
    check(e == 0, "没喷增产剂却出了额外产出")

    c, e, k = run(div, ticks, release_enabled=True, spray_milli=MK3_MILLI)
    ref_c, ref_e, _ = run(1, 2000, release_enabled=True, spray_milli=MK3_MILLI)
    out.write("压 + 放 + 增产剂 Mk.III  : 周期 %3d  额外 %3d  扣料 %3d\n" % (c, e, k))
    out.write("    额外/周期 = %.4f　不分频的巨型建筑是 %.4f　标称的 incTableMilli 是 %.4f\n"
              % (e / c, ref_e / ref_c, MK3_MILLI))
    check(c == want and k == c + 1, "喷了增产剂之后周期 / 扣料对不上")
    check(abs(e / c - MK3_MILLI) < 0.02, "增产比例和原版对不上")
    check(abs(ref_e / ref_c - MK3_MILLI) < 0.02, "参照系（不分频）本身就算错了")

    out.write("\n配方时长扫描（应当恒为 %d 个周期——分频数才是配平的单位，不是倍率）：\n" % want)
    for secs in (8, 10, 35, 60):
        ts = secs * 60 * 10000
        c2, e2, k2 = run(div, ticks, release_enabled=True, time_spend=ts, extra_spend=ts * 10)
        out.write("  %2d 秒配方: 周期 %3d  扣料 %3d\n" % (secs, c2, k2))
        check(c2 == want and k2 == c2 + 1, "%d 秒配方对不上" % secs)

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
