using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches.Diagnostics
{
    // 只在星球级阶段边界取时间，不给每座站、每架无人机增加计时钩子。
    [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
    internal static class TransportBodyProbe
    {
        private static readonly long[] Ticks = new long[5], Counts = new long[5], Max = new long[5];
        private static readonly string[] Names = { "本体准备及沙盒", "物流站循环（本地/星际/采集/充电）", "物流需求刷新", "配送器及其余本体", "物流全局数值前置" };
        [ThreadStatic] private static long _at;
        [ThreadStatic] private static int _phase;

        internal static long Start() => TransportSplitProbe.Armed ? Stopwatch.GetTimestamp() : 0;
        internal static void Record(int phase, long start)
        {
            if (start == 0) return;
            long elapsed = Stopwatch.GetTimestamp() - start;
            Interlocked.Add(ref Ticks[phase], elapsed);
            Interlocked.Increment(ref Counts[phase]);
            long old;
            do { old = Interlocked.Read(ref Max[phase]); if (old >= elapsed) break; }
            while (Interlocked.CompareExchange(ref Max[phase], elapsed, old) != old);
        }
        internal static void Mark(int next)
        {
            if (next == 0) { _phase = 0; _at = Start(); return; }
            Record(_phase, _at);
            _phase = next;
            _at = next < 4 ? Start() : 0;
        }
        [HarmonyFinalizer]
        private static void Finish()
        {
            // 异常中断时也清除线程状态，避免污染下一个星球。
            if (_at != 0) { Record(_phase, _at); _at = 0; }
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            MethodInfo sandbox = AccessTools.Method(typeof(PlanetTransport), "GameTick_SandboxMode");
            MethodInfo needs = AccessTools.Method(typeof(PlanetTransport), "GameTick_UpdateNeeds");
            int sandboxCount = 0, needsCount = 0;
            foreach (var c in codes) { if (c.Calls(sandbox)) sandboxCount++; if (c.Calls(needs)) needsCount++; }
            if (sandboxCount != 1 || needsCount != 1)
            {
                ProjectEdenPlugin.Log.LogWarning("物流本体分段：入口结构不匹配，保留原方法，不注入计时。");
                return codes;
            }
            var result = new List<CodeInstruction>();
            Action<int> mark = n => { result.Add(new CodeInstruction(OpCodes.Ldc_I4, n)); result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TransportBodyProbe), nameof(Mark)))); };
            mark(0);
            foreach (var c in codes)
            {
                if (c.Calls(needs) || c.opcode == OpCodes.Ret)
                {
                    int index = result.Count;
                    mark(c.opcode == OpCodes.Ret ? 4 : 2);
                    // 分支直达 ret 时必须先经过收尾标记。
                    result[index].labels.AddRange(c.labels); c.labels.Clear();
                }
                result.Add(c);
                if (c.Calls(sandbox)) mark(1);
                if (c.Calls(needs)) mark(3);
            }
            ProjectEdenPlugin.Log.LogInfo("物流本体分段：已接入准备/物流站循环/需求刷新/配送器边界；探针关闭时不取时间。");
            return result;
        }

        internal static void Report(long logicTicks)
        {
            var sb = new System.Text.StringBuilder("[物流本体分段] 所有星球线程累计；站点循环包含本地/星际物流及其方法补丁，非主线程帧时。");
            for (int i = 0; i < Ticks.Length; i++)
            {
                long ticks = Interlocked.Exchange(ref Ticks[i], 0), count = Interlocked.Exchange(ref Counts[i], 0), max = Interlocked.Exchange(ref Max[i], 0);
                double ms = ticks * 1000.0 / Stopwatch.Frequency;
                sb.Append($"\n  {Names[i]}：调用={count} 累计={ms:0.###}ms 最大ns={NanosecondProbe.Ns(max)} 每逻辑tick累计={(logicTicks > 0 ? (ms / logicTicks).ToString("0.###") : "不可用")}ms");
            }
            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }
    }
}
