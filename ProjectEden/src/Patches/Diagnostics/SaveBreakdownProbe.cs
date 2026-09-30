using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace ProjectEden.Patches.Diagnostics
{
    // 低频存档分段。Export 直接写 BinaryWriter，因此耗时包含同步I/O，不能称为纯序列化。
    [HarmonyPatch]
    internal static class SaveBreakdownProbe
    {
        private sealed class Session
        {
            internal Session Parent;
            internal long GameDataTicks, FactoryTicks, ModTicks, FactoryCount;
        }
        [ThreadStatic] private static Session _active;

        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(GameSave), "SaveCurrentGame", new[] { typeof(string) });
            yield return AccessTools.Method(typeof(GameData), "Export", new[] { typeof(BinaryWriter) });
            yield return AccessTools.Method(typeof(PlanetFactory), "Export", new[] { typeof(Stream), typeof(BinaryWriter) });
            yield return AccessTools.Method(typeof(ProjectEdenPlugin), "Export", new[] { typeof(BinaryWriter) });
        }

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        internal static void Before(MethodBase __originalMethod, out long __state)
        {
            bool root = __originalMethod.DeclaringType == typeof(GameSave);
            __state = root ? (NanosecondProbe.Enabled ? Stopwatch.GetTimestamp() : 0)
                : (_active != null ? Stopwatch.GetTimestamp() : 0);
            if (root && __state != 0) _active = new Session { Parent = _active };
        }

        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void After(MethodBase __originalMethod, long __state, Exception __exception)
        {
            if (__state == 0 || _active == null) return;
            long ticks = Stopwatch.GetTimestamp() - __state;
            var s = _active;
            Type type = __originalMethod.DeclaringType;
            if (type == typeof(GameData)) s.GameDataTicks += ticks;
            else if (type == typeof(PlanetFactory)) { s.FactoryTicks += ticks; s.FactoryCount++; }
            else if (type == typeof(ProjectEdenPlugin)) s.ModTicks += ticks;
            else
            {
                // 先解除当前会话，确保异常退出、退出存档及下次保存都不会串状态。
                _active = s.Parent;
                ProjectEdenPlugin.Log.LogInfo($"[存档分段] 总计ns={NanosecondProbe.Ns(ticks)} 游戏数据导出ns={NanosecondProbe.Ns(s.GameDataTicks)} 工厂导出ns={NanosecondProbe.Ns(s.FactoryTicks)} 工厂数={s.FactoryCount} Eden附加数据ns={NanosecondProbe.Ns(s.ModTicks)} 异常={__exception?.GetType().Name ?? "无"}；导出包含同步写入，工厂是游戏数据子集，嵌套耗时不可相加；未单独测量磁盘耗时。");
            }
        }
    }
}
