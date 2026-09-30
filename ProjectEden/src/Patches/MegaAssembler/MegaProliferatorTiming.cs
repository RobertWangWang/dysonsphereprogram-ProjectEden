using System;

namespace ProjectEden.Patches
{
    // 巨型建筑按周期预算运行，不能再把万倍速的一整帧当作一个配方周期。
    internal static class MegaProliferatorTiming
    {
        internal static bool Enabled;
        private static double _capacityFactor;
        internal static bool Applies(ref AssemblerComponent c) => Enabled
            && c.speed >= MegaBuildingRegistry.MegaSpeedThreshold && c.recipeExecuteData != null;

        internal static int MainStep(int raw, ref AssemblerComponent c)
        {
            if (!Applies(ref c)) return raw;
            if (c.time < 0) return 0; // 分频/日照/催化剂暂停，不能偷偷推进。
            return Math.Min(raw, c.recipeExecuteData.timeSpend);
        }

        internal static int ExtraStep(int raw, ref AssemblerComponent c, float power)
        {
            if (!Applies(ref c)) return raw;
            if (c.time < 0 || c.speedOverride <= 0 || c.extraSpeed <= 0) return 0;
            int step = Math.Min((int)(power * c.speedOverride), c.recipeExecuteData.timeSpend);
            return (int)((long)Math.Max(0, step) * c.extraSpeed / c.speedOverride);
        }

        internal static void Repair(ref AssemblerComponent c)
        {
            if (!Applies(ref c)) return;
            if (c.extraTime < 0) c.extraTime = 0;
            // 存档只有一个已扣料的在制周期；旧速度溢出的主进度不是额外付过的原料。
            if (c.time > c.recipeExecuteData.timeSpend) c.time = c.recipeExecuteData.timeSpend;
        }

        // 同一补跑期间不会补料。取每个原料槽的最低等级，混合点数按原版下取整，
        // 不把历史 incUsed 或上一周期的加速状态误当成当前原料已喷涂。
        internal static int ScaleCycles(ref AssemblerComponent c, int cycles, long tick, PlanetFactory factory = null)
        {
            if (!Applies(ref c) || cycles <= 0 || (c.recipeExecuteData.productive && !c.forceAccMode)) return cycles;
            var d = c.recipeExecuteData;
            if (c.served == null || c.incServed == null || d.requireCounts == null
                || c.served.Length == 0 || c.incServed.Length != c.served.Length
                || d.requireCounts.Length != c.served.Length) return cycles;
            int level = Math.Min(10, Cargo.accTableMilli.Length - 1);
            for (int i = 0; i < c.served.Length; i++)
            {
                if (c.served[i] <= 0 || c.served[i] < d.requireCounts[i] || c.incServed[i] < 0) return cycles;
                level = Math.Min(level, c.incServed[i] / c.served[i]);
            }
            double scaled = cycles * (1.0 + Cargo.accTableMilli[level]);
            if (double.IsNaN(scaled) || scaled <= cycles) return cycles;
            if (scaled >= int.MaxValue) return int.MaxValue;
            // 将不足一个周期的份额按结算轮次错峰，避免 cycles=1 时永远丢掉小数。
            long milli = (long)Math.Round(scaled * 1000.0);
            if (milli % 1000 == 0) return (int)(milli / 1000);
            int divider = factory == null ? MegaThrottle.GlobalDivider : MegaThrottle.DividerFor(factory, c.entityId);
            long round = tick / Math.Max(1, divider);
            long phase = ((round % 1000) * (milli % 1000) + c.entityId) % 1000;
            return (int)((milli + phase) / 1000);
        }

        internal static int GateCycles(ref AssemblerComponent c)
        {
            return CapacityCycles(MegaThrottle.GateCycles(c.recipeType));
        }

        internal static int CapacityCycles(int basis)
        {
            if (!Enabled) return basis;
            double factor = System.Threading.Volatile.Read(ref _capacityFactor);
            if (factor == 0)
            {
                factor = 1;
                foreach (double x in Cargo.accTableMilli) factor = Math.Max(factor, 1 + x);
                foreach (double x in Cargo.incTableMilli) factor = Math.Max(factor, 1 + x);
                System.Threading.Volatile.Write(ref _capacityFactor, factor);
            }
            return (int)Math.Min(int.MaxValue / 1000, Math.Ceiling(basis * factor) + 2);
        }
    }
}
