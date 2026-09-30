using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ProjectEden.Patches;
using ProjectEden.Patches.Diagnostics;

[HarmonyPatch(typeof(StationComponent), nameof(StationComponent.UpdateNeeds))]
static class ParallelNeedsObserver
{
    internal static Action<StationComponent> Observe;
    [HarmonyPostfix] static void After(StationComponent __instance) => Observe?.Invoke(__instance);
}
static class ParallelNeedsTests
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
    static PlanetTransport Create(int count, int seed)
    {
        var r = new Random(seed);
        var t = new PlanetTransport { stationPool = new StationComponent[count + 1], stationCursor = count + 1, factory = new PlanetFactory { entityNeeds = new int[count + 1][] } };
        for (int id = 1; id <= count; id++)
        {
            if (id % 17 == 0) continue;
            var s = new StationComponent { id = id % 19 == 0 ? 0 : id, entityId = id, storage = new StationStore[id % 5 == 0 ? 5 : id % 7 == 0 ? 40 : 30], needs = new int[6], isStellar = id % 3 == 0, warperMaxCount = 50, warperCount = r.Next(100) };
            for (int j = 0; j < s.storage.Length; j++)
                if (r.Next(3) == 0) s.storage[j] = new StationStore { itemId = 1 + r.Next(20), count = r.Next(101), max = r.Next(101) };
            t.stationPool[id] = s;
        }
        return t;
    }
    internal static void Run()
    {
        var harmony = new Harmony("ProjectEden.ParallelNeeds.Tests");
        harmony.CreateClassProcessor(typeof(ParallelStationNeedsPatches)).Patch();
        harmony.CreateClassProcessor(typeof(ParallelNeedsObserver)).Patch();
        var threads = new ConcurrentDictionary<int, byte>();
        TransportSplitProbe.Armed = true;
        for (int round = 0; round < 20; round++)
        {
            GameMain.gameTick = round * 13;
            var expected = Create(4097, round); var actual = Create(4097, round);
            CpuCostProbe.Config.parallelStationNeeds = false;
            expected.GameTick(false);
            var counts = new int[4098];
            ParallelNeedsObserver.Observe = station =>
            {
                Interlocked.Increment(ref counts[station.entityId]);
                threads.TryAdd(Thread.CurrentThread.ManagedThreadId, 0);
            };
            CpuCostProbe.Config.parallelStationNeeds = true;
            actual.GameTick(false);
            ParallelNeedsObserver.Observe = null;
            for (int id = 1; id < actual.stationCursor; id++)
            {
                var a = actual.stationPool[id]; var b = expected.stationPool[id];
                if (a == null || a.id != id) { Check(counts[id] == 0, "无效站被刷新"); continue; }
                Check(counts[id] == 1, "站点重复或遗漏");
                Check(a.needs.SequenceEqual(b.needs), "并行需求与串行不一致");
                Check(ReferenceEquals(actual.factory.entityNeeds[id], a.needs), "返回前实体需求未提交");
                Check(a.storage.SequenceEqual(b.storage), "库存发生变化");
            }
            Check(!LogisticsTickContext.Active, "返回后上下文未恢复");
        }
        Check(threads.Count > 1 && threads.Count <= 4, "没有实际并行或超过线程上限");
        // 小星球/非运输入口均回退，保留原方法执行。
        var scope = LogisticsTickContext.Enter(100, GameMain.data);
        Check(!ParallelStationNeedsPatches.TryRun(Create(100, 1)), "小星球未回退");
        LogisticsTickContext.Finish(scope);
        Check(!ParallelStationNeedsPatches.TryRun(Create(4097, 1)), "非运输入口被接管");
        // 用真实阻塞构造两颗星球竞争，第二颗必须立即串行，不等待辅助池。
        var first = Create(4097, 1); var second = Create(4097, 2);
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            ParallelNeedsObserver.Observe = station =>
            {
                if (ReferenceEquals(station, first.stationPool[1])) { entered.Set(); release.Wait(); }
            };
            var task = Task.Run(() => first.GameTick(false));
            Check(entered.Wait(5000), "辅助任务未启动");
            try
            {
                scope = LogisticsTickContext.Enter(100, GameMain.data);
                try { Check(!ParallelStationNeedsPatches.TryRun(second), "忙碌辅助池未回退"); }
                finally { LogisticsTickContext.Finish(scope); }
                second.GameTick(false);
                Check(second.factory.entityNeeds[1] != null, "竞争回退没有执行串行");
            }
            finally { release.Set(); task.Wait(); ParallelNeedsObserver.Observe = null; }
        }
        // 工作线程异常必须回传，全部任务结束后本局关闭并行，不重复结算。
        ParallelNeedsObserver.Observe = station => { if (station.id == 1) throw new InvalidOperationException("parallel-test"); };
        try { Create(4097, 3).GameTick(false); throw new Exception("异常被吞掉"); }
        catch (InvalidOperationException e) { Check(e.Message == "parallel-test", "原异常丢失"); }
        finally { ParallelNeedsObserver.Observe = null; }
        scope = LogisticsTickContext.Enter(100, GameMain.data);
        try { Check(!ParallelStationNeedsPatches.TryRun(Create(4097, 4)), "故障后未回退串行"); }
        finally { LogisticsTickContext.Finish(scope); }
        CpuCostProbe.Config.parallelStationNeeds = false;
        Console.WriteLine($"PASS: 需求并行20轮×4097站实际Harmony逐站对照，线程数={threads.Count}，一次且仅一次、返回前提交、跨星球竞争、小站/非运输入口回退、异常等待及故障降级。");
    }
}
