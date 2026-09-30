using System;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using ProjectEden.Patches.Diagnostics;

namespace ProjectEden.Patches
{
    // 索引只活在一次同步蓝图检查内。预览碰撞体不在原表中时，也避免反复全表查找。
    [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
    internal static class BlueprintColliderLookupPatches
    {
        internal sealed class Scope
        {
            internal Scope Previous;
            internal readonly Dictionary<NearColliderLogic, Index> Tables = new Dictionary<NearColliderLogic, Index>();
            internal long Queries, Builds, Entries, BuildTicks;
        }
        internal sealed class Index
        {
            internal object Source;
            internal int Count;
            internal readonly Dictionary<int, int> Ids = new Dictionary<int, int>();
        }
        [ThreadStatic] internal static Scope Current;
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        internal static void Begin(out Scope __state) { __state = new Scope { Previous = Current }; Current = __state; }
        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void End(Scope __state)
        {
            if (__state == null) return;
            Current = __state.Previous;
            if (BlueprintPasteProbe.Config?.enabled == true && __state.Queries > 0)
                ProjectEdenPlugin.Log.LogInfo($"[蓝图碰撞索引] 查询={__state.Queries} 建表={__state.Builds} 扫描条目={__state.Entries} 建表耗时={__state.BuildTicks * 1000.0 / Stopwatch.Frequency:0.###}ms；跳过逐碰撞体全表扫描。");
            __state.Tables.Clear();
        }
        internal static void Invalidate(NearColliderLogic owner)
        {
            for (var scope = Current; scope != null; scope = scope.Previous) scope.Tables.Remove(owner);
        }
        internal static bool Lookup(NearColliderLogic owner, Collider cc, ref int result)
        {
            var scope = Current;
            // Unity已销毁对象/空对象保留原版特殊相等语义。
            if (scope == null || cc == null || owner.colliderObjs == null) return true;
            var source = owner.colliderObjs;
            if (!scope.Tables.TryGetValue(owner, out var index) || !ReferenceEquals(index.Source, source) || index.Count != source.Count)
            {
                long start = Stopwatch.GetTimestamp();
                index = new Index { Source = source, Count = source.Count };
                foreach (var entry in source)
                {
                    var collider = entry.Value.collider;
                    if (collider == null) continue;
                    int key = collider.GetInstanceID();
                    // 与原版枚举顺序一致，重复碰撞体取第一项，不能用字典键代替Value.id。
                    if (!index.Ids.ContainsKey(key)) index.Ids.Add(key, entry.Value.id);
                }
                scope.Tables[owner] = index;
                scope.Builds++; scope.Entries += source.Count;
                scope.BuildTicks += Stopwatch.GetTimestamp() - start;
            }
            scope.Queries++;
            result = index.Ids.TryGetValue(cc.GetInstanceID(), out int id) ? id : 0;
            return false;
        }
    }
    [HarmonyPatch(typeof(NearColliderLogic), nameof(NearColliderLogic.FindColliderId))]
    internal static class BlueprintColliderFindPatch
    {
        [HarmonyPrefix]
        internal static bool Prefix(NearColliderLogic __instance, Collider cc, ref int __result)
            => BlueprintColliderLookupPatches.Lookup(__instance, cc, ref __result);
    }
    [HarmonyPatch]
    internal static class BlueprintColliderMutationPatch
    {
        [HarmonyTargetMethods]
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (string name in new[] { "Init", "Free", "DeleteDeadColliders", "UpdatePlayerPosNear", "UpdateCursorNear", "ActiveBuildPreviewsNear", "ActiveEntityBuildCollidersInArea", "ActiveEnemyBuildingColliderInArea", "ActiveCollidersInArea" })
                yield return AccessTools.Method(typeof(NearColliderLogic), name);
        }
        [HarmonyPrefix]
        internal static void Prefix(NearColliderLogic __instance) => BlueprintColliderLookupPatches.Invalidate(__instance);
        [HarmonyFinalizer]
        internal static void Finalizer(NearColliderLogic __instance) => BlueprintColliderLookupPatches.Invalidate(__instance);
    }
}
