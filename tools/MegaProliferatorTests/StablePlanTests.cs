using System;
using ProjectEden.Patches;

static class StablePlanTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Compare(int time, int extra, int period, int extraPeriod, int step, int extraStep, int budget, long capacity)
    {
        bool accepted = MegaProliferatorBatch.TryStablePlan(time, extra, period, extraPeriod, step, extraStep, budget, capacity, out var p);
        if (!accepted) return;
        int count = 0, bonusTotal = 0, t = time, e = extra;
        for (int i = 0; i < budget; i++)
        {
            if (t < period) break;
            int bonus = e >= extraPeriod ? 1 : 0;
            if ((long)count + bonusTotal + bonus + 1 > capacity) break;
            long nt = (long)t - period, ne = (long)e - bonus * (long)extraPeriod;
            if (nt < period && ne < extraPeriod) { nt += step; ne += extraStep; }
            if (nt > int.MaxValue || ne > int.MaxValue) break;
            t = (int)nt; e = (int)ne; count++; bonusTotal += bonus;
        }
        if (count < 2) Check(p.Count == 0, "单周期边界未回退");
        else Check(p.Mathematical && p.Count == count && p.Extra == bonusTotal && p.Time == t && p.ExtraTime == e, "数学/逐周期规划不一致");
    }
    internal static void Run()
    {
        // 小整数穷举覆盖所有增产相位、零/满步长、容量刚好容纳或少一个产物。
        for (int period = 1; period <= 5; period++)
        for (int ep = 1; ep <= 10; ep++)
        for (int t = period; t < period * 2; t++)
        for (int e = 0; e < ep * 2; e++)
        for (int x = 0; x <= ep; x++)
        for (int cap = 0; cap <= 20; cap++) Compare(t, e, period, ep, period, x, 12, cap);
        var r = new Random(930);
        for (int i = 0; i < 100000; i++)
        {
            int period = r.Next(1, 1000000000), ep = r.Next(1, 1000000000);
            Compare(period + r.Next(period), r.Next(ep * 2), period, ep, period, r.Next(ep + 1), r.Next(2, 1000), r.Next(2000));
        }
        Check(!MegaProliferatorBatch.TryStablePlan(10, 0, 10, 100, 9, 1, 100, 100, out _), "未达稳定步长未回退");
        Check(!MegaProliferatorBatch.TryStablePlan(20, 0, 10, 100, 10, 1, 100, 100, out _), "主进度积压未回退");
        Check(!MegaProliferatorBatch.TryStablePlan(10, 200, 10, 100, 10, 1, 100, 100, out _), "额外进度积压未回退");
        Check(!MegaProliferatorBatch.TryStablePlan(10, 0, 10, int.MaxValue, 10, 2, 100, 100, out _), "计时溢出未回退");
        Check(MegaProliferatorBatch.TryStablePlan(1, 0, 1, 1, 1, 1, int.MaxValue, int.MaxValue, out var large)
            && large.Count == 1073741824 && large.Extra == 1073741823 && large.ExtraTime == 1, "大预算乘法或二分溢出");
        int mathematical = 0;
        for (int i = 0; i < 20000; i++)
        {
            int level = r.Next(11), period = r.Next(1, 1000) * 10000;
            var c = new AssemblerComponent { speed = 100000000, speedOverride = 100000000,
                recipeType = (ERecipeType)r.Next(1, 6), forceAccMode = r.Next(2) == 0,
                served = new[] { r.Next(20, 3000), r.Next(20, 3000) }, incServed = new int[2],
                produced = new int[2], needs = new int[6],
                recipeExecuteData = new RecipeExecuteData { timeSpend = period, extraTimeSpend = period * 10,
                    productive = r.Next(2) == 0, requires = new[] { 1, 2 }, requireCounts = new[] { 1, 2 },
                    products = new[] { 10, 11 }, productCounts = new[] { r.Next(1, 51), r.Next(1, 8) } } };
            for (int j = 0; j < 2; j++) c.incServed[j] = c.served[j] * level;
            float power = new[] { 0.1f, 0.3f, 0.7f, 1f }[r.Next(4)];
            c.InternalUpdate(power, new int[32], new int[32]);
            c.extraTime = r.Next(period * 20);
            for (int j = 0; j < 2; j++) c.produced[j] = r.Next(1000);
            var p = MegaProliferatorBatch.MakePlan(ref c, r.Next(2, 500), power);
            if (p.Count == 0) continue;
            if (p.Mathematical) mathematical++;
            Check(MegaProliferatorBatch.Verify(ref c, p, power, 32, 32), "真实Harmony逐次回放不一致 " + i);
        }
        Check(mathematical > 5000, "数学路径覆盖不足: " + mathematical);
        Check(MegaProliferatorBatch.MathCycles > 0, "生产编排未命中数学路径");
        Console.WriteLine("PASS: 数学计时小整数穷举 + 100000大数状态；20000真实Harmony状态（数学命中 " + mathematical + "）；完整库存/增产点/产物/计时/统计回放一致。");
    }
}
