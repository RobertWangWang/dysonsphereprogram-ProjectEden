using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// <b>在配方窗口里点产物图标把货拿走时，把品质一起带走。</b>
    ///
    /// 这是 <c>produced[]</c> 的<b>第三条出路</b>，而品质原先只挂在前两条上：
    /// <list type="number">
    /// <item><c>produced[]</c> → 本建筑物流槽位 → 物流网（<see cref="QualityRefineryPatches.OnProduced"/>）；</item>
    /// <item><c>produced[]</c> → 传送带（<c>MegaAssemblerPatches.UpdateOutputSlots</c>）；</item>
    /// <item><c>produced[]</c> → <b>点一下产物图标，直接进背包</b>——就是这里。</item>
    /// </list>
    ///
    /// <b>第三条是构造上恒为 0，不是偶发。</b> 实测改写后的程序集，
    /// <c>UIAssemblerWindow.OnProductIcon0Click</c> 调 <c>TryAddItemToPackage</c>
    /// 之前<b>一个字都没写</b>侧信道，而 IL 0097 的 <c>ldc.i4.0 ; stsfld Q0</c>
    /// 是调用<b>之后</b>的擦除。协议是「调用方在调用前写」，没写就等于送 0 分。
    ///
    /// 为什么 preloader 没管它：<c>QualityFieldAnalyzer.IsDisplayOnly</c> 把所有
    /// <c>UI*</c> 类型整个跳过——对绝大多数界面类是对的（它们只画不搬），
    /// 而这两个方法<b>是真的在搬货</b>。**「按名字前缀判定够不够」这件事，
    /// 在这两个方法上不够。**
    ///
    /// <b>口径和另外几条完全一致</b>：机器里算出来的那部分读同一个
    /// <c>quaProduced</c>（并扣掉），提纯厂凭等级铸出来的那部分查同一张
    /// <see cref="QualityRefineryRegistry"/> 表。两部分都没有的配方直接返回，
    /// 寄存器保持被上一次调用擦干净的状态——也就是 0。
    ///
    /// <b>不清理。</b> 写完就交给原版调用，而原版自己在调用后擦（上面那句 IL 0097）。
    /// 再补一次 <c>ClearChannel</c> 反而要担心顺序。
    ///
    /// <b>已知未覆盖</b>：<c>MultiProductUIPatches</c> 给三产物以上的配方克隆出来的
    /// 第 3 个及以后的槽位，走的是它自己的处理器。提纯配方只有一个产物，碰不到，
    /// 所以先不动——真要用到时按同一套写即可。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityTakeOutPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIAssemblerWindow), "OnProductIcon0Click")]
        private static void OnProductIcon0Click_Prefix(UIAssemblerWindow __instance) => Arm(__instance, 0);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIAssemblerWindow), "OnProductIcon1Click")]
        private static void OnProductIcon1Click_Prefix(UIAssemblerWindow __instance) => Arm(__instance, 1);

        /// <summary>
        /// 把这一格产物的整堆分数写进 0 号寄存器。
        ///
        /// 原版取走的是 <c>produced[index]</c> 的<b>全部</b>（IL 0087–008D 把它整个当作 count
        /// 传进去，随后 00B5 直接置 0），所以整格的分数一起走。
        ///
        /// <b>两部分相加，而且它们真的是两回事。</b>
        /// <list type="bullet">
        /// <item><c>quaProduced[index]</c>——这台机器<b>用带品质的料造出来</b>的那部分，
        /// 由 <see cref="QualityCraftFlowPatches"/> 结算进去。取出来就得扣掉，
        /// 不扣的话同一份分数会在下一次出货时再发一遍。</item>
        /// <item>提纯配方的<b>铸造分</b>——提纯厂凭配方等级凭空产生的那部分，它<b>不在</b>
        /// <c>quaProduced</c> 里（注入点是 <see cref="QualityRefineryPatches.OnProduced"/>
        /// 和巨型建筑的带子出口，两处都查 <c>FindTier</c>）。所以这里查同一张表、
        /// 用同一个每件分数，三条出路口径一致。</item>
        /// </list>
        /// 相加不会重复计数：一个来自投料、一个来自配方等级，没有任何一条路会同时写进两边。
        /// </summary>
        private static void Arm(UIAssemblerWindow win, int index)
        {
            if (win == null || QualityAccess.SetChannel0 == null) return;

            int id = win.assemblerId;

            if (id <= 0) return;

            AssemblerComponent[] pool = win.factorySystem?.assemblerPool;

            if (pool == null || id >= pool.Length) return;

            if (pool[id].id != id || pool[id].recipeId <= 0) return;

            int[] produced = pool[id].produced;

            if (produced == null || index >= produced.Length) return;

            int units = produced[index];

            if (units <= 0) return;

            // 整格取走，所以 units == had，DrainSlot 会把这一格清空
            int qua = QualityCraftOut.DrainSlot(ref pool[id], index, units, units);

            QualityRefineryRegistry.Tier tier = QualityRefineryRegistry.FindTier(pool[id].recipeId);

            int mint = tier == null || tier.Quality <= 0 ? 0 : tier.Quality * units;

            if (qua + mint <= 0) return;

            QualityAccess.SetChannel0(qua + mint);

            ReportOnce(units, (qua + mint) / units);
        }

        private static int _reported;

        /// <summary>
        /// 只报一次。<b>这一行存在的理由和别处一样</b>：不打的话，「玩家没走这条路」和
        /// 「走了但品质没接上」在日志里长得一模一样，而这一轮正是被这种二义性拖住的。
        /// </summary>
        private static void ReportOnce(int units, int perItem)
        {
            if (System.Threading.Interlocked.Exchange(ref _reported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质·产物图标取货：{units} 件带着每件 {perItem} 分进了背包。"
                + "**在这之前这条路上的品质恒为 0**——界面类被 preloader 整体跳过，"
                + "而这两个点击处理器是真的在搬货。");
        }
    }
}
