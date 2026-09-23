using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 垃圾箱：一台<b>只进不出</b>的物流站，每 tick 把自己的储物格清空。
    ///
    /// <para><b>为什么借物流站，而不是做一个独立的小建筑。</b>
    /// 这个 mod 的废料堆在物流网里，不在某一台机器旁边——巨型建筑的副产物直接进槽位，
    /// 行星之间靠运输船搬。所以「回收不需要的产物」这件事，缺的从来不是一个容器，
    /// 而是一条<b>能把废料收过来</b>的路。物流站自带三条：传送带口、行星内运输机、
    /// 星际运输船。一格设成本地需求就是「把全行星的这种货收过来」，设成远程需求就是
    /// 跨星系收。于是垃圾箱一行喂料逻辑都不用写，全是原版的。</para>
    ///
    /// <para><b>纯销毁，不产出任何东西</b>（所有者的决定）。给产物就等于开了一条
    /// 「废料 → 某种资源」的通路，而 10000× 的工厂里废料是无限的——那是个无中生有的口子，
    /// <c>EnergyAudit</c> 那一套防的正是这种形状。销毁量记进本行星的
    /// <c>consumeRegister</c>，所以生产面板上看得见扔掉了什么、扔了多少。</para>
    ///
    /// <para><b>翘曲器那一格不清，这一条是读 IL 读出来的，不是想出来的。</b>
    /// <c>StationComponent.InternalTickRemote</c> @0044–007B 的形状是
    /// 「扫 <c>storage</c> 找 <c>itemId == 1210</c>，每 tick 搬一个进 <c>warperCount</c>」——
    /// 翘曲器是<b>走储物格进来的</b>，不是走那个独立的翘曲器框。跟着一起清掉的话，
    /// 垃圾箱自己的星际船永远攒不够翘曲器，而玩家看到的症状是「它跨星系收货特别慢」，
    /// 指不到病因上。<see cref="StationComponent.WARPER_ITEMID"/> 是个 <c>const</c>
    /// （<c>HasConstant=True, Constant=1210</c>），所以这里写它和写 1210 生成的是同一条指令——
    /// 写名字只是为了让下一个人知道它是什么。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class DustbinPatches
    {
        /// <summary>
        /// 哪些建筑是垃圾箱。<b>只在 <see cref="OnPostAddData"/> 里写一次，之后全程只读</b>，
        /// 所以并行 tick 上用普通 <c>HashSet</c> 是安全的（和 <c>TargetProtoIds</c>、
        /// <c>ChargePowerByProto</c> 同一类）。
        /// </summary>
        private static readonly HashSet<int> TargetProtoIds = new HashSet<int>();

        /// <summary>每 10 秒报一行销毁量。平时关着，见 machines.json 的 voidDebugLog。</summary>
        private static bool _debugLog;

        /// <summary>
        /// 多久兜底重扫一次站点列表，单位 tick。
        ///
        /// <b>为什么光看 <c>stationCursor</c> 不够。</b> 拆掉一座物流站时
        /// <c>RemoveStationComponent</c> 只是把它挂进回收链，<c>stationCursor</c> 不动；
        /// 下一座新建的站点会<b>复用那个下标</b>，于是「游标变了＝有新站点」这条判据
        /// 在这一种情况下是假的。兜底重扫把它补上：代价是每 2 秒一趟 O(站点数)，
        /// 在八千站点的星球上也就几毫秒每 20 秒——而每 tick 扫一遍是本文件记过的那笔
        /// 604 毫秒／20 秒的账。
        /// </summary>
        private const int RescanTicks = 120;

        /// <summary>每颗星球一份。<b>一颗星球就是一个工作项、一条线程</b>，所以内部字段不用加锁。</summary>
        private sealed class PlanetVoid
        {
            /// <summary>建这份列表时的 <c>stationCursor</c>。-1 表示还没建过。</summary>
            internal int Cursor = -1;

            /// <summary>下一次兜底重扫的 tick。</summary>
            internal long NextScan;

            /// <summary>这颗星球上垃圾箱的站点号。</summary>
            internal int[] Stations = EmptyStations;

            /// <summary>上一个报告窗口里销毁了多少件。</summary>
            internal long Voided;

            /// <summary>这颗星球是不是已经打过「开始工作」那一行了。</summary>
            internal bool FirstLogged;
        }

        private static readonly int[] EmptyStations = new int[0];

        private static readonly ConcurrentDictionary<int, PlanetVoid> Planets =
            new ConcurrentDictionary<int, PlanetVoid>();

        /// <summary>
        /// 收集配成垃圾箱的建筑，并且<b>无论有没有都打一行状态</b>。
        ///
        /// 挂在 PostAddDataAction 而不是 Awake：<c>MachineRegistry</c> 是在
        /// PreAddDataAction 里才把 <c>Machines</c> 填出来的，Awake 那会儿它还是空的，
        /// 状态行会理直气壮地报「一个都没有」。
        /// </summary>
        internal static void OnPostAddData()
        {
            TargetProtoIds.Clear();
            Planets.Clear();
            _debugLog = false;

            foreach (int itemId in MachineRegistry.DustbinItemIds) TargetProtoIds.Add(itemId);

            foreach (MachineEntry entry in MachineRegistry.Config?.machines ?? new MachineEntry[0])
                if (entry?.enabled == true && entry.station?.voidItems == true && entry.station.voidDebugLog)
                    _debugLog = true;

            if (TargetProtoIds.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "垃圾箱：machines.json 里没有任何一条配了 station.voidItems，本局不销毁任何东西");

                return;
            }

            var names = "";

            foreach (int itemId in TargetProtoIds)
                names += (names.Length > 0 ? "、" : "") + (LDB.items.Select(itemId)?.name ?? itemId.ToString())
                         + $"({itemId})";

            ProjectEdenPlugin.Log.LogInfo(
                $"垃圾箱已接上：{names}。它的储物格每 tick 清空，销毁量记进生产面板的消耗栏；"
                + $"翘曲器那一格不清（原版靠储物格补 warperCount）。"
                + $"每 10 秒的销毁流水{(_debugLog ? "已打开" : "默认关着，要看就开 machines.json 的 voidDebugLog")}");
        }

        /// <summary>
        /// 清空。挂 <c>PlanetTransport.GameTick</c>——单线程和多线程两条路最终都会调到它。
        ///
        /// 这个后置是<b>并行的</b>（<c>FactoryTransportGameTick_Parallel</c>，约 31 条线程，
        /// 每条一颗星球），所以：跨星球的状态放 <see cref="ConcurrentDictionary"/>，
        /// 单颗星球内部的字段不用加锁，而碰 <c>station.storage</c> 时取<b>原版自己那把锁</b>
        /// （<c>InternalTickRemote</c> @0037 就是 <c>Monitor.Enter(storage)</c>）。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void PlanetTransport_GameTick(PlanetTransport __instance)
        {
            // 诊断打点：放在一切 return 之前（理由见 StationCapacityPatches 同一行）
            Diagnostics.TransportSplitProbe.Phase("垃圾箱清空");

            if (TargetProtoIds.Count == 0) return;

            PlanetFactory factory = __instance.factory;

            if (factory == null || __instance.stationPool == null) return;

            EntityData[] entityPool = factory.entityPool;

            if (entityPool == null) return;

            int planetId = factory.planetId;
            long tick = GameMain.gameTick;

            PlanetVoid state = Planets.GetOrAdd(planetId, _ => new PlanetVoid());

            int cursor = __instance.stationCursor;

            if (state.Cursor != cursor || tick >= state.NextScan)
            {
                Rebuild(state, __instance, entityPool, cursor);

                state.NextScan = tick + RescanTicks;
            }

            int[] ids = state.Stations;

            if (ids.Length > 0)
            {
                int[] consume = ConsumeRegister(factory);
                long voided = 0;
                var kinds = 0;
                var sample = 0;

                for (var k = 0; k < ids.Length; k++)
                {
                    int id = ids[k];
                    StationComponent station = __instance.stationPool[id];

                    // 站点被拆了之后 Reset() 会把 id 清零、storage 置空，但它仍然留在
                    // stationPool 里（只是挂进了回收链）。所以这三个判据缺一不可。
                    if (station == null || station.id != id || station.storage == null) continue;

                    StationStore[] storage = station.storage;

                    lock (storage)
                    {
                        for (var s = 0; s < storage.Length; s++)
                        {
                            int itemId = storage[s].itemId;

                            if (itemId <= 0) continue;

                            // 翘曲器是走储物格进 warperCount 的，清掉等于让它永远没法跃迁
                            if (itemId == StationComponent.WARPER_ITEMID) continue;

                            int count = storage[s].count;

                            if (count <= 0) continue;

                            storage[s].count = 0;

                            // 件数和增产点必须一起清。只清件数的话，剩下的 0 件顶着一整摞的
                            // 喷涂点数，下一批货进来就白捡——本仓库在虚拟物流那边栽过这一条。
                            storage[s].inc = 0;

                            voided += count;
                            kinds++;

                            if (sample == 0) sample = itemId;

                            if (consume != null && itemId < consume.Length) consume[itemId] += count;
                        }
                    }
                }

                if (voided > 0)
                {
                    state.Voided += voided;

                    FirstVoidOnce(state, planetId, voided, kinds, sample);
                }
            }

            Report(state, planetId, tick);
        }

        /// <summary>
        /// 重建这颗星球的垃圾箱站点号。
        ///
        /// 这里会分配（<c>List</c> + 数组），所以<b>绝不能每 tick 跑</b>——调用方那两道闸
        /// （游标变了 / 兜底到期）就是为这件事设的。
        /// </summary>
        private static void Rebuild(PlanetVoid state, PlanetTransport transport, EntityData[] entityPool, int cursor)
        {
            List<int> found = null;

            for (var i = 1; i < cursor; i++)
            {
                StationComponent station = transport.stationPool[i];

                if (station == null || station.id != i) continue;

                int entityId = station.entityId;

                if (entityId <= 0 || entityId >= entityPool.Length) continue;

                if (!TargetProtoIds.Contains(entityPool[entityId].protoId)) continue;

                (found ?? (found = new List<int>())).Add(i);
            }

            state.Stations = found == null ? EmptyStations : found.ToArray();
            state.Cursor = cursor;
        }

        /// <summary>
        /// 本行星的消耗统计寄存器。拿不到就返回 null——统计断了不该把销毁也一起断掉。
        ///
        /// <c>index</c> 是<b>属性不是字段</b>（<c>GameLogic._assembler_parallel</c> @012E 是
        /// <c>callvirt get_index()</c>），本仓库为「先分清字段还是属性」付过一次账。
        /// 两个寄存器都是 <c>new int[12000]</c>（<c>FactoryProductionStat.InitRegister</c>
        /// @0001/@0011），按物品 ID 直接下标，本 mod 的 6000 号段稳稳在里面。
        /// </summary>
        private static int[] ConsumeRegister(PlanetFactory factory)
        {
            FactoryProductionStat[] pool = GameMain.statistics?.production?.factoryStatPool;

            if (pool == null) return null;

            int index = factory.index;

            if (index < 0 || index >= pool.Length) return null;

            return pool[index]?.consumeRegister;
        }

        /// <summary>
        /// 每颗星球第一次真的销毁东西时打一行，<b>这一行不受 voidDebugLog 控制</b>。
        ///
        /// 少了它，日志里就只有一条开机状态行，而「补丁没生效」和「玩家还没往格子里配货」
        /// 长得一模一样——本文件记过七次的那条：状态行回答「接上了没有」，
        /// 事件行回答「它决定了什么」，谁都替不了谁。
        /// </summary>
        private static void FirstVoidOnce(PlanetVoid state, int planetId, long voided, int kinds, int sampleItemId)
        {
            if (state.FirstLogged) return;

            state.FirstLogged = true;

            string sample = LDB.items.Select(sampleItemId)?.name ?? sampleItemId.ToString();

            ProjectEdenPlugin.Log.LogInfo(
                $"垃圾箱开始工作：行星 {planetId} 这一 tick 销毁 {voided} 件、{kinds} 格（例如 {sample}）。"
                + "这一行每颗星球只打一次");
        }

        /// <summary>每 10 秒一行销毁流水。默认关着，排查时才开。</summary>
        private static void Report(PlanetVoid state, int planetId, long tick)
        {
            if (tick % 600 != 0) return;

            long voided = state.Voided;

            state.Voided = 0;

            if (!_debugLog) return;

            // 没有垃圾箱的星球每 10 秒也来一行就成刷屏了
            if (state.Stations.Length == 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"垃圾箱：行星 {planetId} 有 {state.Stations.Length} 座，本 10 秒销毁 {voided} 件");
        }
    }
}
