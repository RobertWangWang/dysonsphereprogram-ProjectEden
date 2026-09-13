using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 品质的<b>总量上限</b>：一格货的品质总点数不得超过 <c>件数 × 每件上限</c>，
    /// 超了就削到上限。读档时扫一遍，之后每 30 秒再扫一遍。
    ///
    /// <b>这是所有者拍板的定位改变，值得写清楚。</b> 早先它叫「读档修复」，
    /// 立场是「病因修掉了，这是存量，修一次就该干净」——于是每修一次就 ERROR 一次，
    /// 把「还在漏」当成待查的缺陷。追了四轮，堵掉的洞一个比一个深
    /// （前缀顶掉改写过的方法、本 mod 直接调搬运方法没写侧信道、手写搬运只扣件数），
    /// 每次都还剩下一条。
    ///
    /// 所以立场改成：**不追了，但保证有界。** 品质是可加点数，
    /// 而可加量真正危险的不是「偏高」，是<b>没有上限</b>——那就是无限循环。
    /// 一条常设的上限把它钉死在「每件都是满分」这个物理意义上的天花板上，
    /// 剩下的偏差是有界的、可解释的，而且玩家看到的数永远在量纲内。
    ///
    /// <b>削而不清零。</b> 削到上限保留了「这批货是提纯过的」这个事实，
    /// 清零会把玩家真炼出来的东西一起没收。
    ///
    /// <b>不再报 ERROR。</b> 它现在是常设机制而不是缺陷探针——每 30 秒吼一次
    /// 只会把日志填满，而且会让真正的新问题淹在里面。整局只在第一次真的削了东西时
    /// 说一句，说明这条上限在工作。
    ///
    /// 传送带上的货（<c>Cargo.qua</c>）没扫：它在传送带上停留的时间以秒计，
    /// 而两头的容器都扫了，扫它只是在追一个正在流动的影子。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityRepairPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
        private static void GameData_Import()
        {
            _nextWatch = 0f;
            _saidSoOnce = false;

            Run("读档后");
        }

        // ── 玩的过程中也盯着，而不是只在读档时查一次 ──────────

        private static float _nextWatch;
        private static bool _saidSoOnce;

        /// <summary>
        /// 每 30 秒把上限重新压一遍：<b>机甲背包</b> + <b>当前所在星球</b>的物流站槽位。
        ///
        /// <b>它是执行机制，不是探针。</b> 读档那一次只能保证存档进来时是有界的；
        /// 品质是在玩的过程中流动的，不定期压一次就等于没有上限。
        ///
        /// 范围有意收窄到「玩家看得见的那一圈」：背包是一个 <c>StorageComponent</c>、
        /// 几十格；本星球的物流站是有界的一批。全图每颗星球扫一遍留给读档那一次，
        /// 每 30 秒做那件事会在大存档上变成一次可感的卡顿，而收益只是让看不见的货
        /// 早几分钟被压回去。
        ///
        /// 挂在 <c>UIGame._OnUpdate</c> 上是因为它<b>在主线程</b>——
        /// 物流站那条 tick 是跨星球并行的（约 31 个工作线程），在那上面改共享状态
        /// 是本仓库记过的第 4 号坑。节流用 <c>realtimeSinceStartup</c> 而不是
        /// <c>gameTick</c>：后者换存档时会往回跳，定时就再也不会到期，而且悄无声息。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate()
        {
            if (!QualityAccess.GridWritable && !QualityAccess.Ready) return;

            float now = UnityEngine.Time.realtimeSinceStartup;

            if (now < _nextWatch) return;

            _nextWatch = now + 30f;

            var worst = 0;
            var touched = 0;

            if (QualityAccess.GridWritable)
                touched += ClampStorage(GameMain.mainPlayer?.package, ref worst);

            // 本星球的物流站槽位。跨星球那一圈交给读档那一次。
            PlanetTransport transport = GameMain.localPlanet?.factory?.transport;

            if (QualityAccess.Ready && transport?.stationPool != null)
                for (var s = 1; s < transport.stationCursor; s++)
                {
                    StationComponent station = transport.stationPool[s];

                    if (station == null || station.id != s || station.storage == null) continue;

                    for (var k = 0; k < station.storage.Length; k++)
                        if (ClampStore(ref station.storage[k], ref worst))
                            touched++;
                }

            if (touched <= 0 || _saidSoOnce) return;

            _saidSoOnce = true;

            // **整局只说一次。** 它是常设机制而不是缺陷探针——每 30 秒吼一次
            // 只会把日志填满，还会让真正的新问题淹在里面。说一次是为了让
            // 「上限在工作」和「这段代码根本没跑」在日志上分得开。
            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质：品质总量上限正在生效——这一局第一次压回，{touched} 格超过了" +
                $"「件数 × {QualityRefineryPatches.MaxPerItem}」，最高曾到每件 {worst} 分。" +
                "上限每 30 秒压一次，之后不再重复这一行。");
        }

        internal static void Run(string why)
        {
            if (!QualityAccess.Ready && !QualityAccess.GridWritable) return;

            GameData data = GameMain.data;

            if (data == null) return;

            var slots = 0;
            var grids = 0;
            var worst = 0;

            // 一、每颗星球的物流站槽位
            if (QualityAccess.Ready && data.factories != null)
                for (var i = 0; i < data.factoryCount; i++)
                {
                    PlanetTransport transport = data.factories[i]?.transport;

                    if (transport?.stationPool == null) continue;

                    for (var s = 1; s < transport.stationCursor; s++)
                    {
                        StationComponent station = transport.stationPool[s];

                        if (station == null || station.id != s || station.storage == null) continue;

                        for (var k = 0; k < station.storage.Length; k++)
                            if (ClampStore(ref station.storage[k], ref worst))
                                slots++;
                    }
                }

            if (!QualityAccess.GridWritable) { Report(why, slots, grids, worst); return; }

            // 二、每颗星球的储物箱
            if (data.factories != null)
                for (var i = 0; i < data.factoryCount; i++)
                {
                    FactoryStorage storage = data.factories[i]?.factoryStorage;

                    if (storage?.storagePool == null) continue;

                    for (var s = 1; s < storage.storageCursor; s++)
                        grids += ClampStorage(storage.storagePool[s], ref worst);
                }

            // 三、机甲背包。**它必须单独扫**：背包不挂在任何一颗星球上，
            // 而玩家手里那一堆恰恰是最容易被看到的那一堆。
            grids += ClampStorage(data.mainPlayer?.package, ref worst);

            Report(why, slots, grids, worst);
        }

        private static bool ClampStore(ref StationStore store, ref int worst)
        {
            if (store.itemId <= 0) return false;

            int qua = QualityAccess.GetStationQua(ref store);

            if (qua <= 0) return false;

            int cap = store.count * QualityRefineryPatches.MaxPerItem;

            if (qua <= cap) return false;

            if (store.count > 0)
            {
                int per = qua / store.count;

                if (per > worst) worst = per;
            }

            QualityAccess.SetStationQua(ref store, cap);

            return true;
        }

        private static int ClampStorage(StorageComponent storage, ref int worst)
        {
            if (storage?.grids == null) return 0;

            var fixedUp = 0;

            for (var i = 0; i < storage.grids.Length; i++)
            {
                if (storage.grids[i].itemId <= 0) continue;

                int qua = QualityAccess.GetGridQua(ref storage.grids[i]);

                if (qua <= 0) continue;

                int cap = storage.grids[i].count * QualityRefineryPatches.MaxPerItem;

                if (qua <= cap) continue;

                if (storage.grids[i].count > 0)
                {
                    int per = qua / storage.grids[i].count;

                    if (per > worst) worst = per;
                }

                QualityAccess.SetGridQua(ref storage.grids[i], cap);

                fixedUp++;
            }

            return fixedUp;
        }

        /// <summary>
        /// <b>干净也要报一行。</b> 只在削了东西时说话，会让「这一局没有越界」和
        /// 「这段代码根本没跑」在日志上长得一模一样——本仓库为这个形状付过五次往返。
        ///
        /// <b>但它不再是 ERROR。</b> 上限是常设机制：削到了就是它在干活，
        /// 不是待查的缺陷。把它报成错误，只会让日志里真正的新问题被淹掉。
        /// </summary>
        private static void Report(string why, int slots, int grids, int worst)
        {
            if (slots == 0 && grids == 0)
            {
                ProjectEdenPlugin.Log.LogInfo($"物品品质：{why}核对完毕，没有超过总量上限的。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质：{why}按总量上限压回——物流站槽位 {slots} 格、储物格 {grids} 格，" +
                $"最高曾到每件 {worst} 分。上限是「件数 × {QualityRefineryPatches.MaxPerItem}」，" +
                "也就是「这一格每一件都是满分」——削而不清零，提纯过的事实保留着。");
        }
    }
}
