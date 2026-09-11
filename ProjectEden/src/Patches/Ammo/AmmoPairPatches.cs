using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 合金弹药的「两种合金 → 哪一档 + 出多少」。
    ///
    /// <b>逐建筑改四个 int，一个都不改数组长度</b>：
    /// <c>requires[0]</c> / <c>requires[1]</c>（吃哪两种合金）、<c>products[0]</c>（哪一档）、
    /// <c>productCounts[0]</c>（产量）。长度一变就坏档——<c>AssemblerComponent.Export</c>
    /// 是按 <c>requires</c> / <c>products</c> 的<b>长度</b>决定写几个 served / produced 的。
    /// 这一点和合金配比那套是同一条规矩，见 <see cref="AlloyRatioPatches"/> 的类注释。
    ///
    /// <b>存档沿用 AlloyRatioStore，不加新格式。</b> 它存的就是「每台建筑一个 int[]」，
    /// 这里存的是两个合金的物品 ID——形状一样，所以 SaveVersion 不用动。
    /// 玩家默认值按配方 ID 记，新建和蓝图粘贴出来的机器继承最后一次选的组合。
    ///
    /// <b>为什么产物物品必须换而产量可以不换。</b> 伤害是 <c>ItemProto.Ability</c>，按 proto 存
    /// （<c>TurretComponent.SetNewItem</c>: <c>bulletDamage = proto.Ability</c>），
    /// 所以不同伤害只能是不同物品；产量是 <c>productCounts[0]</c>，一个 int，连续可调。
    /// </summary>
    [HarmonyPatch]
    internal static class AmmoPairPatches
    {
        private static AmmoConfig Cfg => AmmoRegistry.Config;

        // ── 组合怎么算 ────────────────────────────────────────

        /// <summary>
        /// 两种合金按 50:50 混出来的四维。
        ///
        /// 走的是和合金自己一样的混合律：硬度取<b>幂平均</b>（上界）、韧性取<b>调和平均</b>
        /// （被最弱环节拖住）、耐蚀导电线性。韧性用调和平均是关键——
        /// 一脆一韧混出来仍然偏脆，这正好让「又硬又韧」成为稀有解。
        /// </summary>
        internal static void Mix(int itemA, int itemB, out float h, out float t, out float c, out float e)
        {
            float ha = MetalPropertyPatches.Axis(itemA, "hardness");
            float hb = MetalPropertyPatches.Axis(itemB, "hardness");
            float ta = MetalPropertyPatches.Axis(itemA, "toughness");
            float tb = MetalPropertyPatches.Axis(itemB, "toughness");

            h = (float)Math.Sqrt(0.5 * ha * ha + 0.5 * hb * hb);

            double inv = 0.5 / (ta > 0f ? ta : 1f) + 0.5 / (tb > 0f ? tb : 1f);

            t = (float)(inv > 0.0 ? 1.0 / inv : 0.0);

            c = 0.5f * (MetalPropertyPatches.Axis(itemA, "corrosion") + MetalPropertyPatches.Axis(itemB, "corrosion"));
            e = 0.5f * (MetalPropertyPatches.Axis(itemA, "conductivity") + MetalPropertyPatches.Axis(itemB, "conductivity"));
        }

        /// <summary>侵彻分。硬度是主项——穿甲靠硬度，现实里穿甲弹用碳化钨芯就是这个道理。</summary>
        internal static float Score(int itemA, int itemB)
        {
            Mix(itemA, itemB, out float h, out float _, out float c, out float _);

            return h * Cfg.hardnessWeight + c * Cfg.corrosionWeight;
        }

        /// <summary>命中的档位下标。取「分数够得着」的最高一档。</summary>
        internal static int TierIndex(int itemA, int itemB)
        {
            float score = Score(itemA, itemB);

            var index = 0;

            for (var i = 0; i < AmmoRegistry.Tiers.Count; i++)
                if (score >= AmmoRegistry.Tiers[i].Entry.minScore)
                    index = i;

            return index;
        }

        /// <summary>
        /// 产量：按韧性的<b>递减回报</b>算，夹在上下限之间。
        ///
        /// 脆的料成形时崩得多，一炉出的成品就少。指数 &lt; 1 是为了让「韧性再高一点」
        /// 的收益逐步变小，否则高韧组合会把产量拉到离谱。
        /// </summary>
        internal static int Yield(int itemA, int itemB)
        {
            Mix(itemA, itemB, out float _, out float t, out float _, out float _);

            float anchor = Cfg.toughnessAnchor > 0f ? Cfg.toughnessAnchor : 30f;
            float exp = Cfg.toughnessExponent > 0f ? Cfg.toughnessExponent : 0.6f;

            var mul = (float)Math.Pow(Math.Max(t, 0.01f) / anchor, exp);

            float lo = Cfg.yieldMin > 0f ? Cfg.yieldMin : 0.5f;
            float hi = Cfg.yieldMax > 0f ? Cfg.yieldMax : 1.8f;

            if (mul < lo) mul = lo;
            if (mul > hi) mul = hi;

            int baseYield = Cfg.baseYield > 0 ? Cfg.baseYield : 12;
            var v = (int)Math.Round(baseYield * mul);

            return v < 1 ? 1 : v;
        }

        // ── 逐建筑贴配方 ──────────────────────────────────────

        private static bool Locate(PlanetFactory factory, int entityId, out int assemblerId)
        {
            assemblerId = 0;

            if (!AmmoRegistry.Ready || factory?.entityPool == null) return false;
            if (entityId <= 0 || entityId >= factory.entityPool.Length) return false;

            assemblerId = factory.entityPool[entityId].assemblerId;

            if (assemblerId <= 0 || factory.factorySystem?.assemblerPool == null) return false;
            if (assemblerId >= factory.factorySystem.assemblerPool.Length) return false;

            return factory.factorySystem.assemblerPool[assemblerId].recipeId == AmmoRegistry.RecipeId;
        }

        internal static bool IsAmmoAssembler(PlanetFactory factory, int entityId) =>
            Locate(factory, entityId, out int _);

        /// <summary>这台建筑现在吃的是哪两种合金。读的是组件上的实时值。</summary>
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

            int a = Valid(pair[0]) ? pair[0] : AmmoRegistry.Candidates[0];
            int b = Valid(pair[1]) ? pair[1] : AmmoRegistry.Candidates[1 % AmmoRegistry.Candidates.Count];

            int tier = TierIndex(a, b);
            int yield = Yield(a, b);

            // Clone() 保住长度——长度一变就坏档
            var requires = (int[])src.requires.Clone();
            var requireCounts = (int[])src.requireCounts.Clone();
            var products = (int[])src.products.Clone();
            var productCounts = (int[])src.productCounts.Clone();

            requires[0] = a;
            requires[1] = b;

            products[0] = AmmoRegistry.Tiers[tier].ItemId;
            productCounts[0] = yield;

            comp.recipeExecuteData = new RecipeExecuteData(
                requires, requireCounts, products, productCounts,
                src.timeSpend, src.extraTimeSpend, src.productive);

            AlloyRatioStore.Set(factory.planetId, entityId, new[] { a, b });

            Discover(a, b, tier);

            return true;
        }

        private static bool Valid(int itemId) => itemId > 0 && AmmoRegistry.Candidates.Contains(itemId);

        // ── 已发现的组合 ──────────────────────────────────────

        /// <summary>
        /// 已经试出来过的组合。<b>只在内存里</b>——存档块的格式还没动，
        /// 所以重开游戏会忘掉。等这套定型了再进存档，免得为一个还在调的功能升 SaveVersion。
        /// </summary>
        private static readonly HashSet<long> Found = new HashSet<long>();

        private static long Key(int a, int b) => a < b ? (long)a << 32 | (uint)b : (long)b << 32 | (uint)a;

        private static void Discover(int a, int b, int tier)
        {
            if (!Found.Add(Key(a, b))) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"合金弹药：{LDB.items.Select(a)?.name} + {LDB.items.Select(b)?.name} → " +
                $"{AmmoRegistry.Tiers[tier].Entry.name}（伤害 {AmmoRegistry.Tiers[tier].Damage}，产量 {Yield(a, b)}）");
        }

        internal static bool Discovered(int a, int b) => Found.Contains(Key(a, b));

        // ── 钩子：和合金配比同样的三个入口 ────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIAssemblerWindow), nameof(UIAssemblerWindow.OnRecipePickerReturn))]
        private static void UIAssemblerWindow_OnRecipePickerReturn(UIAssemblerWindow __instance)
        {
            if (__instance?.factory == null || __instance.assemblerId <= 0) return;

            int entityId = __instance.factory.factorySystem.assemblerPool[__instance.assemblerId].entityId;

            ApplyDefault(__instance.factory, entityId);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildingParameters), nameof(BuildingParameters.PasteToFactoryObject))]
        private static void BuildingParameters_PasteToFactoryObject(int objectId, PlanetFactory factory)
        {
            if (objectId > 0) ApplyDefault(factory, objectId);
        }

        /// <summary>新建 / 粘贴出来的机器继承玩家最后一次选的组合。</summary>
        private static void ApplyDefault(PlanetFactory factory, int entityId)
        {
            if (!AmmoRegistry.Ready || !Locate(factory, entityId, out int _)) return;

            int[] saved = AlloyRatioStore.TryGet(factory.planetId, entityId, out int[] mine)
                ? mine
                : AlloyRatioStore.GetPlayerDefault(AmmoRegistry.RecipeId);

            if (saved == null || saved.Length < 2)
                saved = new[] { AmmoRegistry.Candidates[0], AmmoRegistry.Candidates[1 % AmmoRegistry.Candidates.Count] };

            Apply(factory, entityId, saved);
        }

        /// <summary>读档后把存着的组合重贴回去。和合金配比同一个理由：Import 会重新推导配方数据。</summary>
        internal static void ReapplyAll()
        {
            if (!AmmoRegistry.Ready) return;

            GameData data = GameMain.data;

            if (data?.factories == null) return;

            foreach (KeyValuePair<(int PlanetId, int EntityId), int[]> pair in AlloyRatioStore.All)
            {
                PlanetFactory factory = null;

                for (var i = 0; i < data.factoryCount; i++)
                    if (data.factories[i] != null && data.factories[i].planetId == pair.Key.PlanetId)
                        factory = data.factories[i];

                if (factory == null) continue;

                if (pair.Value != null && pair.Value.Length == 2) Apply(factory, pair.Key.EntityId, pair.Value);
            }
        }
    }
}
