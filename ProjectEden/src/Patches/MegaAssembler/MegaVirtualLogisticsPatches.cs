using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 巨型建筑的「虚拟行星物流」：直接在储物格之间搬货，不让无人机真的飞。
    ///
    /// <b>目的是省渲染</b>。巨型建筑满速时每秒吃 3600 份配方，物流站为了跟上会一直派无人机，
    /// 几座建筑就是成百上千架同时在天上飞。虚拟搬运之后供需在同一 tick 内就平掉了，
    /// 站点不再需要派车——无人机停在站里不参与渲染，运力也不再是瓶颈。
    ///
    /// <b>必须是双向的</b>：只做入库的话，别的物流站仍然会派自己的无人机<b>来取</b>
    /// 巨型建筑的产物，天上照样有车。所以进出两条都做。
    ///
    /// 格位的方向不用猜——MegaStationPatches.SyncStorageLayout 已经把
    /// requires 排成 Demand、products 排成 Supply。
    ///
    /// <b>六趟扫描，不是「每个巨型站扫一遍别的站」</b>：后者是 mega × stations × 格位²，
    /// 30 格的站一多就爆了。这里每个方向都是「汇总 → 走一遍站点 → 回填」三趟，
    /// 整体 O(stations × 格位)，容器全部复用。
    ///
    /// <b>增产点数必须跟着物品一起搬。</b> <c>StationStore.inc</c> 存的是<b>整格</b>的点数总和，
    /// 所以只扣 <c>count</c> 不扣 <c>inc</c> 的话，源头剩下的货就顶着原来整格的点数
    /// ——每搬一次就凭空多出一批增产剂；而收货那边拿到的是没喷过的货。
    /// 四个改动 <c>count</c> 的地方现在都配了对应的 <c>inc</c>，比例一律按
    /// <c>搬走的量 / 搬之前的总量</c> 算（见 <see cref="SplitInc"/>）。
    ///
    /// 出库方向的第三趟按<b>欠账</b>而不是按「这一格自己的喷涂率」扣，
    /// 这样「对方收下的点数」和「源头扣掉的点数」严格相等，账不会随时间漂。
    ///
    /// 注意 <c>StationStore.inc</c> 是 Int32，和传送带上那个 byte 的
    /// <c>Cargo.inc</c> 不是一个量级，这里不用担心溢出。
    /// </summary>
    [HarmonyPatch]
    internal static class MegaVirtualLogisticsPatches
    {
        private static MegaBuildingsConfig Config => MegaBuildingRegistry.Config;

        // PlanetTransport.GameTick 会在 GameLogic.FactoryTransportGameTick_Parallel 里
        // <b>并行处理多个行星</b>（实测 31 个工作线程）。行星数据本身各管各的，但静态字典是
        // 所有线程共享的——多个线程同时往同一个 Dictionary 写会直接把它写坏，报
        // 「Operations that change non-concurrent collections must have exclusive access」。
        // 所以这些临时容器必须 [ThreadStatic]：每个工作线程一份，互不干扰，也不用加锁。
        // 首次使用时惰性创建（[ThreadStatic] 的字段初始化器只在第一个线程上跑）。

        [ThreadStatic] private static Dictionary<int, long> _need, _pool, _avail, _taken;

        // 上面四个记「多少个物品」，下面三个记与之对应的「多少增产点数」。
        // 必须成对搬运：只搬数量不搬点数的话，源头剩下的货会顶着整堆的点数
        // （等于凭空造增产剂），而收货方拿到的是没喷过的货。
        [ThreadStatic] private static Dictionary<int, long> _poolInc, _availInc, _takenInc;
        [ThreadStatic] private static Dictionary<int, long> _poolQua, _availQua, _takenQua;

        /// <summary>入库：物品 → 巨型建筑合计还缺多少。</summary>
        private static Dictionary<int, long> Need => _need ?? (_need = new Dictionary<int, long>());

        /// <summary>入库：物品 → 这一轮从别的站取到多少。</summary>
        private static Dictionary<int, long> Pool => _pool ?? (_pool = new Dictionary<int, long>());

        /// <summary>出库：物品 → 巨型建筑合计可以出多少。</summary>
        private static Dictionary<int, long> Avail => _avail ?? (_avail = new Dictionary<int, long>());

        /// <summary>出库：物品 → 别的站实际收下了多少。</summary>
        private static Dictionary<int, long> Taken => _taken ?? (_taken = new Dictionary<int, long>());

        /// <summary>入库：物品 → 这一轮取到的货带着多少增产点数。</summary>
        private static Dictionary<int, long> PoolInc => _poolInc ?? (_poolInc = new Dictionary<int, long>());

        /// <summary>出库：物品 → 巨型建筑可出的货带着多少增产点数。</summary>
        private static Dictionary<int, long> AvailInc => _availInc ?? (_availInc = new Dictionary<int, long>());

        /// <summary>出库：物品 → 别的站收下的货带走了多少增产点数。</summary>
        private static Dictionary<int, long> TakenInc => _takenInc ?? (_takenInc = new Dictionary<int, long>());

        // 品质和增产点数是**同一种量**：都存在整格上、都按件数可加可分。
        // 所以它走的是一模一样的三只池子，只是换一个孪生字段。
        //
        // **不跟着搬的话，提纯这条线整个是白做的**：提纯厂把品质注进自己的 Supply 格，
        // 而这个虚拟物流正是那批货离开建筑的主要途径——只搬件数不搬品质，
        // 下游收到的是一堆 0 分的金属，源头那一格反倒因为件数变少而单件分数虚高。
        // 原版的运输机路径不用管，1c 的孪生改写已经把 StationStore.qua 一起搬了；
        // 要补的只有本仓库自己手写的这几条搬运。

        /// <summary>入库：物品 → 这一轮取到的货带着多少品质点数。</summary>
        private static Dictionary<int, long> PoolQua => _poolQua ?? (_poolQua = new Dictionary<int, long>());

        /// <summary>出库：物品 → 巨型建筑可出的货带着多少品质点数。</summary>
        private static Dictionary<int, long> AvailQua => _availQua ?? (_availQua = new Dictionary<int, long>());

        /// <summary>出库：物品 → 别的站收下的货带走了多少品质点数。</summary>
        private static Dictionary<int, long> TakenQua => _takenQua ?? (_takenQua = new Dictionary<int, long>());

        /// <summary>孪生字段不在（没装 preloader）时整套品质搬运静默跳过，不影响增产点数那一半。</summary>
        private static bool Qua => QualityAccess.Ready;

        /// <summary>
        /// 从池子里按比例切走 <paramref name="moved"/> 个物品对应的增产点数。
        ///
        /// <c>StationStore.inc</c> 存的是<b>整格</b>的点数总和，所以搬走一部分货
        /// 就得按 <c>moved / total</c> 的比例把点数一起搬走。
        /// </summary>
        private static long SplitInc(Dictionary<int, long> incPool, int itemId, long moved, long total)
        {
            if (total <= 0 || moved <= 0) return 0;

            if (!incPool.TryGetValue(itemId, out long have) || have <= 0) return 0;

            long share = have * moved / total;

            if (share > have) share = have;

            incPool[itemId] = have - share;

            return share;
        }

        private static int _logged;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void PlanetTransport_GameTick(PlanetTransport __instance, long time)
        {
            if (Config == null || !Config.virtualLogistics) return;

            int interval = Config.virtualIntervalTicks > 0 ? Config.virtualIntervalTicks : 10;

            if (time % interval != 0) return;

            PlanetFactory factory = __instance.factory;

            if (factory?.entityPool == null || __instance.stationPool == null) return;
            if (factory.factorySystem?.assemblerPool == null) return;

            Inbound(__instance, factory);
            Outbound(__instance, factory);
        }

        // ── 入库：别的站的 Supply → 巨型建筑的 Demand ─────────

        private static void Inbound(PlanetTransport transport, PlanetFactory factory)
        {
            long target = Config.virtualStockPerSlot > 0 ? Config.virtualStockPerSlot : 100000;

            Need.Clear();

            var any = false;

            // 一趟：汇总缺口
            for (var i = 1; i < transport.stationCursor; i++)
            {
                StationComponent station = transport.stationPool[i];

                if (!IsMegaStation(station, i, factory)) continue;

                lock (station.storage)
                {
                    for (var s = 0; s < station.storage.Length; s++)
                    {
                        if (station.storage[s].localLogic != ELogisticStorage.Demand) continue;

                        int itemId = station.storage[s].itemId;

                        if (itemId <= 0) continue;

                        long cap = station.storage[s].max < target ? station.storage[s].max : target;
                        long want = cap - station.storage[s].count;

                        if (want <= 0) continue;

                        Add(Need, itemId, want);
                        any = true;
                    }
                }
            }

            if (!any) return;

            Pool.Clear();
            PoolInc.Clear();
            PoolQua.Clear();

            var got = false;

            // 二趟：走一遍所有站点的 Supply 格取货
            for (var i = 1; i < transport.stationCursor; i++)
            {
                StationComponent station = transport.stationPool[i];

                if (station == null || station.id != i || station.storage == null) continue;

                lock (station.storage)
                {
                    for (var s = 0; s < station.storage.Length; s++)
                    {
                        if (station.storage[s].localLogic != ELogisticStorage.Supply) continue;

                        int itemId = station.storage[s].itemId;
                        int count = station.storage[s].count;

                        if (itemId <= 0 || count <= 0) continue;
                        if (!Need.TryGetValue(itemId, out long need) || need <= 0) continue;
                        if (DemandsItself(station, itemId)) continue;

                        long take = count < need ? count : need;

                        // 比例要按**扣减之前**的 count 算，所以这一行必须在 count -= 之前
                        long incTake = (long)station.storage[s].inc * take / count;

                        if (incTake > station.storage[s].inc) incTake = station.storage[s].inc;

                        long quaTake = 0;

                        if (Qua)
                        {
                            long have = QualityAccess.GetStationQua(ref station.storage[s]);

                            quaTake = have * take / count;

                            if (quaTake > have) quaTake = have;

                            QualityAccess.SetStationQua(ref station.storage[s], (int)(have - quaTake));
                        }

                        station.storage[s].count -= (int)take;
                        station.storage[s].inc -= (int)incTake;

                        Need[itemId] = need - take;
                        Add(Pool, itemId, take);
                        Add(PoolInc, itemId, incTake);
                        Add(PoolQua, itemId, quaTake);

                        got = true;
                    }
                }
            }

            if (!got) return;

            // 三趟：回填到巨型建筑的 Demand 格
            for (var i = 1; i < transport.stationCursor; i++)
            {
                StationComponent station = transport.stationPool[i];

                if (!IsMegaStation(station, i, factory)) continue;

                lock (station.storage)
                {
                    for (var s = 0; s < station.storage.Length; s++)
                    {
                        if (station.storage[s].localLogic != ELogisticStorage.Demand) continue;

                        int itemId = station.storage[s].itemId;

                        if (itemId <= 0) continue;
                        if (!Pool.TryGetValue(itemId, out long available) || available <= 0) continue;

                        long cap = station.storage[s].max < target ? station.storage[s].max : target;
                        long want = cap - station.storage[s].count;

                        if (want <= 0) continue;

                        long give = available < want ? available : want;
                        long incGive = SplitInc(PoolInc, itemId, give, available);
                        long quaGive = SplitInc(PoolQua, itemId, give, available);

                        station.storage[s].count += (int)give;
                        station.storage[s].inc += (int)incGive;

                        if (Qua && quaGive > 0)
                            QualityAccess.SetStationQua(ref station.storage[s],
                                (int)(QualityAccess.GetStationQua(ref station.storage[s]) + quaGive));

                        Pool[itemId] = available - give;
                    }
                }
            }

            ReportOnce();
        }

        // ── 出库：巨型建筑的 Supply → 别的站的 Demand ─────────

        private static void Outbound(PlanetTransport transport, PlanetFactory factory)
        {
            Avail.Clear();
            AvailInc.Clear();
            AvailQua.Clear();

            var any = false;

            // 一趟：汇总可出货量（先不扣，等对方真收下再扣）
            for (var i = 1; i < transport.stationCursor; i++)
            {
                StationComponent station = transport.stationPool[i];

                if (!IsMegaStation(station, i, factory)) continue;

                lock (station.storage)
                {
                    for (var s = 0; s < station.storage.Length; s++)
                    {
                        if (station.storage[s].localLogic != ELogisticStorage.Supply) continue;

                        int itemId = station.storage[s].itemId;
                        int count = station.storage[s].count;

                        if (itemId <= 0 || count <= 0) continue;

                        Add(Avail, itemId, count);
                        Add(AvailInc, itemId, station.storage[s].inc);

                        if (Qua) Add(AvailQua, itemId, QualityAccess.GetStationQua(ref station.storage[s]));

                        any = true;
                    }
                }
            }

            if (!any) return;

            Taken.Clear();
            TakenInc.Clear();
            TakenQua.Clear();

            var moved = false;

            // 二趟：塞进别的站空着的 Demand 格
            for (var i = 1; i < transport.stationCursor; i++)
            {
                StationComponent station = transport.stationPool[i];

                if (station == null || station.id != i || station.storage == null) continue;
                if (IsMegaStation(station, i, factory)) continue;

                lock (station.storage)
                {
                    for (var s = 0; s < station.storage.Length; s++)
                    {
                        if (station.storage[s].localLogic != ELogisticStorage.Demand) continue;

                        int itemId = station.storage[s].itemId;

                        if (itemId <= 0) continue;
                        if (!Avail.TryGetValue(itemId, out long available) || available <= 0) continue;

                        long room = station.storage[s].max - station.storage[s].count;

                        if (room <= 0) continue;

                        long give = available < room ? available : room;
                        long incGive = SplitInc(AvailInc, itemId, give, available);
                        long quaGive = SplitInc(AvailQua, itemId, give, available);

                        station.storage[s].count += (int)give;
                        station.storage[s].inc += (int)incGive;

                        if (Qua && quaGive > 0)
                            QualityAccess.SetStationQua(ref station.storage[s],
                                (int)(QualityAccess.GetStationQua(ref station.storage[s]) + quaGive));

                        Avail[itemId] = available - give;
                        Add(Taken, itemId, give);
                        Add(TakenInc, itemId, incGive);
                        Add(TakenQua, itemId, quaGive);

                        moved = true;
                    }
                }
            }

            if (!moved) return;

            // 三趟：把对方收下的量从巨型建筑的 Supply 格扣掉
            for (var i = 1; i < transport.stationCursor; i++)
            {
                StationComponent station = transport.stationPool[i];

                if (!IsMegaStation(station, i, factory)) continue;

                lock (station.storage)
                {
                    for (var s = 0; s < station.storage.Length; s++)
                    {
                        if (station.storage[s].localLogic != ELogisticStorage.Supply) continue;

                        int itemId = station.storage[s].itemId;
                        int count = station.storage[s].count;

                        if (itemId <= 0 || count <= 0) continue;
                        if (!Taken.TryGetValue(itemId, out long owed) || owed <= 0) continue;

                        long pay = count < owed ? count : owed;

                        // 按**欠账**的比例扣，而不是按这一格自己的喷涂率——
                        // 这样「对方收下的点数」和「源头扣掉的点数」严格相等，账不会漂
                        long incPay = SplitInc(TakenInc, itemId, pay, owed);
                        long quaPay = SplitInc(TakenQua, itemId, pay, owed);

                        if (incPay > station.storage[s].inc) incPay = station.storage[s].inc;

                        if (Qua && quaPay > 0)
                        {
                            long have = QualityAccess.GetStationQua(ref station.storage[s]);

                            if (quaPay > have) quaPay = have;

                            QualityAccess.SetStationQua(ref station.storage[s], (int)(have - quaPay));
                        }

                        station.storage[s].count -= (int)pay;
                        station.storage[s].inc -= (int)incPay;

                        Taken[itemId] = owed - pay;
                    }
                }
            }

            ReportOnce();
        }

        // ── 小工具 ────────────────────────────────────────────

        /// <summary>
        /// 这个站点自己是不是也在要这种货。是的话就别从它的供应格上取——
        /// <b>站点不和自己做买卖</b>。
        ///
        /// 这是<b>原版自己的规矩</b>，不是这里发明的：<c>StationComponent.RematchLocalPairs</c>
        /// 的内层循环从 <c>this.id + 1</c> 开始（IL 0039~0041），所以本地配对里永远不会
        /// 出现同一个站点的供需两格。这套虚拟物流没有那条边界，因为在此之前
        /// <b>没有任何配方会让同一种物品同时站在供应和需求两边</b>。
        ///
        /// 同位提纯厂是第一个：它的进料是粗金属、出料是精金属，<b>物品相同、只差品质</b>。
        /// 不挡的话，入库那一趟会把它刚提纯好的货从供应格搬回自己的需求格，
        /// 于是这台厂永远在提纯自己的产物，每转一圈白亏一道收率，而且一件都出不了厂——
        /// 每一步都成功、功能却不存在，本仓库最难查的那种症状。
        ///
        /// 只在这种货确实有人要（<c>Need</c> 命中）之后才查，所以正常配方一次都不会走到。
        /// </summary>
        private static bool DemandsItself(StationComponent station, int itemId)
        {
            for (var i = 0; i < station.storage.Length; i++)
                if (station.storage[i].itemId == itemId
                    && station.storage[i].localLogic == ELogisticStorage.Demand)
                    return true;

            return false;
        }

        /// <summary>这个站点是不是挂在巨型建筑上的。</summary>
        private static bool IsMegaStation(StationComponent station, int index, PlanetFactory factory)
        {
            if (station == null || station.id != index || station.storage == null) return false;

            int entityId = station.entityId;

            if (entityId <= 0 || entityId >= factory.entityPool.Length) return false;

            int assemblerId = factory.entityPool[entityId].assemblerId;

            if (assemblerId <= 0) return false;

            AssemblerComponent[] pool = factory.factorySystem.assemblerPool;

            return assemblerId < pool.Length
                && pool[assemblerId].id == assemblerId
                && pool[assemblerId].speed >= MegaBuildingRegistry.MegaSpeedThreshold;
        }

        private static void ReportOnce()
        {
            if (Interlocked.Exchange(ref _logged, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo("巨型建筑已改为虚拟物流：储物格之间直接搬运，无人机不再起飞");
        }

        private static void Add(Dictionary<int, long> map, int key, long value)
        {
            map[key] = map.TryGetValue(key, out long old) ? old + value : value;
        }
    }
}
