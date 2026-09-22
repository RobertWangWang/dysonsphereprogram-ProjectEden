# -*- coding: utf-8 -*-
"""离线验证物流配对表的**增量维护**和全量重建等价。

    python tools/sim_pairindex.py

**为什么这个模型值得留在仓库里。** 配对表是按 itemId 的等值连接，全量求值的下界是
Ω(输入 + 输出)——输出就是那几百万条配对，换任何数据结构都不可能更快（Yannakakis 1981，
O(N + OUT)，而这个查询是 free-connex）。唯一的出路是增量维护：放一座建筑只新增
「它自己那几条」。实测一颗 8,467 站的星球有 304 万条配对，单站均摊约 360 条。

而增量维护最容易出的错不是慢，是**静默地算错**：
  · 漏摘对侧（对侧表里留下指向旧状态的配对）
  · 重复发射（同一条配对进去两次，多重集不等）
  · 漏发站号比自己小的对侧（全量版有「对侧 > 自己」那道闸，增量版**必须**去掉它）

这三种都不报错，只会让货送错地方。所以这里逐条枚举随机序列，断言
**增量维护出来的多重集 == 从头全量重建的多重集**。

改 LocalPairIndex 的增量逻辑之前先改这里，**先让它复现出新行为，再去改 C#**。
"""
import io
import random
import sys
from collections import Counter

SUPPLY = 1
DEMAND = 2
NONE = 0


def full_rebuild(stations):
    """原版 / 全量索引版的语义：站点升序，只和站号更大的对侧配对，两边各存一份。"""
    pairs = {i: [] for i in stations}

    ids = sorted(stations)

    for i in ids:
        for k, (item, d) in enumerate(stations[i]):
            if item <= 0 or d == NONE:
                continue
            for j in ids:
                if j <= i:
                    continue
                for m, (item2, d2) in enumerate(stations[j]):
                    if item2 != item:
                        continue
                    if d == SUPPLY and d2 == DEMAND:
                        p = (i, k, j, m)
                    elif d == DEMAND and d2 == SUPPLY:
                        p = (j, m, i, k)
                    else:
                        continue
                    pairs[i].append(p)
                    pairs[j].append(p)

    return pairs


def detach(pairs, sid):
    """把 sid 参与的配对从所有对侧摘掉，并清空它自己的（DetachPairsOf）。"""
    for other in list(pairs):
        if other == sid:
            pairs[other] = []
            continue
        pairs[other] = [p for p in pairs[other] if p[0] != sid and p[2] != sid]


def emit_for(pairs, stations, sid, skip=()):
    """只发射 sid 参与的配对，两侧各存一份（EmitPairsFor）。

    **没有「对侧 > 自己」那道闸** —— 那是全量遍历里防重复发射用的，
    只遍历一个站点时留着它会漏掉所有站号更小的对侧。

    `skip` 是「本批里已经发过配对的键」（AlreadyEmitted）：成批处理时
    不跳过它们，键与键之间的配对会被发两次。
    """
    for k, (item, d) in enumerate(stations[sid]):
        if item <= 0 or d == NONE:
            continue
        for other in sorted(stations):
            if other == sid or other in skip:
                continue
            for m, (item2, d2) in enumerate(stations[other]):
                if item2 != item:
                    continue
                if d == SUPPLY and d2 == DEMAND:
                    p = (sid, k, other, m)
                elif d == DEMAND and d2 == SUPPLY:
                    p = (other, m, sid, k)
                else:
                    continue
                pairs[sid].append(p)
                pairs[other].append(p)


def multiset(pairs):
    """对账用的多重集：带上「这条存在谁身上」，少存一边是个真错误。"""
    return Counter((holder, p) for holder, lst in pairs.items() for p in lst)


def rand_slots(rng, items, nslots):
    out = []
    for _ in range(nslots):
        r = rng.random()
        if r < 0.2:
            out.append((0, NONE))
        else:
            out.append((rng.choice(items), SUPPLY if rng.random() < 0.5 else DEMAND))
    return out


def main():
    out = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", newline="")
    failures = []

    def check(ok, why):
        if not ok:
            failures.append(why)

    out.write("=== 物流配对表·增量维护离线验证 ===\n")

    # ── 1. 逐个加站：每加一个就增量，全程和全量重建比 ────────────────
    for seed in range(12):
        rng = random.Random(seed)
        items = [1001, 1002, 1003, 1004]
        stations = {}
        pairs = {}

        for sid in range(1, 13):
            stations[sid] = rand_slots(rng, items, rng.randint(1, 4))
            pairs[sid] = []
            detach(pairs, sid)          # 新站点，本来就没有旧配对
            emit_for(pairs, stations, sid)

        got = multiset(pairs)
        want = multiset(full_rebuild(stations))
        check(got == want, "加站序列（seed %d）：增量和全量不等，差 %d 条"
              % (seed, sum((got - want).values()) + sum((want - got).values())))

    out.write("  ① 逐个加站 ×12 组：%s\n" % ("通过" if not failures else "失败"))

    # ── 2. 随机改槽位内容：先摘再发，和全量比 ──────────────────────
    before = len(failures)

    for seed in range(12):
        rng = random.Random(1000 + seed)
        items = [1001, 1002, 1003]
        stations = {i: rand_slots(rng, items, rng.randint(1, 4)) for i in range(1, 11)}
        pairs = full_rebuild(stations)

        for _ in range(30):
            sid = rng.choice(sorted(stations))
            stations[sid] = rand_slots(rng, items, rng.randint(1, 4))
            detach(pairs, sid)
            emit_for(pairs, stations, sid)

            got = multiset(pairs)
            want = multiset(full_rebuild(stations))
            if got != want:
                check(False, "改槽位（seed %d，站 %d）：增量和全量不等" % (seed, sid))
                break

    out.write("  ② 随机改槽位 ×12 组 ×30 次：%s\n"
              % ("通过" if len(failures) == before else "失败"))

    # ── 3. 拆站：只摘不发，和「剩下这些站的全量重建」比 ──────────────
    before = len(failures)

    for seed in range(12):
        rng = random.Random(2000 + seed)
        items = [1001, 1002, 1003]
        stations = {i: rand_slots(rng, items, rng.randint(1, 4)) for i in range(1, 11)}
        pairs = full_rebuild(stations)

        for _ in range(5):
            if len(stations) <= 2:
                break
            sid = rng.choice(sorted(stations))
            detach(pairs, sid)
            del stations[sid]
            del pairs[sid]

            got = multiset(pairs)
            want = multiset(full_rebuild(stations))
            if got != want:
                check(False, "拆站（seed %d，站 %d）：摘除之后和全量不等" % (seed, sid))
                break

    out.write("  ③ 随机拆站 ×12 组 ×5 次：%s\n"
              % ("通过" if len(failures) == before else "失败"))

    # ── 3.5 一批多个变更站点：共用一次索引，键之间的配对不能发两次 ──────
    #
    # RebuildMany 把一次冲刷里的 N 个 key 合并处理（共用一次索引重建，因为建索引
    # 才是增量的单价）。陷阱是：先摘 A 再摘 B，然后发 A 时 B 已在索引里 → 发出 A↔B，
    # 再发 B 时 A 也在索引里 → **又发一次**。多重集就多了一条。
    # 解法是按处理顺序跳过已经发过的键。这里连这条解法一起验。
    def rebuild_many(pairs, stations, keys):
        live = sorted(k for k in keys if k in stations)
        for k in live:
            detach(pairs, k)
        emitted = set()
        for k in live:
            emit_for(pairs, stations, k, skip=emitted)
            emitted.add(k)

    before = len(failures)

    for seed in range(12):
        rng = random.Random(3000 + seed)
        items = [1001, 1002, 1003]
        stations = {i: rand_slots(rng, items, rng.randint(1, 4)) for i in range(1, 13)}
        pairs = full_rebuild(stations)

        for _ in range(20):
            n = rng.randint(2, 6)
            keys = rng.sample(sorted(stations), n)

            for k in keys:
                stations[k] = rand_slots(rng, items, rng.randint(1, 4))

            rebuild_many(pairs, stations, keys)

            got = multiset(pairs)
            want = multiset(full_rebuild(stations))
            if got != want:
                extra = sum((got - want).values())
                missing = sum((want - got).values())
                check(False, "成批变更（seed %d，键 %s）：多 %d 条、少 %d 条"
                      % (seed, keys, extra, missing))
                break

    out.write("  ③b 成批变更（2~6 个键）×12 组 ×20 次：%s\n"
              % ("通过" if len(failures) == before else "失败"))

    # ── 3.6 负面对照：不跳过已发过的键，必须被抓到 ──────────────────
    def rebuild_many_buggy(pairs, stations, keys):
        live = sorted(k for k in keys if k in stations)
        for k in live:
            detach(pairs, k)
        for k in live:
            emit_for(pairs, stations, k)      # ← 没有 skip，键之间会发两次

    stations = {
        1: [(1001, SUPPLY)],
        2: [(1001, DEMAND)],
        3: [(1001, SUPPLY)],
    }
    pairs = full_rebuild(stations)
    rebuild_many_buggy(pairs, stations, [1, 2])
    caught = multiset(pairs) != multiset(full_rebuild(stations))
    out.write("  ③c 负面对照（批内重复发射）：%s\n" % ("已抓到" if caught else "**没抓到**"))
    check(caught, "批内重复发射没被抓到——检查器失效")

    # ── 4. 负面对照：漏掉「对侧 > 自己」那道闸的反向错误必须被抓到 ──
    #
    # 如果 emit_for 错误地保留了全量版那道闸，站号比自己小的对侧就会全漏。
    # 这里故意复现那个 bug，断言检查器**能**发现它——一个抓不到已知 bug 的
    # 检查器，通过了也说明不了任何事。
    def emit_for_buggy(pairs, stations, sid):
        for k, (item, d) in enumerate(stations[sid]):
            if item <= 0 or d == NONE:
                continue
            for other in sorted(stations):
                if other <= sid:          # ← 这就是那个 bug
                    continue
                for m, (item2, d2) in enumerate(stations[other]):
                    if item2 != item:
                        continue
                    if d == SUPPLY and d2 == DEMAND:
                        p = (sid, k, other, m)
                    elif d == DEMAND and d2 == SUPPLY:
                        p = (other, m, sid, k)
                    else:
                        continue
                    pairs[sid].append(p)
                    pairs[other].append(p)

    # **确定性构造，不用随机种子。** 上一版这里是随机的，结果那个种子下的站点
    # 恰好没有站号更小的对侧，bug 触发不了、对照报「没抓到」——
    # 一个碰运气才能触发的负面对照，和没有负面对照是一回事。
    #
    # 造法：站 1 供给 1001，站 2~5 需求 1001。重发**站 3**（需求）时，
    # 它唯一的对侧是站 1，站号比它小——正好落在那道多余的闸后面。
    stations = {
        1: [(1001, SUPPLY)],
        2: [(1001, DEMAND)],
        3: [(1001, DEMAND)],
        4: [(1001, DEMAND)],
        5: [(1001, DEMAND)],
    }
    pairs = full_rebuild(stations)
    sid = 3
    detach(pairs, sid)
    emit_for_buggy(pairs, stations, sid)
    caught = multiset(pairs) != multiset(full_rebuild(stations))
    out.write("  ④ 负面对照（故意漏掉小站号对侧）：%s\n" % ("已抓到" if caught else "**没抓到**"))
    check(caught, "负面对照没被抓到——检查器本身失效了，前三项的通过不能作数")

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
