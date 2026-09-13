using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 第 2 阶段的<b>最小铸造点</b>：采出来的矿自带品质分。
    ///
    /// 1c 让品质在主干道上流动，但整条主干道上<b>没有一个地方生成品质</b>——
    /// 全是搬运和分摊，源头是 0，下游自然全是 0。所以必须先有一个源，
    /// 哪怕只有一个，才谈得上「进游戏看得见」。
    ///
    /// 挑采矿站作为源有三个理由：它是主干道的**起点**（矿 → 箱子 → 物流站 → 生产），
    /// 它的槽位 0 装的一定是刚采出来的同一种矿（没有混料问题），
    /// 而且 <c>UpdateVeinCollection</c> 只对 <c>isVeinCollector</c> 的站点跑，
    /// 天然只影响采矿机，不会误伤物流站。
    ///
    /// <b>这一版的规则刻意做得最简单：品质分 = 件数 × 每件固定值。</b>
    /// 真正的第 2 阶段（矿脉品位、机器等级、增产剂互动）是后面的事;
    /// 现在要的是「能不能看见品质从矿一路走到建筑」这一个问题的答案。
    ///
    /// 直接**赋值**而不是累加，是因为这里是源头：槽位 0 的内容全部来自采矿，
    /// 没有别处运来的品质需要保留。累加会让停在那里没被取走的矿一直涨品质。
    /// </summary>
    [HarmonyPatch]
    internal static class QualitySourcePatches
    {
        /// <summary>每件矿自带的品质分。挑 10 是因为它在面板上一眼看得出来，不是平衡结论。</summary>
        private const int PerOre = 10;

        private static int _reported;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.UpdateVeinCollection))]
        private static void MintMinedQuality(StationComponent __instance)
        {
            if (!QualityAccess.Ready) return;

            StationStore[] storage = __instance.storage;

            if (storage == null || storage.Length == 0) return;

            int count = storage[0].count;

            if (count <= 0) return;

            QualityAccess.SetStationQua(ref storage[0], count * PerOre);

            // 一次性日志要用 Interlocked 抢：这条路径跑在 ~31 线程的并行行星 tick 上，
            // 普通的 `if (!done) done = true` 会让每个线程都打一遍。
            if (Interlocked.Exchange(ref _reported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogWarning(
                $"物品品质：铸造点已生效——采矿站槽位 0 现在按每件 {PerOre} 分铸造品质" +
                $"（当前 {count} 件 = {count * PerOre} 分）。这是阶段 2 的最小实现。");
        }

        /// <summary>启动时说一句现在处于哪种状态。**三种状态都要打**，不然分不清没装和没接线。</summary>
        internal static void Report()
        {
            if (!QualityWidening.FieldsPresent)
            {
                ProjectEdenPlugin.Log.LogInfo("物品品质：孪生字段不在，铸造点不启用。");

                return;
            }

            if (!QualityAccess.Ready)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质：孪生字段在，但 StationStore.qua 的存取器没建起来——" +
                    "铸造点不启用，品质会一直是 0。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质：铸造点已装（采矿站，每件 {PerOre} 分）。" +
                "要在采矿站真的采到矿之后才会有数。");
        }
    }
}
