using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让矩阵研究站直接和本行星的物流站互通有无，不用铺传送带和分拣器：
    /// <b>取料</b>（物流站 → 研究站）和<b>出货</b>（研究站 → 物流站）两个方向都做。
    ///
    /// 只做取料的话，生产模式造出来的矩阵会一直堆在 produced[] 里，堆到上限就停产，
    /// 玩家还是得铺分拣器把矩阵搬出去——那虚拟供料就只做了一半。
    ///
    /// <b>为什么不是真的装一个物流站</b>：研究站是要堆叠的，给 prefabDesc 打开 isStation
    /// 会让<b>每一层</b>都长出一个独立的 StationComponent 和一批无人机（5 层就是 5 个站），
    /// 而且 prefabDesc 只对新建生效，老存档里的研究站还得额外写补建逻辑。
    /// 这里改成「虚拟供料」：每隔若干 tick 把物流站里 Supply 的货直接搬进研究站的缓冲区。
    /// 效果就是研究站自动从物流网取料，代价是看不到无人机实体在飞。
    ///
    /// <b>两种模式的缓冲区完全不同</b>（见 MatrixLabPatches）：
    ///   · 生产模式 served[]      —— 明文个数，按配方的 requires / requireCounts
    ///   · 研究模式 matrixServed[] —— <b>个数 × 3600</b> 的放大值，按当前科技的 matrixPoints
    ///
    /// <b>三趟扫描而不是「每个研究站扫一遍物流站」</b>：后者是 labs × stations，行星上研究站
    /// 一多就是平方级开销。这里先汇总缺口、再走一遍物流站取货、最后分发，整体是 labs + stations。
    /// 出货方向同样三趟（汇总产量 → 塞进物流站 → 按实际收下的量扣账）。
    /// 用的容器都是复用的静态字典，稳定之后不再分配。
    ///
    /// <b>两个方向都只认玩家明说的那个标记，严格对称</b>：
    /// 取货只从「本地供应」格拿——抢别人标了「需求」的货是不对的；
    /// 出货只往「本地需求」格送——那是玩家明说「请把这个东西送到这里」的地方。
    /// 出货一度是不看物流设定、只按物品 ID 匹配的，结果随手放个站开一格矩阵，
    /// 货就凭空到位并立刻对外供应，等于全行星瞬间传送。见 PushToStations。
    /// </summary>
    [HarmonyPatch]
    internal static class LabLogisticSupplyPatches
    {
        private static LabConfig Config => ProjectEdenPlugin.LabConfig;

        private const int MatrixScale = 3600;

        // PlanetTransport.GameTick 会在 GameLogic.FactoryTransportGameTick_Parallel 里
        // <b>并行处理多个行星</b>（实测 31 个工作线程）。行星数据本身各管各的，但静态字典是
        // 所有线程共享的——多个线程同时往同一个 Dictionary 写会直接把它写坏，报
        // 「Operations that change non-concurrent collections must have exclusive access」。
        // 所以这些临时容器必须 [ThreadStatic]：每个工作线程一份，互不干扰，也不用加锁。
        // 首次使用时惰性创建（[ThreadStatic] 的字段初始化器只在第一个线程上跑）。

        [ThreadStatic] private static Dictionary<int, long> _shortfall, _pool, _output, _taken;

        /// <summary>取料：物品 → 本行星所有研究站合计还缺多少（研究模式记的是放大后的值）。</summary>
        private static Dictionary<int, long> Shortfall => _shortfall ?? (_shortfall = new Dictionary<int, long>());

        /// <summary>取料：物品 → 这一轮从物流站实际取到多少（同上，研究模式是放大值）。</summary>
        private static Dictionary<int, long> Pool => _pool ?? (_pool = new Dictionary<int, long>());

        /// <summary>出货：物品 → 本行星所有研究站合计可以出多少（生产模式的 produced[]，明文个数）。</summary>
        private static Dictionary<int, long> Output => _output ?? (_output = new Dictionary<int, long>());

        /// <summary>出货：物品 → 物流站实际收下了多少。</summary>
        private static Dictionary<int, long> Taken => _taken ?? (_taken = new Dictionary<int, long>());

        private static int _loggedIn, _loggedOut;

        /// <summary>
        /// 挂 PlanetTransport.GameTick 之后：这时本行星的物流站已经跑完这一 tick。
        /// 注意它会<b>并行处理多个行星</b>，所以临时容器必须 [ThreadStatic]，见上面的说明；
        /// 动储物格时仍然按原版的规矩上锁。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void PlanetTransport_GameTick(PlanetTransport __instance, long time)
        {
            // 诊断打点：**放在一切 return 之前**，否则「这一条便宜」和「这一条根本没跑」
            // 在账上长得一模一样。开关关着时它是一句立即返回
            Diagnostics.TransportSplitProbe.Phase("实验室取料");

            if (Config == null) return;
            if (!Config.logisticSupply && !Config.logisticOutput) return;

            int interval = Config.supplyIntervalTicks > 0 ? Config.supplyIntervalTicks : 10;

            if (time % interval != 0) return;

            PlanetFactory factory = __instance.factory;
            FactorySystem system = factory?.factorySystem;

            if (system?.labPool == null || __instance.stationPool == null) return;

            if (Config.logisticSupply) SupplyIn(system, __instance);
            if (Config.logisticOutput) ShipOut(system, __instance);
        }

        /// <summary>取料方向：物流站的 Supply 格 → 研究站的缓冲区。</summary>
        private static void SupplyIn(FactorySystem system, PlanetTransport transport)
        {
            Shortfall.Clear();

            if (!CollectShortfall(system)) return;

            Pool.Clear();

            if (!TakeFromStations(transport)) return;

            Distribute(system);
        }

        /// <summary>出货方向：研究站的 produced[] → 物流站的储物格。</summary>
        private static void ShipOut(FactorySystem system, PlanetTransport transport)
        {
            Output.Clear();

            if (!CollectOutput(system)) return;

            Taken.Clear();

            if (!PushToStations(transport)) return;

            DeductOutput(system);
        }

        // ── 第一趟：汇总所有研究站的缺口 ──────────────────────

        private static bool CollectShortfall(FactorySystem system)
        {
            var any = false;

            for (var i = 1; i < system.labCursor; i++)
            {
                if (system.labPool[i].id != i) continue;

                any |= system.labPool[i].researchMode
                    ? CollectResearch(ref system.labPool[i])
                    : CollectAssemble(ref system.labPool[i]);
            }

            return any;
        }

        private static bool CollectAssemble(ref LabComponent lab)
        {
            RecipeExecuteData recipe = lab.recipeExecuteData;

            if (recipe?.requires == null || lab.served == null) return false;

            int[] requires = recipe.requires;
            int[] requireCounts = recipe.requireCounts;
            long batches = Config.supplyAssembleBatches > 0 ? Config.supplyAssembleBatches : 2000;

            var any = false;
            int count = requires.Length < lab.served.Length ? requires.Length : lab.served.Length;

            for (var i = 0; i < count; i++)
            {
                if (requires[i] <= 0) continue;

                long want = (i < requireCounts.Length ? requireCounts[i] : 1) * batches - lab.served[i];

                if (want <= 0) continue;

                Add(Shortfall, requires[i], want);
                any = true;
            }

            return any;
        }

        private static bool CollectResearch(ref LabComponent lab)
        {
            int[] matrixIds = LabComponent.matrixIds;
            int[] matrixPoints = LabComponent.matrixPoints;

            if (matrixIds == null || matrixPoints == null || lab.matrixServed == null) return false;

            // 目标存量按「个数」配置，内部换算成放大值
            long targetScaled = (Config.supplyMatrixItems > 0 ? Config.supplyMatrixItems : 1000L) * MatrixScale;

            var any = false;
            int count = matrixIds.Length < lab.matrixServed.Length ? matrixIds.Length : lab.matrixServed.Length;

            if (count > matrixPoints.Length) count = matrixPoints.Length;

            for (var i = 0; i < count; i++)
            {
                // matrixPoints 是当前研究的科技每 hash 需要的各矩阵点数，为 0 表示这个科技不用它
                if (matrixPoints[i] <= 0 || matrixIds[i] <= 0) continue;

                long want = targetScaled - lab.matrixServed[i];

                if (want <= 0) continue;

                Add(Shortfall, matrixIds[i], want);
                any = true;
            }

            return any;
        }

        // ── 第二趟：从物流站的 Supply 格位取货 ────────────────

        private static bool TakeFromStations(PlanetTransport transport)
        {
            var any = false;

            // **起点每 tick 轮转**，理由和虚拟物流那边一字不差（见
            // MegaVirtualLogisticsPatches.Rotation）：这一趟是「有多少拿多少、
            // 缺口扣光就跳过后面所有站」，固定从 1 开始等于每 tick 薅同一个站，
            // 别的站永远轮不到。总量对、不报错，只是分布错。
            //
            // 这一处是**数出来的，不是顺手改的**：巨型建筑那边报出症状之后，
            // 把「收集缺口 → 从站点取货 → 分发」这一族的六趟全看了一遍，
            // 研究站这条是同一个形状的另外两处。
            int start = MegaVirtualLogisticsPatches.Rotation(transport);

            for (var k = 0; k < transport.stationCursor - 1; k++)
            {
                int i = 1 + (start + k) % (transport.stationCursor - 1);

                StationComponent station = transport.stationPool[i];

                if (station == null || station.id != i || station.storage == null) continue;

                lock (station.storage)
                {
                    for (var s = 0; s < station.storage.Length; s++)
                    {
                        int itemId = station.storage[s].itemId;

                        if (itemId <= 0 || station.storage[s].count <= 0) continue;

                        // 只拿标了「供应」的货：Demand 是这个站自己要的，别抢
                        if (station.storage[s].localLogic != ELogisticStorage.Supply) continue;
                        if (!Shortfall.TryGetValue(itemId, out long need) || need <= 0) continue;

                        // 研究模式的缺口是放大值，换算回件数再取
                        long wantItems = IsScaled(itemId) ? (need + MatrixScale - 1) / MatrixScale : need;
                        long take = station.storage[s].count < wantItems ? station.storage[s].count : wantItems;

                        if (take <= 0) continue;

                        // 增产点数按比例一起扣掉。只扣 count 不扣 inc 的话，剩下的货会顶着
                        // 原来那一整份点数，相当于凭空多出增产——研究站这边不吃点数（见 GiveAssemble）。
                        int incTake = (int)((long)station.storage[s].inc * take / station.storage[s].count);

                        // 品质同理，而且必须在 count 扣减**之前**算比例。研究站没有品质槽位，
                        // 所以取走的这一份就地丢掉——丢是有界的损失，不扣才是凭空增长：
                        // 剩下的货会顶着整格的点数，单件分数当场跳上去。
                        QualityAccess.TakeStationQua(ref station.storage[s], (int)take);

                        station.storage[s].count -= (int)take;
                        station.storage[s].inc -= incTake;

                        long got = IsScaled(itemId) ? take * MatrixScale : take;

                        Shortfall[itemId] = need - got;
                        Add(Pool, itemId, got);

                        any = true;
                    }
                }
            }

            return any;
        }

        // ── 第三趟：把取到的货分给研究站 ──────────────────────

        private static void Distribute(FactorySystem system)
        {
            for (var i = 1; i < system.labCursor; i++)
            {
                if (system.labPool[i].id != i) continue;

                if (system.labPool[i].researchMode) GiveResearch(ref system.labPool[i]);
                else GiveAssemble(ref system.labPool[i]);
            }

            if (Interlocked.Exchange(ref _loggedIn, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo("矩阵研究站已开始从行星内物流站自动取料");
        }

        private static void GiveAssemble(ref LabComponent lab)
        {
            RecipeExecuteData recipe = lab.recipeExecuteData;

            if (recipe?.requires == null || lab.served == null) return;

            int[] requires = recipe.requires;
            int[] requireCounts = recipe.requireCounts;
            long batches = Config.supplyAssembleBatches > 0 ? Config.supplyAssembleBatches : 2000;

            int count = requires.Length < lab.served.Length ? requires.Length : lab.served.Length;

            for (var i = 0; i < count; i++)
            {
                if (requires[i] <= 0) continue;
                if (!Pool.TryGetValue(requires[i], out long available) || available <= 0) continue;

                long want = (i < requireCounts.Length ? requireCounts[i] : 1) * batches - lab.served[i];

                if (want <= 0) continue;

                long give = available < want ? available : want;

                lab.served[i] += (int)give;
                Pool[requires[i]] = available - give;

                // 从物流站拿的货不带增产点数，别让 incServed 跟着虚高
                if (lab.incServed != null && i < lab.incServed.Length && lab.served[i] <= 0) lab.incServed[i] = 0;
            }
        }

        private static void GiveResearch(ref LabComponent lab)
        {
            int[] matrixIds = LabComponent.matrixIds;
            int[] matrixPoints = LabComponent.matrixPoints;

            if (matrixIds == null || matrixPoints == null || lab.matrixServed == null) return;

            long targetScaled = (Config.supplyMatrixItems > 0 ? Config.supplyMatrixItems : 1000L) * MatrixScale;

            int count = matrixIds.Length < lab.matrixServed.Length ? matrixIds.Length : lab.matrixServed.Length;

            if (count > matrixPoints.Length) count = matrixPoints.Length;

            for (var i = 0; i < count; i++)
            {
                if (matrixPoints[i] <= 0 || matrixIds[i] <= 0) continue;
                if (!Pool.TryGetValue(matrixIds[i], out long available) || available <= 0) continue;

                long want = targetScaled - lab.matrixServed[i];

                if (want <= 0) continue;

                long give = available < want ? available : want;

                lab.matrixServed[i] += (int)give;
                Pool[matrixIds[i]] = available - give;
            }
        }

        // ── 出货第一趟：汇总各研究站的产物 ────────────────────

        /// <summary>
        /// 只有生产模式有产物：研究模式把矩阵烧成 hash，produced[] 是空的。
        /// 先只统计不扣账——扣多少要等物流站真的收下才知道（和巨型建筑出库同一个套路）。
        /// </summary>
        private static bool CollectOutput(FactorySystem system)
        {
            long reserve = Config.outputReserveItems > 0 ? Config.outputReserveItems : 0;

            var any = false;

            for (var i = 1; i < system.labCursor; i++)
            {
                if (system.labPool[i].id != i) continue;
                if (system.labPool[i].researchMode) continue;

                RecipeExecuteData recipe = system.labPool[i].recipeExecuteData;
                int[] produced = system.labPool[i].produced;

                if (recipe?.products == null || produced == null) continue;

                int[] products = recipe.products;
                int count = products.Length < produced.Length ? products.Length : produced.Length;

                for (var p = 0; p < count; p++)
                {
                    if (products[p] <= 0) continue;

                    long give = produced[p] - reserve;

                    if (give <= 0) continue;

                    Add(Output, products[p], give);
                    any = true;
                }
            }

            return any;
        }

        // ── 出货第二趟：塞进物流站的储物格 ────────────────────

        /// <summary>
        /// <b>只往标了「本地需求」的格位送。</b>
        ///
        /// 一开始这里是不看 localLogic 的，理由是「相当于一条传送带把货送进了这个格子，
        /// 原版传送带往站里塞货也只认物品 ID」——这个理由站不住：传送带得<b>真的铺过去</b>，
        /// 虚拟出货没有这层约束，等于全行星瞬间传送。实测的表现是随手放一个站、
        /// 开一格电磁矩阵，货立刻凭空到位并且马上对外供应。
        ///
        /// 「本地需求」正是玩家明说「请把这个东西送到这里」的那个标记，语义对得上，
        /// 也和取货侧只认 Supply 格严格对称。要把矩阵外运就按原版的老套路配：
        /// 本地需求 + 星际供应。没有任何一个 Demand 格的话产物就堆在机内，
        /// 和原版不接分拣器是一个道理。
        /// </summary>
        private static bool PushToStations(PlanetTransport transport)
        {
            var any = false;

            // 同上：这一趟是「有多少给多少」，固定起点会让下标最小的那个站独吞全部出货
            int start = MegaVirtualLogisticsPatches.Rotation(transport);

            for (var k = 0; k < transport.stationCursor - 1; k++)
            {
                int i = 1 + (start + k) % (transport.stationCursor - 1);

                StationComponent station = transport.stationPool[i];

                if (station == null || station.id != i || station.storage == null) continue;

                lock (station.storage)
                {
                    for (var s = 0; s < station.storage.Length; s++)
                    {
                        if (station.storage[s].localLogic != ELogisticStorage.Demand) continue;

                        int itemId = station.storage[s].itemId;

                        if (itemId <= 0) continue;
                        if (!Output.TryGetValue(itemId, out long available) || available <= 0) continue;

                        long room = station.storage[s].max - station.storage[s].count;

                        if (room <= 0) continue;

                        long give = available < room ? available : room;

                        station.storage[s].count += (int)give;

                        Output[itemId] = available - give;
                        Add(Taken, itemId, give);

                        any = true;
                    }
                }
            }

            return any;
        }

        // ── 出货第三趟：把收下的量从研究站扣掉 ────────────────

        private static void DeductOutput(FactorySystem system)
        {
            long reserve = Config.outputReserveItems > 0 ? Config.outputReserveItems : 0;

            for (var i = 1; i < system.labCursor; i++)
            {
                if (system.labPool[i].id != i) continue;
                if (system.labPool[i].researchMode) continue;

                RecipeExecuteData recipe = system.labPool[i].recipeExecuteData;
                int[] produced = system.labPool[i].produced;

                if (recipe?.products == null || produced == null) continue;

                int[] products = recipe.products;
                int count = products.Length < produced.Length ? products.Length : produced.Length;

                for (var p = 0; p < count; p++)
                {
                    if (products[p] <= 0) continue;
                    if (!Taken.TryGetValue(products[p], out long owed) || owed <= 0) continue;

                    long have = produced[p] - reserve;

                    if (have <= 0) continue;

                    long pay = have < owed ? have : owed;

                    produced[p] -= (int)pay;
                    Taken[products[p]] = owed - pay;
                }
            }

            if (Interlocked.Exchange(ref _loggedOut, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo("矩阵研究站已开始把产物自动送进行星内物流站");
        }

        // ── 小工具 ────────────────────────────────────────────

        /// <summary>研究模式的六种矩阵在 matrixServed 里是放大值，别的物品是明文。</summary>
        private static bool IsScaled(int itemId)
        {
            int[] matrixIds = LabComponent.matrixIds;

            if (matrixIds == null) return false;

            foreach (int id in matrixIds)
                if (id == itemId)
                    return true;

            return false;
        }

        private static void Add(Dictionary<int, long> map, int key, long value)
        {
            map[key] = map.TryGetValue(key, out long old) ? old + value : value;
        }
    }
}
