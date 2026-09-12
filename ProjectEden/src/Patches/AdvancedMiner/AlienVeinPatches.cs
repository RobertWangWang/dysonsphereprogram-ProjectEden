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
        /// 物品 ID → 一个它能挖多少矿，0 表示不够格当钻头。
        /// <b>下标就是物品 ID</b>：tick 路径上要 O(1) 而且不能分配。
        /// </summary>
        private static float[] _bitYield = new float[0];

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

        internal static bool Ready => Config != null && Config.enabled && VeinType > 0 && _bitYield.Length > 0;

        internal static void Load() => Config = JsonHelper.Load<AlienVeinConfig>("alienvein");

        // ── 注册 ──────────────────────────────────────────────

        internal static void OnPostAddData()
        {
            VeinType = 0;
            _bitYield = new float[0];
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

            BuildBitTable();
            RaiseMinerSlots();
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
        /// 把谓词在<b>全部物品</b>上跑一遍，结果存成一张按物品 ID 索引的表。
        ///
        /// 这一步是注册时做的，tick 路径上只查表——谓词本身可以运行时求值，
        /// 但没必要每 tick 都重算一遍四维。
        /// </summary>
        private static void BuildBitTable()
        {
            ItemProto[] items = LDB.items?.dataArray;

            if (items == null) return;

            var maxId = 0;

            foreach (ItemProto proto in items)
                if (proto != null && proto.ID > maxId)
                    maxId = proto.ID;

            _bitYield = new float[maxId + 1];

            float floor = _oreHardness - Config.slack;
            var qualified = 0;

            ProjectEdenPlugin.Log.LogInfo(
                $"── 外星矿脉：{Config.veinRef} 矿脉类型 {VeinType}，矿石硬度 {_oreHardness:0}，"
                + $"钻头硬度下限 {floor:0}（余量 slack {Config.slack:0}）──");

            foreach (ItemProto proto in items)
            {
                if (proto == null || !MetalPropertyPatches.Has(proto.ID)) continue;

                float h = MetalPropertyPatches.Axis(proto.ID, "hardness");
                float t = MetalPropertyPatches.Axis(proto.ID, "toughness");
                float margin = h - floor;

                if (margin <= 0f || t <= 0f) continue;

                var y = (float)(Config.@base
                                * Math.Pow(margin, Config.hardExponent)
                                * Math.Pow(t / Config.toughnessRef, Config.toughnessExponent));

                if (y < 1f) continue;

                _bitYield[proto.ID] = y;
                qualified++;

                ProjectEdenPlugin.Log.LogInfo(
                    $"  {proto.Name}({proto.ID})  硬度 {h:0} 韧性 {t:0}  余量 {margin:0}"
                    + $"  → 一个能挖 {y:N0} 矿");
            }

            if (qualified == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    "外星矿脉：**没有任何材料够格当钻头**，这种矿脉将完全挖不动。"
                    + "检查 alienvein.json 的 slack，或给候选材料补 metals.json 的四维行");
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
        internal static void Tick(ref MinerComponent miner, PlanetFactory factory,
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

            int bitId = station.storage[slot].itemId;
            float per = bitId > 0 && bitId < _bitYield.Length ? _bitYield[bitId] : 0f;

            if (per <= 0f || station.storage[slot].count <= 0)
            {
                Block(ref miningSpeed, entityId, per > 0f);

                return;
            }

            // 这一 tick 会挖出多少件
            float items = miningSpeed * perTick / miner.period;

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
                    ? "钻头槽空了。把槽设成 Demand，物流网会自动补。"
                    : "钻头槽里没有合格的材料。在物流站窗口把第 "
                      + Config.bitSlotIndex + " 格设成 Demand 并指定一种够硬的材料；"
                      + "启动日志里「外星矿脉」那一段列出了全部合格材料和各自能挖多少。")
                + "（这条只报一次）");
        }
    }
}
