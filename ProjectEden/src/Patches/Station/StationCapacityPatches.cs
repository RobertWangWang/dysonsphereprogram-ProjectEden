#pragma warning disable 649 // StationsConfig 的字段由 JSON 反序列化赋值

using System;
using System.Collections.Generic;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把物流站单个储物格的容量放大，并改写最大充能功率。
    ///
    /// 容量来自 PrefabDesc.stationMaxItemCount，StationComponent.Init 用它初始化每个仓位的 max，
    /// 而 max 是存进存档的——所以改 prefabDesc 只对之后新建的站点生效，
    /// 已经建好的必须在运行时补齐。两头都做。
    ///
    /// 最大充能功率（给运输机 / 运输船充电的速度）走的是同一个套路，只是链路长一点：
    ///     PrefabDesc.workEnergyPerTick
    ///       → StationComponent.Init 写进 PowerConsumerComponent.workEnergyPerTick（<b>进存档</b>）
    ///       → PlanetTransport.GameTick 调 SetPCState，按 1.05 - energy/energyMax 的比例算出 requiredEnergy
    ///       → 回写 StationComponent.energyPerTick，也就是每 tick 实际充进去的能量
    /// 所以只改 prefabDesc 对已建成的站点没有任何效果，必须一并改 consumerPool 里的值。
    ///
    /// 作用范围：配置里列出的站点（行星内 / 星际物流运输站）加上本 mod 的巨型建筑。
    /// 充能功率只认 chargePower 里显式列出的站点——巨型建筑的 workEnergyPerTick 同时是制造台的
    /// 工作功耗，动它会连带改掉生产耗电，所以默认不碰。
    /// 大型采矿机由 AdvancedMinerPatches 单独处理，不在这里重复。
    /// </summary>
    [HarmonyPatch]
    internal static class StationCapacityPatches
    {
        private static StationsConfig Config => ProjectEdenPlugin.StationsConfig;

        /// <summary>需要放大容量的建筑 protoId。</summary>
        private static readonly HashSet<int> TargetProtoIds = new HashSet<int>();

        /// <summary>每种物流站在本 mod 改动<b>之前</b>的单格容量。见 BootstrapCapacity。</summary>
        private static readonly Dictionary<int, int> VanillaSlotMax = new Dictionary<int, int>();

        /// <summary>
        /// 已经引导过的站点。<b>必须是并发容器</b>——<c>PlanetTransport.GameTick</c> 跑在
        /// 约 31 个工作线程上，每个线程一颗星球（本仓库第 4 号坑）。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int PlanetId, int StationId), byte>
            Bootstrapped = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), byte>();

        /// <summary>需要改写最大充能功率的建筑 protoId → 原版值与目标值。</summary>
        private static readonly Dictionary<int, ChargeTarget> ChargePowerByProto = new Dictionary<int, ChargeTarget>();

        /// <summary>一个站点原版的充能功率，以及我们希望它至少达到的值。</summary>
        private static int _bootstrapReported;

        /// <summary>
        /// <b>把「它是默认值不是锁死值」说出来。</b> 玩家看到面板能改了才知道这是有意的；
        /// 而在此之前这条一直是「改了没反应」，没人会去猜是每 tick 被压回去了。
        /// </summary>
        private static void ReportBootstrapOnce()
        {
            if (System.Threading.Interlocked.Exchange(ref _bootstrapReported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"物流站单格容量：配置里的 {Config.slotCapacity:N0} 是**默认值**，不是锁死值——" +
                "新建的站点直接拿到它，老存档里还停在原版值上的格子会被抬一次，" +
                "之后每一格都由玩家在面板上自己定，本 mod 不再回写。");
        }

        /// <summary>换存档时要清，否则新存档里同号的站点会被当成已经引导过。</summary>
        internal static void ClearBootstrapped()
        {
            Bootstrapped.Clear();
            Settled.Clear();
        }

        /// <summary>
        /// 每颗星球「上一趟全量扫描什么都没做」时的 <c>stationCursor</c> 和下次兜底重扫的 tick。
        ///
        /// <para><b>为什么需要它：一次性的是抬升，不是扫描。</b></para>
        /// 下面那个 <c>for</c> 每 tick 走一遍全星球的站点，而抬升本身被
        /// <see cref="Bootstrapped"/> 挡成每站一次——于是引导早就做完了，扫描还在每 tick
        /// 跑 6397 次「HashSet 查找 + 元组哈希 + 并发字典探测」。
        ///
        /// **实测它是本 mod 五个后置里最贵的一个**：每 20 秒 604 ms、每帧 0.5 ms，
        /// 比虚拟物流（524 ms）还高——而虚拟物流是真在搬货的。
        ///
        /// <para><b>失效条件有两个，第二个是兜底。</b></para>
        /// <list type="number">
        /// <item><c>stationCursor</c> 变了＝有新站点（新建的站点从 <c>prefabDesc</c> 直接拿到
        /// 我们改过的值，本来就不需要引导；但 cursor 变了说明世界变了，重扫一趟最省心）。</item>
        /// <item>每 600 tick（10 秒）无条件重扫一趟。**这是给「我没想到的那种失效」留的**——
        /// 代价是原来的 1/600，而它让任何漏掉的失效路径在 10 秒内自愈。</item>
        /// </list>
        ///
        /// 用 <c>GameMain.gameTick</c> 排期在这里是安全的，尽管本仓库记过它换存档会倒退：
        /// 倒退只会让判断提前到期、**多扫一趟**，方向是安全的那一侧。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int Cursor, long NextScan)>
            Settled = new System.Collections.Concurrent.ConcurrentDictionary<int, (int, long)>();

        private struct ChargeTarget
        {
            public long vanilla;
            public long target;
        }

        /// <summary>proto 就绪后调用：改 prefabDesc，并整理出运行时要认的 protoId 集合。</summary>
        internal static void ApplyPrefabCapacity()
        {
            TargetProtoIds.Clear();
            ChargePowerByProto.Clear();

            if (Config == null) return;

            ApplyChargePower();

            if (Config.slotCapacity <= 0) return;

            if (Config.itemIds != null)
                foreach (int itemId in Config.itemIds)
                    Apply(itemId);

            // 巨型建筑本身也是物流站，一并放大
            MegaBuildingsConfig mega = MegaBuildingRegistry.Config;

            if (mega?.buildings != null && mega.stationEnabled)
                foreach (MegaBuildingEntry entry in mega.buildings)
                    Apply(entry.itemId);

            // machines.json 里 kind 为 station 的新建筑同理：它们就是物流站，
            // 储量 / 格数 / 充能功率跟着这里走，不用在那边重复配一遍
            foreach (int itemId in MachineRegistry.StationItemIds) Apply(itemId);

            // 气体采集器不写死 ID，按 isCollectStation 认，和 GasCollectorPatches 一个口径
            foreach (ItemProto item in LDB.items.dataArray)
            {
                if (item == null) continue;

                ModelProto model = LDB.models.Select(item.ModelIndex);

                if (model?.prefabDesc == null || !model.prefabDesc.isCollectStation) continue;

                Apply(item.ID);
            }
        }

        /// <summary>
        /// 改写最大充能功率。只动 chargePower 里显式列出的站点，
        /// 免得把巨型建筑的制造功耗也一起改了。
        /// </summary>
        private static void ApplyChargePower()
        {
            if (Config.chargePower == null) return;

            foreach (StationChargeEntry entry in Config.chargePower)
            {
                if (entry == null || entry.energyPerTick <= 0) continue;

                ItemProto item = LDB.items.Select(entry.itemId);
                ModelProto model = item != null ? LDB.models.Select(item.ModelIndex) : null;

                if (model?.prefabDesc == null || !model.prefabDesc.isStation)
                {
                    ProjectEdenPlugin.Log.LogWarning($"物品 {entry.itemId} 不是物流站或没有 prefabDesc，最大充能功率未改");
                    continue;
                }

                long before = model.prefabDesc.workEnergyPerTick;

                model.prefabDesc.workEnergyPerTick = entry.energyPerTick;

                // 记下原版值：运行时只补「还停在原版值」的站点，玩家自己拖过的一律不动
                ChargePowerByProto[entry.itemId] = new ChargeTarget { vanilla = before, target = entry.energyPerTick };

                ProjectEdenPlugin.Log.LogInfo(
                    $"{item.name} 最大充能功率：{before * 60 / 1e9:0.###} GW → {entry.energyPerTick * 60 / 1e9:0.###} GW");
            }
        }

        private static void Apply(int itemId)
        {
            ItemProto item = LDB.items.Select(itemId);
            ModelProto model = item != null ? LDB.models.Select(item.ModelIndex) : null;

            if (model?.prefabDesc == null || (!model.prefabDesc.isStation && !model.prefabDesc.isCollectStation))
            {
                ProjectEdenPlugin.Log.LogWarning($"物品 {itemId} 不是物流站或没有 prefabDesc，储物格容量未改");
                return;
            }

            int before = model.prefabDesc.stationMaxItemCount;
            int beforeKinds = model.prefabDesc.stationMaxItemKinds;

            // 记下原版默认值：引导已建成的站点时要靠它分辨
            // 「这一格还停在原版值上」和「玩家自己调过了」。
            if (before > 0) VanillaSlotMax[itemId] = before;

            model.prefabDesc.stationMaxItemCount = Config.slotCapacity;

            // 巨型建筑的格数由 megabuildings.json 单独控制，这里只改配置列出的物流站
            bool isMega = MegaBuildingRegistry.Config?.buildings != null
                       && System.Array.Exists(MegaBuildingRegistry.Config.buildings, b => b.itemId == itemId);

            // 气体采集器的格数必须和行星的气体种类对得上：StationComponent.Init 的采集器分支
            // 按 collectionIds.Length 铺格位、拿 stationMaxItemKinds 封顶，改成 30 只会多出
            // 一堆空格，还会把 StationExpandPatches 的 30 格面板套到采集器头上。只放大容量。
            bool isCollector = model.prefabDesc.isCollectStation;

            if (!isMega && !isCollector && Config.stationMaxItemKinds > 0)
                model.prefabDesc.stationMaxItemKinds = Config.stationMaxItemKinds;

            TargetProtoIds.Add(itemId);

            ProjectEdenPlugin.Log.LogInfo(
                $"{item.name} 储物格：{beforeKinds} → {model.prefabDesc.stationMaxItemKinds} 格，" +
                $"单格容量 {before} → {Config.slotCapacity}");
        }

        /// <summary>
        /// 已建成站点的补齐。挂 PlanetTransport.GameTick——单线程与多线程两条路径
        /// 最终都会调到它，所以只需要这一处。
        ///
        /// 每 tick 只做整数比较，值已经对了就不写，开销可以忽略。
        /// 充能功率是后置修正：本 tick 的 SetPCState 已经跑过了，改完下一 tick 才生效。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void PlanetTransport_GameTick(PlanetTransport __instance)
        {
            // 诊断打点：放在一切 return 之前（理由见 LabLogisticSupplyPatches 同一行）
            Diagnostics.TransportSplitProbe.Phase("物流站容量引导");

            if (Config == null) return;

            bool fixCapacity = Config.slotCapacity > 0 && TargetProtoIds.Count > 0;
            bool fixCharge = ChargePowerByProto.Count > 0;

            if (!fixCapacity && !fixCharge) return;

            PlanetFactory factory = __instance.factory;

            if (factory == null || __instance.stationPool == null) return;

            EntityData[] entityPool = factory.entityPool;
            PowerConsumerComponent[] consumerPool = factory.powerSystem?.consumerPool;
            int capacity = Config.slotCapacity;

            // 引导做完之后就别再每 tick 扫一遍全星球了（理由见 Settled 的注释）
            int cursor = __instance.stationCursor;
            int planetId = factory.planetId;
            long tick = GameMain.gameTick;

            if (Settled.TryGetValue(planetId, out (int Cursor, long NextScan) done)
                && done.Cursor == cursor && tick < done.NextScan)
                return;

            // 这一趟到底动没动东西。**必须真的记「改了没有」，不能记「扫过了」**——
            // 后者在第一趟就会settled，而第一趟正是要干活的那一趟
            var touched = false;

            for (var i = 1; i < cursor; i++)
            {
                StationComponent station = __instance.stationPool[i];

                if (station == null || station.id != i || station.storage == null) continue;

                int entityId = station.entityId;

                if (entityId <= 0 || entityId >= entityPool.Length) continue;

                int protoId = entityPool[entityId].protoId;

                // **一次性引导，不是每 tick 强制。**
                //
                // 这个 max 正是物流站面板上那个「每格库存上限」输入框写的字段。
                // 原先这里每 tick 都把它压回配置值，于是玩家改完一松手就弹回去——
                // 和当年「最大充能功率」滑条被压回去<b>是同一个字段级的错误</b>，
                // 那一条就在这个文件下面几十行的地方写着，而 max 没跟着改。
                //
                // 配置里那个数的正确身份是<b>默认值</b>：新建的站点由 prefabDesc 直接拿到它，
                // 已经建成的（老存档）在这里被抬一次。抬过之后这一格就归玩家了。
                //
                // 两道闸都要：<b>每座站点只引导一次</b>（否则玩家把上限设成正好等于原版默认值时，
                // 会被每 tick 抢一次方向盘），而且<b>只抬还停在原版值或 0 上的格子</b>
                // （否则读档时会把玩家调小过的上限又顶回去）。
                if (fixCapacity && TargetProtoIds.Contains(protoId) &&
                    Bootstrapped.TryAdd((factory.planetId, i), 0))
                {
                    VanillaSlotMax.TryGetValue(protoId, out int vanillaMax);

                    for (var s = 0; s < station.storage.Length; s++)
                    {
                        // 催化剂床那两格有自己的容量（见 catalyst.json 的 //slotCapacity）。
                        //
                        // <b>这里不排除的话会坏一整条线。</b> 催化剂槽是**本地需求格**，
                        // 它按 max 向物流网要货；被这里抬到一千万之后，第一座建成的反应器
                        // 会把全网的催化剂一口气吸光，后面每一座都装不上料——
                        // 而玩家看到的症状是「我别的反应器全停了」，指向的地方
                        // 离病因十万八千里。大型采矿机的钻头槽当初就是为同一件事
                        // 单独配了容量，这是同一个坑换了座建筑。
                        if (CatalystBedPatches.OwnsSlot(station.storage[s].itemId)) continue;

                        int current = station.storage[s].max;

                        // 0 = 还没铺过的格子；等于原版默认 = 老存档里没人动过的格子。
                        // 其余一律不碰——那是玩家自己设的。
                        if (current == 0 || (vanillaMax > 0 && current == vanillaMax))
                        {
                            station.storage[s].max = capacity;

                            touched = true;
                        }
                    }

                    ReportBootstrapOnce();
                }

                if (!fixCharge || consumerPool == null) continue;
                if (!ChargePowerByProto.TryGetValue(protoId, out ChargeTarget charge)) continue;

                int pcId = station.pcId;

                if (pcId <= 0 || pcId >= consumerPool.Length) continue;

                // 只把「还停在原版值」的老站点抬上来，抬过一次之后 current 就高于原版值，
                // 这里再也不会命中。玩家用面板滑条设的值因此能保住——
                // 那个滑条（UIStationWindow.OnMaxChargePowerSliderValueChange）写的正是这个字段，
                // 无条件覆写会让它一松手就缩回去。
                if (consumerPool[pcId].workEnergyPerTick <= charge.vanilla)
                {
                    consumerPool[pcId].workEnergyPerTick = charge.target;

                    touched = true;
                }
            }

            // 这一趟一个字都没改 → 记下「在这个 cursor 上已经做完了」，
            // 下一趟直接早退，直到站点数变了或者 10 秒的兜底到期
            if (!touched)
            {
                Settled[planetId] = (cursor, tick + RescanTicks);

                ReportSettledOnce(planetId, cursor);
            }
        }

        private static int _settledLogged;

        /// <summary>
        /// 第一颗星球停扫时报一行。**没有这一行，「早退生效了」和「这段代码根本没进来」
        /// 在日志里长得一模一样**——而它们的唯一区别要到下一次量耗时才看得出来，
        /// 那是一整个来回。本仓库记过七次的那条。
        /// </summary>
        private static void ReportSettledOnce(int planetId, int cursor)
        {
            if (System.Threading.Interlocked.Exchange(ref _settledLogged, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"物流站容量引导：行星 {planetId} 的 {cursor - 1} 个站点已全部引导完毕，"
                + "**这颗星球从此不再每 tick 全量扫描**（站点数变了、或者每 600 tick 的兜底到期时再扫）。"
                + "引导本身一直是每站一次，可外面那圈扫描原先是永远跑的——实测它每帧 0.5 ms，"
                + "是本 mod 挂在物流运输上的五个后置里最贵的一个。整局只报这一行。");
        }

        /// <summary>
        /// 兜底重扫的间隔。600 tick ＝ 10 秒，开销是原来的 1/600。
        /// **它不是为已知的失效路径准备的**——已知的那条（站点数变了）由 cursor 挡着；
        /// 这一条是给「我没想到的那种」留的，让任何漏网的失效在 10 秒内自愈。
        /// </summary>
        private const long RescanTicks = 600;
    }

    [Serializable]
    internal class StationsConfig
    {
        /// <summary>
        /// 巨型建筑的物流站跳过派机空扫。**默认关**，它会让那些站点彻底不再派出
        /// 行星内运输机——理由、账和三道守卫全写在
        /// <see cref="MegaStationTickSkipPatches"/> 上。
        /// </summary>
        public bool skipIdleMegaStationTick;

        public int slotCapacity;

        /// <summary>储物格数量。超过 6 需要 StationExpandPatches 一并接管。</summary>
        public int stationMaxItemKinds;

        public int[] itemIds;

        /// <summary>行星内物流运输机的单次运载量</summary>
        public int droneCarries;

        /// <summary>星际物流运输船的单次运载量</summary>
        public int shipCarries;

        /// <summary>分拣器堆叠输入层数（解锁函数 41），原版基础值 2</summary>
        public int inserterStackInput;

        /// <summary>分拣器堆叠输出层数（解锁函数 39），原版基础值 1</summary>
        public int inserterStackOutput;

        /// <summary>物流塔集装层数（解锁函数 29），原版基础值 1</summary>
        public int stationPilerLevel;

        /// <summary>要改写最大充能功率的站点，逐个列出</summary>
        public StationChargeEntry[] chargePower;

        /// <summary>气体采集器的采集倍率（PrefabDesc.stationCollectSpeed）。0 = 保持原版</summary>
        public int collectorSpeed;

        /// <summary>气体采集器每 tick 每种气体的采集量上限。0 = 用 slotCapacity</summary>
        public float collectorMaxPerTick;

        /// <summary>
        /// 每个物品一格能堆多少（背包 / 储物箱 / 物流背包共用）。0 = 保持原版。
        /// 见 <see cref="ItemStackSizePatches"/>：它改的是 <c>ItemProto.StackSize</c>，
        /// 和传送带集装（<see cref="stationPilerLevel"/>）、物流站格容量
        /// （<see cref="slotCapacity"/>）是三套互不相干的东西。
        /// </summary>
        public int inventoryStackSize;

        /// <summary>
        /// 行星内物流运输机：一帧最多派几架。0 或 1 = 原版（一帧一架）。
        /// 见 <see cref="LocalDispatchBurstPatches"/>：原版的派机循环本来就会走遍整个
        /// 配对环，只是派出一架就跳出去了。硬上限 200（droneDispatchStatus 是 byte[]）。
        /// </summary>
        public int localDispatchPerTick;

        /// <summary>
        /// 星际物流运输船：一次派船评估最多放几艘。0 或 1 = 原版（一次一艘）。
        /// 见 <see cref="RemoteDispatchBurstPatches"/>：原版的配对扫描本来就会走遍整段
        /// 配对环，只是定下一对就跳出去了。硬上限 64（<c>idleShipIndices</c> 是 UInt64 位图，
        /// 一个站点至多只可能有 64 艘闲置船）。
        ///
        /// <b>它只管「一次评估放几艘」，管不了「多久评估一次」</b>：默认优先级的站点
        /// 原版一秒才被评估一次，把取货端的 <c>routePriority</c> 设成「优先」是另外的
        /// 6 倍，两者相乘。
        /// </summary>
        public int remoteShipsPerDispatch;

        /// <summary>
        /// 同一条运输线（同一对供需配对）一次评估最多连发几艘。**1 = 逐对轮转**（1.10.7 的行为），
        /// 缺配置 = 4，不可能超过 <see cref="remoteShipsPerDispatch"/>。
        ///
        /// 见 <see cref="RemoteDispatchBurstPatches"/>：重试同一对是**安全的**——每次派船
        /// 当场扣掉两端（供给端 <c>count</c>，需求端 <c>remoteOrder</c>），扣光了原版自己换对。
        /// 这个上限管的是**公平性**：本 mod 的物流站格容量是 1000 万，所以需求几乎扣不光，
        /// 一条线能把整次评估的额度吃干净。
        /// </summary>
        public int remoteSameRouteMax;

        /// <summary>
        /// 「货物账本」探针。<b>只观察，不改任何游戏逻辑</b>，用来验证
        /// 「与 cargoPool 平行、按 cargoId 索引的数组」这套骨架跟不跟得住——
        /// 扩容、ID 回收、读档、并行四处都会被检出来。见 CargoLedgerProbe。
        /// </summary>
        public bool cargoLedgerProbe;

        /// <summary>探针的自检间隔（秒）。0 = 10 秒</summary>
        public int cargoLedgerLogSeconds;
    }

    /// <summary>单个站点的最大充能功率设定。</summary>
    [Serializable]
    internal class StationChargeEntry
    {
        public int itemId;

        /// <summary>每 tick 焦耳数。60 tick = 1 秒，所以 500000000 = 30 GW。</summary>
        public long energyPerTick;
    }
}
