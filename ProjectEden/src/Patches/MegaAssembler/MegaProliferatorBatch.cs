using System;
using System.Threading;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 普通配方的均匀喷涂批量路径。按原版顺序推进两个整数计时器，
    /// 稳态区间用整数公式合并计算和写入；不跨混合点数、缺料、堵料或品质边界。
    /// incUsed 是历史标记，不能用来推断当前库存是否喷涂。
    /// </summary>
    internal static class MegaProliferatorBatch
    {
        internal struct Plan
        {
            internal int Count, Extra, Time, ExtraTime;
            internal bool IncUsed, Mathematical;
        }
        private static long _calls, _batched, _checks, _mathCycles;
        internal static long MathCycles => Interlocked.Read(ref _mathCycles);
        internal static long Batched => Interlocked.Read(ref _batched);
        internal static long Checks => Interlocked.Read(ref _checks);

        internal static bool HasQuality(ref AssemblerComponent c)
        {
            if (!QualityAccess.CraftReady) return false;
            if (QualityAccess.GetQuaPending(ref c) != 0 || QualityAccess.GetQuaPendingItems(ref c) != 0) return true;
            var q = QualityAccess.GetQuaServed(ref c);
            if (q != null) foreach (int v in q) if (v != 0) return true;
            q = QualityAccess.GetQuaProduced(ref c);
            if (q != null) foreach (int v in q) if (v != 0) return true;
            return false;
        }

        // 每次主进度恰好补回一个周期，两个结算闸每次至多放行一次。
        // 第n次调用先结算、后推进，因此额外周期数是 floor((e+(n-1)*x)/E)，不能用n*x。
        // 仅在下面的边界内成立；其他计时形状继续走原有逐周期规划。
        internal static bool TryStablePlan(int time, int extraTime, int period, int extraPeriod,
            int step, int extraStep, int budget, long capacity, out Plan plan)
        {
            plan = new Plan();
            if (period <= 0 || extraPeriod <= 0 || budget < 2 || step != period
                || time < period || (long)time >= 2L * period
                || extraTime < 0 || (long)extraTime >= 2L * extraPeriod
                || extraStep < 0 || extraStep > extraPeriod
                || (long)extraPeriod - 1 + extraStep > int.MaxValue) return false;
            capacity = Math.Min(capacity, int.MaxValue);
            // 产物闸限制主产物与额外产物的合计。充足时O(1)，不足时二分最大安全前缀。
            int n = budget;
            if ((long)n + ((long)extraTime + (n - 1L) * extraStep) / extraPeriod > capacity)
            {
                int lo = 0, hi = budget;
                while (lo < hi)
                {
                    int mid = lo + (int)(((long)hi - lo + 1) / 2);
                    long bonus = ((long)extraTime + (mid - 1L) * extraStep) / extraPeriod;
                    if ((long)mid + bonus <= capacity) lo = mid;
                    else hi = mid - 1;
                }
                n = lo;
            }
            if (n < 2) return true; // 不提交一周期；交回真实InternalUpdate。
            long extras = ((long)extraTime + (n - 1L) * extraStep) / extraPeriod;
            plan.Count = n; plan.Extra = (int)extras; plan.Time = time;
            plan.ExtraTime = (int)((long)extraTime + (long)n * extraStep - extras * extraPeriod);
            plan.Mathematical = true;
            return true;
        }

        internal static Plan MakePlan(ref AssemblerComponent c, int budget, float power)
        {
            var plan = new Plan();
            var d = c.recipeExecuteData;
            if (budget < 2 || power < 0.1f || float.IsNaN(power) || float.IsInfinity(power)
                || !c.replicating || c.speed <= 0 || d == null || d.timeSpend <= 0 || d.extraTimeSpend <= 0
                || (int)c.recipeType < 1 || (int)c.recipeType > 5 || HasQuality(ref c)) return plan;
            if (c.served == null || c.incServed == null || c.produced == null || d.requires == null
                || d.requireCounts == null || d.products == null || d.productCounts == null
                || c.served.Length == 0 || c.produced.Length == 0
                || c.served.Length != d.requireCounts.Length || c.incServed.Length != c.served.Length
                || d.requires.Length != c.served.Length || c.produced.Length != d.productCounts.Length
                || d.products.Length != c.produced.Length) return plan;
            int level = 10;
            bool used = c.incUsed;
            for (int i = 0; i < c.served.Length; i++)
            {
                int count = c.served[i], points = c.incServed[i], need = d.requireCounts[i];
                // 整格点数为件数整数倍时，split_inc_level 的余数始终为零。
                if (count <= 0 || need <= 0 || points < 0 || points % count != 0) return plan;
                int l = points / count;
                if (l > 10) return plan;
                level = Math.Min(level, l); used |= l > 0;
                budget = Math.Min(budget, count / need);
            }
            if (budget < 2) return plan;
            bool productive = d.productive && !c.forceAccMode;
            double speed = productive ? c.speed : c.speed * (1.0 + Cargo.accTableMilli[level]) + 0.1;
            double extra = productive ? c.speed * Cargo.incTableMilli[level] * 10.0 + 0.1 : 0;
            if (speed > int.MaxValue || extra > int.MaxValue || speed < 0 || extra < 0) return plan;
            if (c.speedOverride != (int)speed || c.extraSpeed != (int)extra || c.extraPowerRatio != Cargo.powerTable[level]) return plan;
            // 多产物可先算出可写的总周期上界，避免逐周期扫描数组。
            long capacity = long.MaxValue;
            for (int j = 0; j < c.produced.Length; j++)
            {
                int amount = d.productCounts[j], current = c.produced[j];
                if (amount <= 0 || current < 0) return plan;
                long gate = c.recipeType == ERecipeType.Smelt ? (long)MegaOutputGatePatches.SmeltLimit(100, ref c) - amount
                    : (long)MegaOutputGatePatches.Scale(c.recipeType == ERecipeType.Assemble ? 9 : 19, ref c) * amount;
                if (current > gate) return plan;
                capacity = Math.Min(capacity, (gate - current) / amount + 1);
                capacity = Math.Min(capacity, (int.MaxValue - (long)current) / amount);
            }
            int time = c.time, extraTime = c.extraTime;
            float stepF = power * c.speedOverride, extraF = power * c.extraSpeed;
            if (stepF < 0 || stepF >= int.MaxValue || extraF < 0 || extraF >= int.MaxValue) return plan;
            int step = MegaProliferatorTiming.MainStep((int)stepF, ref c);
            int extraStep = MegaProliferatorTiming.ExtraStep((int)extraF, ref c, power);
            if (TryStablePlan(time, extraTime, d.timeSpend, d.extraTimeSpend, step, extraStep,
                budget, capacity, out var stable))
            {
                stable.IncUsed = used;
                return stable;
            }
            int producedCycles = 0;
            for (int i = 0; i < budget; i++)
            {
                // 非稳态或只结额外产物的边界交回真实 InternalUpdate。
                if (time < d.timeSpend) break;
                int bonus = extraTime >= d.extraTimeSpend ? 1 : 0;
                if ((long)producedCycles + bonus + 1 > capacity) break;
                long t = (long)time - d.timeSpend;
                long e = (long)extraTime - (bonus != 0 ? d.extraTimeSpend : 0);
                if (t < d.timeSpend && e < d.extraTimeSpend) { t += step; e += extraStep; }
                if (t > int.MaxValue || t < int.MinValue || e > int.MaxValue || e < int.MinValue) break;
                time = (int)t; extraTime = (int)e;
                producedCycles += bonus + 1;
                plan.Count++; plan.Extra += bonus;
            }
            plan.Time = time; plan.ExtraTime = extraTime; plan.IncUsed = used;
            if (plan.Count < 2) return new Plan();
            return plan;
        }

        internal static void Apply(ref AssemblerComponent c, Plan p, int[] pr, int[] cr)
        {
            var d = c.recipeExecuteData;
            for (int i = 0; i < c.served.Length; i++)
            {
                int level = c.incServed[i] / c.served[i];
                int take = d.requireCounts[i] * p.Count;
                c.served[i] -= take; c.incServed[i] -= level * take;
            }
            for (int j = 0; j < c.produced.Length; j++) c.produced[j] += d.productCounts[j] * (p.Count + p.Extra);
            lock (cr) for (int i = 0; i < d.requires.Length; i++) cr[d.requires[i]] += d.requireCounts[i] * p.Count;
            lock (pr) for (int j = 0; j < d.products.Length; j++) pr[d.products[j]] += d.productCounts[j] * (p.Count + p.Extra);
            c.time = p.Time; c.extraTime = p.ExtraTime; c.incUsed = p.IncUsed;
            c.cycleCount += p.Count; c.extraCycleCount += p.Extra;
        }

        internal static int TryApply(ref AssemblerComponent c, int budget, float power, int[] pr, int[] cr)
        {
            if (!MegaBatchSettle.Enabled || pr == null || cr == null) return 0;
            long planStart = Diagnostics.NanosecondProbe.Now();
            Plan p = MakePlan(ref c, budget, power);
            Diagnostics.NanosecondProbe.End(14, planStart);
            if (p.Count == 0) return 0;
            foreach (int id in c.recipeExecuteData.products) if (id < 0 || id >= pr.Length) return 0;
            foreach (int id in c.recipeExecuteData.requires) if (id < 0 || id >= cr.Length) return 0;
            // 独立于旧批量自检：首次及每65536次命中，逐字段/逐寄存器对照。
            if ((Interlocked.Increment(ref _calls) - 1) % 65536 == 0)
            {
                long auditStart = Diagnostics.NanosecondProbe.Now();
                try
                {
                    if (!Verify(ref c, p, power, pr.Length, cr.Length))
                        throw new InvalidOperationException("逐次回放与批量状态不一致");
                    Interlocked.Increment(ref _checks);
                }
                catch (Exception e)
                {
                    MegaBatchSettle.DisableByAudit();
                    ProjectEdenPlugin.Log.LogError($"均匀增产批量自检失败，已关闭批量并回退逐次。配方={c.recipeId}：{e}");
                    return 0;
                }
                finally { Diagnostics.NanosecondProbe.End(15, auditStart); }
            }
            long applyStart = Diagnostics.NanosecondProbe.Now();
            Apply(ref c, p, pr, cr);
            Diagnostics.NanosecondProbe.End(16, applyStart);
            Interlocked.Add(ref _batched, p.Count);
            if (p.Mathematical) Interlocked.Add(ref _mathCycles, p.Count);
            MegaBatchSettle.CountBatched(p.Count);
            return p.Count;
        }
        internal static AssemblerComponent Clone(AssemblerComponent c)
        {
            c.served = (int[])c.served.Clone(); c.incServed = (int[])c.incServed.Clone();
            c.produced = (int[])c.produced.Clone();
            if (c.needs != null) c.needs = (int[])c.needs.Clone();
            return c;
        }
        internal static bool Equal(int[] a, int[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        internal static bool Verify(ref AssemblerComponent c, Plan p, float power, int prLength, int crLength)
        {
            var actual = Clone(c); var batch = Clone(c);
            var pr = new int[prLength]; var cr = new int[crLength];
            var bp = new int[prLength]; var bc = new int[crLength];
            for (int i = 0; i < p.Count; i++) actual.InternalUpdate(power, pr, cr);
            Apply(ref batch, p, bp, bc);
            return actual.time == batch.time && actual.extraTime == batch.extraTime
                && actual.cycleCount == batch.cycleCount && actual.extraCycleCount == batch.extraCycleCount
                && actual.incUsed == batch.incUsed && actual.replicating == batch.replicating
                && actual.speedOverride == batch.speedOverride && actual.extraSpeed == batch.extraSpeed
                && actual.extraPowerRatio == batch.extraPowerRatio
                && Equal(actual.served, batch.served) && Equal(actual.incServed, batch.incServed)
                && Equal(actual.produced, batch.produced) && Equal(actual.needs, batch.needs)
                && Equal(pr, bp) && Equal(cr, bc);
        }
    }
}
