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
        /// 重建这颗星球的本地配对表。<paramref name="keys"/> 是这一轮合并窗口里
        /// 攒下的变更站点，每个都要补跑一次原版的无人机订单修复。
        /// </summary>
        internal static void Rebuild(PlanetTransport transport,
            System.Collections.Concurrent.ConcurrentDictionary<int, byte> keys)
        {
            StationComponent[] pool = transport.stationPool;

            if (pool == null) return;

            int cursor = transport.stationCursor;

            if (cursor <= 1) return;

            int droneCarries = GameMain.history.logisticDroneCarries;

            Buffers b = _buf ?? (_buf = new Buffers());

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

            int planetId = transport.factory?.planetId ?? 0;
            int n = RebuildsByPlanet.AddOrUpdate(planetId, 1, (_, old) => old + 1);

            if (n <= AuditFirstN || n % AuditEvery == 0)
            {
                long auditBegan = System.Diagnostics.Stopwatch.GetTimestamp();

                Audit(transport, pool, cursor, droneCarries, planetId, n);

                _auditTicks += System.Diagnostics.Stopwatch.GetTimestamp() - auditBegan;
                _auditRuns++;
            }

            // 原版的无人机订单修复。**cursor 传 0 = 跳过匹配、只跑后半段**——
            // 依据是 arg2 在全方法里只出现在 @00BA 和 @0179 两处循环上界，
            // 而匹配循环从 this.id + 1 起、this.id ≥ 1，对 0 的 blt 一次都进不去。
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

        /// <summary>顺序敏感的校验和。四个字段各自参与，避免「换了位置但和不变」。</summary>
        private static long Checksum(StationComponent[] pool, int cursor)
        {
            long h = 1469598103934665603L;

            for (var i = 1; i < cursor; i++)
            {
                StationComponent s = pool[i];

                if (s == null || s.id != i || s.localPairs == null) continue;

                h = (h ^ s.localPairCount) * 1099511628211L;

                for (var p = 0; p < s.localPairCount; p++)
                {
                    h = (h ^ s.localPairs[p].supplyId) * 1099511628211L;
                    h = (h ^ s.localPairs[p].supplyIndex) * 1099511628211L;
                    h = (h ^ s.localPairs[p].demandId) * 1099511628211L;
                    h = (h ^ s.localPairs[p].demandIndex) * 1099511628211L;
                }
            }

            return h;
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
