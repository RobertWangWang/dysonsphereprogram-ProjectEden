using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace ProjectEden.Patches.Diagnostics
{
    // 高频路径只抽样，不逐次写日志；所有时间戳来自同一个 Stopwatch 时钟。
    internal static class NanosecondProbe
    {
        internal static bool Enabled => CpuCostProbe.Config?.nanoTiming == true;
        internal static long Ns(long ticks) => (long)(ticks * (1000000000.0 / Stopwatch.Frequency));
        private static readonly object Sync = new object();
        private static readonly string[] Names = { "配方结算（含单次更新）", "单次配方更新（结算子集）", "传送带槽位", "储物格同步", "其他生产逻辑", "工厂生产总计", "矩阵生产", "矩阵科研", "并行采矿", "并行组装", "并行分馏", "并行弹射", "并行发射井", "并行矩阵生产" };
        internal static readonly string[] ParallelMethods = { "_miner_parallel", "_assembler_parallel", "_fractionator_parallel", "_ejector_parallel", "_silo_parallel", "_lab_produce_parallel" };
        private static readonly long[] Counts = new long[14], Ticks = new long[14], Max = new long[14];
        private static readonly Queue<string> Events = new Queue<string>();
        private static int _dropped;
        private static long _window;
        [ThreadStatic] private static uint _sequence;
        [ThreadStatic] private static bool _sample;
        [ThreadStatic] private static int _planet, _entity, _recipe;

        internal static void Begin(int planet, int entity, int recipe)
        {
            _sample = Enabled && (++_sequence % 64 == 0);
            _planet = planet; _entity = entity; _recipe = recipe;
        }
        internal static long Now() => _sample && Enabled ? Stopwatch.GetTimestamp() : 0;
        internal static void End(int category, long start)
        {
            if (start == 0) return;
            long end = Stopwatch.GetTimestamp();
            Record(category, end - start);
            if (Ns(end - start) >= 100000000)
                Event($"生产慢调用 分类={Names[category]} 星球={_planet} 建筑={_entity} 配方={_recipe}", start, end);
        }
        internal static void Record(int category, long ticks)
        {
            lock (Sync) { Counts[category]++; Ticks[category] += ticks; Max[category] = Math.Max(Max[category], ticks); }
        }
        internal static void Event(string name, long start, long end, bool immediate = false)
        {
            // 存档结束可能再也没有 UI 帧，低频事件直接写入，避免退出时丢失。
            if (immediate)
            {
                ProjectEdenPlugin.Log.LogInfo($"[停顿事件] {name} 起点ns={Ns(start)} 终点ns={Ns(end)} 耗时ns={Ns(end-start)}");
                return;
            }
            lock (Sync)
            {
                if (Events.Count >= 64) { _dropped++; return; }
                Events.Enqueue($"[停顿事件] {name} 起点ns={Ns(start)} 终点ns={Ns(end)} 耗时ns={Ns(end-start)} 线程={System.Threading.Thread.CurrentThread.ManagedThreadId}");
            }
        }
        internal static void Flush(long now)
        {
            if (!Enabled) return;
            string[] events;
            string report = null;
            lock (Sync)
            {
                events = Events.ToArray(); Events.Clear();
                if (_window == 0) _window = now;
                if ((now - _window) / (double)Stopwatch.Frequency >= Math.Max(5, CpuCostProbe.Config.perfProbeSeconds))
                {
                    var sb = new StringBuilder($"[纳秒生产探针] 窗口ns={Ns(now-_window)}；前五项为每线程每64台次抽1次的实测样本，其余项为全量方法调用（并行方法包括等待/调度）；嵌套和并行耗时不能相加，不外推整帧。事件丢弃={_dropped}");
                    for (int i = 0; i < Counts.Length; i++)
                    {
                        sb.Append($"\n  {Names[i]} 次数={Counts[i]} 总计ns={Ns(Ticks[i])} 均值ns={(Counts[i] == 0 ? 0 : Ns(Ticks[i])/Counts[i])} 最大ns={Ns(Max[i])}");
                        Counts[i] = Ticks[i] = Max[i] = 0;
                    }
                    report = sb.ToString(); _window = now; _dropped = 0;
                }
            }
            foreach (string line in events) ProjectEdenPlugin.Log.LogInfo(line);
            if (report != null) ProjectEdenPlugin.Log.LogInfo(report);
        }
    }

    [HarmonyPatch]
    internal static class ProductionEventProbe
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(FactorySystem), "GameTick", new[] { typeof(long), typeof(bool) });
            yield return AccessTools.Method(typeof(FactorySystem), "GameTickLabProduceMode", new[] { typeof(long), typeof(bool) });
            yield return AccessTools.Method(typeof(FactorySystem), "GameTickLabResearchMode", new[] { typeof(long), typeof(bool) });
            foreach (string name in NanosecondProbe.ParallelMethods)
                yield return AccessTools.Method(typeof(GameLogic), name, new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
            yield return AccessTools.Method(typeof(GameSave), "SaveCurrentGame", new[] { typeof(string) });
            yield return AccessTools.Method(typeof(GameSave), "AutoSave", Type.EmptyTypes);
            yield return AccessTools.Method(typeof(BuildTool_BlueprintPaste), "CheckBuildConditions", Type.EmptyTypes);
            yield return AccessTools.Method(typeof(BuildTool_BlueprintPaste), "CreatePrebuilds", Type.EmptyTypes);
        }
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Before(MethodBase __originalMethod, out long __state)
        {
            __state = NanosecondProbe.Enabled ? Stopwatch.GetTimestamp() : 0;
            if (__state != 0 && __originalMethod.DeclaringType != typeof(FactorySystem) && __originalMethod.DeclaringType != typeof(GameLogic))
                ProjectEdenPlugin.Log.LogInfo($"[停顿事件] 开始 {__originalMethod.DeclaringType.Name}.{__originalMethod.Name} 时间ns={NanosecondProbe.Ns(__state)}");
        }
        // Finalizer 也覆盖异常路径；不吞掉或替换游戏异常。
        [HarmonyFinalizer]
        private static void After(MethodBase __originalMethod, object __instance, long __state, Exception __exception)
        {
            if (__state == 0) return;
            long end = Stopwatch.GetTimestamp();
            var factory = __instance as FactorySystem;
            if (factory != null)
            {
                int category = __originalMethod.Name == "GameTick" ? 5 : __originalMethod.Name == "GameTickLabProduceMode" ? 6 : 7;
                NanosecondProbe.Record(category, end-__state);
            }
            if (__originalMethod.DeclaringType == typeof(GameLogic))
                NanosecondProbe.Record(8 + Array.IndexOf(NanosecondProbe.ParallelMethods, __originalMethod.Name), end-__state);
            if ((factory == null && __originalMethod.DeclaringType != typeof(GameLogic)) || NanosecondProbe.Ns(end-__state) >= 100000000 || __exception != null)
                NanosecondProbe.Event($"{__originalMethod.DeclaringType.Name}.{__originalMethod.Name} 星球={factory?.factory?.planetId ?? 0} 异常={__exception?.GetType().Name ?? "无"}", __state, end, __originalMethod.DeclaringType == typeof(GameSave));
        }
    }
}
