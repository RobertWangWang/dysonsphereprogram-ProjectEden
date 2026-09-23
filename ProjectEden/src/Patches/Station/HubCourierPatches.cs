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
    /// <b>改成搬数据，不改配送逻辑。</b> 给枢纽自带一个<b>不露面的中转台</b>
    /// （prefabDesc.isStorage，storageCol × storageRow 格），把它接给配送器当货源。
    ///
    /// <b>它是中转台，不是仓库——存储空间只有物流站那 30 个槽位。</b>
    /// 派机之前把当前服务的那种货摆上台（<see cref="Stage"/>），这一 tick 结束时
    /// 台上的东西<b>一件不留</b>地收回槽位（<see cref="DrainAll"/>）。
    /// tick 与 tick 之间它必然是空的，所以玩家在面板上看到的数字就是真实库存，
    /// 容量也就是槽位自己的一千万，而不是台子那 30 格。
    ///
    /// <b>这一版之前它真的是仓库，那就是「小飞机搬了货、槽位却没动」的成因。</b>
    /// 旧版每种货在台上留 1000 件（BufferPerItem），而且 10 tick 才对齐一次——
    /// 机甲还回来的货最多 1000 件永远躺在台上，槽位上一个数都不变。
    ///
    /// <b>为什么摆台必须在 InternalTick 之前：货跟着飞机走。</b>
    /// CourierData 自带 itemId / itemCount / inc 三个字段，所以出库发生在<b>派机那一刻</b>，
    /// 不是送达那一刻。回收方向反过来——飞机带着货到达后往台上塞，只要有空位就行，
    /// 而空台永远有空位，所以回收不需要预先摆什么。
    ///
    /// <b>为什么不干脆把货源换成物流站槽位。</b> 配送器在整个 InternalTick（3050 条指令）里
    /// <b>一次 StorageComponent 的方法调用都没有</b>，全是直接按 <c>grids[]</c> 字段搬，
    /// 收口只有 pickStorageSearchStart / insertStorageSearchStart 两个书签。
    /// 而 DispenserComponent.storage 的类型写死是 StorageComponent，物流站槽位是
    /// StationStore[]——类型层面就换不掉，除非把那 3050 条指令逐处接管。
    ///
    /// 配送逻辑、路径、动画、能耗全是原版的，一行都没碰。中转台是实现细节，界面上不出现。
    ///
    /// <b>原版不会自动连同一个实体上的储物仓。</b> CreateEntityLogicComponents 里
    /// 配送器那一段是 <c>ReadObjectConn</c> 找<b>相邻</b>实体的 storageId 再
    /// <c>ConnectToDispenser</c>，同实体的储物仓它看不见——所以要自己补一次连接。
    /// </summary>
    [HarmonyPatch]
    internal static class HubCourierPatches
    {
        /// <summary>
        /// 兜底清台的间隔。真正的清台在 <see cref="DispenserComponent_InternalTick_Postfix"/>，
        /// 每 tick 都跑；这一条只管 InternalTick 没跑到的情况（没电），
        /// 以及旧存档里还存着上一版留下的货。
        /// </summary>
        private const int IntervalTicks = 10;

        /// <summary>
        /// 多久<b>检查一次</b>要不要把 filter 轮到下一种货。60 tick = 1 秒。
        ///
        /// <para><b>它是检查周期，不是轮换周期</b>——这个区别是实测逼出来的。
        /// 1.12.10 之前这里就是轮换周期，而一趟配送要好几秒，于是每一趟都在
        /// 落地后的空窗里被换掉，34 次状态采样里 33 次是「20 架闲着 / 0 架在飞」。
        /// 现在真正的判据是「当前这种货还有没有活干」，见 <c>RotateFilter</c>。</para>
        /// </summary>
        private const int RotateTicks = 60;

        /// <summary>
        /// 同一种货最多霸占多久（tick）。1800 = 30 秒。
        ///
        /// <para>兜底用的：<b>没有它，一种永远送不到的货会把整台枢纽锁死</b>——
        /// 「还有活干」会一直成立，于是永远轮不到下一种。</para>
        /// </summary>
        private const int MaxDwellTicks = 1800;

        // ── 一、把配送器接到自己那个中转台上 ──────────────────

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

            // 枢纽点开的是物流站面板、没有配送器面板可调，所以这两个模式只能从配置来。
            //
            // <b>这里原本写着「Init 不给 playerMode 赋值，默认是 0（关闭）」，是错的。</b>
            // 实测 DispenserComponent.Init @0046 就是 <c>ldc.i4.2 ; stfld playerMode</c>——
            // 默认是 2（收发都做），写 0 的是 storageMode。真正会让配送运输机一动不动的
            // 是下面那个 courierAutoReplenish（Init @006A 写 false），那半才是对的。
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
                $"综合物流枢纽（实体 {entityId}）的配送运输机已接到自带中转台（storage {entity.storageId}），" +
                $"对机甲的配送模式 {dispenser?.playerMode}");
        }

        /// <summary>
        /// 启动时报一行：中转台那两个挂点到底有没有真的打上去。
        ///
        /// <b>这次改动的失败形态正是「每一步都成功、功能整个不在」。</b>
        /// 如果前置/后置没挂上，配送器照常工作、日志照常绿，只是货又开始在台上过夜——
        /// 而玩家看到的还是老症状「小飞机搬了货、槽位没动」，
        /// 分不清是补丁没生效还是别的什么。
        ///
        /// 读的是 Harmony 自己的补丁表（<c>GetAllPatchedMethods</c>），也就是
        /// <b>实际生效的状态</b>，不是「我以为我注册了」。所以它必须在 PatchAll <b>之后</b>调。
        /// </summary>
        internal static void Report()
        {
            var hooked = false;

            foreach (MethodBase mb in Harmony.GetAllPatchedMethods())
                if (mb?.DeclaringType == typeof(DispenserComponent) &&
                    mb.Name == nameof(DispenserComponent.InternalTick))
                    hooked = true;

            if (hooked)
                ProjectEdenPlugin.Log.LogInfo(
                    "综合物流枢纽：中转台挂点已生效（DispenserComponent.InternalTick 前置摆货 / 后置清台）。" +
                    "存储空间就是物流站那 30 个槽位，中转台在 tick 之间必然是空的——" +
                    "**之后如果状态行里「中转台滞留」不是 0，那是槽位满了退不回去，不是补丁没生效**。");
            else
                ProjectEdenPlugin.Log.LogError(
                    "综合物流枢纽：DispenserComponent.InternalTick 没有被补丁接管，中转台会退化成仓库——" +
                    "配送运输机搬的货会滞留在一个玩家看不见的储物仓里，物流站槽位上的数字不动。");
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

        // ── 二、中转台：摆上去、当 tick 收回来 ──────────────────

        /// <summary>
        /// 派机之前把当前服务的那种货摆上中转台。
        ///
        /// <b>为什么是前置。</b> CourierData 自带 itemId / itemCount / inc，货跟着飞机走，
        /// 所以出库发生在派机那一刻——摆晚一步，这一 tick 就派不出去。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DispenserComponent), nameof(DispenserComponent.InternalTick))]
        private static void DispenserComponent_InternalTick_Prefix(DispenserComponent __instance,
            PlanetFactory factory, int courierCarries)
        {
            // 没有闲着的配送运输机就派不出机，这一 tick 不会有人来取货——
            // 不摆台，枢纽在没活干的时候就是零开销。已经在飞的那些货跟着飞机走，
            // 不在台上，所以这条捷径不会截断在途的任何一趟
            if (__instance.idleCourierCount <= 0) return;

            StationComponent station = HubStation(__instance, factory);

            if (station == null) return;

            // 摆够「这一 tick 最多可能派出去的量」即可。用 long 算再夹回 int：
            // 本 mod 会把运载量放大，两个 int 相乘是能溢出的，而溢出成负数
            // 会让下面的 want 变成 0——摆不上货，表现是配送运输机原地不动
            long cap = (long)courierCarries * __instance.idleCourierCount;

            // <b>再按「机甲这一种货还缺多少」夹一道，这是 1.12.11 修的那个洞。</b>
            //
            // 上面那个 cap 是<b>理论派出上限</b>：运载量 5000 × 闲置 20 架 = 每 tick
            // 从槽位搬走 <b>10 万件</b>，而机甲往往只缺几百件。多搬的那 9 万多件
            // 当 tick 就要原样退回去——可枢纽的槽位是<b>本地需求</b>、容量一千万，
            // 物流网在同一 tick 里又把它填满了，于是退不回去，
            // <see cref="FindDrainSlot"/> 只好另开一格：玩家看到的就是
            // 「设了一格供货给伊卡洛斯，站里凭空多出一格同样的货，还自动设成了仓储」。
            //
            // 搬「真正缺的那点」而不是「理论上搬得动的那么多」，这笔churn就根本不存在。
            // 式子<b>和 HasWork / MechaNeed 逐字同源</b>——三处共用一个式子，
            // 才不会出现「日志说该送货、摆台却认为不用摆」这种自相矛盾。
            long need = MechaShortfall(GameMain.mainPlayer?.deliveryPackage, __instance.filter, __instance);

            if (need < cap) cap = need;

            // 「机甲不缺这种货」和「想摆却一件也摆不上」是<b>两回事</b>，
            // 合进一个计数就什么也测不出来——本仓库为这个形状付过好几次账
            if (cap <= 0)
            {
                System.Threading.Interlocked.Increment(ref _stageNoNeedSinceReport);

                return;
            }

            int moved = Stage(station, __instance.storage, __instance.filter,
                cap > int.MaxValue ? int.MaxValue : (int)cap);

            // 诊断用：这一 tick 真的摆上去了多少。**状态行到目前为止只能看到「台上剩多少」，
            // 而正常情况下那必然是 0（后置会清空），所以它分不开「摆上去了又被取走」
            // 和「压根没摆上去」**——两者都显示 0，而后者正是「一架不飞」的成因之一。
            if (moved > 0) System.Threading.Interlocked.Add(ref _stagedSinceReport, moved);
            else System.Threading.Interlocked.Increment(ref _stageMissSinceReport);
        }

        /// <summary>
        /// 这一 tick 结束，把中转台上剩的东西<b>全部</b>收回槽位。
        ///
        /// 没派出去的（摆多了）原样退回，机甲还回来的（这一 tick 刚到货）进物流网，
        /// 两件事是同一句话。台子因此在 tick 之间必然是空的。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DispenserComponent), nameof(DispenserComponent.InternalTick))]
        private static void DispenserComponent_InternalTick_Postfix(DispenserComponent __instance,
            PlanetFactory factory)
        {
            StationComponent station = HubStation(__instance, factory);

            if (station == null) return;

            DrainAll(station, __instance.storage);
        }

        /// <summary>
        /// 这台配送器是不是本 mod 的枢纽（同一个实体上还挂着物流站）？是就返回那个物流站。
        ///
        /// <b>判据是实体上同时有 stationId 和 storage，不是 protoId。</b>
        /// 原版的配送器挂在储物箱上，没有 stationId，一条比较就分开了；
        /// 而按 protoId 去查机器表要遍历一遍列表，这是<b>每台配送器每 tick 都走</b>的路。
        /// </summary>
        private static StationComponent HubStation(DispenserComponent dispenser, PlanetFactory factory)
        {
            if (dispenser?.storage == null || factory?.entityPool == null) return null;

            int entityId = dispenser.entityId;

            if (entityId <= 0 || entityId >= factory.entityPool.Length) return null;

            int stationId = factory.entityPool[entityId].stationId;

            if (stationId <= 0) return null;

            StationComponent[] pool = factory.transport?.stationPool;

            if (pool == null || stationId >= pool.Length) return null;

            StationComponent station = pool[stationId];

            return station != null && station.id == stationId && station.storage != null ? station : null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void PlanetTransport_GameTick(PlanetTransport __instance, long time)
        {
            // 诊断打点：放在一切 return 之前（理由见 LabLogisticSupplyPatches 同一行）
            Diagnostics.TransportSplitProbe.Phase("综合物流枢纽");

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

                // 兜底清台：正常情况下 InternalTick 的后置已经清空了，这里什么都搬不动。
                // 它管的是 InternalTick 没跑到的情况（没电），以及旧存档——
                // 上一版把这里当仓库用，每种货留着 1000 件，那些货得还给槽位
                DrainAll(station, buffer);
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

            // 没有别的货可换——这不算「被挡住」，不要走下面那条解释
            if (chosen == 0 || chosen == current) return;

            // <b>当前这种货还有活干的时候不要换。</b>
            //
            // 这一条是实测逼出来的。下面那道 workCourierCount 守卫防住了「在飞时换」，
            // 但它<b>防不住「刚落地又换」</b>：检查每 60 tick 跑一次，而一趟配送要好几秒，
            // 于是每次落地后的那个空窗都会被换掉，下一趟根本轮不到同一种货。
            // 34 次状态采样里 <b>33 次是「20 架闲着 / 0 架在飞」</b>，
            // 而同一行一直写着「机甲持有 0 / 需求 5000 → 该送货」——
            // 货、需求、配对、星球开关全都在，就是没有一架飞出去。
            //
            // 所以判据从「时间到了就换」改成「这一种没活干了才换」：
            // 机甲要的量已经满足（或者该回收的已经收完），或者枢纽里这种货没了。
            //
            // <b>必须留一条兜底</b>，否则一种永远送不到的货会把这台枢纽锁死：
            // 同一种货最多霸占 <see cref="MaxDwellTicks"/>，到点强制轮换。
            (int, int) dwellKey = (factory.planetId, dispenserId);
            long since = DwellSince.GetOrAdd(dwellKey, time);

            if (current > 0
                && HasWork(pkg, current, dispenser)
                && Stocked(station, current)
                && time - since < MaxDwellTicks)
            {
                ExplainDwell(time, current);

                return;
            }

            // <b>换 filter 会把正在飞的配送运输机原地掉头。</b>
            // SetDispenserFilter @0035 调 RefreshDispenserTraffic → OnRematchPairs，
            // 而那个方法里有<b>三处</b> CourierTurnbackFromPlayer，它做的事是
            // <c>endId = 0 ; direction = -1 ; t = maxt</c>——把飞机掉头送回家，空手。
            //
            // 枢纽里只有一种货时 chosen == current，永远不换，所以这个坑一直没露头。
            // 玩家在第二个槽位放上第二种货的那一刻，1 秒换一次 × 一趟往返好几秒，
            // 于是每一架刚飞出去就被掉头——报上来正是「飞机飞出来又跑回去了」。
            //
            // 所以只在<b>一架都不在飞</b>的时候换。这不会饿死别的货：需求被满足之后
            // 这一批就会全部返航，那时自然轮到下一种。
            if (dispenser.workCourierCount > 0)
            {
                ExplainBusy(time);

                return;
            }

            // 走 SetDispenserFilter 而不是直接赋值：它会顺带 RefreshDispenserTraffic 重新配对
            factory.transport.SetDispenserFilter(dispenserId, chosen);

            DwellSince[dwellKey] = time;

            _lastRotateTick = time;
            System.Threading.Interlocked.Increment(ref _rotations);
        }

        /// <summary>
        /// 把原版自己算出来的「卡在哪一道闸」念出来，外加那两道闸各自的实际数值。
        ///
        /// <para><b>DispenserComponent.InternalTick 开头就是一条闸门链，每过一道就把
        /// <c>playerDeliveryCondition</c> 往前推一格</b>（IL 00CE / 00EF / 0105 / 0142 / 015E）：</para>
        /// <code>
        ///   00C0  if (time % 3 != gene)        整段跳过（原版的错帧闸，每 3 tick 轮到一次）
        ///   00CE  condition = 1
        ///   00E8  if (idleCourierCount &lt;= 0)  退出   → 卡在 1：没有闲置配送运输机
        ///   00EF  condition = 2
        ///   00FE  if (playerPairCount &lt;= 0)    退出   → 卡在 2：没有配对
        ///   0105  condition = 3
        ///   0112  CheckDeliveryRange(...)              → 卡在 3：机甲不在配送范围内
        ///   0142  condition = 4（只有在范围内才写）
        ///   0157  if (energy &lt; 距离×20000+100000) 退出 → 卡在 4：配送器电不够
        ///   015E  condition = 5                        → 全过了，正在干活
        /// </code>
        ///
        /// <para><b>配送范围是个角度，不是距离。</b> <c>deliveryRange</c> 由
        /// <c>PlanetTransport.GameTick</c> @02E6–0318 算出来：
        /// <c>cos(history.dispenserDeliveryMaxAngle × π / 180)</c>，小于 −0.999 时钳成 −1（整颗星球）。
        /// 所以判据是「枢纽方向和机甲方向的夹角」，而<b>这正是「换了颗星球就不送货」最自然的解释</b>——
        /// 落点离枢纽太远，超出了那个角度。这里把点积和阈值都打出来，一眼能对。</para>
        /// </summary>
        private static string DeliveryVerdict(PlanetFactory factory, StationComponent station,
            DispenserComponent dispenser)
        {
            if (dispenser == null) return "配送闸 —（没有配送器）";

            var stage = (int)dispenser.playerDeliveryCondition;

            string name;

            switch (stage)
            {
                case 0: name = "**这一 tick 没轮到**（原版每 3 tick 才过一次）"; break;
                case 1: name = "**卡在「没有闲置配送运输机」**"; break;
                case 2: name = "**卡在「没有配对」**"; break;
                case 3: name = "**卡在「机甲不在配送范围内」**"; break;
                case 4: name = "**卡在「配送器电不够」**"; break;
                case 5: name = "全过了，正在派机"; break;
                default: name = $"未知（{stage}）"; break;
            }

            // 范围那一道：算出点积和阈值，直接对着看
            var angleText = "";

            Player player = GameMain.mainPlayer;

            if (player != null && factory.entityPool != null
                                && station.entityId > 0 && station.entityId < factory.entityPool.Length)
            {
                UnityEngine.Vector3 hub = factory.entityPool[station.entityId].pos.normalized;
                UnityEngine.Vector3 me = player.position.normalized;

                double dot = hub.x * me.x + hub.y * me.y + hub.z * me.z;
                float maxAngle = GameMain.history?.dispenserDeliveryMaxAngle ?? 0f;
                double threshold = System.Math.Cos(maxAngle * System.Math.PI / 180.0);

                if (threshold < -0.999) threshold = -1.0;

                angleText = $"｜配送范围 夹角余弦 {dot:0.0000} vs 阈值 {threshold:0.0000}"
                            + $"（上限 {maxAngle:0.#}°，{(dot >= threshold ? "在范围内" : "**超出范围**")}）";
            }

            return $"配送闸 {name}｜配送器电量 {dispenser.energy / 1e6:0.##} MJ / "
                   + $"{dispenser.energyMax / 1e6:0.##} MJ" + angleText
                   + $"｜摆台 本周期 {System.Threading.Interlocked.Exchange(ref _stagedSinceReport, 0)} 件"
                   + $"／空手 {System.Threading.Interlocked.Exchange(ref _stageMissSinceReport, 0)} 次"
                   + $"／机甲不缺 {System.Threading.Interlocked.Exchange(ref _stageNoNeedSinceReport, 0)} 次"
                   + $"（槽位里这种货还有 {SlotStock(station, dispenser.filter)} 件）"
                   + PairMath(dispenser);
        }

        /// <summary>
        /// 派机循环里最后那两道闸的实际数值，外加原版自己给每一对记的 <c>runtimeState</c>。
        ///
        /// <para><b>这两道闸之前一次都没被看过，而它们恰恰在「闸全过了」之后。</b>
        /// <c>playerDeliveryCondition</c> 只走到「进了配对循环」（=5）就不再往下写了，
        /// 循环<b>内部</b>还有两道，记在 <c>SupplyDemandPair.runtimeState</c> 上
        /// （IL 01DF / 026B / 027B）：</para>
        /// <code>
        ///   01D7  if (格子物品 != filter)      跳过这一对        → state 保持 0
        ///   01DF  state = 1
        ///   0263  if (机甲实际持有 >= 需求)     跳过              → 卡在 1：已经够了
        ///   026B  state = 2
        ///   0273  if (还装得下的量 &lt;= 0)       跳过              → 卡在 2：**装不下**
        ///   027B  state = 3                                     → 这一对真的去派机了
        /// </code>
        ///
        /// <para>其中「还装得下的量」= <c>grid.stackSizeModified − grid.modifiedCount
        /// + packageUtility.GetPackageItemCapacity(itemId)</c>，也就是
        /// <b>配送清单那一格的剩余空间 + 背包的剩余空间</b>。两者都满就是 0，
        /// 于是货有、需求有、闸全过，却一架都不派。</para>
        /// </summary>
        private static string PairMath(DispenserComponent dispenser)
        {
            int filter = dispenser.filter;

            if (filter <= 0) return "";

            var states = "";

            if (dispenser.pairs != null)
                for (var p = 0; p < dispenser.playerPairCount && p < dispenser.pairs.Length; p++)
                    states += (p > 0 ? "," : "") + dispenser.pairs[p].runtimeState;

            DeliveryPackage pkg = GameMain.mainPlayer?.deliveryPackage;

            if (pkg?.grids == null) return $"｜配对状态 [{states}]";

            for (var g = 0; g < pkg.gridLength; g++)
            {
                if (pkg.grids[g].itemId != filter) continue;

                int inPack = dispenser.packageUtility?.GetPackageItemCountIncludeHandItem(filter) ?? -1;
                int capacity = dispenser.packageUtility?.GetPackageItemCapacity(filter) ?? -1;
                int stackMod = pkg.grids[g].stackSizeModified;
                int modified = pkg.grids[g].modifiedCount;
                int required = pkg.grids[g].clampedRequireCount;

                long have = pkg.grids[g].count + (long)inPack;
                long room = (long)stackMod - modified + capacity;

                return $"｜配对状态 [{states}]｜派机算式：实际持有 {have}"
                       + $"（清单 {pkg.grids[g].count} + 背包含手上 {inPack}）"
                       + $" vs 需求 {required}"
                       + $"；还装得下 {room}（清单格剩 {stackMod - modified} + 背包剩 {capacity}）"
                       + $" → {(have >= required ? "**已经够了，不派**" : room <= 0 ? "**装不下，不派**" : "该派机")}";
            }

            return $"｜配对状态 [{states}]｜派机算式：**这种货不在配送清单里**";
        }

        /// <summary>
        /// 这种货对机甲还有活干吗？<b>判据逐字抄状态行里那一条</b>
        /// （<see cref="MechaNeed"/>），两处用同一个式子，才不会出现
        /// 「日志说该送货、轮换却认为没活干」这种自相矛盾。
        ///
        /// <para>三个字段都在 <c>DeliveryPackage/GRID</c> 上：持有少于需求 → 该送货；
        /// 持有多于回收线 → 该回收。两者都不是就没活干。</para>
        /// </summary>
        private static bool HasWork(DeliveryPackage pkg, int itemId, DispenserComponent dispenser)
        {
            if (pkg?.grids == null || itemId <= 0) return false;

            for (var g = 0; g < pkg.gridLength; g++)
            {
                if (pkg.grids[g].itemId != itemId) continue;

                // <b>「持有多少」必须把背包（含手上那一格）算进去。</b>
                // 配送清单那一格的 count 只是<b>清单自己</b>的数，机甲真正拥有的在背包里——
                // 原版 InternalTick @01EC–0223 算的是
                // `max(modifiedCount, count) + GetPackageItemCountIncludeHandItem(itemId)`。
                //
                // **只读 count 会把「背包里已经有 5000 个」读成「持有 0」**，于是
                // 「还有活干」恒为真，轮换被卡在一种早就够了的货上直到 30 秒兜底到期。
                // 9 种货就是四分半钟——真正缺的那一种排在后面，表现正是「不给机甲送货」。
                int inPack = dispenser?.packageUtility?.GetPackageItemCountIncludeHandItem(itemId) ?? 0;

                long have = (long)pkg.grids[g].count + inPack;

                // 装得下多少，同样抄原版 @022D–024D：清单格剩余 + 背包剩余。
                // 这一项是 0 的时候原版也不派（@0273），所以它同样算「没活干」
                int capacity = dispenser?.packageUtility?.GetPackageItemCapacity(itemId) ?? 0;

                long room = (long)pkg.grids[g].stackSizeModified - pkg.grids[g].modifiedCount + capacity;

                bool deliver = have < pkg.grids[g].clampedRequireCount && room > 0;
                bool recycle = have > pkg.grids[g].recycleCount;

                return deliver || recycle;
            }

            // 不在清单里的货配送器根本不碰，当然也就没活干
            return false;
        }

        /// <summary>
        /// 机甲这一种货<b>还能吃下多少</b>——摆台量的上限。
        ///
        /// <para><b>式子和 <see cref="HasWork"/> 逐字同源</b>，只是那边回答「有没有活」、
        /// 这边回答「有多少活」。两处必须同源：摆台摆多了的那一笔当 tick 就要退回槽位，
        /// 而枢纽的槽位常年是满的（本地需求 + 一千万容量 + 物流网持续补），
        /// 退不回去就会另开一格同物品的槽位。</para>
        ///
        /// <para>只算<b>送货</b>方向。回收方向是货从机甲往枢纽走，不需要摆台，
        /// 所以这里返回 0 是对的——回收那条路由原版自己往中转台里塞，
        /// <see cref="DrainAll"/> 当 tick 收回槽位。</para>
        /// </summary>
        private static long MechaShortfall(DeliveryPackage pkg, int itemId, DispenserComponent dispenser)
        {
            if (pkg?.grids == null || itemId <= 0) return 0;

            for (var g = 0; g < pkg.gridLength; g++)
            {
                if (pkg.grids[g].itemId != itemId) continue;

                int inPack = dispenser?.packageUtility?.GetPackageItemCountIncludeHandItem(itemId) ?? 0;

                long have = (long)pkg.grids[g].count + inPack;

                int capacity = dispenser?.packageUtility?.GetPackageItemCapacity(itemId) ?? 0;

                long room = (long)pkg.grids[g].stackSizeModified - pkg.grids[g].modifiedCount + capacity;

                long need = pkg.grids[g].clampedRequireCount - have;

                if (need <= 0 || room <= 0) return 0;

                return need < room ? need : room;
            }

            return 0;
        }

        /// <summary>槽位里这种货一共还有多少件——只给诊断用。</summary>
        private static long SlotStock(StationComponent station, int itemId)
        {
            StationStore[] slots = station.storage;

            if (slots == null || itemId <= 0) return 0;

            long sum = 0;

            for (var s = 0; s < slots.Length; s++)
                if (slots[s].itemId == itemId)
                    sum += slots[s].count;

            return sum;
        }

        /// <summary>枢纽的槽位里还有这种货吗？没有了就该轮下一种，再等也没用。</summary>
        private static bool Stocked(StationComponent station, int itemId)
        {
            StationStore[] slots = station.storage;

            if (slots == null) return false;

            for (var s = 0; s < slots.Length; s++)
                if (slots[s].itemId == itemId && slots[s].count > 0)
                    return true;

            return false;
        }

        /// <summary>
        /// 每台配送器「当前这种货是什么时候换上的」。
        ///
        /// <para><b>必须按配送器分开记，不能用一个全局量</b>：
        /// <c>PlanetTransport.GameTick</c> 是跨星球并行的，一个静态字段会被几十个
        /// 星球的枢纽互相覆盖，兜底超时就变成了随机数。本文件里那个
        /// <c>_lastRotateTick</c> 只喂给一条一次性的解释日志，不参与判定。</para>
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), long>
            DwellSince = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), long>();

        private static int _dwellExplained;

        /// <summary>一次性说明：现在是「送完再换」，不是「到点就换」。</summary>
        private static void ExplainDwell(long time, int itemId)
        {
            if (System.Threading.Interlocked.Exchange(ref _dwellExplained, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"综合物流枢纽：当前服务的「{LDB.items.Select(itemId)?.name ?? itemId.ToString()}」还没送完，"
                + "先不轮换。**这是有意的**——检查每秒一次，而一趟配送要好几秒，"
                + "「到点就换」会把每一趟都掐死在落地后的那个空窗里"
                + "（换 filter 会 RefreshDispenserTraffic → CourierTurnbackFromPlayer 把在飞的掉头）。"
                + $"改成「这一种没活干了才换」，并留 {MaxDwellTicks / 60} 秒兜底，"
                + "免得一种永远送不到的货把整台枢纽锁死。");
        }

        /// <summary>轮换过多少次。<b>状态行里必须有这个数</b>，见 <see cref="Report"/> 里的说明。</summary>
        private static int _rotations;

        private static long _lastRotateTick;

        private static int _busyExplained;

        /// <summary>
        /// 「想换但一直有飞机在飞」持续太久时说一声。
        ///
        /// 不说的话这是个哑分支：某一种货长期轮不到，而日志里一个字都没有。
        /// 它也<b>不会</b>强行换——强行换就是让在飞的运输机空手返航，
        /// 用一个看得见的故障去换一个看不见的延迟，不划算。
        /// </summary>
        private static void ExplainBusy(long time)
        {
            // 第一次遇到时先把基准对上，否则 _lastRotateTick 还是 0，减出来必然超阈值
            if (_lastRotateTick == 0)
            {
                _lastRotateTick = time;

                return;
            }

            if (time - _lastRotateTick < 1800) return;
            if (System.Threading.Interlocked.Exchange(ref _busyExplained, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                "综合物流枢纽：配送运输机一直有在飞的，已经 30 秒没能轮换服务的货种。" +
                "**这是有意的**——换货种会让在飞的运输机掉头空手返航" +
                "（SetDispenserFilter → RefreshDispenserTraffic → OnRematchPairs 里三处 " +
                "CourierTurnbackFromPlayer），所以只在全部空闲时才换。" +
                "如果某种货长期轮不到，说明当前这种货的需求一直没被满足。");
        }

        /// <summary>
        /// 每 10 秒报一次枢纽的实际状态。
        ///
        /// <b>为什么要常驻而不是一次性。</b> 这条链有四段——站内槽位 → 中转台 →
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
                // 「10 种（铁矿 铁矿 铁矿）」看着像 bug，其实只是数错了口径。
                //
                // 注意这一栏现在<b>正常就是 0 种</b>：中转台在 tick 之间必然是空的。
                // 不为 0 只有一种含义——槽位满了、货退不回去
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
                $"中转台滞留 {bufferKinds} 种（{bufferText.Trim()}，正常为 0）｜" +
                $"配送清单 {listed} 条｜" +
                $"配送运输机 闲 {dispenser?.idleCourierCount ?? -1} / 忙 {dispenser?.workCourierCount ?? -1}｜" +
                // 这里原本写的是 `dispenser?.pickStorageSearchStart != null ? "是" : "否"`，
                // 而那是个 **Int32**——`int?` 只要 dispenser 不为 null 就恒为 true，
                // 于是这一栏永远是「是」。**一个不会随被测对象变化的读数不是测量**，
                // 本文件记过三次的那条，这是第四次。真正要问的是「货源那个储物仓接上了没有」
                $"货源 {(dispenser?.storage != null ? $"已接（{dispenser.storage.size} 格）" : "**没接上**")}｜" +
                $"已配对 {dispenser?.playerPairCount ?? -1} 条｜" +
                // <b>光报「当前服务哪种货」会骗人，必须带上轮换次数。</b>
                // 这一行每 600 tick 一条，而轮换是每 60 tick 一次——两种货正好轮 10 次（偶数）
                // 回到原点，于是采样出来每次都是同一种，看着像「根本没在轮换」。
                // 「均值掩盖双峰分布」的同族：周期采样和周期过程对齐时，读数是个假象
                $"当前服务 {LDB.items.Select(dispenser?.filter ?? 0)?.name ?? "（无）"}" +
                $"（已轮换 {_rotations} 次）｜" +
                $"{MechaNeed(pkg, dispenser?.filter ?? 0, dispenser)}｜" +
                $"星球配送开关 {factory.transport.playerDeliveryEnabled}｜" +
                // **原版自己就记着「卡在哪一道闸」**，而我们绕了好几轮才想起来读它
                DeliveryVerdict(factory, station, dispenser));
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
                if (Unexplained(0))
                    Explain(0, $"找不到实体 {entityId} 对应的枢纽配置（protoId {factory.entityPool[entityId].protoId}）");

                return;
            }

            if (!cfg.autoDeliveryList)
            {
                // <b>这一支不能报 WARNING。</b> 关着是 1.10.1 起的默认值、是所有者的决定，
                // 不是故障；报成「未生效」会让每一局的日志都带一条假警报，
                // 而这个仓库的全部排查方法就是读日志。
                //
                // 但也不能什么都不说：关掉之后，一台清单里没有对应条目的枢纽是<b>完全不动</b>的，
                // 而「补丁没生效」和「玩家还没在机甲面板上配清单」长得一模一样。
                // 所以照样打一行，只是说清楚该去哪儿配。
                ExplainListOwnedByPlayer();

                return;
            }

            // 只管脚下这颗星球：配送运输机本来也飞不到别的星球。
            // 这一条同时也是线程安全的保证——PlanetTransport.GameTick 是各星球并行跑的，
            // 而 deliveryPackage 是玩家全局的一份，只让脚下这颗星球的那个线程写。
            if (GameMain.localPlanet == null || factory.planetId != GameMain.localPlanet.id) return;

            DeliveryPackage pkg = GameMain.mainPlayer?.deliveryPackage;

            if (pkg?.grids == null)
            {
                if (Unexplained(1)) Explain(1, "取不到伊卡洛斯的配送需求清单（deliveryPackage 为空）");

                return;
            }

            if (!pkg.unlockedAndEnabled)
            {
                if (Unexplained(2))
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
                    if (Unexplained(3))
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

            // 一件都没加成时说清楚是为什么——不然又是「日志一行都没有」。
            // added == 0 是稳态，所以拼字符串之前必须先问 Unexplained（见它的注释）
            if (added == 0 && Unexplained(5))
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

        /// <summary>
        /// 这条理由还没报过吗——<b>拼消息之前先问这一句</b>。
        ///
        /// <see cref="Explain"/> 自己也抢（并行 tick 上两个线程可能同时进来），但那是在
        /// 消息<b>已经拼好之后</b>。而这几条理由里有好几条是稳态：没有需要新加的（5）、
        /// 清单还没解锁（2）、清单满了（3）——每台枢纽每 10 tick 都会走到，
        /// 插值字符串就每 10 tick 分配一次。CLAUDE.md 的 <c>AdvancedMinerPatches.ClaimLog</c>
        /// 那条规矩正是为此：<b>先抢，再拼</b>。
        ///
        /// 拆成「先问一句、再由 Explain 抢」两步，而不是把 Explain 改成收委托：
        /// 闭包捕获局部变量同样要分配一个对象，换汤不换药。
        /// </summary>
        private static bool Unexplained(int reason) =>
            System.Threading.Volatile.Read(ref Explained[reason]) == 0;

        private static void Explain(int reason, string message)
        {
            if (System.Threading.Interlocked.Exchange(ref Explained[reason], 1) != 0) return;

            ProjectEdenPlugin.Log.LogWarning($"综合物流枢纽自动配送未生效：{message}");
        }

        /// <summary>
        /// 当前服务这种货，机甲身上有多少 / 要多少 / 超过多少就回收。
        ///
        /// <b>没有这一栏，状态行答不出「为什么一架都没飞」。</b> 实测有一局从头到尾都是
        /// 「闲 20 / 忙 0」，而<b>「机甲身上够了，没活干」和「派机那一步被挡住了」
        /// 在日志里长得一模一样</b>——前者是正常，后者是故障，需要的处置完全相反。
        ///
        /// 三个数一摆就分开了：持有 == 需求 且 持有 &lt;= 回收线，就是没活干；
        /// 持有 &lt; 需求却一架不飞，才是真出了问题。
        ///
        /// 这三个字段都在 <c>DeliveryPackage/GRID</c> 上（<c>StorageComponent/GRID</c> 没有
        /// requireCount/recycleCount，两个 GRID 是不同的嵌套类型，别弄混）。
        /// </summary>
        private static string MechaNeed(DeliveryPackage pkg, int itemId, DispenserComponent dispenser)
        {
            if (pkg?.grids == null || itemId <= 0) return "机甲需求 —";

            for (var g = 0; g < pkg.gridLength; g++)
            {
                if (pkg.grids[g].itemId != itemId) continue;

                // <b>这一行曾经撒过谎，代价是连着好几轮查错方向。</b>
                // 原先只读 `pkg.grids[g].count`——那是<b>配送清单那一格</b>里的数，
                // 而机甲真正拥有的在背包里。于是背包里躺着 5000 个的时候，
                // 这一行报「持有 0 → 该送货」，而原版同一 tick 判的是「已经够了，不派」，
                // **两者在同一行日志里自相矛盾，却没人发现**。
                //
                // 现在逐字抄原版 InternalTick @01EC–0223 的式子。
                int inPack = dispenser?.packageUtility?.GetPackageItemCountIncludeHandItem(itemId) ?? 0;

                long have = (long)pkg.grids[g].count + inPack;
                int need = pkg.grids[g].clampedRequireCount;
                int back = pkg.grids[g].recycleCount;

                string verdict = have < need ? "**该送货**"
                    : have > back ? "**该回收**"
                    : "够了，没活干";

                return $"机甲持有 {have}（清单 {pkg.grids[g].count} + 背包含手上 {inPack}）"
                       + $" / 需求 {need} / 回收线 {back} → {verdict}";
            }

            return "机甲需求 —（这种货不在配送清单里，配送器不会碰它）";
        }

        private static int _listOwnedExplained;

        /// <summary>
        /// 自动填清单关着时说一行：枢纽服务哪些货，由<b>玩家自己的配送需求清单</b>决定。
        ///
        /// 顺带把清单当前有几条报出来——0 条就是「配送运输机一架都不会动」的完整解释，
        /// 不然玩家看到的只是「小飞机停着」，和补丁没生效分不开。
        /// </summary>
        private static void ExplainListOwnedByPlayer()
        {
            if (System.Threading.Interlocked.Exchange(ref _listOwnedExplained, 1) != 0) return;

            DeliveryPackage pkg = GameMain.mainPlayer?.deliveryPackage;

            var listed = 0;

            if (pkg?.grids != null)
                for (var g = 0; g < pkg.gridLength; g++)
                    if (pkg.grids[g].itemId > 0)
                        listed++;

            ProjectEdenPlugin.Log.LogInfo(
                $"综合物流枢纽：自动填配送清单已关闭（machines.json 的 autoDeliveryList，默认就是关的）。" +
                $"枢纽服务哪些货完全由**你自己的配送需求清单**决定，当前清单里有 {listed} 条。" +
                (listed == 0
                    ? "**现在是 0 条，所以配送运输机一架都不会动**——去机甲面板的物流调配里把要收发的物品加进去。"
                    : "清单里列过、而枢纽槽位里也有的货，就会被收发。"));
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
        /// 中转台 → 站内槽位，<b>一件不留</b>。没派出去的原样退回，机甲还回来的进物流网。
        ///
        /// <b>这里原本有个 1000 件的保留量（BufferPerItem），那是个 bug。</b>
        /// 保留量的本意是「够配送运输机装一趟」，但配送器是派机那一刻才扣货的，
        /// 摆台和派机在同一 tick 内完成，根本不需要跨 tick 囤。而代价是每种货有
        /// 最多 1000 件永远躺在台上——玩家报的<b>「小飞机搬了货，物流站槽位没动」</b>
        /// 就是这 1000 件。
        ///
        /// 退不回去的会留在台上，下一 tick 再试。那只有一种情况：槽位满了、
        /// 或者 30 个槽位里既没有这种货也没有空位——那时货放在哪里都一样。
        /// </summary>
        private static void DrainAll(StationComponent station, StorageComponent buffer)
        {
            StationStore[] slots = station.storage;

            if (slots == null || buffer?.grids == null) return;

            for (var g = 0; g < buffer.size; g++)
            {
                int itemId = buffer.grids[g].itemId;

                if (itemId <= 0 || buffer.grids[g].count <= 0) continue;

                // 同一种货前面已经处理过就跳过——TakeItem 是按物品取的，一次就够。
                // <b>而「有多少」也必须按物品问 GetItemCount，不能读单格的 count</b>：
                // 一种货是摊在多个格子里的（每格一个堆叠上限），逐格算会把大头漏在台上
                if (SeenEarlier(buffer, g, itemId)) continue;

                int slot = FindDrainSlot(slots, itemId, out long room);

                if (slot < 0 || room <= 0) continue;

                int have = buffer.GetItemCount(itemId);
                int move = have < room ? have : (int)room;

                if (move <= 0) continue;

                // **调用前清零**：StorageComponent.TakeItem 既读侧信道也写侧信道
                // （实测读 3 写 2），不清的话它读到的是上一个人留下的值。
                if (QualityAccess.ChannelClearable) QualityAccess.ClearChannel();

                int taken = buffer.TakeItem(itemId, move, out int inc);

                // **调用后读一次**：取货类的方法是「被调方写、调用方读」，
                // 这一笔就是它从台上带出来的品质。读完清掉，别留给下一个人。
                int qua = QualityAccess.ChannelReady ? QualityAccess.GetChannel0() : 0;

                if (QualityAccess.ChannelClearable) QualityAccess.ClearChannel();

                if (taken <= 0) continue;

                // 槽位空着时这一句才真的赋值。方向（本地/远程逻辑）故意不碰——
                // 那是玩家在面板上设的，本 mod 的规矩是只在第一次分配物品时写一次
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

        /// <summary>
        /// 站内槽位 → 中转台。<b>只摆当前服务的那一种货</b>。
        ///
        /// 配送器一次只认 <c>filter</c>——InternalTick 里两处都拿它当物品 ID 用
        /// （<c>grids[i].itemId != filter → continue</c> 和
        /// <c>PickFromStoragePrecalc(filter, …)</c>），别的货摆上去也没人取，
        /// 反而占掉台子的格子。<c>filter</c> 由 <see cref="RotateFilter"/> 每秒轮一轮。
        ///
        /// <b><paramref name="cap"/> 是省事的上限，不是正确性的一部分。</b>
        /// 真正兜底的是 AddItem——它返回自己吃下多少，槽位就只扣多少，
        /// 所以 cap 给大了最多是多搬一趟（后置当 tick 原样收回），给小了才会少派货。
        /// 取「闲置运输机数 × 每架运载量」是这一 tick 可能派出去的上限，不会少。
        /// </summary>
        private static int Stage(StationComponent station, StorageComponent buffer, int filter, int cap)
        {
            if (filter <= 0 || cap <= 0) return 0;

            StationStore[] slots = station.storage;

            if (slots == null) return 0;

            // <b>只补差额，不是每 tick 再加一笔。</b>
            //
            // 摆台的量本来就该以「这一 tick 最多派得出去多少」为上限，而原先是
            // 无条件再搬 cap 件上来。正常情况下后置会把台子清空，所以看不出区别；
            // <b>一旦退不回去（槽位满 / 空格 max 还是 0），中转台就会无界增长</b>——
            // 实测抓到 天工装配厂 ×290000 卡在台上，那是 cap 的好几倍。
            //
            // 改成「台上已经有多少就少搬多少」之后，台面存量构造上不超过 cap，
            // 退不回去最多也只是停在那个上限，不会再把槽位一点点抽干。
            int already = buffer.GetItemCount(filter);
            int want = cap - already;

            if (want <= 0) return 0;

            for (var s = 0; s < slots.Length; s++)
            {
                if (slots[s].itemId != filter || slots[s].count <= 0) continue;

                return Move(ref slots[s], buffer, filter, want);
            }

            return 0;
        }

        /// <summary>这一轮报告周期里摆上台的总件数。</summary>
        private static int _stagedSinceReport;

        /// <summary>这一轮报告周期里「一件都没摆上去」的次数。</summary>
        private static int _stageMissSinceReport;

        /// <summary>
        /// 这一轮报告周期里「机甲根本不缺这种货，所以没摆」的次数。
        ///
        /// <b>和上面那个必须分开记。</b> 「不用摆」是正常稳态，「想摆却摆不上」是故障，
        /// 合成一个计数就分不开——本仓库为「一个计数盖住好几种拒绝理由」栽过两次
        /// （<c>CanBatch</c> 四种、<c>IsSteadyUnit</c> 六种），拆开之后各自一次就定位了。
        /// </summary>
        private static int _stageNoNeedSinceReport;

        /// <summary>
        /// 从一个物流站槽位往中转台搬货，返回真的搬走了多少。
        ///
        /// <b>扣的是 AddItem 吃下的那一份，不是想搬的那一份</b>——和 remainInc 完全同构。
        /// </summary>
        private static int Move(ref StationStore slot, StorageComponent buffer, int itemId, int want)
        {
            if (want > slot.count) want = slot.count;

            if (want <= 0) return 0;

            // 按比例带走增产点数——只扣数量不扣 inc 等于凭空增产
            int inc = (int)((long)slot.inc * want / slot.count);

            // **把这一笔的品质写进侧信道，再调 AddItem。**
            // preloader 把它改写成了「从 ProjectEdenQualityChannel 读品质」，
            // 协议是调用方在调用前写——而这条协议它只在游戏自己的调用点上接好了。
            // 不写的话它消费的是上一个人留下的值，品质会凭空长出来。
            //
            // 这里能把品质真的送过去（而不是像传送带那几条路那样丢掉），
            // 因为两头都是有品质槽位的容器。
            int qua = QuaShareOf(slot, want);

            if (QualityAccess.SetChannel0 != null) QualityAccess.SetChannel0(qua);

            int added = buffer.AddItem(itemId, want, inc, out int remainInc, false);

            // **没吃下的那部分还留在寄存器里**（部分入库时 AddItem 走的是按比例的 Split），
            // 所以真正被带走的是差额。读完清掉。
            int remainQua = QualityAccess.ChannelReady ? QualityAccess.GetChannel0() : 0;

            if (QualityAccess.ChannelClearable) QualityAccess.ClearChannel();

            if (added <= 0) return 0;

            slot.count -= added;
            slot.inc -= inc - remainInc;

            int usedQua = qua - remainQua;

            if (usedQua > 0 && QualityAccess.Ready)
                QualityAccess.SetStationQua(ref slot, QualityAccess.GetStationQua(ref slot) - usedQua);

            return added;
        }

        /// <summary>
        /// 这一站的槽位容量——取所有槽位里最大的那个 <c>max</c>。
        ///
        /// <para>只给 <see cref="DrainAll"/> 在「挑中的是个空格、而空格的 max 还是 0」
        /// 时兜底用。不写死 <c>stations.json</c> 的 slotCapacity，是因为那是<b>默认值不是锁</b>
        /// ——每一格的容量玩家都能在面板上自己改，写死会和玩家改过的值对不上。
        /// 同一站的其它槽位是就地可得、且一定被引导过的参照。</para>
        ///
        /// <para>一个也找不到（整站 max 全是 0，只可能发生在引导之前的头几 tick）就返回 0，
        /// 于是这一笔这一 tick 先不退，下一 tick 再试——不会丢货。</para>
        /// </summary>
        private static long SlotCapacity(StationStore[] slots)
        {
            long max = 0;

            for (var s = 0; s < slots.Length; s++)
                if (slots[s].max > max)
                    max = slots[s].max;

            return max;
        }

        /// <summary>已经为「那一格满了、只好让它超过 max」报过一行的货。<b>每种货一次</b>。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte>
            OverflowExplained = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

        /// <summary>
        /// 某一格被退货顶过 <c>max</c> 时报一行。
        ///
        /// <b>不能让它无声发生。</b> 面板上会出现「10,005,200 / 10,005,000」这种读数，
        /// 玩家看到会当成 bug；而真正该被知道的那件事——「枢纽这一格已经装不下了，
        /// 机甲还在往回还货」——不说就没人知道。
        ///
        /// 并行 tick 上的一次性日志要用并发容器抢，不能用普通 bool：
        /// 每条线程都会看到未置位的标志，于是一起打。
        /// </summary>
        private static void ExplainOverflowOnce(int itemId)
        {
            if (!OverflowExplained.TryAdd(itemId, 0)) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"综合物流枢纽：{LDB.items.Select(itemId)?.name ?? itemId.ToString()} 那一格已经满了，"
                + "而机甲还在往回还这种货，于是让它超过了格容量上限（面板上会显示成超出）。"
                + "这是有意的：另开一格会凭空多出一个「仓储」方向的槽位，留在中转台上会把台子塞死。"
                + "超过上限之后本地需求量变成负数，运输机自己就不再往这一格送了；"
                + "想把多的推出去，把这一格改成「供应」即可。这行每种货只报一次。");
        }

        /// <summary>
        /// 退货时该往哪一格放，以及那一格还剩多少空间。
        ///
        /// <para><b>必须带着「还剩多少空间」一起挑，这是 1.12.11 修的那个洞。</b>
        /// 原先是「找到同物品的格子就返回」——可那一格<b>满了的时候也返回</b>，
        /// 于是 <c>room = 0</c>、这一笔退不回去，而下一 tick 摆台又搬一笔上来。
        /// 枢纽的槽位容量是一千万且由物流网持续补满，所以「同物品的那一格是满的」
        /// 恰恰是常态，不是边角情况：实测 天工装配厂 ×290000 就这样卡在台上，
        /// 一直卡到把中转台塞爆、连当前服务的那种货都摆不上去，**配送整个停摆**。</para>
        ///
        /// <para><b>而「满了就另开一格」是 1.12.11 收回去的，那是上一版留下的洞。</b>
        /// 玩家报的症状是「给伊卡洛斯供货的那一格，站里会凭空多出一格同样的货，
        /// 还自动设成了本地仓储 / 远程仓储」——那一格正是这里开的，
        /// 而空格子的两个方向默认就是仓储。根因在摆台那一侧（见
        /// <see cref="MechaShortfall"/>：原先每 tick 搬 10 万件、只送得出几百件），
        /// 那边改完之后退不回去的量在稳态下是 0。</para>
        ///
        /// <para>所以现在的顺序是：同物品且还有空间 → 那一格；<b>同物品但满了 → 还是那一格，
        /// 让它超过 max</b>；这一站压根没有这种货的格子 → 给它一个空格（不给就永远回不去）；
        /// 一个空格都没有 → -1，这一笔先留在台上。</para>
        ///
        /// <para><b>「满了就溢进同一格」是离线跑出来的，不是拍的</b>（<c>tools/sim_hubtray.py</c>）。
        /// 另外两个选项都试过、都不行：另开一格就是玩家报的症状；「留在台上等那一格腾空间」
        /// 看着最干净，可那一格是<b>本地需求</b>格、物流网只会把它填得更满，永远腾不出空间——
        /// 模型跑 3600 tick，中转台涨到 <b>299,500 / 300,000</b>，那正是 1.12.10 修掉的
        /// 「天工装配厂 ×290000 卡在台上」换了个形态，而且这次会把别的货也一起挡住。</para>
        ///
        /// <para><b>超过 max 是安全的，而且是自我纠正的。</b>
        /// <c>localDemandCount = max - (count + localOrder)</c>，<c>count</c> 越过 <c>max</c>
        /// 之后它变成负数，而原版派机对「需求 ≤ 0」的处理就是跳过——
        /// <b>运输机自己就不再往这一格送这种货了</b>。面板上会显示成
        /// 「10,005,200 / 10,005,000」，略微难看，但那是<b>真的</b>：枢纽确实握着这么多。
        /// 想推出去把这一格改成供应即可。货一件不丢，也不会多出一格。</para>
        /// </summary>
        private static int FindDrainSlot(StationStore[] slots, int itemId, out long room)
        {
            var empty = -1;
            var full = -1;

            for (var s = 0; s < slots.Length; s++)
            {
                if (slots[s].itemId == itemId)
                {
                    long left = (long)slots[s].max - slots[s].count;

                    if (left > 0)
                    {
                        room = left;

                        return s;
                    }

                    // 满了——记下来，但先继续找找有没有另一格同样的货还有空间
                    if (full < 0) full = s;

                    continue;
                }

                if (empty < 0 && slots[s].itemId == 0) empty = s;
            }

            // 这种货有格子、只是全满了：溢进第一格，别另开一格。
            // 上限取 Int32 余量，纯粹是防 count 溢出成负数——真要撞到它，
            // 说明有别的东西坏了，而不是这里该处理的情况
            if (full >= 0)
            {
                ExplainOverflowOnce(itemId);

                room = int.MaxValue - (long)slots[full].count;

                return room > 0 ? full : -1;
            }

            if (empty < 0)
            {
                room = 0;

                return -1;
            }

            // 空格子的 max 往往还是 0（容量是指派物品时才写的），按同站其它槽位兜底
            long max = slots[empty].max;

            if (max <= 0) max = SlotCapacity(slots);

            room = max - slots[empty].count;

            return room > 0 ? empty : -1;
        }

        // ── 三、别让中转台抢走点击建筑时的面板 ────────────────

        /// <summary>供 IL 调用：枢纽实体返回 0，其余原样返回。</summary>
        internal static int FilterHubId(int id, PlanetFactory factory, int objId)
        {
            if (id <= 0 || factory?.entityPool == null) return id;
            if (objId <= 0 || objId >= factory.entityPool.Length) return id;

            EntityData entity = factory.entityPool[objId];

            // 枢纽 = 物流站 + 配送器 + 中转台三件套；别误伤原版的储物仓和配送器
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
        /// 代价是这台建筑点不开储物仓面板，但中转台本来就是实现细节，不该露给玩家。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIGame), nameof(UIGame.OnPlayerInspecteeChange))]
        private static IEnumerable<CodeInstruction> UIGame_OnPlayerInspecteeChange_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo filter = AccessTools.Method(typeof(HubCourierPatches), nameof(FilterHubId));

            var matcher = new CodeMatcher(instructions);
            var patched = 0;

            // 中转台排在最前、配送器排在物流站之后，两支都会把物流站窗口顶掉，所以都要挡
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
    /// 让枢纽自带的中转台<b>对玩家完全隐形</b>。
    ///
    /// <b>症状：往枢纽里放不进物流运输机。</b> PlanetFactory.EntityFastFillIn 里
    /// <c>storageId</c> 的判断排在<b>最前面</b>（比 stationId 早），所以玩家塞进这台建筑的
    /// 任何东西——包括小飞机——都先落进中转台，物流站那一支根本走不到。
    /// EntityFastTakeOut 同理，取出来的也是中转台里的东西。
    ///
    /// 中转台是给配送运输机当货源的实现细节，不该出现在玩家的取放路径上，
    /// 所以把这两个方法里那句入口判断的 storageId 过滤成 0，让它们直接跳过储物仓分支。
    ///
    /// <b>拆除和清空那两条不能过滤</b>（TakeBackItemsInEntity / ClearItemsInEntity）——
    /// 中转台里的货得还给玩家，过滤掉就凭空消失了。
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
                    "综合物流枢纽会把玩家放进去的东西吞进中转台，放不进物流运输机。");
            else
                ProjectEdenPlugin.Log.LogInfo($"{original.Name}：已让综合物流枢纽的中转台避开玩家取放，接管 {count} 处");

            return code;
        }
    }
}