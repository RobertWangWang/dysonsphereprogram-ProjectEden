using System.Collections.Generic;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 活性增产剂：<b>一条配方，两个选料位，四种活性复合材两两组合决定产出哪一种</b>。
    ///
    /// 做法和 <see cref="AmmoPairPatches"/> 完全对称——<c>RecipeProto.requires[i]</c> 是个
    /// <c>int</c>，逐台建筑改它就能让同一条配方吃不同原料。数组<b>长度</b>不动，
    /// 所以存档安全（<c>AssemblerComponent.Export</c> 按 requires/products 的长度写
    /// served/produced）。
    ///
    /// <b>它解决的是「四维算出来没人读」。</b> 活性复合材的四轴此前只显示在提示栏里，
    /// 不参与任何计算——设计稿费力验证过的那条帕累托前沿是装饰。这里两个分数各读两轴，
    /// 四轴全部承重：
    ///
    /// <list type="bullet">
    /// <item><b>性格</b> = 硬度 / 韧性 —— 硬主导出浓缩型（等级高、喷数少）</item>
    /// <item><b>档次</b> = 耐蚀 + 导电 —— 高的出 Mk.V</item>
    /// </list>
    ///
    /// 两个分数在 10 种组合上的相关系数是 <b>0.057</b>，所以二维不会塌成一维；
    /// 两处阈值都取在枚举出来的天然缺口中点，不是先拍再凑。
    ///
    /// <b>混合律复用 <see cref="AmmoPairPatches.Mix"/></b>，也就是合金自己那一套：
    /// 硬度取幂平均 p=2、韧性取调和平均、其余线性。共用而不是抄一份，
    /// 是因为这条规则一旦两处分叉，面板上算出来的和实际产出的就会对不上。
    ///
    /// <b>配方是个秘密，而且直觉是错的。</b> 游戏里不写哪一对出哪一种：纯 IV 刚化双份
    /// 只出 Mk.IV 浓缩（它的耐蚀+导电 93 差一点够不到档次线），反倒是最不起眼的
    /// II 渗流双份出 Mk.V 广延——II 是四级里唯一导电的那一级。
    ///
    /// <b>轴到产物的映射是味道不是机制</b>，这一点写在 proliferator.json 的 <c>//flavour</c> 里：
    /// 增产剂本身是科幻设定，没有现实对应物，所以不假装它是从化学推出来的。
    /// </summary>
    internal static class ProliferatorPatches
    {
        internal class Outcome
        {
            internal ProliferatorOutcomeEntry Entry;
            internal int ItemId;
            internal int Yield;
        }

        internal static ProliferatorConfig Config;

        /// <summary>两个选料位共用的候选名单（活性复合材的物品 ID）。</summary>
        internal static readonly List<int> Candidates = new List<int>();

        /// <summary>四个产物，下标即 <c>(highTier ? 2 : 0) + (dense ? 1 : 0)</c>。</summary>
        internal static readonly List<Outcome> Outcomes = new List<Outcome>();

        internal static int RecipeId { get; private set; }

        internal static bool Ready => Outcomes.Count == 4 && Candidates.Count >= 1 && RecipeId > 0;

        internal static void Load() => Config = JsonHelper.Load<ProliferatorConfig>("proliferator");

        // ── 注册 ──────────────────────────────────────────────

        internal static void OnPostAddData()
        {
            Candidates.Clear();
            Outcomes.Clear();
            RecipeId = 0;

            // 报无聊的那一面：三种「没生效」要在日志里分得清
            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogInfo("活性增产剂：没有 proliferator.json，跳过");
                return;
            }

            if (!Config.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo("活性增产剂：proliferator.json 里 enabled 为 false，跳过");
                return;
            }

            if (!ResolveCandidates() || !ResolveOutcomes()) return;

            if (!VerifyRecipe()) return;

            RecipeId = Config.recipeId;

            RegisterWithSpraycoaters();

            ProjectEdenPlugin.Log.LogInfo(
                $"活性增产剂已就绪：配方 {RecipeId}，{Candidates.Count} 种投料两两组合（可重复）"
                + $"共 {Candidates.Count * (Candidates.Count + 1) / 2} 对 → {Outcomes.Count} 种产物；"
                + $"性格阈值 {Config.characterThreshold:0.##}（硬/韧），"
                + $"档次阈值 {Config.tierThreshold:0.#}（蚀+电）");

            DumpMapping();
        }

        private static bool ResolveCandidates()
        {
            if (Config.candidates == null || Config.candidates.Length == 0)
            {
                ProjectEdenPlugin.Log.LogWarning("活性增产剂：candidates 是空的，面板上没料可选");
                return false;
            }

            foreach (string key in Config.candidates)
            {
                int id = OreRegistry.FindItemIdByRef(key);

                if (id <= 0 || LDB.items.Select(id) == null)
                {
                    ProjectEdenPlugin.Log.LogError($"活性增产剂：投料「{key}」解析不出物品，跳过");
                    continue;
                }

                // 没有四维的料混出来全是 0，会一声不响地全落进同一格
                if (!MetalPropertyPatches.Has(id))
                    ProjectEdenPlugin.Log.LogWarning(
                        $"活性增产剂：「{LDB.items.Select(id).Name}」在 metals.json 里没有四维属性，"
                        + "它参与的组合算出来会是 0 分");

                Candidates.Add(id);
            }

            return Candidates.Count > 0;
        }

        private static bool ResolveOutcomes()
        {
            if (Config.outcomes == null || Config.outcomes.Length != 4)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"活性增产剂：outcomes 要正好 4 个（档次 × 性格），实际 {Config.outcomes?.Length ?? 0} 个");

                return false;
            }

            var slots = new Outcome[4];

            foreach (ProliferatorOutcomeEntry e in Config.outcomes)
            {
                if (e == null) continue;

                int id = OreRegistry.FindItemIdByRef(e.@ref);
                ItemProto proto = id > 0 ? LDB.items.Select(id) : null;

                if (proto == null)
                {
                    ProjectEdenPlugin.Log.LogError($"活性增产剂：产物「{e.@ref}」解析不出物品");
                    return false;
                }

                // 比 Name 不比 name：后者是译文，切英文之后永远对不上
                if (!string.IsNullOrEmpty(e.name) && e.name != proto.Name)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"活性增产剂：配置里写的是「{e.name}」，ref {e.@ref} 解析出来的是「{proto.Name}」");

                // 两处配置各说各话是最难查的：面板按这里的数显示，喷涂机按 proto 的数喷
                if (e.ability != proto.Ability || e.hpMax != proto.HpMax)
                    ProjectEdenPlugin.Log.LogError(
                        $"活性增产剂：「{proto.Name}」的等级/喷数在两处对不上 —— "
                        + $"proliferator.json 写 {e.ability}/{e.hpMax}，"
                        + $"ores.json 注册出来是 {proto.Ability}/{proto.HpMax}。"
                        + "引擎读的是后者，面板读的是前者");

                int slot = (e.highTier ? 2 : 0) + (e.dense ? 1 : 0);

                if (slots[slot] != null)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"活性增产剂：档次/性格组合重复了（highTier={e.highTier} dense={e.dense}），"
                        + $"「{e.name}」和「{slots[slot].Entry.name}」撞在同一格");

                    return false;
                }

                slots[slot] = new Outcome
                {
                    Entry = e,
                    ItemId = id,
                    Yield = e.yield > 0 ? e.yield : 1,
                };
            }

            foreach (Outcome o in slots)
                if (o == null)
                {
                    ProjectEdenPlugin.Log.LogError("活性增产剂：四个档次/性格组合没配全");
                    return false;
                }

            Outcomes.AddRange(slots);

            return true;
        }

        /// <summary>配方的数组长度必须能放下两个投料位和一个产物，否则改了会坏档。</summary>
        private static bool VerifyRecipe()
        {
            RecipeProto recipe = LDB.recipes.Select(Config.recipeId);

            if (recipe == null)
            {
                ProjectEdenPlugin.Log.LogError($"活性增产剂：配方 {Config.recipeId} 不存在");
                return false;
            }

            if (recipe.Items == null || recipe.Items.Length < 2 || recipe.Results == null || recipe.Results.Length < 1)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"活性增产剂：配方「{recipe.Name}」要两个原料槽和一个产物槽，"
                    + $"实际 {recipe.Items?.Length ?? 0} / {recipe.Results?.Length ?? 0}");

                return false;
            }

            return true;
        }

        /// <summary>
        /// 把四个产物追加进喷涂机的 <c>PrefabDesc.incItemId[]</c>。
        ///
        /// <b>这是让喷涂机认它们的唯一一步。</b> 只给 proto 配 <c>Ability</c>/<c>HpMax</c> 不够——
        /// <c>SpraycoaterComponent.InternalUpdate</c> 是拿 <c>Cargo.item</c> 逐个去比这张数组，
        /// 不在表里就永远不会被捡起来，而且<b>一声不响</b>。
        /// 同一族的坑仓库已经踩过三次：<c>ItemProto.fluids</c>（储液罐）、
        /// <c>turretNeeds</c>（炮塔弹药）、<c>UIEntityBriefInfo.icons</c>（物流站格数）。
        ///
        /// <b>用发现而不是写死喷涂机 ID</b>：扫所有 <c>incItemId</c> 非空的 prefab。
        /// 而且它每 tick 现读 prefab，<b>不进存档，已建成的喷涂机立刻生效</b>。
        /// </summary>
        private static void RegisterWithSpraycoaters()
        {
            ItemProto[] items = LDB.items?.dataArray;

            if (items == null) return;

            var buildings = 0;

            foreach (ItemProto proto in items)
            {
                PrefabDesc desc = proto?.prefabDesc;

                if (desc?.incItemId == null || desc.incItemId.Length == 0) continue;

                var add = new List<int>();

                foreach (Outcome o in Outcomes)
                    if (System.Array.IndexOf(desc.incItemId, o.ItemId) < 0)
                        add.Add(o.ItemId);

                if (add.Count == 0) continue;

                int at = desc.incItemId.Length;
                int[] grown = desc.incItemId;

                System.Array.Resize(ref grown, at + add.Count);

                for (var i = 0; i < add.Count; i++) grown[at + i] = add[i];

                desc.incItemId = grown;
                buildings++;

                ProjectEdenPlugin.Log.LogInfo(
                    $"活性增产剂：已登记进「{proto.Name}」的增产剂白名单，"
                    + $"{at} 种 → {grown.Length} 种");
            }

            if (buildings == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    "活性增产剂：没找到任何带 incItemId 的建筑，这几种增产剂不会被喷涂机捡起来");
        }

        /// <summary>把 10 种组合的映射整张打出来。开发期对照用，也是这条链唯一的「答案」。</summary>
        private static void DumpMapping()
        {
            for (var i = 0; i < Candidates.Count; i++)
            for (int j = i; j < Candidates.Count; j++)
            {
                int a = Candidates[i];
                int b = Candidates[j];

                ProjectEdenPlugin.Log.LogInfo(
                    $"  {LDB.items.Select(a)?.Name} + {LDB.items.Select(b)?.Name}"
                    + $"  硬/韧 {Character(a, b):0.00}  蚀+电 {Tier(a, b):0.0}"
                    + $"  → {Outcomes[OutcomeIndex(a, b)].Entry.name}");
            }
        }

        // ── 两个分数 ──────────────────────────────────────────

        /// <summary>性格分：硬度 / 韧性。硬主导 → 浓缩型。</summary>
        internal static float Character(int a, int b)
        {
            AmmoPairPatches.Mix(a, b, out float h, out float t, out float _, out float _);

            return t > 0f ? h / t : 0f;
        }

        /// <summary>档次分：耐蚀 + 导电。</summary>
        internal static float Tier(int a, int b)
        {
            AmmoPairPatches.Mix(a, b, out float _, out float _, out float c, out float e);

            return c + e;
        }

        internal static int OutcomeIndex(int a, int b)
        {
            bool high = Tier(a, b) >= Config.tierThreshold;
            bool dense = Character(a, b) >= Config.characterThreshold;

            return (high ? 2 : 0) + (dense ? 1 : 0);
        }

        // ── 逐台建筑 ──────────────────────────────────────────

        private static bool Locate(PlanetFactory factory, int entityId, out int assemblerId)
        {
            assemblerId = 0;

            if (!Ready || factory?.entityPool == null || entityId <= 0 || entityId >= factory.entityCursor)
                return false;

            assemblerId = factory.entityPool[entityId].assemblerId;

            if (assemblerId <= 0 || assemblerId >= factory.factorySystem.assemblerCursor) return false;

            return factory.factorySystem.assemblerPool[assemblerId].recipeId == RecipeId;
        }

        internal static bool IsProliferatorAssembler(PlanetFactory factory, int entityId) =>
            Locate(factory, entityId, out int _);

        /// <summary>这台建筑现在吃的是哪两种复合材。读的是组件上的实时值。</summary>
        internal static bool Current(PlanetFactory factory, int entityId, out int[] pair)
        {
            pair = null;

            if (!Locate(factory, entityId, out int assemblerId)) return false;

            RecipeExecuteData data = factory.factorySystem.assemblerPool[assemblerId].recipeExecuteData;

            if (data?.requires == null || data.requires.Length < 2) return false;

            pair = new[] { data.requires[0], data.requires[1] };

            return true;
        }

        internal static bool Apply(PlanetFactory factory, int entityId, int[] pair)
        {
            if (pair == null || pair.Length < 2) return false;
            if (!Locate(factory, entityId, out int assemblerId)) return false;

            ref AssemblerComponent comp = ref factory.factorySystem.assemblerPool[assemblerId];

            RecipeExecuteData src = comp.recipeExecuteData;

            if (src?.requires == null || src.requires.Length < 2) return false;
            if (src.products == null || src.products.Length < 1) return false;

            int a = Valid(pair[0]) ? pair[0] : Candidates[0];
            int b = Valid(pair[1]) ? pair[1] : Candidates[0];

            Outcome outcome = Outcomes[OutcomeIndex(a, b)];

            // Clone() 保住长度——长度一变就坏档
            var requires = (int[])src.requires.Clone();
            var requireCounts = (int[])src.requireCounts.Clone();
            var products = (int[])src.products.Clone();
            var productCounts = (int[])src.productCounts.Clone();

            requires[0] = a;
            requires[1] = b;

            products[0] = outcome.ItemId;
            productCounts[0] = outcome.Yield;

            comp.recipeExecuteData = new RecipeExecuteData(
                requires, requireCounts, products, productCounts,
                src.timeSpend, src.extraTimeSpend, src.productive);

            AlloyRatioStore.Set(factory.planetId, entityId, new[] { a, b });

            return true;
        }

        private static bool Valid(int itemId) => itemId > 0 && Candidates.Contains(itemId);

        /// <summary>
        /// 读档后重贴。<c>AssemblerComponent.Import</c> 会把 <c>recipeExecuteData</c>
        /// 换回 <c>RecipeProto</c> 上的那个全局对象，所以逐台的改动必须重来一遍。
        /// </summary>
        internal static int ReapplyAll()
        {
            if (!Ready) return 0;

            GameData data = GameMain.data;

            if (data?.factories == null) return 0;

            var done = 0;

            foreach (KeyValuePair<(int PlanetId, int EntityId), int[]> kv in AlloyRatioStore.All)
            {
                if (kv.Value == null || kv.Value.Length != 2) continue;

                PlanetFactory factory = null;

                for (var i = 0; i < data.factoryCount; i++)
                    if (data.factories[i] != null && data.factories[i].planetId == kv.Key.PlanetId)
                        factory = data.factories[i];

                if (factory == null) continue;

                // Locate 里按 recipeId 过滤，所以这里不会和合金配比 / 合金弹药 / 复合材抢同一台
                if (Apply(factory, kv.Key.EntityId, kv.Value)) done++;
            }

            return done;
        }
    }
}
