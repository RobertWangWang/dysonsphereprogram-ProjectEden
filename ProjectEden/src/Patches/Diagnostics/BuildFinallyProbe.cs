using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace ProjectEden.Patches.Diagnostics
{
    // 仅在BuildFinally调用范围记录；排他计时避免实体创建与内部组件创建重复累计。
    [HarmonyPatch]
    internal static class BuildFinallyProbe
    {
        internal static readonly string[] MethodNames = {
            "BuildFinally", "FlattenTerrain", "AddEntityDataWithComponents", "AddEntityData",
            "HandleObjectConnChangeWhenBuild", "CreateEntityLogicComponents", "CreateEntityDisplayComponents",
            "RemovePrebuildWithComponents", "OnBeltBuilt", "OnInserterBuilt", "OnAddonBuilt",
            "OnBuildEntity", "OnSinglyBuildEntity", "CheckDysonSphereConditionAfterConstruction",
            "NotifyBuilt", "MarkItemBuilt", "NotifyOnBuild"
        };
        private static readonly string[] Labels = {
            "BuildFinally其余", "地形平整", "实体创建其余", "实体池登记",
            "预建物连接迁移", "逻辑组件创建", "显示组件创建（模型/碰撞/音频）",
            "移除预建物", "传送带连接", "分拣器连接", "附加设备连接",
            "建造事件/Mod回调", "单体建造事件", "戴森球条件更新",
            "建造工具通知", "建造历史登记", "剧情/成就通知"
        };
        private static readonly Dictionary<MethodBase, int> Methods = FindMethods();
        private static Dictionary<MethodBase, int> FindMethods()
        {
            var result = new Dictionary<MethodBase, int>();
            for (int i = 0; i < MethodNames.Length; i++)
            {
                Type type = i == 14 ? typeof(PlayerAction_Build) : i == 15 ? typeof(GameHistoryData) : i == 16 ? typeof(GameScenarioLogic) : typeof(PlanetFactory);
                var matches = AccessTools.GetDeclaredMethods(type).Where(m => m.Name == MethodNames[i]).ToArray();
                if (matches.Length != 1)
                {
                    ProjectEdenPlugin.Log.LogWarning($"建造结算细分：{type.Name}.{MethodNames[i]} 匹配{matches.Length}个，跳过该项；耗时留在上级其余项。");
                    continue;
                }
                result.Add(matches[0], i);
            }
            return result;
        }
        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> Targets() => Methods.Keys;
        internal sealed class Measurement
        {
            internal readonly long[] Ticks = new long[17], Calls = new long[17];
        }
        internal sealed class Scope
        {
            internal Scope Parent;
            internal Measurement Data;
            internal long Start, ChildTicks;
            internal int Kind;
        }
        [ThreadStatic] private static Scope _current;
        private static readonly object Gate = new object();
        private static readonly long[] Ticks = new long[17], Calls = new long[17];
        private static long _roots, _total, _worst, _errors;
        private static float _nextReport;
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        internal static void Before(MethodBase __originalMethod, out Scope __state)
        {
            __state = null;
            if (_current == null && BlueprintPasteProbe.Config?.enabled != true) return;
            int kind = Methods[__originalMethod];
            if (_current == null && kind != 0) return;
            __state = new Scope { Parent = _current, Kind = kind, Data = _current?.Data ?? new Measurement(), Start = Stopwatch.GetTimestamp() };
            _current = __state;
        }
        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void After(Scope __state, Exception __exception)
        {
            if (__state == null) return;
            long elapsed = Stopwatch.GetTimestamp() - __state.Start;
            _current = __state.Parent;
            var data = __state.Data;
            data.Ticks[__state.Kind] += elapsed - __state.ChildTicks;
            data.Calls[__state.Kind]++;
            if (__state.Parent != null) { __state.Parent.ChildTicks += elapsed; return; }
            // 完整调用返回后一次合并；窗口不能拆开总量与子项，异常保持原传播。
            lock (Gate)
            {
                _roots++; _total += elapsed; if (elapsed > _worst) _worst = elapsed;
                if (__exception != null) _errors++;
                for (int i = 0; i < Ticks.Length; i++) { Ticks[i] += data.Ticks[i]; Calls[i] += data.Calls[i]; }
            }
        }
        internal static long[] Take()
        {
            lock (Gate)
            {
                var result = new long[38];
                result[0] = _roots; result[1] = _total; result[2] = _worst; result[3] = _errors;
                Array.Copy(Ticks, 0, result, 4, 17); Array.Copy(Calls, 0, result, 21, 17);
                _roots = _total = _worst = _errors = 0;
                Array.Clear(Ticks, 0, Ticks.Length); Array.Clear(Calls, 0, Calls.Length);
                return result;
            }
        }
        internal static void Report(float now)
        {
            if (BlueprintPasteProbe.Config?.enabled != true) return;
            if (_nextReport != 0 && now < _nextReport) return;
            _nextReport = now + BlueprintPasteProbe.Config.ReportSeconds();
            BatchPowerConsumerRefresh.Report();
            var t = Take(); if (t[0] == 0) return;
            double ms = 1000.0 / Stopwatch.Frequency;
            var sb = new StringBuilder($"[建造结算细分] 最外层BuildFinally调用={t[0]} 总计={t[1]*ms:0.###}ms 单次均值={t[1]*ms/t[0]:0.###}ms 最慢={t[2]*ms:0.###}ms 异常={t[3]}；各项排他累计，含子调用补丁/等待与探针开销，不是帧时：");
            foreach (int i in Enumerable.Range(0, 17).OrderByDescending(i => t[4+i]))
                if (t[21+i] > 0)
                    sb.Append($"\n  {Labels[i]}（{MethodNames[i]}）：{t[4+i]*ms:0.###}ms / {t[21+i]}次，占比={(t[1]>0 ? 100.0*t[4+i]/t[1] : 0):0.0}%");
            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }
    }
    [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
    internal static class BuildFinallyProbeReport
    {
        [HarmonyPostfix]
        private static void Postfix() => BuildFinallyProbe.Report(UnityEngine.Time.realtimeSinceStartup);
    }
}
