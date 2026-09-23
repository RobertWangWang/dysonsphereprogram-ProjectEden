using System.Collections.Generic;
using System.Threading;
using System.Text;
using HarmonyLib;
using ProjectEden.Utils;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把自定义稀有矿脉<b>具体在哪颗星球</b>找出来，打进日志。
    ///
    /// <b>为什么期望值不够用。</b> <see cref="RareVeinSurvey"/> 能算出「这一局大概有 3 颗」，
    /// 但玩家要的是「哪三颗」。候选行星有几十颗，一颗颗飞过去看不是办法，
    /// 而星图上未生成的星球压根没有矿脉数据可看——
    /// <c>PlanetData.runtimeVeinGroups</c> 在没有 factory 时只是读 <c>veinGroups</c> 字段，
    /// <b>它不触发生成</b>（CLAUDE.md 里曾说它会「现生成」，那句话不准，已改）。
    ///
    /// <b>做法：借游戏自己的扫描线程，不自己复算随机数。</b>
    /// <c>PlanetModelingManager.RequestScanPlanet</c> 正是星图扫描走的路，
    /// 扫描线程 <c>PlanetScanThreadMain</c> 会 <c>GetUnloadedCopy</c> 出一份副本、
    /// 在副本上跑 <c>GenerateTerrain</c> + <c>GenerateVeins</c> + <c>SummarizeVeinGroups</c>，
    /// 然后 <c>CopyScannedDataFrom</c> 把结果抄回真身、<c>ReleaseCopy</c> 把副本扔掉——
    /// <b>所以内存开销是一颗星球，不是几十颗</b>，而且结果和玩家自己飞过去看到的完全一致。
    ///
    /// <b>为什么不复算那次掷骰。</b> 想过：<c>GenerateVeins</c> 用的是
    /// <c>DotNet35Random(planet.seed)</c>，稀有矿那两次 <c>NextDouble</c> 在 IL 048C / 04B8。
    /// 但它们之前还有一段<b>次数取决于数据</b>的抽取（IL 03A4–03CD 那个最多 12 次、
    /// 失败即停的循环），要复算就得把前面整段一起模拟。一旦模拟和原版错开一次抽取，
    /// 它<b>不会报错，只会自信地报出错误的星球</b>——那比不报还糟。
    /// 借游戏自己的实现就没有这个失配面。
    ///
    /// 代价是它要在后台扫几十颗星球，每颗零点几秒。所以做成开关，
    /// 而且<b>关着的时候也打一行状态</b>（仓库为这条已经付过三次代价）。
    /// </summary>
    [HarmonyPatch]
    internal static class RareVeinProspector
    {
        /// <summary>同时挂起的扫描请求上限，免得和游戏自己的扫描抢线程。</summary>
        private const int MaxInFlight = 4;

        private const float PollSeconds = 0.4f;

        private static readonly List<PlanetData> Waiting = new List<PlanetData>();
        private static readonly List<PlanetData> InFlight = new List<PlanetData>();
        private static readonly Dictionary<int, List<string>> Hits = new Dictionary<int, List<string>>();
        private static readonly HashSet<int> RareVeinIds = new HashSet<int>();

        private static bool _running;
        private static float _nextPoll;
        private static int _total;
        private static int _scanned;

        /// <summary>上次有进展的时刻。扫描线程要是死了，挂起的请求永远不会回来。</summary>
        private static float _lastProgress;

        /// <summary>
        /// 多久没进展就认定扫描线程出事了。原版一颗星球零点几秒，30 秒足够宽。
        ///
        /// <b>但这个数必须跟着行星半径走，不能是个常量。</b> 扫描的工作量
        /// ∝ 顶点数 ∝ 半径²（<c>PlanetAuxData..ctor</c> @0015 把 <c>segment</c> 定成半径，
        /// 而格数 ∝ segment²），所以 planet.json 把半径从 200 翻到 400 之后，
        /// 同一颗星球要花 <b>4 倍</b>的时间——30 秒就从「足够宽」变成了会误报。
        /// 实测正是如此：136 颗扫到 16 颗被判定「扫描线程出事了」，而它并没有出事。
        ///
        /// 这是 CLAUDE.md 里那条「按次数计的节流只在每次代价恒定时成立」的又一例：
        /// 阈值的单位要和代价的单位一致，代价随世界规模涨，阈值就得跟着涨。
        /// </summary>
        private static float StallSeconds
        {
            get
            {
                int radius = PlanetRadiusPatches.Active ? PlanetRadiusPatches.Radius : VanillaRadius;

                if (radius <= 0) radius = VanillaRadius;

                float scale = (float)radius / VanillaRadius;

                return 30f * scale * scale;
            }
        }

        /// <summary>原版普通行星的半径。上面那个缩放的分母。</summary>
        private const int VanillaRadius = 200;

        /// <summary>
        /// 把这个阈值是怎么算出来的写进日志。报了状态才分得清
        /// 「线程真死了」和「只是这个数定小了」——上一版没有这一句，
        /// 于是一条正确格式的警告说了一件假事，还看不出假在哪。
        /// </summary>
        private static string StallBasis()
        {
            if (!PlanetRadiusPatches.Active || PlanetRadiusPatches.Radius <= 0) return "";

            float scale = (float)PlanetRadiusPatches.Radius / VanillaRadius;

            return $"（阈值已按行星半径 {PlanetRadiusPatches.Radius} 放大：扫描量 ∝ 半径²，"
                   + $"{scale:0.##}² = {scale * scale:0.#} 倍，30 秒 → {30f * scale * scale:0} 秒）";
        }

        private static int _waitLogged;

        /// <summary>
        /// 矿种数组够不够长。<c>GenerateVeins</c> 里的 veinSpots 等三个数组是
        /// <c>new [veinProtos.Length]</c>，然后<b>用矿种 ID 当下标</b>，
        /// 所以长度必须大于最大矿种号，否则稀有矿一命中就越界。
        /// </summary>
        private static bool ArraysReady()
        {
            VeinProto[] protos = PlanetModelingManager.veinProtos;

            return protos != null && protos.Length > OreRegistry.MaxVeinId;
        }

        /// <summary>由 <see cref="RareVeinSurvey"/> 在星区数完之后启动。</summary>
        internal static void Start(GalaxyData galaxy)
        {
            Waiting.Clear();
            InFlight.Clear();
            Hits.Clear();
            RareVeinIds.Clear();
            _running = false;
            _scanned = 0;

            bool on = AlienVeinPatches.Config?.prospectRareVeins ?? false;

            foreach (OreRegistry.Ore ore in OreRegistry.Ores)
                if (ore.Entry?.placement?.mode == "rare")
                    RareVeinIds.Add(ore.VeinId);

            // 关着也要报，否则「开关没开」和「这段代码根本没进 DLL」长得一样
            if (!on)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "稀有矿脉探矿：已关闭（alienvein.json 的 prospectRareVeins）。"
                    + "打开它会在后台用游戏自己的扫描把每颗候选星球扫一遍，逐条报出「哪个星系哪颗行星」");

                return;
            }

            if (RareVeinIds.Count == 0 || galaxy?.stars == null)
            {
                ProjectEdenPlugin.Log.LogInfo($"稀有矿脉探矿：没有 rare 矿脉（{RareVeinIds.Count} 种），不用扫");

                return;
            }

            foreach (StarData star in galaxy.stars)
            {
                if (star?.planets == null) continue;

                foreach (PlanetData planet in star.planets)
                {
                    if (planet == null || !ThemeCarriesAny(planet.theme)) continue;

                    // 已经有矿脉数据的（去过 / 已扫过）直接读，不用再排队
                    if (planet.veinGroups != null) Collect(planet);
                    else Waiting.Add(planet);
                }
            }

            _total = Waiting.Count;
            _running = true;
            _waitLogged = 0;
            _nextPoll = 0f;
            _lastProgress = Time.realtimeSinceStartup;

            ProjectEdenPlugin.Log.LogInfo(
                $"稀有矿脉探矿：开始后台扫描 {_total} 颗候选星球"
                + $"（{RareVeinIds.Count} 种稀有矿脉；已有数据的已直接读取）。"
                + "每颗零点几秒，扫完会给一份汇总");

            if (_total == 0) Finish();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameMain), nameof(GameMain.LateUpdate))]
        private static void GameMain_LateUpdate()
        {
            if (!_running) return;
            if (Time.realtimeSinceStartup < _nextPoll) return;

            _nextPoll = Time.realtimeSinceStartup + PollSeconds;

            // <b>定容没做完就别请求扫描。</b> 本类是在 UniverseGen.CreateGalaxy 启动的，
            // 而给矿种数组定容的 PlanetModelingManager.PrepareWorks 要到更晚的
            // PlanetModelingManager.Start 才跑（StartPlanetScanThread 也在那里）。
            // 抢在它前面把几十个扫描请求塞进队列，等于让扫描线程拿着还没定好容的数组去跑
            // GenerateVeins —— 那正是 VeinProtoArrayPatches 里记的那个越界。
            // 玩家自己是不会在这个时刻触发扫描的，所以这个时序只有本类会撞上。
            if (!ArraysReady())
            {
                if (Interlocked.Exchange(ref _waitLogged, 1) == 0)
                    ProjectEdenPlugin.Log.LogInfo("稀有矿脉探矿：矿种数组还没定容，等 PrepareWorks 跑完再扫");

                // 等定容的时间不算停滞，否则下面那个 30 秒判定会误报「扫描线程死了」
                _lastProgress = Time.realtimeSinceStartup;

                return;
            }

            // 收已经扫完的
            for (int i = InFlight.Count - 1; i >= 0; i--)
            {
                PlanetData planet = InFlight[i];

                if (planet.veinGroups == null && !planet.scanned) continue;

                InFlight.RemoveAt(i);
                _scanned++;
                _lastProgress = Time.realtimeSinceStartup;
                Collect(planet);
            }

            // 扫描线程死了的话挂起的请求永远回不来，会在这里空转到天荒地老
            if (Time.realtimeSinceStartup - _lastProgress > StallSeconds)
            {
                string err = PlanetModelingManager.planetScanThreadError;

                ProjectEdenPlugin.Log.LogWarning(
                    $"稀有矿脉探矿：{StallSeconds:0} 秒没有任何一颗星球扫完，判定扫描线程出事了，停止探矿。"
                    + $"已扫 {_scanned}/{_total} 颗，挂起 {InFlight.Count} 颗。"
                    + (string.IsNullOrEmpty(err) ? "游戏没有报错误信息" : "扫描线程的错误是：" + err)
                    + StallBasis());

                Finish();

                return;
            }

            // 补队
            while (InFlight.Count < MaxInFlight && Waiting.Count > 0)
            {
                PlanetData planet = Waiting[Waiting.Count - 1];
                Waiting.RemoveAt(Waiting.Count - 1);

                if (planet.veinGroups != null)
                {
                    _scanned++;
                    _lastProgress = Time.realtimeSinceStartup;
                    Collect(planet);

                    continue;
                }

                PlanetModelingManager.RequestScanPlanet(planet);
                InFlight.Add(planet);
            }

            if (Waiting.Count == 0 && InFlight.Count == 0) Finish();
        }

        private static void Collect(PlanetData planet)
        {
            VeinGroup[] groups = planet.veinGroups;

            if (groups == null) return;

            for (var i = 0; i < groups.Length; i++)
            {
                var type = (int)groups[i].type;

                if (!RareVeinIds.Contains(type)) continue;

                if (!Hits.TryGetValue(type, out List<string> list))
                {
                    list = new List<string>();
                    Hits[type] = list;
                }

                // 同一颗星球上同种矿脉可能有好几群，合并成一条
                string where = $"{planet.star.displayName} · {planet.displayName}";
                var merged = false;

                for (var j = 0; j < list.Count; j++)
                    if (list[j].StartsWith(where))
                    {
                        merged = true;

                        break;
                    }

                if (merged) continue;

                long amount = 0;
                var count = 0;

                for (var j = 0; j < groups.Length; j++)
                    if ((int)groups[j].type == type)
                    {
                        amount += groups[j].amount;
                        count += groups[j].count;
                    }

                list.Add($"{where}（{count} 个矿脉，储量 {amount:N0}）");
            }
        }

        private static void Finish()
        {
            _running = false;

            var sb = new StringBuilder($"── 稀有矿脉探矿结果（扫了 {_scanned}/{_total} 颗候选星球）──");

            foreach (OreRegistry.Ore ore in OreRegistry.Ores)
            {
                if (!RareVeinIds.Contains(ore.VeinId)) continue;

                sb.Append($"\n  {ore.Entry.veinName}：");

                if (!Hits.TryGetValue(ore.VeinId, out List<string> list) || list.Count == 0)
                {
                    sb.Append("**这一局一颗都没有**");

                    continue;
                }

                sb.Append($"{list.Count} 颗星球");

                foreach (string line in list) sb.Append("\n      ").Append(line);
            }

            sb.Append("\n  （这是游戏自己的扫描结果，和飞过去看到的一致；");
            sb.Append("已经去过的星球读的是存档里的数据）");

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }

        private static bool ThemeCarriesAny(int themeId)
        {
            ThemeProto theme = themeId > 0 ? LDB.themes.Select(themeId) : null;

            if (theme?.RareVeins == null) return false;

            for (var i = 0; i < theme.RareVeins.Length; i++)
                if (RareVeinIds.Contains(theme.RareVeins[i]))
                    return true;

            return false;
        }
    }
}
