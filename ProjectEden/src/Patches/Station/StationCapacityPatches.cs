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

        /// <summary>需要改写充能功率 / 能量容积的建筑 protoId → 原版值与目标值。</summary>
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
            SettledLogged.Clear();
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
            /// <summary>本 mod 改动<b>之前</b>的充能功率（每 tick 焦耳），0 = 这一项不管。</summary>
            public long vanillaPower;

            /// <summary>目标充能功率（每 tick 焦耳），0 = 这一项不管。</summary>
            public long targetPower;

            /// <summary>目标能量容积（焦耳），0 = 这一项不管。</summary>
            public long targetEnergyMax;
        }

        /// <summary>proto 就绪后调用：改 prefabDesc，并整理出运行时要认的 protoId 集合。</summary>
        internal static void ApplyPrefabCapacity()
        {
            TargetProtoIds.Clear();
            ChargePowerByProto.Clear();

            if (Config == null) return;

            ApplyChargePower();
            ApplyDroneCount();

            if (Config.slotCapacity <= 0) return;

            if (Config.itemIds != null)
                foreach (int itemId in Config.itemIds)
                    Apply(itemId);

            // 巨型建筑本身也是物流站，一并放大。
            //
            // <b>这一路是「把整张表都过一遍」，所以里头本来就可能有不是物流站的。</b>
            // 奇点储能厂只挂能量枢纽组件，没有物流站段（它不收发货），于是必然落进
            // Apply 里那句「不是物流站」的分支。那不是缺陷，是这台建筑的设计，
            // 所以这一路传 optional: true，降成 Info。
            // 另外两路（stations.json 的 itemIds、machines.json 的 kind: station）
            // 是**点着名要求**它是物流站的，那边落进同一个分支就是真的配错了，保持 WARNING。
            MegaBuildingsConfig mega = MegaBuildingRegistry.Config;

            if (mega?.buildings != null && mega.stationEnabled)
                foreach (MegaBuildingEntry entry in mega.buildings)
                    Apply(entry.itemId, optional: true);

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
        /// 改写最大充能功率与最大能量容积。只动 stationEnergy 里显式列出的站点，
        /// 免得把巨型建筑的制造功耗也一起改了。
        ///
        /// <para><b>两项的性质不一样，所以运行时的补法也不一样，别看它们挨在一起就以为对称。</b>
        /// <c>workEnergyPerTick</c> 是物流站面板上那根「最大充能功率」滑条写的字段
        /// （<c>UIStationWindow.OnMaxChargePowerSliderValueChange</c>），所以它只能<b>抬一次</b>；
        /// 而 <c>energyMax</c> 在整个程序集里只有三处写入——<c>Init</c>、<c>Reset</c>、<c>Import</c>，
        /// <b>没有任何界面能改它</b>（枚举过：其余全是 ldfld），所以它可以直接对齐，升降都行。</para>
        /// </summary>
        private static void ApplyChargePower()
        {
            if (Config.stationEnergy == null) return;

            foreach (StationEnergyEntry entry in Config.stationEnergy)
            {
                if (entry == null) continue;
                if (entry.chargePowerWatt <= 0 && entry.maxEnergyJoule <= 0) continue;

                ItemProto item = LDB.items.Select(entry.itemId);
                ModelProto model = item != null ? LDB.models.Select(item.ModelIndex) : null;

                if (model?.prefabDesc == null || !model.prefabDesc.isStation)
                {
                    ProjectEdenPlugin.Log.LogWarning(
                        (item != null ? $"「{item.Name}」({entry.itemId})" : $"物品 {entry.itemId}")
                        + " 不是物流站或没有 prefabDesc，充能功率 / 能量容积未改");
                    continue;
                }

                // 配置说的是瓦，落到 prefabDesc 是「每 tick 焦耳」——60 tick = 1 秒
                long targetPower = entry.chargePowerWatt > 0 ? entry.chargePowerWatt / 60L : 0L;

                long beforePower = model.prefabDesc.workEnergyPerTick;
                long beforeAcc = model.prefabDesc.stationMaxEnergyAcc;

                if (targetPower > 0) model.prefabDesc.workEnergyPerTick = targetPower;
                if (entry.maxEnergyJoule > 0) model.prefabDesc.stationMaxEnergyAcc = entry.maxEnergyJoule;

                // 记下原版功率：运行时只补「还停在原版值」的站点，玩家自己拖过滑条的一律不动
                ChargePowerByProto[entry.itemId] = new ChargeTarget
                {
                    vanillaPower = beforePower,
                    targetPower = targetPower,
                    targetEnergyMax = entry.maxEnergyJoule
                };

                ProjectEdenPlugin.Log.LogInfo(
                    $"{item.name} 最大充能功率：{beforePower * 60 / 1e9:0.###} GW → "
                    + $"{model.prefabDesc.workEnergyPerTick * 60 / 1e9:0.###} GW；"
                    + $"最大能量容积：{beforeAcc / 1e9:0.###} GJ → "
                    + $"{model.prefabDesc.stationMaxEnergyAcc / 1e9:0.###} GJ");
            }
        }

        /// <summary>
        /// 物流站能停几架行星内物流运输机（<c>PrefabDesc.stationMaxDroneCount</c>）。
        ///
        /// <para><b>这一项不需要运行时补丁，而这一点是查出来的、不是想当然的。</b>
        /// 枚举过这个字段的全部读取点，一共六处：<c>StationComponent.Init</c> @012B/@013D
        /// （建站时按它开 <c>workDroneDatas</c> 和 <c>workDroneOrders</c> 两个数组）、
        /// <c>PrefabDesc.ReadPrefab</c>（写入方）、两个界面的「往里塞运输机」按钮
        /// （<c>OnDroneIconClick</c>，算的是 <c>max −（idle + work）</c>还能塞几架），
        /// 以及 <b><c>PlanetTransport.Import</c> @00C3</b>——它每次读档都按<b>当前</b>
        /// prefab 的值调一次 <c>StationComponent.PatchDroneArray</c>。</para>
        ///
        /// <para>而 <c>PatchDroneArray</c> 是<b>双向对齐</b>的：长度不等就重开一个数组，
        /// 拷贝 <c>min(旧长度, 新长度)</c> 个元素（@0009 的 <c>beq</c> 早退、
        /// @0028 的 <c>blt</c> 选较小者）。所以老站点读一次档就跟上了，调大调小都生效——
        /// 和 <c>StorageComponent.Import</c> 重新推导 <c>GRID.stackSize</c> 是同一族，
        /// 是「陷阱一」的反例。</para>
        ///
        /// <para><b>调小有代价，而且是崩溃级的，所以只在站点空着的时候调小。</b>
        /// <c>PatchDroneArray</c> 只搬数组，<b>不动 <c>idleDroneCount</c> / <c>workDroneCount</c></b>。
        /// 一座停着 300 架的站点把上限改成 100，那两个计数还是 300，而派机写的是
        /// <c>workDroneDatas[workDroneCount]</c>——直接越界。原版同样有这个洞，只是原版
        /// 没有任何途径去改这个值。</para>
        ///
        /// <para>内存：一架的账是 <c>DroneData</c> 60 字节 + <c>LocalLogisticOrder</c> 24 字节
        /// = <b>84 字节</b>，而且<b>不管有没有真的停着运输机，建站时就按上限全开</b>。
        /// 750 架 = 每座站 63 KB。巨型建筑不走这里（它们的架数在 megabuildings.json），
        /// 所以乘的是「真站点」的数量。</para>
        /// </summary>
        private static void ApplyDroneCount()
        {
            if (Config.stationDrones == null) return;

            foreach (StationDroneEntry entry in Config.stationDrones)
            {
                if (entry == null || entry.maxDroneCount <= 0) continue;

                ItemProto item = LDB.items.Select(entry.itemId);
                ModelProto model = item != null ? LDB.models.Select(item.ModelIndex) : null;

                if (model?.prefabDesc == null || !model.prefabDesc.isStation)
                {
                    ProjectEdenPlugin.Log.LogWarning(
                        (item != null ? $"「{item.Name}」({entry.itemId})" : $"物品 {entry.itemId}")
                        + " 不是物流站或没有 prefabDesc，运输机停放数未改");
                    continue;
                }

                int before = model.prefabDesc.stationMaxDroneCount;

                if (before == entry.maxDroneCount) continue;

                model.prefabDesc.stationMaxDroneCount = entry.maxDroneCount;

                // 每架 84 字节（DroneData 60 + LocalLogisticOrder 24），建站时按上限全开，
                // 所以这一行报的是「每座站点多吃多少内存」，不是「实际停了几架」。
                ProjectEdenPlugin.Log.LogInfo(
                    $"{item.name} 行星内运输机停放数：{before} → {entry.maxDroneCount} 架"
                    + $"（每座站点预分配 {entry.maxDroneCount * 84 / 1024.0:0.#} KB；"
                    + "老站点读档时由原版 PlanetTransport.Import → PatchDroneArray 自动跟上，"
                    + "双向对齐。注意 PatchDroneArray 不动 idleDroneCount，"
                    + "所以停着运输机的时候**不要调小**，会越界）");
            }
        }

        /// <summary>
        /// 这座建筑每一格该有多大：<c>stationSlotCapacity</c> 里单独指定的优先，
        /// 否则用全局的 <c>slotCapacity</c>。
        ///
        /// <b>注册时和运行时引导必须调同一个方法。</b> 两处各算各的，就会出现
        /// 「新建的站点是一个数、老站点被引导成另一个数」这种只在旧存档上发作的差异——
        /// 而引导那一侧还拿这个值去比对「这一格是不是还停在原版默认上」，判据会一起错。
        /// </summary>
        private static int CapacityFor(int itemId)
        {
            StationSlotEntry[] list = Config?.stationSlotCapacity;

            if (list != null)
                foreach (StationSlotEntry entry in list)
                    if (entry != null && entry.itemId == itemId && entry.capacity > 0)
                        return entry.capacity;

            return Config?.slotCapacity ?? 0;
        }

        /// <param name="optional">
        /// 调用方是「整表扫一遍」而不是点名要求：落进「不是物流站」分支属于正常，报 Info 就够了。
        /// </param>
        private static void Apply(int itemId, bool optional = false)
        {
            ItemProto item = LDB.items.Select(itemId);
            ModelProto model = item != null ? LDB.models.Select(item.ModelIndex) : null;

            if (model?.prefabDesc == null || (!model.prefabDesc.isStation && !model.prefabDesc.isCollectStation))
            {
                // 带上名字：光一个号码要回头翻四个配置文件才知道说的是谁。
                // 取 Name（原始键）不是 name（译名）——日志是中文的，而译名在英文客户端下是英文
                string who = item != null ? $"「{item.Name}」({itemId})" : $"物品 {itemId}";

                if (optional)
                    ProjectEdenPlugin.Log.LogInfo($"{who} 没有物流站组件，跳过储物格容量——这台建筑本来就不收发货");
                else
                    ProjectEdenPlugin.Log.LogWarning($"{who} 不是物流站或没有 prefabDesc，储物格容量未改");

                return;
            }

            int before = model.prefabDesc.stationMaxItemCount;
            int beforeKinds = model.prefabDesc.stationMaxItemKinds;

            // 记下原版默认值：引导已建成的站点时要靠它分辨
            // 「这一格还停在原版值上」和「玩家自己调过了」。
            if (before > 0) VanillaSlotMax[itemId] = before;

            int capacity = CapacityFor(itemId);

            model.prefabDesc.stationMaxItemCount = capacity;

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
                $"单格容量 {before} → {capacity}"
                + (capacity != Config.slotCapacity ? "（stationSlotCapacity 单独指定）" : ""));
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

                    // 逐站点的容量。**必须按这座建筑的 protoId 取，不能用全局值**——
                    // stationSlotCapacity 单独指定过的站点，注册时写进 prefabDesc 的是
                    // 那个数，这里要是还用全局值，新建的和老站点就会是两个容量
                    int capacity = CapacityFor(protoId);

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

                if (!fixCharge) continue;
                if (!ChargePowerByProto.TryGetValue(protoId, out ChargeTarget charge)) continue;

                // 最大能量容积：**直接对齐，升降都做**。
                //
                // 它和下面的充能功率是<b>两种不同性质的值</b>，别因为挨着就照抄那边的「只抬一次」。
                // energyMax 在整个程序集里只有 Init / Reset / Import 三处写入（其余全是 ldfld），
                // 也就是说**没有任何界面能改它**——没有「玩家的值」要保，那条规矩不适用；
                // 反过来，只抬不降会让这个配置项变成单向的（本仓库记过的那条：
                // 只抬会让配置值改小之后没有效果）。
                if (charge.targetEnergyMax > 0 && station.energyMax != charge.targetEnergyMax)
                {
                    station.energyMax = charge.targetEnergyMax;

                    // 调小时已存的能量可能越界。SetPCState 按 1.05 - energy/energyMax 算需求，
                    // 越界会算出负的需求量，站点从此不再充电。
                    if (station.energy > station.energyMax) station.energy = station.energyMax;

                    touched = true;
                }

                if (consumerPool == null) continue;
                if (charge.targetPower <= 0) continue;

                int pcId = station.pcId;

                if (pcId <= 0 || pcId >= consumerPool.Length) continue;

                // 充能功率：**只把「还停在原版值」的老站点抬上来**，抬过一次之后 current 就高于
                // 原版值，这里再也不会命中。玩家用面板滑条设的值因此能保住——
                // 那个滑条（UIStationWindow.OnMaxChargePowerSliderValueChange）写的正是这个字段，
                // 无条件覆写会让它一松手就缩回去。
                //
                // **代价要说出来：把配置值调小，对已经被前一个版本抬上去的站点没有效果。**
                // 那些站点的当前值高于原版值，和「玩家自己拖上去的」在数据上长得一模一样，
                // 分不开——所以宁可不动。新建的站点从 prefabDesc 直接拿到新值，
                // 老站点拖一下滑条就跟上（滑条范围也是从 prefabDesc 推的：prefab/2 ~ prefab×5）。
                // `!= targetPower` 这一半不是多余的：目标值和原版值撞上时（配置恰好填了原版数，
                // 或者两个条目共用同一个 prefabDesc 导致第二条记下的「原版值」其实是第一条改过的值），
                // 光有 `<=` 会让一座已经正确的站点每一趟都被「改」成同一个数并把 touched 置上，
                // 于是**这颗星球永远 settle 不了**，那圈每 tick 全量扫描就回来了（实测 0.5 ms/帧）。
                if (consumerPool[pcId].workEnergyPerTick <= charge.vanillaPower
                    && consumerPool[pcId].workEnergyPerTick != charge.targetPower)
                {
                    consumerPool[pcId].workEnergyPerTick = charge.targetPower;

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

        /// <summary>已经报过停扫的星球。<b>并发容器</b>，理由同 <see cref="Bootstrapped"/>。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte>
            SettledLogged = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

        /// <summary>
        /// 每颗星球停扫时报一行。**没有这一行，「早退生效了」和「这段代码根本没进来」
        /// 在日志里长得一模一样**——而它们的唯一区别要到下一次量耗时才看得出来，
        /// 那是一整个来回。本仓库记过七次的那条。
        ///
        /// <para><b>按星球记，不是整局记一次——这一条是栽过之后改的。</b></para>
        /// 第一版用一个全局 one-shot，结果最先 settle 的是一颗空星球
        ///（<c>stationCursor == 1</c>），日志里留下「行星 104 的 **0 个站点**已全部引导完毕」，
        /// 而真正有 6397 个站、正是这次要优化的那颗，**一个字都没报**。
        ///
        /// 这正是本仓库记过的那条：<b>「记一次」应该是每类一次，不是每局一次</b>
        ///（<c>MegaStationPatches</c> 的储物格转储当年栽在同一处：八种巨型建筑里只有最先
        /// tick 的那一种会打印）。星球数是十几个量级，逐颗报一行不会淹没日志。
        /// </summary>
        private static void ReportSettledOnce(int planetId, int cursor)
        {
            if (!SettledLogged.TryAdd(planetId, 0)) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"物流站容量引导：行星 {planetId} 的 {cursor - 1} 个站点已全部引导完毕，"
                + "**这颗星球从此不再每 tick 全量扫描**（站点数变了、或者每 600 tick 的兜底到期时再扫）。"
                + "引导本身一直是每站一次，可外面那圈扫描原先是永远跑的——实测它每帧 0.5 ms，"
                + "是本 mod 挂在物流运输上的五个后置里最贵的一个。每颗星球只报一行。");
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

        /// <summary>
        /// <b>逐站点覆盖单格容量</b>，只动这里列出来的，其余一律用 <see cref="slotCapacity"/>。
        ///
        /// <para>为什么需要它：<c>slotCapacity</c> 是<b>全体生效</b>的——三个物流站、
        /// 十六座巨型建筑、垃圾箱、还有全部气体采集器都吃同一个数。想只抬物流站
        /// （巨型建筑的槽位是给制造台配料用的，抬到十亿只会让一台建筑把全网的料吸干），
        /// 只能逐个列。和 <see cref="stationEnergy"/> / <see cref="stationDrones"/> 同一个形状。</para>
        /// </summary>
        public StationSlotEntry[] stationSlotCapacity;

        /// <summary>储物格数量。超过 6 需要 StationExpandPatches 一并接管。</summary>
        public int stationMaxItemKinds;

        public int[] itemIds;

        /// <summary>行星内物流运输机的单次运载量</summary>
        public int droneCarries;

        /// <summary>星际物流运输船的单次运载量</summary>
        public int shipCarries;

        /// <summary>
        /// 配送运输机（送到机甲手上那种）的单次运载量。<c>ModeConfig</c> 的默认值是 5，
        /// 由科技累加；实测存档里是 <b>20</b>——那正是「运力只有 20」的来源。0 = 保持原版。
        /// </summary>
        public int courierCarries;

        /// <summary>
        /// 行星内物流运输机的<b>基础</b>速度倍率，乘在原版基础值上（原版 8，
        /// 活取自 <c>Configs.freeMode.logisticDroneSpeed</c>）。0 或负数 = 保持原版。
        ///
        /// <b>它乘的是基础值，不是科技那层倍率。</b> 最终速度 = 基础 × 科技倍率，
        /// 而科技只写倍率那一层（<c>UnlockTechFunction</c> @0345），所以两者不冲突；
        /// 代价是往后每级速度科技的收益也跟着一起放大了。
        /// </summary>
        public float droneSpeedMultiplier;

        /// <summary>
        /// 配送运输机的<b>基础</b>速度倍率，乘在原版基础值上（原版 <b>6</b>，实测；
        /// <c>ModeConfig..ctor</c> 里的字面量是 10，但那是默认值，<c>freeMode</c> 会覆盖——
        /// 活取自 <c>Configs.freeMode.logisticCourierSpeed</c>）。0 或负数 = 保持原版。
        /// 和 <see cref="droneSpeedMultiplier"/> 结构完全一样：科技只写
        /// <c>logisticCourierSpeedScale</c>（<c>UnlockTechFunction</c> @0512），从不碰基础值。
        /// </summary>
        public float courierSpeedMultiplier;

        /// <summary>分拣器堆叠输入层数（解锁函数 41），原版基础值 2</summary>
        public int inserterStackInput;

        /// <summary>分拣器堆叠输出层数（解锁函数 39），原版基础值 1</summary>
        public int inserterStackOutput;

        /// <summary>物流塔集装层数（解锁函数 29），原版基础值 1</summary>
        public int stationPilerLevel;

        /// <summary>要改写最大充能功率 / 最大能量容积的站点，逐个列出</summary>
        public StationEnergyEntry[] stationEnergy;

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

        /// <summary>
        /// 逐座物流站能停几架行星内物流运输机。见
        /// <see cref="StationCapacityPatches.ApplyDroneCount"/>——它<b>只改 prefabDesc</b>，
        /// 老站点由原版 <c>PlanetTransport.Import</c> 自己补齐。
        ///
        /// <para><b>这是这个数在本仓库里唯一的写入口，是有意收拢的。</b>
        /// 巨型建筑走 <c>megabuildings.json</c> 的 <c>stationMaxDroneCount</c>（那是另一族建筑），
        /// 而 <c>machines.json</c> 里 6531 的 <c>maxDroneCount</c> 已经留成 0 让给这里——
        /// 同一个事实由两个文件、两个键各写一份，谁赢只取决于注册顺序，本仓库为
        /// 模型 ID / 建造栏槽位 / 合成面板格位栽过三次。</para>
        /// </summary>
        public StationDroneEntry[] stationDrones;
    }

    /// <summary>
    /// 单座物流站的<b>单格容量</b>覆盖值。
    ///
    /// <para><b>上限不是 <c>StationStore.max</c> 那个 Int32，而是 <c>StationStore.inc</c>。</b>
    /// 那一格的增产点数是<b>整格的总点数</b>，随 <c>count</c> 成正比长，而
    /// <c>StationComponent.InputItem</c> @0057–0061 是裸的 <c>inc += inc</c>，
    /// <b>一个夹子都没有</b>。所以安全上限是 <c>int.MaxValue ÷ 每件喷涂点数</c>：
    /// 不喷涂 21.47 亿、原版 Mk.III（每件 4 点）5.37 亿、本 mod 最高档（每件 6 点）
    /// <b>3.58 亿</b>。</para>
    ///
    /// <para><b>越界的后果是抛异常，不是数字难看。</b> <c>inc</c> 绕成负数之后，
    /// 喷涂等级是 <c>inc / count</c>——负下标去查 <c>Cargo.incTableMilli</c>
    /// 直接越界。所以要填大数，先确认这种货<b>不会被喷涂</b>。</para>
    /// </summary>
    [Serializable]
    internal class StationSlotEntry
    {
        public int itemId;

        /// <summary>这座建筑每一格的容量上限。见类注释里那张「安全上限」表。</summary>
        public int capacity;
    }

    /// <summary>单座物流站能停几架行星内物流运输机。</summary>
    [Serializable]
    internal class StationDroneEntry
    {
        public int itemId;

        /// <summary>
        /// 停放上限（<c>PrefabDesc.stationMaxDroneCount</c>）。0 = 不动这一项。
        ///
        /// <para><b>没有类型级上限</b>——和运输船不一样。船有 <c>idleShipIndices</c>
        /// 这个 UInt64 位图，所以 64 是硬顶；运输机这边只有
        /// <c>idleDroneCount</c>（Int32）和两个按它开的数组，没有位图。
        /// 真正的约束是内存：每架 84 字节 × 站点数。</para>
        /// </summary>
        public int maxDroneCount;
    }

    /// <summary>单个站点的最大充能功率设定。</summary>
    [Serializable]
    internal class StationEnergyEntry
    {
        public int itemId;

        /// <summary>
        /// 最大充能功率，单位<b>瓦</b>。落到 <c>PrefabDesc.workEnergyPerTick</c> 时除以 60
        /// （60 tick = 1 秒）。0 = 不动这一项。
        ///
        /// <para>单位写瓦而不是「每 tick 焦耳」是有意的：面板上写的是瓦，所有者说的也是瓦，
        /// 让配置和它们对齐，除以 60 这一步交给代码。<c>machines.json</c> 的
        /// <c>workEnergyWatt</c> 早就是这个口径。</para>
        /// </summary>
        public long chargePowerWatt;

        /// <summary>
        /// 最大能量容积，单位<b>焦耳</b>，来自 <c>PrefabDesc.stationMaxEnergyAcc</c>。0 = 不动这一项。
        /// </summary>
        public long maxEnergyJoule;
    }
}
