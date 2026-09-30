using System;
using System.Linq;
using System.Reflection.Emit;
using System.Threading.Tasks;
using HarmonyLib;
using ProjectEden.Patches.Diagnostics;

static class StationLoopProbeTests
{
    static void Check(bool v, string m) { if (!v) throw new Exception(m); }
    internal static void Run()
    {
        new Harmony("ProjectEden.StationLoop.OfflineTest").CreateClassProcessor(typeof(StationLoopProbe)).Patch();
        TransportSplitProbe.Armed = true;
        StationLoopProbe.Take();
        Parallel.For(0, 8, worker =>
        {
            var p = new PlanetTransport { LoopStations = new[] {
                new StationComponent(), new StationComponent { isCollector = true },
                new StationComponent { isStellar = true, isVeinCollector = true } } };
            for (int i = 0; i < 10000; i++) p.GameTick(false);
            Check(p.LoopStations[0].LoopValue == 200000 && p.LoopStations[1].LoopValue == 310000 && p.LoopStations[2].LoopValue == 400000, "采样改变运输结果");
        });
        var t = StationLoopProbe.Take();
        Check(t[0] > 500 && t[0] < 2500 && t[2] == 0, "采样轮次异常");
        Check(t[4] == t[0] * 3 && t[7] == t[0] && t[10] == t[0] && t[13] == t[0] && t[16] == t[0] * 3, "并发分类计数不守恒");
        Check(t[1] >= t[3] + t[6] + t[9] + t[12] + t[15], "循环计时边界不守恒");
        var nested = new PlanetTransport { LoopStations = new[] { new StationComponent() } };
        var outer = new PlanetTransport { LoopStations = new[] { new StationComponent { LoopDuring = () => nested.GameTick(false) } } };
        for (int i = 0; i < 10000; i++) outer.GameTick(false);
        t = StationLoopProbe.Take();
        Check(t[0] > 0 && t[4] == t[0] && t[16] == t[0], "嵌套重复计时或未恢复外层");
        Check(nested.LoopStations[0].LoopValue == 200000, "嵌套执行结果改变");
        outer.LoopStations[0].LoopDuring = () => { throw new InvalidOperationException(); };
        for (int i = 0; i < 10000; i++)
        {
            try { outer.GameTick(false); throw new Exception("吞掉异常"); }
            catch (InvalidOperationException) { }
        }
        t = StationLoopProbe.Take();
        Check(t[0] > 0 && t[2] == t[0] && t[4] == t[0] && t[16] == 0, "异常样本未收尾");
        outer.LoopStations[0].LoopDuring = null;
        for (int i = 0; i < 10000; i++) outer.GameTick(false);
        t = StationLoopProbe.Take();
        Check(t[0] > 0 && t[2] == 0 && t[16] == t[0], "异常污染后续轮次");
        TransportSplitProbe.Armed = false;
        for (int i = 0; i < 1000; i++) outer.GameTick(false);
        Check(StationLoopProbe.Take().All(x => x == 0), "关闭后仍采样");
        var input = new[] { new CodeInstruction(OpCodes.Ret) };
        var generator = new DynamicMethod("Shape", typeof(void), Type.EmptyTypes).GetILGenerator();
        Check(StationLoopProbe.Transpile(input, generator).Single() == input[0], "形状回退改变指令");
        TransportSplitProbe.Armed = true;
        for (int i = 0; i < 1000; i++) outer.GameTick(false);
        Check(StationLoopProbe.Take().All(x => x == 0), "形状失败仍采样");
        Console.WriteLine("PASS：物流站循环细分真实Harmony接入、8万轮并发、分类守恒、嵌套、异常恢复、开关及形状回退。");
    }
}
