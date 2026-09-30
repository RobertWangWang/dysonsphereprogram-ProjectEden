using System;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using ProjectEden.Patches.Diagnostics;

namespace ProjectEden.Patches
{
    // 只调整原版任务窃取参数；任务领取、库存、统计和阶段屏障仍由原版负责。
    [HarmonyPatch(typeof(GameLogic), "_assembler_parallel")]
    internal static class ProductionSchedulingPatches
    {
        internal struct Scope
        {
            internal ScatterTaskContext Context;
            internal long Start;
            internal int Attempts, Successes;
        }
        [ThreadStatic] private static Scope _current;
        private static long _calls, _attempts, _successes, _ticks, _errors;
        [HarmonyPrefix]
        internal static void Before(GameLogic __instance, ref int workBatchSize, ref int maxRedispatchChance, out Scope __state)
        {
            __state = _current;
            _current = default(Scope);
            if (CpuCostProbe.Config?.balanceProductionWorkers != true) return;
            var context = __instance.threadController?.gameThreadContext?.assembler;
            if (context == null || context.workerThreadCount < 4 || context.batchCount <= 0 ||
                context.ordinals == null || context.batchCount >= context.ordinals.Length ||
                context.ordinals[context.batchCount] < 4096 || workBatchSize < 24 || maxRedispatchChance != 2) return;
            workBatchSize = 16;
            maxRedispatchChance = 8;
            if (CpuCostProbe.Config.systemTiming)
                _current = new Scope { Context = context, Start = Stopwatch.GetTimestamp() };
        }
        [HarmonyFinalizer]
        internal static void After(Exception __exception, Scope __state)
        {
            var current = _current;
            _current = __state;
            if (current.Context == null) return;
            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _attempts, current.Attempts);
            Interlocked.Add(ref _successes, current.Successes);
            Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - current.Start);
            if (__exception != null) Interlocked.Increment(ref _errors);
        }
        internal static void Note(ScatterTaskContext context, bool result)
        {
            if (!ReferenceEquals(_current.Context, context)) return;
            _current.Attempts++;
            if (result) _current.Successes++;
        }
        internal static long[] Take() => new[] {
            Interlocked.Exchange(ref _calls, 0), Interlocked.Exchange(ref _attempts, 0),
            Interlocked.Exchange(ref _successes, 0), Interlocked.Exchange(ref _ticks, 0),
            Interlocked.Exchange(ref _errors, 0) };
        internal static void Report()
        {
            var t = Take();
            ProjectEdenPlugin.Log.LogInfo($"[生产任务均衡] 开关={CpuCostProbe.Config?.balanceProductionWorkers == true} 门槛=4096 批大小=16 接手上限=8 工作线程调用={t[0]} 接手尝试={t[1]} 成功={t[2]} 异常={t[4]} 平均调用ns={(t[0] > 0 ? t[3] * (1000000000.0 / Stopwatch.Frequency) / t[0] : 0):0}；复用原版线程和阶段屏障，耗时含工作/调度/锁等待，不能当作帧时或纯计算耗时。");
        }
    }
    [HarmonyPatch(typeof(ScatterTaskContext), nameof(ScatterTaskContext.Redispatch))]
    internal static class ProductionRedispatchProbe
    {
        [HarmonyPostfix]
        internal static void After(ScatterTaskContext __instance, bool __result) => ProductionSchedulingPatches.Note(__instance, __result);
    }
}
