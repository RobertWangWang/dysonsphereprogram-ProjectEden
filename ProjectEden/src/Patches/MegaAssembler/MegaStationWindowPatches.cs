// 本文件移植自 ProjectGenesis（创世之书），属于其衍生作品。
// Portions of this file are derived from ProjectGenesis (GenesisBook).
//
//     Copyright (C) 2022-2026 Awbugl and ProjectGenesis contributors
//     https://github.com/Awbugl/ProjectGenesis
//
// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 巨型建筑同时有 assemblerId、stationId，而氧化还原燃烧厂还多一个 powerGenId。
    /// <c>UIGame.OnPlayerInspecteeChange</c> 打开每一个窗口前都先 ShutAllFunctionWindow()，
    /// 而这几个判断是<b>顺序排下来的独立 if</b>——所以<b>最后命中的那个赢</b>。
    /// 按 IL 顺序：assemblerId @00A7 → powerGenId @0182 → stationId @01AA，
    /// 于是制造台窗口每次都被后面两个关掉，配方就没法选了。
    ///
    /// 这里对巨型建筑把 <b>stationId、powerGenId 和 powerNodeId 三个都报成 0</b>，只留制造台窗口。
    /// 储物格是按配方自动配置的（见 MegaStationPatches），运输机数量和运送量也是自动拉满的；
    /// 燃料舱则由 <c>RedoxBurnerPatches.Burn</c> 自己喂，两边都没有需要玩家手动设置的项。
    ///
    /// <b>powerGenId 那一条是被一次崩溃逼出来的，不是预先想到的。</b>
    /// 给燃烧厂挂上发电组件之后，点开它直接抛
    /// <c>NullReferenceException at UIPowerGeneratorWindow._OnOpen</c>。
    /// 把 DMD 偏移换算回原始体（该方法在 0x50 之前有两条短跳转，各 +3 字节）落在
    /// <c>ldfld powerNetworkDesc</c> @0046 / <c>callvirt ManualBehaviour::_Open()</c> @004B 上——
    /// 也就是那个窗口的某个子部件对这台建筑根本没被装配起来。
    /// <b>修法不是去把那个窗口伺候好，而是压根别开它</b>：发电组件照常 tick、照常发电，
    /// 被过滤掉的只是「点开时显示哪个面板」这一次查询。
    ///
    /// <b>powerNodeId 那一条是被第二次点开逼出来的，而它的起因正是上一条的修法。</b>
    /// 给燃烧厂补上「接得上电网」的身份（<c>isPowerNode</c>）之后，它同时也就有了
    /// <c>powerNodeId</c>，而 <c>OpenNodeWindow()</c>(IL 0640) 排在
    /// <c>OpenAssemblerWindow()</c>(IL 0361) <b>后面</b>——于是电力节点窗口接替发电机窗口，
    /// 继续把制造台窗口顶掉。症状从「点开就崩」变成「点开了但没有配方按钮」。
    ///
    /// <b>教训不是「再补一个」，是这一族窗口要按清单一次数清。</b>
    /// 这个方法里有 23 个组件 id 判断，每一个都可能被将来某座建筑同时命中；
    /// 判据是「这台机器有没有这个组件」而不是「我想不想要这个窗口」。
    /// 巨型建筑目前会命中 assembler / station / powerCon / powerGen / powerNode 五个，
    /// 其中 powerCon 不开窗口，其余三个都要压掉。
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
        /// 目标代码（同一个形状出现两次，字段名不同）：
        ///     int stationId  = factory.entityPool[objId].stationId;
        ///     int powerGenId = factory.entityPool[objId].powerGenId;
        /// 在读出之后各插一次过滤调用。
        ///
        /// <b>两个字段都要改，而且要报出改了几处。</b> 只改 stationId 的话，
        /// 挂了发电组件的巨型建筑仍然会开发电机窗口（而那个窗口对它是空引用）；
        /// 而 <c>MatchForward</c> 找不到时是静默返回原指令的——本仓库的规矩是
        /// 匹配数要打进日志、为 0 要吼出来，否则「它悄悄什么都没做」就没法诊断。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIGame), nameof(UIGame.OnPlayerInspecteeChange))]
        private static IEnumerable<CodeInstruction> UIGame_OnPlayerInspecteeChange_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);

            var done = 0;

            foreach (string field in new[]
                     {
                         nameof(EntityData.stationId),
                         nameof(EntityData.powerGenId),
                         nameof(EntityData.powerNodeId),
                     })
            {
                var matcher = new CodeMatcher(code);

                matcher.MatchForward(true,
                    new CodeMatch(OpCodes.Ldfld,
                        AccessTools.Field(typeof(PlanetFactory), nameof(PlanetFactory.entityPool))),
                    new CodeMatch(OpCodes.Ldarg_2),
                    new CodeMatch(OpCodes.Ldelema),
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(EntityData), field)));

                if (matcher.IsInvalid)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"UIGame.OnPlayerInspecteeChange 没找到 {field} 读取点——" +
                        "巨型建筑点开后会是别的窗口而不是制造台窗口，无法选配方" +
                        (field == nameof(EntityData.powerGenId)
                            ? "，而氧化还原燃烧厂还会直接抛 NullReferenceException"
                            : ""));

                    continue;
                }

                // 把加载 factory 的那条指令复制一份，避免写死局部变量编号
                var loadFactory = new CodeInstruction(matcher.InstructionAt(-4));

                matcher.Advance(1)
                       .Insert(loadFactory,
                               new CodeInstruction(OpCodes.Ldarg_2),
                               new CodeInstruction(OpCodes.Call, FilterStationIdMethod));

                code = new List<CodeInstruction>(matcher.InstructionEnumeration());

                done++;
            }

            if (done == 3)
                ProjectEdenPlugin.Log.LogInfo(
                    "UIGame.OnPlayerInspecteeChange：已接管巨型建筑的窗口选择"
                    + "（stationId / powerGenId / powerNodeId 三处）；"
                    + "物流站窗口改由 MegaBothWindowsPatches 自己开，不走原版的记账");
            else
                ProjectEdenPlugin.Log.LogError(
                    $"UIGame.OnPlayerInspecteeChange：只改写了 {done} 处，应为 3 处"
                    + "——巨型建筑点开后会是别的窗口而不是制造台窗口，选不了配方");

            return code;
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
