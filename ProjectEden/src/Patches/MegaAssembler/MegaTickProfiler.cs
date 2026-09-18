// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Diagnostics;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把 <c>MegaAssemblerPatches.MegaTick</c> 每 tick 的耗时按阶段拆开。
    ///
    /// <b>它回答的是一个别处分不开的问题。</b> 游戏性能面板的「生产设施」
    /// （<c>ECpuWorkEntry.Facilities</c>）里混着两样东西：原版的配方结算，和
    /// <b>本 mod 自己插进装配 tick 的那些每建筑每 tick 的活</b>——储物格同步要扫 30 格、
    /// 传送带槽位 12 个、催化剂床、燃烧厂。实测一颗星球这一栏 12.276 ms、占逻辑帧 71%，
    /// 而「大头是原版周期」和「大头是我们的包装」该动的地方完全不同：前者只剩风险很高的
    /// 批量结算，后者加个脏标记就行。<b>没有这行日志就只能猜，而这个仓库为猜付过太多次钱。</b>
    ///
    /// <b>三个实现上的讲究：</b>
    ///
    /// 1. <b>计时用 <c>Stopwatch.GetTimestamp()</c>，不用 <c>Time.realtimeSinceStartup</c>。</b>
    ///    这条路跑在 <c>_assembler_parallel</c> 上，Unity 的时间 API 在工作线程上不可用；
    ///    <c>GetTimestamp</c> 是 QueryPerformanceCounter，线程安全且便宜。
    ///
    /// 2. <b>换算成「每逻辑帧多少毫秒」时，帧数是量出来的，不是按 60 算的。</b>
    ///    这颗存档现在跑在 58.4 ups 而不是 60——正是因为卡，所以拿 60 去除必然低估，
    ///    而低估的方向恰好会让人以为问题比实际小。报告时读 <c>GameMain.gameTick</c> 的
    ///    差值，那是精确的。（它在换存档时会倒退，所以差值非正就重新对表。）
    ///
    /// 3. <b>报的是这 60 秒的增量，不是全局累计。</b> 累计值会被开局那几分钟稀释。
    /// </summary>
    [HarmonyPatch]
    internal static class MegaTickProfiler
    {
        /// <summary>
        /// 每次读配置太贵（这是 tick 路径），所以缓存一次。
        /// <c>MegaBuildingRegistry.Config</c> 在 <c>Awake</c> 里就位，而 tick 远在其后。
        /// </summary>
        private static int _enabled = -1;

        internal static bool Enabled
        {
            get
            {
                int e = _enabled;

                if (e >= 0) return e != 0;

                e = MegaBuildingRegistry.Config?.phaseTiming == true ? 1 : 0;
                _enabled = e;

                return e != 0;
            }
        }

        /// <summary>关掉时返回 0，调用方据此跳过 <c>Add*</c>——一次字段读，没有时间戳开销。</summary>
        internal static long Now() => Enabled ? Stopwatch.GetTimestamp() : 0L;

        private static long _tCycles;   // RunExtraCycles：真正的配方结算
        private static long _tStorage;  // MegaStationPatches.UpdateStationStorage：30 格同步
        private static long _tSlots;    // 本 mod 自己的传送带进出货槽位
        private static long _tOther;    // ApplySpeed / 催化剂床 / 燃烧厂
        private static long _buildings; // 被 tick 到的巨型建筑次数

        internal static void AddCycles(long t0) { if (t0 != 0L) Interlocked.Add(ref _tCycles, Stopwatch.GetTimestamp() - t0); }
        internal static void AddStorage(long t0) { if (t0 != 0L) Interlocked.Add(ref _tStorage, Stopwatch.GetTimestamp() - t0); }
        internal static void AddSlots(long t0) { if (t0 != 0L) Interlocked.Add(ref _tSlots, Stopwatch.GetTimestamp() - t0); }
        internal static void AddOther(long t0) { if (t0 != 0L) Interlocked.Add(ref _tOther, Stopwatch.GetTimestamp() - t0); }
        internal static void CountBuilding() { if (Enabled) Interlocked.Increment(ref _buildings); }

        private static float _nextReport;
        private static long _lastGameTick = -1;
        private static long _lc, _lst, _lsl, _lo, _lb;
        private static int _entered;

        // ── 标定：不信 Stopwatch.Frequency，量一遍 ──────────────────
        //
        // **第一版信了它，报出来的数差了 12 倍。** 那一版写着
        // `msPerFrame = 1000.0 / Stopwatch.Frequency / ticks`，运行时自报 10,000,000 Hz，
        // 于是 60 秒的墙钟里"累计"出了 731 秒的耗时——每次 InternalUpdate 调用 6.7 µs，
        // 而它只有 693 条 IL。**而且它不可能是并行造成的**：本地星球 1080 台、全局
        // 1194 台，其余星球只有 114 台，凑不出十二条线程同时跑在 MegaTick 里。
        //
        // 所以这里改成量：报告在主线程上，两次报告之间的
        // <c>Time.realtimeSinceStartup</c> 差值就是墙钟秒数，同一窗口的
        // <c>GetTimestamp()</c> 差值除以它就是**这个运行时实际的**每秒计数。
        // 标定值和自报值差得离谱时会额外打一行，把两个数都摆出来。
        //
        // 这条规矩本仓库写过很多遍，这次是在自己身上又验了一次：
        // **一个自报的常量在核对之前只是主张**（`kMaxCargoFlowSpeedPerSecond` 那条）。
        private static long _lastStamp;
        private static float _lastWall;
        private static int _warnedFreq;

        /// <summary>
        /// 每 60 秒报一行。挂 <c>UIGame._OnUpdate</c> 是因为它<b>在主线程</b>：
        /// 累加发生在并行的装配 tick 上，而 <c>Time.realtimeSinceStartup</c> 只能主线程读。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate_PhaseReport()
        {
            if (!Enabled) return;

            if (Interlocked.Exchange(ref _entered, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo("巨型建筑·分段耗时：统计挂点已跑到，之后每 60 秒报一行。");

            float now = UnityEngine.Time.realtimeSinceStartup;

            if (now < _nextReport) return;

            long gameTick = GameMain.gameTick;

            // 第一次只对表不报；gameTick 倒退（换了存档）也重新对表。
            if (_nextReport <= 0f || _lastGameTick < 0 || gameTick <= _lastGameTick)
            {
                _nextReport = now + 60f;
                _lastGameTick = gameTick;
                _lastStamp = Stopwatch.GetTimestamp();
                _lastWall = now;
                Snapshot();

                return;
            }

            _nextReport = now + 60f;

            long ticks = gameTick - _lastGameTick;
            _lastGameTick = gameTick;

            // 标定：这一窗口里，计数器实际走了多少、墙钟走了多少秒。
            long stampNow = Stopwatch.GetTimestamp();
            double wall = now - _lastWall;
            double measuredFreq = wall > 0.001 ? (stampNow - _lastStamp) / wall : Stopwatch.Frequency;

            _lastStamp = stampNow;
            _lastWall = now;

            if (measuredFreq <= 0.0) measuredFreq = Stopwatch.Frequency;

            // 差得离谱就单独吼一声，把两个数都摆出来——沉默地用一个错的分母，
            // 正是上一版报出 240 ms/帧 的原因。
            double ratio = measuredFreq / Stopwatch.Frequency;

            if ((ratio > 1.05 || ratio < 0.95) && Interlocked.Exchange(ref _warnedFreq, 1) == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    $"巨型建筑·分段耗时：Stopwatch.Frequency 自报 {Stopwatch.Frequency:N0} Hz，"
                    + $"实测 {measuredFreq:N0} Hz（差 {ratio:0.##} 倍）。"
                    + "下面的毫秒数按**实测值**换算——信自报值会让耗时整体放大同样的倍数。"
                    + "整局只说这一行。");

            long c = Interlocked.Read(ref _tCycles) - _lc;
            long st = Interlocked.Read(ref _tStorage) - _lst;
            long sl = Interlocked.Read(ref _tSlots) - _lsl;
            long o = Interlocked.Read(ref _tOther) - _lo;
            long b = Interlocked.Read(ref _buildings) - _lb;

            Snapshot();

            long total = c + st + sl + o;

            // 一次都没 tick 到 = 这颗存档里没有巨型建筑。**照样报一行**，
            // 否则「没有巨型建筑」和「计时坏了」在日志里长得一样。
            if (total <= 0L || ticks <= 0L)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"巨型建筑·分段耗时：过去 60 秒没有采到样本（逻辑帧 {ticks}、建筑次数 {b}）"
                    + "——这颗存档里没有巨型建筑，或者它们全部断电停摆。");

                return;
            }

            double msPerFrame = 1000.0 / measuredFreq / ticks;

            // **自检：累计耗时不可能超过墙钟 × 线程数。** 超了就说明分母还是错的，
            // 或者这些建筑真的散在很多星球上并行跑——两种情况的结论完全不同，
            // 所以把"折合几条线程"直接印出来，而不是让人从毫秒数里去猜。
            double busyThreads = wall > 0.001 ? total / measuredFreq / wall : 0.0;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑·分段耗时（{ticks} 个逻辑帧、{b / (double)ticks:0} 台/帧、"
                + $"折合 {busyThreads:0.##} 条线程满载）："
                + $"配方周期 {c * msPerFrame:0.###} ms（{100.0 * c / total:0.#}%）、"
                + $"储物格同步 {st * msPerFrame:0.###} ms（{100.0 * st / total:0.#}%）、"
                + $"传送带槽位 {sl * msPerFrame:0.###} ms（{100.0 * sl / total:0.#}%）、"
                + $"其余 {o * msPerFrame:0.###} ms（{100.0 * o / total:0.#}%），"
                + $"合计 {total * msPerFrame:0.###} ms/帧。"
                + "**拿它和性能面板的「生产设施」对着看**：那一栏还包含采矿机和研究站，"
                + "所以合计必然小于它；差额就是这两者。"
                + "（如果合计反而**大于**面板那一栏，先看上面那行「折合几条线程」："
                + "接近 1 说明分母还是错的，明显大于 1 说明这些建筑散在多颗星球上并行跑，"
                + "那时候合计是 CPU 时间而不是单帧墙钟时间。）"
                + "配方周期占大头 → 只剩批量结算这条路（风险高）；"
                + "储物格同步占大头 → 加脏标记就行（风险低）。");
        }

        private static void Snapshot()
        {
            _lc = Interlocked.Read(ref _tCycles);
            _lst = Interlocked.Read(ref _tStorage);
            _lsl = Interlocked.Read(ref _tSlots);
            _lo = Interlocked.Read(ref _tOther);
            _lb = Interlocked.Read(ref _buildings);
        }

        /// <summary>
        /// 开机状态行。<b>开和关两种情况都要打</b>——只在开着时才说话，会让
        /// 「没开」和「这段代码根本没进 DLL」长得一模一样，这条规矩本仓库付过三次钱。
        /// </summary>
        internal static void Report()
        {
            if (!Enabled)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "巨型建筑·分段耗时：未启用（megabuildings.json 的 phaseTiming 为 false）。"
                    + "打开它可以把性能面板「生产设施」那一栏里，原版配方结算和本 mod 自己的"
                    + "每建筑每 tick 开销分开。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "巨型建筑·分段耗时：已启用，每 60 秒报一行，把 MegaTick 拆成"
                + "「配方周期 / 储物格同步 / 传送带槽位 / 其余」四段。"
                + $"计时用 Stopwatch（频率 {Stopwatch.Frequency:N0} Hz），"
                + "每台每 tick 多读四次时间戳，开销在 1% 以内——**定了方向之后把它关掉**。");
        }
    }
}
