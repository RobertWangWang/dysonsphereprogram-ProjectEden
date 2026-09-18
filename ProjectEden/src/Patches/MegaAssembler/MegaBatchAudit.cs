// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 批量结算的守恒自检——<b>它不是装饰，是 <see cref="MegaBatchSettle"/> 能存在的前提。</b>
    ///
    /// 批量的三道上界里，原料和周期预算是量出来的，<b>输出闸是复现原版的</b>
    /// （按 <c>recipeType</c> 分支的 100 / ×9 / ×19 表）。复现一道闸就是本仓库付过
    /// 学费的那类错：原版将来多一道闸、或者哪个常数变了，复现的那份不会报错，
    /// 它会<b>静默地多产或少产</b>。所以这里用<b>回放</b>去验它。
    ///
    /// <b>回放怎么做的：</b>在真的落地之前，把这台建筑的状态<b>深拷一份</b>，在副本上
    /// 老老实实调 <c>n + 1</c> 遍原版 <c>InternalUpdate</c>，数出实际结算了几个周期，
    /// 再和我们算出来的 <c>n</c> 比：
    /// <list type="bullet">
    /// <item>回放 == n：一致，放行。</item>
    /// <item>回放 &gt; n：我们偏保守（少乘了），只是慢一点，记一行 INFO。</item>
    /// <item>回放 &lt; n：<b>我们会多产</b>——这正是要抓的那个错。整局关掉批量、
    /// 退回逐次循环，并报 ERROR 把两个数都印出来。</item>
    /// </list>
    ///
    /// <b>为什么回放 n + 1 遍而不是 n 遍</b>：只跑 n 遍的话，「原版本来还能再跑一遍」
    /// 和「正好 n 遍」分不开，而那正是「我们是不是偏保守」这个问题。
    ///
    /// <b>三个实现上的讲究：</b>
    ///
    /// 1. <b><c>AssemblerComponent</c> 是结构体，直接赋值只拷标量，数组还是同一份。</b>
    ///    所以 <c>served</c> / <c>incServed</c> / <c>produced</c> / <c>needs</c> 必须
    ///    逐个换成拷贝，否则回放会直接改坏真实建筑——自检本身变成最严重的 bug。
    ///    <c>recipeExecuteData</c> 这一路是只读的，共享即可。
    /// 2. <b>统计寄存器要用替身。</b> 回放会往 <c>productRegister</c> /
    ///    <c>consumeRegister</c> 里加数，用真的就会让生产统计凭空翻倍。
    ///    替身按真寄存器的长度懒分配一次并复用——<b>tick 路径上不许每次分配</b>。
    /// 3. <b>整个自检由 <c>Interlocked</c> 抢占，同一时刻只有一台在跑。</b>
    ///    装配 tick 是并行的，替身数组只有一份。
    /// </summary>
    internal static class MegaBatchAudit
    {
        /// <summary>多久抽查一次（秒）。用 tick 计数而不是墙钟——这条在工作线程上。</summary>
        private const long IntervalTicks = 30 * 60;

        private static long _nextTick;
        private static int _busy;

        private static int[] _productScratch;
        private static int[] _consumeScratch;

        private static long _checks;
        private static long _conservative;
        private static int _reportedOnce;

        internal static long Checks => Interlocked.Read(ref _checks);
        internal static long Conservative => Interlocked.Read(ref _conservative);

        /// <summary>
        /// 该抽查了吗。用 <c>GameMain.gameTick</c> 而不是 <c>realtimeSinceStartup</c>：
        /// 后者在工作线程上不可用。<b>倒退（换存档）就重新对表</b>，否则
        /// <c>next = tick + 间隔</c> 永远不再到期、自检静默死掉，而那看起来和
        /// 「一直没抓到问题」一模一样（本仓库第 4 号坑）。
        /// </summary>
        internal static bool Due()
        {
            long now = GameMain.gameTick;
            long next = Interlocked.Read(ref _nextTick);

            if (now < next)
            {
                // 倒退：重新对表，这一次不查。
                if (next - now > IntervalTicks * 4) Interlocked.Exchange(ref _nextTick, now + IntervalTicks);

                return false;
            }

            return Interlocked.CompareExchange(ref _nextTick, now + IntervalTicks, next) == next;
        }

        /// <summary>
        /// 回放校验。返回 true 表示放行（一致或偏保守）；false 表示抓到多产，
        /// 调用方应当退回逐次。
        /// </summary>
        internal static bool Verify(ref AssemblerComponent c, int n, float power,
            int[] productRegister, int[] consumeRegister)
        {
            if (n <= 0) return true;
            if (Interlocked.Exchange(ref _busy, 1) != 0) return true; // 别的线程在查，这次放行

            try
            {
                int[] pr = Scratch(ref _productScratch, productRegister);
                int[] cr = Scratch(ref _consumeScratch, consumeRegister);

                if (pr == null || cr == null) return true;

                // 结构体赋值只拷标量，数组得逐个换成拷贝——否则会改坏真实建筑。
                AssemblerComponent copy = c;

                copy.served = (int[])c.served.Clone();
                copy.produced = (int[])c.produced.Clone();

                if (c.incServed != null) copy.incServed = (int[])c.incServed.Clone();
                if (c.needs != null) copy.needs = (int[])c.needs.Clone();

                int before = copy.cycleCount;

                for (var k = 0; k <= n; k++) copy.InternalUpdate(power, pr, cr);

                int replayed = copy.cycleCount - before;

                Interlocked.Increment(ref _checks);

                if (replayed == n) return true;

                if (replayed > n)
                {
                    // 偏保守：少乘了，只是慢一点。记一次，整局只说一行。
                    Interlocked.Increment(ref _conservative);

                    if (Interlocked.Exchange(ref _reportedOnce, 1) == 0)
                        ProjectEdenPlugin.Log.LogInfo(
                            $"巨型建筑·批量结算·自检：回放 {replayed} 个周期，我们只算了 {n} 个——"
                            + "**偏保守，不影响正确性**（少乘几个周期只是慢一点，不会多产也不会少产）。"
                            + "整局只报这一行；真正要抓的是反过来的那种，那会报 ERROR。");

                    return true;
                }

                // 回放 < n：我们会多产。这就是要抓的那个错。
                ProjectEdenPlugin.Log.LogError(
                    $"巨型建筑·批量结算·自检**不一致**：我们算出还能跑 {n} 个周期，"
                    + $"而原版回放只跑得了 {replayed} 个——照这个乘下去会**多产**。"
                    + $"（配方类型 {c.recipeType}，产物缓冲首项 {c.produced[0]}，"
                    + "最可能的原因是输出闸那张按 recipeType 复现的表和原版对不上了。）"
                    + "**已整局关闭批量结算，退回逐次循环**——产能和正确性不受影响，只是慢回去。");

                MegaBatchSettle.DisableByAudit();

                return false;
            }
            catch (System.Exception e)
            {
                // 自检自己炸了绝不能拖垮生产：关掉批量，报出来，继续跑。
                ProjectEdenPlugin.Log.LogError(
                    $"巨型建筑·批量结算·自检抛异常，已整局关闭批量结算并退回逐次：{e}");

                MegaBatchSettle.DisableByAudit();

                return false;
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }

        /// <summary>替身寄存器：按真寄存器的长度懒分配一次并复用，tick 路径上不许每次分配。</summary>
        private static int[] Scratch(ref int[] slot, int[] real)
        {
            if (real == null) return null;

            int[] s = slot;

            if (s != null && s.Length >= real.Length) return s;

            s = new int[real.Length];
            slot = s;

            return s;
        }
    }
}
