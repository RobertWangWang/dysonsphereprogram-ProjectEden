#pragma warning disable 649 // 配置类的字段由 JSON 反序列化赋值

using System;
using System.Collections.Concurrent;
using System.Threading;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>data/alienvein.json 的映射类型。</summary>
    [Serializable]
    internal class AlienVeinConfig
    {
        public bool enabled;

        /// <summary>哪一种矿脉吃钻头（ores.json 的 ore key）。</summary>
        public string veinRef;

        /// <summary>给哪台采矿机开钻头槽。</summary>
        public int minerItemId;

        /// <summary>把那台的 <c>stationMaxItemKinds</c> 抬到几。</summary>
        public int stationMaxItemKinds;

        /// <summary>钻头槽用第几格。</summary>
        public int bitSlotIndex;

        /// <summary>允许比矿石软多少仍能挖。</summary>
        public float slack;

        public float hardExponent;
        public float toughnessRef;
        public float toughnessExponent;
        public float @base;

        /// <summary>钻头物品与它的配方模板。见 <see cref="DrillBitRegistry"/>。</summary>
        public AlienVeinBitEntry bit;

        /// <summary>钻头槽囤多少个。见 <see cref="EnsureBitSlot"/> 里为什么不能抄第 0 格。</summary>
        public int bitSlotCapacity;

        /// <summary>开了就在后台把每颗候选星球扫一遍，报出稀有矿脉具体在哪。见 <see cref="RareVeinProspector"/>。</summary>
        public bool prospectRareVeins;
    }

    /// <summary>
    /// 外星矿脉：<b>挖它要消耗钻头，而钻头是一个谓词不是一个物品</b>。
    /// 完整推导在仓库根目录的 `外星矿脉V1.md`。
    ///
    /// <b>为什么这里的谓词可以运行时求值。</b> CLAUDE.md 记着「谓词驱动的配方必须在
    /// 注册时展开成一条条具体配方，不能运行时求值」——因为 <c>RecipeProto.Items</c>
    /// 是 <c>int[]</c>，表达不了「任何硬度 ≥ X 的材料」。<b>但钻头不走配方</b>：
    /// 它是本 mod 自己的消耗逻辑，物流站槽里放着什么、四维是多少每 tick 都读得到。
    /// 所以那条约束不适用，可以直接对槽里的东西求值——
    /// <b>这是本仓库第一次真正用上谓词，而它恰好没落在配方上。</b>
    ///
    /// <b>钻头往哪放：采矿机自带的物流站。</b> <c>MinerComponent</c> 一个原料槽都没有，
    /// 但 <c>UIGame.OnPlayerInspecteeChange</c> IL 02F2 是
    /// <c>if (minerId != 0 &amp;&amp; stationId == 0) OpenMinerWindow()</c>——
    /// 大型采矿机自带 <c>StationComponent</c>，所以点它开的是<b>物流站窗口</b>，
    /// 钻头槽就是一个储物格，玩家自己设成 Demand，无人机自动送。零新界面。
    ///
    /// <b>第 1 格一定是空的，这是读 IL 确认的：</b>
    /// <c>Init</c> IL 01B5 <c>storage = new StationStore[stationMaxItemKinds]</c>；
    /// Init 里两个填充循环都只跑到 <c>collectionIds.Length</c> 并提前跳出；
    /// <c>UpdateVeinCollection</c> 全方法每处 <c>ldelema</c> 前面都是 <c>ldc.i4.0</c>，
    /// <b>只碰 <c>storage[0]</c></b>。所以抬高 <c>stationMaxItemKinds</c> 腾出来的格子
    /// 不会被任何人冲掉。
    ///
    /// <b>判定只挂在这一种矿脉上，所以对现存存档零影响</b>——老档里根本没有这种矿脉，
    /// 也就不需要配置开关，没钻头直接停机也可以接受。
    /// </summary>
    internal static class AlienVeinPatches
    {
        internal static AlienVeinConfig Config;

        /// <summary>吃钻头的那种矿脉的类型号。</summary>
        internal static int VeinType { get; private set; }

        /// <summary>矿石自己的硬度，谓词的基准。</summary>
        private static float _oreHardness;

        /// <summary>
        /// 一个钻头能挖多少矿。
        ///
        /// <b>钻头是统一的</b>——四维的差别搬到了「做一个钻头花多少料」那一头
        /// （见 <see cref="DrillBitRegistry"/>）。这个数取自合格材料里最高的那个产量，
        /// 所以最好的材料投 1 个是<b>算出来的</b>，不是定出来的。
        /// </summary>
        internal static float BitCapacity { get; private set; }

        internal static void SetBitCapacity(float capacity) => BitCapacity = capacity;

        /// <summary>
        /// 逐台采矿机的消耗进度。
        ///
        /// <b>不进存档</b>：重开游戏最多白送不到一个钻头，不值得为它升 SaveVersion。
        /// <b>必须是 ConcurrentDictionary</b>——采矿机跑在 <c>_miner_parallel</c> 上，
        /// 普通 Dictionary 会在几分钟内被写坏（仓库已经为这条付过一次代价）。
        /// </summary>
        private static readonly ConcurrentDictionary<long, float> Progress =
            new ConcurrentDictionary<long, float>();

        private static int _blockedLogged;

        internal static bool Ready =>
            Config != null && Config.enabled && VeinType > 0
            && BitCapacity > 0f && DrillBitRegistry.BitItemId > 0;

        internal static void Load() => Config = JsonHelper.Load<AlienVeinConfig>("alienvein");

        // ── 注册 ──────────────────────────────────────────────

        internal static void OnPostAddData()
        {
            VeinType = 0;
            Progress.Clear();

            // 报无聊的那一面：三种「没生效」在日志里要分得清
            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogInfo("外星矿脉：没有 alienvein.json，跳过");
                return;
            }

            if (!Config.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo("外星矿脉：alienvein.json 里 enabled 为 false，跳过");
                return;
            }

            if (!ResolveVein()) return;

            RaiseMinerSlots();

            ProjectEdenPlugin.Log.LogInfo(
                $"外星矿脉已就绪：{Config.veinRef} 矿脉类型 {VeinType}，"
                + $"钻头物品 {DrillBitRegistry.BitItemId}，一个能挖 {BitCapacity:N0} 矿，"
                + $"钻头槽是第 {Config.bitSlotIndex} 格（本 mod 自己布置成「钻头」的 Demand 槽，囤 {(Config.bitSlotCapacity > 0 ? Config.bitSlotCapacity : DefaultBitSlotCapacity):N0} 个）");
        }

        private static bool ResolveVein()
        {
            OreRegistry.Ore ore = null;

            // OreRegistry 只提供按矿脉类型查（Find），没有按 key 查的；
            // 这里就地扫一遍 —— 注册期一次性的事，不值得为它加一个公开方法
            foreach (OreRegistry.Ore candidate in OreRegistry.Ores)
                if (candidate?.Entry != null && candidate.Entry.key == Config.veinRef)
                    ore = candidate;

            if (ore == null)
            {
                ProjectEdenPlugin.Log.LogError($"外星矿脉：找不到矿种「{Config.veinRef}」，本功能不生效");
                return false;
            }

            VeinType = ore.VeinId;
            _oreHardness = MetalPropertyPatches.Axis(ore.OreItemId, "hardness");

            if (_oreHardness <= 0f)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"外星矿脉：「{Config.veinRef}」的矿石在 metals.json 里没有硬度，"
                    + "谓词算不出来，本功能不生效");

                VeinType = 0;

                return false;
            }

            return true;
        }

        /// <summary>
        /// 把采矿机的储物格数抬高，腾出钻头槽。
        ///
        /// <b>只抬一格。</b> 抬到 30 的话多出来的空格会把 30 格的物流站界面拖到采矿机上来，
        /// 这是气体采集器那条老教训（<c>StationComponent.Init</c> 按 <c>collectionIds.Length</c>
        /// 铺格位、拿 <c>stationMaxItemKinds</c> 封顶，多出来的只是空格）。
        ///
        /// <b>只影响之后新建的采矿机</b>：<c>storage</c> 在建造时就按当时的值固化进存档了
        /// （陷阱一）。这对本功能不构成问题——这种矿脉只出现在尚未生成的星球上，
        /// 玩家在它上面建的每一台都是新的。
        /// </summary>
        private static void RaiseMinerSlots()
        {
            ItemProto item = LDB.items.Select(Config.minerItemId);
            ModelProto model = item != null ? LDB.models.Select(item.ModelIndex) : null;

            if (model?.prefabDesc == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"外星矿脉：物品 {Config.minerItemId} 找不到 prefab，钻头槽开不出来");

                return;
            }

            int before = model.prefabDesc.stationMaxItemKinds;

            if (Config.stationMaxItemKinds <= before)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"外星矿脉：{item.Name} 的储物格已经是 {before} 格，不用抬");

                return;
            }

            model.prefabDesc.stationMaxItemKinds = Config.stationMaxItemKinds;

            ProjectEdenPlugin.Log.LogInfo(
                $"外星矿脉：{item.Name} 储物格 {before} → {Config.stationMaxItemKinds} 格，"
                + $"第 {Config.bitSlotIndex} 格留给钻头（**只对之后新建的采矿机有效**——"
                + "storage 在建造时固化进存档）");
        }

        // ── tick 路径 ─────────────────────────────────────────

        /// <summary>
        /// 这台采矿机现在挖的是不是那种矿脉。
        ///
        /// 判的是<b>当前正在挖的那一条矿脉</b>而不是整台机器：一台采矿机的覆盖范围里
        /// 可以有不同类型的矿。
        /// </summary>
        private static bool OnAlienVein(ref MinerComponent miner, PlanetFactory factory)
        {
            if (miner.veins == null || miner.veinCount <= 0) return false;

            VeinData[] pool = factory.veinPool;

            if (pool == null) return false;

            int at = miner.currentVeinIndex;

            if (at < 0 || at >= miner.veins.Length) at = 0;

            int veinId = miner.veins[at];

            if (veinId <= 0 || veinId >= pool.Length) return false;

            return (int)pool[veinId].type == VeinType;
        }

        /// <summary>
        /// 钻头的消耗与停机判定。<b>不分配、不装箱</b>——这是 tick 路径，
        /// 而且采矿机跑在 <c>_miner_parallel</c> 上。
        ///
        /// <c>perTick</c> 是调用方已经算好的「每 tick 的 time 累加量除以 miningSpeed」，
        /// 不在这里重算一遍。
        /// </summary>
        internal static void Tick(ref MinerComponent miner, PlanetFactory factory, float power,
            ref float miningSpeed, float perTick)
        {
            if (!Ready || miner.period <= 0) return;
            if (!OnAlienVein(ref miner, factory)) return;

            int entityId = miner.entityId;

            if (entityId <= 0 || entityId >= factory.entityPool.Length) return;

            int stationId = factory.entityPool[entityId].stationId;
            StationComponent[] pool = factory.transport?.stationPool;

            if (stationId <= 0 || pool == null || stationId >= pool.Length) return;

            StationComponent station = pool[stationId];
            int slot = Config.bitSlotIndex;

            if (station?.storage == null || slot < 0 || slot >= station.storage.Length)
            {
                // 老采矿机只有一格，压根没有钻头槽 —— 挖不动，而且原因和「没料」不同
                Block(ref miningSpeed, entityId, false);

                return;
            }

            // 这一格是我们加出来的，游戏自己不会初始化它 —— 见 EnsureBitSlot
            if (EnsureBitSlot(station, slot)) factory.transport.RefreshStationTraffic();

            // <b>没电也别扣。</b> 原版 InternalUpdate 第一行就是
            // <c>if (power &lt; 0.1f) return false;</c>（IL 0000）——没电一件都不挖。
            // 阈值抄它的，不另定一个，否则两边会在 0.1 附近分叉。
            //
            // <b>这个判断故意排在 EnsureBitSlot 之后。</b> 放前面的话，
            // 一台还没接电的新采矿机永远不会被布置出钻头槽，
            // 玩家打开面板看不到「需求 钻头」那一行，也就无从知道该运什么过来——
            // 而「没电」和「不需要钻头」在界面上长得一模一样。
            if (power < 0.1f) return;

            // <b>缓存满了就别扣钻头。</b> 本方法是在原版挖矿逻辑<u>之前</u>跑的，
            // 按 miningSpeed 推算这一 tick「会」挖多少件——但那是<b>名义速率</b>，
            // 原版真正决定挖不挖的是 <c>productCount >= GetCapacity(...)</c> 那道门
            // （被 <see cref="AdvancedMinerPatches"/> 从原版的 50 转译过来的三处之一）。
            // 机内缓存顶满、站点仓位也满的时候，原版一件都不挖，而我们照扣——
            // 玩家看到的就是「矿采满了，钻头还在烧」。
            //
            // <b>算消耗要跟着真实产出走，不能跟着速率参数走。</b>
            // 这和仓库里那条「面板读 speed、吞吐读 miningSpeed，改一个另一个不动」是同一类：
            // 同一件事有两个来源，挑错了不会报错，只会静悄悄地算错账。
            // <b>判据是「站点仓位收不收得下」，不是「机内缓存满没满」。</b>
            // 上一版判的是 productCount >= GetCapacity()，那道门在原版是有效的
            // （原版缓存只有 50，一满就停），但本 mod 把它抬到了 1000 万——
            // 按 24 万/分钟要四十多分钟才填满，于是那道门<b>几乎永远不成立</b>，
            // 玩家看到的仍然是「矿满了，钻头照烧」。
            //
            // 真正决定矿有没有去处的是原版 <c>StationComponent.UpdateVeinCollection</c>
            // 的第一道门（IL 001D–0035）：
            // <code>if (storage[0].localSupplyCount >= storage[0].max) return;</code>
            // 仓位满了它直接返回、一件都不收，矿只会堆在机内缓存里。
            //
            // <b>这里选择连挖矿一起停（miningSpeed 归零），而不是只停扣钻头。</b>
            // 只停扣的话，玩家把仓位堵满就能<b>白挖</b>一缓存的矿再放出来——
            // 那是把一个显示问题变成一个刷矿手法。没矿出去就没钻头消耗，两边都停才是一致的。
            // 和没钻头时一样<b>只碰 miningSpeed，不碰 speed</b>（后者是面板上那个数）。
            bool outFull = station.storage[0].localSupplyCount >= station.storage[0].max;
            bool bufferFull = miner.productCount >= AdvancedMinerPatches.GetCapacity(ref miner, factory);

            if (outFull || bufferFull)
            {
                miningSpeed = 0f;
                ReportFullOnce(entityId, outFull);

                return;
            }

            bool isBit = station.storage[slot].itemId == DrillBitRegistry.BitItemId;
            float per = BitCapacity;

            if (!isBit || station.storage[slot].count <= 0)
            {
                Block(ref miningSpeed, entityId, isBit);

                return;
            }

            // 这一 tick 会挖出多少件。
            //
            // <b>逐项对齐原版的产量算式</b>（InternalUpdate IL 0032–0056）：
            // <code>time += (int)(power × speedDamper × speed × miningSpeed × veinCount);</code>
            // 传进来的 perTick 只有 <c>speed × veinCount</c>（<c>MiningMultiplier</c> 那一项），
            // <b>power 和 speedDamper 原来都漏了</b>。漏 power 的后果是没电照扣、半电多扣一倍；
            // 漏 speedDamper 的后果是原版节流时多扣——两者都不会报错，只会静悄悄地算错账。
            float items = miningSpeed * perTick * power * miner.speedDamper / miner.period;

            if (items <= 0f) return;

            long key = (long)factory.planetId << 32 | (uint)entityId;
            float progress = Progress.TryGetValue(key, out float had) ? had + items : items;

            while (progress >= per)
            {
                if (station.storage[slot].count <= 0)
                {
                    // 刚好在这一 tick 用完：让它把这一 tick 挖完，下一 tick 再停
                    progress = 0f;
                    break;
                }

                station.storage[slot].count--;
                progress -= per;
            }

            Progress[key] = progress;
        }

        /// <summary>
        /// 配置没给时的钻头槽容量。
        ///
        /// <b>按满速开采推的：</b> 大型采矿机在本 mod 里约 20 万矿/秒
        /// （设计稿 外星矿脉V1.md §一；advancedminer.json 里没有这个数——speed 是按防溢出限幅逐台倒推的），一个钻头能挖 <see cref="BitCapacity"/> 矿，
        /// 实测 47,970 —— 也就是每秒烧掉约 4.2 个钻头。
        /// 3000 个够满速挖 12 分钟，足以扛过一次产线波动，
        /// 又不至于让一台机器把物流网里的钻头全吸走。
        /// </summary>
        private const int DefaultBitSlotCapacity = 3000;

        /// <summary>
        /// 把钻头槽布置好：给容量、指定物品、设成 Demand。返回是否真的改了。
        ///
        /// <b>这一步必不可少，而且正是「槽有了却还是看不到钻头」的原因。</b>
        /// <c>StationComponent.Init</c> 的矿脉分支只铺到 <c>collectionIds.Length</c> 为止
        /// （矿脉采集器就一种矿，所以只有第 0 格），多出来的格子留在默认值 ——
        /// <b><c>max</c> 是 0，也就是容量为零，什么都装不下</b>，物流站窗口里那一格自然是死的。
        /// 实测日志：<c>storage 长度 2</c>、<c>储物格 1：（空）本地 None 远程 None 数量 0/0</c>
        /// —— 数组确实是 2 格，容量却是 0。<b>「格子数够了」不等于「格子能用」。</b>
        ///
        /// <b>容量是单独一个配置值，<u>不能</u>抄第 0 格。</b> 那一格的 max 已经被
        /// <see cref="StationCapacityPatches"/> 放大到 1000 万（实测 10,005,000），
        /// 而本地 Demand 槽是<b>照着 max 要货</b>的
        /// （<c>localDemandCount = max - (count + localOrder)</c>）——
        /// 抄过来就等于第一台采矿机向物流网索要一千万个钻头，
        /// 把后面每一台都饿死。症状会是「我别的采矿机全停了」，
        /// 而那句话指向的地方离真正的原因很远。
        /// 所以这里只囤一个够用的缓冲，多出来的产能留给别的机器。
        ///
        /// <b>物品和 Demand 由本 mod 直接写，不留给玩家。</b> 配置里原来那句
        /// 「槽里放什么全由玩家定，那正是谓词的意义」，在钻头还是「一堆合格材料」时是对的；
        /// 谓词后来搬到了配方那一头（见 <see cref="DrillBitRegistry"/>），
        /// 钻头<b>只剩一种物品</b>，这一格没有第二种可能了，
        /// 再让玩家自己去物流站窗口里翻出来只是多一道谜题。
        ///
        /// <b>改完要刷物流网。</b> 需求是 <c>RefreshStationTraffic</c> 建的表算出来的，
        /// 只写 <c>localLogic</c> 不刷表，运输机不知道这里要货 —— 那一格会一直空着。
        /// 它要遍历整颗星球的物流站，所以只在真的改了的时候调（同
        /// <see cref="MegaStationPatches"/>）。
        /// </summary>
        private static bool EnsureBitSlot(StationComponent station, int slot)
        {
            var changed = false;

            int want = Config.bitSlotCapacity > 0 ? Config.bitSlotCapacity : DefaultBitSlotCapacity;

            // 兜底：真配了个比整格还大的数，仍然不许超过第 0 格的量级
            if (station.storage[0].max > 0 && want > station.storage[0].max)
                want = station.storage[0].max;

            if (station.storage[slot].max != want)
            {
                station.storage[slot].max = want;
                changed = true;
            }

            if (station.storage[slot].itemId <= 0)
            {
                station.storage[slot].itemId = DrillBitRegistry.BitItemId;
                station.storage[slot].localLogic = ELogisticStorage.Demand;
                changed = true;
            }

            return changed;
        }

        private static int _fullLogged;

        /// <summary>
        /// 缓存满了导致停挖，报一次。
        ///
        /// <b>这条要报，因为「不扣钻头」这件事本身是看不见的。</b>
        /// 玩家只会看到钻头数量不动，而那既可能是修好了，也可能是消耗逻辑整个没跑。
        /// </summary>
        private static void ReportFullOnce(int entityId, bool outFull)
        {
            if (Interlocked.Exchange(ref _fullLogged, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"外星矿脉：实体 {entityId} 的采矿机停机 —— "
                + (outFull
                    ? "站点仓位已满，矿没地方去（原版 UpdateVeinCollection 这时一件都不收）"
                    : "机内缓存已满")
                + "，所以这一 tick 既不挖矿也不扣钻头。（这条只报一次）");
        }

        /// <summary>没钻头就停。<b>只把 miningSpeed 归零，不碰 speed</b>——
        /// <c>speed</c> 是面板上那个数，改它会让玩家以为机器坏了。</summary>
        private static void Block(ref float miningSpeed, int entityId, bool hasSlotButEmpty)
        {
            miningSpeed = 0f;

            // 一次就够，而且先抢占再拼字符串
            if (Interlocked.Exchange(ref _blockedLogged, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"外星矿脉：实体 {entityId} 的采矿机停在这种矿脉上——"
                + (hasSlotButEmpty
                    ? "钻头槽空了。这一格已经是 Demand，物流网里有钻头就会自动补。"
                    : "钻头槽里放的不是钻头 —— 玩家把第 "
                      + Config.bitSlotIndex + " 格改成别的物品了。改回「钻头」即可；"
                      + "启动日志里「钻头」那一段列出了每种材料要投几个。")
                + "（这条只报一次）");
        }
    }
}
