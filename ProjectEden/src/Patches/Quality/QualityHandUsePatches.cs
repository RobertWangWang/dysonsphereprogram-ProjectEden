using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// <b>手上那一摞被用掉时，品质要跟着件数一起扣——原版改写后的 <c>Player.UseHandItems</c>
    /// 两个分支各漏了一半。</b>
    ///
    /// 这两条是把那 61 条指令逐条读出来的，不是推断：
    ///
    /// <b>分支 A（手上比要用的多，只用掉一部分）——只扣件数，不扣品质。</b>
    /// <code>
    /// 0024: ldfld &lt;inhandItemQua&gt;  → V_2      // 读到原始【整摞】的分
    /// 0031: call split_inc(ref 件数, ref inc, 用几个)  // 只劈了 inc
    /// 003A: ldloc.2 ; stsfld Q0                  // Q0 = V_2，原始全额
    /// 003F: call set_inhandItemInc(V_1)          // 手上的 qua := 全额
    /// </code>
    /// 对照 <c>Player.TakeItemFromPlayer</c>：它在同样位置有一句
    /// <c>ProjectEdenQualityChannel::Split(...)</c>，**这里没有**。于是拿走几个之后，
    /// 剩下的货顶着整摞的点数——<b>单件分数凭空往上涨</b>，就是本仓库在
    /// <c>StationStore.inc</c> 上记过的那个形状。
    ///
    /// <b>分支 B（手上的全用光）——算对了，然后在返回前自己擦掉。</b>
    /// <code>
    /// 0061: stsfld Q0                 // 把手上的品质发布出去，正确
    /// 0070: ldc.i4.0 ; stsfld Q0      // 擦
    /// 007B: ldc.i4.0 ; stsfld Q0      // 再擦，然后 ret
    /// </code>
    /// 和 <c>TakeItemFromPlayer</c> **一模一样的形状**：擦除在被调方体内，
    /// 调用方读到的必然是 0，而且调用方那边再怎么转译都够不着。
    ///
    /// <b>所以修法是量差值，而且它是精确的不是近似的。</b> 前置记下整摞的分和件数，
    /// 后置按「这次用掉几件」算出该走的那一份，**把剩下的写回去**。
    /// 两个分支用同一个式子就都对了：分支 B 里原版已经写了 0，而
    /// <c>原分 − 全额 = 0</c>，两者一致；分支 A 里原版什么都没扣，这里补上。
    ///
    /// <b>不往侧信道发布。</b> 后置确实跑在被调方那两次擦除之后、控制权回到调用方之前，
    /// 所以技术上发得出去——但没有调用方在读它，而一个没人读的寄存器留着非零值，
    /// 正是「品质凭空长出来」的来源（实测过一次每件 1010 分对上限 100）。
    /// 修好字段本身已经够了：建造那一刀和喂料那一刀都是**量玩家身上少了多少**，
    /// 字段准了，它们自动跟着准。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityHandUsePatches
    {
        /// <summary>这条只由玩家操作触发，不在 tick 上，所以普通静态字段就够。</summary>
        private static int _beforeQua;

        private static int _beforeCount;

        private static int _reported;

        internal static void Report()
        {
            if (QualityAccess.GetInhandQua == null || QualityAccess.SetInhandQua == null)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质·手上那一格：**没接上**——读不到或写不进 <inhandItemQua>。"
                    + "这种情况下手上那一摞用掉一部分时品质不会扣，剩下的货单件分数会凭空上涨，而且不报错。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "物品品质·手上那一格：已接线。用掉一部分时按件数比例扣品质"
                + "（原版改写后漏扣，剩下的货会顶着整摞的分）。第一次真的扣到时会再报一行。");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), nameof(Player.UseHandItems))]
        private static void Pre(Player __instance)
        {
            _beforeQua = 0;
            _beforeCount = 0;

            if (__instance == null || QualityAccess.GetInhandQua == null) return;

            _beforeCount = __instance.inhandItemCount;
            _beforeQua = QualityAccess.GetInhandQua(__instance);
        }

        /// <summary>
        /// <paramref name="__result"/> 是这次真正用掉的件数。
        ///
        /// <b>件数无条件汇报给建造那一刀，分数有多少算多少。</b> 普通料也要进分母——
        /// 它就是「掺进来把平均分拉低」的那一半，漏掉它，拿普通料建造反而不掉分。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), nameof(Player.UseHandItems))]
        private static void Post(Player __instance, int __result)
        {
            if (__result <= 0) return;

            var used = 0;

            if (_beforeCount > 0 && _beforeQua > 0 && QualityAccess.SetInhandQua != null)
            {
                used = __result >= _beforeCount
                    ? _beforeQua
                    : (int)((long)_beforeQua * __result / _beforeCount);

                // 整摞用光就整摞带走，免得整数除法留下永远出不去的零头
                QualityAccess.SetInhandQua(__instance, _beforeQua - used);

                if (used > 0) ReportOnce(__result, used);
            }

            // 建造扣料正在进行中的话，这一份要计进那座建筑的材料平均分
            if (QualityBuildPatches._depth > 0) QualityBuildPatches.NoteHandUse(__result, used);
        }

        private static void ReportOnce(int items, int qua)
        {
            if (System.Threading.Interlocked.Exchange(ref _reported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogWarning(
                $"物品品质·手上那一格：第一次按比例扣掉品质——用掉 {items} 件、带走 {qua} 分。"
                + "**在这之前手上那一摞是只扣件数不扣分的**，剩下的货会顶着整摞的点数，"
                + "单件分数越拿越高。整局只报一次。");
        }
    }
}
