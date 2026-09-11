using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 巨型建筑同时有 assemblerId 和 stationId，而 UIGame 打开这两个窗口前都会先
    /// ShutAllFunctionWindow()，物流站窗口在后面执行，会把制造台窗口关掉——配方就没法选了。
    ///
    /// 这里对巨型建筑把 stationId 报成 0，只留制造台窗口。储物格是按配方自动配置的
    /// （见 MegaStationPatches），运输机数量和运送量也是自动拉满的，没有需要玩家手动设置的项。
    ///
    /// 移植自 ProjectGenesis 的 MegaAssemblerLogisticPatches.FilterStationId。
    /// </summary>
    [HarmonyPatch]
    internal static class MegaStationWindowPatches
    {
        private static readonly MethodInfo FilterStationIdMethod =
            AccessTools.Method(typeof(MegaStationWindowPatches), nameof(FilterStationId));

        /// <summary>供 IL 调用：巨型建筑返回 0，其余原样返回。</summary>
        internal static int FilterStationId(int stationId, PlanetFactory factory, int objId)
        {
            if (stationId <= 0 || factory == null) return stationId;

            return IsMegaBuilding(factory, objId) ? 0 : stationId;
        }

        /// <summary>
        /// 目标代码：
        ///     int stationId = factory.entityPool[objId].stationId;
        /// 在读出 stationId 之后插入过滤调用。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIGame), nameof(UIGame.OnPlayerInspecteeChange))]
        private static IEnumerable<CodeInstruction> UIGame_OnPlayerInspecteeChange_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var matcher = new CodeMatcher(instructions);

            matcher.MatchForward(true,
                new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(PlanetFactory), nameof(PlanetFactory.entityPool))),
                new CodeMatch(OpCodes.Ldarg_2),
                new CodeMatch(OpCodes.Ldelema),
                new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(EntityData), nameof(EntityData.stationId))));

            if (matcher.IsInvalid)
            {
                ProjectEdenPlugin.Log.LogError(
                    "UIGame.OnPlayerInspecteeChange 没找到 stationId 读取点，" +
                    "巨型建筑点开后会是物流站窗口而不是制造台窗口，无法选配方。");

                return matcher.InstructionEnumeration();
            }

            // 把加载 factory 的那条指令复制一份，避免写死局部变量编号
            var loadFactory = new CodeInstruction(matcher.InstructionAt(-4));

            matcher.Advance(1)
                   .Insert(loadFactory,
                           new CodeInstruction(OpCodes.Ldarg_2),
                           new CodeInstruction(OpCodes.Call, FilterStationIdMethod));

            ProjectEdenPlugin.Log.LogInfo("UIGame.OnPlayerInspecteeChange：已接管巨型建筑的窗口选择");

            return matcher.InstructionEnumeration();
        }

        private static bool IsMegaBuilding(PlanetFactory factory, int entityId)
        {
            if (entityId <= 0) return false;

            EntityData[] entityPool = factory.entityPool;

            if (entityId >= entityPool.Length) return false;

            int assemblerId = entityPool[entityId].assemblerId;

            if (assemblerId <= 0) return false;

            AssemblerComponent[] pool = factory.factorySystem.assemblerPool;

            return assemblerId < pool.Length && pool[assemblerId].speed >= MegaBuildingRegistry.MegaSpeedThreshold;
        }
    }
}
