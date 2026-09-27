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
        time_spend=21_000_000, extra_spend=210_000_000, entity_id=227,
        cycles_per_tick=1, want_extra_time=False,
        slot_room_per_tick=None, gate_coeff=None, product_count=1,
        stall_aware=False, want_gate=False):
    """跑 ticks 个 tick，返回（结算周期数，额外产出次数，扣料次数）。

    cycles_per_tick 就是 megabuildings.json 那个旋钮：RunExtraCycles 在原版那次调用
    **之前**补跑 cycles_per_tick − 1 遍，所以一个非压制 tick 一共跑 cycles_per_tick 遍。
    压制 tick 直接 return 0，补跑一遍都没有，只剩原版那一次（而且被 Hold 压住）。

    slot_room_per_tick / gate_coeff 是「产物出不去」那一族的两个闸，默认都关着：
    None 表示物流站槽位每 tick 全收（老用例就是这么假设的，它们量的是分频的时序，
    不是产出闸），此时闸门在结构上碰不到，模型里就不引入它。给了值才参与。

    stall_aware 是本次修复：产出闸拒绝结算时原版**先**在 IL 0129 把 replicating
    抹成 false、**再**判闸，而拒绝路径是裸 ret，不会把它恢复。原版靠
    「time 一直 ≥ timeSpend，所以永远重新进结算块、根本走不到 IL 0383」把这个洞盖住；
    而 Hold / Suppress 把 time 压成负数正好就是通往 IL 0383 的那条路。
    """
    time = 0
    extra_time = 0
    replicating = False
    speed_override = SPEED
    extra_speed = 0
    served = 10 ** 9          # 物流站每 tick 补满，不在这里设限
    consumed = 0
    cycles = 0
    extra_cycles = 0
    produced = 0              # AssemblerComponent.produced[0]
    shipped = 0               # 已经搬进物流站槽位的件数
    gate_hits = 0             # 产出闸拒绝结算的次数
    stall_ticks = 0           # 「闸拒绝过、我们这一 tick 不插手」的次数

    for tick in range(ticks):
        # ── MegaStationPatches.UpdateStationStorage：produced → 物流站槽位 ──
        #
        # 它排在 MegaTick 里的 RunExtraCycles **之前**（UpdateSlots 那一步），
        # 所以每 tick 先把缓冲区腾一次，能腾多少由槽位剩余容量决定。
        if slot_room_per_tick is None:
            shipped += produced
            produced = 0
        elif slot_room_per_tick > 0:
            give = min(produced, slot_room_per_tick)
            produced -= give
            shipped += give

        # ── MegaTick：跑在原版调用**之前** ──────────────────────
        hold = False

        # ── 修复：产出闸拒绝过的那一 tick，我们一个字都不碰 ──────────────
        #
        # 判据是**实测末态**，不是复现闸门条件（复现会随原版多一道闸而失准）：
        # 只有「闸拒绝过」这一种收尾会留下「!replicating 而且 time ≥ timeSpend」——
        # 缺料那一支留下的是 time == 0（IL 03E6）、刚建好的是 0、被压住的是 ≈ −1、
        # 正常跑着的 replicating 是 true。
        #
        # 不压、不放、不补跑之后，原版自己那一次调用会照原样重新进结算块再被拒一次,
        # **也就是纯原版在产物出不去时的行为：停着**。这比「把 replicating 补回去」
        # 强的地方是它一个字段都不写，也不需要预测这一次放行会不会被拒。
        stalled = stall_aware and not replicating and time >= time_spend

        if stalled:
            stall_ticks += 1
        elif divider > 1:
            hold = tick % divider != entity_id % divider

            # 两条钩子都先把增产计时器倒回「进度」本身：上一次原版调用在底部
            # （IL 0586）无条件加了一个 extraSpeed，那一笔不是这个周期该得的。
            # 不倒回的话 Release 那一次看到的是「进度 + 一整个 extraSpeed」，
            # 当场越过门槛——实测比例会是 1.00 而不是原版的 0.25。
            if release_enabled and replicating and extra_speed > 0:
                extra_time -= extra_speed
            elif extra_speed > 0:
                extra_time = -extra_speed - 1
            else:
                # 没喷增产剂：哨兵没有意义（加 0 跨不过门槛），而 extraTime 是存档字段、
                # 原版不清零，写进去的 −1 会永久留着并让 CanBatch 把这台踢出批量结算。
                extra_time = 0

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
        #
        # 一个 tick 跑几遍：压制 tick 只有原版那一次（RunExtraCycles 已经 return 0），
        # 其余 tick 是「补跑 cycles_per_tick − 1 遍 + 原版 1 遍」。
        # 顺序上补跑在前（MegaTick 是前置钩子），但这个模型里两者完全同构，不区分。
        # 闸拒绝过的那一 tick：补跑一遍都不跑（反正每一遍都会被拒），只剩原版那一次。
        calls = 1 if stalled or (divider > 1 and hold) else cycles_per_tick

        for _ in range(calls):
            if power < 0.1:                                         # IL 0000
                break
            if extra_time >= extra_spend:                           # IL 0022
                extra_cycles += 1
                # **增产那条旁路一处闸都没有**（IL 0050 / 0095 直接 produced += counts）。
                # 原版碰不到：它只在 IL 0586 推进 extraTime，而闸拒绝时是裸 ret、走不到那里。
                produced += product_count                           # IL 0050
                extra_time -= extra_spend                           # IL 00F3
            if time >= time_spend:                                  # IL 0101
                replicating = False                                 # IL 0127

                # ── 产出闸 IL 0138–02E8 ─────────────────────────────
                # 拒绝时是 `ldc.i4.0 ; ret`：**不恢复上面那句抹掉的 replicating**，
                # 也不扣 time。原版靠「time 没扣过 → 下一次照样进这个块 → 走不到
                # IL 0383」自洽；Hold / Suppress 把 time 压成负数就绕开了这层保护。
                if gate_coeff is not None and produced > product_count * gate_coeff:
                    gate_hits += 1

                    continue                                        # IL 0183 ret 0

                produced += product_count                           # IL 0185
                extra_speed = 0                                     # IL 034D
                speed_override = SPEED                              # IL 0355
                cycles += 1
                time -= time_spend                                  # IL 0375
            if not replicating:                                     # IL 0383
                if served < 1:                                      # IL 03D7 够不够
                    time = 0
                    break
                served -= 1
                consumed += 1
                extra_speed = int(SPEED * spray_milli * 10 + 0.1)   # IL 04C7
                speed_override = SPEED                              # IL 04F2
                replicating = True                                  # IL 054E
            if time < time_spend and extra_time < extra_spend:      # IL 055D / 0566
                time += int(power * speed_override)                 # IL 056F
                extra_time += int(power * extra_speed)              # IL 0586

    if want_gate:
        # 槽位满那一族要看的数：结算、扣料、闸拒绝、不插手的 tick、缓冲、增产计时器
        return cycles, consumed, gate_hits, stall_ticks, produced, extra_time

    if want_extra_time:
        return cycles, extra_cycles, consumed, extra_time

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

    # ── 全局分频（globalTickDivider）：产能必须一个周期都不差 ────────────────
    #
    # 它把**每一座**巨型建筑的分频数乘以 G、周期数也乘以 G，所以推导上
    # 吞吐 = 周期/分频 = (基准×G)/(每座×G) 与 G 无关。推导归推导，这里量一遍——
    # 原版是「上一次攒、这一次结」的两段流水线，而分频恰好让「攒满的下一 tick」
    # 变成压制 tick，1.12.5 就是在这个接缝上丢光了产量的。
    out.write("\n全局分频 G：产能应当与 G 无关（分频和周期同乘 G）\n")
    base_c, base_e, base_k = run(1, ticks, release_enabled=True, cycles_per_tick=60)
    out.write("  G = 1（基准，60 周期/tick）      : 周期 %6d  扣料 %6d\n" % (base_c, base_k))

    for gg in (2, 3, 4, 8):
        c3, e3, k3 = run(gg, ticks, release_enabled=True, cycles_per_tick=60 * gg)
        out.write("  G = %d（%3d 周期/tick，%d tick 一次): 周期 %6d  扣料 %6d  差 %+d\n"
                  % (gg, 60 * gg, gg, c3, k3, c3 - base_c))
        # 容差 = 一个 G 周期的边界效应：7000 不一定整除 G，收尾那一轮可能少跑一次
        check(abs(c3 - base_c) <= 60 * gg, "全局分频 G = %d 改变了产能（差 %d 个周期）" % (gg, c3 - base_c))

    # 反物质那四座（cyclesPerTick 1 / tickDivider 70）叠上全局分频，同样不能变
    out.write("  反物质四座（1 周期 / 70 分频）叠 G：\n")
    ref4, _, _ = run(70, ticks, release_enabled=True, cycles_per_tick=1)

    for gg in (2, 4):
        c4, _, _ = run(70 * gg, ticks, release_enabled=True, cycles_per_tick=gg)
        out.write("    G = %d: 周期 %3d（不叠是 %3d）\n" % (gg, c4, ref4))
        check(abs(c4 - ref4) <= gg, "全局分频改变了反物质那四座的产能")

    # ── 压制不许往 extraTime 里留残值（没喷增产剂时）────────────────────────
    #
    # 实际发作过：RewindExtra 在 extraSpeed == 0 时写 −1，而 extraTime 是存档字段、
    # 原版推进它的唯一一处乘的正是 extraSpeed，所以那个 −1 永久留在存档里，
    # MegaBatchSettle.CanBatch 据此把每一台都踢出批量结算——**产量一件不差，只是慢几十倍**。
    # 这里把 run() 改成返回末态的 extra_time，断言「不喷就不留残值」。
    out.write("\n压制残值：没喷增产剂时 extraTime 必须停在 0\n")

    for dv, cyc in ((2, 120), (70, 1), (4, 240)):
        _, _, _, left = run(dv, ticks, release_enabled=True, cycles_per_tick=cyc, want_extra_time=True)
        out.write("  分频 %2d / %3d 周期: 末态 extraTime = %d\n" % (dv, cyc, left))
        check(left == 0, "分频 %d 在没喷增产剂时往 extraTime 留了残值 %d" % (dv, left))

    # 喷了的那一支不能被上面那条改坏：残值本来就该有
    _, e5, _, left5 = run(70, ticks, release_enabled=True, spray_milli=MK3_MILLI, want_extra_time=True)
    out.write("  分频 70 + 增产剂 Mk.III: 额外 %d 次，末态 extraTime = %d（这一支有残值是对的）\n"
              % (e5, left5))
    check(e5 > 0, "喷了增产剂却一次额外产出都没有")

    # ── 槽位满：必须真的停，不能一边空转一边继续扣料 ────────────────────────
    #
    # 玩家报的是「物流站属性存满了产物，巨型建筑还是会继续生产，不会停止」。
    # 病因在两句 IL 的配合上：产出闸拒绝结算时，原版**先**在 IL 0129 把 replicating
    # 抹成 false、**再**判闸，而拒绝路径是 `ldc.i4.0 ; ret`，既不恢复那个标记也不扣 time。
    # 纯原版自洽，因为 time 一直 ≥ timeSpend，下一次调用照样重新进结算块，
    # **永远走不到 IL 0383 那句 `if (replicating)`**。
    # 而 MegaThrottle.Hold 把 time 压成 −speedOverride−1 正好就是通往 IL 0383 的那条路：
    # 那里看到 replicating 是个过期的 false，于是**为一个产物永远发不出去的周期再扣一次料**。
    #
    # globalTickDivider 默认 2，所以每座巨型建筑每隔一 tick 就来一次压制 ——
    # 一台产物槽满的巨型建筑每 2 个 tick 白吃一份原料，30 份/秒。
    gate = 119                # MegaOutputGatePatches.Scale：cyclesPerTick × G − 1
    out.write("\n槽位满（产物出不去）：应当停产、且一份原料都不再扣\n")

    c6, k6, g6, r6, p6, _ = run(2, ticks, release_enabled=True, cycles_per_tick=1,
                                slot_room_per_tick=0, gate_coeff=gate,
                                stall_aware=False, want_gate=True)
    out.write("  未修: 周期 %4d  扣料 %4d  闸拒绝 %4d   ← 扣料远多于周期就是在白吃原料\n"
              % (c6, k6, g6))
    check(g6 > 0, "槽位满了闸却一次都没拒绝，模型没复现出「产物出不去」")
    check(k6 > c6 + 1, "模型没复现出那个原料黑洞（扣料应当远多于结算）")

    c7, k7, g7, r7, p7, x7 = run(2, ticks, release_enabled=True, cycles_per_tick=1,
                                 slot_room_per_tick=0, gate_coeff=gate,
                                 stall_aware=True, want_gate=True)
    out.write("  已修: 周期 %4d  扣料 %4d  闸拒绝 %4d  不插手 %4d  缓冲 %3d\n"
              % (c7, k7, g7, r7, p7))
    # 扣料 = 结算 + 1：那多出来的一份是原版流水线自己压着的「在制」周期，不是漏
    check(k7 == c7 + 1, "修完之后扣料仍然不等于「结算 + 在制的那一份」（%d vs %d）" % (k7, c7))
    check(c7 == c6, "修复改变了槽位满之前的产量（%d vs %d）" % (c7, c6))
    check(p7 <= gate + 1, "修完之后产物缓冲仍然越过闸门上限（%d > %d）" % (p7, gate + 1))
    out.write("    白吃的原料：未修 %d 份 → 已修 %d 份\n" % (k6 - c6 - 1, k7 - c7 - 1))

    # 喷了增产剂的那一支：extraTime 是存档字段，不许漂
    _, k8, _, _, p8, x8 = run(2, ticks, release_enabled=True, cycles_per_tick=1,
                              spray_milli=MK3_MILLI, slot_room_per_tick=0,
                              gate_coeff=gate, stall_aware=True, want_gate=True)
    out.write("  已修 + 增产剂: 扣料 %4d  缓冲 %3d  末态 extraTime = %d\n" % (k8, p8, x8))
    check(abs(x8) <= 210_000_000, "槽位满 + 增产剂时 extraTime 漂了（%d）——那是存档字段" % x8)
    check(p8 <= gate + 1, "槽位满 + 增产剂时产物缓冲越过了闸门（%d）" % p8)

    # 槽位没满的时候，修复必须一个周期都不改
    out.write("  槽位不满（回归）：修复不许改变任何产量\n")

    for room in (1, 2, 1000):
        a1, b1, _, _, _, _ = run(2, ticks, release_enabled=True, cycles_per_tick=1,
                                 slot_room_per_tick=room, gate_coeff=gate,
                                 stall_aware=False, want_gate=True)
        a2, b2, _, _, _, _ = run(2, ticks, release_enabled=True, cycles_per_tick=1,
                                 slot_room_per_tick=room, gate_coeff=gate,
                                 stall_aware=True, want_gate=True)
        out.write("    槽位每 tick 收 %4d 件: 周期 %4d→%4d  扣料 %4d→%4d\n"
                  % (room, a1, a2, b1, b2))
        check(a1 == a2, "槽位每 tick 收 %d 件时修复改变了产量（%d → %d）" % (room, a1, a2))
        check(b2 <= b1, "槽位每 tick 收 %d 件时修复反而多扣了料" % room)

    # ── 背压区（排得出去但排得慢）：修复不许把产量压下去 ──────────────────
    #
    # 这一段才是这个修复唯一可能亏产的地方：闸拒绝过的那一 tick 我们不补跑周期，
    # 只剩原版那一次。结论是**不亏**，而且理由是结构性的——放行那一 tick 能一次结算
    # base × G 个周期，只要 base ≥ 排货速率就追得回来，而「排得比造得慢」正是这个前提。
    out.write("  背压区（120 周期/tick 对上有限排货量）：产量应当只由排货速率决定\n")

    for room in (1, 10, 100):
        d1, e1, _, _, _, _ = run(2, ticks, release_enabled=True, cycles_per_tick=120,
                                 slot_room_per_tick=room, gate_coeff=gate,
                                 stall_aware=False, want_gate=True)
        d2, e2, _, _, _, _ = run(2, ticks, release_enabled=True, cycles_per_tick=120,
                                 slot_room_per_tick=room, gate_coeff=gate,
                                 stall_aware=True, want_gate=True)
        out.write("    每 tick 收 %3d 件: 周期 %6d→%6d  扣料 %6d→%6d  白吃 %5d→%5d\n"
                  % (room, d1, d2, e1, e2, e1 - d1 - 1, e2 - d2 - 1))
        check(d2 >= d1, "背压区（每 tick 收 %d 件）修复亏了产量（%d → %d）" % (room, d1, d2))
        check(e2 == d2 + 1, "背压区（每 tick 收 %d 件）修完之后还在白吃原料" % room)

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
