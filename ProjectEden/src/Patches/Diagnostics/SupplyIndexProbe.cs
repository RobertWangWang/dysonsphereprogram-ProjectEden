using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace ProjectEden.Patches.Diagnostics
{
    // 每轮只包围Supply和BuildIndex，整轮结束一次提交，保证同一报告窗口内分段可比。
    [HarmonyPatch]
    internal static class SupplyIndexProbe
    {
        internal struct Frame { internal bool Active; internal int Kind; internal long IndexTicks, IndexCalls; }
        internal struct Scope { internal bool Total, Entered; internal long Start; internal Frame Previous; }
        [ThreadStatic] private static Frame _frame;
        private static readonly object Gate = new object();
        // 每类依次：轮次、总耗时、建索引次数、建索引耗时、异常轮次。
        private static readonly long[] Totals = new long[10];
        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (var type in new[] { typeof(Fusion.FusionFuelLogisticsPatches), typeof(MegaExchangerLogisticsPatches) })
            {
                yield return AccessTools.Method(type, "Supply");
                yield return AccessTools.Method(type, "BuildIndex");
            }
        }
        [HarmonyPrefix]
        internal static void Before(MethodBase __originalMethod, out Scope __state)
        {
            int kind = __originalMethod.DeclaringType == typeof(Fusion.FusionFuelLogisticsPatches) ? 0 : 1;
            bool total = __originalMethod.Name == "Supply";
            __state = new Scope { Total = total };
            if (total)
            {
                __state.Previous = _frame;
                _frame = new Frame { Active = TransportSplitProbe.Armed, Kind = kind };
            }
            if (!_frame.Active || _frame.Kind != kind) return;
            __state.Entered = true;
            __state.Start = Stopwatch.GetTimestamp();
        }
        [HarmonyFinalizer]
        internal static void After(Exception __exception, Scope __state)
        {
            long elapsed = __state.Entered ? Stopwatch.GetTimestamp() - __state.Start : 0;
            if (!__state.Total)
            {
                if (__state.Entered) { _frame.IndexCalls++; _frame.IndexTicks += elapsed; }
                return;
            }
            var frame = _frame;
            _frame = __state.Previous;
            if (!__state.Entered) return;
            lock (Gate)
            {
                int i = frame.Kind * 5;
                Totals[i]++; Totals[i + 1] += elapsed;
                Totals[i + 2] += frame.IndexCalls; Totals[i + 3] += frame.IndexTicks;
                if (__exception != null) Totals[i + 4]++;
            }
        }
        internal static long[] Take()
        {
            lock (Gate) { var result = (long[])Totals.Clone(); Array.Clear(Totals, 0, Totals.Length); return result; }
        }
        internal static void Report()
        {
            var t = Take(); double ns = 1000000000.0 / Stopwatch.Frequency;
            for (int kind = 0; kind < 2; kind++)
            {
                int i = kind * 5; string name = kind == 0 ? "聚变燃料" : "储能柜";
                ProjectEdenPlugin.Log.LogInfo($"[供需索引分段] {name} 轮次={t[i]} 建索引次数={t[i + 2]} 总计ns={t[i + 1] * ns:0} 建索引ns={t[i + 3] * ns:0} 建索引占比={(t[i + 1] > 0 ? t[i + 3] * 100.0 / t[i + 1] : 0):0.0}% 单次建索引ns={(t[i + 2] > 0 ? t[i + 3] * ns / t[i + 2] : 0):0} 搬运及其余ns={(t[i + 1] - t[i + 3]) * ns:0} 异常轮次={t[i + 4]}；全量线程累计，含库存锁等待/补丁/计时开销，轮次包括无需补给的早退；不可与物流总项相加，不能当作帧时。");
            }
        }
    }
}
