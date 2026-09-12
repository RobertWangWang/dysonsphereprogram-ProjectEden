using System.Collections.Generic;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 活性复合材的下游：<b>烧结析出</b>。放哪一级进去，就出哪一样原版后期材料。
    ///
    /// <b>为什么是四个不同的产物，而不是一样东西的四个档次。</b>
    /// 四级复合材是<b>互不支配</b>的——它们是四种不同的材料，不是四档升级
    /// （见 活性复合材料V1.md 第 9.5 节，六对全验过）。所以下游只要"想要最好那一级"，
    /// 另外三级立刻变回死内容。接四样<b>你都缺</b>的东西，四级才会被同时需要。
    ///
    /// 机制和 <see cref="CompositePatches"/> 同一套：一条配方，逐台建筑改
    /// <c>requires[0]</c> / <c>products[0]</c>，数组长度一个都不动。
    /// 存档同样沿用 <see cref="AlloyRatioStore"/>，这里存长度 1 的 <c>{ 等级物品 ID }</c>——
    /// 另外两家的 <c>ReapplyAll</c> 都只认长度 2，所以彼此不会串。
    ///
    /// <b>配方名没有以产物开头，这是故意的。</b> 仓库的规矩是"配方名必须以产物打头，
    /// 否则玩家在合成面板里找不到那个产物"——但这条配方的产物是<b>四个原版物品</b>，
    /// 它们在合成面板里本来就各有自己的格子，不靠这条配方被找到。规矩的目的达到了，
    /// 所以形式上可以不守。
    /// </summary>
    [HarmonyPatch]
    internal static class CompositeOutputPatches
    {
        private static bool Locate(PlanetFactory factory, int entityId, out int assemblerId)
        {
            assemblerId = 0;

            if (!CompositeRegistry.OutputReady || factory?.entityPool == null) return false;
            if (entityId <= 0 || entityId >= factory.entityPool.Length) return false;

            assemblerId = factory.entityPool[entityId].assemblerId;

            if (assemblerId <= 0 || factory.factorySystem?.assemblerPool == null) return false;
            if (assemblerId >= factory.factorySystem.assemblerPool.Length) return false;

            return factory.factorySystem.assemblerPool[assemblerId].recipeId
                   == CompositeRegistry.OutputRecipeId;
        }

        /// <summary>这台建筑现在烧的是哪一级。读的是组件上的实时值。</summary>
        internal static bool Current(PlanetFactory factory, int entityId, out int[] state)
        {
            state = null;

            if (!Locate(factory, entityId, out int assemblerId)) return false;

            RecipeExecuteData data = factory.factorySystem.assemblerPool[assemblerId].recipeExecuteData;

            if (data?.requires == null || data.requires.Length < 1) return false;

            state = new[] { data.requires[0] };

            return true;
        }

        internal static bool Apply(PlanetFactory factory, int entityId, int[] state)
        {
            if (state == null || state.Length < 1) return false;
            if (!Locate(factory, entityId, out int assemblerId)) return false;

            ref AssemblerComponent comp = ref factory.factorySystem.assemblerPool[assemblerId];

            RecipeExecuteData src = comp.recipeExecuteData;

            if (src?.requires == null || src.requires.Length < 1) return false;
            if (src.products == null || src.products.Length < 1) return false;

            CompositeRegistry.Output pick = CompositeRegistry.FindOutput(state[0])
                                            ?? CompositeRegistry.Outputs[0];

            // Clone() 保住长度——长度一变就坏档
            var requires = (int[])src.requires.Clone();
            var requireCounts = (int[])src.requireCounts.Clone();
            var products = (int[])src.products.Clone();
            var productCounts = (int[])src.productCounts.Clone();

            requires[0] = pick.GradeItemId;
            requireCounts[0] = pick.Entry.input > 0 ? pick.Entry.input : 3;

            products[0] = pick.TargetItemId;
            productCounts[0] = pick.Entry.count > 0 ? pick.Entry.count : 1;

            int time = pick.Entry.timeSpend > 0 ? pick.Entry.timeSpend : src.timeSpend;

            comp.recipeExecuteData = new RecipeExecuteData(
                requires, requireCounts, products, productCounts,
                time, src.extraTimeSpend, src.productive);

            AlloyRatioStore.Set(factory.planetId, entityId, new[] { pick.GradeItemId });

            Discover(pick);

            return true;
        }

        // ── 已试过的组合 ──────────────────────────────────────

        private static readonly HashSet<int> Found = new HashSet<int>();

        private static void Discover(CompositeRegistry.Output pick)
        {
            if (!Found.Add(pick.GradeItemId)) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"烧结析出：{LDB.items.Select(pick.GradeItemId)?.name} ×{pick.Entry.input} → " +
                $"{LDB.items.Select(pick.TargetItemId)?.name} ×{pick.Entry.count}" +
                $"（{pick.Entry.timeSpend / 60f:0.##} 秒）");
        }

        // ── 钩子：和另外三家同样的入口 ────────────────────────

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

        private static void ApplyDefault(PlanetFactory factory, int entityId)
        {
            if (!CompositeRegistry.OutputReady || !Locate(factory, entityId, out int _)) return;

            int[] saved = AlloyRatioStore.TryGet(factory.planetId, entityId, out int[] mine)
                ? mine
                : AlloyRatioStore.GetPlayerDefault(CompositeRegistry.OutputRecipeId);

            if (saved == null || saved.Length < 1)
                saved = new[] { CompositeRegistry.Outputs[0].GradeItemId };

            Apply(factory, entityId, saved);
        }

        /// <summary>读档后把存着的选择重贴回去。理由同另外三家：Import 会重新推导配方数据。</summary>
        internal static void ReapplyAll()
        {
            if (!CompositeRegistry.OutputReady) return;

            GameData data = GameMain.data;

            if (data?.factories == null) return;

            foreach (KeyValuePair<(int PlanetId, int EntityId), int[]> pair in AlloyRatioStore.All)
            {
                // 只认长度 1 —— 另外两家存的是长度 2，这一条就把它们挡在外面了；
                // Locate 里的 recipeId 核对是第二道
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
