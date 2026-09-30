using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches.Diagnostics
{
    // 在调用方夹住真实方法（含其他mod前后置），不改变参数、调用顺序或运输状态。
    [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
    internal static class StationLoopProbe
    {
        private static readonly string[] Methods = { "InternalTickLocal", "InternalTickRemote", "UpdateCollection", "UpdateVeinCollection", "SetPCState" };
        private static readonly string[] Names = { "本地运输（含充能及方法补丁）", "星际运输（含方法补丁）", "气态采集", "矿物采集", "耗电状态设置" };
        private sealed class Sample
        {
            internal uint Random = unchecked((uint)Thread.CurrentThread.ManagedThreadId * 747796405u + 2891336453u);
            internal int Phase = -1;
            internal long At, LoopAt, LoopTicks;
            internal readonly long[] Ticks = new long[5], Counts = new long[5], Max = new long[5];
        }
        internal struct Scope { internal bool Entered, Previous; }
        [ThreadStatic] private static Sample _sample;
        [ThreadStatic] private static bool _active;
        [ThreadStatic] private static int _depth;
        private static bool _installed;
        private static readonly object Gate = new object();
        // 0轮次、1循环耗时、2异常，其后每类依次为耗时/次数/最大耗时。
        private static readonly long[] Totals = new long[18];

        [HarmonyPrefix]
        internal static void Begin(out Scope __state)
        {
            __state = default(Scope);
            if (!TransportSplitProbe.Armed || !_installed) return;
            __state = new Scope { Entered = true, Previous = _active };
            _active = false;
            if (++_depth != 1) return; // 嵌套只计入外层方法，不重复记账。
            var s = _sample ?? (_sample = new Sample());
            uint x = s.Random; x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            s.Random = x == 0 ? 1u : x;
            if ((x & 63) != 0) return;
            _active = true;
            s.Phase = -1; s.At = s.LoopAt = s.LoopTicks = 0;
            Array.Clear(s.Ticks, 0, 5); Array.Clear(s.Counts, 0, 5); Array.Clear(s.Max, 0, 5);
        }

        internal static void LoopStart() => _sample.LoopAt = Stopwatch.GetTimestamp();
        internal static void LoopEnd()
        {
            var s = _sample;
            if (s.LoopAt == 0) return;
            s.LoopTicks = Stopwatch.GetTimestamp() - s.LoopAt;
            s.LoopAt = 0;
        }
        internal static void Start(int phase)
        {
            _sample.Phase = phase;
            _sample.At = Stopwatch.GetTimestamp();
        }
        internal static void End()
        {
            var s = _sample;
            if (s.Phase < 0) return;
            long elapsed = Stopwatch.GetTimestamp() - s.At;
            int phase = s.Phase;
            s.Ticks[phase] += elapsed; s.Counts[phase]++;
            if (elapsed > s.Max[phase]) s.Max[phase] = elapsed;
            s.Phase = -1;
        }
        [HarmonyFinalizer]
        internal static void Finish(Exception __exception, Scope __state)
        {
            if (!__state.Entered) return;
            if (_depth == 1 && _active)
            {
                End(); LoopEnd();
                var s = _sample;
                lock (Gate)
                {
                    Totals[0]++; Totals[1] += s.LoopTicks;
                    if (__exception != null) Totals[2]++;
                    for (int i = 0; i < 5; i++)
                    {
                        Totals[3 + i * 3] += s.Ticks[i];
                        Totals[4 + i * 3] += s.Counts[i];
                        Totals[5 + i * 3] = Math.Max(Totals[5 + i * 3], s.Max[i]);
                    }
                }
            }
            _depth--; _active = __state.Previous;
        }

        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codes = new List<CodeInstruction>(instructions);
            var targets = new MethodInfo[5];
            for (int i = 0; i < 5; i++) targets[i] = AccessTools.Method(typeof(StationComponent), Methods[i]);
            var sandbox = AccessTools.Method(typeof(PlanetTransport), "GameTick_SandboxMode");
            var needs = AccessTools.Method(typeof(PlanetTransport), "GameTick_UpdateNeeds");
            int[] counts = new int[7];
            foreach (var c in codes)
            {
                for (int i = 0; i < 5; i++) if (targets[i] != null && c.Calls(targets[i])) counts[i]++;
                if (c.Calls(sandbox)) counts[5]++;
                if (c.Calls(needs)) counts[6]++;
            }
            _installed = Array.TrueForAll(counts, n => n == 1);
            if (!_installed)
            {
                ProjectEdenPlugin.Log.LogWarning("物流站循环细分：调用结构不匹配，保留原方法并关闭本探针。");
                return codes;
            }
            var result = new List<CodeInstruction>();
            Action<string, int> guard = (method, phase) =>
            {
                Label skip = generator.DefineLabel();
                result.Add(new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(StationLoopProbe), nameof(_active))));
                result.Add(new CodeInstruction(OpCodes.Brfalse, skip));
                if (phase >= 0) result.Add(new CodeInstruction(OpCodes.Ldc_I4, phase));
                result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(StationLoopProbe), method)));
                var nop = new CodeInstruction(OpCodes.Nop); nop.labels.Add(skip); result.Add(nop);
            };
            foreach (var c in codes)
            {
                int phase = Array.FindIndex(targets, t => c.Calls(t));
                if (phase >= 0 || c.Calls(needs))
                {
                    int begin = result.Count;
                    guard(phase >= 0 ? nameof(Start) : nameof(LoopEnd), phase);
                    result[begin].labels.AddRange(c.labels); c.labels.Clear();
                    result[begin].blocks.AddRange(c.blocks); c.blocks.Clear();
                }
                result.Add(c);
                if (phase >= 0) guard(nameof(End), -1);
                if (c.Calls(sandbox)) guard(nameof(LoopStart), -1);
            }
            ProjectEdenPlugin.Log.LogInfo("物流站循环细分：约1/64轮次计时本地/星际/气态采集/矿物采集/耗电设置；未抽中的站点仅检查标志，不读取计时器。");
            return result;
        }
        internal static long[] Take()
        {
            lock (Gate) { var t = (long[])Totals.Clone(); Array.Clear(Totals, 0, Totals.Length); return t; }
        }
        internal static void Report()
        {
            var t = Take();
            double ns = 1000000000.0 / Stopwatch.Frequency;
            long classified = 0;
            var sb = new System.Text.StringBuilder($"[物流站循环细分] 约1/64整轮随机采样；样本轮次={t[0]} 异常轮次={t[2]} 循环累计ns={t[1] * ns:0}；含方法补丁和计时开销，不乘64外推，不与全量本体分段相加。充能包含在本地运输内，耗电状态设置不是充能结算。");
            for (int i = 0; i < 5; i++)
            {
                long ticks = t[3 + i * 3], count = t[4 + i * 3]; classified += ticks;
                sb.Append($"\n  {Names[i]}：次数={count} 累计ns={ticks * ns:0} 均值ns={(count > 0 ? ticks * ns / count : 0):0} 最大ns={t[5 + i * 3] * ns:0} 占循环={(t[1] > 0 ? ticks * 100.0 / t[1] : 0):0.0}%");
            }
            sb.Append($"\n  遍历/参数读取/动画/统计及计时边界：累计ns={(t[1] - classified) * ns:0} 占循环={(t[1] > 0 ? (t[1] - classified) * 100.0 / t[1] : 0):0.0}%");
            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }
    }
}
