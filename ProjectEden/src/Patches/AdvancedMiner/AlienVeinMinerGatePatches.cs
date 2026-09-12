using System.Collections.Generic;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 外星矿脉（莫桑石）<b>只有大型采矿机能采</b>，普通采矿机在它上面盖不下去。
    ///
    /// <b>这不是新规则，是把一条已有的规则从运行时挪到建造时。</b>
    /// <see cref="AlienVeinPatches.Tick"/> 里本来就有这么一段：拿不到钻头槽的采矿机
    /// 直接 <c>Block</c>，一件也挖不出来——而普通采矿机<b>结构上就不可能有钻头槽</b>，
    /// 那一格是<c>物流站仓位</c>，它连 StationComponent 都没有。
    /// 所以此前的表现是：建得出来、看着正常、通上电、就是不出矿，
    /// 而且面板上没有任何一处说得清为什么。**这正是本仓库最不能接受的失败形状**
    /// ——每一步都成功，功能却是缺的。现在改成建造时就拒绝。
    ///
    /// <b>判据用 <c>isVeinCollector</c> 而不是物品号。</b> 它正是大型采矿机与普通采矿机的分界
    /// （原版 <c>CheckBuildConditions</c> 在 <c>if (desc.veinMiner)</c> 里就是按它分的两条采集路径：
    /// 收集器那条查询半径 18、普通那条 12），而且它和「有没有物流站仓位」是<b>同一件事</b>——
    /// 也就是和钻头槽存不存在同一件事。写物品号只是碰巧对，写这个标志才是因果对。
    ///
    /// <b>拒绝理由用 <c>NeedResource</c>，这有原版先例。</b> 普通采矿机挨着原油涌泉时拿到的
    /// 也是它——油被 <c>if (veinPool[id].type == EVeinType.Oil) continue;</c> 滤出了它的矿脉表，
    /// 于是在这台机器看来「附近没有能用的矿」。莫桑石对它是同一回事。
    /// （<c>EBuildCondition</c> 里没有「机型不对」这一项，而它的文案表在 resources.assets 里，
    /// 加不了新的。）
    ///
    /// 矿脉表不用自己重算：原版在两条分支末尾都把结果写进了
    /// <c>BuildPreview.parameters</c> / <c>paramCount</c>（IL 0688–069F 是普通采矿机那条），
    /// 读它就是读原版自己算出来的答案。
    /// </summary>
    [HarmonyPatch]
    internal static class AlienVeinMinerGatePatches
    {
        private static int _logged;

        // ── 挂钩：和 MinerBuildRulePatches 同样的三个工具 ────────
        //
        // Addon / Inserter 两个工具放不了采矿机，不必挂。

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_Click), nameof(BuildTool_Click.CheckBuildConditions))]
        private static void BuildTool_Click_CheckBuildConditions(BuildTool __instance, ref bool __result)
            => Gate(__instance, ref __result);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
        private static void BuildTool_BlueprintPaste_CheckBuildConditions(BuildTool __instance, ref bool __result)
            => Gate(__instance, ref __result);

        [HarmonyPatch(typeof(BuildTool_Path), nameof(BuildTool_Path.CheckBuildConditions))]
        [HarmonyPostfix]
        private static void BuildTool_Path_CheckBuildConditions(BuildTool __instance, ref bool __result)
            => Gate(__instance, ref __result);

        /// <summary>
        /// 拿着建筑时每帧都会跑，<b>不能有任何分配</b>：下标遍历 List、不取枚举器，
        /// 日志先抢占标志位再拼字符串。
        ///
        /// <b>自己把 <paramref name="result"/> 打成 false，不指望别人代劳。</b>
        /// <see cref="MinerBuildRulePatches"/> 的后置只在「它真的放行过东西」时才重算返回值
        /// （<c>if (cleared) result = allOk;</c>），两个后置谁先谁后又不保证，
        /// 所以这里既写条件也写返回值——两种顺序下结果都对。
        /// </summary>
        private static void Gate(BuildTool tool, ref bool result)
        {
            if (!AlienVeinPatches.Ready) return;

            AlienVeinConfig config = AlienVeinPatches.Config;

            if (config == null || !config.advancedMinerOnly) return;

            List<BuildPreview> previews = tool?.buildPreviews;

            if (previews == null || previews.Count == 0) return;

            VeinData[] pool = tool.factory?.veinPool;

            if (pool == null) return;

            int veinType = AlienVeinPatches.VeinType;

            for (var i = 0; i < previews.Count; i++)
            {
                BuildPreview preview = previews[i];
                PrefabDesc desc = preview?.desc;

                // 只管普通采矿机。大型采矿机是 isVeinCollector，放它过去
                if (desc == null || !desc.veinMiner || desc.isVeinCollector) continue;

                // 原版已经拒了就别改写理由——它给的那条多半更准
                if (preview.condition != EBuildCondition.Ok) continue;

                int[] ids = preview.parameters;
                int count = preview.paramCount;

                if (ids == null || count <= 0) continue;

                if (count > ids.Length) count = ids.Length;

                for (var j = 0; j < count; j++)
                {
                    int id = ids[j];

                    if (id <= 0 || id >= pool.Length) continue;

                    // EVeinType 是 byte 底的枚举，直接转 int 是编译期转换，没有装箱
                    if ((int)pool[id].type != veinType) continue;

                    preview.condition = EBuildCondition.NeedResource;
                    result = false;

                    if (Interlocked.Exchange(ref _logged, 1) == 0)
                        ProjectEdenPlugin.Log.LogInfo(
                            $"外星矿脉：已拦下普通采矿机盖在矿脉类型 {veinType} 上——" +
                            "钻头槽是物流站仓位，只有大型采矿机有");

                    break;
                }
            }
        }
    }
}
