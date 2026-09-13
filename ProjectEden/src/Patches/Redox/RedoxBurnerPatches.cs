using System;
using System.Threading;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 氧化还原燃烧厂：<b>本仓库第一台既是组装机又是发电机的实体</b>。
    ///
    /// <b>一句被订正的话。</b> CLAUDE.md 长期记着「DSP 的组件模型能表达『烧 X 发电』和
    /// 『把 X 变成 Y』，但表达不了『边把 X 变成 Y 边发电』」——那句话只对了一半。
    /// 它对的是<b>一个组件</b>做不到；错的是由此推出<b>一台建筑</b>做不到。
    /// <c>PlanetFactory.CreateEntityLogicComponents</c> 里 <c>isPowerGen</c>（IL 059E）和
    /// <c>isAssembler</c>（IL 1122）是两个<b>完全独立的顺序 if</b>，而 <c>EntityData</c> 有
    /// 各自的 <c>assemblerId</c> / <c>stationId</c> / <c>powerGenId</c> / <c>powerConId</c>
    /// 四个字段。先例就在本仓库里：综合物流枢纽同时挂 <c>StationComponent</c> 和
    /// <c>DispenserComponent</c>。
    ///
    /// <b>把药柱喂给自己不需要任何 transpiler。</b>
    /// <c>PowerGeneratorComponent.SetNewFuel(itemId, count, inc)</c> 是公开方法，
    /// 自己会从 <c>LDB.items</c> 取 <c>HeatValue</c> 填 <c>fuelHeat</c>（IL 0016~0030）；
    /// <c>EnergyCap_Fuel</c> 只看 <c>fuelCount &gt; 0</c>，<b>烧的时候不查 fuelMask</b>
    /// ——掩码只是传送带取料时的过滤器。所以整条链就是一次方法调用。
    ///
    /// <b>档次由配对决定，产量与配氧比连续。</b> 和合金弹药同一个形状，理由也同一个：
    /// 药柱的能量是 <c>ItemProto.HeatValue</c>，按 proto 存，所以能量密度只能分档；
    /// 而产量走 <c>productCounts[0]</c>，一个物品格都不占。
    /// </summary>
    [HarmonyPatch]
    internal static class RedoxBurnerPatches
    {
        private static RedoxConfig Cfg => RedoxRegistry.Config;

        // ── 数值 ──────────────────────────────────────────────

        /// <summary>配氧比（百分数 → 倍数），夹在配置给的区间里。</summary>
        internal static float Phi(int ratio)
        {
            int lo = Cfg.ratioMin > 0 ? Cfg.ratioMin : 70;
            int hi = Cfg.ratioMax > 0 ? Cfg.ratioMax : 130;

            if (ratio < lo) ratio = lo;
            if (ratio > hi) ratio = hi;

            return ratio / 100f;
        }

        /// <summary>
        /// 药柱的能量密度，单位是 <b>MJ / 每件投料</b>——皮带上真正的货币。
        ///
        /// 化学计量下就是 <c>热值 ÷ (1 + 氧需求/氧供给)</c>：一件还原剂要配
        /// <c>氧需求/氧供给</c> 件氧化剂，两者一起上皮带。
        ///
        /// <b>φ^n 那一项是平衡旋钮，形状照着一个真实效应捏的，但不是从它推出来的。</b>
        /// 真实的一面：富氧燃烧更完全、火焰更热、压出来的药柱更致密。
        /// 被它盖过去的一面：多投的氧化剂本身也是死重，会把密度拉低。
        /// 之所以让前者赢，是因为两边都算的话最优解恒定在 100，滑条就成了摆设——
        /// 合金那一节记过同样的教训（单一最优会让自由度变装饰）。按仓库的规矩，
        /// 旋钮就说是旋钮，别硬套一个推导。
        /// </summary>
        internal static float Density(RedoxRegistry.Agent reducer, RedoxRegistry.Agent oxidizer, int ratio)
        {
            if (reducer == null || oxidizer == null || oxidizer.Oxygen <= 0f) return 0f;

            float stoich = reducer.Oxygen / oxidizer.Oxygen;
            float basis = reducer.Heat / 1e6f / (1f + stoich);

            float e = Cfg.densityExponent > 0f ? Cfg.densityExponent : 0.5f;

            return basis * (float)Math.Pow(Phi(ratio), e);
        }

        /// <summary>密度落在哪一档。取「够得着」的最高一档。</summary>
        internal static int TierIndex(float density)
        {
            var best = 0;

            for (var i = 0; i < RedoxRegistry.Tiers.Count; i++)
                if (density >= RedoxRegistry.Tiers[i].Entry.minDensity)
                    best = i;

            return best;
        }

        /// <summary>
        /// 一次配方要投几件氧化剂。= 还原剂份数 × 化学计量比 × 配氧比。
        /// </summary>
        internal static int OxidizerCount(RedoxRegistry.Agent reducer, RedoxRegistry.Agent oxidizer, int ratio)
        {
            if (reducer == null || oxidizer == null || oxidizer.Oxygen <= 0f) return 1;

            int parts = Cfg.reducerParts > 0 ? Cfg.reducerParts : 8;

            var n = (int)Math.Round(parts * (reducer.Oxygen / oxidizer.Oxygen) * Phi(ratio));

            return n < 1 ? 1 : n;
        }

        /// <summary>
        /// 一次配方出几根药柱。
        ///
        /// <b>贫氧那一侧是严格推出来的，不是罚分</b>：氧化剂只够烧掉 φ 比例的燃料，
        /// 那就只有 φ 比例的燃料进得了药柱，产量直接乘 φ。富氧那一侧燃料已经全烧完，
        /// 再多投也不涨——多出来的氧化剂纯属浪费。
        ///
        /// 取整用 <c>floor</c>，所以<b>产出热值不可能超过投入热值</b>：这条配方在
        /// 能量审计面前是构造性安全的，不需要 <c>energyNote</c> 豁免。
        /// </summary>
        internal static int Yield(RedoxRegistry.Agent reducer, int tier, int ratio)
        {
            if (reducer == null || tier < 0 || tier >= RedoxRegistry.Tiers.Count) return 1;

            long tierHeat = RedoxRegistry.Tiers[tier].Entry.heatValue;

            if (tierHeat <= 0L) return 1;

            int parts = Cfg.reducerParts > 0 ? Cfg.reducerParts : 8;
            float phi = Phi(ratio);

            if (phi > 1f) phi = 1f;

            var n = (int)(parts * reducer.Heat * phi / tierHeat);

            return n < 1 ? 1 : n;
        }

        // ── 逐建筑贴配方 ──────────────────────────────────────

        private static bool Locate(PlanetFactory factory, int entityId, out int assemblerId)
        {
            assemblerId = 0;

            if (!RedoxRegistry.Ready || factory?.entityPool == null) return false;
            if (entityId <= 0 || entityId >= factory.entityPool.Length) return false;

            assemblerId = factory.entityPool[entityId].assemblerId;

            if (assemblerId <= 0 || factory.factorySystem?.assemblerPool == null) return false;
            if (assemblerId >= factory.factorySystem.assemblerPool.Length) return false;

            return factory.factorySystem.assemblerPool[assemblerId].recipeId == RedoxRegistry.RecipeId;
        }

        /// <summary>找下标：这件东西在候选表里排第几。找不到返回 −1。</summary>
        private static int IndexOf(System.Collections.Generic.List<RedoxRegistry.Agent> list, int itemId)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i].ItemId == itemId)
                    return i;

            return -1;
        }

        /// <summary>
        /// 这台建筑现在的状态：<c>[还原剂物品, 氧化剂物品, 配氧比]</c>。
        ///
        /// 前两项读组件上的实时值（那是机器真的在吃的东西），配氧比读
        /// <see cref="AlloyRatioStore"/>——它不在组件上，只体现为 <c>requireCounts[1]</c>，
        /// 从那里反推会被取整抹掉。
        /// </summary>
        internal static bool Current(PlanetFactory factory, int entityId, out int[] state)
        {
            state = null;

            if (!Locate(factory, entityId, out int assemblerId)) return false;

            RecipeExecuteData data = factory.factorySystem.assemblerPool[assemblerId].recipeExecuteData;

            if (data?.requires == null || data.requires.Length < 2) return false;

            int ratio = Cfg.ratioDefault > 0 ? Cfg.ratioDefault : 100;

            if (AlloyRatioStore.TryGet(factory.planetId, entityId, out int[] saved) && saved != null
                && saved.Length >= 3)
                ratio = saved[2];

            state = new[] { data.requires[0], data.requires[1], ratio };

            return true;
        }

        /// <summary>
        /// 把一组状态贴到这台建筑上。
        ///
        /// 改的是 <c>recipeExecuteData</c> 的<b>值</b>而不是<b>长度</b>——
        /// <c>AssemblerComponent.Export</c> 按 <c>requires</c> / <c>products</c> 的长度
        /// 决定写几个 served / produced，长度一变，存档就和读档时重建的数组错位。
        /// 而且必须换成<b>自己新建的那份</b>：<c>RecipeExecuteData</c> 是个 class，
        /// <c>SetRecipe</c> 从 <c>RecipeProto</c> 的静态字典里取，全服同配方的机器共用同一个对象，
        /// 原地改就是改全局配方表（而眼前这台确实变了，所以看起来像只改了这一台）。
        /// </summary>
        internal static bool Apply(PlanetFactory factory, int entityId, int[] state)
        {
            if (state == null || state.Length < 3) return false;
            if (!Locate(factory, entityId, out int assemblerId)) return false;

            ref AssemblerComponent comp = ref factory.factorySystem.assemblerPool[assemblerId];

            RecipeExecuteData src = comp.recipeExecuteData;

            if (src?.requires == null || src.requires.Length < 2) return false;
            if (src.requireCounts == null || src.requireCounts.Length < 2) return false;
            if (src.products == null || src.products.Length < 1) return false;
            if (src.productCounts == null || src.productCounts.Length < 1) return false;

            int ri = IndexOf(RedoxRegistry.Reducers, state[0]);
            int oi = IndexOf(RedoxRegistry.Oxidizers, state[1]);

            if (ri < 0) ri = 0;
            if (oi < 0) oi = 0;

            RedoxRegistry.Agent reducer = RedoxRegistry.Reducers[ri];
            RedoxRegistry.Agent oxidizer = RedoxRegistry.Oxidizers[oi];

            int ratio = state[2];
            int lo = Cfg.ratioMin > 0 ? Cfg.ratioMin : 70;
            int hi = Cfg.ratioMax > 0 ? Cfg.ratioMax : 130;

            if (ratio < lo) ratio = lo;
            if (ratio > hi) ratio = hi;

            int tier = TierIndex(Density(reducer, oxidizer, ratio));

            // Clone() 保住长度
            var requires = (int[])src.requires.Clone();
            var requireCounts = (int[])src.requireCounts.Clone();
            var products = (int[])src.products.Clone();
            var productCounts = (int[])src.productCounts.Clone();

            requires[0] = reducer.ItemId;
            requires[1] = oxidizer.ItemId;

            requireCounts[0] = Cfg.reducerParts > 0 ? Cfg.reducerParts : 8;
            requireCounts[1] = OxidizerCount(reducer, oxidizer, ratio);

            products[0] = RedoxRegistry.Tiers[tier].ItemId;
            productCounts[0] = Yield(reducer, tier, ratio);

            comp.recipeExecuteData = new RecipeExecuteData(
                requires, requireCounts, products, productCounts,
                src.timeSpend, src.extraTimeSpend, src.productive);

            AlloyRatioStore.Set(factory.planetId, entityId,
                new[] { reducer.ItemId, oxidizer.ItemId, ratio });

            AlloyRatioStore.SetPlayerDefault(RedoxRegistry.RecipeId,
                new[] { reducer.ItemId, oxidizer.ItemId, ratio });

            return true;
        }

        // ── 把自己压出来的药柱烧掉 ────────────────────────────

        /// <summary>
        /// 发电机燃料舱的目标存量。<c>fuelCount</c> 是 <b>Int16</b>，所以这个数
        /// 必须远低于 32767——不是留余量，是超了会翻负。
        /// </summary>
        private const int FuelTarget = 3000;

        private static int _noGenLogged;
        private static int _burnLogged;
        private static int _stateLogged;

        /// <summary>
        /// 一次性状态行：把这台机器发电链上的每一环都打出来。
        ///
        /// <b>它存在的理由是一次真实的返工。</b> 「能产燃料包但不发电」这个症状，
        /// 可能出在配方号对不上、发电组件没建起来、没并上电网、燃料没搬进去、
        /// 电网不取电——五个环节，而且每一环失败时都<b>什么都不报</b>。
        /// 逐个猜要五轮，打一行就够了。这是本仓库在配送器那条链上学到的同一件事：
        /// <b>这种链要的是状态转储，不是假设。</b>
        ///
        /// <c>networkId == 0</c> 是这里最值得盯的一个数：<c>NewGeneratorComponent</c>
        /// 自己不并网，发电机是顺着<b>节点</b>进电网的，所以这台机器必须也是 isPowerNode。
        /// 而 prefabDesc 的改动只对<b>新建</b>的实体生效（坑 1），所以老建筑要拆了重建。
        /// </summary>
        private static void ReportOnce(PlanetFactory factory, ref AssemblerComponent component,
                                       int genId, int produced)
        {
            if (Interlocked.Exchange(ref _stateLogged, 1) != 0) return;

            string head =
                $"氧化还原燃烧厂自检：配方 {component.recipeId}（期望 {RedoxRegistry.RecipeId}）" +
                $"　产物槽 {produced}　powerGenId {genId}";

            if (genId <= 0 || factory.powerSystem?.genPool == null
                || genId >= factory.powerSystem.genPool.Length)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    head + "　→ **没有发电机组件**。generator 段没生效，或者这台是改动之前建的——" +
                    "prefabDesc 只对新建实体生效，拆了重建即可");

                return;
            }

            ref PowerGeneratorComponent g = ref factory.powerSystem.genPool[genId];

            string line =
                head +
                $"　networkId {g.networkId}　发电上限 {g.genEnergyPerTick * 60 / 1e9:0.##} GW" +
                $"　燃料 {g.fuelId}×{g.fuelCount}（单根 {g.fuelHeat / 1e6:0.#} MJ）" +
                $"　本 tick 上限 {g.capacityCurrentTick * 60 / 1e9:0.###} GW" +
                $"　实发 {g.generateCurrentTick * 60 / 1e9:0.###} GW";

            if (g.networkId <= 0)
                ProjectEdenPlugin.Log.LogWarning(
                    line + "　→ **networkId 为 0，没并上电网**，所以一度电也送不出去。" +
                    "发电机是顺着电力节点进电网的（只有 PowerSystem.OnNodeAdded 往 " +
                    "PowerNetwork.generators 里加元素），所以这台必须也是 isPowerNode。" +
                    "改动之前建的那些要拆了重建——prefabDesc 只对新建实体生效");
            else
                ProjectEdenPlugin.Log.LogInfo(line);
        }

        /// <summary>
        /// 每 tick 一次：把组装机刚产出的药柱搬进发电机的燃料舱。
        ///
        /// <b>只在 <c>fuelId</c> 相同或燃料舱空着的时候搬。</b> 档次换了而旧药柱还没烧完时
        /// 就等着——<c>SetNewFuel</c> 是整体替换，换一半会把 <c>fuelHeat</c> 改成新档的值，
        /// 而 <c>fuelEnergy</c> 里还留着旧档烧剩的那部分，账就对不上了。
        ///
        /// <b>tick 路径上不许分配</b>：这里没有 new、没有字符串插值、没有闭包；
        /// 两条一次性日志用 <c>Interlocked</c> 抢，消息串也留在抢到之后再拼
        /// （组装机的 tick 是跨星球并行的，普通 bool 守卫挡不住每个线程各打一遍）。
        /// </summary>
        internal static void Burn(PlanetFactory factory, ref AssemblerComponent component)
        {
            if (!RedoxRegistry.Ready) return;
            if (component.recipeId != RedoxRegistry.RecipeId) return;

            int entityId = component.entityId;

            if (entityId <= 0 || factory.entityPool == null || entityId >= factory.entityPool.Length) return;

            int genId = factory.entityPool[entityId].powerGenId;

            // 状态行放在所有早退之前。**「没东西可做」那一支也必须留一行日志**，
            // 否则「产物槽是空的」和「这段代码根本没跑」在日志里长得一模一样——
            // 这条规矩本仓库已经付过四次学费
            ReportOnce(factory, ref component, genId,
                       component.produced != null && component.produced.Length > 0 ? component.produced[0] : -1);

            if (component.produced == null || component.produced.Length < 1) return;
            if (component.produced[0] <= 0) return;

            if (genId <= 0 || factory.powerSystem?.genPool == null || genId >= factory.powerSystem.genPool.Length)
            {
                if (Interlocked.Exchange(ref _noGenLogged, 1) == 0)
                    ProjectEdenPlugin.Log.LogWarning(
                        "氧化还原燃烧厂没有发电机组件（powerGenId 为 0）——" +
                        "它会照常压药柱，但一度电也不发。检查 megabuildings.json 里这一条的 generator 段。");

                return;
            }

            ref PowerGeneratorComponent gen = ref factory.powerSystem.genPool[genId];

            // 产物物品读 recipeExecuteData 而不是 RecipeProto —— 那是这台机器的**自己那份克隆**，
            // 档次由玩家选的配对决定，读配方原型会拿到默认档
            RecipeExecuteData data = component.recipeExecuteData;

            if (data?.products == null || data.products.Length < 1) return;

            int packetId = data.products[0];

            if (packetId <= 0) return;

            // 燃料舱里是别的档次而且还没烧完 —— 等它烧完再换
            if (gen.fuelCount > 0 && gen.fuelId != packetId) return;

            int room = FuelTarget - gen.fuelCount;

            if (room <= 0) return;

            int move = component.produced[0];

            if (move > room) move = room;

            component.produced[0] -= move;

            if (gen.fuelCount <= 0) gen.SetNewFuel(packetId, (short)move, 0);
            else gen.fuelCount = (short)(gen.fuelCount + move);

            if (Interlocked.Exchange(ref _burnLogged, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    "氧化还原燃烧厂已开始自烧：药柱从组装机产物槽直接进燃料舱，不经过传送带。");
        }

        // ── 新建 / 粘贴 / 读档 ────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIAssemblerWindow), "OnRecipePickerReturn")]
        private static void UIAssemblerWindow_OnRecipePickerReturn(UIAssemblerWindow __instance)
        {
            if (__instance?.factory == null || __instance._assemblerId <= 0) return;

            ApplyDefault(__instance.factory,
                __instance.factory.factorySystem.assemblerPool[__instance._assemblerId].entityId);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildingParameters), nameof(BuildingParameters.PasteToFactoryObject))]
        private static void BuildingParameters_PasteToFactoryObject(int objectId, PlanetFactory factory) =>
            ApplyDefault(factory, objectId);

        /// <summary>新建 / 粘贴出来的机器继承玩家最后一次设的那一组。</summary>
        private static void ApplyDefault(PlanetFactory factory, int entityId)
        {
            if (!RedoxRegistry.Ready || factory == null || entityId <= 0) return;
            if (!Locate(factory, entityId, out int _)) return;

            int[] want = AlloyRatioStore.GetPlayerDefault(RedoxRegistry.RecipeId);

            if (want == null || want.Length < 3)
                want = new[]
                {
                    RedoxRegistry.Reducers[0].ItemId,
                    RedoxRegistry.Oxidizers[0].ItemId,
                    Cfg.ratioDefault > 0 ? Cfg.ratioDefault : 100,
                };

            Apply(factory, entityId, want);
        }

        /// <summary>
        /// 读档后把存着的一组重贴回去。理由和合金配比完全一样：
        /// <c>AssemblerComponent.Import</c> 会用 <c>LDB.recipes.Select(recipeId)</c>
        /// 重新推导配方数据，把我们贴上去的克隆冲掉。
        /// </summary>
        internal static int ReapplyAll()
        {
            if (!RedoxRegistry.Ready || GameMain.data?.factories == null) return 0;

            var done = 0;

            foreach (PlanetFactory factory in GameMain.data.factories)
            {
                if (factory?.entityPool == null) continue;

                foreach (System.Collections.Generic.KeyValuePair<(int PlanetId, int EntityId), int[]> kv
                         in AlloyRatioStore.All)
                {
                    if (kv.Key.PlanetId != factory.planetId) continue;
                    if (kv.Value == null || kv.Value.Length < 3) continue;

                    if (Apply(factory, kv.Key.EntityId, kv.Value)) done++;
                }
            }

            return done;
        }
    }
}
