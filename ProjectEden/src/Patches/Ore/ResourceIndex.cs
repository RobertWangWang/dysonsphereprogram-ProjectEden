using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches.Ore
{
    /// <summary>
    /// 全星系资源索引：<b>物品 → 哪些星球有、各有多少</b>。给游戏内的资源搜索窗供数据。
    ///
    /// <para><b>数据来源是游戏自己的扫描线程，绝不自己摇随机数。</b>
    /// 这一条是 <see cref="RareVeinProspector"/> 用血换来的：<c>GenerateVeins</c> 里
    /// 构造 <c>DotNet35Random(planet.seed)</c> 和稀有矿那两次抽签之间，隔着一个
    /// <b>数据相关</b>的循环（最多 12 次、失败即停）。自己模拟一旦错开一次抽签
    /// <b>不会抛异常，它会理直气壮地报出错误的星球</b>——比报不出来更糟。
    /// 所以这里走 <c>PlanetModelingManager.RequestScanPlanet</c>，
    /// 拿到的结果和玩家飞过去看到的逐字一致。</para>
    ///
    /// <para><b>矿脉不进存档</b>：<c>PlanetData</c> 的 Export/Import 里没有
    /// <c>veinGroups</c> / <c>veinPool</c>，每次都是从种子重算的（三个调用方
    /// 全在建模 / 扫描线程里）。所以索引可以随时重建，不存在「缓存脏了」。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class ResourceIndex
    {
        /// <summary>一条结果：某颗星球上有多少这种资源。</summary>
        internal class Entry
        {
            internal PlanetData Planet;

            /// <summary>矿脉数。气态巨星上没有这个概念，填 0。</summary>
            internal int Veins;

            /// <summary>储量；气态巨星上是「采集速率」那一档的原始值。</summary>
            internal long Amount;

            /// <summary>气态巨星的气体。展示时要和矿脉分开说。</summary>
            internal bool Gas;
        }

        /// <summary>物品 ID → 有这种资源的星球。<b>只在主线程读写</b>（LateUpdate 与 UI）。</summary>
        private static readonly Dictionary<int, List<Entry>> ByItem = new Dictionary<int, List<Entry>>();

        private static readonly List<PlanetData> Waiting = new List<PlanetData>();
        private static readonly List<PlanetData> InFlight = new List<PlanetData>();

        /// <summary>同时挂起的扫描请求上限，免得和游戏自己的扫描抢线程。</summary>
        private const int MaxInFlight = 4;

        private const float PollSeconds = 0.4f;

        /// <summary>多久没进展就认定扫描线程出事了。一颗星球零点几秒，30 秒足够宽。</summary>
        private const float StallSeconds = 30f;

        private static bool _running;
        private static float _nextPoll;
        private static float _lastProgress;
        private static int _total;
        private static int _scanned;
        private static int _waitLogged;

        internal static bool Running => _running;

        internal static int Scanned => _scanned;

        internal static int Total => _total;

        /// <summary>索引里一共有多少种资源——状态行用，0 和「还没开始扫」要分得开。</summary>
        internal static int Kinds => ByItem.Count;

        /// <summary>
        /// 开始建索引。可以反复调用：每次都从头扫，因为矿脉本来就不进存档。
        /// </summary>
        internal static void Rebuild()
        {
            Waiting.Clear();
            InFlight.Clear();
            ByItem.Clear();

            _running = false;
            _scanned = 0;
            _total = 0;
            _waitLogged = 0;

            GalaxyData galaxy = GameMain.galaxy;

            if (galaxy?.stars == null)
            {
                ProjectEdenPlugin.Log.LogWarning("资源索引：星系还没生成，这次不建");

                return;
            }

            foreach (StarData star in galaxy.stars)
            {
                if (star?.planets == null) continue;

                foreach (PlanetData planet in star.planets)
                {
                    if (planet == null) continue;

                    // 气态巨星的气体在 gasItems 里，不用扫矿脉就能读
                    CollectGas(planet);

                    // 已经有矿脉数据的（去过 / 已扫过）直接读，不用排队
                    if (planet.veinGroups != null) CollectVeins(planet);
                    else Waiting.Add(planet);
                }
            }

            _total = Waiting.Count;
            _running = _total > 0;
            _nextPoll = 0f;
            _lastProgress = Time.realtimeSinceStartup;

            ProjectEdenPlugin.Log.LogInfo(
                $"资源索引：开始后台扫描 {_total} 颗星球（已有数据的已直接读取，"
                + $"当前已收 {ByItem.Count} 种资源）。用的是游戏自己的扫描线程，"
                + "结果和飞过去看到的一致");

            // <b>一颗都不用扫的时候也必须收尾。</b> 漏了这一句的后果不是少打一行日志——
            // <see cref="Finish"/> 还负责把每种资源的星球列表按矿脉数排序，
            // 所以「全部星球都已经有数据」这条最常见的路径反而<b>拿不到排序</b>，
            // 窗口里是插入顺序。
            //
            // 而它是<b>从那行缺失的日志反推出来的</b>：日志里有「开始扫描 0 颗」却没有
            // 「完成」，两行之间的空白就是这个 bug。本文件记过很多次
            // 「计数为 0 时整条日志被跳过，于是『没事可做』和『根本没跑』分不开」——
            // 这次是同一个形状，只不过缺的那一行顺带带走了一个功能。
            if (_total == 0) Finish();
        }

        /// <summary>查某种资源在哪些星球上，按「矿脉数多的在前」排好。</summary>
        internal static List<Entry> Find(int itemId)
        {
            return ByItem.TryGetValue(itemId, out List<Entry> list) ? list : null;
        }

        /// <summary>索引里出现过的全部物品 ID——搜索框列候选用。</summary>
        internal static IEnumerable<int> Items => ByItem.Keys;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameMain), nameof(GameMain.LateUpdate))]
        private static void GameMain_LateUpdate()
        {
            if (!_running) return;
            if (Time.realtimeSinceStartup < _nextPoll) return;

            _nextPoll = Time.realtimeSinceStartup + PollSeconds;

            // <b>定容没做完就别请求扫描。</b> GenerateVeins 里的 veinSpots 等三个数组是
            // new [veinProtos.Length] 再用矿种 ID 当下标，定容要到
            // PlanetModelingManager.PrepareWorks 才做。抢在它前面塞请求，等于让扫描线程
            // 拿着还没定好容的数组去跑 —— 那正是 VeinProtoArrayPatches 记的那个越界。
            if (!ArraysReady())
            {
                if (Interlocked.Exchange(ref _waitLogged, 1) == 0)
                    ProjectEdenPlugin.Log.LogInfo("资源索引：矿种数组还没定容，等 PrepareWorks 跑完再扫");

                // 等定容不算停滞，否则下面 30 秒那道判定会误报「扫描线程死了」
                _lastProgress = Time.realtimeSinceStartup;

                return;
            }

            for (int i = InFlight.Count - 1; i >= 0; i--)
            {
                PlanetData planet = InFlight[i];

                if (planet.veinGroups == null && !planet.scanned) continue;

                InFlight.RemoveAt(i);
                _scanned++;
                _lastProgress = Time.realtimeSinceStartup;
                CollectVeins(planet);
            }

            // 扫描线程死了的话挂起的请求永远回不来，会在这里空转到天荒地老
            if (Time.realtimeSinceStartup - _lastProgress > StallSeconds)
            {
                string err = PlanetModelingManager.planetScanThreadError;

                ProjectEdenPlugin.Log.LogWarning(
                    $"资源索引：{StallSeconds:0} 秒没有任何一颗星球扫完，判定扫描线程出事了，停止建索引。"
                    + $"已扫 {_scanned}/{_total} 颗，挂起 {InFlight.Count} 颗。"
                    + (string.IsNullOrEmpty(err) ? "游戏没有报错误信息" : "扫描线程的错误是：" + err));

                Finish();

                return;
            }

            while (InFlight.Count < MaxInFlight && Waiting.Count > 0)
            {
                PlanetData planet = Waiting[Waiting.Count - 1];
                Waiting.RemoveAt(Waiting.Count - 1);

                if (planet.veinGroups != null)
                {
                    _scanned++;
                    _lastProgress = Time.realtimeSinceStartup;
                    CollectVeins(planet);

                    continue;
                }

                PlanetModelingManager.RequestScanPlanet(planet);
                InFlight.Add(planet);
            }

            if (Waiting.Count == 0 && InFlight.Count == 0) Finish();
        }

        private static void Finish()
        {
            _running = false;

            foreach (List<Entry> list in ByItem.Values)
                list.Sort(CompareEntries);

            ProjectEdenPlugin.Log.LogInfo(
                $"资源索引：完成，扫了 {_scanned}/{_total} 颗星球，收进 {ByItem.Count} 种资源。"
                + "按 " + UI.ResourceSearchWindow.HotkeyText + " 打开搜索窗");
        }

        /// <summary>矿脉多的排前面；一样多就按储量。</summary>
        private static int CompareEntries(Entry a, Entry b)
        {
            if (a.Veins != b.Veins) return b.Veins.CompareTo(a.Veins);

            return b.Amount.CompareTo(a.Amount);
        }

        private static void CollectVeins(PlanetData planet)
        {
            VeinGroup[] groups = planet.veinGroups;

            if (groups == null) return;

            for (var i = 0; i < groups.Length; i++)
            {
                var type = (int)groups[i].type;

                if (type <= 0) continue;

                int itemId = ProductOf(type);

                if (itemId <= 0) continue;

                Add(itemId, planet, groups[i].count, groups[i].amount, false);
            }
        }

        private static void CollectGas(PlanetData planet)
        {
            int[] items = planet.gasItems;

            if (items == null) return;

            float[] speeds = planet.gasSpeeds;

            for (var i = 0; i < items.Length; i++)
            {
                if (items[i] <= 0) continue;

                // 气体没有「矿脉数」，把采集速率当储量那一栏用，展示时会标成气态
                var rate = (long)((speeds != null && i < speeds.Length ? speeds[i] : 0f) * 10000f);

                Add(items[i], planet, 0, rate, true);
            }
        }

        /// <summary>
        /// 矿种 → 产出物品。<b>读 <c>PlanetModelingManager.veinProducts</c>，不查 LDB。</b>
        /// 那张表就是游戏自己用的那一张，而且本仓库已经有一整套补丁保证它够长、填满
        /// （见 <c>VeinProtoArrayPatches</c>）；绕开它去查 LDB 等于又多一份会走样的手抄件。
        /// </summary>
        private static int ProductOf(int veinType)
        {
            int[] products = PlanetModelingManager.veinProducts;

            if (products == null || veinType >= products.Length) return 0;

            return products[veinType];
        }

        private static void Add(int itemId, PlanetData planet, int veins, long amount, bool gas)
        {
            if (!ByItem.TryGetValue(itemId, out List<Entry> list))
            {
                list = new List<Entry>();
                ByItem[itemId] = list;
            }

            // 同一颗星球上同种矿可能有好几群，合并成一条——玩家关心的是「去哪颗星球」
            for (var i = 0; i < list.Count; i++)
                if (list[i].Planet == planet)
                {
                    list[i].Veins += veins;
                    list[i].Amount += amount;

                    return;
                }

            list.Add(new Entry { Planet = planet, Veins = veins, Amount = amount, Gas = gas });
        }

        /// <summary>
        /// 矿种数组够不够长。<c>GenerateVeins</c> 用矿种 ID 当下标去写三个
        /// <c>new [veinProtos.Length]</c> 的数组，长度不够一命中就越界。
        /// </summary>
        private static bool ArraysReady()
        {
            int[] products = PlanetModelingManager.veinProducts;

            return products != null && products.Length > OreRegistry.MaxVeinId;
        }
    }
}
