using System;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ProjectEden.Patches;
using ProjectEden.Patches.Diagnostics;

static class NeedsProbeTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static StationComponent Station(int slots)
    {
        var s = new StationComponent { id = 1, storage = new StationStore[slots], needs = new int[6] };
        for (int i = 0; i < slots; i++) s.storage[i] = new StationStore { itemId = 100 + i, max = 100 };
        return s;
    }
    internal static void Run()
    {
        var harmony = new Harmony("ProjectEden.NeedsProbe.OfflineTest");
        harmony.CreateClassProcessor(typeof(StationBeltInputPatches)).Patch();
        harmony.CreateClassProcessor(typeof(NeedsRefreshProbe)).Patch();
        harmony.CreateClassProcessor(typeof(StationConfiguredSlotsPatches)).Patch();
        var configured = Station(30);
        Array.Clear(configured.storage, 0, configured.storage.Length);
        StationConfiguredSlots.Get(configured.storage, 1, out _);
        var setup = new PlanetTransport { stationPool = new[] { null, configured } };
        setup.SetStationStorage(1, 29, 123);
        var layout = StationConfiguredSlots.Get(configured.storage, 1, out bool rebuilt);
        Check(rebuilt && layout.Mask == (1u << 29), "真实设置入口未使缓存失效");
        setup.SetStationStorage(1, 29, 0);
        Check(StationConfiguredSlots.Get(configured.storage, 1, out rebuilt).Mask == 0 && rebuilt, "真实清空入口未使缓存失效");
        harmony.CreateClassProcessor(typeof(LogisticsTickContext)).Patch();
        var scoped = new PlanetTransport { Stations = Enumerable.Range(0, 1000).Select(i => Station(30)).ToArray() };
        GameMain.gameTick = 100; GameMain.TickReads = 0;
        scoped.GameTick(false);
        Check(GameMain.TickReads == 1, "物流轮次反复读取Unity时钟属性");
        GameMain.gameTick = 200;
        LogisticsTickContext.Begin(out var outer);
        GameMain.gameTick = 300;
        LogisticsTickContext.Begin(out var inner);
        Check(LogisticsTickContext.Tick == 300, "嵌套上下文未进入");
        LogisticsTickContext.Finish(inner);
        Check(LogisticsTickContext.Tick == 200, "嵌套上下文未恢复");
        LogisticsTickContext.Finish(outer);
        Check(LogisticsTickContext.Tick == 300, "轮次结束未恢复实时入口");
        try { scoped.GameTick(true); } catch (InvalidOperationException) { }
        GameMain.gameTick = 400;
        Check(LogisticsTickContext.Tick == 400, "异常遗留物流上下文");
        TransportSplitProbe.Armed = true;
        NeedsRefreshProbe.Take();
        Parallel.For(0, 8, worker =>
        {
            var p = new PlanetTransport { Stations = new[] { Station(5), Station(30), Station(40) } };
            for (int i = 0; i < 10000; i++) p.GameTick_UpdateNeeds();
            Check(p.Stations.All(s => s.needs[0] >= 100), "采样改变需求结果");
            Check(!NeedsRefreshProbe.Active, "线程采样未清理");
        });
        var t = NeedsRefreshProbe.Take();
        Check(t[0] > 500 && t[0] < 2500, "抽样频率异常");
        Check(t[1] == t[0] * 3 && t[2] == t[0] * 2 && t[3] == t[0] * 70, "并发样本计数不守恒");
        Check(t[4] >= t[5] && t[5] >= t[6] + t[7] + t[8] && t[9] == 0, "分段不守恒");
        Check(t[6] > 0 && t[7] > 0 && t[8] > 0, "阶段未接入");
        // 强制等待真实库存锁，确认等待归入锁获取，而不是扫描或遍历。
        bool state;
        do { NeedsRefreshProbe.Begin(out state); if (!NeedsRefreshProbe.Active) NeedsRefreshProbe.Finish(null, state); }
        while (!NeedsRefreshProbe.Active);
        var blocked = Station(30);
        using (var held = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var holder = Task.Run(() => { lock (blocked.storage) { held.Set(); release.Wait(); Thread.Sleep(100); } });
            held.Wait(); release.Set();
            NeedsRefreshProbe.UpdateStation(blocked);
            holder.Wait();
        }
        NeedsRefreshProbe.Finish(null, state);
        t = NeedsRefreshProbe.Take();
        Check(t[6] * 1000.0 / Stopwatch.Frequency >= 50, "锁等待未正确归因");
        var fail = Station(5); fail.During = () => { throw new InvalidOperationException(); };
        var transport = new PlanetTransport { Stations = new[] { fail } };
        for (int i = 0; i < 10000; i++)
        {
            try { transport.GameTick_UpdateNeeds(); throw new Exception("异常被吞掉"); }
            catch (InvalidOperationException) { }
            Check(!NeedsRefreshProbe.Active, "异常污染后续样本");
        }
        t = NeedsRefreshProbe.Take();
        Check(t[0] > 0 && t[9] == t[0] && t[1] == t[0], "异常轮次漏报");
        TransportSplitProbe.Armed = false;
        transport.Stations = new[] { Station(30) };
        for (int i = 0; i < 1000; i++) transport.GameTick_UpdateNeeds();
        Check(NeedsRefreshProbe.Take().All(x => x == 0), "关闭后仍计时");
        ParallelNeedsTests.Run();
        // 转译结构不匹配时保留原指令，不启用半套探针。
        var input = new[] { new CodeInstruction(System.Reflection.Emit.OpCodes.Ret) };
        Check(NeedsRefreshProbe.Transpile(input).Single() == input[0], "形状保护改变原方法");
        TransportSplitProbe.Armed = true;
        NeedsRefreshProbe.Begin(out state);
        Check(!state && !NeedsRefreshProbe.Active, "形状失败仍启用采样");
        Console.WriteLine("PASS：需求细分真实Harmony接入、8万轮并发计数、两种扩容路径、真实锁等待、异常清理、开关及形状回退。");
    }
}
