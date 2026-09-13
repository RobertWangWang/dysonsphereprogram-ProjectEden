using System.Collections.Generic;
using System.Text;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 提纯线的<b>万用配方模板</b>：三级提纯各是一条配方，提哪一种金属由<b>每台建筑</b>
    /// 自己选，候选表是<b>推导出来的</b>，不枚举。
    ///
    /// <b>提纯吃的是金属块，不是矿石。</b> 这一条是被本 mod 自己的大型采矿机逼出来的：
    /// 它对铜矿、硅石、钛石和三种稀有矿<b>直接输出产物</b>（<c>advancedminer.json</c> 的
    /// <c>productMap</c>），所以玩家手里根本就没有那几种矿石——按矿石进料的话，
    /// 铜这一条线连原料都凑不出来。金属块则是每一种矿最后都会变成的东西，
    /// 不管中间经没经过熔炉。
    ///
    /// <b>于是原料和产物是同一种物品，而这恰恰是品质系统存在的理由。</b>
    /// 「提纯的铜」和「粗铜」不是两种物品——DSP 里没有任何逐件或逐堆的元数据
    /// （<c>Cargo</c> 结构体是满的，四维合金属性、<c>inc</c> 字节都撞过这堵墙），
    /// 所以差别只能记在<b>容器</b>上，也就是 <c>StationStore.qua</c>。
    /// 每种金属再开一个「高纯 X」物品就是在枚举，而枚举正是万用模板要消掉的东西。
    ///
    /// 代价有两个，都写在明处：
    /// <list type="bullet">
    /// <item>储物格要能区分「进料的那一格」和「出货的那一格」——
    ///       见 <c>MegaStationPatches.FindSlot</c> 的方向参数，那一刀是这条设计的前提。</item>
    /// <item>提纯过的金属再送回去还能再提一遍，白白损失一道收率。<b>但它不是漏洞</b>：
    ///       注入是「每件固定分数」，不是在原有品质上加，所以反复提纯<b>不叠加</b>，
    ///       只亏料。设计稿 5.2 节本来就是这么定的。</item>
    /// </list>
    ///
    /// <b>为什么不用 preloader。</b> 「万用配方」听着像要动类型结构，其实不用：
    /// <c>RecipeExecuteData.requires[i]</c> 和 <c>products[0]</c> 都是 <c>int</c>，
    /// 而这个对象是可以逐台建筑换掉的——本仓库的合金弹药（<see cref="AmmoPairPatches"/>）、
    /// 烧结析出（<see cref="CompositeOutputPatches"/>）走的就是这条路。preloader 能改的是
    /// 字段、字段类型、方法签名、结构体大小；逐台建筑换配方里的值是 Harmony 的活。
    ///
    /// <b>候选表是推出来的，不是列出来的。</b> 列清单意味着每加一种矿就要改配置，
    /// 而「哪种矿炼出哪种金属」这件事游戏自己已经知道：
    ///
    /// <list type="number">
    /// <item>先取所有矿脉的 <c>VeinProto.MiningItem</c>——这正是「是矿石」的定义，
    ///       本 mod 自己的八种矿也自动在内（它们都注册了矿脉原型）。</item>
    /// <item>每种矿炼出什么，取<b>只吃这一种矿</b>的那条原版配方的第一个产物。
    ///       这是 <c>advancedminer.json</c> 的 <c>productMap</c> 早就在用的推导，
    ///       可燃冰→石墨烯、分形硅石→晶格硅、刺笋结晶→碳纳米管在原版正好都是这个形状。</item>
    /// <item>推不出来的（本 mod 的矿要配还原剂，一条配方吃两样）再退一步：
    ///       去 <see cref="OreRegistry"/> 拿这个矿种自己声明的锭。</item>
    /// <item>去重之后，<b>这些产物</b>就是可提纯的金属表。矿石自己不在表里。</item>
    /// </list>
    ///
    /// <b>一种矿有两条「只吃它」的配方时要有确定的取舍，否则铁会被丢掉。</b>
    /// 铁矿同时能出铁块和磁铁，两条都是 1 进 1 出——「有歧义就跳过」会把整条提纯线
    /// 最核心的一种金属排除在外。现在的次序是：先按<b>每炉吃几个矿</b>取小（最直接的那条），
    /// 再按<b>产物物品 ID</b> 取小（原版的 ID 按进度排，锭永远排在衍生品前面：
    /// 铁块 1101 &lt; 磁铁 1102、石材 1108 &lt; 玻璃 1110）。
    /// <b>整张表每次启动都打出来</b>，推错了一眼能看见——这是唯一的验收手段，
    /// 因为原版物品住在 <c>resources.assets</c> 里，离线读不到。
    /// </summary>
    internal static class QualityRefineryRegistry
    {
        /// <summary>一种可以提纯的金属。原料和产物都是它。</summary>
        internal sealed class Feed
        {
            internal int ItemId;
            internal string Name;

            /// <summary>它是从哪种矿来的，只用来写日志——推导过程的凭据。</summary>
            internal string FromOre;

            /// <summary>
            /// 是靠哪一级规则定下来的。<b>必须打进日志</b>：原版配方表在 resources.assets 里、
            /// 离线枚举不到，所以「这一格为什么是它」只有运行时能回答，
            /// 而「氢」和「增产剂 Mk.I」两次混进来都是在这里现形的。
            /// </summary>
            internal string How;
        }

        /// <summary>一级提纯。三级各一条配方，共用同一张候选表。</summary>
        internal sealed class Tier
        {
            internal int RecipeId;
            internal string Name;

            /// <summary>产物每件带多少品质分。</summary>
            internal int Quality;

            /// <summary>收率，0~1。原料和产物同种，所以它就是「投一百件出几件」。</summary>
            internal double Yield;

            /// <summary>每炉投多少件。取自配方原型，逐台建筑不变。</summary>
            internal int InputUnits;

            internal int TimeSpend;

            /// <summary>配方原型上写着的那种金属——没存过选择的建筑用它。</summary>
            internal int DefaultItemId;
        }

        internal static readonly List<Feed> Feeds = new List<Feed>();
        internal static readonly List<Tier> Tiers = new List<Tier>();

        internal static bool Ready => Tiers.Count > 0 && Feeds.Count > 0;

        internal static Tier FindTier(int recipeId)
        {
            for (var i = 0; i < Tiers.Count; i++)
                if (Tiers[i].RecipeId == recipeId)
                    return Tiers[i];

            return null;
        }

        internal static Feed FindFeed(int itemId)
        {
            for (var i = 0; i < Feeds.Count; i++)
                if (Feeds[i].ItemId == itemId)
                    return Feeds[i];

            return null;
        }

        /// <summary>这一级每炉出几件。至少 1 件——0 件的配方是死配方。</summary>
        internal static int OutputOf(Tier tier)
        {
            if (tier == null) return 1;

            var n = (int)(tier.InputUnits * tier.Yield);

            return n < 1 ? 1 : n;
        }

        // ── 建表 ──────────────────────────────────────────────

        /// <summary>
        /// <b>在 PostAddData 之后调</b>：那时配方 ID 已经被 LDBTool 敲定，矿脉原型也都进了 LDB。
        /// </summary>
        internal static void Build()
        {
            Tiers.Clear();
            Feeds.Clear();

            BuildTiers();

            if (Tiers.Count > 0) BuildFeeds();

            Report();
        }

        private static void BuildTiers()
        {
            OreConfig cfg = OreRegistry.Config;

            if (cfg?.recipes == null) return;

            CollectTiers(cfg.recipes);

            if (cfg.ores == null) return;

            foreach (OreEntry ore in cfg.ores)
                if (ore?.recipes != null)
                    CollectTiers(ore.recipes);
        }

        private static void CollectTiers(IEnumerable<OreRecipeEntry> list)
        {
            foreach (OreRecipeEntry r in list)
            {
                if (r == null || !r.enabled || r.quality <= 0) continue;

                RecipeProto proto = ByName(r.name);

                if (proto == null)
                {
                    // 按名字找不到就整条跳过并报错：查表用的是运行时的 recipeId，
                    // 对不上的话品质恒为 0，而那看起来像「提纯没生效」而不是「配方没找到」。
                    ProjectEdenPlugin.Log.LogError(
                        $"物品品质：提纯配方「{r.name}」在 LDB 里按名字找不到，这一级不会生效。" +
                        "改过配方名的话，LDBTool 那两个 cfg 里的旧条目要删。");

                    continue;
                }

                if (proto.Items == null || proto.Items.Length < 1
                    || proto.ItemCounts == null || proto.ItemCounts.Length < 1
                    || proto.Results == null || proto.Results.Length < 1)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"物品品质：提纯配方「{r.name}」的原料或产物是空的，这一级不会生效。");

                    continue;
                }

                if (proto.Items[0] != proto.Results[0])
                    ProjectEdenPlugin.Log.LogWarning(
                        $"物品品质：提纯配方「{r.name}」第一味原料和第一样产物不是同一种物品" +
                        "——提纯是「同种金属，只差品质」，配置里这两格应当写同一样东西。" +
                        "逐台建筑贴上去的时候会按同一种金属强行对齐，合成面板上的展示则会是错的。");

                Tiers.Add(new Tier
                {
                    RecipeId = proto.ID,
                    Name = proto.Name,
                    Quality = r.quality > QualityRefineryPatches.MaxPerItem
                        ? QualityRefineryPatches.MaxPerItem
                        : r.quality,
                    Yield = r.yield > 0 ? r.yield : 1d,
                    InputUnits = proto.ItemCounts[0],
                    TimeSpend = proto.TimeSpend > 0 ? proto.TimeSpend : 600,
                    DefaultItemId = proto.Results[0]
                });
            }
        }

        private static RecipeProto ByName(string name)
        {
            RecipeProto[] all = LDB.recipes?.dataArray;

            if (all == null || string.IsNullOrEmpty(name)) return null;

            foreach (RecipeProto p in all)
                if (p != null && p.Name == name)
                    return p;

            return null;
        }

        // ── 候选金属的推导 ────────────────────────────────────

        private static readonly List<string> Skipped = new List<string>();

        private static void BuildFeeds()
        {
            Skipped.Clear();

            VeinProto[] veins = LDB.veins?.dataArray;

            if (veins == null)
            {
                ProjectEdenPlugin.Log.LogError("物品品质：LDB.veins 读不到，提纯候选表建不起来。");

                return;
            }

            var seenOre = new HashSet<int>();
            var seenProduct = new HashSet<int>();

            foreach (VeinProto v in veins)
            {
                if (v == null || v.MiningItem <= 0 || !seenOre.Add(v.MiningItem)) continue;

                ItemProto ore = LDB.items.Select(v.MiningItem);

                if (ore == null) continue;

                // **流体不算矿。** 原油涌泉也是一种矿脉，它的 MiningItem 是原油；
                // 提纯是固体冶金工序，液体和气体没有「锭」可言。
                if (ore.IsFluid)
                {
                    Skipped.Add(ore.name + "（流体）");

                    continue;
                }

                int product = DeriveProduct(v.MiningItem, out string how);

                if (product <= 0 || product == v.MiningItem)
                {
                    Skipped.Add(ore.name);

                    continue;
                }

                ItemProto made = LDB.items.Select(product);

                if (made == null || made.IsFluid)
                {
                    Skipped.Add(ore.name);

                    continue;
                }

                // 同一种金属可能有好几种矿来源（本 mod 的钒有矿脉也有残渣提取），
                // 只进一次表，不然选料行里会出现两个一模一样的名字
                if (!seenProduct.Add(product)) continue;

                Feeds.Add(new Feed
                {
                    ItemId = product, Name = made.name, FromOre = ore.name, How = how
                });
            }

            Feeds.Sort((a, b) => a.ItemId.CompareTo(b.ItemId));
        }

        /// <summary>
        /// 这种矿炼出什么。<b>三级优先，前两级是本仓库已经维护着的表，第三级才是现场推导。</b>
        ///
        /// <list type="number">
        /// <item><b><c>advancedminer.json</c> 的 <c>productMap</c></b>：大型采矿机直接出产物用的就是它，
        ///       条目本身带着「这种矿有没有唯一明显的下游」这条判据和它的理由。
        ///       同一个问题已经有一张经过推敲的答案表，再推一遍只会推出第二个答案。</item>
        /// <item><b><see cref="OreRegistry"/> 声明的锭</b>：本 mod 的矿，矿种自己就写了它炼出什么。</item>
        /// <item>都没有才去扫原版配方，而且<b>只认熔炉（<c>ERecipeType.Smelt</c>）</b>。</item>
        /// </list>
        ///
        /// <b>第三级的熔炉判据是被实测逼出来的，两次。</b>
        /// 第一版取「只吃这一种矿」的配方的 <c>Results[0]</c>，选料表里冒出来<b>氢</b>——
        /// 可燃冰那条配方的第一个产物就是氢，原版把副产物排在前面是常事。
        /// 改成「第一个固体产物」之后又冒出来<b>增产剂 Mk.I</b>：它是煤矿 ×1 出 1 个，
        /// 比高能石墨的煤矿 ×2 出 1 个「更直接」，于是赢了那条按投料量取小的比较。
        ///
        /// 两次都是同一个错误——<b>把「结构上像」当成了「语义上是」</b>。
        /// 「只吃一种矿的配方」在原版里根本不止冶炼：还有喷涂剂、磁铁、玻璃。
        /// 真正在问的问题是「这是不是一道冶炼」，而那正是 <c>ERecipeType.Smelt</c> 本身。
        ///
        /// <b>原版配方表在 <c>resources.assets</c> 里，离线读不到</b>（CLAUDE.md 记着），
        /// 所以这一级只能靠启动日志验收——推导表每条都会打出是哪条配方定的。
        /// </summary>
        private static int DeriveProduct(int oreId, out string how)
        {
            how = null;

            // 一、大型采矿机的产物映射
            if (AdvancedMinerPatches.ProductMap != null
                && AdvancedMinerPatches.ProductMap.TryGetValue(oreId, out int mapped)
                && mapped > 0)
            {
                how = "采矿机产物映射";

                return mapped;
            }

            // 二、本 mod 的矿种自己声明的锭
            for (var i = 0; i < OreRegistry.Ores.Count; i++)
            {
                OreRegistry.Ore owner = OreRegistry.Ores[i];

                if (owner.OreItemId != oreId || !owner.HasIngot || owner.IngotItemId <= 0) continue;

                how = "矿种声明的锭";

                return owner.IngotItemId;
            }

            // 三、只吃这一种矿的**熔炉**配方
            RecipeProto[] all = LDB.recipes?.dataArray;

            if (all == null) return 0;

            var product = 0;
            var orePer = 0;
            string from = null;

            foreach (RecipeProto r in all)
            {
                if (r?.Items == null || r.Results == null || r.ItemCounts == null) continue;

                // **只认冶炼。** 喷涂剂（组装）、以及任何非冶炼的一味配方都在这一句之外。
                if (r.Type != ERecipeType.Smelt) continue;

                if (r.Items.Length != 1 || r.Items[0] != oreId) continue;
                if (r.ItemCounts.Length < 1 || r.ItemCounts[0] <= 0) continue;

                // 提纯配方自己不能当推导依据——它现在的原料就是金属块，
                // 万一哪天某一级的试剂被去掉，这一句挡住自指
                if (FindTier(r.ID) != null) continue;

                int solid = FirstSolid(r.Results, oreId);

                if (solid <= 0) continue;

                // **先比产物 ID，再比投料量。** 顺序反过来写过一版，正是它让增产剂 Mk.I
                // 赢了高能石墨——煤矿 ×1 出 1 个比煤矿 ×2 出 1 个「更直接」，
                // 于是「最直接」这条判据把一瓶喷涂剂选成了煤的冶炼产物。
                //
                // 原版物品 ID 是按进度排的，**基础锭永远排在它的衍生品前面**：
                // 铁块 1101 < 磁铁 1102、石材 1108 < 玻璃 1110、高能石墨 1109 < 增产剂 Mk.I 1141。
                // 已知的三组歧义它全部答对，而且不依赖「熔炉」判据是否恰好把杂项挡住——
                // 两道判据都对才算稳，只靠一道是在赌。
                var better = product == 0
                             || solid < product
                             || (solid == product && r.ItemCounts[0] < orePer);

                if (!better) continue;

                product = solid;
                orePer = r.ItemCounts[0];
                from = r.name;
            }

            if (product > 0) how = "熔炉配方「" + from + "」";

            return product;
        }

        /// <summary>这一串产物里第一个不是流体、也不是矿石本身的。都不合格就返回 0。</summary>
        private static int FirstSolid(int[] results, int oreId)
        {
            if (results == null) return 0;

            for (var i = 0; i < results.Length; i++)
            {
                if (results[i] <= 0 || results[i] == oreId) continue;

                ItemProto p = LDB.items.Select(results[i]);

                if (p != null && !p.IsFluid) return results[i];
            }

            return 0;
        }

        // ── 状态行 ────────────────────────────────────────────

        /// <summary>
        /// <b>推导表每次启动都打出来，包括推不出来的那些。</b>
        ///
        /// 这不是装饰：原版物品住在 <c>resources.assets</c> 里，离线枚举不到，
        /// 所以「铁矿推成了磁铁」这种错在代码里看不出来，只能在日志里看出来。
        /// 只在成功时打印还会让「没配提纯配方」和「这段代码根本没进 DLL」长得一模一样——
        /// 本仓库已经为这个形状付过五次往返。
        /// </summary>
        private static void Report()
        {
            if (Tiers.Count == 0)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质：**一条提纯配方都没有**——品质将永远是 0。" +
                    "检查 ores.json 里那几条配方的 quality 字段。");

                return;
            }

            if (Feeds.Count == 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"物品品质：提纯配方有 {Tiers.Count} 条，但**一种可提纯的金属都没推出来**——" +
                    "提纯厂会一直卡在配方原型写死的那一种上。");

                return;
            }

            // 推导的第一级读的是采矿机那张表，而它在 MegaBuildingRegistry.OnPostAddData 里建，
            // 靠的是注册顺序（那一个挂在 119 行，这一个挂在 152 行）。顺序一旦被挪动，
            // 表会是空的、推导会整体回落到第三级，而那不会报错——所以在这里核一次。
            if (AdvancedMinerPatches.ProductMap == null || AdvancedMinerPatches.ProductMap.Count == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质：大型采矿机的产物映射是空的，提纯候选表只能靠扫熔炉配方推。" +
                    "如果它本该有内容，检查 PostAddDataAction 的注册顺序。");

            var sb = new StringBuilder();

            sb.Append("物品品质：提纯线万用模板已建——").Append(Tiers.Count).Append(" 级 × ")
              .Append(Feeds.Count).Append(" 种金属。原料和产物同种，差别只在品质。")
              .Append("\n  可提纯的金属（由哪种矿推出来的）：");

            foreach (Feed f in Feeds)
                sb.Append("\n    ").Append(f.Name).Append("　←　").Append(f.FromOre)
                  .Append("　［").Append(f.How ?? "?").Append("］");

            foreach (Tier t in Tiers)
                sb.Append("\n  ").Append(t.Name).Append("：每炉 ").Append(t.InputUnits)
                  .Append(" 件进、").Append(OutputOf(t)).Append(" 件出（收率 ")
                  .Append((t.Yield * 100d).ToString("0.#")).Append("%），每件 ")
                  .Append(t.Quality).Append(" 分，").Append(t.TimeSpend / 60f).Append(" 秒");

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());

            if (Skipped.Count > 0)
                ProjectEdenPlugin.Log.LogInfo(
                    "物品品质：这些矿推不出可提纯的产物，不进候选（没有「只吃这一种矿」的原版配方，" +
                    "也没有本 mod 声明的锭）：" + string.Join("、", Skipped.ToArray()));
        }
    }
}
