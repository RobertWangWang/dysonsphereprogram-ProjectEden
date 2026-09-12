using System.Text;
using System.Threading;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把采矿机自带物流站的格位情况打进启动日志与首次运行日志。
    ///
    /// <b>为什么要问这个。</b> 外星矿脉那条链要给采矿机加一个「钻头」槽，而
    /// <c>MinerComponent</c> 一个原料槽都没有——唯一能放东西的地方是它自带的
    /// <c>StationComponent</c>。设计稿 `外星矿脉V1.md` §4.4 列的两个未知数就是这里要答的。
    ///
    /// <b>离线已经读出来的部分（不用探针，写在这里免得重读）：</b>
    ///
    /// <list type="number">
    /// <item><c>StationComponent.Init</c> IL 01B5：
    /// <c>storage = new StationStore[_desc.stationMaxItemKinds]</c>——
    /// <b>数组长度就是这个字段</b>，抬高它就有空格。</item>
    /// <item>Init 里两个填充循环（气体采集器 0344、矿脉采集器 0420）<b>都只跑到
    /// <c>collectionIds.Length</c></b>，并且以 <c>i &gt; stationMaxItemKinds - 1</c> 提前跳出。
    /// 所以超出矿种数的格子<b>不会被写</b>，留在默认值等我们用。</item>
    /// <item><c>StationComponent.UpdateVeinCollection</c> 全方法 174 条指令里，
    /// 每一处 <c>ldelema StationStore</c> 前面都是 <c>ldc.i4.0</c>——
    /// <b>它只碰 storage[0]</b>，不会冲掉第二格。</item>
    /// </list>
    ///
    /// <b>探针要答的是剩下那个：这台机器的 <c>stationMaxItemKinds</c> 到底是几。</b>
    /// 它在 prefab 里，也就是 <c>resources.assets</c>，离线读不到——和燃料位、
    /// 增产剂参数是同一种情况，做法照抄 <c>FuelSurvey</c> / <c>ProliferatorSurvey</c>。
    ///
    /// 顺带报 <c>StationCapacityPatches</c> 认不认得它：那边的格数提升按
    /// <c>isStation || isCollectStation</c> 过滤，<b>而采矿机是 <c>isVeinCollector</c></b>，
    /// 两个标志都不带的话它现在是被整个跳过的。
    /// </summary>
    internal static class MinerStationSurvey
    {
        private static bool _prefabDone;
        private static int _liveLogged;

        // ── 启动时：prefab 侧 ──────────────────────────────────

        internal static void OnPostAddData()
        {
            if (_prefabDone) return;

            _prefabDone = true;

            ItemProto[] items = LDB.items?.dataArray;

            if (items == null)
            {
                ProjectEdenPlugin.Log.LogWarning("采矿机物流站普查：LDB.items 还没建好，跳过");
                return;
            }

            var found = 0;

            foreach (ItemProto proto in items)
            {
                PrefabDesc desc = proto?.prefabDesc;

                if (desc == null || !desc.isVeinCollector) continue;

                found++;

                ProjectEdenPlugin.Log.LogInfo(
                    $"── 矿脉采集建筑：{proto.Name}({proto.ID}) ──\n"
                    + $"  储物格数 stationMaxItemKinds = {desc.stationMaxItemKinds}"
                    + $"（storage 数组长度就是它，Init IL 01B5）\n"
                    + $"  每格容量 stationMaxItemCount = {desc.stationMaxItemCount}\n"
                    + $"  标志：isVeinCollector={desc.isVeinCollector}"
                    + $" isStation={desc.isStation}"
                    + $" isCollectStation={desc.isCollectStation}"
                    + $" isStellarStation={desc.isStellarStation}\n"
                    + $"  → StationCapacityPatches 的格数提升{(desc.isStation || desc.isCollectStation ? "覆盖得到它" : "**覆盖不到它**（那边按 isStation || isCollectStation 过滤）")}\n"
                    + $"  → 要加钻头槽，得把 stationMaxItemKinds 抬到 {desc.stationMaxItemKinds + 1} 或更高");
            }

            if (found == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    "采矿机物流站普查：没找到 isVeinCollector 的建筑 —— 这不正常，原版至少有大型采矿机");
        }

        // ── 首次运行：活的那一台 ──────────────────────────────

        /// <summary>
        /// 第一台跑起来的采矿机，把它真实的格位布局打一遍。
        ///
        /// <b>prefab 是「新建的会是什么样」，这里是「已经建好的现在是什么样」。</b>
        /// 两者能差开——<c>storage</c> 在建造时就固化进存档了，之后改 prefab 不影响它
        /// （陷阱一）。要加钻头槽就必须知道老机器手里那个数组有多长。
        ///
        /// <b>跑在 <c>_miner_parallel</c> 上，所以用 Interlocked 抢占，而且先抢占再拼字符串</b>——
        /// 否则每帧都会在 tick 路径上分配一个插值字符串。
        /// </summary>
        internal static void ReportOnce(ref MinerComponent miner, PlanetFactory factory)
        {
            if (_liveLogged != 0) return;
            if (factory?.entityPool == null || factory.transport?.stationPool == null) return;

            int entityId = miner.entityId;

            if (entityId <= 0 || entityId >= factory.entityPool.Length) return;

            int stationId = factory.entityPool[entityId].stationId;

            if (stationId <= 0 || stationId >= factory.transport.stationPool.Length) return;

            StationComponent station = factory.transport.stationPool[stationId];

            if (station?.storage == null) return;

            if (Interlocked.Exchange(ref _liveLogged, 1) != 0) return;

            var sb = new StringBuilder("── 已建成采矿机的物流站实况（第一台）──");

            sb.Append($"\n  实体 {entityId} / 站点 {stationId}")
              .Append($"，storage 长度 {station.storage.Length}")
              .Append($"，collectionIds 长度 {station.collectionIds?.Length ?? 0}")
              .Append($"，isVeinCollector={station.isVeinCollector}");

            for (var i = 0; i < station.storage.Length; i++)
            {
                StationStore s = station.storage[i];
                ItemProto item = s.itemId > 0 ? LDB.items.Select(s.itemId) : null;

                sb.Append($"\n    储物格 {i}：")
                  .Append(item != null ? $"{item.Name}({s.itemId})" : "（空）")
                  .Append($" 本地 {s.localLogic} 远程 {s.remoteLogic}")
                  .Append($" 数量 {s.count}/{s.max}");
            }

            // 空格就是钻头槽能落脚的地方；一格不剩的话就得先抬 stationMaxItemKinds
            var free = 0;

            foreach (StationStore s in station.storage)
                if (s.itemId <= 0)
                    free++;

            sb.Append($"\n  空格 {free} 个 → ")
              .Append(free > 0
                  ? "可以直接拿一格当钻头槽（设成 Demand，无人机会自动送）"
                  : "**一格不剩**，要先把 prefab 的 stationMaxItemKinds 抬高，"
                    + "而且只对之后新建的采矿机有效（storage 在建造时固化进存档）");

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }
    }
}
