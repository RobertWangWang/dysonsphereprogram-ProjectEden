using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 活性复合材的「哪种合金 + 放多少 → 哪一级 + 出多少」。
    ///
    /// <b>逐建筑改四个 int，一个都不改数组长度</b>：<c>requires[1]</c>（吃哪种合金）、
    /// <c>requireCounts[0..1]</c>（基体与合金的份数）、<c>products[0]</c>（哪一级）、
    /// <c>productCounts[0]</c>（产量）。长度一变就坏档——<c>AssemblerComponent.Export</c>
    /// 是按 <c>requires</c> / <c>products</c> 的<b>长度</b>决定写几个 served / produced 的。
    /// 这条规矩和合金配比、合金弹药是同一条，见 <see cref="AlloyRatioPatches"/> 的类注释。
    ///
    /// <b>存档沿用 AlloyRatioStore，不加新格式。</b> 它存的就是「每台建筑一个 int[]」，
    /// 这里存 <c>{ 合金物品 ID, 合金份数 }</c> —— 形状和弹药那对合金 ID 一样，
    /// 所以 SaveVersion 不用动。三家共用一个 Store 也是安全的：
    /// 每家的 <c>Locate</c> 都先核对 <c>recipeId</c>，不是自己的机器直接 no-op。
    ///
    /// <b>等级由份数推出来，不单独选。</b> 这是物理口径而不是省事：
    /// Halpin–Tsai 在高刚度比下对填料模量钝化，硬度 50 与 90 的合金在同一配比下
    /// 综合分差不到 5%，<b>配比才是等级的主要杠杆，填料种类是次要的</b>。
    /// </summary>
    [HarmonyPatch]
    internal static class CompositePatches
    {
        private static CompositeConfig Cfg => CompositeRegistry.Config;

        // ── 混合律 ────────────────────────────────────────────

        /// <summary>
        /// 基体与填料按体积分数混出来的四维。四条轴各走各的规律，不是一把加权：
        ///
        /// <list type="bullet">
        /// <item><b>硬度</b>幂平均（p=2，上界）—— 刚性相主导</item>
        /// <item><b>韧性</b>调和平均（被最弱环节拖住），再乘刚化惩罚 ——
        /// 网络三角化之后没有耗散机制了</item>
        /// <item><b>耐蚀</b>线性</item>
        /// <item><b>导电</b>走<b>渗流曲线</b>，见下</item>
        /// </list>
        ///
        /// <b>导电为什么不能线性。</b> 线性混合会让「硬度涨一点、导电就涨一点」，
        /// 而渗流的全部意义恰恰是二者脱钩：越过阈值的那一刻导电跳一个数量级，
        /// 硬度几乎没动——导电只需要<b>一条</b>贯穿路径，承载需要<b>每个方向</b>都有路。
        /// 没有这条曲线，II 级就不存在。
        /// </summary>
        internal static void Mix(int alloyId, float vf,
            out float h, out float t, out float c, out float e)
        {
            CompositeAxes m = Cfg?.matrixAxes ?? new CompositeAxes
                { hardness = 10f, toughness = 92f, corrosion = 72f, conductivity = 2f };

            float ah = MetalPropertyPatches.Axis(alloyId, "hardness");
            float at = MetalPropertyPatches.Axis(alloyId, "toughness");
            float ac = MetalPropertyPatches.Axis(alloyId, "corrosion");
            float ae = MetalPropertyPatches.Axis(alloyId, "conductivity");

            if (vf < 0f) vf = 0f;
            if (vf > 1f) vf = 1f;

            float wm = 1f - vf;

            h = (float)Math.Sqrt(wm * m.hardness * m.hardness + vf * ah * ah);

            double inv = wm / Math.Max(m.toughness, 1f) + vf / Math.Max(at, 1f);
            var tough = (float)(inv > 0.0 ? 1.0 / inv : 0.0);

            // 刚化惩罚：配比越高掉得越快，用平方让它集中在高配比端
            float pen = Cfg != null && Cfg.rigidizePenalty > 0f ? Cfg.rigidizePenalty : 0.55f;

            t = tough * (1f - pen * vf * vf);

            if (t < 1f) t = 1f;

            c = wm * m.corrosion + vf * ac;

            // 渗流：阈值以下几乎只有基体的值；越过之后陡升，
            // 再用 0.6 次幂让它逐步逼近填料——但永远到不了填料本身
            float start = Cfg != null && Cfg.percolationStart > 0f ? Cfg.percolationStart : 0.25f;
            float span = Cfg != null && Cfg.percolationSpan > 0f ? Cfg.percolationSpan : 0.45f;

            float over = (vf - start) / span;

            if (over < 0f) over = 0f;
            if (over > 1f) over = 1f;

            e = m.conductivity + (ae - m.conductivity) * (float)Math.Pow(over, 0.6);

            if (e < m.conductivity) e = m.conductivity;
        }

        /// <summary>这台建筑当前配比下的体积分数。</summary>
        internal static float Vf(int alloyParts) =>
            CompositeRegistry.TotalParts > 0 ? (float)alloyParts / CompositeRegistry.TotalParts : 0f;

        /// <summary>
        /// 产量：按填料韧性的<b>递减回报</b>算，夹在上下限之间。
        /// 和合金弹药同一条判据——脆料成形时崩得多，一炉出的成品就少。
        /// </summary>
        internal static int Yield(int alloyId)
        {
            float anchor = Cfg != null && Cfg.toughnessAnchor > 0f ? Cfg.toughnessAnchor : 55f;
            float exp = Cfg != null && Cfg.toughnessExponent > 0f ? Cfg.toughnessExponent : 0.5f;

            float at = MetalPropertyPatches.Axis(alloyId, "toughness");

            var mul = (float)Math.Pow(Math.Max(at, 0.01f) / anchor, exp);

            float lo = Cfg != null && Cfg.yieldMin > 0f ? Cfg.yieldMin : 0.5f;
            float hi = Cfg != null && Cfg.yieldMax > 0f ? Cfg.yieldMax : 2.0f;

            if (mul < lo) mul = lo;
            if (mul > hi) mul = hi;

            int baseYield = Cfg != null && Cfg.baseYield > 0 ? Cfg.baseYield : 2;
            var v = (int)Math.Round(baseYield * mul);

            return v < 1 ? 1 : v;
        }

        // ── 逐建筑贴配方 ──────────────────────────────────────

        private static bool Locate(PlanetFactory factory, int entityId, out int assemblerId)
        {
            assemblerId = 0;

            if (!CompositeRegistry.Ready || factory?.entityPool == null) return false;
            if (entityId <= 0 || entityId >= factory.entityPool.Length) return false;

            assemblerId = factory.entityPool[entityId].assemblerId;

            if (assemblerId <= 0 || factory.factorySystem?.assemblerPool == null) return false;
            if (assemblerId >= factory.factorySystem.assemblerPool.Length) return false;

            return factory.factorySystem.assemblerPool[assemblerId].recipeId == CompositeRegistry.RecipeId;
        }

        /// <summary>这台建筑现在吃的是哪种合金、放几份。读的是组件上的实时值。</summary>
        internal static bool Current(PlanetFactory factory, int entityId, out int[] state)
        {
            state = null;

            if (!Locate(factory, entityId, out int assemblerId)) return false;

            RecipeExecuteData data = factory.factorySystem.assemblerPool[assemblerId].recipeExecuteData;

            if (data?.requires == null || data.requires.Length < 2) return false;
            if (data.requireCounts == null || data.requireCounts.Length < 2) return false;

            state = new[] { data.requires[1], data.requireCounts[1] };

            return true;
        }

        internal static bool Apply(PlanetFactory factory, int entityId, int[] state)
        {
            if (state == null || state.Length < 2) return false;
            if (!Locate(factory, entityId, out int assemblerId)) return false;

            ref AssemblerComponent comp = ref factory.factorySystem.assemblerPool[assemblerId];

            RecipeExecuteData src = comp.recipeExecuteData;

            if (src?.requires == null || src.requires.Length < 2) return false;
            if (src.requireCounts == null || src.requireCounts.Length < 2) return false;
            if (src.products == null || src.products.Length < 1) return false;

            int alloy = Valid(state[0]) ? state[0] : CompositeRegistry.Candidates[0];

            int total = CompositeRegistry.TotalParts;
            int parts = state[1];

            // 至少留一份基体、也至少放一份合金——两端都空的配方没有意义
            if (parts < 1) parts = 1;
            if (parts > total - 1) parts = total - 1;

            int gi = CompositeRegistry.GradeIndex(parts);
            CompositeRegistry.Grade grade = CompositeRegistry.Grades[gi];

            // Clone() 保住长度——长度一变就坏档
            var requires = (int[])src.requires.Clone();
            var requireCounts = (int[])src.requireCounts.Clone();
            var products = (int[])src.products.Clone();
            var productCounts = (int[])src.productCounts.Clone();

            requires[0] = CompositeRegistry.MatrixItemId;
            requires[1] = alloy;

            requireCounts[0] = total - parts;
            requireCounts[1] = parts;

            products[0] = grade.ItemId;
            productCounts[0] = Yield(alloy);

            int time = grade.Entry.timeSpend > 0 ? grade.Entry.timeSpend : src.timeSpend;

            comp.recipeExecuteData = new RecipeExecuteData(
                requires, requireCounts, products, productCounts,
                time, src.extraTimeSpend, src.productive);

            AlloyRatioStore.Set(factory.planetId, entityId, new[] { alloy, parts });

            Discover(alloy, parts, gi);

            return true;
        }

        private static bool Valid(int itemId) =>
            itemId > 0 && CompositeRegistry.Candidates.Contains(itemId);

        // ── 已试过的组合 ──────────────────────────────────────

        /// <summary>
        /// 已经试出来过的组合。<b>只在内存里</b>——存档块的格式没动，重开会忘掉。
        /// 和弹药那边同一个取舍：等定型了再进存档，免得为一个还在调的功能升 SaveVersion。
        /// </summary>
        private static readonly HashSet<int> Found = new HashSet<int>();

        private static void Discover(int alloy, int parts, int gi)
        {
            if (!Found.Add(alloy * 100 + parts)) return;

            Mix(alloy, Vf(parts), out float h, out float t, out float c, out float e);

            ProjectEdenPlugin.Log.LogInfo(
                $"活性复合材：{LDB.items.Select(alloy)?.name} {parts}/{CompositeRegistry.TotalParts} → " +
                $"{CompositeRegistry.Grades[gi].Entry.name} ×{Yield(alloy)}" +
                $"（硬度 {h:0.0} 韧性 {t:0.0} 耐蚀 {c:0.0} 导电 {e:0.0}）");
        }

        // ── 钩子：和合金配比 / 合金弹药同样的入口 ──────────────

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
            if (!CompositeRegistry.Ready || !Locate(factory, entityId, out int _)) return;

            int[] saved = AlloyRatioStore.TryGet(factory.planetId, entityId, out int[] mine)
                ? mine
                : AlloyRatioStore.GetPlayerDefault(CompositeRegistry.RecipeId);

            if (saved == null || saved.Length < 2)
                saved = new[] { CompositeRegistry.Candidates[0], CompositeRegistry.TotalParts / 2 };

            Apply(factory, entityId, saved);
        }

        /// <summary>
        /// 读档后把存着的组合重贴回去。和合金配比同一个理由：
        /// <c>AssemblerComponent.Import</c> 会从 <c>LDB.recipes</c> 重新推导配方数据，
        /// 把我们那份克隆冲掉。
        /// </summary>
        internal static void ReapplyAll()
        {
            if (!CompositeRegistry.Ready) return;

            GameData data = GameMain.data;

            if (data?.factories == null) return;

            foreach (KeyValuePair<(int PlanetId, int EntityId), int[]> pair in AlloyRatioStore.All)
            {
                PlanetFactory factory = null;

                for (var i = 0; i < data.factoryCount; i++)
                    if (data.factories[i] != null && data.factories[i].planetId == pair.Key.PlanetId)
                        factory = data.factories[i];

                if (factory == null) continue;

                // Locate 会核对 recipeId，不是活性复合材的机器在这里自然 no-op，
                // 所以三家共用一个 Store 是安全的
                if (pair.Value != null && pair.Value.Length == 2) Apply(factory, pair.Key.EntityId, pair.Value);
            }
        }
    }
}
