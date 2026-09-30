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
            for (int item = 1; item <= 8; item++)
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
                        if (slot.itemId == item && slot.localLogic == mode) { expected.Add(id); break; }
                }
                var actual = new List<int>();
                foreach (int id in MegaCandidateIndex.Select(state, world, mode, group, new Dictionary<int, long> { [item] = 1 }, start)) actual.Add(id);
                Check(string.Join(",", actual) == string.Join(",", expected), "候选顺序/分组/去重不一致");
            }
            return state;
        }
        finally { state.Release(); }
    }
    internal static void Run()
    {
        var world = Program.Create(19, 2053);
        long parallel = Counter("_parallel");
        var state = Verify(world);
        Check(Environment.ProcessorCount < 4 || Counter("_parallel") > parallel, "未执行并行分支");
        var old = state.Entries[3].Value;
        world.stationPool[3].storage[0].count++;
        world.stationPool[3].storage[0].inc++;
        world.stationPool[3].storage[0].qua++;
        world.stationPool[3].storage[0].max++;
        Verify(world);
        Check(ReferenceEquals(old, state.Entries[3].Value), "库存变化不应重建布局");
        world.stationPool[3].storage[0] = new StationStore { itemId = 8, localLogic = ELogisticStorage.Demand };
        Verify(world);
        Check(!ReferenceEquals(old, state.Entries[3].Value), "直接改槽未发现");
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
            lock (world.stationPool[1].storage)
            {
                work = Task.Run(() => { var value = MegaCandidateIndex.Prepare(world, world.factory); value.Release(); });
                Check(SpinWait.SpinUntil(() => Counter("_busy") != 0, 5000), "后台任务未启动");
                parallel = Counter("_parallel"); Verify(other);
                Check(Counter("_parallel") == parallel, "繁忙时未串行回退");
            }
            Check(work.Wait(5000), "索引线程未结束");
        }
        // 最后测试故障：会关闭本进程后续索引。
        world.factory.factorySystem = null;
        bool thrown = false;
        try { MegaCandidateIndex.Prepare(world, world.factory); } catch (NullReferenceException) { thrown = true; }
        Check(thrown && Counter("_busy") == 0, "异常未传播或忙标记未释放");
        Check(MegaCandidateIndex.Prepare(world, world.factory) == null, "故障后未回退");
        Check(!state.InUse && state.Factory == null && state.Pool == null, "故障后保留运行引用/锁");
        Console.WriteLine("PASS: 24组2053站世界×3轮搬运对照；缓存复用、直接改槽、去重/轮转、拆除重建、游标伸缩、并行/争用/关闭/故障回退。");
    }
}
