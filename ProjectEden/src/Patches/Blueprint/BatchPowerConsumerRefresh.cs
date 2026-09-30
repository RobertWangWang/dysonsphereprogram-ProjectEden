using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectEden.Patches.Diagnostics;

namespace ProjectEden.Patches
{
    // 只接管CreateEntityLogicComponents末尾的显示刷新，不推迟真实电网组件/连接创建。
    [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.CreateEntityLogicComponents))]
    internal static class BatchPowerConsumerRefresh
    {
        internal sealed class Scope : IDisposable
        {
            internal Scope Previous;
            internal FactoryModel Model;
            internal bool Dirty, Closed;
            internal long Requests;
            public void Dispose()
            {
                if (Closed) return;
                Closed = true;
                Current = Previous; // 刷新失败也不能把批次上下文留到下一轮。
                var model = Model; Model = null;
                if (!Dirty) return;
                bool timed = BlueprintPasteProbe.Config?.enabled == true;
                long start = timed ? Stopwatch.GetTimestamp() : 0;
                bool failed = false;
                try { model.RefreshPowerConsumers(); }
                catch { failed = true; throw; }
                finally
                {
                    if (timed) lock (Gate)
                    {
                        _batches++; _requests += Requests; _flushes++;
                        _ticks += Stopwatch.GetTimestamp() - start;
                        if (failed) _errors++;
                    }
                }
            }
        }
        [ThreadStatic] internal static Scope Current;
        private static readonly object Gate = new object();
        private static long _batches, _requests, _flushes, _ticks, _errors;
        internal static Scope Begin(FactoryModel model)
        {
            var scope = new Scope { Previous = Current, Model = model };
            Current = scope; return scope;
        }
        internal static void Request(FactoryModel model)
        {
            var scope = Current;
            if (scope != null && model != null && ReferenceEquals(scope.Model, model))
            {
                scope.Dirty = true; scope.Requests++; return;
            }
            model.RefreshPowerConsumers();
        }
        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var refresh = AccessTools.Method(typeof(FactoryModel), nameof(FactoryModel.RefreshPowerConsumers), Type.EmptyTypes);
            var hits = code.Where(c => c.Calls(refresh)).ToArray();
            if (hits.Length != 1)
            {
                ProjectEdenPlugin.Log.LogWarning("批量电力显示刷新：建造入口不匹配，保持逐座刷新。"); return code;
            }
            hits[0].opcode = OpCodes.Call;
            hits[0].operand = AccessTools.Method(typeof(BatchPowerConsumerRefresh), nameof(Request));
            ProjectEdenPlugin.Log.LogInfo("批量电力显示刷新：秒建批次内合并制造组件末尾的显示刷新，批次结束/异常退出时同步刷新；电网逻辑即时更新。");
            return code;
        }
        internal static long[] Take()
        {
            lock (Gate)
            {
                var t = new[] { _batches, _requests, _flushes, _ticks, _errors };
                _batches = _requests = _flushes = _ticks = _errors = 0; return t;
            }
        }
        internal static void Report()
        {
            var t = Take(); if (t[0] == 0) return;
            ProjectEdenPlugin.Log.LogInfo($"[批量电力显示刷新] 批次={t[0]} 原刷新请求={t[1]} 实际刷新={t[2]} 合并省去={t[1]-t[2]} 刷新总计={t[3]*1000.0/Stopwatch.Frequency:0.###}ms 异常={t[4]}；耗时位于批次收尾，需与BuildFinally总耗时一起评估。");
        }
    }
}
