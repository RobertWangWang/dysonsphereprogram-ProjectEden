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
    /// <b>实测答案（2316 大型采矿机）：<c>stationMaxItemKinds = 1</c>，而且那一格被矿占满。</b>
    /// 所以钻头槽必须靠抬高这个字段来腾，抬到 <b>2</b> 就够——
    /// 别抬到 30，多出来的空格会把 30 格的物流站界面拖上来（气体采集器那条老教训）。
    ///
    /// <b>它现在没被 <see cref="StationCapacityPatches"/> 改过，但原因不是标志。</b>
    /// 采矿机的 <c>isStation</c> 其实是 <c>true</c>，能过那边 <c>Apply()</c> 的守卫；
    /// 真正的原因是 <c>Apply()</c> <b>只对一份固定名单调用</b>（stations.json 的
    /// <c>itemIds</c>、巨型建筑、machines.json 里 kind 为 station 的，
    /// 外加按 <c>isCollectStation</c> 发现的采集器），采矿机一个都不在里面。
    /// 本类头一版就是按标志推的，报出「覆盖得到它」而实测是 1 格，结论正好反了——
    /// <b>验最终状态，不验自己那一步。</b>
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

                // <b>报测出来的结果，不报「按标志推应该怎样」。</b>
                // 头一版这里写的是 isStation || isCollectStation ? "覆盖得到它" : ... ——
                // 那是 StationCapacityPatches 内部 Apply() 的守卫条件，可它遍历的是一份
                // <b>固定名单</b>（stations.json 的 itemIds + 巨型建筑 + machines.json 的
                // station，外加按 isCollectStation 发现的采集器），采矿机一个都不在里面，
                // Apply 根本没被调用。于是探针报「覆盖得到」而实测是 1 格，结论正好反了。
                // <b>这就是仓库那条「验最终状态，不验自己那一步」。</b>
                int want = ProjectEdenPlugin.StationsConfig?.stationMaxItemKinds ?? 0;
                bool raised = want > 0 && desc.stationMaxItemKinds >= want;

                ProjectEdenPlugin.Log.LogInfo(
                    $"── 矿脉采集建筑：{proto.Name}({proto.ID}) ──\n"
                    + $"  储物格数 stationMaxItemKinds = {desc.stationMaxItemKinds}"
                    + $"（storage 数组长度就是它，Init IL 01B5）\n"
                    + $"  每格容量 stationMaxItemCount = {desc.stationMaxItemCount}\n"
                    + $"  标志：isVeinCollector={desc.isVeinCollector}"
                    + $" isStation={desc.isStation}"
                    + $" isCollectStation={desc.isCollectStation}"
                    + $" isStellarStation={desc.isStellarStation}\n"
                    + $"  → stations.json 要 {want} 格，实测 {desc.stationMaxItemKinds} 格："
                    + (raised
                        ? "已被 StationCapacityPatches 改过"
                        : "**没被改过** —— 那边遍历的是固定名单（stations.json 的 itemIds、"
                          + "巨型建筑、machines.json 的 station，加按 isCollectStation 发现的采集器），"
                          + "本建筑不在其中")
                    + $"\n  → 要加钻头槽，把 stationMaxItemKinds 抬到 {desc.stationMaxItemKinds + 1} 就够；"
                    + "**别抬到 30** —— 多出来的空格会把 30 格的物流站界面拖上来（气体采集器那条老教训）");
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

            var sb = new StringBuilder("── 已建成采矿机实况（第一台跑起来的）──");

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

            // 这台机器脚下是什么矿脉 —— 「为什么没有钻头槽」十有八九答在这里
            int veinType = 0;
            VeinData[] pool = factory.veinPool;

            if (pool != null && miner.veins != null && miner.veinCount > 0)
            {
                int v = miner.veins[0];

                if (v > 0 && v < pool.Length) veinType = (int)pool[v].type;
            }

            VeinProto vein = veinType > 0 ? LDB.veins.Select(veinType) : null;
            bool alien = AlienVeinPatches.VeinType > 0 && veinType == AlienVeinPatches.VeinType;

            sb.Append($"\n  脚下矿脉：{(vein != null ? vein.Name : "?")}（类型 {veinType}）")
              .Append(alien ? " ← 这是吃钻头的那种" : "，不吃钻头");

            int slot = AlienVeinPatches.Config?.bitSlotIndex ?? -1;

            if (!alien)
                // 这不是故障。钻头槽是挂在矿脉上的，不是挂在采矿机上的
                sb.Append("\n  → 所以这台机器本来就没有钻头槽。")
                  .Append($"钻头槽只出现在「{AlienVeinPatches.Config?.veinRef}」矿脉上的采矿机身上，")
                  .Append("那种矿脉只在本功能启用之后新生成的星球上才有。");
            else if (slot < 0 || slot >= station.storage.Length)
                sb.Append($"\n  → **这台机器的 storage 只有 {station.storage.Length} 格，放不下第 {slot} 格**。")
                  .Append("它是在本功能启用之前建的（storage 在建造时固化进存档）—— 拆掉重建一台即可。");
            else if (station.storage[slot].max <= 0)
                sb.Append($"\n  → **第 {slot} 格容量是 0，还没被布置过**。")
                  .Append("正常情况下采矿机跑第一个 tick 时就会布置好；如果一直是 0，说明 Tick 没跑到。");
            else
                sb.Append($"\n  → 钻头槽（第 {slot} 格）已就位，容量 {station.storage[slot].max:N0}。");

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }
    }
}
