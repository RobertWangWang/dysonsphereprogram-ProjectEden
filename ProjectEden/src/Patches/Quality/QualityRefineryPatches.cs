using System.Threading;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 品质的<b>注入点</b>：提纯配方的产物落进提纯厂自己的物流站槽位时，按配方声明的
    /// 「每件品质分」把点数加进 <c>StationStore.qua</c>。
    ///
    /// <b>为什么挂在这里而不是让配方本身带品质。</b> 原版装配逻辑做不到：
    /// <c>AssemblerComponent</c> 只有输入侧有品质（<c>incServed</c> 的孪生），
    /// 产物侧连 <c>incProduced</c> 都没有，没有可孪生的字段。补那一块是阶段 3 的活。
    /// 而产物落进本建筑自己的 Supply 槽位这一步，本仓库本来就拥有
    /// （<see cref="MegaStationPatches"/>），在那一行后面加一句就够——
    /// <c>StationStore.qua</c> 早就是主干道载荷、早就进存档、早就能在面板上看见。
    ///
    /// <b>注入的是「件数 × 每件分」，不是一个固定的桶。</b> 品质是可加量，
    /// 单件分数 = 总点数 ÷ 件数——所以按件数注入，槽位里原有的货和新产的货
    /// 自动按件数加权平均，不需要任何额外规则。掺了粗料就会把平均拉下来，
    /// 而那正是设计要的压力。
    /// </summary>
    internal static class QualityRefineryPatches
    {
        private static int _reported;

        /// <summary>
        /// 品质上限（单件）。设计稿 6.4 节：满分对应顶尖 +30%，线性插值。
        /// <b>注入时就夹住</b>，而不是等效果层去夹——越界的点数一旦进了槽位就会
        /// 随着搬运扩散出去，再想收回来就得追着整条主干道跑。
        /// </summary>
        internal const int MaxPerItem = 100;

        /// <summary>
        /// 产物进槽位之后调一次。<paramref name="units"/> 是这一次实际放进去的件数。
        ///
        /// 挂在 tick 路径上，所以：不分配、不插值字符串、查表用已经建好的字典。
        /// </summary>
        internal static void OnProduced(ref StationStore store, int recipeId, int units)
        {
            if (units <= 0 || !QualityAccess.Ready) return;

            // 三级提纯，线性扫三条比字典查一次还便宜，而且不分配。
            QualityRefineryRegistry.Tier tier = QualityRefineryRegistry.FindTier(recipeId);

            if (tier == null) return;

            int perItem = tier.Quality;

            if (perItem <= 0) return;

            int had = QualityAccess.GetStationQua(ref store);
            int add = units * perItem;

            // 夹在「这一格全部都是满分」之下。件数是加进去之后的值，所以上限跟着它走。
            int cap = store.count * MaxPerItem;
            int now = had + add;

            QualityAccess.SetStationQua(ref store, now > cap ? cap : now);

            if (Interlocked.Exchange(ref _reported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogWarning(
                $"物品品质：提纯厂第一次注入——配方 {recipeId} 产出 {units} 件，" +
                $"每件 {perItem} 分，槽位现在 {store.count} 件 / {QualityAccess.GetStationQua(ref store)} 分。");
        }

        /// <summary>
        /// 建表。<b>在 PostAddData 之后调</b>，那时配方 ID 已经被 LDBTool 敲定。
        ///
        /// 表本身住在 <see cref="QualityRefineryRegistry"/> 里——注入要的
        /// 「这条配方每件多少分」和面板要的「这一级能吃哪些矿」是同一张表的两面，
        /// 分两处建会让它们有机会不一致。状态行也在那边一并打。
        /// </summary>
        internal static void Build() => QualityRefineryRegistry.Build();
    }
}
