// 本文件移植自 ProjectGenesis（创世之书），属于其衍生作品。
// Portions of this file are derived from ProjectGenesis (GenesisBook).
//
//     Copyright (C) 2022-2026 Awbugl and ProjectGenesis contributors
//     https://github.com/Awbugl/ProjectGenesis
//
// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Collections.Concurrent;
using System.Threading;
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
        internal static int MaxSafeStorageKinds
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
            if (SyncStorageLayout(factory, station, requires, products)) factory.transport.RefreshStationTraffic();

            lock (station.storage)
            {
                // 原料：储物格 → served
                for (var i = 0; i < requires.Length; i++)
                {
                    int slot = FindSlot(station, requires[i], ELogisticStorage.Demand);

                    if (slot < 0) continue;

                    int want = requireCounts[i] * Config.requireStockMultiplier - component.served[i];

                    if (want <= 0) continue;

                    int take = station.storage[slot].count < want ? station.storage[slot].count : want;

                    if (take <= 0) continue;

                    // **品质要按件数一起扣。** 只扣件数不扣品质，留下的货就白白继承了
                    // 整格的点数——这和本仓库在 `StationStore.inc` 上记过的
                    // 「每次搬运都白送一次增产」是同一个形状，只是换了一个孪生字段。
                    if (QualityAccess.Ready)
                    {
                        int qua = QualityAccess.GetStationQua(ref station.storage[slot]);

                        if (qua > 0)
                            QualityAccess.SetStationQua(ref station.storage[slot],
                                qua - (int)((long)qua * take / station.storage[slot].count));
                    }

                    station.storage[slot].count -= take;
                    component.served[i] += take;
                }

                // 产物：produced → 储物格
                for (var i = 0; i < products.Length; i++)
                {
                    int produced = component.produced[i];

                    if (produced <= 0) continue;

                    int slot = FindSlot(station, products[i], ELogisticStorage.Supply);

                    if (slot < 0) continue;

                    int room = station.storage[slot].max - station.storage[slot].count;
                    int give = produced < room ? produced : room;

                    if (give <= 0) continue;

                    // **机器自己造出来的那部分品质跟着货走，而且要从缓冲区里扣掉。**
                    // 只搬件数不扣分，留在 quaProduced 里的那份会在下一次出货时再发一遍。
                    // 比例要拿扣减前的件数算，所以在 produced[i] -= give 之前。
                    int own = QualityCraftOut.DrainSlot(ref component, i, give, produced);

                    station.storage[slot].count += give;
                    component.produced[i] -= give;

                    if (own > 0) QualityAccess.GiveStationQua(ref station.storage[slot], own);

                    // 提纯配方额外的铸造分：产物落进本建筑自己的槽位时按件数加分。
                    // 它和上面那份是两回事——一个来自投料，一个来自配方等级，不会重复计数。
                    // 非提纯配方在表里查不到，一次字典查找就返回，tick 上不分配。
                    QualityRefineryPatches.OnProduced(ref station.storage[slot],
                        component.recipeId, give);
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
        /// <summary>
        /// 储物格转储<b>按建筑类型各报一次</b>，不是全局一次。
        ///
        /// 原来是一个全局 bool，于是<b>八种巨型建筑里只有最先 tick 到的那一种</b>
        /// 会打出储物格清单——催化反应器的催化剂槽到底排出来没有，在日志里根本查不到，
        /// 因为那一次早被熔岩冷却厂用掉了。诊断行「只打一次」的目的是不刷屏，
        /// 而不是只覆盖一种建筑；按 protoId 分桶两者都满足。
        ///
        /// 组装机 tick 跑在 <c>_assembler_parallel</c> 上，所以用 <c>ConcurrentDictionary</c>
        /// 的 <c>TryAdd</c> 领号，不用普通集合（CLAUDE.md 第 4 号坑）。
        /// </summary>
        private static readonly ConcurrentDictionary<int, byte> Reported = new ConcurrentDictionary<int, byte>();

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
            int protoId = factory.entityPool[entityId].protoId;

            if (!Reported.TryAdd(protoId, 0)) return;

            int slots = station.storage?.Length ?? 0;
            int droneCapacity = station.workDroneDatas?.Length ?? 0;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑物流站接管成功（{LDB.items.Select(protoId)?.name ?? protoId.ToString()}"
                + $" / 实体 {entityId} / 站点 {station.id}）：" +
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

        /// <summary>
        /// 找这种物品的储物格。<paramref name="logic"/> 是<b>期望的方向</b>：
        /// 原料要本地需求那一格，产物要本地供应那一格。
        ///
        /// <b>同一种物品可以同时站在原料和产物两边，而提纯正是这个形状。</b>
        /// 提纯的产物和原料是同一种金属，差别只在品质——而品质是<b>容器</b>的属性，
        /// 不是物品原型的属性，所以它不可能变成另一个 <c>ItemProto</c>
        /// （每种金属再开一个「高纯 X」物品就是在枚举，而枚举正是万用模板要消掉的东西）。
        ///
        /// 只按物品 ID 找的话，两个循环都会命中<b>第一格</b>，也就是进料的需求格：
        /// 提纯出来的金属被倒回进料格，<b>永远出不了厂</b>，而屏幕上是「机器在转、
        /// 电在耗、产量为零」——本仓库最难查的那种症状。
        ///
        /// 方向对不上就退回只按 ID 找，所以配方两边没有重复物品时行为和以前完全一样。
        /// </summary>
        private static int FindSlot(StationComponent station, int itemId, ELogisticStorage logic)
        {
            if (itemId <= 0) return -1;

            var any = -1;

            for (var i = 0; i < station.storage.Length; i++)
            {
                if (station.storage[i].itemId != itemId) continue;

                if (station.storage[i].localLogic == logic) return i;

                if (any < 0) any = i;
            }

            return any;
        }

        /// <summary>
        /// 按当前配方重排储物格：原料挂本地需求、产物挂本地供应。
        ///
        /// <b>这里不再是「从 0 号格往后顺次写」。</b> 那种写法有两个后果，都被玩家撞到了：
        /// 直接覆盖 <c>itemId</c> 会让 100 个电路板当场变成 100 个齿轮（物品变质）；
        /// 改成「先把旧货搬到空格」之后又变成「换个配方，旧货噌地跳到最后两格去了」。
        ///
        /// 真正的答案是<b>谁都别动</b>：旧货留在它原来的格子里，只把方向改成本地供应
        /// 让运输机把它取走；新配方要的货优先落在已经放着这种货的格子上，
        /// 剩下的去占**没有存货**的格子。于是变质结构上不可能发生，也没有任何东西会跳位置。
        ///
        /// 三遍的顺序是必须的：「已有的先占」要在「找空格」之前**全部**做完，
        /// 否则先处理的那一种货可能占掉后一种货已经在用的那个空标签格。
        /// </summary>
        private static bool SyncStorageLayout(PlanetFactory factory, StationComponent station, int[] requires, int[] products)
        {
            // 只在前 MaxSafeStorageKinds 格里排布局：UIEntityBriefInfo 的 icons 是定长数组，
            // 按储物格种类数遍历，超出即越界崩溃。
            int total = station.storage.Length;
            int length = total > MaxSafeStorageKinds ? MaxSafeStorageKinds : total;

            var claimed = 0L;
            var changed = false;

            // 第一遍：本配方要的货，优先落在**已经放着这种货的格子**上——位置一格都不动。
            var reqPlaced = 0L;
            var prodPlaced = 0L;

            for (var i = 0; i < requires.Length && i < 63; i++)
                if (ClaimExisting(station, length, requires[i], ELogisticStorage.Demand, ref claimed, ref changed))
                    reqPlaced |= 1L << i;

            for (var i = 0; i < products.Length && i < 63; i++)
                if (ClaimExisting(station, length, products[i], ELogisticStorage.Supply, ref claimed, ref changed))
                    prodPlaced |= 1L << i;

            // 第二遍：其余的占一个**没有存货**的格子。
            for (var i = 0; i < requires.Length && i < 63; i++)
                if ((reqPlaced & (1L << i)) == 0)
                    ClaimFree(station, length, requires[i], ELogisticStorage.Demand, ref claimed, ref changed);

            for (var i = 0; i < products.Length && i < 63; i++)
                if ((prodPlaced & (1L << i)) == 0)
                    ClaimFree(station, length, products[i], ELogisticStorage.Supply, ref claimed, ref changed);

            // 催化反应器还要两格：催化剂（需求）和待生催化剂（供应）。
            //
            // <b>它必须排在这里，而不是由那边自己去填。</b> 下面那个清理循环会把
            // 没被认领的格子清掉，唯一的赦免是 count > 0——而催化剂槽恰恰
            // 要在**空的时候**存在（空着才是在向物流网要货）。从别处填的话，
            // 每 tick 都会被这里擦掉一次，症状是「反应器永远等不到催化剂」，
            // 而病因在一个名字里根本没有「催化剂」三个字的方法里。
            if (CatalystBedPatches.IsReactor(factory, station.entityId))
                changed |= CatalystBedPatches.ClaimSlots(station, length, ref claimed);

            // 第三遍：没被本配方认领的格子。
            //
            // 还有存货的——那是旧配方剩下的东西——**位置不动**，只改挂本地供应让运输机取走；
            // 取空之后下一轮自然会走到下面那个分支被清掉。
            //
            // 要一直走到数组末尾而不是 length：早先版本按 12 格建过站点，那些建筑的
            // storage 数组已经存进存档，只改 prefabDesc 不会缩短它，残留在后面的物品
            // 照样会让简要信息面板越界。
            for (var i = 0; i < total; i++)
            {
                if (i < length && (claimed & (1L << i)) != 0) continue;
                if (station.storage[i].itemId == 0) continue;

                if (station.storage[i].count > 0)
                {
                    // **只把「需求」翻成「供应」，仓储和供应一律不碰。**
                    // 一个还挂着需求的旧格子会继续向物流网要一种本配方根本不用的货，
                    // 那必须停掉；而「仓储」是玩家自己设的（比如他想用传送带喂料、
                    // 不让运输机插手），翻掉它就是又一次和玩家抢方向盘。
                    if (station.storage[i].localLogic != ELogisticStorage.Demand) continue;

                    station.storage[i].localLogic = ELogisticStorage.Supply;
                    changed = true;

                    ReportLeftover(station.storage[i].itemId, station.storage[i].count, i);

                    continue;
                }

                station.storage[i].itemId = 0;
                station.storage[i].localLogic = ELogisticStorage.None;
                station.storage[i].remoteLogic = ELogisticStorage.None;
                changed = true;
            }

            return changed;
        }

        /// <summary>已经放着这种货的格子，原地认领。找到返回 true。</summary>
        internal static bool ClaimExisting(StationComponent station, int length, int itemId,
            ELogisticStorage logic, ref long claimed, ref bool changed)
        {
            if (itemId <= 0) return false;

            for (var i = 0; i < length; i++)
            {
                if ((claimed & (1L << i)) != 0 || station.storage[i].itemId != itemId) continue;

                claimed |= 1L << i;
                changed |= Apply(station, i, itemId, logic);

                return true;
            }

            return false;
        }

        /// <summary>
        /// 占一个**没有存货**的格子。有存货的一律不碰——那正是「不许变质」这条规矩本身。
        /// 一个都找不到时什么也不做：机器会因为 <see cref="FindSlot"/> 找不到格子而停着等，
        /// 那是看得见、把货取走就能恢复的故障，而变质是不可逆的。
        /// </summary>
        internal static bool ClaimFree(StationComponent station, int length, int itemId,
            ELogisticStorage logic, ref long claimed, ref bool changed)
        {
            if (itemId <= 0) return false;

            for (var i = 0; i < length; i++)
            {
                if ((claimed & (1L << i)) != 0 || station.storage[i].count > 0) continue;

                claimed |= 1L << i;
                changed |= Apply(station, i, itemId, logic);

                return true;
            }

            ReportNoFreeSlot(itemId);

            return false;
        }

        /// <summary>
        /// 写下这一格的物品和方向。<b>只会写到「本来就是这种货」或者「一件存货都没有」
        /// 的格子上</b>（两个认领方法各自保证了这一点），所以这里不可能把 A 变成 B。
        ///
        /// <b>方向只在这一格刚被指派给这种货的时候写一次，之后归玩家。</b>
        /// 原先是每 tick 强制写回去的，后果是物流站面板上那三个「需求 / 仓储 / 供应」
        /// 按钮成了摆设——点下去、松手就弹回来，而玩家完全看不出为什么。
        /// 有人想走传送带喂料，就得把原料格从「需求」改成「仓储」让运输机别再送；
        /// 这是个合理的玩法，不该被自动布局锁死。
        ///
        /// 自动布局仍然负责「哪一格放哪种货」——那是跟着配方走的，玩家改不了也不需要改。
        /// </summary>
        private static bool Apply(StationComponent station, int index, int itemId, ELogisticStorage logic)
        {
            bool fresh = station.storage[index].itemId != itemId;

            station.storage[index].itemId = itemId;

            if (fresh)
            {
                station.storage[index].localLogic = logic;
                // 默认不参与星际配送；玩家想开就自己开。
                station.storage[index].remoteLogic = ELogisticStorage.None;
            }

            if (station.storage[index].max <= 0) station.storage[index].max = Config.stationMaxItemCount;

            return fresh;
        }

        private static int _leftoverReported;
        private static int _noFreeSlotReported;

        private static void ReportLeftover(int itemId, int count, int slot)
        {
            // 抢占要在拼字符串之前：这条路跑在 _assembler_parallel 上。
            if (Interlocked.Exchange(ref _leftoverReported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑换配方：旧配方剩下的 {count} 个「{ItemName(itemId)}」留在原来的第 {slot} 格，" +
                "只把方向改成了本地供应，等物流运输机取走——**位置不动，也不会变成新配方的产物**。" +
                "这一行整局只打一次。");
        }

        private static void ReportNoFreeSlot(int itemId)
        {
            if (Interlocked.Exchange(ref _noFreeSlotReported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogWarning(
                $"巨型建筑：「{ItemName(itemId)}」排不进储物格——每一格都还有存货。" +
                "这一格暂时空着，机器会停着等；把旧货取走之后它自己就恢复。" +
                "（宁可停产也不让物品变质。）");
        }

        private static string ItemName(int itemId)
        {
            ItemProto proto = LDB.items.Select(itemId);

            return proto != null ? proto.name : itemId.ToString();
        }
    }
}
