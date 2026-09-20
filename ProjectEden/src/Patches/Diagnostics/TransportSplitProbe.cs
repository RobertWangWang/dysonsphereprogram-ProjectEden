using System;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches.Diagnostics
{
    /// <summary>
    /// 把「物流运输」那一栏劈成两半：**原版的 <c>PlanetTransport.GameTick</c> 本体**，
    /// 和**本 mod 挂在它后面的那六个后置**。
    ///
    /// <para><b>为什么非劈不可。</b></para>
    /// 逐任务表里 <c>FactoryTransport</c>（物流运输）排第一，而那个任务执行的就是
    /// <c>PlanetTransport.GameTick</c>。本 mod 往那个方法上挂了**六个**后置：
    /// <c>LabLogisticSupplyPatches</c>、<c>MegaVirtualLogisticsPatches</c>、
    /// <c>GasCollectorPatches</c>、<c>HubCourierPatches</c>、<c>LogisticsGlobalPatches</c>、
    /// <c>StationCapacityPatches</c>。前两个每 tick 全量扫这颗星球的站点和储物格，
    /// 而这颗星球有 6397 个站、其中 4632 个是 30 格的巨型建筑。
    ///
    /// 所以那 8 ms 里有多少是原版的 <c>InternalTickLocal × 6397</c>、有多少是我们自己的，
    /// **原版的分析器结构上分不出来**——它按任务计时，而我们的代码就在那个任务里面。
    ///
    /// <para><b>做法：用优先级把六个后置夹住。</b></para>
    /// Harmony 的后置按优先级从高到低跑，所以
    /// <c>Priority.First</c> 的后置排在那六个**之前**、<c>Priority.Last</c> 的排在**之后**。
    /// 于是：
    /// <list type="bullet">
    /// <item><c>中 − 前</c> ＝ 原版本体（前缀在 <c>Priority.First</c>，先于一切）</item>
    /// <item><c>后 − 中</c> ＝ 本 mod 那六个后置的合计</item>
    /// </list>
    /// **一行现有代码都不用改**，六个补丁类原样不动。
    ///
    /// <para><b>两个坑，都是本文件记过的。</b></para>
    /// <list type="number">
    /// <item><b>这条路是并行的。</b> <c>PlanetTransport.GameTick</c> 跑在
    /// <c>GameLogic.FactoryTransportGameTick_Parallel</c> 上，约 31 个工作线程各管一颗星球。
    /// 所以时间戳必须 <c>[ThreadStatic]</c>，累加必须 <c>Interlocked</c>。</item>
    /// <item><b><c>Stopwatch.Frequency</c> 是自报的，自报的数只是主张。</b>
    /// <c>MegaTickProfiler</c> 当年直接除它，报出过 12 倍的假数。这里**按墙钟标定**：
    /// 同时累计窗口的真实秒数，算出来的毫秒数如果超过「窗口 × 帧数」这个物理上界就报警。</item>
    /// </list>
    /// </summary>
    [HarmonyPatch]
    internal static class TransportSplitProbe
    {
        [ThreadStatic] private static long _t0;
        [ThreadStatic] private static long _tMid;

        private static long _vanillaTicks;
        private static long _oursTicks;
        private static long _calls;

        private static float _windowStart;
        private static int _entered;

        private static bool Armed => CpuCostProbe.Config != null && CpuCostProbe.Config.enabled;

        /// <summary>
        /// <c>Priority.First</c>：在原版本体之前。前缀里只取一个时间戳，
        /// 刻意不做任何分支——这条路每颗星球每 tick 都会走一遍。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void Before()
        {
            if (!Armed) return;

            _t0 = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// <c>Priority.First</c> 的后置＝**六个后置里最先跑的那个**，所以这里截到的是
        /// 原版本体的终点。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void Mid()
        {
            if (!Armed) return;

            _tMid = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// <c>Priority.Last</c> 的后置＝最后一个跑的，所以 <c>后 − 中</c> 就是
        /// 本 mod 那六个后置的合计。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void After()
        {
            if (!Armed) return;

            if (_t0 == 0 || _tMid == 0) return;

            long now = Stopwatch.GetTimestamp();

            Interlocked.Add(ref _vanillaTicks, _tMid - _t0);
            Interlocked.Add(ref _oursTicks, now - _tMid);
            Interlocked.Increment(ref _calls);

            _t0 = 0;
            _tMid = 0;
        }

        /// <summary>
        /// 由 <see cref="CpuCostProbe"/> 在报表的同一刻调用，好让两张表说的是同一段时间。
        /// </summary>
        internal static void ReportWindow()
        {
            if (!Armed) return;

            float now = UnityEngine.Time.realtimeSinceStartup;

            if (Interlocked.Exchange(ref _entered, 1) == 0)
            {
                _windowStart = now;

                ProjectEdenPlugin.Log.LogInfo(
                    "物流运输·劈半：哨兵已就位（Priority.First 的前置＋后置夹住原版本体，"
                    + "Priority.Last 的后置收尾），下次报表起给出「原版 : 本 mod」的比例。");

                return;
            }

            long vanilla = Interlocked.Exchange(ref _vanillaTicks, 0);
            long ours = Interlocked.Exchange(ref _oursTicks, 0);
            long calls = Interlocked.Exchange(ref _calls, 0);

            float window = now - _windowStart;

            _windowStart = now;

            if (calls == 0)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "物流运输·劈半：这一窗口里 PlanetTransport.GameTick 一次都没跑完"
                    + "——哨兵没夹住（优先级被别的 mod 顶了？），本次不给数");

                return;
            }

            double freq = Stopwatch.Frequency;
            double vanillaMs = vanilla / freq * 1000.0;
            double oursMs = ours / freq * 1000.0;

            // **按墙钟标定，别信 Stopwatch.Frequency 的自报。**
            // 这条路是并行的，所以合计可以超过墙钟（多个线程同时在跑）——上界是
            // 「窗口秒数 × 线程数」，而线程数我们不知道，所以只报「相当于几个线程满载」，
            // 明显离谱时自己说出来，而不是把一个 12 倍的假数当结论。
            double total = vanillaMs + oursMs;
            double threads = window > 0 ? total / 1000.0 / window : 0;

            var warn = threads > 64
                ? "　⚠ **相当于 64 个以上线程满载，这个数不可信**——多半是 Stopwatch.Frequency 自报的值不对"
                : "";

            ProjectEdenPlugin.Log.LogInfo(
                $"物流运输·劈半（过去 {window:0} 秒，{calls} 次星球 tick）："
                + $"原版本体 {vanillaMs:0} ms　本 mod 的六个后置 {oursMs:0} ms"
                + $"　＝ {(total > 0 ? ours / (double)(vanilla + ours) * 100.0 : 0):0.0}% 是我们的。"
                + $"　折算相当于 {threads:0.0} 个线程满载{warn}"
                + "\n  这六个是 LabLogisticSupplyPatches、MegaVirtualLogisticsPatches、GasCollectorPatches、"
                + "HubCourierPatches、LogisticsGlobalPatches、StationCapacityPatches。"
                + "**比例高就说明该削的是我们自己的代码，低就说明是原版 InternalTickLocal × 站点数**，"
                + "那两条路要动的东西完全不同。");
        }
    }
}
