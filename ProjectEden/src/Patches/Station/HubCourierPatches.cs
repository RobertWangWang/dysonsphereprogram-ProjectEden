using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让综合物流枢纽的第三种无人机（配送运输机）工作，<b>货源是物流站那 30 个槽位</b>，
    /// 而不是原版那样去连一个旁边的储物箱。
    ///
    /// <b>为什么不直接改 PickFromStoragePrecalc / InsertIntoStoragePrecalc。</b>
    /// 那两个方法看起来是收口点，其实<b>只负责「能取/能放多少」并记下书签</b>
    /// （pickStorageSearchStart + pickGridSearchStart），真正的搬货散落在
    /// DispenserComponent.InternalTick 里好几处，全都按那对书签去 StorageComponent 上取放。
    /// 只换掉 Precalc 的话，配送器会以为自己拿得到货、到了搬运那一步却搬不出来。
    /// 而 InternalTick 有 8.9 KB IL，逐处接管风险太高，还是每 tick 都跑的热路径。
    ///
    /// <b>改成搬数据，不改配送逻辑。</b> 给枢纽自带一个<b>不露面的缓冲仓</b>
    /// （prefabDesc.isStorage，storageCol × storageRow 格），把它接给配送器当货源，
    /// 再每 tick 让它和物流站的 30 个槽位对齐：
    ///
    ///   · 站内槽位有货 → 补进缓冲仓，配送运输机就能拿去补给机甲
    ///   · 机甲回收回来的货落进缓冲仓 → 收回站内槽位，进物流网
    ///
    /// 配送逻辑、路径、动画、能耗全是原版的，一行都没碰；玩家看到的效果就是
    /// 「配送运输机在拿这台建筑 30 个槽位里的东西」。缓冲仓是实现细节，界面上不出现。
    ///
    /// <b>原版不会自动连同一个实体上的储物仓。</b> CreateEntityLogicComponents 里
    /// 配送器那一段是 <c>ReadObjectConn</c> 找<b>相邻</b>实体的 storageId 再
    /// <c>ConnectToDispenser</c>，同实体的储物仓它看不见——所以要自己补一次连接。
    /// </summary>
    [HarmonyPatch]
    internal static class HubCourierPatches
    {
        /// <summary>缓冲仓里每个物品保留多少个。够配送运输机装一趟就行，不必囤。</summary>
        private const int BufferPerItem = 1000;

        /// <summary>多久对齐一次缓冲仓。配送本来就不是高频操作，不必每 tick 都扫。</summary>
        private const int IntervalTicks = 10;

        /// <summary>多久把配送器的 filter 轮到下一种货。60 tick = 1 秒。</summary>
        private const int RotateTicks = 60;

        // ── 一、把配送器接到自己那个缓冲仓上 ──────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.CreateEntityLogicComponents))]
        private static void PlanetFactory_CreateEntityLogicComponents(PlanetFactory __instance, int entityId)
        {
            if (__instance?.entityPool == null || entityId <= 0 || entityId >= __instance.entityPool.Length) return;

            ref EntityData entity = ref __instance.entityPool[entityId];

            // 只管「同时是物流站 + 配送器 + 储物仓」的实体，也就是本 mod 的枢纽
            if (entity.stationId <= 0 || entity.dispenserId <= 0 || entity.storageId <= 0) return;

            PlanetTransport transport = __instance.transport;

            if (transport == null) return;

            transport.ConnectToDispenser(entity.dispenserId, entity.storageId);

            // DispenserComponent.Init 不给 playerMode 赋值，默认是 0（关闭），
            // 配送运输机会一动不动。枢纽又没有配送器面板可调，只能在这里按配置设好。
            DispenserComponent dispenser = transport.dispenserPool?[entity.dispenserId];

            if (dispenser != null)
            {
                MachineStationEntry cfg = HubConfig(__instance, entityId);

                dispenser.playerMode = (EPlayerDeliveryMode)(cfg?.playerDeliveryMode ?? 2);
                dispenser.storageMode = (EStorageDeliveryMode)(cfg?.storageDeliveryMode ?? 0);

                // Init 里这个是 false（运输机/运输船默认 true，所以它们会自己补满，
                // 配送运输机不会）——不打开的话枢纽永远 0 架配送运输机，一动不动
                dispenser.courierAutoReplenish = true;

                dispenser.UpdateKeepMode();
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"综合物流枢纽（实体 {entityId}）的配送运输机已接到自带缓冲仓（storage {entity.storageId}），" +
                $"对机甲的配送模式 {dispenser?.playerMode}");
        }

        /// <summary>这个实体是本 mod 的哪台枢纽？找不到就返回 null，调用方用默认值。</summary>
        private static MachineStationEntry HubConfig(PlanetFactory factory, int entityId)
        {
            int protoId = factory.entityPool[entityId].protoId;

            foreach (MachineRegistry.Machine machine in MachineRegistry.Machines)
                if (machine.ItemId == protoId)
                    return machine.Entry.station;

            return null;
        }

        // ── 二、缓冲仓 ←→ 物流站 30 个槽位 ────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void PlanetTransport_GameTick(PlanetTransport __instance, long time)
        {
            if (time % IntervalTicks != 0) return;

            PlanetFactory factory = __instance.factory;

            if (factory?.entityPool == null || __instance.stationPool == null) return;

            for (var i = 1; i < __instance.stationCursor; i++)
            {
                StationComponent station = __instance.stationPool[i];

                if (station == null || station.id != i || station.storage == null) continue;

                int entityId = station.entityId;

                if (entityId <= 0 || entityId >= factory.entityPool.Length) continue;

                ref EntityData entity = ref factory.entityPool[entityId];

                if (entity.dispenserId <= 0 || entity.storageId <= 0) continue;

                StorageComponent[] pool = factory.factoryStorage?.storagePool;

                if (pool == null || entity.storageId >= pool.Length) continue;

                StorageComponent buffer = pool[entity.storageId];

                if (buffer == null) continue;

                Drain(station, buffer);
                TopUp(station, buffer);
                SyncDeliveryList(factory, station, entityId);
                RotateFilter(factory, station, entity.dispenserId, time);
                Report(factory, station, buffer, entity.dispenserId, time);
            }
        }

        /// <summary>
        /// 轮换配送器的 filter，让一台枢纽能服务多种货。
        ///
        /// <b>原版的配送器在设计上就是单物品的。</b> filter 不只是配对条件，它<b>就是</b>
        /// 这台配送器服务的那件货——DispenserComponent.InternalTick 里两处都靠它：
        /// <code>
        ///   if (grids[demandIndex].itemId != this.filter) continue;   // 跳过别的货
        ///   PickFromStoragePrecalc(this.filter, needCnt);             // 直接拿 filter 当物品 ID 取货
        /// </code>
        /// 所以「让配对对任意物品成立」是没用的——试过，配对确实成了（已配对 2 条），
        /// 但 InternalTick 照样按 filter=0 干活，一件也送不出去。
        ///
        /// 真要一台服务多种货，得把 8.9 KB 的 InternalTick 里每处 filter 都接管掉，风险太高。
        /// 改成<b>轮换</b>：每隔一段时间把 filter 换成枢纽里的下一种货，
        /// 交给原版自己去配对、取货、派机。同一时刻只服务一种货，但轮得够快就看不出来。
        /// </summary>
        private static void RotateFilter(PlanetFactory factory, StationComponent station, int dispenserId, long time)
        {
            if (time % RotateTicks != 0) return;
            if (GameMain.localPlanet == null || factory.planetId != GameMain.localPlanet.id) return;

            DispenserComponent dispenser = factory.transport.dispenserPool[dispenserId];

            if (dispenser == null) return;

            DeliveryPackage pkg = GameMain.mainPlayer?.deliveryPackage;

            if (pkg?.grids == null || !pkg.unlockedAndEnabled) return;

            // 候选：枢纽槽位里有、且在配送清单里的货。清单外的货送了也没人要
            int first = 0;
            int next = 0;
            int current = dispenser.filter;
            var passedCurrent = false;

            foreach (StationStore slot in station.storage)
            {
                int itemId = slot.itemId;

                if (itemId <= 0 || slot.count <= 0) continue;
                if (!Listed(pkg, itemId)) continue;

                if (first == 0) first = itemId;

                if (passedCurrent && next == 0) next = itemId;

                if (itemId == current) passedCurrent = true;
            }

            // 轮到末尾就回到第一个；当前这件已经不在候选里了也回到第一个
            int chosen = next != 0 ? next : first;

            if (chosen == 0 || chosen == current) return;

            // 走 SetDispenserFilter 而不是直接赋值：它会顺带 RefreshDispenserTraffic 重新配对
            factory.transport.SetDispenserFilter(dispenserId, chosen);
        }

        /// <summary>
        /// 每 10 秒报一次枢纽的实际状态。
        ///
        /// <b>为什么要常驻而不是一次性。</b> 这条链有四段——站内槽位 → 缓冲仓 →
        /// 配送需求清单 → 配送运输机——每一段都可能是空的，而<b>启动时的日志什么都说明不了</b>：
        /// 那时候玩家还没给槽位配货。前面几轮就是靠启动日志来回猜，白绕了好几圈。
        /// 一行把四段的实际数字都摆出来，抓一次日志就能定位是哪一段断的。
        /// </summary>
        private static void Report(PlanetFactory factory, StationComponent station, StorageComponent buffer,
            int dispenserId, long time)
        {
            if (time % 600 != 0) return;
            if (GameMain.localPlanet == null || factory.planetId != GameMain.localPlanet.id) return;

            // 平时关着：这行每 10 秒一条，排查时才打开（machines.json 的 courierDebugLog）
            if (HubConfig(factory, station.entityId)?.courierDebugLog != true) return;

            var slots = 0;
            var slotText = "";

            foreach (StationStore slot in station.storage)
            {
                if (slot.itemId <= 0 || slot.count <= 0) continue;

                slots++;

                if (slots <= 3)
                    slotText += $"{LDB.items.Select(slot.itemId)?.name ?? slot.itemId.ToString()}×{slot.count} ";
            }

            var bufferKinds = 0;
            var bufferText = "";

            for (var g = 0; g < buffer.size; g++)
            {
                int itemId = buffer.grids[g].itemId;

                if (itemId <= 0 || buffer.grids[g].count <= 0) continue;

                // 一种货摊在多个格子里，按种类报才有意义——之前报的是格子数，
                // 「10 种（铁矿 铁矿 铁矿）」看着像 bug，其实只是数错了口径
                if (SeenEarlier(buffer, g, itemId)) continue;

                bufferKinds++;

                if (bufferKinds <= 3)
                    bufferText += $"{LDB.items.Select(itemId)?.name ?? "?"}×{buffer.GetItemCount(itemId)} ";
            }

            DispenserComponent dispenser = factory.transport.dispenserPool[dispenserId];
            DeliveryPackage pkg = GameMain.mainPlayer?.deliveryPackage;

            var listed = 0;

            if (pkg?.grids != null)
                for (var g = 0; g < pkg.gridLength; g++)
                    if (pkg.grids[g].itemId > 0)
                        listed++;

            ProjectEdenPlugin.Log.LogInfo(
                $"综合物流枢纽状态：槽位有货 {slots} 种（{slotText.Trim()}）｜" +
                $"缓冲仓 {bufferKinds} 种（{bufferText.Trim()}）｜" +
                $"配送清单 {listed} 条｜" +
                $"配送运输机 闲 {dispenser?.idleCourierCount ?? -1} / 忙 {dispenser?.workCourierCount ?? -1}｜" +
                $"货源已接 {(dispenser?.pickStorageSearchStart != null ? "是" : "否")}｜" +
                $"已配对 {dispenser?.playerPairCount ?? -1} 条｜" +
                $"当前服务 {LDB.items.Select(dispenser?.filter ?? 0)?.name ?? "（无）"}｜" +
                $"星球配送开关 {factory.transport.playerDeliveryEnabled}");
        }

        /// <summary>
        /// 把枢纽里有的货自动补进伊卡洛斯的「配送需求清单」。
        ///
        /// <b>这是配送运输机会不会动的总闸。</b> DispenserComponent.InternalTick 遍历的是
        /// <c>Player.deliveryPackage.grids</c>：某格的持有量低于需求量就派机送货、
        /// 高于回收量就派机收回。<b>清单里没配的物品，它一眼都不会看</b>——
        /// 所以哪怕枢纽塞满了货、配送运输机停满了机库，清单是空的就全员待命。
        ///
        /// 清单是<b>玩家全局</b>的东西（不是这台建筑的），所以这里只往<b>空格</b>里填，
        /// 绝不碰玩家自己配好的条目；也只在玩家当前所在的星球上做，
        /// 免得给一颗够不着的星球上的货占掉清单格子。
        /// </summary>
        private static void SyncDeliveryList(PlanetFactory factory, StationComponent station, int entityId)
        {
            MachineStationEntry cfg = HubConfig(factory, entityId);

            if (cfg == null)
            {
                Explain(0, $"找不到实体 {entityId} 对应的枢纽配置（protoId {factory.entityPool[entityId].protoId}）");

                return;
            }

            if (!cfg.autoDeliveryList)
            {
                Explain(4, "machines.json 里 autoDeliveryList 是 false，本功能关着");

                return;
            }

            // 只管脚下这颗星球：配送运输机本来也飞不到别的星球。
            // 这一条同时也是线程安全的保证——PlanetTransport.GameTick 是各星球并行跑的，
            // 而 deliveryPackage 是玩家全局的一份，只让脚下这颗星球的那个线程写。
            if (GameMain.localPlanet == null || factory.planetId != GameMain.localPlanet.id) return;

            DeliveryPackage pkg = GameMain.mainPlayer?.deliveryPackage;

            if (pkg?.grids == null)
            {
                Explain(1, "取不到伊卡洛斯的配送需求清单（deliveryPackage 为空）");

                return;
            }

            if (!pkg.unlockedAndEnabled)
            {
                Explain(2, $"配送需求清单不可用：unlocked={pkg.unlocked}，enable={pkg.enable}。" +
                           "unlocked 是科技，enable 是机甲面板里那个开关——两个都要开，配送运输机才会动。");

                return;
            }

            StationStore[] slots = station.storage;

            if (slots == null) return;

            // 心跳：确认真的跑到了这里。没有这条的话，「日志一行都没有」既可能是
            // 被上面某个条件挡了，也可能是压根没调用到，分不清
            if (System.Threading.Interlocked.Exchange(ref _heartbeat, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"综合物流枢纽自动配送已开始工作：清单 {pkg.rowCount} 行 × {pkg.colCount} 列（{pkg.activeCount} 格可用）");

            int stacks = cfg.deliveryKeepStacks > 0 ? cfg.deliveryKeepStacks : 1;

            var filled = 0;
            var listed = 0;
            var added = 0;

            for (var s = 0; s < slots.Length; s++)
            {
                int itemId = slots[s].itemId;

                if (itemId <= 0 || slots[s].count <= 0) continue;

                filled++;

                if (Listed(pkg, itemId))
                {
                    listed++;

                    continue;
                }

                int grid = FreeGrid(pkg);

                if (grid < 0)
                {
                    Explain(3, $"配送需求清单没有空格了：{pkg.rowCount} 行 × {pkg.colCount} 列，" +
                               $"共 {pkg.activeCount} 格可用，已全部占满");

                    return; // 清单满了，不用再看别的槽位
                }

                // <b>必须走 Player.SetDeliveryItem，不能直接调 DeliveryPackage 上的同名方法。</b>
                // 玩家那条路除了写格子，还会 RefreshDispenserTraffic(-(index + 1)) 去<b>重新配对</b>
                // 配送器；只写格子的话游戏认为「无匹配的配送器」，运输机停着不动。
                GameMain.mainPlayer.SetDeliveryItem(grid, itemId);

                // <b>别用 stackSizeModified。</b> 它是 stackSize × stackSizeMultiplier，
                // 而 SetDeliveryItem 只填 stackSize、不碰 stackSizeMultiplier——
                // 新格子上那个乘数是 0，算出来的需求量就是 0，加进清单也照样不送货。
                int stackSize = pkg.grids[grid].stackSize;

                if (stackSize <= 0) stackSize = 100;

                // 需求量 = 回收量：机甲身上保持这么多，多出来的自动送回枢纽。
                // （原版 SetDeliveryItem 把 recycleCount 设成 int.MaxValue = 永不回收）
                int keep = stackSize * stacks;

                pkg.grids[grid].requireCount = keep;
                pkg.grids[grid].recycleCount = keep;

                added++;

                // 加成过就把「没东西可加」那条警告重新武装：
                // 槽位空只是一时的，之后有货了它得能再说话
                System.Threading.Interlocked.Exchange(ref Explained[5], 0);

                ProjectEdenPlugin.Log.LogInfo(
                    $"综合物流枢纽：已把「{LDB.items.Select(itemId)?.name ?? itemId.ToString()}」" +
                    $"加进伊卡洛斯的配送需求清单（第 {grid} 格，需求/回收 {keep}）");
            }

            // 一件都没加成时说清楚是为什么——不然又是「日志一行都没有」
            if (added == 0)
                Explain(5, $"这台枢纽的 {slots.Length} 个槽位里，有货的 {filled} 个、" +
                           $"已经在清单里的 {listed} 个，所以没有需要新加的。" +
                           (filled == 0 ? "槽位是空的：先在物流站面板上给槽位指定物品并让它进货。" : ""));
        }

        /// <summary>
        /// 每种「没干成的理由」各报一次。
        ///
        /// 这些 return 原本全是静默的，结果是「日志一行都没有」——分不清是没跑到、
        /// 还是跑到了但被某个条件挡住。<b>并行 tick 上的一次性日志要用 Interlocked 抢</b>，
        /// 否则每个星球的线程都会各打一遍（见 CLAUDE.md）。
        ///
        /// <b>一次性也会骗人。</b> 「槽位是空的」这条只在当时成立，之后货进来了功能会
        /// 静默地开始工作，而警告不再出现——看上去就像「只更新了一次就不管了」。
        /// 所以真的加成东西之后会把对应的标志重新武装。
        /// </summary>
        private static readonly int[] Explained = new int[6];

        private static int _heartbeat;

        private static void Explain(int reason, string message)
        {
            if (System.Threading.Interlocked.Exchange(ref Explained[reason], 1) != 0) return;

            ProjectEdenPlugin.Log.LogWarning($"综合物流枢纽自动配送未生效：{message}");
        }

        private static bool Listed(DeliveryPackage pkg, int itemId)
        {
            for (var g = 0; g < pkg.gridLength; g++)
                if (pkg.grids[g].itemId == itemId)
                    return true;

            return false;
        }

        private static int FreeGrid(DeliveryPackage pkg)
        {
            for (var g = 0; g < pkg.gridLength; g++)
                if (pkg.IsGridActive(g) && pkg.grids[g].itemId == 0)
                    return g;

            return -1;
        }

        /// <summary>
        /// 缓冲仓 → 站内槽位。机甲回收回来的东西落在缓冲仓里，收回物流网。
        ///
        /// 先收再补，顺序不能反：反过来的话刚补进去的货会被当成回收品原样收回来，
        /// 白白空转一轮。
        /// </summary>
        private static void Drain(StationComponent station, StorageComponent buffer)
        {
            StationStore[] slots = station.storage;

            if (slots == null) return;

            // <b>按物品的「总量」算超出量，不能拿单格的 count 去比。</b>
            // 缓冲仓里一种货是摊在多个格子里的（每格一个堆叠上限），
            // 逐格比 BufferPerItem 的话，格子数再多每格也到不了阈值，
            // 结果就是机甲回收回来的货永远躺在缓冲仓里、回不到物流网。
            for (var g = 0; g < buffer.size; g++)
            {
                int itemId = buffer.grids[g].itemId;

                if (itemId <= 0 || buffer.grids[g].count <= 0) continue;

                // 同一种货前面已经处理过就跳过——TakeItem 是按物品取的，一次就够
                if (SeenEarlier(buffer, g, itemId)) continue;

                int surplus = buffer.GetItemCount(itemId) - BufferPerItem;

                if (surplus <= 0) continue;

                int slot = FindSlot(slots, itemId);

                if (slot < 0) continue;

                int room = slots[slot].max - slots[slot].count;

                if (room <= 0) continue;

                int move = surplus < room ? surplus : room;

                // **调用前清零**：StorageComponent.TakeItem 既读侧信道也写侧信道
                // （实测读 3 写 2），不清的话它读到的是上一个人留下的值。
                if (QualityAccess.ChannelClearable) QualityAccess.ClearChannel();

                int taken = buffer.TakeItem(itemId, move, out int inc);

                // **调用后读一次**：取货类的方法是「被调方写、调用方读」，
                // 这一笔就是它从缓冲仓里带出来的品质。读完清掉，别留给下一个人。
                int qua = QualityAccess.ChannelReady ? QualityAccess.GetChannel0() : 0;

                if (QualityAccess.ChannelClearable) QualityAccess.ClearChannel();

                if (taken <= 0) continue;

                slots[slot].itemId = itemId;
                slots[slot].count += taken;
                slots[slot].inc += inc;

                QualityAccess.GiveStationQua(ref slots[slot], qua);
            }
        }

        /// <summary>
        /// 这一格里 <paramref name="move"/> 件货对应多少品质。<b>只算不扣</b>——
        /// 实际扣多少要等 AddItem 告诉我们它吃下了多少（和 <c>remainInc</c> 同构）。
        /// </summary>
        private static int QuaShareOf(StationStore store, int move)
        {
            if (!QualityAccess.Ready || move <= 0 || store.count <= 0) return 0;

            int qua = QualityAccess.GetStationQua(ref store);

            if (qua <= 0) return 0;

            long share = (long)qua * move / store.count;

            return share > qua ? qua : (int)share;
        }

        /// <summary>这一格的物品在前面的格子里出现过吗——用来做「每种货只处理一次」。</summary>
        private static bool SeenEarlier(StorageComponent buffer, int grid, int itemId)
        {
            for (var g = 0; g < grid; g++)
                if (buffer.grids[g].itemId == itemId)
                    return true;

            return false;
        }

        /// <summary>站内槽位 → 缓冲仓，把每种货补到保留量，配送运输机就有东西可拿。</summary>
        private static void TopUp(StationComponent station, StorageComponent buffer)
        {
            StationStore[] slots = station.storage;

            if (slots == null) return;

            for (var s = 0; s < slots.Length; s++)
            {
                int itemId = slots[s].itemId;

                if (itemId <= 0 || slots[s].count <= 0) continue;

                int have = buffer.GetItemCount(itemId);
                int want = BufferPerItem - have;

                if (want <= 0) continue;

                int move = slots[s].count < want ? slots[s].count : want;

                // 按比例带走增产点数——只扣数量不扣 inc 等于凭空增产
                int inc = slots[s].count > 0 ? (int)((long)slots[s].inc * move / slots[s].count) : 0;

                // **把这一笔的品质写进侧信道，再调 AddItem。**
                // preloader 把它改写成了「从 ProjectEdenQualityChannel 读品质」，
                // 协议是调用方在调用前写——而这条协议它只在游戏自己的调用点上接好了。
                // 不写的话它消费的是上一个人留下的值，品质会凭空长出来。
                //
                // 这里能把品质真的送过去（而不是像传送带那几条路那样丢掉），
                // 因为两头都是有品质槽位的容器。
                int qua = QuaShareOf(slots[s], move);

                if (QualityAccess.SetChannel0 != null) QualityAccess.SetChannel0(qua);

                int added = buffer.AddItem(itemId, move, inc, out int remainInc, false);

                // **没吃下的那部分还留在寄存器里**（部分入库时 AddItem 走的是按比例的 Split），
                // 所以真正被带走的是差额——和 remainInc 完全同构。读完清掉。
                int remainQua = QualityAccess.ChannelReady ? QualityAccess.GetChannel0() : 0;

                if (QualityAccess.ChannelClearable) QualityAccess.ClearChannel();

                if (added <= 0) continue;

                // remainInc 是没能塞进去的那部分增产点数，扣的只能是真正带走的
                int usedInc = inc - remainInc;

                slots[s].count -= added;
                slots[s].inc -= usedInc;

                int usedQua = qua - remainQua;

                if (usedQua > 0 && QualityAccess.Ready)
                    QualityAccess.SetStationQua(ref slots[s],
                        QualityAccess.GetStationQua(ref slots[s]) - usedQua);
            }
        }

        private static int FindSlot(StationStore[] slots, int itemId)
        {
            var empty = -1;

            for (var s = 0; s < slots.Length; s++)
            {
                if (slots[s].itemId == itemId) return s;

                if (empty < 0 && slots[s].itemId == 0) empty = s;
            }

            return empty;
        }

        // ── 三、别让缓冲仓抢走点击建筑时的面板 ────────────────

        /// <summary>供 IL 调用：枢纽实体返回 0，其余原样返回。</summary>
        internal static int FilterHubId(int id, PlanetFactory factory, int objId)
        {
            if (id <= 0 || factory?.entityPool == null) return id;
            if (objId <= 0 || objId >= factory.entityPool.Length) return id;

            EntityData entity = factory.entityPool[objId];

            // 枢纽 = 物流站 + 配送器 + 缓冲仓三件套；别误伤原版的储物仓和配送器
            return entity.stationId > 0 && entity.dispenserId > 0 && entity.storageId > 0 ? 0 : id;
        }

        /// <summary>
        /// 点击建筑时开哪个窗口，是 UIGame.OnPlayerInspecteeChange 里一串顺序判断，
        /// 每个分支都先 ShutAllFunctionWindow() 再开自己那个，<c>storageId</c> 排在最前面，
        /// 会被后面的物流站 / 配送器分支顶掉，中间还闪一下储物仓面板。
        ///
        /// 所以对枢纽实体把读出来的 storageId 过滤成 0。
        /// <b>是在读进局部变量的那一刻过滤，不是去改 entityPool</b>——那是真实实体数据，
        /// 改了会连带毁掉组件连接和存档。做法照抄 MegaStationWindowPatches。
        ///
        /// 代价是这台建筑点不开储物仓面板，但缓冲仓本来就是实现细节，不该露给玩家。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIGame), nameof(UIGame.OnPlayerInspecteeChange))]
        private static IEnumerable<CodeInstruction> UIGame_OnPlayerInspecteeChange_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo filter = AccessTools.Method(typeof(HubCourierPatches), nameof(FilterHubId));

            var matcher = new CodeMatcher(instructions);
            var patched = 0;

            // 缓冲仓排在最前、配送器排在物流站之后，两支都会把物流站窗口顶掉，所以都要挡
            foreach (string field in new[] { nameof(EntityData.storageId), nameof(EntityData.dispenserId) })
            {
                matcher.Start();
                matcher.MatchForward(true,
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(PlanetFactory), nameof(PlanetFactory.entityPool))),
                    new CodeMatch(OpCodes.Ldarg_2),
                    new CodeMatch(OpCodes.Ldelema),
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(EntityData), field)));

                if (matcher.IsInvalid || filter == null) continue;

                // 把加载 factory 的那条指令复制一份，避免写死局部变量编号
                var loadFactory = new CodeInstruction(matcher.InstructionAt(-4));

                matcher.Advance(1)
                       .Insert(loadFactory,
                               new CodeInstruction(OpCodes.Ldarg_2),
                               new CodeInstruction(OpCodes.Call, filter));

                patched++;
            }

            if (patched < 2)
                ProjectEdenPlugin.Log.LogError(
                    $"UIGame.OnPlayerInspecteeChange 只接管了 {patched}/2 处，" +
                    "点开综合物流枢纽可能是储物仓或配送器窗口，而不是物流站窗口。");
            else
                ProjectEdenPlugin.Log.LogInfo("UIGame.OnPlayerInspecteeChange：综合物流枢纽固定打开物流站窗口");

            return matcher.InstructionEnumeration();
        }
    }

    /// <summary>
    /// 让枢纽自带的缓冲仓<b>对玩家完全隐形</b>。
    ///
    /// <b>症状：往枢纽里放不进物流运输机。</b> PlanetFactory.EntityFastFillIn 里
    /// <c>storageId</c> 的判断排在<b>最前面</b>（比 stationId 早），所以玩家塞进这台建筑的
    /// 任何东西——包括小飞机——都先落进缓冲仓，物流站那一支根本走不到。
    /// EntityFastTakeOut 同理，取出来的也是缓冲仓里的东西。
    ///
    /// 缓冲仓是给配送运输机当货源的实现细节，不该出现在玩家的取放路径上，
    /// 所以把这两个方法里那句入口判断的 storageId 过滤成 0，让它们直接跳过储物仓分支。
    ///
    /// <b>拆除和清空那两条不能过滤</b>（TakeBackItemsInEntity / ClearItemsInEntity）——
    /// 缓冲仓里的货得还给玩家，过滤掉就凭空消失了。
    /// </summary>
    [HarmonyPatch]
    internal static class HubStorageBypassPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.EntityFastFillIn));
            yield return AccessTools.Method(typeof(PlanetFactory), nameof(PlanetFactory.EntityFastTakeOut));
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            MethodBase original)
        {
            FieldInfo storageId = AccessTools.Field(typeof(EntityData), nameof(EntityData.storageId));
            MethodInfo filter = AccessTools.Method(typeof(HubCourierPatches), nameof(HubCourierPatches.FilterHubId));

            var code = new List<CodeInstruction>(instructions);

            if (storageId == null || filter == null)
            {
                ProjectEdenPlugin.Log.LogError($"{original.Name}：解析不到 storageId / FilterHubId，补丁未生效");

                return code;
            }

            var count = 0;

            // 只认入口判断：ldfld storageId / ldc.i4.0 / ble——分支内部那几处读取不用管，
            // 入口一跳过它们就执行不到了
            for (var i = 0; i < code.Count - 2; i++)
            {
                if (!code[i].LoadsField(storageId)) continue;
                if (!code[i + 1].LoadsConstant(0)) continue;
                if (!code[i + 2].opcode.Name.StartsWith("ble")) continue;

                code.InsertRange(i + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0), // PlanetFactory this
                    new CodeInstruction(OpCodes.Ldarg_1), // int entityId
                    new CodeInstruction(OpCodes.Call, filter),
                });

                count++;
                i += 3;
            }

            if (count == 0)
                ProjectEdenPlugin.Log.LogError(
                    $"{original.Name}：没找到 storageId 的入口判断，" +
                    "综合物流枢纽会把玩家放进去的东西吞进缓冲仓，放不进物流运输机。");
            else
                ProjectEdenPlugin.Log.LogInfo($"{original.Name}：已让综合物流枢纽的缓冲仓避开玩家取放，接管 {count} 处");

            return code;
        }
    }
}