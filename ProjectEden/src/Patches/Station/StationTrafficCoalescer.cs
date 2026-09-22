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
        private static bool Defer(PlanetTransport __instance, int keyStationId)
        {
            if (_flushing) return true;

            PlanetFactory factory = __instance?.factory;

            if (factory == null) return true;

            MarkDirty(factory);
            RememberKey(factory.planetId, keyStationId);

            // **拆站那一次绝不能延迟**，理由见 _removing 的注释：延迟它会留下悬空引用，
            // 而不是「晚两秒」。这里立刻冲刷，正好是原版的时机（原版就在
            // RemoveStationComponent @02F5 同步调这一次）。
            // 前置里已经把这个站点的配对增量摘干净了，就不必再全量冲刷一次——
            // 那次同步冲刷存在的理由正是「别留下悬空引用」，而它已经被消掉了。
            // 摘不成（_detached 为 false）时照旧同步全量，行为和以前完全一样。
            if (_removing && !_detached) FlushNow(__instance, GameMain.gameTick);

            return false;
        }

        /// <summary>
        /// 正在 <c>PlanetTransport.RemoveStationComponent</c> 里面。
        ///
        /// <b>这是 1.10.6 修掉的一个会让玩家崩游戏的 bug，起因是合并刷新把两种「陈旧」
        /// 混为一谈了。</b>
        ///
        /// <list type="bullet">
        /// <item><b>内容陈旧</b>（某一格改了物品、新放了一座站）—— 晚两秒无害，
        ///       这正是这条优化要换的东西。</item>
        /// <item><b>存在性陈旧</b>（站点没了）—— 是**悬空引用**，不是延迟。</item>
        /// </list>
        ///
        /// 实测链路：<c>RemoveStationComponent</c> @02D1 调 <c>Reset()</c>，而 <c>Reset</c> @00BC
        /// 把 <c>storage</c> 置 null、@0001 把 <c>id</c> 置 0，**组件对象仍然留在
        /// <c>stationPool</c> 里**（只是进了回收表）。紧接着 @02F5 它同步调
        /// <c>RefreshStationTraffic</c> 重建配对表——所以原版永远不会有一条活着的配对
        /// 点名一个已死的站点。
        ///
        /// 而 <c>InternalTickLocal</c> @07CF 的判空只判**对象**、不判 <c>storage</c>：
        /// <code>
        /// 07C1: V_47 = stationPool[pair.supplyId]
        /// 07CF: if (V_47 == null) goto 1175;   // 只判对象
        /// 07D6: V_26 = V_47.storage            // 回收过的站点，这里是 null
        /// 07E4: Monitor.Enter(V_26, ...)       // ArgumentNullException
        /// </code>
        /// 那个判空在原版是**够用的**，因为它依赖 @02F5 的同步重建。我们把那次重建延迟了
        /// 最多 120 tick，于是拆掉一座物流站之后的两秒里，其它站点的 <c>localPairs</c>
        /// 仍然点着它——工作线程一锁就炸。玩家报的正是这个：
        /// <c>ArgumentNullException ... mono_monitor_enter ... DMD&lt;InternalTickLocal&gt;</c>。
        ///
        /// <b>连发派机（1.10.4）不是病因，但它放大了暴露面</b>：原版一帧只摸一对配对，
        /// 现在最多摸 10 对，撞上陈旧配对的概率高一个量级。所以它出现在堆栈上。
        ///
        /// <b>同源还有一个不崩溃的症状</b>，更难发现：<c>stationRecycle</c> 会把同一个下标
        /// 发给新建的站点，于是那两秒里陈旧配对会指向一个毫不相干的新站点——货送错地方，
        /// 一个字不报。修掉拆除这一条就一并关掉了，因为重建之后表里不再有那个下标。
        ///
        /// <b>为什么只特判这一个调用方。</b> 全程序集只有四处调 <c>RefreshStationTraffic</c>：
        /// <c>Import</c>（读档，表是空的，不是悬空）、<c>SetStationStorage</c>（内容）、
        /// <c>ApplyPrebuildParametersToEntity</c>（放建筑，正是这条优化要合并的那一个）、
        /// 以及 <c>RemoveStationComponent</c>。**只有最后一个是存在性变更。**
        ///
        /// <b>代价是明说的</b>：每拆一座物流站付一次全量重建。索引版实测 6.7 毫秒，
        /// 而原版本来就要付 81 毫秒——所以即使逐座重建，**仍然比原版快一个数量级**，
        /// 而「放下建筑」那条路（这条优化真正的目标）一点没变。
        /// </summary>
        [System.ThreadStatic] private static bool _removing;

        /// <summary>这一次拆站的配对已经在前置里增量摘干净了，<see cref="Defer"/> 不必再全量冲刷。</summary>
        [System.ThreadStatic] private static bool _detached;

        /// <summary>
        /// <b>拆站的摘除必须在这里做，不能等到 <c>RefreshStationTraffic</c> 那一刻。</b>
        ///
        /// <para>实测链路：<c>RemoveStationComponent</c> @02D1 调 <c>Reset()</c>，
        /// @02F5 才调 <c>RefreshStationTraffic</c>。而 <c>Reset()</c> 把 <c>id</c> 归零、
        /// <c>storage</c> 置空——<b>但它不动 <c>localPairs</c></b>（枚举过 <c>Reset</c> 的全部指令，
        /// 一条都没有）。所以到了 @02F5，配对数组还在、站号已经没了，
        /// 增量摘除认不出这个站点是谁。前置里它还是完整的。</para>
        ///
        /// <para>摘干净之后就不需要那次同步全量冲刷了——那次冲刷存在的理由正是
        /// 「别留下悬空引用」，而这里已经把悬空引用消掉了。做不了增量时
        /// <c>_detached</c> 保持 false，<see cref="Defer"/> 照旧同步全量，行为不变。</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.RemoveStationComponent))]
        private static void RemoveStation_Prefix(PlanetTransport __instance, int id)
        {
            _removing = true;
            _detached = false;

            if (!LocalPairIndex.Enabled || !LocalPairIndex.IncrementalEnabled) return;

            _detached = LocalPairIndex.DetachOne(__instance, id);
        }

        /// <summary>
        /// 后置里清标志。<b>用后置而不是 try/finally</b>：Harmony 的后置在原方法抛异常时
        /// 不会跑，所以这里额外在 <see cref="Defer"/> 用完之后也不依赖它——
        /// <c>_removing</c> 只在「进了 RemoveStationComponent 又还没出来」这段为真，
        /// 而那段里唯一会读它的就是 <see cref="Defer"/>。万一真漏了一次没清，
        /// 后果是**下一次刷新不再延迟**（退回原版行为），不是崩溃或静默错误——
        /// 失败方向是安全的那一侧。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.RemoveStationComponent))]
        private static void RemoveStation_Postfix()
        {
            _removing = false;
            _detached = false;
        }

        /// <summary>
        /// 每颗星球欠着哪几个 <c>keyStationId</c>。
        ///
        /// <b>这一项是补的，上一版漏了，而漏的方式很典型。</b>
        /// <c>RefreshStationTraffic(int keyStationId = 0)</c> 是<b>可选参数</b>，
        /// 所以上一版写 <c>RefreshStationTraffic()</c> 能编译、能跑、不报错——
        /// 但那等于永远传 0，而 <c>RematchLocalPairs</c> @018C 正是
        /// <c>keyStationId &lt;= 0 → 跳过整个后半段</c>，也就是<b>无人机订单修复从此没再跑过</b>。
        /// 症状会是在飞的运输机订单和槽位的 localOrder 对不上，而且不报任何错。
        ///
        /// 合并之后一次冲刷可能对应好几次原版调用，所以按<b>去重的 key 集合</b>存，
        /// 冲刷时每个 key 各补跑一次——那正是原版逐次调用的语义。
        /// </summary>
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> PendingKeys =
            new ConcurrentDictionary<int, ConcurrentDictionary<int, byte>>();

        /// <summary>
        /// 一次冲刷最多记多少个变更站点。
        ///
        /// <para><b>这个数原来是 32，而那是给全量路径定的——增量上线之后它同时太小、也太危险。</b></para>
        ///
        /// <para><b>太小</b>：实测一个 2 秒窗口里放建筑能攒下 10~20 个，连续建造轻松破 32，
        /// 于是每次都退回全量（实测 32.5 ms），而增量只要 1.14 ms。而增量的单价是
        /// 「一次索引重建 ≈ 2 ms + 每键约 360 条发射」，和全量的盈亏平衡点在
        /// <c>304 万 ÷ 360 ≈ 8,400</c> 个键——32 离它差了两个半数量级。</para>
        ///
        /// <para><b>更危险</b>：以前丢一个键只意味着「那台站点的无人机订单没补跑」，
        /// 配对表反正是全量重建的、不受影响。<b>增量上线之后，丢键意味着那台站点的配对
        /// 永远不会更新</b>——表会对那一台静默地陈旧下去。所以光放大不够，见
        /// <see cref="DeltaIncomplete"/>：真丢了键就强制这一次走全量。</para>
        ///
        /// <para>512 是按「远低于盈亏平衡点、又足够大到实际碰不到」取的；碰到了也安全，
        /// 只是退化成全量。</para>
        /// </summary>
        private const int MaxKeysPerFlush = 512;

        private static int _keyOverflowWarned;

        /// <summary>
        /// 这颗星球的这一批 delta <b>不完整</b>（有键被丢掉了），冲刷时必须走全量。
        ///
        /// <b>「不知道 delta 是什么的时候，唯一安全的 delta 是全部」</b>——
        /// 这条规则在 <c>keys</c> 为空时已经用过一次，丢键是同一种情况的另一种形态，
        /// 而且更隐蔽：<c>keys</c> 非空，看着像个正常的增量批次。
        /// </summary>
        private static readonly ConcurrentDictionary<int, byte> Incomplete = new ConcurrentDictionary<int, byte>();

        internal static bool DeltaIncomplete(int planetId) => Incomplete.ContainsKey(planetId);

        private static void RememberKey(int planetId, int keyStationId)
        {
            if (keyStationId <= 0) return;

            ConcurrentDictionary<int, byte> set =
                PendingKeys.GetOrAdd(planetId, _ => new ConcurrentDictionary<int, byte>());

            if (set.Count >= MaxKeysPerFlush)
            {
                // **标记这一批 delta 不完整，本次冲刷退回全量。** 只警告不标记的话，
                // 增量会拿着一份缺项的 delta 往下算，而那台站点的配对从此静默陈旧。
                Incomplete[planetId] = 0;

                if (System.Threading.Interlocked.Exchange(ref _keyOverflowWarned, 1) == 0)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"物流配对刷新：一次合并窗口里攒了超过 {MaxKeysPerFlush} 个变更站点。" +
                        "**本次冲刷已退回全量重建**（缺项的 delta 会让那几台站点的配对静默陈旧），" +
                        "所以配对表仍然是对的，只是这一次不省时间。整局只报这一行。");

                return;
            }

            set.TryAdd(keyStationId, 0);
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

            FlushNow(__instance, time);
        }

        /// <summary>
        /// 真正冲刷一次，<b>不看限频</b>。
        ///
        /// 从上面那个后置里抽出来的，因为现在有两个调用方：限频的那条（后置），
        /// 和拆站那条不许延迟的（见 <see cref="_removing"/>）。抽出来而不是复制一份，
        /// 是为了让「冲刷」只有一个实现——两份各自演化正是这份文件记过的那类账。
        /// </summary>
        private static void FlushNow(PlanetTransport __instance, long time)
        {
            PlanetFactory factory = __instance?.factory;

            if (factory == null) return;

            int planetId = factory.planetId;

            if (!Dirty.TryRemove(planetId, out _)) return;

            LastFlush[planetId] = time;

            // 放行标志要包在 finally 里：真刷那一次里要是抛了，标志留着 true
            // 就等于把去抖整个关掉，而且不会有任何迹象
            PendingKeys.TryRemove(planetId, out ConcurrentDictionary<int, byte> keys);

            // **取走即清**，否则这颗星球会永远走全量。
            //
            // 注意这里**不能**顺手把 keys 丢掉来逼出全量——keys 还要用来补跑无人机
            // 订单修复，丢了它就换来另一个静默 bug（本仓库为这个可选参数栽过一次）。
            // 所以「配对表走全量」和「keys 用于订单修复」是两个独立的信号。
            bool deltaComplete = !Incomplete.TryRemove(planetId, out _);

            long began = System.Diagnostics.Stopwatch.GetTimestamp();

            _flushing = true;

            try
            {
                if (LocalPairIndex.Enabled)
                {
                    LocalPairIndex.Rebuild(__instance, keys, deltaComplete);
                }
                else
                {
                    // 退回原版：逐个 key 调一次，没有 key 就调一次空的（只重建配对表）
                    if (keys == null || keys.Count == 0) __instance.RefreshStationTraffic();
                    else
                        foreach (int k in keys.Keys)
                            __instance.RefreshStationTraffic(k);
                }
            }
            finally
            {
                _flushing = false;
            }

            _flushed++;

            NoteFlushCost(__instance, System.Diagnostics.Stopwatch.GetTimestamp() - began, keys);
            MeasurePairs(__instance);
        }

        /// <summary>
        /// 已经量过的星球。<b>第一版这里是个全局计数器「整局只量三次」，那是个错误</b>：
        /// 先冲刷的是几颗小星球，三次名额被它们用光，而真正要看的那颗
        /// （两千多个站点的主工厂星）一次都没量到。
        /// <b>「整局只报 N 次」和「每种对象报一次」不是一回事</b>——
        /// 这条规矩本文件为 MegaStationPatches 的储物格转储记过一次，这里又踩了。
        /// </summary>
        private static readonly ConcurrentDictionary<int, byte> Measured = new ConcurrentDictionary<int, byte>();

        private const int MeasureMaxPlanets = 8;

        /// <summary>
        /// 每颗星球各自累计。<b>第一版是全局的，那让这条读数不可用。</b>
        /// 它打印时挂的是「触发第 5 次的那颗星球」的星球号，而累加的是所有星球的冲刷——
        /// 于是「行星 104 最近 5 次平均 8.09 毫秒」里混着别的星球，
        /// 我据此推出「无人机订单修复约 25 毫秒」，那个数是<b>假的</b>。
        ///
        /// <b>本轮第三次栽在同一个全局计数器上</b>（规模实测、自检节奏、这里），
        /// 三处都在这个文件里。规矩写在 CLAUDE.md：**按对象计数，不要按次数计数。**
        /// </summary>
        private sealed class Cost
        {
            internal long Ticks;
            internal long DroneTicks;
            internal long AuditTicks;
            internal int AuditRuns;
            internal int Count;
            internal int Reports;
        }

        private static readonly ConcurrentDictionary<int, Cost> Costs = new ConcurrentDictionary<int, Cost>();

        /// <summary>
        /// 报一次冲刷的真实耗时。<b>这是这条改动唯一的验收标准</b>：
        /// 改之前实测一次 81 毫秒，索引版应该落在个位数毫秒。
        ///
        /// <b>带自检的那几次会明显偏大</b>——它多跑了一遍原版的匹配，那正是被替掉的那一段。
        /// </summary>
        private static void NoteFlushCost(PlanetTransport transport, long ticks,
            ConcurrentDictionary<int, byte> keys)
        {
            int planetId = transport.factory?.planetId ?? 0;
            Cost c = Costs.GetOrAdd(planetId, _ => new Cost());

            c.Ticks += ticks;
            c.DroneTicks += LocalPairIndex.TakeDroneTicks();
            c.AuditTicks += LocalPairIndex.TakeAuditTicks(out int runs);
            c.AuditRuns += runs;
            c.Count++;

            if (c.Count < 5 || c.Reports >= 6) return;

            c.Reports++;

            double freq = System.Diagnostics.Stopwatch.Frequency;
            double all = c.Ticks / freq * 1000.0;
            double drone = c.DroneTicks / freq * 1000.0;
            double audit = c.AuditTicks / freq * 1000.0;
            // **除的是总次数，不是非自检次数**：自检只是某几次冲刷里多做的一段，
            // 而纯重建每一次都做。第一版按非自检次数除，把带自检那个窗口的
            // 「配对重建」报成了 18.14 毫秒，实际是 6.67——虚高 2.5 倍，正好是 5/2
            double pure = all - drone - audit;

            ProjectEdenPlugin.Log.LogInfo(
                $"物流配对刷新·耗时（行星 {planetId}，最近 {c.Count} 次）：" +
                $"**配对重建 {pure / c.Count:0.##} 毫秒／次**（改之前实测 81 毫秒）｜" +
                $"无人机订单修复 {drone / c.Count:0.##} 毫秒／次（原版代码，循环上界是 workDroneCount @0CCF，" +
                "没有在飞的运输机就一次都不进）｜" +
                $"回放自检 {c.AuditRuns} 次共 {audit:0.#} 毫秒（它要跑一遍**原版**的匹配，" +
                "也就是被替掉的那一段——所以它贵恰好说明这条改动有用）｜" +
                $"本次变更站点 {keys?.Count ?? 0} 个，" +
                $"索引版{(LocalPairIndex.Enabled ? "生效中" : "**已被自检关掉，正在用原版**")}。");

            c.Ticks = 0;
            c.DroneTicks = 0;
            c.AuditTicks = 0;
            c.AuditRuns = 0;
            c.Count = 0;
        }

        /// <summary>
        /// 刚刷完的这一刻，把这张表的真实规模量出来。
        ///
        /// <b>这是决定「换索引值不值」的那个数。</b> 现在的匹配是三角形全对连接，
        /// 代价约 <c>Σ_A(激活格 × (n − A.id) × 对方格数)</c>；换成按 itemId 的索引之后，
        /// 代价降到<b>「建索引 + 配对数 P」</b>——而 P 是输出本身，谁也省不掉。
        /// 所以 P 要是本来就有几百万，这条路的收益上限就远没有估的那么高。
        ///
        /// <c>localPairCount</c> 是<b>每条配对两边各存一份</b>（RematchLocalPairs @0090 和 @00A2
        /// 对 this 和 other 各调一次 AddLocalPair），所以逻辑上的对数是这个和的一半。
        ///
        /// 整局只量三次：这一段要走一遍 stationPool，不该常驻。
        /// </summary>
        private static void MeasurePairs(PlanetTransport transport)
        {
            int planetId = transport.factory?.planetId ?? 0;

            if (Measured.Count >= MeasureMaxPlanets && !Measured.ContainsKey(planetId)) return;
            if (!Measured.TryAdd(planetId, 0)) return;

            StationComponent[] pool = transport.stationPool;

            if (pool == null) return;

            int cursor = transport.stationCursor;

            long pairSlots = 0;
            long active = 0;
            long slots = 0;
            long stations = 0;
            long mega = 0;

            // 每站的激活格数和总格数先收下来，**扫描次数要按真实的后缀格数算**，
            // 不能拿「每站 30 格」去估——这个比值是用来做决定的，估出来的数不配当依据
            var act = new int[cursor];
            var cap = new int[cursor];

            for (var i = 1; i < cursor; i++)
            {
                StationComponent s = pool[i];

                if (s == null || s.id != i || s.storage == null) continue;

                stations++;
                pairSlots += s.localPairCount;
                slots += s.storage.Length;
                cap[i] = s.storage.Length;

                // 这一站是不是巨型建筑。**配对数按「同物品的供给站 × 需求站」平方增长，
                // 而巨型建筑的配对可证明用不上**——虚拟物流是直接在储物格之间搬货、
                // 不读 localPairs，所以它们的配对生成出来、被扫过、永远找不到活干
                // （skipIdleMegaStationTick 这个开关存在的理由就是这个）。
                // 要判断「把它们排除出配对表」值不值，就得先知道它们占多少——
                // **这个比例是用来做决定的，估出来的数不配当依据。**
                if (IsMegaStation(transport, i)) mega++;

                for (var k = 0; k < s.storage.Length; k++)
                    if (s.storage[k].itemId > 0 && s.storage[k].localLogic != ELogisticStorage.None)
                        act[i]++;

                active += act[i];
            }

            // suffix[i] = 站号 > i 的所有站点的格数之和
            long suffix = 0;
            long scan = 0;

            for (int i = cursor - 1; i >= 1; i--)
            {
                scan += act[i] * suffix;
                suffix += cap[i];
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"物流配对表·规模实测（行星 {transport.factory?.planetId}）：站点 {stations} 个、" +
                $"储物格合计 {slots} 个、其中方向不是 None 且有物品的 {active} 个｜" +
                $"**配对表条目 {pairSlots} 条（两边各存一份，逻辑上 {pairSlots / 2} 对）**｜" +
                $"原版全刷的内层迭代约 {scan} 次。" +
                "**换成按 itemId 的索引之后，代价降到「建索引 ≈ 储物格数」加「配对数」**——" +
                $"也就是约 {slots + pairSlots} 次，对比 {scan} 次。" +
                "这两个数的比值就是这条改动的收益上限。" +
                $"｜**其中巨型建筑 {mega} 个（{(stations > 0 ? 100.0 * mega / stations : 0):0.#}%），"
                + $"真站点 {stations - mega} 个**——索引版之后剩下的代价就是「发 {pairSlots} 条配对」，"
                + "而配对数按「同物品的供给站 × 需求站」平方增长。巨型建筑的配对可证明用不上"
                + "（虚拟物流直接在储物格之间搬货，不读 localPairs），所以把它们排除出配对表"
                + $"大致能把配对数降到 {(stations > 0 ? (double)(stations - mega) / stations : 1):0.###}² ≈ "
                + $"{(stations > 0 ? 100.0 * (stations - mega) * (stations - mega) / (stations * stations) : 100):0.#}%。"
                + "**这一行是用来决定那条改动值不值的，别拿它当结论。**");
        }

        /// <summary>
        /// 这一站是不是巨型建筑。判据用本仓库一贯的那个——
        /// <c>AssemblerComponent.speed &gt;= megaSpeedThreshold</c>，而不是 proto id：
        /// 「哪些机器算巨型」这件事全仓库只有一个答案。
        /// </summary>
        private static bool IsMegaStation(PlanetTransport transport, int stationIndex)
        {
            PlanetFactory factory = transport?.factory;
            StationComponent station = transport?.stationPool?[stationIndex];

            if (factory?.entityPool == null || station == null) return false;

            int entityId = station.entityId;

            if (entityId <= 0 || entityId >= factory.entityPool.Length) return false;

            int asmId = factory.entityPool[entityId].assemblerId;
            AssemblerComponent[] asm = factory.factorySystem?.assemblerPool;

            if (asmId <= 0 || asm == null || asmId >= asm.Length) return false;

            return asm[asmId].id == asmId && asm[asmId].speed >= MegaBuildingRegistry.MegaSpeedThreshold;
        }

        /// <summary>换存档时清空：星球号会重复使用。</summary>
        internal static void Reset()
        {
            Dirty.Clear();
            Measured.Clear();
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
                "无人机会多用两秒的旧配对。读档那一次不受影响。" +
                $"另外配对表本身已改为**按 itemId 建索引**重建（{nameof(LocalPairIndex)}）：" +
                "匹配谓词只有「itemId 相同 + 方向互补」，是一次等值连接，" +
                "所以三角形全对扫描（实测这颗星球 8900 万次内层迭代）可以降到" +
                "「建索引 + 配对数」（约 64 万次）。**每 20 次重建做一次回放自检**，" +
                "把原版的匹配也跑一遍逐条比对，对不上就整局退回原版并报 ERROR。");
        }

        /// <summary>合并了多少次、实际刷了多少次——比值就是这条改动省下的倍数。</summary>
        internal static string Stats() => $"标脏 {_coalesced} 次 / 实际刷新 {_flushed} 次";
    }
}
