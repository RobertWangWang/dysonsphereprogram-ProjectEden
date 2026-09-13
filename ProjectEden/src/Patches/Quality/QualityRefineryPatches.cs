using System.Collections.Generic;
using System.Threading;
using ProjectEden.Utils;

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
        /// <summary>配方 ID → 产物每件多少品质分。只有提纯配方在里面。</summary>
        private static Dictionary<int, int> _byRecipe;

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

            Dictionary<int, int> map = _byRecipe;

            if (map == null || !map.TryGetValue(recipeId, out int perItem) || perItem <= 0) return;

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
        /// 按<b>配方名</b>去 LDB 把实际 ID 读回来。名字是 LDBTool 记 ID 的键，
        /// 所以它比配置里写的 ID 更接近运行时的事实。
        /// </summary>
        private static int ResolveId(OreRecipeEntry r)
        {
            RecipeProto[] all = LDB.recipes?.dataArray;

            if (all == null || string.IsNullOrEmpty(r.name)) return 0;

            foreach (RecipeProto p in all)
                if (p != null && p.Name == r.name)
                    return p.ID;

            return 0;
        }

        /// <summary>
        /// 建表。<b>在 PostAddData 之后调</b>，那时配方 ID 已经被 LDBTool 敲定。
        ///
        /// 状态行三种情况都打：没有提纯配方、字段不在、建好了各多少条——
        /// 只在成功时打日志会让「没配」和「没装」长得一模一样。
        /// </summary>
        internal static void Build()
        {
            if (OreRegistry.Config?.recipes == null)
            {
                ProjectEdenPlugin.Log.LogInfo("物品品质：ores.json 没读到，提纯注入表为空。");

                return;
            }

            var map = new Dictionary<int, int>();
            var missing = 0;

            foreach (OreRecipeEntry r in OreRegistry.Config.recipes)
            {
                if (r == null || !r.enabled || r.quality <= 0) continue;

                // **按名字去 LDB 把实际 ID 读回来，不用配置里写的那个。**
                // LDBTool 的 CustomID.cfg 按显示名记 ID，并在后面的每次启动把记下的值
                // 写回原型——所以配置里那个数在这台机器上可能早就不是它了。
                // 查表用的是 component.recipeId，那是**运行时**的 ID，对不上就一条都注入不了,
                // 而且不会报错：品质恒为 0，看起来像提纯没生效。
                int id = ResolveId(r);

                if (id <= 0) { missing++; continue; }

                map[id] = r.quality > MaxPerItem ? MaxPerItem : r.quality;
            }

            if (missing > 0)
                ProjectEdenPlugin.Log.LogError(
                    $"物品品质：有 {missing} 条提纯配方在 LDB 里按名字找不到，" +
                    "它们产出的东西不会带品质。改过配方名的话，LDBTool 那两个 cfg 里的旧条目要删。");

            _byRecipe = map;

            if (map.Count == 0)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质：**一条提纯配方都没有**——品质将永远是 0。" +
                    "检查 ores.json 里那几条配方的 quality 字段。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质：提纯注入表已建，{map.Count} 条配方会产出带品质的产物" +
                $"（单件上限 {MaxPerItem} 分）。");
        }
    }
}
