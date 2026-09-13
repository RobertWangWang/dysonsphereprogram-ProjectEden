#pragma warning disable 649 // 字段由 JSON 反序列化赋值

using System;

namespace ProjectEden.Utils
{
    /// <summary>data/advancedminer.json 的映射类型。</summary>
    [Serializable]
    internal class AdvancedMinerConfig
    {
        /// <summary>大型采矿机的物品 ID（原版 2316）</summary>
        public int minerItemId;

        /// <summary>
        /// 是否把抽水类设备（抽水站、大抽水机等，MinerComponent.type == Water）
        /// 一并纳入提速、免消耗与缓存放大。按 type 判定，不必逐个列物品 ID。
        /// </summary>
        public bool boostWaterPumps;

        /// <summary>
        /// 是否把原油萃取站（MinerComponent.type == Oil）一并纳入提速、免消耗与缓存放大。
        /// 原油分支的产量乘数是「油井储量 × VeinData.oilSpeedMultiplier」而不是矿脉数，
        /// 防溢出倒推 speed 时按 MiningMultiplier 区分处理。
        /// </summary>
        public bool boostOilExtractors;

        /// <summary>采矿机送进站点之前的机内缓存上限，原版三条采集分支都写死 50</summary>
        public int capacity;

        /// <summary>
        /// 小型采矿机（出料到传送带的那种，原版 2301）的机内缓存上限，原版 50。
        /// 填 0 或不大于 50 就是保持原版。
        ///
        /// <b>这个值同时会被拿去当节流分母</b>——原版是
        /// <c>speedDamper = min(1, -2.45 × min(1, productCount / 50) + 2.47)</c>，
        /// 只抬缓存不抬分母的话，缓存过 50 之后采矿机会一直以 2% 速度爬。
        /// 见 <c>AdvancedMinerPatches.RetuneSmallMinerDamper</c>。
        /// </summary>
        public int smallMinerCapacity;

        /// <summary>
        /// 小型采矿机是否也不消耗矿脉。
        ///
        /// 大型采矿机 / 抽水站 / 采油站由 <see cref="forceMiningCostRate"/> 覆盖，
        /// 小型采矿机原本是个缺口。补上它之后，本 mod 对<b>所有</b>采矿设备都提供
        /// 「矿脉完全不消耗」，玩家因此可以放心关掉 UXAssist 的「矿脉保护」——
        /// 那个功能的前置<b>返回 false、整个跳过原版方法体</b>，会让本 mod 的
        /// 矿石→锭替换、缓存上限、钻头消耗全部静默失效。见 <c>UXAssistCompat</c>。
        /// </summary>
        public bool protectSmallMinerVeins;

        /// <summary>站点仓储上限（面板上的「上限」），来自 PrefabDesc.stationMaxItemCount</summary>
        public int stationCapacity;

        /// <summary>工作功率，单位焦/tick。60 tick = 1 秒，所以 375000 = 22.5 MW。0 表示不改。</summary>
        public long workEnergyPerTick;

        /// <summary>是否把矿石换成冶炼产物。站点仓位按矿脉产物建立，开启会导致产物堆积。</summary>
        public bool remapProduct;

        /// <summary>
        /// 是否把 speedDamper 固定为 1。原版按 productCount/50 节流，
        /// 高速采矿时缓存瞬间超过 50，速度会被钳到 2%。
        /// </summary>
        public bool overrideSpeedDamper;

        /// <summary>
        /// 是否按防溢出限幅倒推每台采矿机能吃满的 speed。
        /// 关闭则只用满级科技倍率，那通常只用掉限幅空间的很小一部分。
        /// </summary>
        public bool maxOutMinerSpeed;

        /// <summary>矿物利用科技按此等级算满级</summary>
        public int maxMiningTechLevel;

        /// <summary>
        /// 直接指定矿物消耗倍率：0 = 矿脉完全不消耗（矿物利用率的上限）。
        /// 填负数则改为按满级科技推算。
        /// </summary>
        public float forceMiningCostRate;

        /// <summary>每 tick 允许累加到 MinerComponent.time 上的最大值，防 int 溢出</summary>
        public float maxTimeIncrementPerTick;

        /// <summary>
        /// 是否取消大型采矿机的建造间距限制。原版在 CheckBuildConditions 里按
        /// 25 米（对采矿机）/ 15 米（对其他建筑）判定，超出会给出 TowerTooClose /
        /// MK2MinerTooClose。注意原版<b>本来就允许</b>两台采矿机重叠，这里放开的是
        /// 采矿机与其他物流站之间的间距。
        /// </summary>
        public bool removeBuildDistanceLimit;

        /// <summary>
        /// 是否允许大型采矿机与其他建筑重叠放置。诊断实测「无法与其他大型采矿站建造在
        /// 同一个位置」对应的是 EBuildCondition.Collide。做两件事：转译掉 desc.veinMiner
        /// 分支里那个 layer 2048 的 Physics.CheckBox（采矿机专属碰撞），并在后置里清掉
        /// 采矿机自己的 Collide。副作用是采矿机也能叠进别的建筑。
        /// </summary>
        public bool allowMinerOverlap;

        /// <summary>
        /// 是否允许采矿机盖在原油涌泉上。原版在建造时收集矿脉的两个循环里都有
        /// <c>if (veinPool[id].type == EVeinType.Oil) continue;</c>，这是唯一的卡点——
        /// 建成之后 veins[]、collectionIds、Vein 分支全程都不看矿脉类型。
        /// 两处循环的指令特征相同，所以普通采矿机会一并放开。
        /// </summary>
        public bool allowMinerOnOil;

        /// <summary>
        /// 是否让抽水站在熔岩星球上抽出岩浆。
        /// <c>PlanetData.waterItemId</c> 是个带标签的联合体，熔岩海洋编码为 <b>-1</b>；
        /// 原版的出料分支只在 <c>&gt; 0</c> 时产出，建造又另有一张
        /// <c>prefabDesc.waterTypes</c> 白名单。两道闸相互独立，详见 LavaPumpPatches。
        /// </summary>
        public bool lavaPumping;

        /// <summary>岩浆在 ores.json 的 items 段里的 key。<b>按名字解析，不写死物品号</b>，因为 ResolveItemId 碰号时会顺延。</summary>
        public string lavaItemKey;

        public OreProduct[] productMap;
    }

    [Serializable]
    internal class OreProduct
    {
        /// <summary>矿石物品 ID。填 0 则按 veinType 现查（VeinProto.MiningItem）。</summary>
        public int ore;

        /// <summary>
        /// 产物物品 ID。填 0 则去找「只吃这一种矿」的那条原版配方，取它的第一个产物。
        /// 稀有矿（可燃冰、分形硅石、刺笋结晶）在原版正好都是这个形状。
        /// </summary>
        public int product;

        /// <summary>
        /// 矿脉类型（EVeinType）：1 铁 2 铜 3 硅 4 钛 5 石 6 煤 7 原油
        /// 8 可燃冰 9 金伯利 10 分形硅石 11 有机晶体 12 光栅石 13 刺笋结晶 14 单极磁石。
        /// 只在 ore 填 0 时使用。
        /// </summary>
        public int veinType;

        /// <summary>仅供 JSON 里注释用，代码不读</summary>
        public string comment;
    }
}
