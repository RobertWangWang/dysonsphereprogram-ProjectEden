using System.Collections.Generic;
using ProjectEden.Utils;
using xiaoye97;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 氧化还原燃烧厂的物品与配方：三档药柱 + 一条配方。
    ///
    /// <b>沿用合金弹药那套已经跑通的形状</b>：<c>requires[i]</c> 是 int，所以<b>一条</b>配方
    /// 就能按建筑吃不同的原料。12 种组合不各自成为配方——那样合成面板会把答案全列出来，
    /// 而且配方格也放不下（合成面板加不了行，<c>row &gt;= 8</c> 直接跳过）。
    ///
    /// <b>档次必须是枚举出来的物品，这堵墙和弹药伤害是同一堵。</b> 药柱的能量由
    /// <c>ItemProto.HeatValue</c> 承载，按 proto 存；DSP 里没有任何按堆 / 按件的元数据槽位
    /// （<c>Cargo</c> 那 32 字节已经排满，四维合金属性也撞的是它）。所以能量密度只能分档，
    /// 而<b>产量</b>走 <c>productCounts[0]</c>，连续，一个物品格都不占。
    ///
    /// <b>燃料位取 32。</b> <c>FuelSurvey</c> 实测原版已用掩码 15（1 化学 / 2 氘核 /
    /// 4 反物质 / 8 满蓄电器），可燃液体占了 16，32 是 bit 0~5 里最后一个空位。
    /// 给了 32 而不给 1，意思是「火力发电厂烧不了它」——药柱自带氧化剂，
    /// 塞进燃煤锅炉是炸膛不是发电。
    /// </summary>
    internal static class RedoxRegistry
    {
        /// <summary>一档药柱的运行时状态。</summary>
        internal class Tier
        {
            internal RedoxTierEntry Entry;
            internal int ItemId;
            internal int Grid;
        }

        /// <summary>一种还原剂 / 氧化剂的运行时状态。</summary>
        internal class Agent
        {
            internal RedoxAgentEntry Entry;
            internal int ItemId;

            /// <summary>还原剂：这件东西的热值（焦耳）。氧化剂恒为 0。</summary>
            internal long Heat;

            internal float Oxygen => Entry.oxygen;
            internal string Name => Entry.name;
        }

        internal static RedoxConfig Config { get; private set; }

        internal static readonly List<Tier> Tiers = new List<Tier>();
        internal static readonly List<Agent> Reducers = new List<Agent>();
        internal static readonly List<Agent> Oxidizers = new List<Agent>();

        internal static int RecipeId { get; private set; }

        /// <summary>燃料位。见类注释。</summary>
        internal const int FuelMask = 32;

        internal static bool Ready =>
            Tiers.Count > 0 && Reducers.Count > 0 && Oxidizers.Count > 0 && RecipeId > 0;

        internal static void Load()
        {
            Config = JsonHelper.Load<RedoxConfig>("redox");

            if (Config == null) ProjectEdenPlugin.Log.LogWarning("读不到 redox.json，氧化还原燃烧厂未启用");
            else if (!Config.enabled) ProjectEdenPlugin.Log.LogInfo("氧化还原燃烧厂已在配置里关闭");
        }

        // ── 注册 ──────────────────────────────────────────────

        internal static void OnPreAddData()
        {
            Tiers.Clear();

            if (Config == null || !Config.enabled || Config.tiers == null || Config.tiers.Length == 0) return;

            // 固体模板。iconFrom 不只是图标来源，它同时是 proto 模板：
            // DescFields 和 StackSize 都从它身上抄，缺了会在玩家划过物品时空引用崩溃，
            // 而且栈里点名的是别的 mod（钻头那次的教训）。
            ItemProto template = LDB.items.Select(1005) ?? LDB.items.Select(1006);

            if (template == null)
            {
                ProjectEdenPlugin.Log.LogError("找不到可当 proto 模板的原版固体物品，药柱未注册");

                return;
            }

            foreach (RedoxTierEntry entry in Config.tiers)
            {
                if (entry == null) continue;

                var tier = new Tier { Entry = entry };

                tier.ItemId = ProtoSlots.ResolveItemId(entry.itemId, entry.name);
                tier.Grid = ProtoSlots.ResolveGridIndex(0, entry.name, ProtoSlots.GridKind.Item, Pending);

                AddItem(tier, template);

                ProtoSlots.ReserveItemId(tier.ItemId);
                ProtoSlots.ReserveGrid(tier.Grid, ProtoSlots.GridKind.Item);

                Tiers.Add(tier);
            }

            AddRecipe(template);
        }

        private static bool Pending(int grid)
        {
            for (var i = 0; i < Tiers.Count; i++)
                if (Tiers[i].Grid == grid)
                    return true;

            return false;
        }

        private static void AddItem(Tier tier, ItemProto template)
        {
            var item = new ItemProto
            {
                ID = tier.ItemId,
                Name = tier.Entry.name,
                Description = tier.Entry.description,
                Type = template.Type,
                GridIndex = tier.Grid,
                StackSize = template.StackSize,
                IconPath = "Assets/projecteden/" + tier.Entry.key,
                IsFluid = false,
                CanBuild = false,
                BuildIndex = 0,
                // 药柱自带氧化剂，只有燃烧厂烧得了 —— 给 32 不给 1
                FuelType = FuelMask,
                HeatValue = tier.Entry.heatValue,
                Grade = 0,
                Upgrades = new int[0],
                DescFields = template.DescFields,
                // 坑 5：新开局会清空 recipeUnlocked，-1 让 ItemUnlocked 直接返回 true
                UnlockKey = -1,
                PreTechOverride = 0,
                prefabDesc = PrefabDesc.none,
            };

            item.name = tier.Entry.name;

            LDBTool.PreAddProto(item);
        }

        /// <summary>
        /// 一条配方。两个原料槽先放占位物品，真正吃哪两种由面板逐建筑改
        /// （改<b>值</b>不改<b>长度</b>——<c>AssemblerComponent.Export</c> 按
        /// <c>requires</c> / <c>products</c> 的长度决定写几个 served / produced，
        /// 长度一变存档就错位）。
        /// </summary>
        private static void AddRecipe(ItemProto template)
        {
            if (Tiers.Count == 0) return;

            RecipeId = ProtoSlots.ResolveRecipeId(Config.recipeId, "推进剂药柱");

            int grid = ProtoSlots.ResolveGridIndex(Config.recipeGridIndex, "推进剂药柱（配方）",
                ProtoSlots.GridKind.Recipe);

            var recipe = new RecipeProto
            {
                ID = RecipeId,
                Name = "推进剂药柱 · 压制",
                Description = "把一种还原剂和一种氧化剂压成药柱。配平了才烧得干净——" +
                              "氧化剂不够，烧不掉的燃料原样留在渣里；氧化剂过量，多出来的只是死重。",
                Type = (ERecipeType)Config.recipeType,
                Handcraft = false,
                Explicit = true,
                TimeSpend = Config.timeSpend > 0 ? Config.timeSpend : 120,
                Items = new[] { template.ID, template.ID },
                ItemCounts = new[] { Config.reducerParts > 0 ? Config.reducerParts : 8, 8 },
                Results = new[] { Tiers[0].ItemId },
                ResultCounts = new[] { 1 },
                GridIndex = grid,
                IconPath = "Assets/projecteden/" + Tiers[0].Entry.key,
                preTech = null,
            };

            recipe.name = recipe.Name;

            LDBTool.PreAddProto(recipe);

            MegaBuildingRegistry.RecipeIds.Add(RecipeId);

            ProtoSlots.ReserveRecipeId(RecipeId);
            ProtoSlots.ReserveGrid(grid, ProtoSlots.GridKind.Recipe);
        }

        // ── LDB 建好之后：解析原料、核对名字、把对照表打进日志 ──

        internal static void OnPostAddData()
        {
            Reducers.Clear();
            Oxidizers.Clear();

            if (Config == null || !Config.enabled || Tiers.Count == 0) return;

            Resolve(Config.reducers, Reducers, true);
            Resolve(Config.oxidizers, Oxidizers, false);

            if (Reducers.Count == 0 || Oxidizers.Count == 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    "氧化还原燃烧厂：还原剂 " + Reducers.Count + " 种、氧化剂 " + Oxidizers.Count +
                    " 种，至少各要一种，本次未启用");

                return;
            }

            ApplyDefaultToProto();

            DumpTable();
        }

        /// <summary>
        /// 把配方原型上的<b>占位原料</b>换成真正的默认一对。
        ///
        /// <b>为什么原型上会有占位料。</b> 配方是在 <c>PreAddDataAction</c> 注册的，
        /// 那时本 mod 的物品还一件都不在 LDB 里（<c>LDBTool.PreAddProto</c> 只是排队），
        /// 所以两个原料槽只能先放一件一定存在的原版固体。真正吃什么是<b>逐建筑</b>改
        /// <c>recipeExecuteData</c> 决定的，机器本身从第一刻起就是对的——
        /// <b>但合成面板画的是配方原型</b>，于是它一直显示「石矿 ×8 + 石矿 ×8」。
        ///
        /// 这正是本仓库记过的那种错：机器是对的、面板是错的，而两边读的是<b>不同的源</b>。
        ///
        /// <b>放在这里改是白送的。</b> <c>RecipeProto.InitRecipeItems</c> 会把整张
        /// <c>recipeExecuteData</c> 字典重建一遍，而 LDBTool 是在 <c>PostAddDataAction</c>
        /// <b>之后</b>才调它的——所以在这里改 <c>Items</c> / <c>ItemCounts</c> 会被自动采纳，
        /// 不需要我们自己刷新任何缓存。宇宙矩阵加第七味原料走的就是同一条路。
        ///
        /// 数组<b>长度不动</b>，只改值：长度是存档格式的一部分
        /// （<c>AssemblerComponent.Export</c> 按它决定写几个 served / produced）。
        /// </summary>
        private static void ApplyDefaultToProto()
        {
            RecipeProto recipe = LDB.recipes.Select(RecipeId);

            if (recipe == null)
            {
                ProjectEdenPlugin.Log.LogError($"推进剂药柱：配方 {RecipeId} 在 LDB 里不存在，默认原料没换");

                return;
            }

            Agent reducer = Reducers[0];
            Agent oxidizer = Oxidizers[0];

            int ratio = Config.ratioDefault > 0 ? Config.ratioDefault : 100;
            int tier = RedoxBurnerPatches.TierIndex(RedoxBurnerPatches.Density(reducer, oxidizer, ratio));

            recipe.Items = new[] { reducer.ItemId, oxidizer.ItemId };
            recipe.ItemCounts = new[]
            {
                Config.reducerParts > 0 ? Config.reducerParts : 8,
                RedoxBurnerPatches.OxidizerCount(reducer, oxidizer, ratio),
            };
            recipe.Results = new[] { Tiers[tier].ItemId };
            recipe.ResultCounts = new[] { RedoxBurnerPatches.Yield(reducer, tier, ratio) };

            // 图标也跟着默认档走，否则合成面板里是 I 档的图、产物却是 III 档的。
            // Preload 的参数是**在 dataArray 里的下标**，不是 ID——它是 ProtoSet
            // 反序列化时按位置调的。取不到下标就只改路径，那样图标会停在 I 档，
            // 但不会崩，也不会画出白块。
            recipe.IconPath = "Assets/projecteden/" + Tiers[tier].Entry.key;
            recipe._iconSprite = null;

            int index = System.Array.IndexOf(LDB.recipes.dataArray, recipe);

            if (index >= 0) recipe.Preload(index);
            else
                ProjectEdenPlugin.Log.LogWarning(
                    "推进剂药柱：在 LDB.recipes.dataArray 里找不到这条配方，图标停在默认档");

            ProjectEdenPlugin.Log.LogInfo(
                $"推进剂药柱：配方原型的默认原料已换成 {reducer.Name} ×{recipe.ItemCounts[0]} + " +
                $"{oxidizer.Name} ×{recipe.ItemCounts[1]} → {Tiers[tier].Entry.name} ×{recipe.ResultCounts[0]}" +
                "（占位的原版固体只在注册阶段用，那时本 mod 的物品还不在 LDB 里）");
        }

        /// <summary>
        /// 解析一组原料。<b>名字要和真实 proto 的 <c>Name</c> 交叉核对</b>——
        /// 写死的原版 ID 指错了东西是<b>完全静默</b>的，配方照样注册、照样能跑，
        /// 只是吃的是别的物品。比 <c>proto.name</c> 是错的：那是翻译过的，
        /// 英文客户端上每一条都会误报（这个错本仓库犯过，记在 English localization 一节）。
        /// </summary>
        private static void Resolve(RedoxAgentEntry[] entries, List<Agent> into, bool needHeat)
        {
            if (entries == null) return;

            foreach (RedoxAgentEntry e in entries)
            {
                if (e == null) continue;

                int id = e.id > 0 ? e.id : OreRegistry.FindItemIdByRef(e.@ref);

                if (id <= 0)
                {
                    ProjectEdenPlugin.Log.LogError($"氧化还原燃烧厂：原料「{e.name}」解析不出物品，已跳过");

                    continue;
                }

                ItemProto proto = LDB.items.Select(id);

                if (proto == null)
                {
                    ProjectEdenPlugin.Log.LogError($"氧化还原燃烧厂：物品 {id}（{e.name}）在 LDB 里不存在，已跳过");

                    continue;
                }

                if (!string.IsNullOrEmpty(e.name) && proto.Name != e.name)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"氧化还原燃烧厂：配置里写的是「{e.name}」，而物品 {id} 实际叫「{proto.Name}」——" +
                        "ID 指错了东西，已跳过这一条");

                    continue;
                }

                if (e.oxygen <= 0f)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"氧化还原燃烧厂：「{e.name}」的 oxygen 是 {e.oxygen}，必须为正，已跳过");

                    continue;
                }

                if (needHeat && proto.HeatValue <= 0L)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"氧化还原燃烧厂：还原剂「{e.name}」没有热值（HeatValue={proto.HeatValue}），" +
                        "压出来的药柱会是零能量，已跳过");

                    continue;
                }

                into.Add(new Agent { Entry = e, ItemId = id, Heat = needHeat ? proto.HeatValue : 0L });
            }
        }

        /// <summary>
        /// 把全部组合的密度、档次、配比打进日志。
        ///
        /// 这不是装饰：药柱的档次是「密度过不过线」算出来的，而密度取决于热值，
        /// 热值又可能被 <c>vanillaHeat</c> 改过。<b>从最终状态去核，而不是从配置去核</b>——
        /// 这条规矩仓库在流体白名单那次已经付过学费。同时它也是这套数值唯一的验收面：
        /// 想知道调一个数之后十二种组合各落在哪一档，读这张表就够了，不用进游戏试。
        /// </summary>
        private static void DumpTable()
        {
            ProjectEdenPlugin.Log.LogInfo(
                $"氧化还原燃烧厂：还原剂 {Reducers.Count} 种 × 氧化剂 {Oxidizers.Count} 种 = " +
                $"{Reducers.Count * Oxidizers.Count} 种组合，{Tiers.Count} 档药柱，配方 {RecipeId}");

            foreach (Agent r in Reducers)
            foreach (Agent o in Oxidizers)
            {
                float density = RedoxBurnerPatches.Density(r, o, Config.ratioDefault);
                int tier = RedoxBurnerPatches.TierIndex(density);
                int yield = RedoxBurnerPatches.Yield(r, tier, Config.ratioDefault);
                int oxCount = RedoxBurnerPatches.OxidizerCount(r, o, Config.ratioDefault);

                ProjectEdenPlugin.Log.LogInfo(
                    $"  {r.Name} ×{Config.reducerParts} + {o.Name} ×{oxCount} → " +
                    $"{Tiers[tier].Entry.name} ×{yield}　（密度 {density:0.00} MJ/件，" +
                    $"投入 {r.Heat * Config.reducerParts / 1e6:0.#} MJ，" +
                    $"产出 {Tiers[tier].Entry.heatValue * yield / 1e6:0.#} MJ）");
            }
        }
    }
}
