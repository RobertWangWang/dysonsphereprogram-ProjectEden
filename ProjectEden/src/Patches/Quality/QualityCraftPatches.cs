using System;
using System.Reflection;
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

            // **这一行专门用来区分「补丁没挂上」和「挂上了但条件不满足」。**
            // 只报一次，而且只在最外层——它出现就证明手搓这条路确实经过我们这里。
            if (System.Threading.Interlocked.Exchange(ref _enteredOnce, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    "物品品质·手搓：MechaForge.AddTaskIterate 的前置已跑到（补丁确实挂上了）。"
                    + "接下来该出现的是「手搓传递」或者「手搓诊断」二者之一。");
        }

        private static int _enteredOnce;

        /// <summary>
        /// 启动时报一行：这三个挂点到底有没有真的打上去。
        ///
        /// <b>这个类原先一行启动日志都没有，而那正是这轮卡住的原因。</b>
        /// 「手搓没品质」报上来时，日志里既没有成功行也没有诊断行，于是
        /// <b>「补丁没挂上」和「挂上了但这一次没人去点合成」长得一模一样</b>，
        /// 只能让玩家再进一次游戏去试——白白多花一轮。
        ///
        /// 读的是 Harmony 自己的补丁表（<c>GetAllPatchedMethods</c>），
        /// 也就是<b>实际生效的状态</b>，不是「我以为我注册了」。
        /// 这条规矩这个仓库已经付过五次以上往返，写在 CLAUDE.md 里。
        /// </summary>
        internal static void Report()
        {
            var add = false;
            var deliver = false;
            var take = false;

            foreach (MethodBase mb in Harmony.GetAllPatchedMethods())
            {
                if (mb?.DeclaringType == typeof(MechaForge))
                {
                    if (mb.Name == nameof(MechaForge.AddTaskIterate)) add = true;
                    if (mb.Name == nameof(MechaForge.TaskDeliver)) deliver = true;
                }
                else if (mb?.DeclaringType == typeof(StorageComponent) && mb.Name == nameof(StorageComponent.TakeItem))
                {
                    take = true;
                }
            }

            if (add && deliver && take)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "物品品质·手搓：三个挂点都已打上（AddTaskIterate / TaskDeliver / StorageComponent.TakeItem）。"
                    + "**之后如果手搓了却一行「手搓…」都没有，那就是合成面板根本没走 MechaForge.AddTaskIterate**，"
                    + "而不是补丁没生效。");

                return;
            }

            ProjectEdenPlugin.Log.LogError(
                $"物品品质·手搓：挂点没打全——AddTaskIterate={add}、TaskDeliver={deliver}、"
                + $"StorageComponent.TakeItem={take}。缺的那个对应的环节不会有任何效果，"
                + "而且不会报错。");
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

            // **每一条「没事可做」的出口都要能说话。** 这个后置原先有五个静默 return，
            // 于是「手搓没品质」报上来时日志里一个字都没有——分不清是补丁没跑、
            // 任务是 null、还是配方没被认出来。本仓库为这个形状已经付过五次以上往返，
            // 规矩写在 CLAUDE.md 里：有「什么都不做」的分支，就在同一次提交里给它一行。
            if (__result == null)
            {
                Trace("AddTaskIterate 返回 null（任务可能并进了已排队的任务）", 0, 0, 0);

                return;
            }

            if (__result.productCounts == null)
            {
                Trace("productCounts 是 null", __result.recipeId, 0, 0);

                return;
            }

            long products = 0;

            foreach (int n in __result.productCounts) products += (long)n * __result.count;

            if (products <= 0)
            {
                Trace("产出件数算出来是 0", __result.recipeId, products, 0);

                return;
            }

            // ── 手搓只**传递**品质，从不**创造** ───────────────────────────
            //
            // **这里曾经有一段「手搓提纯配方就按档位直接给分」的分支，它是死代码，
            // 已经删掉。** 留下这段话，是因为写它的理由听起来完全成立，而它错了。
            //
            // 当时的推理：玩家报「手搓的没有品质」，而品质的唯一注入点
            // <see cref="QualityRefineryPatches.OnProduced"/> 挂在「产物落进提纯厂自己的
            // 物流站槽位」那一步，手搓走 MechaForge → TryAddItemToPackage，够不着
            // StationStore；于是同一条配方在机器上有品质、手搓没有，是「每步都成功、
            // 功能却不在」。推理本身没问题，前提错了——<b>玩家根本搓不了提纯配方</b>。
            //
            // 实测（原版 IL，不是推断）：<c>UIReplicatorWindow.OnOkButtonClick</c>
            // IL 0138 <c>ldfld RecipeProto::Handcraft</c> / 013D <c>brtrue</c>，假就弹
            // 「该配方 X 生产」并在 <b>IL 016A</b> 返回，而 <c>AddTask</c> 在 <b>IL 01DE</b>
            // ——在那个 return 的后面。提纯配方来自 ores.json，<c>OreRegistry</c> 给
            // 所有这类配方写的是 <c>Handcraft = false</c>，所以那个分支一次也跑不到。
            //
            // <b>连带纠正 CLAUDE.md 里一条只对了一半的撤回。</b>那条说
            // 「UIReplicatorWindow 并不以 Handcraft 为门槛」——对 <c>RefreshRecipeIcons</c>
            // （画）成立，它只拿 Handcraft 挑灰不灰；对 <c>OnOkButtonClick</c>（做）不成立，
            // 那里是硬闸门。**读了一个方法就推广到整个窗口**，正是本仓库自己记着的
            // 「枚举出口再下结论」。
            //
            // 所以品质的来源仍然只有提纯厂一处，和设计一致（启动日志里那句
            // 「来源只有提纯厂」说的就是这件事）。手搓在这条链上的职责只有一个：
            // 拿带品质的料搓东西时，别把品质弄丢。
            if (_items <= 0 || _points <= 0)
            {
                Trace("投入没带品质（手搓只传递、不创造品质）", __result.recipeId, products, 0);

                return;
            }

            // **除的是投入件数，不是产出件数——这就是「按件数加权平均」。**
            //
            // 曾经这里写的是 `_points / products`（投入总分 ÷ 产出件数），也就是求和规则，
            // 后面还跟着一个夹在 100 的夹子。两处都改掉了，理由见
            // <see cref="QualityCraftFlowPatches"/> 的类注释：求和会让合成**凭配方比例造品质**
            // （多件变少件，每件分数必然往上翻），而那和「品质的来源只有提纯厂」直接冲突。
            //
            // 夹子一并去掉，因为平均值**构造上**就超不过最好的那种料——
            // 留一个永远不会触发的夹子，只会在真出事的那天把问题盖住。
            var perItem = (int)(_points / _items);

            if (perItem <= 0)
            {
                Trace("算出来每件 0 分", __result.recipeId, products, perItem);

                return;
            }

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
            if (task?.produced == null || task.produced.Length == 0)
            {
                TraceDeliver("交货时 produced 是空的", 0, 0, 0);

                return;
            }

            if (!PerProduct.TryGetValue(task, out StrongBox<int> perItem) || perItem.Value <= 0)
            {
                // **这一条最值得看**：任务排队时算出的每件分数没能跟到交货这一步。
                // ConditionalWeakTable 按对象身份查——排队和交货如果不是同一个
                // ForgeTask 实例（比如中途被合并、被复制），这里就永远查不到。
                TraceDeliver("查不到这个任务的每件分数（排队时没算出来，或者交货的不是同一个任务对象）",
                    task.recipeId, task.produced[0], 0);

                return;
            }

            int delivering = task.produced[0];

            if (delivering <= 0)
            {
                TraceDeliver("这一批交货件数是 0", task.recipeId, delivering, perItem.Value);

                return;
            }

            if (QualityAccess.SetChannel0 == null)
            {
                TraceDeliver("侧信道写不进去（SetChannel0 为 null）", task.recipeId, delivering, perItem.Value);

                return;
            }

            QualityAccess.SetChannel0(perItem.Value * delivering);

            if (System.Threading.Interlocked.Exchange(ref _deliveredOnce, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质·手搓交货：配方 {task.recipeId} 交 {delivering} 件 × 每件 {perItem.Value} 分 "
                + $"= {perItem.Value * delivering} 分已写进侧信道，接下来由 TryAddItemToPackage 带进背包那一格。");
        }

        private static int _deliveredOnce;
        private static int _tracedDeliver;

        private static void TraceDeliver(string why, int recipeId, int delivering, int perItem)
        {
            if (_tracedDeliver >= 4) return;

            _tracedDeliver++;

            ProjectEdenPlugin.Log.LogWarning(
                $"物品品质·手搓交货诊断 #{_tracedDeliver}：{why}。"
                + $"配方 {recipeId}、本批 {delivering} 件、每件 {perItem} 分。");
        }

        private static int _traced;

        /// <summary>
        /// 把这次手搓走到哪一步、看见了什么，原样打出来。
        ///
        /// <b>它报的是「我什么都没做」的那几条路</b>——成功那条由 <see cref="ReportOnce"/> 报。
        /// 两者加起来，任何一次手搓都必然在日志里留下一行，于是「补丁没跑」和
        /// 「补丁跑了但条件不满足」永远分得开。
        /// </summary>
        private static void Trace(string why, int recipeId, long products, int perItem)
        {
            if (_traced >= 6) return;

            _traced++;

            ProjectEdenPlugin.Log.LogWarning(
                $"物品品质·手搓诊断 #{_traced}：{why}。配方 {recipeId}、产出 {products} 件、"
                + $"每件 {perItem} 分；本单投入 {_items} 件 / {_points} 分。"
                + "（手搓只传递品质，不创造。**提纯配方是 Handcraft=false，在合成面板上按不动**"
                + "——点了只会弹「该配方 同位提纯厂 生产」，品质只能从提纯厂里出来。）");
        }

        private static int _reported;

        private static void ReportOnce(ForgeTask task, int perItem, long products)
        {
            if (_reported >= 3) return;

            _reported++;

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质·手搓传递 #{_reported}：配方 {task.recipeId} ×{task.count}，" +
                $"投入 {_items} 件合计 {_points} 分 → 产出 {products} 件，每件 {perItem} 分" +
                $"（= {_points} ÷ {_items}，**按投入件数平均，和产出几件无关**）。");
        }
    }
}
