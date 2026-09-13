using System.Collections.Generic;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 三级提纯的<b>逐台建筑选料</b>：面板上选一种金属，这台提纯厂就提那一种，
    /// 候选表由 <see cref="QualityRefineryRegistry"/> 推出来，品质由级别决定。
    ///
    /// <b>原料和产物是同一种金属</b>（理由见 <c>QualityRefineryRegistry</c> 的类注释：
    /// 大型采矿机对好几种矿直接出产物，所以进料只能是金属块；而「提纯过的铜」和「粗铜」
    /// 不是两种物品，差别记在容器的品质上）。
    ///
    /// 机制和 <see cref="CompositeOutputPatches"/> 完全同形，也是同一批理由：
    ///
    /// <list type="bullet">
    /// <item><c>RecipeExecuteData</c> 是 <b>class</b>，而且是从 <c>RecipeProto</c> 上一张
    ///       <b>静态字典</b>里取来的<b>同一个对象</b>——直接改它等于改全局配方表，
    ///       眼前这台机器确实变了，所以看起来像「我只改了这一台」。必须换成自己的新实例。</item>
    /// <item>数组<b>长度</b>是进存档的（<c>AssemblerComponent.Export</c> 按
    ///       <c>requires</c> / <c>products</c> 的长度决定写几个 <c>served</c> / <c>produced</c>），
    ///       所以 <c>Clone()</c> 保长度，只换里面的值。</item>
    /// <item>读档时 <c>Import</c> 会用 <c>recipeId</c> 重新推导配方数据，把克隆冲掉，
    ///       所以选择要自己存（<see cref="AlloyRatioStore"/>）并在读档后重贴。</item>
    /// </list>
    ///
    /// 存的是长度 1 的 <c>{ 金属物品 ID }</c>。烧结析出存的也是长度 1，两边靠
    /// <c>Locate</c> 里的 <c>recipeId</c> 核对彼此分开——长度只是第一道筛子。
    ///
    /// <b>第一味原料和第一样产物一起换掉，其余一个不动。</b> 试剂（电解液 / 氮气 /
    /// 一氧化碳）是这一级的工艺特征，不随金属变。所以只动
    /// <c>requires[0]</c> / <c>requireCounts[0]</c> / <c>products[0]</c> / <c>productCounts[0]</c>，
    /// 而且这两边写的是<b>同一个物品 ID</b>。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityRefinerySelectPatches
    {
        private static bool Locate(PlanetFactory factory, int entityId,
            out int assemblerId, out QualityRefineryRegistry.Tier tier)
        {
            assemblerId = 0;
            tier = null;

            if (!QualityRefineryRegistry.Ready || factory?.entityPool == null) return false;
            if (entityId <= 0 || entityId >= factory.entityPool.Length) return false;

            assemblerId = factory.entityPool[entityId].assemblerId;

            if (assemblerId <= 0 || factory.factorySystem?.assemblerPool == null) return false;
            if (assemblerId >= factory.factorySystem.assemblerPool.Length) return false;

            tier = QualityRefineryRegistry.FindTier(
                factory.factorySystem.assemblerPool[assemblerId].recipeId);

            return tier != null;
        }

        /// <summary>这台建筑现在提的是哪种金属。读的是组件上的实时值。</summary>
        internal static bool Current(PlanetFactory factory, int entityId,
            out QualityRefineryRegistry.Tier tier, out int[] state)
        {
            state = null;

            if (!Locate(factory, entityId, out int assemblerId, out tier)) return false;

            RecipeExecuteData data = factory.factorySystem.assemblerPool[assemblerId].recipeExecuteData;

            if (data?.requires == null || data.requires.Length < 1) return false;

            state = new[] { data.requires[0] };

            return true;
        }

        internal static bool Apply(PlanetFactory factory, int entityId, int[] state)
        {
            if (state == null || state.Length < 1) return false;
            if (!Locate(factory, entityId, out int assemblerId,
                    out QualityRefineryRegistry.Tier tier)) return false;

            ref AssemblerComponent comp = ref factory.factorySystem.assemblerPool[assemblerId];

            RecipeExecuteData src = comp.recipeExecuteData;

            if (src?.requires == null || src.requires.Length < 1) return false;
            if (src.requireCounts == null || src.requireCounts.Length < 1) return false;
            if (src.products == null || src.products.Length < 1) return false;
            if (src.productCounts == null || src.productCounts.Length < 1) return false;

            QualityRefineryRegistry.Feed feed = QualityRefineryRegistry.FindFeed(state[0])
                                                ?? QualityRefineryRegistry.FindFeed(tier.DefaultItemId)
                                                ?? QualityRefineryRegistry.Feeds[0];

            // Clone() 保住长度——长度一变就坏档
            var requires = (int[])src.requires.Clone();
            var requireCounts = (int[])src.requireCounts.Clone();
            var products = (int[])src.products.Clone();
            var productCounts = (int[])src.productCounts.Clone();

            requires[0] = feed.ItemId;
            requireCounts[0] = tier.InputUnits;

            products[0] = feed.ItemId;
            productCounts[0] = QualityRefineryRegistry.OutputOf(tier);

            // **耗时取配方原型的值，不取 src 的。** src 极可能已经是我们自己的克隆，
            // 拿它当基数会在每次重贴时再乘一遍——合金那边记过这个形状的静默失控。
            comp.recipeExecuteData = new RecipeExecuteData(
                requires, requireCounts, products, productCounts,
                tier.TimeSpend, src.extraTimeSpend, src.productive);

            AlloyRatioStore.Set(factory.planetId, entityId, new[] { feed.ItemId });

            Discover(tier, feed);

            return true;
        }

        // ── 已试过的组合 ──────────────────────────────────────

        private static readonly HashSet<long> Found = new HashSet<long>();

        private static void Discover(QualityRefineryRegistry.Tier tier, QualityRefineryRegistry.Feed feed)
        {
            if (!Found.Add((long)tier.RecipeId << 32 | (uint)feed.ItemId)) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"同位提纯：{tier.Name} —— {feed.Name} ×{tier.InputUnits} → " +
                $"{feed.Name} ×{QualityRefineryRegistry.OutputOf(tier)}" +
                $"（每件 {tier.Quality} 分，{tier.TimeSpend / 60f:0.##} 秒）");
        }

        // ── 钩子：和另外四家同样的入口 ────────────────────────

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

        /// <summary>
        /// 这台建筑没存过选择时贴什么：先看玩家在同一级上最后一次选的，
        /// 再退回配方原型自己写着的那一种。
        /// </summary>
        private static void ApplyDefault(PlanetFactory factory, int entityId)
        {
            if (!Locate(factory, entityId, out int _, out QualityRefineryRegistry.Tier tier)) return;

            int[] saved = AlloyRatioStore.TryGet(factory.planetId, entityId, out int[] mine)
                ? mine
                : AlloyRatioStore.GetPlayerDefault(tier.RecipeId);

            if (saved == null || saved.Length < 1) saved = new[] { tier.DefaultItemId };

            Apply(factory, entityId, saved);
        }

        /// <summary>读档后把存着的选择重贴回去。理由同另外四家：Import 会重新推导配方数据。</summary>
        internal static void ReapplyAll()
        {
            if (!QualityRefineryRegistry.Ready) return;

            GameData data = GameMain.data;

            if (data?.factories == null) return;

            foreach (KeyValuePair<(int PlanetId, int EntityId), int[]> pair in AlloyRatioStore.All)
            {
                // 只认长度 1 —— 弹药、复合材、燃烧厂存的都不是 1，这一条先把它们挡在外面；
                // 烧结析出存的也是 1，靠 Locate 里的 recipeId 核对分开
                if (pair.Value == null || pair.Value.Length != 1) continue;

                PlanetFactory factory = null;

                for (var i = 0; i < data.factoryCount; i++)
                    if (data.factories[i] != null && data.factories[i].planetId == pair.Key.PlanetId)
                        factory = data.factories[i];

                if (factory == null) continue;

                Apply(factory, pair.Key.EntityId, pair.Value);
            }
        }
    }
}
