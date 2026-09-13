#pragma warning disable 649 // 字段由 JSON 反序列化赋值

using System;

namespace ProjectEden.Utils
{
    /// <summary>data/machines.json 的映射类型：克隆原版机器做出来的新生产设备。</summary>
    [Serializable]
    internal class MachinesConfig
    {
        public bool enabled;

        public MachineEntry[] machines;
    }

    /// <summary>
    /// 一台新机器：整台从某个原版建筑克隆而来，只换外观、名字和<b>配方类型</b>。
    ///
    /// <b>配方类型是这件事的全部意义。</b> 原版的配方选择器按单一 ERecipeType 过滤
    /// （UIRecipePicker.RefreshIcons 拿 filter 和 recipe.Type 比，UIAssemblerWindow
    /// 传的是 prefabDesc.assemblerRecipeType），一台机器只认一种类型。所以「做一类新配方
    /// 的新机器」等价于「造一个新的类型号 + 一台 assemblerRecipeType 指向它的机器」。
    ///
    /// ERecipeType 是 int 枚举，(ERecipeType)9 不需要有名字也合法，和 EVeinType 一个道理，
    /// 不用写 preloader。原版占了 1~8 和 15，<b>9~14 是空的</b>。
    /// </summary>
    [Serializable]
    internal class MachineEntry
    {
        /// <summary>日志用的短名，不进存档</summary>
        public string key;

        public bool enabled;

        /// <summary>
        /// 这台建筑是哪一类：<c>assembler</c>（默认，认一种配方类型的制造设备）、
        /// <c>station</c>（物流站，见 <see cref="station"/>）
        /// <c>accumulator</c>（蓄电器，见 <see cref="accumulator"/>）
        /// <c>exchanger</c>（能量枢纽，见 <see cref="exchanger"/>）
        /// 或 <c>generator</c>（发电设备，见 <see cref="generator"/>）。
        /// </summary>
        public string kind;

        public string displayName;
        public string description;

        /// <summary>从哪个原版建筑克隆：物品模板、模型、碰撞体、prefabDesc 全部来自它</summary>
        public int copyFromItemId;

        /// <summary>新物品 ID。<b>进存档</b>，定下来别改</summary>
        public int itemId;

        /// <summary>新模型 ID。<b>进存档</b>。被占用会自动顺延</summary>
        public int modelId;

        /// <summary>这台机器认哪种配方类型。原版 1~8 / 15 已用，自定义从 9 起</summary>
        public int recipeType;

        /// <summary>配方类型在界面上的名字，比如「电化学」</summary>
        public string recipeTypeName;

        /// <summary>物品提示栏「类型」那一行的文字，比如「电化学设备」</summary>
        public string machineTypeName;

        /// <summary>合成面板格位的<b>起点</b>，被占用时自动往后找。留 0 则沿用源建筑那一页</summary>
        public int gridIndex;

        /// <summary>
        /// 把这台建筑放进本 mod 的「巨型建筑」分页与同名建造分类，而不是跟着源建筑走。
        ///
        /// <b>页号不能写死在 JSON 里</b>：合成面板的页号由 CommonAPI 在运行时分配
        /// （<c>MegaBuildingRegistry.TabIndex</c>），建造分类号在 megabuildings.json 里配。
        /// 所以这里只给行/列和槽位，页号与分类号由注册器现取。
        /// 打开后 <see cref="gridIndex"/> 与 <see cref="buildIndex"/> 都不再参与计算。
        /// </summary>
        public bool megaTab;

        /// <summary>megaTab 为真时，合成面板格位的行；页号由巨型建筑分页现取</summary>
        public int gridRow;

        /// <summary>megaTab 为真时，合成面板格位的列</summary>
        public int gridCol;

        /// <summary>megaTab 为真时，建造栏里的槽位；分类号取自 megabuildings.json 的 buildCategory</summary>
        public int buildSlot;

        /// <summary>
        /// 建造栏位置 = 分类 × 100 + 槽位。留 0 则跟着源建筑的分类走、自动找空槽。
        /// 化工厂是 508（第 5 类第 8 槽）。
        /// </summary>
        public int buildIndex;

        /// <summary>模型材质的染色，RGB 0~1。留空则不染</summary>
        public float[] tint;

        // ── 图标：由源建筑的图标改色而来 ─────────────────────

        public float iconHue;
        public float iconSaturationScale;
        public float iconMinSaturation;
        public float iconValueScale;

        // ── 建造这台机器的配方 ───────────────────────────────

        public int recipeId;
        public int recipeTimeSpend;
        public int recipeGridIndex;

        /// <summary>
        /// 这条建造配方<b>只能手搓</b>，任何制造设备都做不了。
        ///
        /// 做法是把配方的 <c>Type</c> 设成 0（None）：合成器只看
        /// <c>RecipeProto.Handcraft</c>，照样列出来；而配方选择器的过滤是
        /// <c>filter != 0 &amp;&amp; filter != recipe.Type 就跳过</c>，
        /// 任何机器的 <c>assemblerRecipeType</c> 都不会等于 0，于是哪台机器都选不到它。
        ///
        /// 代价只有一处，已核对过：<c>ItemProto.InitProductionMask</c> 开头就
        /// <c>if (recipe.Type == 0) continue;</c>，所以产物拿不到 productionMask 位。
        /// 而整个程序集里读这个位的只有 <c>UIReferenceSpeedTip</c>（参考速率面板）——
        /// 手搓配方本来就没有工厂产能，没有损失。
        /// </summary>
        public bool recipeHandcraftOnly;

        /// <summary>原料。只吃原版物品，写 { "id": …, "count": … }</summary>
        public RecipeItemEntry[] recipeItems;

        /// <summary>kind 为 station 时的参数；其余情况忽略</summary>
        public MachineStationEntry station;

        /// <summary>kind == accumulator 时的参数</summary>
        public MachineAccumulatorEntry accumulator;

        /// <summary>kind == exchanger 时的参数</summary>
        public MachineExchangerEntry exchanger;

        /// <summary>kind == generator 时的参数</summary>
        public MachineGeneratorEntry generator;

        /// <summary><c>kind: "miner"</c> 专用。</summary>
        public MachineMinerEntry miner;
    }

    /// <summary>
    /// 发电设备型建筑的参数：整台克隆一个原版发电建筑，只放大发电功率。
    ///
    /// <b>为什么只要改一个数。</b> <c>PowerSystem.NewGeneratorComponent</c>
    /// 把 <c>PrefabDesc</c> 的 <c>photovoltaic</c> / <c>windForcedPower</c> /
    /// <c>gammaRayReceiver</c> / <c>geothermal</c> / <c>genEnergyPerTick</c> /
    /// <c>useFuelPerTick</c> / <c>fuelMask</c> 逐字段抄进
    /// <c>PowerGeneratorComponent</c>（IL 0090~0132），发电种类和功率全在这里定。
    /// 风力那一支的每 tick 上限是 <c>EnergyCap_Wind</c>：
    /// <c>capacityCurrentTick = (long)(windStrength * genEnergyPerTick)</c>，
    /// <b>没有任何钳位</b>，所以功率乘几倍就是几倍。
    ///
    /// <b>参数是倍率不是绝对值</b>，和蓄电器同理：真值存在源建筑的 PrefabDesc 里，
    /// 那是 resources.assets 的预制体，离线看不到，写死绝对值就是猜。
    /// 乘出来的真值会打进日志，物品提示栏的「发电功率」也会自动跟上
    /// （<c>ItemProto.GetPropValue</c> 直接读 <c>prefabDesc.genEnergyPerTick × 60</c>）。
    /// </summary>
    [Serializable]
    /// <summary>
    /// <c>kind: "miner"</c>：克隆一台采矿机，把产量<b>钉死</b>成一个固定值。
    ///
    /// <b>「钉死」是这种机器唯一的卖点，也是它全部的实现难度。</b>
    /// 原版每 tick 的产出是
    /// <c>time += power × speedDamper × speed × miningSpeed × veinCount</c>，
    /// 其中 <c>miningSpeed</c> 是被「矿物利用」系列科技放大过的，<c>veinCount</c> 是脚下矿脉数。
    /// 要让产量和这两者都无关，就得每 tick 反解 <c>speed</c> 把它们除掉——
    /// 见 <c>MiniMinerPatches</c>。
    /// </summary>
    internal class MachineMinerEntry
    {
        /// <summary>每分钟采多少矿。固定值，不随科技和矿脉数变。</summary>
        public int oresPerMinute;

        /// <summary>工作功率，瓦。</summary>
        public long workEnergyWatt;

        /// <summary>自带物流站那一格的容量。</summary>
        public int stationCapacity;

        /// <summary>
        /// 采矿时消耗矿脉储量吗。<c>false</c> = 矿脉永不枯竭。
        /// </summary>
        public bool consumeVeins;
    }

    internal class MachineGeneratorEntry
    {
        /// <summary>发电功率倍率。genEnergyPerTick</summary>
        public float powerMultiplier;

        /// <summary>
        /// 燃料消耗倍率。留 0 则跟随 <see cref="powerMultiplier"/>——
        /// 两者同倍等于发电效率不变，这几乎总是想要的。
        /// 风能 / 光伏这类没有燃料的发电建筑用不到它。
        ///
        /// <b>被 <see cref="efficiency"/> 覆盖</b>：两个都填时以 efficiency 为准。
        /// </summary>
        public float fuelMultiplier;

        /// <summary>
        /// 能量利用率，直接指定。填了它就按
        /// <c>useFuelPerTick = genEnergyPerTick / efficiency</c> 反推，不再用
        /// <see cref="fuelMultiplier"/>。
        ///
        /// <b>为什么要有这个字段。</b> 效率是
        /// <c>genEnergyPerTick / useFuelPerTick</c>（见
        /// <c>PowerGeneratorComponent.GenEnergyByFuel</c>：扣掉的燃料能量是
        /// <c>energy × useFuelPerTick / genEnergyPerTick</c>）。想要一个指定的效率，
        /// 用倍率表达就得写 <c>fuelMultiplier = powerMultiplier × 源效率 / 目标效率</c>——
        /// 一个只有回推才看得懂的数。**仓库的规矩是配置里写依据，不写算好的结果**，
        /// 所以这里让配置直接写效率，倍率由代码去算并打进日志。
        ///
        /// <b>它同时是 ABN_PowerGenerator 那条红线的锚点。</b> 该检查要求运行时的
        /// <c>useFuelPerTick</c> 不低于 prefab 的 0.7 倍，所以逐台改这个字段的功能
        /// 必须把 prefab 锚在<b>效率最高</b>的那一档，其余只能往上乘。
        /// </summary>
        public float efficiency;

        /// <summary>
        /// 可烧的燃料类型掩码。留 0 则继承源建筑的。
        ///
        /// <c>ItemProto.fuelNeeds</c> 按<b>掩码值</b>索引且长度为 64，所以合法位只有
        /// bit 0~5。空位要用 <c>FuelSurvey</c> 在游戏里查——物品表离线读不到。
        /// </summary>
        public int fuelMask;
    }

    /// <summary>
    /// 物流站型建筑的参数。
    ///
    /// <b>「行星内运输机」和「星际运输船」本来就是同一个组件的两半。</b>
    /// StationComponent 同时持有 idleDroneCount 和 idleShipCount，
    /// 由 prefabDesc.isStellarStation 决定要不要分配运输船——所以只要克隆一台
    /// 星际物流运输站，两种无人机就都在了，一行逻辑都不用写。
    ///
    /// 这里只调数量。储量、格数、充能功率留 0 的话，交给 stations.json 那套统一处理
    /// （注册时会把本建筑的物品 ID 报给 StationCapacityPatches）。
    /// </summary>
    /// <summary>
    /// 蓄电器型建筑的参数。<b>全部是倍率，不是绝对值。</b>
    ///
    /// 原因：容量和充放电功率都存在源建筑的 <c>PrefabDesc</c> 里，而 PrefabDesc 是从
    /// <c>resources.assets</c> 的预制体读出来的，<b>离线看不到、反编译也查不到</b>——
    /// 硬写绝对值等于凭记忆猜。倍率则是拿运行时的真值去乘，原版怎么调都跟得上，
    /// 而且「充电快多少倍、容量大多少倍」本来就是这类建筑想表达的东西。
    /// 注册时会把换算出来的绝对值打进日志，想钉死数值照着日志看即可。
    /// </summary>
    [Serializable]
    internal class MachineAccumulatorEntry
    {
        /// <summary>储能容量倍率（能量密度）。maxAcuEnergy</summary>
        public float capacityMultiplier;

        /// <summary>「满」版本物品。留空则这台蓄电器没有满版本</summary>
        public MachineFullVariantEntry fullVariant;

        /// <summary>充电功率倍率（充电速度）。inputEnergyPerTick</summary>
        public float inputMultiplier;

        /// <summary>放电功率倍率。outputEnergyPerTick</summary>
        public float outputMultiplier;
    }

    /// <summary>
    /// 蓄电器的「满」版本物品，对应原版的「蓄电器（满）」。
    ///
    /// <b>它不是一台新建筑，而是同一台建筑的另一个物品面孔。</b> 原版 2206 和 2207
    /// <b>共用同一个 ModelIndex</b>，区别只有三处：<c>BuildIndex = 0</c>（不占建造栏槽位，
    /// 只能从物品栏放下去）、自己的 <c>GridIndex</c>、以及 <c>FuelType</c> + <c>HeatValue</c>
    /// ——「满」的那个是<b>机甲燃料</b>。
    ///
    /// 空↔满的转换由能量枢纽做，配对关系写在<b>枢纽</b>的
    /// <c>PrefabDesc.emptyId / fullId</c> 上（<see cref="MachineExchangerEntry"/>），
    /// 蓄电器自己不知道有没有满版本。
    /// </summary>
    [Serializable]
    internal class MachineFullVariantEntry
    {
        /// <summary>新物品 ID。<b>进存档</b></summary>
        public int itemId;

        public string displayName;
        public string description;

        /// <summary>从哪个原版「满」物品抄模板并改色图标。2207 = 蓄电器（满）</summary>
        public int copyFromItemId;

        /// <summary>合成面板格位的起点，0 则跟着源物品走</summary>
        public int gridIndex;

        /// <summary>燃料类型。0 则继承源物品（原版是 8）</summary>
        public int fuelType;

        /// <summary>
        /// 热值（焦耳）。<b>留 0 则按容量倍率从源物品的热值推</b>——
        /// 容量放大几倍，一块满电池能放出的能量就该是几倍，不用手写数字。
        /// </summary>
        public long heatValue;

        /// <summary>
        /// 机甲反应堆的<b>功率</b>加成。<c>Mecha.GenerateEnergy</c> 里
        /// <c>ratio = ItemProto.ReactorInc + 1</c>，再乘到 <c>reactorPowerGen</c> 上，
        /// 所以 1.5 就是<b>功率 +150%</b>（×2.5）。
        ///
        /// 注意它只改<b>功率</b>（每秒能取多少），不改<b>总能量</b>（那是 heatValue）——
        /// 加成高等于同一块电池放得更快、但放得更少次。原版参照：
        /// 蓄电器（满）1.0、氢燃料棒 1.0、氘核燃料棒 2.0、原油 −0.5。
        ///
        /// 留空（0）则继承源物品的值；要显式配成「无加成」请填一个极小的负数是不行的，
        /// 直接改源物品或接受继承即可。
        /// </summary>
        public float reactorInc;
    }

    /// <summary>
    /// 能量枢纽型建筑的参数。
    ///
    /// <b>一台枢纽只服务一对空/满。</b> <c>PowerExchangerComponent</c> 的
    /// <c>emptyId</c> / <c>fullId</c> 直接来自 <c>PrefabDesc.emptyId</c> / <c>fullId</c>，
    /// 整个组件从头到尾就认这两个 ID——所以想让新的蓄电器能充满，
    /// 办法不是给原版枢纽打补丁，而是<b>再克隆一台枢纽指向新的一对</b>，
    /// 这也正是原版建模这件事的方式。
    /// </summary>
    [Serializable]
    internal class MachineExchangerEntry
    {
        /// <summary>
        /// 服务哪一台蓄电器的空/满对，填那台机器的 <c>key</c>。
        /// 那条目必须排在本条之前（注册按数组顺序走），否则解析不到会报 ERROR。
        /// </summary>
        public string pairMachineKey;

        /// <summary>充放电功率倍率。exchangeEnergyPerTick</summary>
        public float energyMultiplier;

        /// <summary>内部能量池倍率。maxExcEnergy</summary>
        public float poolMultiplier;
    }

    [Serializable]
    internal class MachineStationEntry
    {
        /// <summary>行星内物流运输机的上限</summary>
        public int maxDroneCount;

        /// <summary>
        /// 星际物流运输船的上限。<b>不能超过 64</b>：
        /// StationComponent.idleShipIndices 是一个 UInt64 位图，按 <c>1L &lt;&lt; (index &amp; 63)</c> 索引。
        /// </summary>
        public int maxShipCount;

        /// <summary>留 0 则沿用来源建筑的值，之后由 stations.json 统一放大</summary>
        public int maxItemCount;

        public int maxItemKinds;

        public long maxEnergyAcc;

        /// <summary>最大充能功率，单位焦/tick。留 0 则沿用来源建筑</summary>
        public long workEnergyPerTick;

        // ── 第三种无人机：配送运输机 ─────────────────────────

        /// <summary>
        /// 配送运输机的数量。&gt; 0 就给这台建筑挂上 DispenserComponent，
        /// 同时自带一个<b>不露面的缓冲仓</b>当货源——见 HubCourierPatches。
        /// 填 0 则完全不挂，行为和普通物流站一样。
        /// </summary>
        public int courierCount;

        /// <summary>配送器的储能上限，单位焦。留 0 用一个够用的默认值</summary>
        public long courierEnergyAcc;

        /// <summary>
        /// 对机甲的配送模式：0 关闭 / 1 只回收 / 2 收发都做 / 3 只供应。
        ///
        /// <b>必须配。</b> DispenserComponent.Init 不给 playerMode 赋值，默认是 0（关闭），
        /// 配送运输机会一动不动。而枢纽点开的是物流站面板、没有配送器面板可调，
        /// 所以这个值只能从配置来。
        /// </summary>
        public int playerDeliveryMode;

        /// <summary>对周围储物仓的配送模式：0 关闭 / 1 供应 / 2 需求。枢纽一般填 0</summary>
        public int storageDeliveryMode;

        /// <summary>
        /// 自动把枢纽里有的货补进伊卡洛斯的「配送需求清单」。
        ///
        /// <b>不开的话配送运输机不会动。</b> 原版配送器每 tick 遍历的是
        /// <c>Player.deliveryPackage</c>——只对清单里配过的物品干活，清单是空的就全员待命。
        /// 这一项打开后，枢纽非空槽位里的货会自动占用清单里的空格，需求量按下面那个值给。
        ///
        /// <b>只填空格，绝不改玩家已经配好的条目。</b> 清单是玩家全局的东西，不是这台建筑的。
        /// </summary>
        public bool autoDeliveryList;

        /// <summary>自动填清单时每种货给几组的需求量（同时也是回收线）。留 0 按 1 组算</summary>
        public int deliveryKeepStacks;

        /// <summary>
        /// 每 10 秒往日志里打一行枢纽状态：槽位 / 缓冲仓 / 配送清单 / 运输机 / 配对 / 当前服务的货。
        ///
        /// 排查配送不动时很有用——这条链有五段，任何一段空了表现都是「运输机停着」，
        /// 光看画面分不出是哪一段。平时关着，别刷屏。
        /// </summary>
        public bool courierDebugLog;

        /// <summary>
        /// 缓冲仓的格数（行 × 列）。它只是配送运输机和 30 个槽位之间的中转，
        /// 有多少种货就要多少格，给到和槽位数一样即可，多了没用。
        /// </summary>
        public int bufferCols;

        public int bufferRows;
    }
}
