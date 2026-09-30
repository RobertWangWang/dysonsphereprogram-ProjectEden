using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ProjectEden.Patches;
using ProjectEden.Patches.Diagnostics;

static class CandidateIndexTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static long Counter(string name) => Convert.ToInt64(typeof(MegaCandidateIndex).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
    static MegaCandidateIndex.State Verify(PlanetTransport world)
    {
        var state = MegaCandidateIndex.Prepare(world, world.factory);
        try
        {
            for (int item = 0; item <= 8; item++)
            foreach (var mode in new[] { ELogisticStorage.Supply, ELogisticStorage.Demand })
            for (int group = 0; group <= 2; group++)
            foreach (int start in new[] { 0, 9, world.stationCursor - 2 })
            {
                var expected = new List<int>();
                for (int offset = 0; offset < world.stationCursor - 1; offset++)
                {
                    int id = 1 + (start + offset) % (world.stationCursor - 1);
                    var station = world.stationPool[id];
                    if (station == null || station.id != id || station.storage == null) continue;
                    int assembler = world.factory.entityPool[station.entityId].assemblerId;
                    var machine = world.factory.factorySystem.assemblerPool[assembler];
                    bool mega = assembler > 0 && machine.id == assembler && machine.speed >= ProjectEden.MegaBuildingRegistry.MegaSpeedThreshold;
                    if (group == 1 && !mega || group == 2 && mega) continue;
                    foreach (var slot in station.storage)
                        if ((item == 0 ? slot.itemId >= 1 && slot.itemId <= 8 : slot.itemId == item) && slot.localLogic == mode) { expected.Add(id); break; }
                }
                var actual = new List<int>();
                var wanted = new Dictionary<int, long>();
                if (item == 0) for (int k = 8; k >= 1; k--) wanted[k] = 1;
                else wanted[item] = 1;
                wanted[999] = 0;
                foreach (int id in MegaCandidateIndex.Select(state, world, mode, group, wanted, start)) actual.Add(id);
                Check(string.Join(",", actual) == string.Join(",", expected), "候选顺序/分组/去重不一致");
            }
            return state;
        }
        finally { state.Release(); }
    }
    static void SelectiveInvalidation()
    {
        var world = Program.Create(1, 2);
        for (int id = 1; id <= 2; id++)
            world.stationPool[id] = new StationComponent { id = id, entityId = id,
                storage = new[] { new StationStore { itemId = id, localLogic = ELogisticStorage.Supply } } };
        string Query(int[] items, int group, bool hit)
        {
            var state = MegaCandidateIndex.Prepare(world, world.factory);
            try
            {
                var keys = new Dictionary<int, long>(); foreach (int item in items) keys[item] = 1;
                long before = Counter("_queryHits"); var result = new List<int>();
                foreach (int id in MegaCandidateIndex.Select(state, world, ELogisticStorage.Supply, group, keys, 0)) result.Add(id);
                Check(Counter("_queryHits") - before == (hit ? 1 : 0), "局部缓存命中不符");
                Check(state.MapsMatch(), "局部更新映射不一致");
                return string.Join(",", result);
            }
            finally { state.Release(); }
        }
        Check(Query(new[] { 1 }, 0, false) == "1", "初始供货");
        Check(Query(new[] { 2 }, 0, false) == "2", "初始供货2");
        Check(Query(new[] { 1, 2 }, 0, false) == "1,2", "并集初始");
        Check(Query(new[] { 1 }, 1, false) == "", "初始巨型分类");
        // 站1物品1换成物品2：只失效单物品1/2，并集仍有同样的两个成员。
        world.stationPool[1].storage[0].itemId = 2;
        StationConfiguredSlots.Invalidate(world.stationPool[1].storage);
        Check(Query(new[] { 1, 2 }, 0, true) == "1,2", "并集成员不变应复用");
        Check(Query(new[] { 1 }, 0, false) == "", "移除相关供应未失效");
        Check(Query(new[] { 2 }, 0, false) == "1,2", "增加相关供应未失效");
        Check(Query(new[] { 1 }, 1, true) == "", "不相关分组不应失效");
        long sorts = Counter("_sorts");
        world.stationPool[1].storage = new[] { new StationStore(), new StationStore { itemId = 2, localLogic = ELogisticStorage.Supply } };
        Check(Query(new[] { 2 }, 0, true) == "1,2", "移槽不应失效成员查询");
        Check(Counter("_sorts") == sorts, "移槽不应重新排序");
        Check(Query(new[] { 2 }, 1, false) == "", "巨型分组初始为空");
        world.factory.factorySystem.assemblerPool[1].speed = ProjectEden.MegaBuildingRegistry.MegaSpeedThreshold;
        Check(Query(new[] { 2 }, 1, false) == "1", "巨型分类变化未失效");
        Check(Query(new[] { 2 }, 0, true) == "1,2", "分类变化不应清空任意站查询");
        Console.WriteLine("PASS: 按成员局部失效，含空结果、多个物品并集、换槽及巨型分类变化；无关查询/排序复用。");
    }
    static void RotationWalks()
    {
        foreach (int count in new[] { 0, 1, 2, 31, 32, 33, 63, 64, 65, 2053 })
        {
            var ids = new List<int>(); for (int i = 0; i < count; i++) ids.Add(i * 7 + 3);
            for (int start = 0; start < Math.Max(1, count * 2); start++)
            foreach (bool indexed in new[] { false, true })
            {
                var walk = new MegaCandidateIndex.StationWalk(indexed ? ids : null, count, start);
                int n = 0;
                foreach (int id in walk)
                {
                    int at = (start + n) % count;
                    Check(id == (indexed ? ids[at] : at + 1), "递增轮转顺序改变"); n++;
                }
                Check(n == count, "轮转漏站/多站");
                var e = walk.GetEnumerator(); while (e.MoveNext()) { }
                Check(!e.MoveNext() && !e.MoveNext(), "结束后重新开始枚举");
            }
        }
        Console.WriteLine("PASS: 候选/全扫枚举器逐项对照，含空集合、字边界、2053站、全部起点及跨界起点。");
    }
    internal static void Run()
    {
        RotationWalks();
        SelectiveInvalidation();
        var world = Program.Create(19, 2053);
        long parallel = Counter("_parallel");
        var state = Verify(world);
        Check(Counter("_bitmapQueries") > 0 && Counter("_heapQueries") > 0, "未覆盖位图/堆两种查询路径");
        Check(Environment.ProcessorCount < 4 || Counter("_parallel") > parallel, "未执行并行分支");
        // 无物品过滤的两个巨型集合也必须保持升序轮转。
        foreach (var mode in new[] { ELogisticStorage.Supply, ELogisticStorage.Demand })
        {
            var held = MegaCandidateIndex.Prepare(world, world.factory);
            try
            {
                var expected = new List<int>();
                for (int id = 1; id < held.Entries.Length; id++)
                {
                    var snapshot = held.Entries[id].Value;
                    if (snapshot != null && snapshot.Mega && (mode == ELogisticStorage.Supply ? snapshot.Supply : snapshot.Demand).Length > 0) expected.Add(id);
                }
                var actual = new List<int>();
                foreach (int id in MegaCandidateIndex.Select(held, world, mode, 1, null, 0)) actual.Add(id);
                Check(string.Join(",", actual) == string.Join(",", expected), "巨型集合有序视图不一致");
            }
            finally { held.Release(); }
        }
        Check(Counter("_queryHits") > 0, "重复查询未复用缓存");
        if (Environment.ProcessorCount >= 4)
        {
            long batches = Counter("_scanBatches");
            Verify(world);
            Check(Counter("_scanBatches") - batches == (state.Entries.Length - 1 + 511) / 512,
                "动态批次漏扫/重复扫描");
        }
        var old = state.Entries[3].Value;
        world.stationPool[3].storage[0].count++;
        world.stationPool[3].storage[0].inc++;
        world.stationPool[3].storage[0].qua++;
        world.stationPool[3].storage[0].max++;
        long validated = Counter("_validated"), sorts = Counter("_sorts");
        Verify(world);
        Check(Counter("_validated") == validated && Counter("_sorts") == sorts, "稳定布局仍复核/排序");
        Check(ReferenceEquals(old, state.Entries[3].Value), "库存变化不应重建布局");
        StationConfiguredSlots.Invalidate(world.stationPool[3].storage);
        Verify(world);
        Check(ReferenceEquals(old, state.Entries[3].Value) && Counter("_sorts") == sorts, "无布局变化的通知不应重建快照/排序");
        Check(state.Changes.IsEmpty, "提交后残留变化队列");
        world.stationPool[3].storage[0] = new StationStore { itemId = 8, localLogic = ELogisticStorage.Demand };
        // 未知直接写入允许至多60tick延迟；复查时与同一边界全量结果对账。
        GameMain.gameTick += 60;
        Verify(world);
        Check(!ReferenceEquals(old, state.Entries[3].Value), "错峰复核未发现直接改槽");
        world.stationPool[3].storage[0].localLogic = ELogisticStorage.Supply;
        StationConfiguredSlots.Invalidate(world.stationPool[3].storage);
        Verify(world);
        world.stationPool[7].id = 7;
        Verify(world);
        world.stationPool[7].storage[39] = new StationStore { itemId = 8, localLogic = ELogisticStorage.Demand };
        StationConfiguredSlots.Invalidate(world.stationPool[7].storage);
        Verify(world);
        GameMain.gameTick -= 100;
        Verify(world);
        world.stationPool[3].storage = new[] { new StationStore { itemId = 8, localLogic = ELogisticStorage.Supply }, new StationStore { itemId = 8, localLogic = ELogisticStorage.Supply } };
        Verify(world);
        world.factory.factorySystem.assemblerPool[3].speed = 0;
        Verify(world);
        world.stationPool[3] = null;
        Verify(world);
        Check(state.Entries[3].Value == null, "拆除后保留旧站");
        world.stationPool[3] = new StationComponent { id = 3, entityId = 3, storage = new[] { new StationStore { itemId = 8, localLogic = ELogisticStorage.Demand } } };
        world.stationPool = (StationComponent[])world.stationPool.Clone();
        Verify(world);
        world.stationCursor = 16;
        Verify(world);
        Check(state.Entries[2000].Value == null, "游标收缩后未清除");
        world.stationCursor = world.stationPool.Length;
        Verify(world);
        CpuCostProbe.Config.parallelMegaIndex = false;
        parallel = Counter("_parallel"); Verify(world);
        Check(Counter("_parallel") == parallel, "串行开关无效");
        CpuCostProbe.Config.parallelMegaIndex = true;
        CpuCostProbe.Config.indexedMegaLogistics = false;
        Check(MegaCandidateIndex.Prepare(world, world.factory) == null, "关闭索引未回退");
        CpuCostProbe.Config.indexedMegaLogistics = true;
        if (Environment.ProcessorCount >= 4)
        {
            var other = Program.Create(20, 2053);
            Task work;
            StationConfiguredSlots.Invalidate(world.stationPool[1].storage);
            lock (world.stationPool[1].storage)
            {
                work = Task.Run(() => { var value = MegaCandidateIndex.Prepare(world, world.factory); value.Release(); });
                Check(SpinWait.SpinUntil(() => Counter("_busy") != 0, 5000), "后台任务未启动");
                parallel = Counter("_parallel"); Verify(other);
                Check(Counter("_parallel") == parallel, "繁忙时未串行回退");
            }
            Check(work.Wait(5000), "索引线程未结束");
        }
        // 同边界审计必须发现遗漏和伪造映射，并在查询前修复。
        state = Verify(world);
        state.Supply[99999] = new MegaCandidateIndex.Candidates();
        state.Supply[99999].Add(3);
        Check(!state.MapsMatch(), "未发现多余候选");
        long repairs = Counter("_auditRepairs");
        GameMain.gameTick += 600;
        Verify(world);
        Check(state.MapsMatch() && Counter("_auditRepairs") == repairs + 1, "运行时审计未修复");
        long rebuilds = Counter("_rebuilds");
        for (int id = 1; id < world.stationCursor; id++)
        {
            var station = world.stationPool[id];
            if (station == null) continue;
            station.id = id;
            station.storage = new[] { new StationStore { itemId = 8, localLogic = ELogisticStorage.Demand } };
        }
        Verify(world);
        Check(Counter("_rebuilds") > rebuilds && state.MapsMatch(), "大量变化未走全量重建");
        // 结构数组扩容必须保留既有快照，新条目必须为零初始化。
        var retained = state.Entries[3].Value;
        int addedId = world.stationPool.Length;
        Array.Resize(ref world.stationPool, addedId + 65);
        world.stationCursor = world.stationPool.Length;
        world.stationPool[addedId] = new StationComponent { id = addedId, entityId = 0,
            storage = new[] { new StationStore { itemId = 8, localLogic = ELogisticStorage.Demand } } };
        Verify(world);
        Check(ReferenceEquals(retained, state.Entries[3].Value), "扩容丢失原快照");
        Check(state.Entries[addedId].Value != null && state.Entries[addedId + 1].Value == null, "扩容新条目状态错误");
        // 最后测试故障：会关闭本进程后续索引。
        world.factory.factorySystem = null;
        bool thrown = false;
        try { MegaCandidateIndex.Prepare(world, world.factory); } catch (NullReferenceException) { thrown = true; }
        Check(thrown && Counter("_busy") == 0, "异常未传播或忙标记未释放");
        Check(MegaCandidateIndex.Prepare(world, world.factory) == null, "故障后未回退");
        Check(!state.InUse && state.Factory == null && state.Pool == null, "故障后保留运行引用/锁");
        Console.WriteLine("PASS: 24组2053站世界×3轮搬运对照；稳定轮零格位复核/零排序、通知及60tick直接改槽、40格失效、多物品归并/去重/轮转、拆除重建、游标伸缩、并行/争用/关闭/故障回退。");
    }
}
