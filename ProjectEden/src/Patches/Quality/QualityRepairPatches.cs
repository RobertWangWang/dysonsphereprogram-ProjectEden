using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 读档后把<b>越界的品质夹回上限</b>，并报出修了多少格。
    ///
    /// <b>为什么需要它：病因修掉了，存量不会自己好。</b>
    /// 品质是按比例跟着货走的——一格 502 分的铜块，搬到哪儿都还是 502 分，
    /// 分一半走也是两堆 502 分。所以只要那批货还在，屏幕上就一直是错的数，
    /// 而玩家没有任何办法分辨「这是老账」还是「还在漏」。
    ///
    /// 病因是 <c>StationExpandPatches</c> 那个 <c>return false</c> 的前缀顶掉了
    /// preloader 改写过的 <c>StationComponent.AddItem</c>：侧信道寄存器里的品质
    /// 既没入库、也没被消费，留给了下一个读它的方法（实测单件涨到 502，上限是 100）。
    ///
    /// <b>夹而不是清零。</b> 夹回上限保留了「这批货是提纯过的」这个事实，
    /// 清零会把玩家真的炼出来的东西也一起没收——修复不该比 bug 本身更伤人。
    ///
    /// <b>每次读档都跑，不是只跑一次。</b> 它同时是一道<b>常设的安全网</b>：
    /// 将来再出现一条只搬件数不搬品质的路径，这里会把它夹住并报出来，
    /// 而不是让那个数悄悄涨到天上去。这和 <c>CargoIncClampPatches</c> 的定位一样——
    /// <b>把静默的错误变成确定的降级加一行日志</b>。
    ///
    /// 传送带上的货（<c>Cargo.qua</c>）没扫：它在传送带上停留的时间以秒计，
    /// 而且两头的容器都扫了，扫它只是在追一个正在流动的影子。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityRepairPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
        private static void GameData_Import() => Run("读档后");

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
        /// <b>干净也要报一行。</b> 只在修了东西时说话，会让「这一局没问题」和
        /// 「这段代码根本没跑」在日志上长得一模一样——本仓库为这个形状付过五次往返。
        /// </summary>
        private static void Report(string why, int slots, int grids, int worst)
        {
            if (slots == 0 && grids == 0)
            {
                ProjectEdenPlugin.Log.LogInfo($"物品品质：{why}核对完毕，没有越界的品质。");

                return;
            }

            ProjectEdenPlugin.Log.LogWarning(
                $"物品品质：{why}修正了越界的品质——物流站槽位 {slots} 格、储物格 {grids} 格，" +
                $"最高曾到每件 {worst} 分（上限 {QualityRefineryPatches.MaxPerItem}）。" +
                "这是修掉病因之前留下的存量，已按上限夹回；" +
                "**如果以后每次读档都还在报，说明还有一条只搬件数不搬品质的路径。**");
        }
    }
}
