using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches.Diagnostics
{
    // 随机抽取约1/64个星球刷新轮次，避免固定周期与星球分工锁相。
    // 仅保存线程私有数值；每轮结束一次汇总，不在逐站热路径争抢共享原子计数。
    [HarmonyPatch(typeof(PlanetTransport), "GameTick_UpdateNeeds")]
    internal static class NeedsRefreshProbe
    {
        private sealed class Sample
        {
            internal uint Random = unchecked((uint)Thread.CurrentThread.ManagedThreadId * 747796405u + 2891336453u);
            internal int Depth, Phase;
            internal bool Active;
            internal long Start, At, StationTicks, Stations, Expanded, Slots, Visited, Rebuilt;
            internal readonly long[] Parts = new long[3];
        }
        [ThreadStatic] private static Sample _sample;
        private static readonly object Gate = new object();
        // 轮次、所有站调用、扩容站调用、扩容格位、总耗时、站调用耗时、锁等待、扫描、填充、异常轮次。
        private static readonly long[] Totals = new long[12];
        private static bool _installed;
        internal static bool Active => _sample != null && _sample.Active && _sample.Depth == 1;

        [HarmonyPrefix]
        internal static void Begin(out bool __state)
        {
            __state = false;
            if (!TransportSplitProbe.Armed || !_installed) return;
            __state = true;
            var s = _sample ?? (_sample = new Sample());
            if (s.Depth++ != 0) return;
            uint x = s.Random; x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            s.Random = x == 0 ? 1u : x;
            s.Active = (x & 63) == 0;
            if (!s.Active) return;
            Array.Clear(s.Parts, 0, s.Parts.Length);
            s.StationTicks = s.Stations = s.Expanded = s.Slots = s.Visited = s.Rebuilt = 0;
            s.Start = Stopwatch.GetTimestamp();
        }

        [HarmonyFinalizer]
        internal static void Finish(Exception __exception, bool __state)
        {
            if (!__state) return;
            var s = _sample;
            if (s == null || s.Depth == 0 || --s.Depth != 0) return;
            if (!s.Active) return;
            long elapsed = Stopwatch.GetTimestamp() - s.Start;
            s.Active = false;
            lock (Gate)
            {
                Totals[0]++; Totals[1] += s.Stations; Totals[2] += s.Expanded; Totals[3] += s.Slots;
                Totals[10] += s.Visited; Totals[11] += s.Rebuilt;
                Totals[4] += elapsed; Totals[5] += s.StationTicks;
                for (int i = 0; i < 3; i++) Totals[6 + i] += s.Parts[i];
                if (__exception != null) Totals[9]++;
            }
        }

        internal static void UpdateStation(StationComponent station)
        {
            if (!Active) { station.UpdateNeeds(); return; }
            var s = _sample;
            long at = Stopwatch.GetTimestamp();
            try { station.UpdateNeeds(); }
            finally { s.StationTicks += Stopwatch.GetTimestamp() - at; s.Stations++; }
        }

        internal static void SlotsVisited(int count, bool rebuilt)
        {
            _sample.Visited += count;
            if (rebuilt) _sample.Rebuilt++;
        }

        internal static void BeforeLock(int slots)
        {
            var s = _sample;
            s.Expanded++; s.Slots += slots; s.Phase = 0; s.At = Stopwatch.GetTimestamp();
        }
        internal static void LockAcquired() => Advance(1);
        internal static void ScanFinished() { if (Active) Advance(2); }
        internal static void ExpandedFinished() => Advance(3);
        private static void Advance(int next)
        {
            var s = _sample;
            long now = Stopwatch.GetTimestamp();
            s.Parts[s.Phase] += now - s.At;
            s.At = now; s.Phase = next;
        }

        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var original = AccessTools.Method(typeof(StationComponent), nameof(StationComponent.UpdateNeeds));
            int count = 0;
            foreach (var code in codes) if (code.Calls(original)) count++;
            _installed = count == 1;
            if (!_installed)
            {
                ProjectEdenPlugin.Log.LogWarning("需求刷新细分：站点调用结构不匹配，保留原方法并关闭细分采样。");
                return codes;
            }
            foreach (var code in codes)
                if (code.Calls(original))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(NeedsRefreshProbe), nameof(UpdateStation));
                }
            ProjectEdenPlugin.Log.LogInfo("需求刷新细分：已接入约1/64轮次采样，锁等待/扫描/填充/站点其余/遍历分别计时。");
            return codes;
        }

        internal static long[] Take()
        {
            lock (Gate) { var result = (long[])Totals.Clone(); Array.Clear(Totals, 0, Totals.Length); return result; }
        }
        internal static void Report()
        {
            var t = Take();
            double ns = 1000000000.0 / Stopwatch.Frequency;
            var sb = new System.Text.StringBuilder($"[需求刷新细分] 约1/64单元随机采样（串行为整轮，并行为至多256站批次）；样本轮次={t[0]} 站调用={t[1]} 扩容站={t[2]} 扩容格位={t[3]} 实际扫描格位={t[10]} 布局重建={t[11]} 异常轮次={t[9]}；仅样本实测，不乘64外推，不与全量本体分段相加。时钟读数含探针开销，ns为换算单位而非纳秒精度。");
            string[] names = { "库存锁获取（含等待）", "库存扫描及曲速器准备", "白名单轮换/填充及解锁", "站点其余（普通站/其他补丁/计时边界）", "遍历及实体需求挂接（含调用边界开销）" };
            long[] values = { t[6], t[7], t[8], t[5] - t[6] - t[7] - t[8], t[4] - t[5] };
            sb.Append($"\n  样本总计ns={t[4] * ns:0}");
            for (int i = 0; i < values.Length; i++)
                sb.Append($"\n  {names[i]}：累计ns={values[i] * ns:0} 占样本={(t[4] > 0 ? values[i] * 100.0 / t[4] : 0):0.0}%");
            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }
    }
}
