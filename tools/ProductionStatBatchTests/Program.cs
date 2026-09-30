using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using HarmonyLib;
using ProjectEden.Patches.Diagnostics;

class Program
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
    static void Main()
    {
        var b = new ProductionStatBuffer();
        int[] a = { 0, int.MaxValue, 2 }, other = new int[3];
        Check(ReferenceEquals(b.Redirect(a), a), "未激活不重定向");
        Check(b.Begin() && !b.Begin(), "嵌套不重复提交");
        int[] d = b.Redirect(a);
        Check(ReferenceEquals(d, b.Redirect(a)), "同一目标复用");
        d[1] = 2; d[2] = -2;
        b.Redirect(other)[0] = 9;
        Check(a[1] == int.MaxValue && other[0] == 0, "提交前隔离");
        b.Flush(out int arrays, out int items);
        Check(a[1] == int.MinValue + 1 && a[2] == 0 && other[0] == 9 && arrays == 2 && items == 3, "溢出与多星球合并");
        for (int round = 0; round < 100; round++)
        {
            b.Begin();
            Check(b.Redirect(new int[round + 1]).All(x => x == 0), "复用无残留且长度可变");
            b.Flush(out _, out _);
        }
        b.Begin();
        var many = Enumerable.Range(0, 64).Select(_ => new int[8]).ToArray();
        foreach (int[] target in many) b.Redirect(target)[3] = 7;
        b.Flush(out arrays, out items);
        Check(arrays == 64 && many.All(x => x[3] == 7), "超缓存上限正常提交");

        // 与未改造的原版 lock 写入并存；多个工作线程共享同一星球统计。
        int[] shared = new int[128];
        Parallel.For(0, 16, worker =>
        {
            var local = new ProductionStatBuffer();
            for (int round = 0; round < 500; round++)
            {
                local.Begin();
                int[] delta = local.Redirect(shared);
                for (int i = 0; i < 100; i++) delta[i]++;
                lock (shared) for (int i = 0; i < 100; i++) shared[i]++;
                local.Flush(out _, out _);
            }
        });
        Check(shared.Take(100).All(x => x == 16000) && shared.Skip(100).All(x => x == 0), "并发及原路径混合不丢统计");

        // 测试真实补丁入口、异常后的 Finalizer、关闭开关和转译器形状保护。
        var patch = typeof(ProductionStatBatchPatches);
        var before = patch.GetMethod("Before", BindingFlags.NonPublic | BindingFlags.Static);
        var after = patch.GetMethod("After", BindingFlags.NonPublic | BindingFlags.Static);
        object[] state = { false };
        before.Invoke(null, state);
        var failedTarget = new int[2];
        try { ProductionStatBatchPatches.Redirect(failedTarget)[1] = 42; throw new InvalidOperationException(); }
        catch (InvalidOperationException) { }
        finally { after.Invoke(null, state); }
        Check(failedTarget[1] == 42, "异常路径提交");
        CpuCostProbe.Config.batchProductionStatistics = false;
        state[0] = false;
        before.Invoke(null, state);
        Check(!(bool)state[0] && ReferenceEquals(ProductionStatBatchPatches.Redirect(failedTarget), failedTarget), "关闭开关回退");

        var source = new[] {
            new CodeInstruction(OpCodes.Ldfld, typeof(FactoryProductionStat).GetField("productRegister")),
            new CodeInstruction(OpCodes.Ldfld, typeof(FactoryProductionStat).GetField("consumeRegister")) };
        var transpile = patch.GetMethod("Transpile", BindingFlags.NonPublic | BindingFlags.Static);
        var output = ((System.Collections.Generic.IEnumerable<CodeInstruction>)transpile.Invoke(null, new object[] { source, typeof(Program).GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic) })).ToArray();
        Check(output.Length == 4 && output[1].opcode == OpCodes.Call && output[3].opcode == OpCodes.Call, "两处统计字段重定向");
        var fallback = ((System.Collections.Generic.IEnumerable<CodeInstruction>)transpile.Invoke(null, new object[] { source.Take(1), typeof(Program).GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic) })).ToArray();
        Check(fallback.Length == 1 && fallback[0].opcode == OpCodes.Ldfld, "形状不匹配整条回退");
        CpuCostProbe.Config.batchProductionStatistics = true;
        new Harmony("ProjectEden.StatBatch.OfflineTest").CreateClassProcessor(patch).Patch();
        var stat = new FactoryProductionStat();
        var game = new GameLogic { Stat = stat };
        game.During = () => Check(stat.productRegister[0] == 0 && stat.consumeRegister[0] == 0, "实际补丁调用内部只写私有数组");
        game.Invoke(false, false);
        Check(stat.productRegister[0] == 3 && stat.consumeRegister[0] == 7, "实际组装机钩子提交");
        game.During = null;
        try { game.Invoke(true, true); throw new Exception("应抛出原异常"); }
        catch (InvalidOperationException) { }
        Check(stat.productRegister[0] == 6 && stat.consumeRegister[0] == 14, "实际研究站异常钩子提交且保留异常");
        Parallel.For(0, 16, worker => { for (int i = 0; i < 100; i++) game.Invoke((i & 1) == 0, false); });
        Check(stat.productRegister[0] == 4806 && stat.consumeRegister[0] == 11214, "实际并发钩子统计守恒");
        new Harmony("ProjectEden.TransportBody.OfflineTest").CreateClassProcessor(typeof(TransportBodyProbe)).Patch();
        var transport = new PlanetTransport();
        transport.GameTick(false);
        Check(transport.Value == 7, "物流分段注入不改变执行");
        var counts = (long[])typeof(TransportBodyProbe).GetField("Counts", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        Check(counts.Take(4).All(x => x == 1), "四段各记录一次");
        try { transport.GameTick(true); } catch (InvalidOperationException) { }
        transport.GameTick(false);
        Check(counts[0] == 3 && counts[1] == 3 && counts[2] == 2 && counts[3] == 2, "物流异常不污染后续调用");
        TransportSplitProbe.Armed = false;
        transport.GameTick(false);
        Check(counts[0] == 3, "探针关闭不计时");
        NeedsProbeTests.Run();
        StationLoopProbeTests.Run();
        Console.WriteLine("PASS：复用、嵌套、多星球、溢出、64目标、并发混合写入、异常提交、开关、转译器及回退。");
    }
}
class GameLogic
{
    internal FactoryProductionStat Stat;
    internal Action During;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void _assembler_parallel(int a, int b, int c, int fail) { Run(Stat.productRegister, Stat.consumeRegister, fail); }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void _lab_produce_parallel(int a, int b, int c, int fail) { Run(Stat.productRegister, Stat.consumeRegister, fail); }
    private void Run(int[] product, int[] consume, int fail)
    {
        lock (product) product[0] += 3;
        lock (consume) consume[0] += 7;
        During?.Invoke();
        if (fail != 0) throw new InvalidOperationException();
    }
    internal void Invoke(bool lab, bool fail) { if (lab) _lab_produce_parallel(0, 0, 0, fail ? 1 : 0); else _assembler_parallel(0, 0, 0, fail ? 1 : 0); }
}
class FactoryProductionStat { public int[] productRegister = new int[1], consumeRegister = new int[1]; }
class PlanetTransport
{
    internal int Value;
    public StationComponent[] stationPool;
    public int stationCursor; public PlanetFactory factory;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void SetStationStorage(int stationId, int storageIdx, int itemId) { stationPool[stationId].storage[storageIdx].itemId = itemId; }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void GameTick(bool fail)
    {
        GameTick_SandboxMode();
        if (fail) throw new InvalidOperationException();
        foreach (var station in LoopStations)
        {
            station.InternalTickLocal(3);
            if (station.isCollector) station.UpdateCollection();
            if (station.isVeinCollector) station.UpdateVeinCollection();
            if (station.isStellar) station.InternalTickRemote(7);
            station.SetPCState();
        }
        GameTick_UpdateNeeds();
        Value += 4;
    }
    public void GameTick_SandboxMode() { Value += 1; }
    internal StationComponent[] LoopStations = new StationComponent[0];
    internal StationComponent[] Stations = new StationComponent[0];
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void GameTick_UpdateNeeds()
    {
        var pool = stationPool ?? Stations;
        int end = stationPool == null ? pool.Length : stationCursor;
        for (int i = stationPool == null ? 0 : 1; i < end; i++)
        {
            var station = pool[i];
            if (station == null || (stationPool != null && station.id != i)) continue;
            station.UpdateNeeds();
            if (factory != null) factory.entityNeeds[station.entityId] = station.needs;
        }
        Value += 2;
    }
}
class PlanetFactory { public int[][] entityNeeds; }
class ProjectEdenPlugin { internal static LogStub Log = new LogStub(); }
class LogStub { public void LogWarning(string text) { } public void LogInfo(string text) { } }
namespace ProjectEden.Patches.Diagnostics
{
    class TransportSplitProbe { internal static bool Armed = true; }
    class CpuCostProbe { internal static ConfigData Config = new ConfigData(); }
    class ConfigData { public bool parallelStationNeeds = false; public bool batchProductionStatistics = true; public bool systemTiming = true; }
    class NanosecondProbe { internal static long Ns(long ticks) => ticks; }
}
