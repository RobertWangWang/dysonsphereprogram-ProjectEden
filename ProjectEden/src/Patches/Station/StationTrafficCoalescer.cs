using System.Collections.Concurrent;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把「刷新物流供需配对」从<b>立刻做</b>改成<b>标脏 + 限频冲刷</b>。
    ///
    /// <b>为什么需要：这个方法在这个 mod 的星球上是每次 88 毫秒。</b>
    /// <c>PlanetTransport.RefreshStationTraffic</c> 只有 66 条指令，但形状是确定的
    /// <b>O(站点数²)</b>——两个走满 <c>stationCursor</c> 的循环，第一个逐站
    /// <c>ClearLocalPairs()</c>，第二个把<b>整个 stationPool</b> 传进
    /// <c>RematchLocalPairs</c>，也就是每个站点和其它每个站点重配一遍。
    ///
    /// 原版存档上无所谓；而<b>本 mod 让每台巨型建筑同时是物流站</b>，实测这颗星球
    /// 2078 个站点（其中 1175 台巨型建筑），2078² ≈ 430 万次配对。
    ///
    /// 玩家报「一建造就卡」，分段计时把账摊开了：
    /// <code>
    /// 24.5 秒里建了 12 座：BuildFinally 119.68 毫秒、收尾四步 8.29、地形发布 8.06、扫描 0.44
    /// 而 RefreshStationTraffic 被调用 42 次、合计 3706.38 毫秒  ← 占这段墙钟的 15%
    /// </code>
    /// 42 次里约 30 次来自 <c>MegaStationPatches</c>：新建的巨型建筑在随后几个 tick 里
    /// 布局反复变，每变一次就整颗星球重配一次。
    ///
    /// <b>为什么是限频而不是每 tick 合并。</b> 实测这些调用是摊开在几十秒里的
    /// （42 次 / 24.5 秒），同一 tick 内撞在一起的很少，所以单纯的「每 tick 只做一次」
    /// 省不下什么。真正有效的是拉开间隔：配对晚几秒重建，对玩家不可见——
    /// 巨型建筑之间的搬运本来就走 <see cref="MegaVirtualLogisticsPatches"/> 的虚拟物流，
    /// 无人机根本不起飞。
    ///
    /// <b>原版那条路也得合并——这一点我一开始判断错了，是实测纠正的。</b>
    /// 第一版只接管本 mod 自己的调用，理由是「原版按自己的时机调，替它改行为不划算」。
    /// 下一局的自测行直接打脸：<c>标脏 0 次 / 实际刷新 0 次</c>，而
    /// <c>RefreshStationTraffic</c> 照样被调了 25 次、2022 毫秒——**一次都不是我们的**。
    /// 路径是 <c>BuildFinally → BuildingParameters.ApplyPrebuildParametersToEntity @1024
    /// → RefreshStationTraffic</c>：<b>放下一座建筑就整颗星球重配一遍</b>。
    ///
    /// 所以前置改成一律去抖。代价说清楚：**供需配对最多晚 2 秒重建**——
    /// 玩家在面板上改一格、或者拆掉一座站，无人机会多用两秒的旧配对。
    /// 这个延迟在原版是 0，是本 mod 拿它换的 CPU；<c>PlanetTransport.Import</c> 那一次
    /// 不受影响（读档后 <c>last</c> 是 0，下一个 tick 就会立刻冲刷）。
    /// </summary>
    [HarmonyPatch]
    internal static class StationTrafficCoalescer
    {
        /// <summary>
        /// 两次冲刷之间至少隔多少 tick。120 tick = 2 秒。
        ///
        /// 这是个<b>明说的取舍</b>：一次 88 毫秒，2 秒一次就是 4.4% 的 CPU；
        /// 再拉长省得更多，但新建的巨型建筑要更久才会进物流网的供需表。
        /// </summary>
        private const int IntervalTicks = 120;

        /// <summary>哪几颗星球欠一次刷新。<b>并行 tick 上共享，必须是并发容器</b>（见 CLAUDE.md）。</summary>
        private static readonly ConcurrentDictionary<int, byte> Dirty = new ConcurrentDictionary<int, byte>();

        private static readonly ConcurrentDictionary<int, long> LastFlush = new ConcurrentDictionary<int, long>();

        private static int _coalesced;
        private static int _flushed;

        /// <summary>本 mod 改完站点槽位之后调这个，而不是直接调 RefreshStationTraffic。</summary>
        internal static void MarkDirty(PlanetFactory factory)
        {
            if (factory == null) return;

            Dirty[factory.planetId] = 0;
            System.Threading.Interlocked.Increment(ref _coalesced);
        }

        /// <summary>正在由 <see cref="Flush"/> 真调那一次——前置要放行，否则它把自己也挡掉。</summary>
        [System.ThreadStatic] private static bool _flushing;

        /// <summary>
        /// 拦下所有 <c>RefreshStationTraffic</c>，改成标脏，由每星球的 tick 限频冲刷。
        ///
        /// <b>返回 false 就是不执行原方法。</b> 这是本仓库少有的「替原版改时机」的改动，
        /// 理由是实测：这颗星球上它一次 81 毫秒，而<b>放下一座建筑就要调一次</b>
        /// （BuildFinally → ApplyPrebuildParametersToEntity @1024），
        /// 10 秒里 25 次、合计 2 秒，占掉 20% 的 CPU。
        ///
        /// 读档那一次不会被拖住：那时这颗星球的 <c>last</c> 还是 0，
        /// <c>time - 0</c> 必然大于间隔，下一个 tick 就冲刷。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.RefreshStationTraffic))]
        private static bool Defer(PlanetTransport __instance)
        {
            if (_flushing) return true;

            PlanetFactory factory = __instance?.factory;

            if (factory == null) return true;

            MarkDirty(factory);

            return false;
        }

        /// <summary>
        /// 每颗星球自己的 tick 上冲刷。
        ///
        /// 放在这里而不是某个全局定时器，是因为 <c>RefreshStationTraffic</c> 动的是
        /// 这颗星球自己的 <c>stationPool</c>，而 <c>PlanetTransport.GameTick</c>
        /// 正是「一颗星球一个线程」的那条路——同一份数据只会有一个线程碰。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void PlanetTransport_GameTick(PlanetTransport __instance, long time)
        {
            PlanetFactory factory = __instance?.factory;

            if (factory == null) return;

            int planetId = factory.planetId;

            if (!Dirty.ContainsKey(planetId)) return;

            LastFlush.TryGetValue(planetId, out long last);

            // 读档会让 gameTick 往回跳，那时直接放行一次，否则 last 永远大于 time、再也刷不了
            if (time >= last && time - last < IntervalTicks) return;

            if (!Dirty.TryRemove(planetId, out _)) return;

            LastFlush[planetId] = time;

            // 放行标志要包在 finally 里：真刷那一次里要是抛了，标志留着 true
            // 就等于把去抖整个关掉，而且不会有任何迹象
            _flushing = true;

            try
            {
                __instance.RefreshStationTraffic();
            }
            finally
            {
                _flushing = false;
            }

            _flushed++;
        }

        /// <summary>换存档时清空：星球号会重复使用。</summary>
        internal static void Reset()
        {
            Dirty.Clear();
            LastFlush.Clear();
        }

        internal static void Report()
        {
            ProjectEdenPlugin.Log.LogInfo(
                $"物流配对刷新已改为限频合并（至少隔 {IntervalTicks} tick = {IntervalTicks / 60.0:0.#} 秒），" +
                "**包括原版自己发起的那些**。RefreshStationTraffic 是 O(站点数²)——两个走满 " +
                "stationCursor 的循环，第二个把整个 stationPool 传进 RematchLocalPairs。" +
                "本 mod 让每台巨型建筑同时是物流站，站点数远超原版，实测这颗星球上一次 81 毫秒，" +
                "而**放下一座建筑就要调一次**（BuildFinally → ApplyPrebuildParametersToEntity），" +
                "10 秒里 25 次、合计 2 秒。" +
                "**代价：供需配对最多晚 2 秒重建**——面板上改一格、拆一座站之后，" +
                "无人机会多用两秒的旧配对。读档那一次不受影响。");
        }

        /// <summary>合并了多少次、实际刷了多少次——比值就是这条改动省下的倍数。</summary>
        internal static string Stats() => $"标脏 {_coalesced} 次 / 实际刷新 {_flushed} 次";
    }
}
