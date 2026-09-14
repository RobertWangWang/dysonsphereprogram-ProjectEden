using System;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// <b>手搓时把投入的品质传给产物。</b> 这是源头和效果之间缺的那一环。
    ///
    /// 实测发现的断点：<c>AssemblerComponent</c> 有 <c>served</c> / <c>incServed</c> /
    /// <c>quaServed</c>，投入带得进机器；但产物侧<b>只有 <c>produced</c>，没有
    /// <c>incProduced</c>，自然也没有 <c>quaProduced</c></b>——原版的产物本来就不带
    /// 增产点数，孪生变换没有东西可以镜像。于是
    /// <code>
    /// 同位提纯厂 → 铜块（有品质） → 合成 → 产物品质 = 0 → 建造 = 0 分
    /// </code>
    /// 设计稿 §5.1「产物点数 = 各投入点数之和」是<b>意图，不是实现</b>。
    /// 而建筑物品必然是合成出来的，所以效果层在这一环打通之前拿不到任何非零输入。
    ///
    /// <b>先做手搓，因为它是唯一不需要新字段的合成路径。</b> 手搓的投入和产出两头都在
    /// 机甲背包里，而 <c>GRID.qua</c> 早就有了——纯 mod 侧，整条链一次进游戏就能验穿：
    /// 提纯铜块 → 手搓一台建筑 → 建造 → 省电。装配台批量生产要给产物加孪生字段，
    /// 那是 preloader 的活，等这条链验穿了再上。
    ///
    /// 两个挂点（都实测过签名，都不是重载）：
    /// <list type="bullet">
    /// <item><c>MechaForge.AddTaskIterate</c> 排任务时从背包扣料，底下走
    /// <c>StorageComponent.TakeItem</c>——<b>它写 Q0</b>，品质白拿；</item>
    /// <item><c>MechaForge.TaskDeliver</c> 交货，底下走
    /// <c>Player.TryAddItemToPackage</c>——<b>它读 Q0</b>，所以在它之前写好就行。
    /// 这正是侧信道的既定协议：调用方在调用前写。</item>
    /// </list>
    /// </summary>
    [HarmonyPatch]
    internal static class QualityCraftPatches
    {
        /// <summary>
        /// 扣料范围。<c>StorageComponent.TakeItem</c> 全游戏到处都在调
        /// （补燃料、补弹药、补曲速器……），不圈范围就会把补给也算成合成投入。
        /// </summary>
        [ThreadStatic] private static int _depth;

        [ThreadStatic] private static int _items;
        [ThreadStatic] private static long _points;

        /// <summary>
        /// 每个合成任务算出来的「每件产物多少分」。
        ///
        /// 用 <see cref="ConditionalWeakTable{TKey,TValue}"/> 而不是普通字典：任务是
        /// 引擎自己的对象，什么时候被丢掉我们管不着，普通字典会把它们全都钉住不放。
        ///
        /// <b>不进存档。</b> 排着队还没做完的任务，存盘再读回来品质会退回 0——
        /// 那是一次合成的损失，而为它加一个存档分支不划算。有界，写在明处。
        /// </summary>
        private static readonly ConditionalWeakTable<ForgeTask, StrongBox<int>> PerProduct =
            new ConditionalWeakTable<ForgeTask, StrongBox<int>>();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MechaForge), nameof(MechaForge.AddTaskIterate))]
        private static void AddTaskIterate_Prefix()
        {
            if (_depth++ > 0) return;

            _items = 0;
            _points = 0;
        }

        /// <summary>
        /// 任务排好了：把这一单投入的总点数摊到它将要产出的总件数上。
        ///
        /// <b>这一步就是设计稿里的「加权平均是免费的」</b>——可加量求和再除以产出件数，
        /// 本身就是加权平均，不需要额外规则。它只会稀释，永远不会超过最好的那个投入，
        /// 所以也不可能凭空造出满分货。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MechaForge), nameof(MechaForge.AddTaskIterate))]
        private static void AddTaskIterate_Postfix(ForgeTask __result)
        {
            if (--_depth > 0) return;
            if (_depth < 0) _depth = 0;

            if (__result?.productCounts == null || _items <= 0 || _points <= 0) return;

            long products = 0;

            foreach (int n in __result.productCounts) products += (long)n * __result.count;

            if (products <= 0) return;

            var perItem = (int)(_points / products);

            if (perItem > QualityRefineryPatches.MaxPerItem) perItem = QualityRefineryPatches.MaxPerItem;
            if (perItem <= 0) return;

            PerProduct.Remove(__result);
            PerProduct.Add(__result, new StrongBox<int>(perItem));

            ReportOnce(__result, perItem, products);
        }

        /// <summary>
        /// 扣料。后置时 <c>inc</c> 已经填好，品质在 Q0 里。<b>读而不清</b>——
        /// 游戏自己的调用点后面还有一次 preloader 插进去的读。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StorageComponent), nameof(StorageComponent.TakeItem))]
        private static void TakeItem_Postfix(int __result)
        {
            if (_depth <= 0 || __result <= 0) return;

            _items += __result;

            if (QualityAccess.ChannelReady) _points += QualityAccess.GetChannel0();
        }

        /// <summary>
        /// 交货：在 <c>TryAddItemToPackage</c> 之前把这一批的品质写进侧信道，
        /// 它会连着件数一起进背包那一格。
        ///
        /// <b>已知的有界损失：多产物配方只有第一种产物拿得到品质。</b>
        /// preloader 给每个调用点后面都插了「调用后擦除」（那是当初堵品质膨胀的那一刀），
        /// 所以 <c>TaskDeliver</c> 里第二次 <c>TryAddItemToPackage</c> 读到的是 0。
        /// 要做全就得转译这个方法体，而绝大多数手搓配方只有一种产物——
        /// 代价和收益不成比例。丢而不是发明，方向是对的。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MechaForge), nameof(MechaForge.TaskDeliver))]
        private static void TaskDeliver_Prefix(ForgeTask task)
        {
            if (task?.produced == null || task.produced.Length == 0) return;
            if (!PerProduct.TryGetValue(task, out StrongBox<int> perItem) || perItem.Value <= 0) return;

            int delivering = task.produced[0];

            if (delivering <= 0) return;

            if (QualityAccess.SetChannel0 != null)
                QualityAccess.SetChannel0(perItem.Value * delivering);
        }

        private static int _reported;

        private static void ReportOnce(ForgeTask task, int perItem, long products)
        {
            if (_reported >= 3) return;

            _reported++;

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质·手搓传递 #{_reported}：配方 {task.recipeId} ×{task.count}，" +
                $"投入 {_items} 件合计 {_points} 分 → 产出 {products} 件，每件 {perItem} 分。" +
                "（装配台批量生产还传不了品质：产物侧没有孪生字段，那要动 preloader。）");
        }
    }
}
