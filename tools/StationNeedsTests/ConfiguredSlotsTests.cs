using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ProjectEden.Patches;

static class ConfiguredSlotsTests
{
    static Func<StationComponent, bool> Bind(Type t) => (Func<StationComponent, bool>)t.GetMethod("UpdateNeeds_Prefix", BindingFlags.Static | BindingFlags.NonPublic).CreateDelegate(typeof(Func<StationComponent, bool>));
    static readonly Func<StationComponent, bool> Current = Bind(typeof(StationBeltInputPatches)), Previous = Bind(typeof(UncachedNeedsReference));
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Compare(StationComponent s)
    {
        var expected = new StationComponent { id = s.id, storage = s.storage, needs = (int[])s.needs.Clone(), isStellar = s.isStellar, warperCount = s.warperCount, warperMaxCount = s.warperMaxCount };
        if (Previous(expected)) expected.UpdateNeeds();
        if (Current(s)) s.UpdateNeeds();
        Check(s.needs.SequenceEqual(expected.needs), "缓存与逐格扫描需求不一致");
    }
    static StationComponent Create(int count)
    {
        var s = new StationComponent { id = 1, storage = new StationStore[30], needs = new int[6] };
        for (int i = 0; i < count; i++) s.storage[i] = new StationStore { itemId = i + 1, max = 100 };
        return s;
    }
    internal static void Run()
    {
        var s = Create(3);
        var rng = new Random(6401);
        for (int i = 0; i < 100000; i++)
        {
            GameMain.gameTick = i;
            int slot = rng.Next(30);
            s.storage[slot].count = rng.Next(101);
            s.storage[slot].max = rng.Next(101);
            if (i % 17 == 0)
            {
                s.storage[slot].itemId = rng.Next(3) == 0 ? 0 : rng.Next(1, 100);
                StationConfiguredSlots.Invalidate(s.storage);
            }
            if (i % 151 == 0) s.storage = (StationStore[])s.storage.Clone();
            s.isStellar = i % 5 == 0; s.warperMaxCount = 50; s.warperCount = i % 100;
            Compare(s);
        }
        // 未知mod绕过入口直接加格位，错峰复查必须在60tick内补上。
        s = Create(0); GameMain.gameTick = 100; Current(s);
        s.storage[29] = new StationStore { itemId = 777, max = 100 };
        GameMain.gameTick += 60; Compare(s);
        Check(s.needs[0] == 777, "兜底复查未识别直接写入");
        // 回拨时间、换存档、同站号更换数组，以及已有格数量/容量变化都不能串缓存。
        GameMain.gameTick = 1; s.storage[10] = new StationStore { itemId = 888, max = 100 }; Compare(s);
        GameMain.data = new object(); GameMain.gameTick++;
        s.storage = new StationStore[32]; s.storage[31] = new StationStore { itemId = 999, max = 1 }; Compare(s);
        s.storage[31].count = 1; Compare(s);
        s.storage[31].max = 2; Compare(s);
        // 同数组跨工作线程共享失效标志；生产代码在同一库存锁下读取布局。
        Parallel.For(0, 16, worker =>
        {
            for (int i = 0; i < 1000; i++) lock (s.storage)
            {
                s.storage[worker].itemId = (i & 1) == 0 ? worker + 1 : 0;
                s.storage[worker].max = 10;
                StationConfiguredSlots.Invalidate(s.storage);
                Compare(s);
            }
        });
        Console.WriteLine("PASS: 布局缓存10万次动态库存/容量/换货对照，数组替换、32位、时间回拨、换档、60tick兜底及1.6万次跨线程失效。");
        // 多站热集，避免单站缓存掩盖字典查找成本；计入定期布局复查。
        foreach (int configured in new[] { 0, 3, 6, 15, 30 })
        {
            var stations = Enumerable.Range(0, 4096).Select(i => { var x = Create(configured); x.id = i + 1; return x; }).ToArray();
            for (int i = 0; i < 50000; i++) { Previous(stations[i & 4095]); Current(stations[i & 4095]); }
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 500000; i++) { GameMain.gameTick = 1000 + i / 4096; Previous(stations[i & 4095]); }
            double old = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            for (int i = 0; i < 500000; i++) { GameMain.gameTick = 1000 + i / 4096; Current(stations[i & 4095]); }
            Console.WriteLine($"4096站/每站配置{configured}格，50万次：逐格版{old:0.0}ms，布局缓存{watch.Elapsed.TotalMilliseconds:0.0}ms（.NET8替身，非游戏帧率）。");
        }
    }
}
