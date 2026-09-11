using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让巨型建筑同时作为行星内物流站工作：
    /// 储物格按当前配方自动配置——原料格挂本地需求、产物格挂本地供应，
    /// 运输机就会自动去行星物流站和大型采矿站取料、把产物送出去。
    ///
    /// 这些建筑的模型本来就是物流运输站改的，prefabDesc 自带停机坪锚点，
    /// 所以只要打开 isStation 就能复用整套运输机调度，不必自己实现配送。
    ///
    /// 移植自 ProjectGenesis 的 MegaAssemblerLogisticPatches。
    /// </summary>
    internal static class MegaStationPatches
    {
        private static MegaBuildingsConfig Config => MegaBuildingRegistry.Config;

        /// <summary>原版最大的星际物流站是 5 格，所以没有任何扩容时 5 一定撑得住。</summary>
        private const int VanillaSafeStorageKinds = 5;

        /// <summary>
        /// 储物格种类数的安全上限，<b>跟着 icons 数组的实际大小走</b>。
        ///
        /// <c>UIEntityBriefInfo._OnUpdate</c> 按储物格种类数遍历 prefab 里那个<b>定长</b>的
        /// icons 数组，超出即 <c>IndexOutOfRangeException</c>——这以前是把巨型建筑钉在 5 格的原因。
        /// 后来 <c>StationExpandPatches</c> 在 <c>_OnCreate</c> 之前把那个数组扩了容，
        /// 上限也就跟着抬起来了，所以这里不再写死 5，而是问它扩到了多大。
        ///
        /// 扩容没生效（配置关掉了）时退回 5，保持原来的行为。
        /// </summary>
        private static int MaxSafeStorageKinds
        {
            get
            {
                int expanded = StationExpandPatches.ExpandedIconKinds;

                return expanded > VanillaSafeStorageKinds ? expanded : VanillaSafeStorageKinds;
            }
        }

        /// <summary>
        /// 每 tick 在储物格与制造台之间搬运：原料格 → served，produced → 产物格。
        /// 由巨型建筑自己的 tick 调用，那时 recipeExecuteData 已是最新。
        /// </summary>
        internal static void UpdateStationStorage(PlanetFactory factory, ref AssemblerComponent component)
        {
            if (Config == null || !Config.stationEnabled) return;

            int stationId = factory.entityPool[component.entityId].stationId;

            if (stationId <= 0)
            {
                // 建筑建造时若 prefabDesc.isStation 还是关的，游戏就不会为它创建 StationComponent，
                // 之后再打开也不会补建。这类建筑只能靠传送带，运输机功能对它无效。
                WarnMissingStation(factory, component.entityId);

                return;
            }

            StationComponent station = factory.transport.stationPool[stationId];

            if (station?.storage == null || station.id != stationId) return;

            EnsureDrones(station);

            RecipeExecuteData executeData = component.recipeExecuteData;

            if (executeData == null || component.recipeId <= 0)
            {
                // 没配方就没有需求/供应，运输机不会动。这是最常见的"看起来没生效"
                WarnNoRecipe(factory, component.entityId);

                return;
            }

            ReportOnce(factory, component.entityId, station);

            int[] requires = executeData.requires;
            int[] requireCounts = executeData.requireCounts;
            int[] products = executeData.products;

            // 直接改 storage 绕过了原版设置物品的入口（PlanetTransport.SetStationStorage），
            // 而供需配对只在那条路径上重建，所以布局一变就得自己刷一次，否则运输机永远配不上对。
            // RefreshStationTraffic 会遍历整颗星球的物流站，只能在真正变化时调用。
            if (SyncStorageLayout(station, requires, products)) factory.transport.RefreshStationTraffic();

            lock (station.storage)
            {
                // 原料：储物格 → served
                for (var i = 0; i < requires.Length; i++)
                {
                    int slot = FindSlot(station, requires[i]);

                    if (slot < 0) continue;

                    int want = requireCounts[i] * Config.requireStockMultiplier - component.served[i];

                    if (want <= 0) continue;

                    int take = station.storage[slot].count < want ? station.storage[slot].count : want;

                    if (take <= 0) continue;

                    station.storage[slot].count -= take;
                    component.served[i] += take;
                }

                // 产物：produced → 储物格
                for (var i = 0; i < products.Length; i++)
                {
                    int produced = component.produced[i];

                    if (produced <= 0) continue;

                    int slot = FindSlot(station, products[i]);

                    if (slot < 0) continue;

                    int room = station.storage[slot].max - station.storage[slot].count;
                    int give = produced < room ? produced : room;

                    if (give <= 0) continue;

                    station.storage[slot].count += give;
                    component.produced[i] -= give;
                }
            }
        }

        /// <summary>
        /// 运输机补满、运送量拉满、储能充满。
        ///
        /// 派车条件是 localPairCount &gt; 0 &amp;&amp; idleDroneCount &gt; 0 &amp;&amp; energy &gt; 800000，
        /// 三者缺一就永远不出车。这些建筑的 pcId 和制造台共用，靠 energyPerTick 慢慢充
        /// 会被制造台的耗电拖累，所以直接把储能给满——效果等同于充电速度拉到最大。
        /// </summary>
        private static void EnsureDrones(StationComponent station)
        {
            station.droneAutoReplenish = true;

            DroneData[] datas = station.workDroneDatas;

            if (datas != null)
            {
                int idle = datas.Length - station.workDroneCount;

                if (station.idleDroneCount < idle) station.idleDroneCount = idle;
            }

            int percent = Config.deliveryDronePercent;

            if (percent > 0 && station.deliveryDrones != percent) station.deliveryDrones = percent;

            if (station.energy < station.energyMax) station.energy = station.energyMax;
        }

        private static bool _warnedMissingStation;
        private static bool _warnedNoRecipe;
        private static bool _reported;

        /// <summary>建筑有站点但没设配方时提示一次。</summary>
        private static void WarnNoRecipe(PlanetFactory factory, int entityId)
        {
            if (_warnedNoRecipe) return;

            _warnedNoRecipe = true;

            ProjectEdenPlugin.Log.LogWarning(
                $"巨型建筑（实体 {entityId}）已有物流站组件，但没有设置配方。" +
                "储物格是按配方自动配置的，没配方就没有需求和供应，运输机不会出车。");
        }

        /// <summary>
        /// 首次成功接管一台巨型建筑时，把派车的三个硬条件打出来：
        /// localPairCount &gt; 0、idleDroneCount &gt; 0、energy &gt; 800000。
        /// 哪一项是 0 就能直接定位问题，不用再靠截图猜。
        /// </summary>
        private static void ReportOnce(PlanetFactory factory, int entityId, StationComponent station)
        {
            if (_reported) return;

            _reported = true;

            int slots = station.storage?.Length ?? 0;
            int droneCapacity = station.workDroneDatas?.Length ?? 0;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑物流站接管成功（实体 {entityId} / 站点 {station.id}）：" +
                $"储物格 {slots} 个，运输机 空闲 {station.idleDroneCount} / 工作 {station.workDroneCount} / 容量 {droneCapacity}，" +
                $"运送量 {station.deliveryDrones}%，储能 {station.energy}/{station.energyMax}，" +
                $"本地供需配对 {station.localPairCount} 组");

            if (station.storage == null) return;

            for (var i = 0; i < station.storage.Length; i++)
            {
                int itemId = station.storage[i].itemId;

                if (itemId <= 0) continue;

                ItemProto proto = LDB.items.Select(itemId);

                ProjectEdenPlugin.Log.LogInfo(
                    $"    储物格 {i}：{proto?.name ?? itemId.ToString()} 本地 {station.storage[i].localLogic} " +
                    $"数量 {station.storage[i].count}/{station.storage[i].max}");
            }
        }

        /// <summary>对没有站点组件的巨型建筑只提示一次，避免每 tick 刷屏。</summary>
        private static void WarnMissingStation(PlanetFactory factory, int entityId)
        {
            if (_warnedMissingStation) return;

            _warnedMissingStation = true;

            int protoId = entityId > 0 && entityId < factory.entityPool.Length ? factory.entityPool[entityId].protoId : 0;
            ItemProto proto = protoId > 0 ? LDB.items.Select(protoId) : null;

            ProjectEdenPlugin.Log.LogWarning(
                $"巨型建筑「{proto?.name ?? protoId.ToString()}」没有物流站组件，运输机功能对它无效。" +
                "这类建筑是在启用物流站能力之前建造的——游戏只在建造时按 prefabDesc 创建 StationComponent，" +
                "之后不会补建。拆掉重建即可。");
        }

        private static int FindSlot(StationComponent station, int itemId)
        {
            if (itemId <= 0) return -1;

            for (var i = 0; i < station.storage.Length; i++)
                if (station.storage[i].itemId == itemId)
                    return i;

            return -1;
        }

        /// <summary>
        /// 把储物格排成「先原料后产物」，原料挂本地需求、产物挂本地供应。
        /// 配方一换就跟着重排，玩家不需要手动配置储物格。
        /// </summary>
        private static bool SyncStorageLayout(StationComponent station, int[] requires, int[] products)
        {
            // 只在前 MaxSafeStorageKinds 格里排布局：UIEntityBriefInfo 的 icons 是定长数组，
            // 按储物格种类数遍历，超出即越界崩溃。
            int total = station.storage.Length;
            int length = total > MaxSafeStorageKinds ? MaxSafeStorageKinds : total;

            var cursor = 0;
            var changed = false;

            for (var i = 0; i < requires.Length && cursor < length; i++, cursor++)
                changed |= SetSlot(station, cursor, requires[i], ELogisticStorage.Demand);

            for (var i = 0; i < products.Length && cursor < length; i++, cursor++)
                changed |= SetSlot(station, cursor, products[i], ELogisticStorage.Supply);

            // 多出来的格子清空，免得换配方后残留旧物品的需求。
            // 这里要一直清到数组末尾而不是 length：早先版本按 12 格建过站点，
            // 那些建筑的 storage 数组已经存进存档，只改 prefabDesc 不会缩短它，
            // 残留在第 5 格之后的物品照样会让简要信息面板越界。
            for (; cursor < total; cursor++)
            {
                if (station.storage[cursor].itemId == 0) continue;
                if (station.storage[cursor].count > 0) continue;

                station.storage[cursor].itemId = 0;
                station.storage[cursor].localLogic = ELogisticStorage.None;
                station.storage[cursor].remoteLogic = ELogisticStorage.None;
                changed = true;
            }

            return changed;
        }

        private static bool SetSlot(StationComponent station, int index, int itemId, ELogisticStorage logic)
        {
            if (station.storage[index].itemId == itemId && station.storage[index].localLogic == logic) return false;

            station.storage[index].itemId = itemId;
            station.storage[index].localLogic = logic;
            // 只做行星内物流，不参与星际配送
            station.storage[index].remoteLogic = ELogisticStorage.None;

            if (station.storage[index].max <= 0) station.storage[index].max = Config.stationMaxItemCount;

            return true;
        }
    }
}
