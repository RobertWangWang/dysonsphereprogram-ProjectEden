using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 大型采矿机改造：
    ///   1. 采到的矿石直接换成冶炼产物（铁矿→铁块 等），映射表在 data/advancedminer.json；
    ///   2. 机内缓存从 50 放开到一千万；
    ///   3. 始终按矿物利用科技满级工作：采矿速度取满级倍率，矿物消耗取满级最低值。
    ///
    /// 三项都只作用于大型采矿机（按实体 protoId 判定），普通采矿机和抽水站不受影响。
    /// 思路参考 ProjectGenesis 的 AdvancedMinerPatches。
    /// </summary>
    [HarmonyPatch]
    internal static class AdvancedMinerPatches
    {
        /// <summary>解锁函数 21：miningSpeedScale += value（加性）。矿物利用系列科技用它加采矿速度。</summary>
        private const int FuncMiningSpeedScale = 21;

        /// <summary>解锁函数 20：miningCostRate *= value（乘性）。同一系列科技用它降低矿物消耗。</summary>
        private const int FuncMiningCostRate = 20;

        /// <summary>原版 UnlockTechFunction 里的触底阈值：miningCostRate 低于它就直接归零。</summary>
        private const double MiningCostRateFloor = 3.4e-11;

        /// <summary>原版三条采集分支写死的缓存上限。</summary>
        private const int VanillaCapacity = 50;

        private static readonly Dictionary<int, int> ProductMap = new Dictionary<int, int>();

        private static float _fullScale = -1f;
        private static float _fullCostRate = -1f;
        private static int _fullMinerSpeed = -1;

        private static AdvancedMinerConfig Config => ProjectEdenPlugin.MinerConfig;

        /// <summary>从配置建立矿石 → 产物映射。在 proto 就绪之后调用。</summary>
        internal static void BuildProductMap()
        {
            ProductMap.Clear();

            ApplyStationCapacity();

            if (Config?.productMap == null || !Config.remapProduct) return;

            foreach (OreProduct pair in Config.productMap)
            {
                int oreId = ResolveOre(pair);

                if (oreId <= 0)
                {
                    ProjectEdenPlugin.Log.LogWarning($"产物映射「{pair.comment}」认不出矿石（ore={pair.ore} veinType={pair.veinType}），已跳过");
                    continue;
                }

                int productId = ResolveProduct(pair, oreId);

                if (productId <= 0)
                {
                    ProjectEdenPlugin.Log.LogWarning($"产物映射「{pair.comment}」认不出产物（product={pair.product}），已跳过");
                    continue;
                }

                ItemProto ore = LDB.items.Select(oreId);
                ItemProto product = LDB.items.Select(productId);

                if (ore == null || product == null)
                {
                    ProjectEdenPlugin.Log.LogWarning($"大型采矿机产物映射 {oreId} → {productId} 里有物品不存在，已跳过");
                    continue;
                }

                ProductMap[oreId] = productId;

                ProjectEdenPlugin.Log.LogInfo($"  大型采矿机产物映射：{ore.name} → {product.name}");
            }
        }

        /// <summary>
        /// 矿石 ID：优先用配置里写死的，没写就按矿脉类型现查
        /// （VeinProto.MiningItem 就是这种矿脉挖出来的物品）。
        /// 稀有矿的物品 ID 不好记，用矿脉类型写配置更不容易出错。
        /// </summary>
        private static int ResolveOre(OreProduct pair)
        {
            if (pair.ore > 0) return pair.ore;
            if (pair.veinType <= 0) return 0;

            VeinProto vein = LDB.veins.Select(pair.veinType);

            return vein?.MiningItem ?? 0;
        }

        /// <summary>
        /// 产物 ID：优先用配置里写死的，没写就去找<b>只吃这一种矿</b>的那条原版配方，
        /// 取它的第一个产物。可燃冰→石墨烯、分形硅石→晶格硅、刺笋结晶→碳纳米管
        /// 在原版都正好是这个形状，这样就不必把一串 11xx 的物品 ID 靠记忆写进配置。
        /// 找不到或找到多条都返回 0，由调用方 loud-fail。
        /// </summary>
        private static int ResolveProduct(OreProduct pair, int oreId)
        {
            if (pair.product > 0) return pair.product;

            var found = 0;
            var count = 0;

            foreach (RecipeProto recipe in LDB.recipes.dataArray)
            {
                if (recipe?.Items == null || recipe.Results == null) continue;
                if (recipe.Items.Length != 1 || recipe.Items[0] != oreId) continue;
                if (recipe.Results.Length == 0) continue;

                found = recipe.Results[0];
                count++;
            }

            if (count == 1) return found;

            ProjectEdenPlugin.Log.LogWarning(
                count == 0
                    ? $"找不到只以物品 {oreId} 为唯一原料的原版配方，无法推出产物"
                    : $"以物品 {oreId} 为唯一原料的原版配方有 {count} 条，无法确定推哪一条，请在配置里写死 product");

            return 0;
        }

        /// <summary>
        /// 站点仓储上限。StationComponent.Init 用 PrefabDesc.stationMaxItemCount 初始化每个仓位的 max，
        /// 所以改 prefabDesc 即可——但只影响之后新建的采矿机，存档里已建好的沿用旧值。
        /// </summary>
        private static void ApplyStationCapacity()
        {
            if (Config == null || Config.stationCapacity <= 0) return;

            ItemProto item = LDB.items.Select(Config.minerItemId);
            ModelProto model = item != null ? LDB.models.Select(item.ModelIndex) : null;

            if (model?.prefabDesc == null)
            {
                ProjectEdenPlugin.Log.LogWarning($"找不到大型采矿机（{Config.minerItemId}）的 prefabDesc，仓储上限未改");
                return;
            }

            int before = model.prefabDesc.stationMaxItemCount;

            model.prefabDesc.stationMaxItemCount = Config.stationCapacity;

            ProjectEdenPlugin.Log.LogInfo($"大型采矿机站点仓储上限：{before} → {Config.stationCapacity}（仅对新建生效）");

            if (Config.workEnergyPerTick <= 0) return;

            long beforeEnergy = model.prefabDesc.workEnergyPerTick;

            model.prefabDesc.workEnergyPerTick = Config.workEnergyPerTick;

            ProjectEdenPlugin.Log.LogInfo(
                $"大型采矿机工作功率：{beforeEnergy * 60 / 1000000.0:0.##} MW → {Config.workEnergyPerTick * 60 / 1000000.0:0.##} MW");
        }

        // ── 需求 3：缓存上限 ────────────────────────────────────

        /// <summary>供 IL 调用：这台采矿机的缓存上限。</summary>
        internal static int GetCapacity(ref MinerComponent miner, PlanetFactory factory)
            => IsBoosted(ref miner, factory) ? Config.capacity : VanillaCapacity;

        // ── 需求 1 / 2：产物替换 ────────────────────────────────

        /// <summary>供 IL 调用：把刚从矿脉取到的 productId 换成冶炼产物。</summary>
        internal static void RemapProduct(ref MinerComponent miner, PlanetFactory factory)
        {
            // 默认关闭：大型采矿机是站点型采矿机，站点仓位按矿脉产物建立，
            // 换成冶炼产物后采矿机交接不进站点，表现为面板「产物堆积」且采集速率归零。
            if (Config == null || !Config.remapProduct) return;

            bool advanced = IsAdvancedMiner(ref miner, factory);
            bool mapped = advanced && ProductMap.TryGetValue(miner.productId, out _);

            ReportRemapOnce(ref miner, factory, advanced, mapped);

            if (!advanced) return;

            if (ProductMap.TryGetValue(miner.productId, out int product)) miner.productId = product;
        }

        private static int _remapReported;

        /// <summary>
        /// 第一台走到这里的采矿机，把判断依据<b>无条件</b>报一次——成不成都报。
        ///
        /// 之前这条路整个是哑的：映射表建好了、补丁也注入了，日志里却没有任何一行能说明
        /// 「这台机器到底有没有被认成大型采矿机」。于是「配置没生效」和「识别没通过」
        /// 在日志上完全一样。<b>这个仓库已经为同一个形状付过好几次往返。</b>
        ///
        /// 采矿机 tick 跑在 <c>_miner_parallel</c> 上，一次性标志必须用 Interlocked 抢，
        /// 否则每个线程都会看到未置位、各打一行。
        /// </summary>
        private static void ReportRemapOnce(ref MinerComponent miner, PlanetFactory factory, bool advanced, bool mapped)
        {
            if (Interlocked.Exchange(ref _remapReported, 1) != 0) return;

            int entityId = miner.entityId;
            int protoId = factory?.entityPool != null && entityId > 0 && entityId < factory.entityPool.Length
                ? factory.entityPool[entityId].protoId
                : -1;

            ProjectEdenPlugin.Log.LogInfo(
                $"采矿机产物替换首次判定：entityId={entityId}，该实体 protoId={protoId}，" +
                $"配置 minerItemId={Config?.minerItemId}，当前 productId={miner.productId}，" +
                $"矿机类型={miner.type}，认定为大型采矿机={advanced}，映射表里有这个矿={mapped}，" +
                $"映射条目 {ProductMap.Count} 条");
        }

        /// <summary>
        /// 站点按 MinerComponent.productId 建立仓位。如果仓位先按原矿建好、
        /// 采矿机之后才把产物换成冶炼材料，两边就对不上，表现为「产物堆积」且速率归零。
        /// 所以在站点读取之前先把 productId 换掉，让仓位一开始就是冶炼产物。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.UpdateVeinCollection))]
        private static void StationComponent_UpdateVeinCollection_Prefix(StationComponent __instance, PlanetFactory factory)
        {
            if (Config == null || !Config.remapProduct) return;
            if (factory?.factorySystem?.minerPool == null) return;

            int minerId = __instance.minerId;

            if (minerId <= 0 || minerId >= factory.factorySystem.minerPool.Length) return;

            RemapProduct(ref factory.factorySystem.minerPool[minerId], factory);
        }

        // ── 需求 4：满级采矿速度 ────────────────────────────────

        /// <summary>
        /// miningSpeed 是按值传进来的参数，用 Prefix 直接改就够，不必动 IL。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MinerComponent), nameof(MinerComponent.InternalUpdate))]
        private static void MinerComponent_InternalUpdate_Prefix(ref MinerComponent __instance, PlanetFactory factory,
            float power, ref float miningRate, ref float miningSpeed)
        {
            if (!IsBoosted(ref __instance, factory)) return;

            // 矿物利用满级不只是采矿更快，消耗也降到最低。两者出自同一系列科技，
            // 只给速度不给消耗，等于矿脉被更快地挖空。
            float fullCost = FullMiningCostRate();

            if (fullCost < miningRate) miningRate = fullCost;

            // 加成写进 speed 而不是 miningSpeed：speed 就是面板上的「开采速度」，
            // 只改 miningSpeed 的话实际采矿变快了、滑条却还停在被科技上限钳住的数值。
            // 每 tick 重写一次，界面把它钳回去也会立刻被覆盖。
            int fullSpeed = FullMinerSpeed();

            // 满级科技只是个固定倍率，往往远没吃满防溢出限幅留下的空间。
            // 这里按限幅倒推每台采矿机能承受的最大 speed：
            //     累加量 = speed × miningSpeed × veinCount ≤ maxTimeIncrementPerTick
            // 每台的矿脉数不同，所以逐台算，取两者较大值。
            if (Config.maxOutMinerSpeed)
            {
                float divisor = miningSpeed * MiningMultiplier(ref __instance, factory);

                if (divisor > 0f)
                {
                    double desired = Config.maxTimeIncrementPerTick / divisor;

                    if (desired > int.MaxValue) desired = int.MaxValue;

                    if (desired > fullSpeed) fullSpeed = (int)desired;
                }
            }

            if (__instance.speed < fullSpeed) __instance.speed = fullSpeed;

            // 绕开产量节流。原版每 tick 按 productCount/50 算 speedDamper：
            //     damper = min(1, productCount / 50)
            //     speedDamper = clamp(-2.45 * damper + 2.47, 上限 1)
            // productCount 一旦到 50，speedDamper 就是 0.02，即 2% 速度。
            // 我们单 tick 的产量远超 50，会被立刻钳死，所以对巨型速度的采矿机固定为满速。
            if (Config.overrideSpeedDamper) __instance.speedDamper = 1f;

            SyncStationStorage(ref __instance, factory);
            ApplyFixedPower(ref __instance, factory);
            ReportCeilingOnce(ref __instance, factory, miningSpeed);
            MinerStationSurvey.ReportOnce(ref __instance, factory);

            // MinerComponent 每 tick 往 int 型的 time 上累加 speed * miningSpeed * veinCount，
            // 越过 int 上限会掉进原版兜底反而被钳死，所以先行限幅
            float perTick = __instance.speed * MiningMultiplier(ref __instance, factory);
            float limit = Config.maxTimeIncrementPerTick;

            if (perTick > 0f && miningSpeed * perTick > limit) miningSpeed = limit / perTick;

            // 外星矿脉的钻头消耗：排在最后，因为它要用限幅完毕的 miningSpeed
            // 算这一 tick 到底挖出多少件；而且它只对那一种矿脉生效
            AlienVeinPatches.Tick(ref __instance, factory, power, ref miningSpeed, perTick);
        }

        // ── 一次性日志 ────────────────────────────────────────
        //
        // 这些方法跑在 GameLogic._miner_parallel 上，多个线程会同时看到缓存还没填好、
        // 各算各的、各打各的日志（实测刷了三十多行）。计算本身是幂等的，重算无害，
        // 真正要去重的是日志。用 Interlocked 抢占，并且<b>先抢占再拼字符串</b>——
        // 否则每帧都会在 tick 路径上分配一个插值字符串。
        private static int _logCeiling, _logMinerSpeed, _logCostRate, _logSpeedScale;

        private static bool ClaimLog(ref int flag) => Interlocked.Exchange(ref flag, 1) == 0;


        /// <summary>
        /// 一次性打印实际产量与理论上限。
        ///
        /// 原版公式（MinerComponent.InternalUpdate）：
        ///     time += (int)(power * speedDamper * speed * miningSpeed * veinCount)
        ///     产出个数 = time / period
        /// 所以每 tick 产量 = 上述累加值 / period，
        /// 而累加值受 time（Int32）约束，本 mod 用 maxTimeIncrementPerTick 预留了余量。
        /// </summary>
        private static void ReportCeilingOnce(ref MinerComponent miner, PlanetFactory factory, float miningSpeed)
        {
            if (miner.period <= 0) return;

            double perTick = (double)miner.speed * miningSpeed * MiningMultiplier(ref miner, factory);
            double limited = perTick > Config.maxTimeIncrementPerTick ? Config.maxTimeIncrementPerTick : perTick;

            double itemsPerTick = limited / miner.period;
            double ceilingPerTick = (double)int.MaxValue / miner.period;

            if (!ClaimLog(ref _logCeiling)) return;

            // 抽水站也走 MinerComponent，它的 veinCount 是 0，别让日志看着像采矿机坏了
            string what = miner.type == EMinerType.Water ? "抽水站"
                        : miner.type == EMinerType.Oil ? "原油萃取站"
                        : "大型采矿机";

            ProjectEdenPlugin.Log.LogInfo(
                $"{what}产量：period={miner.period}，速率因子 {MiningMultiplier(ref miner, factory):0.###}，" +
                $"当前约 {itemsPerTick * 60:N0}/秒（{itemsPerTick * 3600:N0}/分钟）；" +
                $"该建筑的理论上限约 {ceilingPerTick * 60:N0}/秒——受 time 为 Int32 约束");
        }

        /// <summary>
        /// 拦下按速度平方缩放的功耗。
        ///
        /// 原版 MinerComponent.SetPCState 是：
        ///     SetRequiredEnergy(speedDamper * speed * speed / 100000000.0)
        /// speed=10000（100%）时比值为 1。我们把 speed 提到 1001 万，比值就变成一百万左右，
        /// 22.5 MW 被放大成 20 TW 级。这里对本 mod 提速过的采矿机直接取比值 1，
        /// 功率就等于 workEnergyPerTick 本身，不随开采速度浮动。
        ///
        /// 判定不用 protoId：SetPCState 拿不到 PlanetFactory，没法查实体。
        /// 改用 speed 是否等于我们写入的满级值——原版受科技上限约束，不可能达到该值。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MinerComponent), nameof(MinerComponent.SetPCState))]
        private static bool MinerComponent_SetPCState_Prefix(ref MinerComponent __instance, PowerConsumerComponent[] pcPool)
        {
            if (Config == null || Config.workEnergyPerTick <= 0) return true;
            if (__instance.speed < _fullMinerSpeed || _fullMinerSpeed <= 0) return true;

            int pcId = __instance.pcId;

            if (pcId <= 0 || pcPool == null || pcId >= pcPool.Length) return true;

            __instance.DetermineState();

            pcPool[pcId].SetRequiredEnergy(__instance.workstate > EWorkState.Idle ? 1.0 : 0.0);

            return false;
        }

        /// <summary>
        /// 固定工作功率。
        ///
        /// 功耗来自 PowerConsumerComponent.workEnergyPerTick，它在建造时从 prefabDesc 取值并存进存档，
        /// 所以改 prefabDesc 只对新建的生效；已建成的要在这里覆盖。
        /// 每 tick 写一次即可，值相同时不做任何事。
        /// </summary>
        private static void ApplyFixedPower(ref MinerComponent miner, PlanetFactory factory)
        {
            // 只覆盖大型采矿机的功率。抽水类设备保留各自的原版功率——
            // 它们同样不会随速度平方缩放，因为 SetPCState 的比值已被固定为 1。
            if (!IsAdvancedMiner(ref miner, factory)) return;

            long energy = Config.workEnergyPerTick;

            if (energy <= 0) return;

            int pcId = miner.pcId;

            if (pcId <= 0) return;

            PowerConsumerComponent[] pool = factory.powerSystem?.consumerPool;

            if (pool == null || pcId >= pool.Length) return;

            if (pool[pcId].workEnergyPerTick != energy) pool[pcId].workEnergyPerTick = energy;
        }

        /// <summary>
        /// 把站点的采集物 ID 和仓位物品都对齐到替换后的产物。
        ///
        /// UpdateVeinCollection 开头有一道门禁：
        ///     if (miner.productId != station.collectionIds[0]) return;
        /// 只要两者不等，整段交接直接跳过，采矿机产物堆在机内，
        /// 表现为面板「产物堆积」、采集速率归零。仓位物品对上也没用——门禁看的是 collectionIds。
        ///
        /// 这两个字段都存进存档，且只在矿脉集合变化时才重建，所以每 tick 顺手校正一次。
        /// 对齐之后它们不再是映射表的键，查不中即结束，不会反复改写。
        /// </summary>
        private static void SyncStationStorage(ref MinerComponent miner, PlanetFactory factory)
        {
            if (ProductMap.Count == 0) return;

            EntityData[] entityPool = factory.entityPool;
            int entityId = miner.entityId;

            if (entityId <= 0 || entityId >= entityPool.Length) return;

            int stationId = entityPool[entityId].stationId;

            if (stationId <= 0) return;

            StationComponent[] stationPool = factory.transport?.stationPool;

            if (stationPool == null || stationId >= stationPool.Length) return;

            StationComponent station = stationPool[stationId];

            if (station?.storage == null) return;

            // 门禁字段：不改这个，交接永远不会发生
            if (station.collectionIds != null)
                for (var i = 0; i < station.collectionIds.Length; i++)
                {
                    int id = station.collectionIds[i];

                    if (id > 0 && ProductMap.TryGetValue(id, out int mapped)) station.collectionIds[i] = mapped;
                }

            for (var i = 0; i < station.storage.Length; i++)
            {
                int itemId = station.storage[i].itemId;

                if (itemId <= 0) continue;

                if (ProductMap.TryGetValue(itemId, out int product)) station.storage[i].itemId = product;
            }
        }

        /// <summary>
        /// 满级采矿速度换算成 MinerComponent.speed 的百分比值（100 = 100%）。
        /// 上限压在 int 的安全范围内，限幅逻辑另在 Prefix 里按 veinCount 处理。
        /// </summary>
        private static int FullMinerSpeed()
        {
            if (_fullMinerSpeed > 0) return _fullMinerSpeed;

            // speed 的单位是 100 = 1%（原版面板 300% 对应 speed 30000），
            // 所以倍率要乘 10000 才是 speed，乘 100 只是百分数
            double speed = FullMiningSpeedScale() * 10000.0;

            if (speed > 1.0e9) speed = 1.0e9;
            if (speed < 10000.0) speed = 10000.0;

            _fullMinerSpeed = (int)speed;

            if (ClaimLog(ref _logMinerSpeed))
                ProjectEdenPlugin.Log.LogInfo($"大型采矿机开采速度设定值：speed={_fullMinerSpeed}（面板 {_fullMinerSpeed / 100}%）");

            return _fullMinerSpeed;
        }

        /// <summary>
        /// 矿物利用科技全部研究到满级时的 miningCostRate。只算一次。
        /// 与速度不同，这一项是乘性的：每级乘上解锁值（小于 1），满级后基本贴到 0。
        /// </summary>
        private static float FullMiningCostRate()
        {
            if (_fullCostRate >= 0f) return _fullCostRate;

            // 配置里直接指定时不做推算：矿物利用率的上限就是「完全不消耗矿脉」，
            // 与其依赖对科技数据结构的推断，不如给一个确定值。
            if (Config.forceMiningCostRate >= 0f)
            {
                _fullCostRate = Config.forceMiningCostRate;

                if (ClaimLog(ref _logCostRate))
                    ProjectEdenPlugin.Log.LogInfo($"大型采矿机矿物消耗倍率（配置指定）：{_fullCostRate:G6}");

                return _fullCostRate;
            }

            var rate = 1.0; // 原版基准值

            foreach (TechProto tech in LDB.techs.dataArray)
            {
                if (tech?.UnlockFunctions == null || tech.UnlockValues == null) continue;

                int levels = LevelsToMax(tech);

                if (levels <= 0) continue;

                int count = tech.UnlockFunctions.Length < tech.UnlockValues.Length
                    ? tech.UnlockFunctions.Length
                    : tech.UnlockValues.Length;

                for (var i = 0; i < count; i++)
                {
                    if (tech.UnlockFunctions[i] != FuncMiningCostRate) continue;

                    double value = tech.UnlockValues[i];

                    if (value <= 0.0 || value >= 1.0) continue;

                    rate *= System.Math.Pow(value, levels);
                }
            }

            // 与原版 UnlockTechFunction 一致：低于阈值直接归零（矿脉不再消耗）
            if (rate < MiningCostRateFloor) rate = 0.0;

            _fullCostRate = (float)rate;

            if (ClaimLog(ref _logCostRate))
                ProjectEdenPlugin.Log.LogInfo($"大型采矿机矿物消耗倍率（矿物利用满级）：{_fullCostRate:G6}");

            return _fullCostRate;
        }

        /// <summary>这项科技从当前等级到（受配置封顶的）满级还有多少级。</summary>
        private static int LevelsToMax(TechProto tech)
        {
            int maxLevel = tech.MaxLevel;

            if (maxLevel > Config.maxMiningTechLevel) maxLevel = Config.maxMiningTechLevel;

            return maxLevel - tech.Level + 1;
        }

        /// <summary>矿物利用科技全部研究到满级时的 miningSpeedScale。只算一次。</summary>
        private static float FullMiningSpeedScale()
        {
            if (_fullScale > 0f) return _fullScale;

            var scale = 1f; // 原版基准值

            foreach (TechProto tech in LDB.techs.dataArray)
            {
                if (tech?.UnlockFunctions == null || tech.UnlockValues == null) continue;

                int levels = LevelsToMax(tech);

                if (levels <= 0) continue;

                int count = tech.UnlockFunctions.Length < tech.UnlockValues.Length
                    ? tech.UnlockFunctions.Length
                    : tech.UnlockValues.Length;

                for (var i = 0; i < count; i++)
                    if (tech.UnlockFunctions[i] == FuncMiningSpeedScale)
                        scale += (float)tech.UnlockValues[i] * levels;
            }

            _fullScale = scale;

            if (ClaimLog(ref _logSpeedScale))
                ProjectEdenPlugin.Log.LogInfo($"大型采矿机采矿速度倍率（矿物利用满级）：{scale:0.##}");

            return _fullScale;
        }

        // ── IL 改写：常量上限与产物替换 ──────────────────────────

        private static readonly FieldInfo MinerProductCountField =
                                              AccessTools.Field(typeof(MinerComponent), nameof(MinerComponent.productCount)),
                                          MinerProductIdField =
                                              AccessTools.Field(typeof(MinerComponent), nameof(MinerComponent.productId)),
                                          VeinProductIdField = AccessTools.Field(typeof(VeinData), nameof(VeinData.productId)),
                                          MinerVeinsField = AccessTools.Field(typeof(MinerComponent), nameof(MinerComponent.veins));

        private static readonly MethodInfo MapMinerProductMethod =
                                               AccessTools.Method(typeof(AdvancedMinerPatches), nameof(MapMinerProduct));

        // ── 统计面板：让换过产物的采矿机能被算进去 ──────────────
        //
        // 参考速率（UIReferenceSpeedTip.AddEntryDataWithFactory）和生产统计的理论产能
        // （ProductionExtraInfoCalculator.CalculateFactory）都是这么判的：
        //     if (veinPool[miner.veins[...]].productId == 要统计的物品) 才把这台算进去
        // 取的是<b>矿脉的原始产物</b>，不是我们改过的 MinerComponent.productId，
        // 所以铜矿 → 铜块之后，铜块永远匹配不上，采矿机就从统计里消失了。
        //
        // 两处方法的指令形状完全一样：
        //     ldloc <采矿机> ; ldfld MinerComponent::veins ; … ; ldelema VeinData ; ldfld VeinData::productId
        // 所以在每个 ldfld VeinData::productId 之后补一次映射即可。装载采矿机的那条 ldloc
        // 靠往回找 ldfld MinerComponent::veins 定位——它的前一条就是，两处都成立。

        /// <summary>供 IL 调用：把矿脉的原始产物换成这台采矿机实际产出的物品。</summary>
        internal static int MapMinerProduct(int veinProductId, ref MinerComponent miner, PlanetFactory factory)
        {
            if (ProductMap.Count == 0) return veinProductId;

            // 只有大型采矿机换产物，普通采矿机 / 抽水站要原样返回
            if (!IsAdvancedMiner(ref miner, factory)) return veinProductId;

            return ProductMap.TryGetValue(veinProductId, out int product) ? product : veinProductId;
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIReferenceSpeedTip), nameof(UIReferenceSpeedTip.AddEntryDataWithFactory))]
        [HarmonyPatch(typeof(ProductionExtraInfoCalculator), nameof(ProductionExtraInfoCalculator.CalculateFactory))]
        private static IEnumerable<CodeInstruction> MinerProductStat_Transpiler(IEnumerable<CodeInstruction> instructions,
                                                                                MethodBase original)
        {
            var list = new List<CodeInstruction>(instructions);

            if (VeinProductIdField == null || MinerVeinsField == null || MapMinerProductMethod == null)
            {
                ProjectEdenPlugin.Log.LogError("解析不到 VeinData.productId / MinerComponent.veins，统计面板不会计入换产物的采矿机");

                return list;
            }

            // 两个方法都是实例方法，但 PlanetFactory 参数的位置不同，按签名找
            int factoryArg = -1;
            ParameterInfo[] parameters = original.GetParameters();

            for (var i = 0; i < parameters.Length; i++)
                if (parameters[i].ParameterType == typeof(PlanetFactory))
                {
                    factoryArg = i + (original.IsStatic ? 0 : 1);

                    break;
                }

            if (factoryArg < 0)
            {
                ProjectEdenPlugin.Log.LogError($"{original.DeclaringType?.Name}.{original.Name} 没有 PlanetFactory 参数，跳过");

                return list;
            }

            var patched = 0;

            // 倒着走，插入不会打乱还没处理到的下标
            for (int i = list.Count - 1; i >= 1; i--)
            {
                if (!list[i].LoadsField(VeinProductIdField)) continue;

                CodeInstruction loadMiner = null;

                for (int j = i - 1; j >= 1 && j > i - 12; j--)
                    if (list[j].LoadsField(MinerVeinsField))
                    {
                        loadMiner = list[j - 1];

                        break;
                    }

                if (loadMiner == null) continue;

                list.InsertRange(i + 1, new[]
                {
                    // 新建指令而不是复用对象，免得把原指令上的跳转标签也复制一份
                    new CodeInstruction(loadMiner.opcode, loadMiner.operand),
                    LoadArg(factoryArg),
                    new CodeInstruction(OpCodes.Call, MapMinerProductMethod),
                });

                patched++;
            }

            if (patched == 0)
                ProjectEdenPlugin.Log.LogError(
                    $"{original.DeclaringType?.Name}.{original.Name} 没找到采矿机的产物判定，统计面板不会计入换产物的采矿机");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"{original.DeclaringType?.Name}.{original.Name}：已让 {patched} 处采矿机产物判定跟随替换后的产物");

            return list;
        }

        private static CodeInstruction LoadArg(int index)
        {
            if (index == 0) return new CodeInstruction(OpCodes.Ldarg_0);
            if (index == 1) return new CodeInstruction(OpCodes.Ldarg_1);
            if (index == 2) return new CodeInstruction(OpCodes.Ldarg_2);
            if (index == 3) return new CodeInstruction(OpCodes.Ldarg_3);

            return new CodeInstruction(OpCodes.Ldarg_S, (byte)index);
        }

        private static readonly MethodInfo GetCapacityMethod =
                                               AccessTools.Method(typeof(AdvancedMinerPatches), nameof(GetCapacity)),
                                           RemapProductMethod =
                                               AccessTools.Method(typeof(AdvancedMinerPatches), nameof(RemapProduct));

        /// <summary>
        /// 两处改写都在 MinerComponent.InternalUpdate 里，没有 Prefix/Postfix 的替代方案：
        ///   · 缓存上限 50 是方法体内的字面量，且三条分支各有一处；
        ///   · productId 必须在同一 tick 内、送上传送带之前就换掉。
        ///
        /// 注意 InternalUpdate 是结构体上的实例方法，所以 ldarg.0 是 MinerComponent&amp;、ldarg.1 才是 factory。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(MinerComponent), nameof(MinerComponent.InternalUpdate))]
        private static IEnumerable<CodeInstruction> MinerComponent_InternalUpdate_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);

            var capacityPatched = 0;
            var productPatched = 0;
            var comparePatched = 0;

            for (int i = list.Count - 2; i >= 1; i--)
            {
                // ── productCount 与 50 比较 → 换成按机型取上限 ──
                // 特征：ldfld productCount ; ldc.i4.s 50
                if (list[i].LoadsConstant(VanillaCapacity) && list[i - 1].LoadsField(MinerProductCountField))
                {
                    // 就地改写而不是新建指令：原指令上可能挂着跳转标签，换掉对象会让分支目标丢失
                    list[i].opcode = OpCodes.Ldarg_0;
                    list[i].operand = null;

                    list.InsertRange(i + 1, new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(OpCodes.Call, GetCapacityMethod),
                    });

                    capacityPatched++;

                    continue;
                }

                // ── 那道「产物对不上就不开采」的判断 ──
                //
                // 原版：if (productId != 0 && productId != 矿脉产物) 跳过本次开采。
                // 只改赋值点不改这里，替换之后 productId 是高能石墨、矿脉产物还是煤矿，
                // <b>判断永远不成立，采矿机从此不再开采</b>。
                // 这和自动集装机那条「同一个值既当赋值又当比较，只改一半」是同一个形状。
                //
                // 特征：ldfld VeinData::productId，而后面跟的<b>不是</b> stfld MinerComponent::productId
                // （全方法只有三处读取：@015E 是这道判断，@0170 / @04DB 是两处赋值）
                if (list[i].LoadsField(VeinProductIdField) && !list[i + 1].StoresField(MinerProductIdField))
                {
                    list.InsertRange(i + 1, new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(OpCodes.Call, MapMinerProductMethod),
                    });

                    comparePatched++;

                    continue;
                }

                // ── 从矿脉取到 productId 之后立刻替换 ──
                // 特征：ldfld VeinData::productId ; stfld MinerComponent::productId
                if (list[i].StoresField(MinerProductIdField) && list[i - 1].LoadsField(VeinProductIdField))
                {
                    list.InsertRange(i + 1, new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(OpCodes.Call, RemapProductMethod),
                    });

                    productPatched++;
                }
            }

            if (capacityPatched == 0 || productPatched != 2 || comparePatched != 1)
                ProjectEdenPlugin.Log.LogError(
                    $"MinerComponent.InternalUpdate 改写不完整：缓存上限 {capacityPatched} 处、产物替换 {productPatched} 处" +
                    $"（应为 2）、产物判断 {comparePatched} 处（应为 1）。游戏版本变化后需要重新对照 IL。");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"MinerComponent.InternalUpdate：缓存上限改写 {capacityPatched} 处，" +
                    $"产物替换注入 {productPatched} 处，产物判断接管 {comparePatched} 处");

            return list;
        }

        /// <summary>
        /// 是否由本 mod 接管。大型采矿机按 protoId 认，抽水类设备按 MinerComponent.type 认
        /// ——抽水站、大抽水机等都是 EMinerType.Water，不必逐个列 ID。
        /// </summary>
        /// <summary>
        /// 每 tick 累加到 time 上的产量乘数，三条采集分支各不相同（见 MinerComponent.InternalUpdate）：
        ///     矿脉 time += power × speedDamper × speed × miningSpeed × <b>veinCount</b>
        ///     原油 time += power × speedDamper × speed × miningSpeed × <b>amount × oilSpeedMultiplier</b>
        ///     抽水 time += power × speedDamper × speed × miningSpeed
        /// 防溢出倒推 speed 时必须用对应的乘数，否则原油那条会按 1 估算、把 speed 抬过头，
        /// time 溢出 Int32 之后反而掉进原版兜底被钳死。
        /// </summary>
        private static float MiningMultiplier(ref MinerComponent miner, PlanetFactory factory)
        {
            if (miner.type == EMinerType.Vein) return miner.veinCount;
            if (miner.type == EMinerType.Oil) return OilRate(ref miner, factory);

            return 1f;
        }

        /// <summary>原油萃取站的速率因子：油井储量 × 全局倍率，也就是面板上的「原油速率」。</summary>
        private static float OilRate(ref MinerComponent miner, PlanetFactory factory)
        {
            if (miner.veinCount <= 0 || miner.veins == null || miner.veins.Length == 0) return 1f;

            VeinData[] pool = factory?.veinPool;

            if (pool == null) return 1f;

            int veinId = miner.veins[0];

            if (veinId <= 0 || veinId >= pool.Length) return 1f;

            float rate = pool[veinId].amount * VeinData.oilSpeedMultiplier;

            return rate > 0f ? rate : 1f;
        }

        private static bool IsBoosted(ref MinerComponent miner, PlanetFactory factory)
        {
            if (Config == null) return false;

            if (Config.boostWaterPumps && miner.type == EMinerType.Water) return true;
            if (Config.boostOilExtractors && miner.type == EMinerType.Oil) return true;

            return IsAdvancedMiner(ref miner, factory);
        }

        private static bool IsAdvancedMiner(ref MinerComponent miner, PlanetFactory factory)
        {
            if (factory == null || Config == null) return false;

            int entityId = miner.entityId;

            if (entityId <= 0) return false;

            EntityData[] pool = factory.entityPool;

            return entityId < pool.Length && pool[entityId].protoId == Config.minerItemId;
        }
    }
}
