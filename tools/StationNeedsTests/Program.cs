using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ProjectEden.Patches;
using ProjectEden.Patches.Diagnostics;

class Program
{
    static Action<StationComponent> Bind(Type type) => (Action<StationComponent>)type.GetMethod(type == typeof(StationBeltInputPatches) ? "BuildExpandedNeeds" : "UpdateNeeds_Postfix", BindingFlags.NonPublic | BindingFlags.Static).CreateDelegate(typeof(Action<StationComponent>));
    static readonly Action<StationComponent> Current = Bind(typeof(StationBeltInputPatches)), Original = Bind(typeof(StationNeedsReference));
    static void Compare(StationComponent s)
    {
        StationConfiguredSlots.Invalidate(s.storage);
        var expected = new StationComponent { id = s.id, storage = s.storage, needs = s.needs == null ? null : (int[])s.needs.Clone() };
        Original(expected); Current(s);
        if (s.needs != null && !s.needs.SequenceEqual(expected.needs)) throw new Exception("白名单与旧实现不一致");
    }
    static void Main()
    {
        Parallel.For(0, 32, worker =>
        {
            for (int round = 0; round < 100; round++)
            {
                try
                {
                    for (int i = 0; i < 1000; i++) StationDiagnosticCounters.Add(i % 4);
                    if (round % 10 == 0) throw new InvalidOperationException();
                }
                catch (InvalidOperationException) { }
                finally { StationDiagnosticCounters.Flush(); }
                StationDiagnosticCounters.Flush(); // 空汇总不得重复计数。
            }
        });
        for (int i = 0; i < 4; i++)
            if (StationDiagnosticCounters.Read(i) != 800000 || StationDiagnosticCounters.Take(i) != 800000 || StationDiagnosticCounters.Read(i) != 0)
                throw new Exception("多线程诊断计数丢失或重复");
        Console.WriteLine("PASS: 320万次诊断计数并发汇总守恒，包含异常及重复空汇总。");
        var random = new Random(6401);
        // 穷举16格需求组合与全部轮换相位，另核验最高位（第32格）。
        var exhaustive = new StationComponent { id = 16, storage = new StationStore[16], needs = new int[6] };
        for (int mask = 0; mask < 65536; mask++)
        {
            for (int i = 0; i < 16; i++) exhaustive.storage[i] = new StationStore { itemId = i + 100, count = (mask & (1 << i)) == 0 ? 1 : 0, max = 1 };
            for (int phase = 0; phase < 16; phase++)
            {
                GameMain.gameTick = phase * 60;
                exhaustive.needs[5] = (phase & 1) == 0 ? 1210 : 0;
                Compare(exhaustive);
            }
        }
        for (int bit = 0; bit < 32; bit++)
        {
            var one = new StationComponent { id = 1, storage = new StationStore[32], needs = new int[6] };
            one.storage[bit] = new StationStore { itemId = 200 + bit, max = 1 };
            Compare(one);
            if (one.needs[0] != 200 + bit) throw new Exception("位下标映射错误");
        }
        Console.WriteLine("PASS: 65536种需求掩码×16相位，以及全部32个单独格位。");
        var prefix = (Func<StationComponent, bool>)typeof(StationBeltInputPatches).GetMethod("UpdateNeeds_Prefix", BindingFlags.NonPublic | BindingFlags.Static).CreateDelegate(typeof(Func<StationComponent, bool>));
        for (int round = 0; round < 100000; round++)
        {
            GameMain.gameTick = random.NextInt64(0, long.MaxValue / 2);
            var stores = new StationStore[random.Next(0, 100)];
            for (int i = 0; i < stores.Length; i++) stores[i] = new StationStore { itemId = random.Next(0, 20), count = random.Next(100), max = random.Next(100) };
            var station = new StationComponent { id = random.Next(1, 10000), storage = stores, needs = new int[round % 5 == 0 ? 8 : 6], isStellar = (round & 1) == 0, warperCount = random.Next(100), warperMaxCount = random.Next(100) };
            for (int i = 0; i < station.needs.Length; i++) station.needs[i] = random.Next(2) == 0 ? 0 : 1210;
            var expected = new StationComponent { id = station.id, storage = stores, needs = (int[])station.needs.Clone(), isStellar = station.isStellar, warperCount = station.warperCount, warperMaxCount = station.warperMaxCount };
            expected.UpdateNeeds(); Original(expected);
            bool vanilla = prefix(station);
            if (vanilla) station.UpdateNeeds();
            if (!station.needs.SequenceEqual(expected.needs)) throw new Exception("完整原版＋扩展对照不一致");
            if (vanilla != (stores.Length <= station.needs.Length)) throw new Exception("回退范围错误");
        }
        Console.WriteLine("PASS: 100000组原版＋扩展全流程对照，含曲速器、普通站及第三方白名单形状回退。");
        for (int round = 0; round < 100000; round++)
        {
            GameMain.gameTick = round % 7 == 0 ? -random.Next(10000) : random.NextInt64(0, long.MaxValue / 2);
            var stores = new StationStore[random.Next(0, 100)];
            for (int i = 0; i < stores.Length; i++) stores[i] = new StationStore { itemId = random.Next(0, 15), count = random.Next(0, 101), max = random.Next(0, 101) };
            var needs = new int[random.Next(0, 12)];
            for (int i = 0; i < needs.Length; i++) needs[i] = random.Next(2) == 0 ? 0 : 1210;
            Compare(new StationComponent { id = random.Next(1, 10000), storage = stores, needs = needs });
        }
        Compare(new StationComponent());
        GameMain.gameTick = 59;
        Parallel.For(0, 16, worker =>
        {
            for (int round = 0; round < 2000; round++)
            {
                var s = new StationComponent { id = worker + round, needs = new int[6], storage = new StationStore[30 + worker] };
                for (int i = 0; i < s.storage.Length; i++) s.storage[i] = new StationStore { itemId = 10 + i, max = 100, count = (round + i) % 101 };
                s.needs[5] = (round & 1) == 0 ? 1210 : 0;
                Compare(s);
            }
        });
        var warm = new StationComponent { id = 1, storage = new StationStore[30], needs = new int[6] };
        for (int i = 0; i < 30; i++) warm.storage[i] = new StationStore { itemId = 1 + i, max = 10 };
        for (int i = 0; i < 100; i++) { warm.needs[5] = 0; Current(warm); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) { warm.needs[5] = 0; Current(warm); }
        if (GC.GetAllocatedBytesForCurrentThread() != before) throw new Exception("热路径产生托管分配");
        // 满仓和换货必须下一调用立即生效，不能读到上一次候选缓冲的残留。
        foreach (ref var slot in warm.storage.AsSpan()) slot.count = slot.max;
        warm.needs[5] = 1210; Compare(warm);
        if (warm.needs.Take(5).Any(x => x != 0) || warm.needs[5] != 1210) throw new Exception("满仓或曲速器保留失败");
        warm.storage[29] = new StationStore { itemId = 777, max = 100 };
        Compare(warm);
        if (warm.needs[0] != 777) throw new Exception("换货未生效");
        Console.WriteLine("PASS: 100000组随机白名单、32000组并发对照、空/满仓/曲速器/轮换/换货及10000次热路径零分配。");
        ConfiguredSlotsTests.Run();
        NeedsOutputCacheTests.Run();
        // 与上一版完整前置比较，两边均包含库存锁，不把已取消的原版路径算入基线。
        var previous = (Func<StationComponent, bool>)typeof(SingleScanReference).GetMethod("UpdateNeeds_Prefix", BindingFlags.NonPublic | BindingFlags.Static).CreateDelegate(typeof(Func<StationComponent, bool>));
        var bench = new StationComponent { id = 1, storage = new StationStore[30], needs = new int[6], isStellar = true, warperMaxCount = 50 };
        for (int i = 0; i < 30; i++) bench.storage[i] = new StationStore { itemId = i + 1, max = 100 };
        for (int i = 0; i < 20000; i++) { previous(bench); prefix(bench); }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 500000; i++) previous(bench);
        double oldMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        for (int i = 0; i < 500000; i++) { if (prefix(bench)) bench.UpdateNeeds(); }
        Console.WriteLine($"离线微基准500000次：上一版 {oldMs:0.0} ms，位掩码版 {timer.Elapsed.TotalMilliseconds:0.0} ms（.NET 8替身，不代表Unity实测）。");
    }
}
public struct StationStore { public int itemId, count, max; }
public class StationComponent
{
    public int id, warperCount, warperMaxCount; public bool isStellar; public int[] needs; public StationStore[] storage;
    // 来自反编译的原版 UpdateNeeds：锁内填写前五格以及曲速器位。
    public void UpdateNeeds()
    {
        lock (storage)
        {
            int n = storage.Length;
            needs[0] = 0 < n && storage[0].count < storage[0].max ? storage[0].itemId : 0;
            needs[1] = 1 < n && storage[1].count < storage[1].max ? storage[1].itemId : 0;
            needs[2] = 2 < n && storage[2].count < storage[2].max ? storage[2].itemId : 0;
            needs[3] = 3 < n && storage[3].count < storage[3].max ? storage[3].itemId : 0;
            needs[4] = 4 < n && storage[4].count < storage[4].max ? storage[4].itemId : 0;
            needs[5] = isStellar && warperCount < warperMaxCount ? 1210 : 0;
        }
    }
    public void InputItem() { }
}
public static class GameMain { public static long gameTick; public static object data = new object(); }
public static class ProjectEdenPlugin { public static LogStub Log = new LogStub(); }
public class LogStub { public void LogInfo(string s) { } }
namespace HarmonyLib
{
    public class HarmonyPatch : Attribute { public HarmonyPatch() { } public HarmonyPatch(Type t, string name) { } }
    public class HarmonyFinalizer : Attribute { }
    public class HarmonyPostfix : Attribute { }
    public class HarmonyPrefix : Attribute { }
}

namespace ProjectEden.Patches.Diagnostics { static class NeedsRefreshProbe { internal static bool Active => false; internal static void BeforeLock(int slots) { } internal static void LockAcquired() { } internal static void ScanFinished() { } internal static void SlotsVisited(int count, bool rebuilt) { } internal static void ExpandedFinished() { } } }

public class PlanetTransport { public StationComponent[] stationPool; public void SetStationStorage(int stationId) { } public void GameTick() { } }

namespace ProjectEden.Patches.Diagnostics { static class TransportSplitProbe { internal static bool Armed = true; } }
