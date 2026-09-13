#pragma warning disable 649 // 配置类的字段由 JSON 反序列化赋值

using System;
using System.Threading;
using ProjectEden.Utils;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>data/catalyst.json 的映射。字段说明写在 JSON 里。</summary>
    [Serializable]
    internal class CatalystConfig
    {
        public bool enabled;
        public int reactorItemId;
        public string catalystRef;
        public string spentRef;
        public int chargeSize;
        public int slotCapacity;
        public int ticksPerCharge;
        public bool debugLog;
    }

    /// <summary>
    /// 催化反应器的催化剂床。<b>本 mod 第一条有状态的生产</b>——机器记得自己床里
    /// 那批催化剂还剩多少活性，跑到失活就停下、把待生催化剂吐进物流网，
    /// 等再生炉烧完碳送回来。
    ///
    /// 三件事决定了它长成现在这样，每一件都是仓库里已经付过学费的教训：
    ///
    /// <b>1. 进出走物流站仓位，不碰配方数组。</b>
    /// <c>AssemblerComponent.Export</c> 按 <c>recipeExecuteData.products</c> 的<b>长度</b>
    /// 决定写多少条 <c>produced</c>——改值安全，<b>改长度坏档</b>。而待生催化剂是
    /// 「跑很久才吐一次」，做成配方的第二产物就只剩两条路：动态改长度（坏档），
    /// 或常年挂一个 <c>count = 0</c> 的产物格（界面上永远显示一个 0）。
    /// 巨型建筑本来就是行星内物流站，让它多占两格就行，配方数组一个字节不动。
    ///
    /// <b>2. 那两格必须由 <see cref="MegaStationPatches"/> 的布局代码自己排。</b>
    /// 本来打算照 <c>AlienVeinPatches.EnsureBitSlot</c> 那样从 tick 路径上把仓位填出来，
    /// 读了 <c>SyncStorageLayout</c> 才发现这条路是死的：它排完 requires / products 之后，
    /// 会把后面所有格子清空，唯一的赦免是 <c>count &gt; 0</c>——
    /// 而<b>催化剂槽恰恰要在空的时候存在</b>（空着才是在向物流网要货）。
    /// 每 tick 被我们自己的代码擦掉一次，症状是「反应器永远等不到催化剂」，
    /// 病因却在一个名字里没有「催化剂」三个字的方法里。所以这里提供
    /// <see cref="LayoutSlots"/>，让它成为布局的一部分，而不是去和布局抢。
    ///
    /// <b>3. 只在真的产出了的 tick 才扣活性。</b> 断电、缺料、产物槽满这三种情况下
    /// 原版本来就不结算，跟着扣的话就是「机器停着、催化剂照烧」——
    /// 钻头那次玩家报的原话。一个跑在原版<b>决定要不要动手之前</b>的钩子，
    /// 必须复现它的决定，而不是标称速率。
    /// </summary>
    internal static class CatalystBedPatches
    {
        internal static CatalystConfig Config;

        internal static int CatalystId { get; private set; }

        internal static int SpentId { get; private set; }

        /// <summary>配置在、开关开着、两个物品都解析出来了，才算就绪。</summary>
        internal static bool Ready { get; private set; }

        private static int _loggedFirstCharge;
        private static int _loggedFirstEject;
        private static int _loggedDerivedGate;
        private static float _nextDebug;

        // ── 注册期：解析物品号并报状态 ────────────────────────

        /// <summary>
        /// <b>三种状态都要报。</b> 只在「开着」时才打印的话，「关掉了」和
        /// 「这段代码根本没进 DLL」在日志里长得一模一样——这条规矩本仓库已经
        /// 付过三次学费（<c>AlloyRatioPatches.ReapplyAll</c>、<c>ReportCheats</c>、
        /// <c>ReportCargoProbe</c>）。
        /// </summary>
        internal static void OnPostAddData()
        {
            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogWarning("催化剂床：读不到 data/catalyst.json，反应器不需要催化剂就能跑");

                return;
            }

            if (!Config.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo("催化剂床：已在 catalyst.json 里关闭，反应器不需要催化剂就能跑");

                return;
            }

            CatalystId = OreRegistry.FindItemIdByRef(Config.catalystRef);
            SpentId = OreRegistry.FindItemIdByRef(Config.spentRef);

            if (CatalystId <= 0 || SpentId <= 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"催化剂床：物品解析失败（{Config.catalystRef}={CatalystId}，{Config.spentRef}={SpentId}），" +
                    "本功能整体停用——反应器会变成不吃催化剂的普通巨型建筑");

                return;
            }

            Ready = true;

            ProjectEdenPlugin.Log.LogInfo(
                $"催化剂床已就绪：建筑 {Config.reactorItemId}，催化剂 {CatalystId}／待生 {SpentId}，" +
                $"一床 {Config.chargeSize} 份、撑 {Config.ticksPerCharge:N0} 个产出 tick" +
                $"（满负荷约 {Config.ticksPerCharge / 60.0:0.#} 秒），" +
                $"催化剂槽容量 {Config.slotCapacity:N0}");
        }

        // ── 仓位：由 SyncStorageLayout 调用，成为布局的一部分 ──

        /// <summary>
        /// 给催化反应器在 requires / products 之后再排两格：催化剂（本地需求）、待生（本地供应）。
        ///
        /// 返回是否改动过。<b>必须沿用调用方的 <c>changed</c> 语义</b>——
        /// <c>RefreshStationTraffic</c> 要遍历整颗行星的物流站，只能在真的变了时调。
        /// </summary>
        internal static bool LayoutSlots(StationComponent station, ref int cursor, int length)
        {
            if (!Ready) return false;

            var changed = false;

            changed |= SetSlot(station, ref cursor, length, CatalystId, ELogisticStorage.Demand, Config.slotCapacity);
            changed |= SetSlot(station, ref cursor, length, SpentId, ELogisticStorage.Supply, Config.slotCapacity);

            return changed;
        }

        /// <summary>
        /// 这两格是不是催化剂床的。<see cref="StationCapacityPatches"/> 用它来跳过——
        /// 那边每 tick 把物流站每一格的 <c>max</c> 刷成一千万，刷到催化剂槽上
        /// 就等于让第一座反应器把全网的催化剂吸光。
        /// </summary>
        internal static bool OwnsSlot(int itemId) =>
            Ready && itemId > 0 && (itemId == CatalystId || itemId == SpentId);

        private static bool SetSlot(StationComponent station, ref int cursor, int length,
            int itemId, ELogisticStorage logic, int max)
        {
            if (cursor >= length) return false;

            int i = cursor++;

            bool changed = station.storage[i].itemId != itemId || station.storage[i].localLogic != logic;

            station.storage[i].itemId = itemId;
            station.storage[i].localLogic = logic;
            station.storage[i].remoteLogic = ELogisticStorage.None;

            // 容量每 tick 重申一次。StationCapacityPatches 现在会跳过这两格，
            // 但「每 tick 重申」比「相信没人来动」便宜得多——一次 int 比较而已，
            // 而赌错的代价是整条催化线静默失效。
            if (station.storage[i].max != max) station.storage[i].max = max;

            return changed;
        }

        // ── tick：装料、失活、吐料、停转 ──────────────────────

        /// <summary>
        /// 在原版结算<b>之前</b>跑。返回 false 表示这一 tick 不许生产——
        /// 调用方会跳过补跑的周期，而原版自己那一次由 <see cref="MegaLightPatches.Suppress"/> 压住
        /// （前置钩子取消不了它后面那次调用，只能把 time 预置成怎么加都够不到 timeSpend）。
        /// </summary>
        internal static bool Gate(PlanetFactory factory, ref AssemblerComponent component)
        {
            if (!Ready) return true;
            if (!IsReactor(factory, component.entityId)) return true;

            int planetId = factory.planet?.id ?? 0;
            int entityId = component.entityId;

            CatalystBedStore.TryGetBed(planetId, entityId, out int[] bed);

            // 床还活着，什么都不用做——这是绝大多数 tick 走的路，要最短
            if (bed != null && bed[0] > 0 && bed[1] > 0) return true;

            StationComponent station = Station(factory, entityId);

            if (station?.storage == null)
            {
                MegaLightPatches.Suppress(ref component);

                return false;
            }

            lock (station.storage)
            {
                // 1) 失活的那一床先吐出去。吐不掉（待生仓满）就停在这儿——
                //    停产是对的，继续跑等于把催化剂凭空烧掉。
                if (bed != null && bed[0] > 0)
                {
                    if (!PushSpent(station, bed[0]))
                    {
                        MegaLightPatches.Suppress(ref component);

                        return false;
                    }

                    CatalystBedStore.Remove(planetId, entityId);

                    // 先抢标志位再拼字符串：插值本身就是一次分配，留在 tick 路径上
                    // 等于每次换料都白分配一个字符串，哪怕永远不打印
                    if (Interlocked.Exchange(ref _loggedFirstEject, 1) == 0)
                        ProjectEdenPlugin.Log.LogInfo(
                            $"催化剂床：首次失活换料——{bed[0]} 份待生催化剂已推进供应格，等再生");
                }

                // 2) 装新的一床。**要整批，不接受半床**——半床会让活性和装填量脱钩，
                //    面板上「还能跑多久」立刻失去意义。
                if (!TakeCharge(station, Config.chargeSize))
                {
                    MegaLightPatches.Suppress(ref component);

                    return false;
                }
            }

            CatalystBedStore.Set(planetId, entityId, Config.chargeSize, Config.ticksPerCharge);

            if (Interlocked.Exchange(ref _loggedFirstCharge, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"催化剂床：首次装填——{Config.chargeSize} 份，可撑 {Config.ticksPerCharge:N0} 个产出 tick");

            return true;
        }

        /// <summary>
        /// 在原版结算<b>之后</b>跑，扣掉这一 tick 的活性。
        ///
        /// <paramref name="settled"/> 是<b>实测</b>的结算周期数（补跑那几遍里真的出了货的），
        /// <paramref name="derived"/> 是没有补跑周期可测时按原版判据推出来的结果。
        /// 实测优先——它不会因为原版将来多一道闸而失准；推导是 <c>cyclesPerTick = 1</c> 时的退路。
        /// </summary>
        internal static void Settle(PlanetFactory factory, ref AssemblerComponent component, int settled, bool derived)
        {
            if (!Ready) return;
            if (!IsReactor(factory, component.entityId)) return;

            bool produced = settled > 0 || derived;

            if (!produced) return;

            if (!CatalystBedStore.TryGetBed(factory.planet?.id ?? 0, component.entityId, out int[] bed)) return;

            // 原地减，不重建数组：这是 tick 路径，不许分配
            if (bed[1] > 0) bed[1]--;
        }

        /// <summary>
        /// <c>cyclesPerTick = 1</c> 时没有补跑周期可测，只能复现原版的判据：
        /// 电力够、每种原料都够。<b>产物槽满这一条这里测不到</b>——
        /// 原版那道闸在 <c>InternalUpdate</c> 内部，而我们跑在它之前。
        /// 所以这条退路会在「产物堆满」时多扣一点活性，是已知的、有界的偏差；
        /// 默认配置（60 周期/tick）走的是实测那条，不受影响。
        /// </summary>
        internal static bool LooksProductive(ref AssemblerComponent component, float power)
        {
            if (power < 0.1f) return false;

            int[] served = component.served;
            int[] need = component.recipeExecuteData?.requireCounts;

            if (served == null || need == null) return false;

            for (var i = 0; i < need.Length && i < served.Length; i++)
                if (served[i] < need[i])
                    return false;

            ReportOnce(ref _loggedDerivedGate,
                       "催化剂床：cyclesPerTick = 1，活性扣减改用推导判据（电力 + 原料），" +
                       "产物槽满这一条测不到，会略微多扣");

            return true;
        }

        // ── 面板：只读，玩家不配置任何东西 ────────────────────

        /// <summary>
        /// 给 <see cref="AlloySliderPatches"/> 那块面板取一台反应器的五级状态：
        /// 床里装了多少、还能跑多久、两个仓位各有多少。
        ///
        /// <b>这是那块面板的第六个用户，也是第一个纯只读的。</b> 前五个（合金配比、
        /// 合金弹药、活性复合材、烧结析出、活性增产剂）都要接输入；催化剂由配方决定、
        /// 不给玩家选，所以只有 Refresh，没有 HandleInput——这也是它最便宜的原因。
        ///
        /// <b>它存在的理由是巨型建筑的储物格对玩家不可见。</b>
        /// <c>MegaStationWindowPatches</c> 把这些建筑的 stationId 报成 0，
        /// 好让配方窗口代替物流站窗口打开（否则两个窗口抢 ShutAllFunctionWindow），
        /// 代价就是那 30 格谁也看不到。催化剂床恰恰住在那里面。
        /// </summary>
        internal static bool Current(PlanetFactory factory, int entityId,
            out int charge, out int life, out int stock, out int spent)
        {
            charge = life = stock = spent = 0;

            if (!Ready || !IsReactor(factory, entityId)) return false;

            if (CatalystBedStore.TryGetBed(factory.planet?.id ?? 0, entityId, out int[] bed))
            {
                charge = bed[0];
                life = bed[1];
            }

            StationComponent station = Station(factory, entityId);

            if (station?.storage == null) return true;

            int i = Find(station, CatalystId);
            int j = Find(station, SpentId);

            // −1 表示「这一格根本没排出来」，和「排出来了但是空的」要分得开：
            // 前者是布局出了问题，后者是正常在等货
            stock = i >= 0 ? station.storage[i].count : -1;
            spent = j >= 0 ? station.storage[j].count : -1;

            return true;
        }

        // ── 仓位读写 ──────────────────────────────────────────

        private static bool TakeCharge(StationComponent station, int need)
        {
            int slot = Find(station, CatalystId);

            if (slot < 0 || station.storage[slot].count < need) return false;

            // 催化剂本身通常没有品质，但**不扣就是不变量的漏洞**：万一哪天有一笔品质
            // 落到这一格上，只扣件数会让剩下的催化剂单件分数一路涨上去。
            // 比例要在 count 扣减之前算。
            QualityAccess.TakeStationQua(ref station.storage[slot], need);

            station.storage[slot].count -= need;

            return true;
        }

        private static bool PushSpent(StationComponent station, int amount)
        {
            int slot = Find(station, SpentId);

            if (slot < 0) return false;
            if (station.storage[slot].count + amount > station.storage[slot].max) return false;

            station.storage[slot].count += amount;

            return true;
        }

        private static int Find(StationComponent station, int itemId)
        {
            for (var i = 0; i < station.storage.Length; i++)
                if (station.storage[i].itemId == itemId)
                    return i;

            return -1;
        }

        private static StationComponent Station(PlanetFactory factory, int entityId)
        {
            int stationId = factory.entityPool[entityId].stationId;

            if (stationId <= 0) return null;

            StationComponent station = factory.transport.stationPool[stationId];

            return station != null && station.id == stationId ? station : null;
        }

        internal static bool IsReactor(PlanetFactory factory, int entityId)
        {
            if (Config == null || Config.reactorItemId <= 0) return false;
            if (factory?.entityPool == null || entityId <= 0 || entityId >= factory.entityPool.Length) return false;

            return factory.entityPool[entityId].protoId == Config.reactorItemId;
        }

        // ── 日志 ──────────────────────────────────────────────

        /// <summary>
        /// 一次性日志要先用 <c>Interlocked</c> 抢占标志位<b>再拼字符串</b>：
        /// 组装机 tick 跑在 <c>_assembler_parallel</c> 上，普通的 <c>if (done) return;</c>
        /// 挡不住并发，而字符串插值留在 tick 路径上本身就是分配。
        /// </summary>
        private static void ReportOnce(ref int flag, string message)
        {
            if (Interlocked.Exchange(ref flag, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(message);
        }

        /// <summary>
        /// 每 10 秒把一台反应器的五级状态打成一行。
        ///
        /// <b>用 <c>Time.realtimeSinceStartup</c> 而不是 <c>GameMain.gameTick</c>。</b>
        /// 换存档时 gameTick 会往回跳，<c>next = tick + interval</c> 就再也不会到期，
        /// 报告悄悄停掉——看起来和「一切正常」一模一样。
        ///
        /// 这类多级链条（仓位 → 装填 → 活性 → 吐料 → 再生）每一级出问题的症状都是
        /// 「机器不转」，靠开局日志分不出是哪一级；综合物流枢纽那次为此付了五个来回。
        /// </summary>
        internal static void DebugTick(PlanetFactory factory, ref AssemblerComponent component)
        {
            if (!Ready || Config == null || !Config.debugLog) return;
            if (!IsReactor(factory, component.entityId)) return;

            float now = Time.realtimeSinceStartup;

            if (now < _nextDebug) return;

            _nextDebug = now + 10f;

            int planetId = factory.planet?.id ?? 0;

            CatalystBedStore.TryGetBed(planetId, component.entityId, out int[] bed);

            StationComponent station = Station(factory, component.entityId);
            int cat = -1, spent = -1;

            if (station?.storage != null)
            {
                int i = Find(station, CatalystId);
                int j = Find(station, SpentId);

                cat = i >= 0 ? station.storage[i].count : -1;
                spent = j >= 0 ? station.storage[j].count : -1;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"催化剂床｜{factory.planet?.displayName ?? "?"} 实体 {component.entityId}：" +
                $"床 {(bed == null ? "空" : bed[0] + " 份")}，" +
                $"活性 {(bed == null ? 0 : bed[1])}/{Config.ticksPerCharge}，" +
                $"催化剂槽 {(cat < 0 ? "无此格" : cat.ToString())}，" +
                $"待生槽 {(spent < 0 ? "无此格" : spent.ToString())}，" +
                $"配方 {component.recipeId}");
        }
    }
}
