using System;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 用<b>按 itemId 的索引</b>重建行星内供需配对表，替掉原版那个三角形全对连接。
    ///
    /// <b>匹配谓词只有一条，所以这是一次等值连接。</b>
    /// <c>StationComponent.RematchLocalPairs</c> 的内层判定就两句
    /// （@0126 / @0136 和镜像的 @0072 / @0082）：<c>itemId</c> 相同、方向互补
    /// （<c>Supply</c> ↔ <c>Demand</c>）。没有距离、没有优先级、没有分组——
    /// 等值连接的标准解法就是按连接键建索引，二叉树和堆在这里都无用武之地
    /// （它们解决排序和取极值，这两样这里都不需要）。
    ///
    /// <b>实测规模（同一存档的五颗星球）：</b>
    /// <code>
    /// 星球  站点  储物格  激活格   逻辑配对    原版扫描      索引后    比值
    /// 3701    28    728      45        28      15,756       784     20x
    /// 3703   112    228     118       110      18,429       448     41x
    ///  103   558  3,174     661     5,315   1,223,061    13,804     89x
    ///  104  2101 37,190   4,017   303,093  89,279,581   643,376    139x
    /// </code>
    /// 89.3M 次内层迭代对应实测的 81 毫秒（约 0.9 纳秒一次），所以这个模型是准的。
    /// <b>别指望 139 倍</b>：643k 里有 606k 是 <c>AddLocalPair</c> 本身，那是输出、省不掉。
    ///
    /// <b>切口：<c>stationCursor</c> 只被用作那两个循环的上界。</b>
    /// 全方法里 arg2 只出现在 @00BA 和 @0179 两处 <c>blt</c>；而后半段（无人机订单修复，
    /// 占了方法 2800 字节）在 @018C 被 <c>keyStationId &lt;= 0</c> 整段跳过，
    /// 且它索引 <c>stationPool</c> 用的是无人机的 <c>endId</c>、不受 cursor 约束。所以
    /// <b><c>RematchLocalPairs(真实的 pool, 0, keyStationId, droneCarries)</c>
    /// 等于「只跑无人机修复、完全不做匹配」</b>（匹配循环从 <c>this.id + 1</c> 起，
    /// 而 <c>this.id ≥ 1</c>，对 0 的 <c>blt</c> 一次都进不去）。
    ///
    /// 于是这里只替换连接那一段，原版那 2800 字节原样调用，一个字节没碰。
    /// </summary>
    internal static class LocalPairIndex
    {
        /// <summary>
        /// 每条链表的节点。<b>用平铺数组 + 链式桶，不用 Dictionary</b>：
        /// itemId 上界是已知的几千，桶头一个 <c>int[]</c> 就够，
        /// 而节点数组按总槽位数一次分配、跨次复用，**每次重建零分配**。
        /// </summary>
        private sealed class Buffers
        {
            internal int[] SupplyHead = new int[0];
            internal int[] DemandHead = new int[0];

            /// <summary>这一轮碰过哪些 itemId——复位时只清这些，不清整张表。</summary>
            internal int[] Touched = new int[256];

            internal int TouchedCount;

            internal int[] NodeNext = new int[0];
            internal int[] NodeStation = new int[0];
            internal int[] NodeSlot = new int[0];
            internal int NodeCount;
        }

        /// <summary>
        /// <b><c>[ThreadStatic]</c> 且惰性创建。</b> 冲刷点在
        /// <c>PlanetTransport.GameTick</c> 上，那是「一颗星球一个线程」的并行路径——
        /// 静态可变缓冲区在那里会被几十个线程同时踩（见 CLAUDE.md 的陷阱 4）。
        /// <c>[ThreadStatic]</c> 的初始化器只在第一个线程上跑，所以必须判空再建。
        /// </summary>
        [ThreadStatic] private static Buffers _buf;

        /// <summary>索引版是否启用。自检抓到不一致会整局关掉它，退回原版。</summary>
        internal static bool Enabled => System.Threading.Volatile.Read(ref _disabled) == 0;

        private static int _disabled;

        /// <summary>
        /// <b>每颗星球各自计数，而不是一个全局计数器。</b>
        ///
        /// 第一版用的是全局 <c>_rebuilds</c>，于是「头 3 次每次都查」的名额被最先冲刷的
        /// 三颗小星球用光，<b>真正要验的那颗（2101 个站点）一次都没查到</b>——
        /// 而且冲刷点在并行的 <c>PlanetTransport.GameTick</c> 上，几颗星球同时自增，
        /// 日志里三行自检全写着「第 3 次」，连计数本身都是错的。
        ///
        /// <b>这是本文件两轮前刚记过的那个错</b>（规模实测那里：「整局只报 N 次」
        /// 不等于「每种对象报一次」），在自检的节奏上又犯了一遍，还叠加了并行竞争。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> RebuildsByPlanet =
            new System.Collections.Concurrent.ConcurrentDictionary<int, int>();

        private static int _audits;
        private static int _reported;

        /// <summary>
        /// 多少次重建做一次回放自检。一次自检的代价就是跑一遍原版的匹配（实测 81 毫秒）。
        ///
        /// <b>头几次每次都查，之后才拉开</b>。第一版一律每 20 次，结果第一局只冲刷了 5 次，
        /// 自检一行都没出——**速度验收到了，正确性没有**。
        /// 而最该密集查的恰恰是刚上线的那几次：真要错，第一次就会错。
        /// </summary>
        private const int AuditEvery = 20;

        private const int AuditFirstN = 3;

        /// <summary>
        /// 自检允许的估算代价上限，单位是「原版匹配的内层迭代次数」。
        ///
        /// <para><b>按次数节流是错的，因为代价按规模涨。</b> 上面那个「头 3 次 + 每 20 次」
        /// 是在一颗 <b>2,101 站</b>的星球上定的，当时原版侧实测 81 ms。而原版的匹配是
        /// <b>O(站点²)</b>：同一条规则放到一颗 <b>8,465 站</b>的星球上，单次自检外推到
        /// <c>(8465/2101)² × 81 ≈ 1,300 ms</c>——**每 20 次放建筑冻一秒三，进星球时连冻三次**。
        /// 玩家报的「放一座建筑都卡」就是这个。</para>
        ///
        /// <para>所以阈值不是拍的，是从那个实测点反推的：81 ms 对应 2,101² ≈ 4.41M 次迭代，
        /// 要把单次自检压到 <b>30 ms 以内</b>就是 <c>4.41M × 30/81 ≈ 1.6M</c>，
        /// 约合 1,280 站。</para>
        ///
        /// <para><b>为什么可以跳过。</b> 自检要验的是「索引发出的配对和原版逐条一致」——
        /// 那是**算法的性质，不是星球的性质**：一颗 539 站的星球走的是同一段代码。
        /// 所以在小星球上照常密集验，大星球上跳过，并且**大声说出来**——
        /// 一个静默降级的安全网等于没有安全网。</para>
        /// </summary>
        private const long AuditCostBudget = 1_600_000L;

        /// <summary>哪些星球已经因为太大而跳过自检——每颗只说一次。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> AuditSkipReported
            = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

        /// <summary>
        /// 重建这颗星球的本地配对表。<paramref name="keys"/> 是这一轮合并窗口里
        /// 攒下的变更站点，每个都要补跑一次原版的无人机订单修复。
        /// </summary>
        /// <param name="deltaComplete">
        /// 这一批 <paramref name="keys"/> 是否涵盖了窗口里<b>全部</b>的变更站点。
        /// 合并窗口攒的键超过上限时会丢弃多余的，那时它是 false ——
        /// <b>缺项的 delta 会让被丢掉的那几台站点的配对静默陈旧</b>，所以必须退回全量。
        /// keys 本身仍然要用（补跑无人机订单修复），两个信号是独立的。
        /// </param>
        internal static void Rebuild(PlanetTransport transport,
            System.Collections.Concurrent.ConcurrentDictionary<int, byte> keys, bool deltaComplete = true)
        {
            StationComponent[] pool = transport.stationPool;

            if (pool == null) return;

            int cursor = transport.stationCursor;

            if (cursor <= 1) return;

            int droneCarries = GameMain.history.logisticDroneCarries;
            int planet = transport.factory?.planetId ?? 0;

            // ── 增量路径 ────────────────────────────────────────────────
            //
            // **前提：表在应用 delta 之前必须已经是对的。** 读档之后、或者这颗星球
            // 这一局还没全量建过时，表里是 Import 留下的东西，delta 加在错的基础上
            // 还是错的——所以每颗星球的第一次冲刷一定走全量，之后才允许增量。
            //
            // keys 为空表示「有东西变了但不知道是谁」（keyStationId 传的是 0），
            // 同样退回全量。**不知道 delta 是什么的时候，唯一安全的 delta 是全部。**
            bool canIncremental = IncrementalEnabled
                                  && deltaComplete
                                  && keys != null && keys.Count > 0
                                  && keys.Count <= IncrementalMaxKeys
                                  && FullyBuilt.ContainsKey(planet)
                                  && !DueForReconcile(planet, keys.Count);

            if (canIncremental)
            {
                int vanished = RebuildMany(transport, pool, cursor, keys, out int unsupported);

                if (unsupported == 0)
                {
                    DroneRepair(transport, pool, cursor, droneCarries, keys);

                    if (vanished > 0 &&
                        System.Threading.Interlocked.Increment(ref _vanishedReported) <= 3)
                        ProjectEdenPlugin.Log.LogInfo(
                            $"物流配对表·增量：行星 {planet} 这一批里有 {vanished} 个站点已经拆掉了，"
                            + "它们的配对在拆站那一刻就摘干净了，这里只补跑无人机订单修复。"
                            + "**这是正常路径**——上一版把它误判成「增量失败」，每拆一座站都白做一次全量重建。");

                    return;
                }

                // 真的做不了（拿不到 stationPool 之类）才整批退回全量：
                // 半套增量比全量更难解释，而全量永远是对的。
                ProjectEdenPlugin.Log.LogWarning(
                    $"物流配对表·增量：行星 {planet} 拿不到站点池，本次退回全量重建。");
            }

            Buffers b = _buf ?? (_buf = new Buffers());

            // 对账：如果这次全量是为了核对增量的结果，先记下增量算出来的校验和。
            bool reconciling = FullyBuilt.ContainsKey(planet) && IncrementalEnabled && ReconcileDue(planet);
            long before = reconciling ? Checksum(pool, cursor) : 0L;

            try
            {
                ClearAll(pool, cursor);
                BuildIndex(b, pool, cursor);
                EmitPairs(b, pool, cursor);
            }
            finally
            {
                ResetIndex(b);
            }

            FullyBuilt[planet] = 0;

            if (reconciling) Reconcile(pool, cursor, planet, before);

            int planetId = planet;
            int n = RebuildsByPlanet.AddOrUpdate(planetId, 1, (_, old) => old + 1);

            if (n <= AuditFirstN || n % AuditEvery == 0)
            {
                // 原版匹配是 O(站点²)，所以先按规模估一下这次自检要多久。
                long est = (long)cursor * cursor;

                if (est > AuditCostBudget)
                {
                    // **每颗星球只说一次，但一定要说。** 静默降级的安全网等于没有安全网。
                    if (AuditSkipReported.TryAdd(planetId, 0))
                        ProjectEdenPlugin.Log.LogWarning(
                            $"物流配对表·自检已跳过（行星 {planetId}，站点 {cursor} 个）："
                            + $"自检要再跑一遍原版的 O(站点²) 匹配，按 2,101 站 81 ms 的实测点外推约 "
                            + $"{81.0 * est / 4_410_000.0:0} ms，**那会表现为「每放一座建筑就卡一下」**，"
                            + "所以这颗星球不查。索引版的正确性在较小的星球上仍然逐条验证——"
                            + "那验的是算法的性质，和星球多大无关。");
                }
                else
                {
                    long auditBegan = System.Diagnostics.Stopwatch.GetTimestamp();

                    Audit(transport, pool, cursor, droneCarries, planetId, n);

                    _auditTicks += System.Diagnostics.Stopwatch.GetTimestamp() - auditBegan;
                    _auditRuns++;
                }
            }

            DroneRepair(transport, pool, cursor, droneCarries, keys);
        }

        /// <summary>
        /// 原版的无人机订单修复。<b><c>cursor</c> 传 0 = 跳过匹配、只跑后半段</b>——
        /// 依据是 arg2 在全方法里只出现在 @00BA 和 @0179 两处循环上界，
        /// 而匹配循环从 <c>this.id + 1</c> 起、<c>this.id ≥ 1</c>，对 0 的 <c>blt</c> 一次都进不去。
        ///
        /// <b>增量路径也必须跑它</b>：它修的是「在飞的运输机订单和槽位的 localOrder 对不上」，
        /// 和配对表怎么算出来的无关。漏掉它不报任何错——本仓库为这个可选参数栽过一次。
        /// </summary>
        private static void DroneRepair(PlanetTransport transport, StationComponent[] pool, int cursor,
            int droneCarries, System.Collections.Concurrent.ConcurrentDictionary<int, byte> keys)
        {
            if (keys == null || keys.Count == 0) return;

            long began = System.Diagnostics.Stopwatch.GetTimestamp();

            foreach (int key in keys.Keys)
            {
                if (key <= 0) continue;

                for (var i = 1; i < cursor; i++)
                {
                    StationComponent s = pool[i];

                    if (s != null && s.id == i) s.RematchLocalPairs(pool, 0, key, droneCarries);
                }
            }

            _droneTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
        }

        // ── 增量维护的开关、节奏与对账 ──────────────────────────────────

        /// <summary>增量维护是否还开着。对账抓到不一致会关掉它，<b>但保留全量索引版</b>。</summary>
        internal static bool IncrementalEnabled => System.Threading.Volatile.Read(ref _ivmOff) == 0;

        private static int _ivmOff;

        /// <summary>
        /// 一次冲刷里最多几个变更站点还走增量。
        ///
        /// <para><b>上一版这里是 8，而那个数是照着一个错的成本模型定的，结果增量一次都没走成。</b>
        /// 当时算的是「每个 key 各建一次索引 ≈ 0.3 ms」，实测全量 40~49 ms、其中发 304 万条
        /// 配对占约 96%，所以建索引实际约 <b>2 ms</b>——贵 7 倍。而玩家一个 2 秒窗口里
        /// 实测攒下 10~20 个变更站点，全都超过 8，于是每次都退回全量。</para>
        ///
        /// <para>真正的修法不是调大阈值，是 <see cref="RebuildMany"/>：**一次冲刷只建一次索引**，
        /// 于是 N 个 key 的代价变成 <c>2 ms + N × 约 360 条发射</c>，几乎与 N 无关。
        /// 之后这个阈值就只剩「防病态批次」的作用——和全量的盈亏平衡点在
        /// <c>304 万 ÷ 360 ≈ 8,400</c> 个 key，而上游 <c>MaxKeysPerFlush</c> 本来就封顶 32，
        /// 所以取 32 等于「永远不触发」，留着只是别让上游改大了之后这里无声失配。</para>
        /// </summary>
        private const int IncrementalMaxKeys = 32;

        /// <summary>这颗星球这一局全量建过没有。<b>没建过就不许上 delta</b>。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> FullyBuilt =
            new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

        /// <summary>每颗星球攒了多少次增量，用来决定什么时候插一次全量来对账。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> IncrementsByPlanet =
            new System.Collections.Concurrent.ConcurrentDictionary<int, int>();

        /// <summary>多少次增量之后插一次全量对账。全量实测 60 ms，摊到 200 次上可以忽略。</summary>
        private const int ReconcileEvery = 200;

        /// <summary>
        /// 该不该把这一次转成全量来对账。<b>先加计数再判断</b>，所以每颗星球
        /// 每 <see cref="ReconcileEvery"/> 次增量恰好触发一次。
        /// </summary>
        private static bool DueForReconcile(int planet, int keyCount)
        {
            int n = IncrementsByPlanet.AddOrUpdate(planet, keyCount, (_, old) => old + keyCount);

            return n >= ReconcileEvery;
        }

        /// <summary>全量分支里再问一次：这次是不是为了对账。问完就清零。</summary>
        private static bool ReconcileDue(int planet)
        {
            if (!IncrementsByPlanet.TryGetValue(planet, out int n) || n < ReconcileEvery) return false;

            IncrementsByPlanet[planet] = 0;

            return true;
        }

        /// <summary>
        /// 对账：把增量维护出来的表和刚刚全量重建的表比一比。
        ///
        /// <para><b>这就是增量这条路能走的唯一理由</b>，和全量版当初那个「和原版逐条比」
        /// 的自检同构，只是换了一层传递性：全量版 ≡ 原版（在小星球上仍然逐条验），
        /// 增量 ≡ 全量版（这里，每颗星球都验）。两段接起来就是 增量 ≡ 原版。</para>
        ///
        /// <para><b>为什么不直接和原版比</b>：原版是 O(站点²)，在一颗 8,467 站的星球上
        /// 单次 1,300 ms，而它正是「每放一座建筑就卡一下」的成因。
        /// 和全量索引版比只要 60 ms，而全量索引版的正确性另有保证。</para>
        ///
        /// <para>不一致就<b>只关增量</b>，保留全量索引版——那仍然比原版快一个数量级。
        /// 而且此刻表里留下的是全量算出来的那一份，所以物流不受影响。</para>
        /// </summary>
        private static void Reconcile(StationComponent[] pool, int cursor, int planet, long before)
        {
            long after = Checksum(pool, cursor);

            if (before == after)
            {
                if (System.Threading.Interlocked.Increment(ref _reconciled) <= 8)
                    ProjectEdenPlugin.Log.LogInfo(
                        $"物流配对表·增量对账通过（行星 {planet}，站点 {cursor - 1} 个，"
                        + $"配对 {Pairs(pool, cursor)} 条）：增量维护出来的表和全量重建的表是同一个多重集。");

                return;
            }

            System.Threading.Volatile.Write(ref _ivmOff, 1);

            ProjectEdenPlugin.Log.LogError(
                $"物流配对表·增量对账**失败**（行星 {planet}）：增量 {before}，全量 {after}。"
                + "**已整局关掉增量维护，退回全量索引版**（那仍比原版快一个数量级）。"
                + "本次留下的是全量算出来的表，所以物流不受影响。");
        }

        private static int _reconciled;

        private static int _vanishedReported;

        [ThreadStatic] private static long _droneTicks;

        [ThreadStatic] private static long _auditTicks;

        [ThreadStatic] private static int _auditRuns;

        /// <summary>
        /// 取走并清零「这一次冲刷里自检花了多久、跑了几次」。
        ///
        /// <b>不拆出来就读不出稳态。</b> 一次自检要跑一遍原版匹配（实测 81 毫秒）
        /// 加两遍校验和，而按星球的节奏头 3 次重建都自检——
        /// 于是前几个窗口的平均值被它撑到 70 毫秒上下，
        /// 看起来像「这条改动没用」，其实那正是<b>被它替掉的那一段</b>的开销。
        /// </summary>
        internal static long TakeAuditTicks(out int runs)
        {
            long t = _auditTicks;

            runs = _auditRuns;
            _auditTicks = 0;
            _auditRuns = 0;

            return t;
        }

        /// <summary>
        /// 取走并清零「这一次冲刷里无人机订单修复花了多久」。
        ///
        /// <b>单独量它，是因为我拿两个不可比的窗口推出过一个「约 25 毫秒」的假数。</b>
        /// 真值得看的是：它的循环上界是 <c>workDroneCount</c>（@0CCF），
        /// 所以没有在飞的运输机就一次都不进——这颗星球上巨型建筑走虚拟物流、
        /// 无人机根本不起飞，那这一段<b>应该很便宜</b>。是不是，让这个读数说。
        /// </summary>
        internal static long TakeDroneTicks()
        {
            long t = _droneTicks;

            _droneTicks = 0;

            return t;
        }

        private static void ClearAll(StationComponent[] pool, int cursor)
        {
            for (var i = 1; i < cursor; i++)
            {
                StationComponent s = pool[i];

                if (s != null && s.id == i) s.ClearLocalPairs();
            }
        }

        /// <summary>
        /// 回放自检：把原版的匹配也跑一遍，逐条比对配对表。
        ///
        /// <b>这不是装饰，是这条改动能走的唯一理由</b>——和 1.10.0 的批量结算自检同构。
        /// 抓到不一致就整局退回原版并报 ERROR，最坏情况是「没变快」，不是「配对错了」。
        ///
        /// 比的是<b>顺序敏感的校验和</b>而不是快照：快照要 12 MB（实测这颗星球 606186 条），
        /// 而校验和是 O(配对数)、零内存。我们的发射顺序是刻意和原版逐条对齐的
        /// （站点升序 → 槽位升序 → 链表升序），所以顺序敏感是<b>更强</b>的判据，不是更弱。
        ///
        /// 比完之后<b>留下的是原版算的那一份</b>：它是权威，而且这样自检本身也不会改变结果。
        /// </summary>
        private static void Audit(PlanetTransport transport, StationComponent[] pool, int cursor, int droneCarries,
            int planetId, int rebuildNo)
        {
            System.Threading.Interlocked.Increment(ref _audits);

            long ours = Checksum(pool, cursor);

            ClearAll(pool, cursor);

            for (var i = 1; i < cursor; i++)
            {
                StationComponent s = pool[i];

                // keyStationId 传 0：只要匹配，不要无人机修复（那一段外面单独按 key 跑）
                if (s != null && s.id == i) s.RematchLocalPairs(pool, cursor, 0, droneCarries);
            }

            long theirs = Checksum(pool, cursor);

            if (ours == theirs)
            {
                // 每颗星球各报一次头几条，别让一颗星球把名额吃光——同上面那条注释
                if (System.Threading.Interlocked.Increment(ref _reported) <= 8)
                    ProjectEdenPlugin.Log.LogInfo(
                        $"物流配对表·索引版自检通过（行星 {planetId} 的第 {rebuildNo} 次重建，" +
                        $"站点 {cursor - 1} 个，配对 {Pairs(pool, cursor)} 条）：" +
                        "索引重建的配对表和原版逐条一致。");

                return;
            }

            System.Threading.Volatile.Write(ref _disabled, 1);

            ProjectEdenPlugin.Log.LogError(
                $"物流配对表·索引版自检**失败**（行星 {planetId}，第 {rebuildNo} 次重建）：" +
                $"索引校验和 {ours}，原版 {theirs}。**已整局退回原版实现**，" +
                "本次留下的是原版算出来的表，所以物流不受影响，只是不再变快。");
        }

        /// <summary>这颗星球此刻的配对表条目数——自检那行带上它，好知道这次验的规模。</summary>
        private static long Pairs(StationComponent[] pool, int cursor)
        {
            long n = 0;

            for (var i = 1; i < cursor; i++)
            {
                StationComponent s = pool[i];

                if (s != null && s.id == i) n += s.localPairCount;
            }

            return n;
        }

        /// <summary>
        /// <b>多重集校验和：对配对的顺序不敏感，对内容和重数敏感。</b>
        ///
        /// <para><b>以前这里是顺序敏感的，而那在增量维护下必然误报。</b> 全量重建是
        /// 「站点升序 → 槽位升序」重排一遍，增量维护是往已有数组尾部追加——
        /// 两者的<b>集合</b>一样、<b>顺序</b>必然不同。顺序敏感的校验和会把每一次增量
        /// 都判成不一致，然后按设计整局关掉索引版、退回 O(站点²)，
        /// **比不做增量还慢一个数量级**。</para>
        ///
        /// <para><b>为什么是加法而不是异或。</b> 异或不区分重数：同一条配对出现两次会
        /// 互相抵消，而「重复发射了一条配对」正是增量维护最可能犯的错。加法是多重集
        /// 的正确组合子。每条先过一遍雪崩混合（<c>Mix</c>）再累加，
        /// 免得四个小整数直接相加时「张三多一、李四少一」互相抵消。</para>
        ///
        /// <para><b>代价</b>：判据比以前弱了一点——它不再能发现「集合对、顺序错」。
        /// 那是有意的取舍：顺序在原版里只影响 <c>localPairProcess</c> 这个轮询游标
        /// 从哪一条开始扫，扫完一圈的结果一样。</para>
        /// </summary>
        private static long Checksum(StationComponent[] pool, int cursor)
        {
            long sum = 0;

            for (var i = 1; i < cursor; i++)
            {
                StationComponent s = pool[i];

                if (s == null || s.id != i || s.localPairs == null) continue;

                for (var p = 0; p < s.localPairCount; p++)
                {
                    // 把「这条配对存在谁身上」也算进去：两边各存一份，
                    // 少存一边是个真错误，不带 i 的话看不出来。
                    long h = Mix(i);

                    h = Mix(h ^ s.localPairs[p].supplyId);
                    h = Mix(h ^ s.localPairs[p].supplyIndex);
                    h = Mix(h ^ s.localPairs[p].demandId);
                    h = Mix(h ^ s.localPairs[p].demandIndex);

                    unchecked { sum += h; }
                }
            }

            return sum;
        }

        /// <summary>splitmix64 的混合步——把小整数摊开，否则相加会互相抵消。</summary>
        private static long Mix(long x)
        {
            unchecked
            {
                var z = (ulong)x + 0x9E3779B97F4A7C15UL;

                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;

                return (long)(z ^ (z >> 31));
            }
        }

        /// <summary>
        /// 建索引：<c>itemId</c> → 该方向上所有 (站号, 槽位)。
        ///
        /// <b>站点倒着走、槽位也倒着走，是为了让链表正着出来。</b>
        /// 插入用的是头插，所以倒序插入得到的链表就是按 (站号, 槽位) 升序的——
        /// 而下面发射配对时要的正是这个顺序，它决定了我们发出来的配对序列
        /// 和原版<b>逐条一致</b>，而不只是集合相同。
        /// </summary>
        private static void BuildIndex(Buffers b, StationComponent[] pool, int cursor)
        {
            var slotTotal = 0;
            var maxItem = 0;

            for (var i = 1; i < cursor; i++)
            {
                StationComponent s = pool[i];

                if (s?.storage == null || s.id != i) continue;

                slotTotal += s.storage.Length;

                for (var k = 0; k < s.storage.Length; k++)
                    if (s.storage[k].itemId > maxItem)
                        maxItem = s.storage[k].itemId;
            }

            EnsureHeads(b, maxItem + 1);
            EnsureNodes(b, slotTotal);

            for (int i = cursor - 1; i >= 1; i--)
            {
                StationComponent s = pool[i];

                if (s?.storage == null || s.id != i) continue;

                for (int k = s.storage.Length - 1; k >= 0; k--)
                {
                    int item = s.storage[k].itemId;

                    if (item <= 0) continue;

                    ELogisticStorage dir = s.storage[k].localLogic;

                    if (dir != ELogisticStorage.Supply && dir != ELogisticStorage.Demand) continue;

                    int[] head = dir == ELogisticStorage.Supply ? b.SupplyHead : b.DemandHead;

                    if (head[item] < 0 && Other(b, dir)[item] < 0) Touch(b, item);

                    int node = b.NodeCount++;

                    b.NodeStation[node] = i;
                    b.NodeSlot[node] = k;
                    b.NodeNext[node] = head[item];
                    head[item] = node;
                }
            }
        }

        private static int[] Other(Buffers b, ELogisticStorage dir) =>
            dir == ELogisticStorage.Supply ? b.DemandHead : b.SupplyHead;

        /// <summary>
        /// 发射配对。<b>站点升序、槽位升序、链表升序</b>——和原版逐条一致。
        ///
        /// 原版的两个分支（<c>RematchLocalPairs</c> @0085 和 @013E）参数顺序是镜像的：
        /// 供应分支 <c>AddLocalPair(this.id, s, 对方, t)</c>，
        /// 需求分支 <c>AddLocalPair(对方, t, this.id, s)</c>，两边都各存一份。
        ///
        /// <b>跳过站号 ≤ 自己的那一段，是原版「从 this.id + 1 开始」的等价物</b>
        /// （@0039–0041），它保证每条逻辑配对只被发射一次。
        /// </summary>
        private static void EmitPairs(Buffers b, StationComponent[] pool, int cursor)
        {
            for (var i = 1; i < cursor; i++)
            {
                StationComponent s = pool[i];

                if (s?.storage == null || s.id != i) continue;

                for (var k = 0; k < s.storage.Length; k++)
                {
                    int item = s.storage[k].itemId;

                    if (item <= 0) continue;

                    ELogisticStorage dir = s.storage[k].localLogic;

                    if (dir == ELogisticStorage.Supply)
                    {
                        for (int n = b.DemandHead[item]; n >= 0; n = b.NodeNext[n])
                        {
                            int other = b.NodeStation[n];

                            if (other <= i) continue;

                            StationComponent o = pool[other];

                            if (o == null) continue;

                            s.AddLocalPair(i, k, other, b.NodeSlot[n]);
                            o.AddLocalPair(i, k, other, b.NodeSlot[n]);
                        }
                    }
                    else if (dir == ELogisticStorage.Demand)
                    {
                        for (int n = b.SupplyHead[item]; n >= 0; n = b.NodeNext[n])
                        {
                            int other = b.NodeStation[n];

                            if (other <= i) continue;

                            StationComponent o = pool[other];

                            if (o == null) continue;

                            s.AddLocalPair(other, b.NodeSlot[n], i, k);
                            o.AddLocalPair(other, b.NodeSlot[n], i, k);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// <b>增量维护：只重算「这一个站点」的配对，而不是整张表。</b>
        ///
        /// <para>这是经典的增量视图维护（IVM）。配对表是一个按 <c>itemId</c> 的等值连接，
        /// 而等值连接的全量求值下界是 <c>Ω(输入 + 输出)</c>——输出就是那 304 万条配对，
        /// <b>换任何数据结构都不可能更快</b>。唯一的出路是别每次都把输出重新写一遍：
        /// 插入一个站点只会新增「它自己那几条」，这就是 delta。</para>
        ///
        /// <para><b>实测量级</b>（一颗 8,467 站、304 万配对的星球）：全量 304 万条；
        /// 而单站均摊 304 万 ÷ 8,467 ≈ <b>360 条</b>，两侧各存一份也就 720 次操作。
        /// 主要成本反而落在「建索引」上，那是 O(全星球槽位数) ≈ 12.7 万——
        /// 仍然比 304 万小一个多数量级。</para>
        ///
        /// <para><b>为什么每次都重建索引，而不是把索引也增量维护。</b> 那要多一份
        /// 必须和 <c>storage</c> 时刻一致的状态，而 <c>storage</c> 是原版随时会改的；
        /// O(槽位) 重建一次约 0.3 ms，已经够便宜，不值得为它再开一条会失步的路。
        /// **少一份需要同步的状态，就少一整类 bug。**</para>
        ///
        /// <para>返回 false 表示「这次做不了增量」（站点不合法等），调用方应退回全量。</para>
        /// </summary>
        /// <summary>
        /// <see cref="RebuildOne"/> 的结果。**「站点已经不在了」和「做不了增量」是两回事**，
        /// 上一版把它们合成一个 <c>false</c>，于是每拆一座站都会白白触发一次全量重建。
        ///
        /// 链路是这样的：<c>RemoveStation_Prefix</c> 把配对摘干净之后，那个站号**仍然留在
        /// <c>PendingKeys</c> 里**（<c>MarkDirty</c>/<c>RememberKey</c> 早就跑过了），
        /// 而且**必须留着**——原版在 @02F5 就是拿它去跑无人机订单修复的。
        /// 等定时冲刷来时站点已被 <c>Reset()</c>、<c>id</c> 归零，
        /// 于是「查不到这个站」被当成了「增量失败」。
        ///
        /// <b>它不是失败，是没有 delta 要算</b>——该做的在拆站那一刻已经做完了。
        /// </summary>
        internal enum DeltaResult
        {
            /// <summary>增量已应用。</summary>
            Applied,

            /// <summary>这个站点已经不存在了——没有 delta 要算，跳过即可，**不要退回全量**。</summary>
            Vanished,

            /// <summary>这次做不了增量，调用方应退回全量重建。</summary>
            Unsupported,
        }

        /// <summary>
        /// 一次冲刷里的一批变更站点，<b>共用一次索引重建</b>。返回「已消失」的个数，
        /// <paramref name="unsupported"/> 非零表示调用方该退回全量。
        ///
        /// <para><b>为什么必须是一批而不是逐个：索引重建才是增量的单价。</b>
        /// 实测一次全量 40~49 ms，其中发 304 万条配对占约 96%，所以建索引本身约 2 ms
        /// （O(全星球槽位) ≈ 12.7 万）。逐个 key 各建一次的话，20 个 key 就是 40 ms——
        /// 和全量一样贵，增量白做。共用一次之后，N 个 key 的代价是
        /// <c>2 ms + N × 约 360 条发射</c>，**几乎与 N 无关**。</para>
        ///
        /// <para><b>一个批内的陷阱：两个变更站点之间的配对会被发两次。</b>
        /// 先摘 A 再摘 B，然后发 A（此时 B 在索引里 → 发出 A↔B）、再发 B
        /// （A 也在索引里 → 又发一次 A↔B）。多重集就多了一条，而这正是
        /// <c>sim_pairindex.py</c> 专门测的那类「重复发射」。
        /// 解法是<b>按处理顺序跳过已经发过的键</b>：处理 kᵢ 时跳过 {k₁..kᵢ₋₁}，
        /// 于是 kᵢ↔kⱼ 只在处理较早的那个时发出一次。</para>
        /// </summary>
        private static int RebuildMany(PlanetTransport transport, StationComponent[] pool, int cursor,
            System.Collections.Concurrent.ConcurrentDictionary<int, byte> keys, out int unsupported)
        {
            unsupported = 0;

            var vanished = 0;

            // 排序只是为了让「已处理」这个概念有确定的顺序，和正确性无关，
            // 但确定的顺序让对账失败时可复现。
            var live = new System.Collections.Generic.List<int>(keys.Count);

            foreach (int k in keys.Keys)
            {
                if (k <= 0 || k >= cursor) { vanished++; continue; }

                StationComponent s = pool[k];

                if (s == null || s.id != k || s.storage == null) { vanished++; continue; }

                live.Add(k);
            }

            if (live.Count == 0) return vanished;

            live.Sort();

            Buffers b = _buf ?? (_buf = new Buffers());

            // ① 先把这一批的旧配对全摘掉，再建索引——**顺序不能反**：
            //    索引要反映「摘完之后」的状态，否则发射时会把刚摘掉的又配回来。
            foreach (int k in live) DetachPairsOf(pool, cursor, k);

            try
            {
                BuildIndex(b, pool, cursor);

                // ② 用 emitted 戳标记「这一批里已经发过的键」，见上面那段注释。
                if (_emitted == null || _emitted.Length < cursor) _emitted = new int[cursor + 64];

                unchecked { _emittedGen++; }

                if (_emittedGen == 0) { Array.Clear(_emitted, 0, _emitted.Length); _emittedGen = 1; }

                foreach (int k in live)
                {
                    EmitPairsFor(b, pool, cursor, k);

                    _emitted[k] = _emittedGen;
                }
            }
            finally
            {
                ResetIndex(b);
            }

            return vanished;
        }

        /// <summary>这一批里已经发射过配对的键，见 <see cref="RebuildMany"/>。</summary>
        [ThreadStatic] private static int[] _emitted;

        [ThreadStatic] private static int _emittedGen;

        /// <summary>
        /// 这个对侧在本批里已经发过配对了吗。<b>单个键的路径（<see cref="RebuildOne"/>）
        /// 走到这里时戳是空的，所以恒为 false</b>——同一个判据服务两条路，不用分叉。
        /// </summary>
        private static bool AlreadyEmitted(int stationId) =>
            _emitted != null && stationId < _emitted.Length
                             && _emittedGen != 0 && _emitted[stationId] == _emittedGen;

        internal static DeltaResult RebuildOne(PlanetTransport transport, int stationId)
        {
            StationComponent[] pool = transport?.stationPool;

            if (pool == null) return DeltaResult.Unsupported;

            int cursor = transport.stationCursor;

            if (cursor <= 1) return DeltaResult.Unsupported;

            // 站号越界 / 组件已回收 / storage 已置空——全都是「这个站点没了」，
            // 而不是「算不出来」。拆站之后留在待办里的键走的正是这一条。
            if (stationId <= 0 || stationId >= cursor) return DeltaResult.Vanished;

            StationComponent s = pool[stationId];

            if (s == null || s.id != stationId || s.storage == null) return DeltaResult.Vanished;

            Buffers b = _buf ?? (_buf = new Buffers());

            // ① 先把这个站点现有的配对从**所有对侧**摘干净，再摘它自己的。
            DetachPairsOf(pool, cursor, stationId);

            // ② 重建索引（此时表里已经没有这个站点的旧配对），只发射它自己的那些。
            //
            // **先把批次戳作废。** AlreadyEmitted 读的是 [ThreadStatic] 的 _emitted/_emittedGen，
            // 上一批 RebuildMany 可能在这条线程上留下了非零的世代——不清的话这次会
            // 误跳过某些对侧，而且**不报错，只是少发几条配对**。
            _emittedGen = 0;

            try
            {
                BuildIndex(b, pool, cursor);
                EmitPairsFor(b, pool, cursor, stationId);
            }
            finally
            {
                ResetIndex(b);
            }

            return DeltaResult.Applied;
        }

        /// <summary>
        /// 把 <paramref name="stationId"/> 参与的所有配对从对侧数组里摘掉，并清空它自己的。
        ///
        /// <para><b>找对侧不用扫全星球</b>：这个站点自己的 <c>localPairs</c> 里已经记着
        /// 每一条配对的另一端，所以对侧集合就是它。去重用的是<b>按站号打世代戳</b>
        /// 的数组，O(1) 判重且不分配——同一个对侧可能因为多个物品出现很多次，
        /// 不去重就会把它的数组反复压缩，退化成 O(配对数²)。</para>
        /// </summary>
        private static void DetachPairsOf(StationComponent[] pool, int cursor, int stationId)
        {
            StationComponent s = pool[stationId];

            if (s?.localPairs == null || s.localPairCount <= 0)
            {
                s?.ClearLocalPairs();

                return;
            }

            if (_stamp == null || _stamp.Length < cursor) _stamp = new int[cursor + 64];

            unchecked { _stampGen++; }

            if (_stampGen == 0) { Array.Clear(_stamp, 0, _stamp.Length); _stampGen = 1; }

            for (var p = 0; p < s.localPairCount; p++)
            {
                int other = s.localPairs[p].supplyId == stationId
                    ? s.localPairs[p].demandId
                    : s.localPairs[p].supplyId;

                if (other <= 0 || other >= cursor || other == stationId) continue;
                if (_stamp[other] == _stampGen) continue;

                _stamp[other] = _stampGen;

                Compact(pool[other], stationId);
            }

            s.ClearLocalPairs();
        }

        /// <summary>
        /// 就地压缩 <paramref name="o"/> 的配对数组，去掉所有提到
        /// <paramref name="stationId"/> 的条目。
        ///
        /// <b>顺序会变，这是有意的</b>——压缩必然改下标，而 <c>localPairProcess</c>
        /// 只是个轮询游标（<c>InternalTickLocal</c> 从它开始扫一圈），
        /// 下标移位最多让某一条被跳过或重看一次，扫完一圈的结果不变。
        /// </summary>
        private static void Compact(StationComponent o, int stationId)
        {
            if (o?.localPairs == null || o.localPairCount <= 0) return;

            var w = 0;

            for (var r = 0; r < o.localPairCount; r++)
            {
                if (o.localPairs[r].supplyId == stationId || o.localPairs[r].demandId == stationId) continue;

                if (w != r) o.localPairs[w] = o.localPairs[r];

                w++;
            }

            o.localPairCount = w;
        }

        /// <summary>
        /// 只发射 <paramref name="stationId"/> 参与的配对，两侧各存一份。
        ///
        /// <b>和全量版的唯一区别是没有「对侧站号必须大于自己」那道闸。</b>
        /// 那道闸是原版「从 this.id + 1 开始」的等价物，作用是让每条逻辑配对
        /// 在全量遍历里只被发射一次；而这里只遍历一个站点，不存在重复发射的问题，
        /// 留着它反而会漏掉所有站号比自己小的对侧。
        /// <c>other == stationId</c> 仍然要跳——原版从 <c>this.id + 1</c> 起，
        /// 所以它从不产生自配对。
        /// </summary>
        private static void EmitPairsFor(Buffers b, StationComponent[] pool, int cursor, int stationId)
        {
            StationComponent s = pool[stationId];

            for (var k = 0; k < s.storage.Length; k++)
            {
                int item = s.storage[k].itemId;

                if (item <= 0 || item >= b.SupplyHead.Length) continue;

                ELogisticStorage dir = s.storage[k].localLogic;

                if (dir == ELogisticStorage.Supply)
                {
                    for (int n = b.DemandHead[item]; n >= 0; n = b.NodeNext[n])
                    {
                        int other = b.NodeStation[n];

                        if (other == stationId || other <= 0 || other >= cursor) continue;
                        if (AlreadyEmitted(other)) continue;

                        StationComponent o = pool[other];

                        if (o == null || o.id != other) continue;

                        s.AddLocalPair(stationId, k, other, b.NodeSlot[n]);
                        o.AddLocalPair(stationId, k, other, b.NodeSlot[n]);
                    }
                }
                else if (dir == ELogisticStorage.Demand)
                {
                    for (int n = b.SupplyHead[item]; n >= 0; n = b.NodeNext[n])
                    {
                        int other = b.NodeStation[n];

                        if (other == stationId || other <= 0 || other >= cursor) continue;
                        if (AlreadyEmitted(other)) continue;

                        StationComponent o = pool[other];

                        if (o == null || o.id != other) continue;

                        s.AddLocalPair(other, b.NodeSlot[n], stationId, k);
                        o.AddLocalPair(other, b.NodeSlot[n], stationId, k);
                    }
                }
            }
        }

        /// <summary>
        /// 站点被拆除时，把它的配对从所有对侧摘掉——<b>不做全量重建</b>。
        ///
        /// <para><b>这一条是存在性变更，必须同步做完，不能进合并窗口。</b>
        /// <c>RemoveStationComponent</c> @02D1 调 <c>Reset()</c> 把 <c>storage</c> 置空、
        /// <c>id</c> 归零，而组件仍留在 <c>stationPool</c> 里等着被回收复用；
        /// 别人表里那条指着它的配对就成了悬空引用，
        /// <c>InternalTickLocal</c> @07D6 会在 <c>Monitor.Enter(null)</c> 上炸
        /// （本仓库 1.10.6 修过一次）。</para>
        ///
        /// <para><b>所以要在 <c>Reset()</c> 之前调</b>——那时候这个站点自己的
        /// <c>localPairs</c> 还完整，对侧集合直接就在里面。</para>
        /// </summary>
        internal static bool DetachOne(PlanetTransport transport, int stationId)
        {
            StationComponent[] pool = transport?.stationPool;

            if (pool == null) return false;

            int cursor = transport.stationCursor;

            if (cursor <= 1 || stationId <= 0 || stationId >= cursor) return false;

            StationComponent s = pool[stationId];

            if (s == null || s.id != stationId) return false;

            DetachPairsOf(pool, cursor, stationId);

            return true;
        }

        /// <summary>按站号打世代戳的去重数组，见 <see cref="DetachPairsOf"/>。</summary>
        [ThreadStatic] private static int[] _stamp;

        [ThreadStatic] private static int _stampGen;

        private static void Touch(Buffers b, int item)
        {
            if (b.TouchedCount == b.Touched.Length) Array.Resize(ref b.Touched, b.Touched.Length * 2);

            b.Touched[b.TouchedCount++] = item;
        }

        /// <summary>只清这一轮碰过的桶头，不清整张表——否则每次重建都要走一遍上万个物品位。</summary>
        private static void ResetIndex(Buffers b)
        {
            for (var t = 0; t < b.TouchedCount; t++)
            {
                int item = b.Touched[t];

                b.SupplyHead[item] = -1;
                b.DemandHead[item] = -1;
            }

            b.TouchedCount = 0;
            b.NodeCount = 0;
        }

        private static void EnsureHeads(Buffers b, int size)
        {
            if (b.SupplyHead.Length >= size) return;

            var cap = 256;

            while (cap < size) cap *= 2;

            var supply = new int[cap];
            var demand = new int[cap];

            for (var i = 0; i < cap; i++)
            {
                supply[i] = -1;
                demand[i] = -1;
            }

            b.SupplyHead = supply;
            b.DemandHead = demand;
        }

        private static void EnsureNodes(Buffers b, int size)
        {
            if (b.NodeNext.Length >= size) return;

            var cap = 256;

            while (cap < size) cap *= 2;

            b.NodeNext = new int[cap];
            b.NodeStation = new int[cap];
            b.NodeSlot = new int[cap];
        }
    }
}
