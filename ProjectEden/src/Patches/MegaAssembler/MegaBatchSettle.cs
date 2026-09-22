// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 批量结算：把巨型建筑一个 tick 里的 N 个配方周期，从「调 N 遍原版」变成
    /// 「调一遍原版 + 一次乘法」。
    ///
    /// <b>为什么做。</b> 实测一颗星球：性能面板「生产设施」12.276 ms、占逻辑帧 71%，
    /// 而 <c>MegaTickProfiler</c> 把 <c>MegaTick</c> 拆开后<b>配方周期占 99.1%</b>
    /// （储物格同步 0.5%、传送带槽位 0.2%）——也就是说本 mod 自己的包装可以忽略，
    /// 剩下的全是原版 <c>AssemblerComponent.InternalUpdate</c> 被调用了三万次每帧。
    ///
    /// <b>这不是「重写原版的结算逻辑」，是「量一遍再乘」。</b> 先让原版<b>照常跑一次</b>，
    /// 比对前后差值量出这一个周期到底吃了什么、出了什么，再按缓冲还够几遍乘上去。
    /// 和 <c>RunExtraCycles</c> 数 <c>settled</c> 用的是同一套路子——<b>实测而不是复现</b>。
    ///
    /// ── 稳态是什么形状（这是整件事成立的前提，实测自 IL）─────────────────
    ///
    /// <c>InternalUpdate</c> 一次调用做两件事：<b>结算上一个周期</b>（0101 起，
    /// <c>time >= timeSpend</c> 时过输出闸、写产物、<c>cycleCount++</c>、
    /// <c>time -= timeSpend</c>），然后<b>为下一个周期扣料</b>（038E 起，
    /// <c>!replicating</c> 时检查并扣掉 <c>requireCounts</c>、置 <c>replicating</c>）。
    /// 所以稳态下一次调用的净变化正好是：
    /// <code>
    ///   served[i]   -= requireCounts[i]
    ///   produced[j] += productCounts[j]
    ///   cycleCount  += 1
    /// </code>
    /// 而 <b><c>time</c> 不用碰</b>：每次调用是 <c>time -= timeSpend</c> 再
    /// <c>time += power × speedOverride</c>，而 <c>speedOverride</c> 是 1e8、比任何
    /// <c>timeSpend</c> 大一两个数量级，所以调用结束时 <c>time</c> 必然已经饱和在
    /// 「下一个周期随时可结算」的状态。批量不调用，<c>time</c> 就停在那儿——正确，
    /// 而且是保守的一侧。<c>replicating</c> / <c>speedOverride</c> 同理。
    ///
    /// ── 三道上界，前两道是量的，第三道是抄的 ───────────────────────────
    ///
    /// 1. <b>原料</b>：<c>served[i] / requireCounts[i]</c>。量出来的，没有解释空间。
    /// 2. <b>周期预算</b>：<c>cyclesPerTick</c> 还剩几个。
    /// 3. <b>输出闸</b>：<b>这一道必须复现原版</b>，因为它是个按 <c>recipeType</c>
    ///    分支的表（IL 013E–02F5，单产物那三支就是多产物同一条规则的展开）：
    /// <code>
    ///   recipeType 1  冶炼   produced[j] + count[j] > 100      → 拒绝
    ///   recipeType 4  组装   produced[j] > count[j] × 9        → 拒绝
    ///   其余（2/3/5/默认）   produced[j] > count[j] × 19       → 拒绝
    /// </code>
    ///    <b>但后两档的系数现在不是 9 / 19 了</b>——<see cref="MegaOutputGatePatches"/>
    ///    把它们对巨型建筑抬到了 <c>cyclesPerTick × 全局分频 − 1</c>。所以这里
    ///    <b>必须问它要，不能照抄常量</b>：抄死了只会让批量止步于 20，剩下的周期
    ///    退回逐次真调，**一个字都不报，只是慢几十倍**（实测 14 ms → 190 ms）。
    ///    这是同一个数的第四份手抄件（前三份：PlanetCensus、ReferenceRatePatches、这里）。
    ///    <b>复现一道闸正是本仓库记过账的那类错</b>（钻头消耗：在原版决定要不要动手之前
    ///    跑的钩子，必须复现那个决定，而不是名义速率）。所以它<b>必须</b>配自检——
    ///    见 <see cref="MegaBatchAudit"/>，那不是装饰，是这条路能走的唯一理由。
    ///
    /// ── 不批量的情况，以及为什么要划这条线 ────────────────────────────
    ///
    /// <b>带增产剂的建筑一律退回逐次循环。</b> <c>split_inc_level</c> 每周期从
    /// <c>incServed</c> 扣点数，点数见底时 <c>extraSpeed</c> 归零（IL 04CF–0549），
    /// 于是<b>每周期的净变化在批量中途就变了</b>——这是真正的制度切换，而
    /// 「量一遍再乘」的前提正是中途不变。硬做也不是不行（把 n 卡在点数耗尽那个拐点上），
    /// 但那要依赖 <c>split_inc_level</c> 的取整行为，错了会静默地多产或少产增产产物。
    /// <b>划掉它，批量就在构造上没有制度切换。</b> 代价是那些建筑拿不到加速，
    /// 覆盖率每 60 秒打进日志，值不值看得见。
    ///
    /// 同理，一次调用如果没有正好落在稳态（<c>cycleCount</c> 没 +1、产物/原料的差值
    /// 和配方表对不上、额外产物动了），也<b>一律退回逐次</b>——判据是<b>观测到的差值
    /// 和配方表严格相等</b>，不是「差不多」。
    /// </summary>
    internal static class MegaBatchSettle
    {
        private static int _enabled = -1;

        /// <summary>自检抓到不一致时整局关掉批量，退回逐次——功能不受影响，只是慢。</summary>
        private static int _disabledByAudit;

        internal static bool Enabled
        {
            get
            {
                if (Volatile.Read(ref _disabledByAudit) != 0) return false;

                int e = _enabled;

                if (e >= 0) return e != 0;

                e = MegaBuildingRegistry.Config?.batchSettle == true ? 1 : 0;
                _enabled = e;

                return e != 0;
            }
        }

        internal static void DisableByAudit() => Volatile.Write(ref _disabledByAudit, 1);

        // 覆盖率统计：批量吃掉了多少周期、退回逐次的又有多少。
        private static long _batched;
        private static long _stepped;
        private static long _bailProliferator;
        private static long _bailShape;

        internal static long Batched => Interlocked.Read(ref _batched);
        internal static long Stepped => Interlocked.Read(ref _stepped);
        internal static long BailProliferator => Interlocked.Read(ref _bailProliferator);
        internal static long BailShape => Interlocked.Read(ref _bailShape);

        internal static void CountStepped(int n) { if (n > 0) Interlocked.Add(ref _stepped, n); }

        /// <summary>
        /// 带增产剂就不批量。判据用<b>状态</b>（<c>incUsed</c> / <c>incServed</c> /
        /// <c>extraSpeed</c> / <c>extraTime</c>）而不是配方或建筑类型——同一台机器
        /// 喷不喷是随时会变的，按类型判会在变的那一刻悄悄失准。
        /// </summary>
        internal static bool CanBatch(ref AssemblerComponent c)
        {
            if (!Enabled) return false;

            // **四个条件各记各的。** 它们原来合成一个「带增产剂」计数，于是日志只能说
            // 「全都退回了」而说不出为什么——而这四条的含义完全不同：incServed 有余量是
            // 玩家真的喷了（结构性，只能这样），而 extraTime 非零更可能是**本 mod 自己**
            // 留下的（MegaLightPatches.Suppress 写 −speedOverride−1、MegaThrottle.Hold 写
            // −extraSpeed−1），那就是个 bug 而不是设计。**一个分不出分支的计数器量不出东西。**
            if (c.incUsed) { Interlocked.Increment(ref _bailIncUsed); return false; }
            if (c.extraSpeed != 0) { Interlocked.Increment(ref _bailExtraSpeed); return false; }

            // **这里曾经还有一条 `extraTime != 0`，它是这个功能整个失效的原因。**
            //
            // 走到这里 extraSpeed 已经是 0，而原版推进额外计时器的**唯一**一条指令是
            // IL 0586 的 `extraTime += (int)(power * extraSpeed)` —— 乘数是 0，所以
            // extraTime 被**冻住**了：它不可能在批量中途跨过 extraTimeSpend，也就不可能
            // 冒出一份没被复制的额外产出。所以它的值是多少都与批量无关。
            //
            // 而它偏偏很容易非零，且**存档里会一直留着**：原版没有任何一处把它清零，
            // 本 mod 的两处压制（MegaLightPatches.Suppress、MegaThrottle.RewindExtra）
            // 又会在 extraSpeed == 0 时写进一个 −1 / −speedOverride−1。于是「某一局开过
            // 一次分频」就足以让这个存档里每一台巨型建筑**永久**退出批量结算——实测
            // 363,714 台次全中，每台每 tick 真调 47 次 InternalUpdate，生产设施 14 ms → 210 ms。
            //
            // 去掉这一条顺带把已经中招的存档修好了，不需要迁移：判据本来就该问
            // 「计时器会不会动」，而不是「它现在是几」。
            // **一个过强的守卫不会报错，它只是让功能悄悄不生效。**

            int[] inc = c.incServed;

            if (inc != null)
                for (var i = 0; i < inc.Length; i++)
                    if (inc[i] != 0)
                    {
                        Interlocked.Increment(ref _bailIncServed);

                        return false;
                    }

            return true;
        }

        internal static void CountBailProliferator() => Interlocked.Increment(ref _bailProliferator);

        /// <summary>被 <see cref="CanBatch"/> 拒掉的四条理由，分开记。</summary>
        internal static long BailIncUsed => Interlocked.Read(ref _bailIncUsed);

        internal static long BailExtraSpeed => Interlocked.Read(ref _bailExtraSpeed);

        internal static long BailExtraTime => Interlocked.Read(ref _bailExtraTime);

        internal static long BailIncServed => Interlocked.Read(ref _bailIncServed);

        private static long _bailIncUsed, _bailExtraSpeed, _bailExtraTime, _bailIncServed;

        /// <summary><see cref="IsSteadyUnit"/> 的六个出口，分开记。</summary>
        internal static long ShapeCycle => Interlocked.Read(ref _shapeCycle);

        internal static long ShapeExtra => Interlocked.Read(ref _shapeExtra);

        internal static long ShapeNoRecipe => Interlocked.Read(ref _shapeNoRecipe);

        internal static long ShapeLength => Interlocked.Read(ref _shapeLength);

        internal static long ShapeServed => Interlocked.Read(ref _shapeServed);

        internal static long ShapeProduced => Interlocked.Read(ref _shapeProduced);

        private static long _shapeCycle, _shapeExtra, _shapeNoRecipe, _shapeLength, _shapeServed, _shapeProduced;

        /// <summary>
        /// 量一遍原版的净变化，判断它是不是稳态的那一个单位。
        /// <b>严格相等，不是「差不多」</b>：差一点点就说明这一次调用里还发生了别的事，
        /// 那就不该乘。
        /// </summary>
        internal static bool IsSteadyUnit(ref AssemblerComponent c, int[] servedBefore, int[] producedBefore,
            int cycleBefore, int extraCycleBefore)
        {
            // **六个出口各记各的。** 它们原来合成一个「没落在稳态」计数，于是日志只能说
            // 五分之一的台次退回了，说不出为什么——而这六条的含义差得很远：
            // 「一个周期都没结算」是这一台缺料 / 产物满（正常），而「差值和配方表对不上」
            // 是这一次调用里还发生了别的事（可能是别的机制在插手）。
            // 这一招在 CanBatch 上连中两次，同一个理由。
            if (c.cycleCount != cycleBefore + 1)
            {
                Interlocked.Increment(ref _shapeCycle);

                return false;
            }

            if (c.extraCycleCount != extraCycleBefore)
            {
                Interlocked.Increment(ref _shapeExtra);

                return false;
            }

            RecipeExecuteData data = c.recipeExecuteData;

            if (data?.requireCounts == null || data.productCounts == null)
            {
                Interlocked.Increment(ref _shapeNoRecipe);

                return false;
            }

            int[] served = c.served;
            int[] produced = c.produced;

            if (served == null || produced == null
                               || served.Length != data.requireCounts.Length
                               || produced.Length != data.productCounts.Length)
            {
                Interlocked.Increment(ref _shapeLength);

                return false;
            }

            for (var i = 0; i < served.Length; i++)
                if (servedBefore[i] - served[i] != data.requireCounts[i])
                {
                    Interlocked.Increment(ref _shapeServed);

                    return false;
                }

            for (var j = 0; j < produced.Length; j++)
                if (produced[j] - producedBefore[j] != data.productCounts[j])
                {
                    Interlocked.Increment(ref _shapeProduced);

                    return false;
                }

            return true;
        }

        internal static void CountBailShape() => Interlocked.Increment(ref _bailShape);

        /// <summary>
        /// 还能再结算几个周期。三道上界取最小，<b>任何一道算不出来就返回 0</b>
        /// （宁可不批量，也不猜）。
        /// </summary>
        internal static int BatchSize(ref AssemblerComponent c, int budget)
        {
            if (budget <= 0) return 0;

            RecipeExecuteData data = c.recipeExecuteData;

            if (data == null) return 0;

            int[] served = c.served;
            int[] requireCounts = data.requireCounts;
            int[] produced = c.produced;
            int[] productCounts = data.productCounts;

            if (served == null || requireCounts == null || produced == null || productCounts == null) return 0;

            int n = budget;

            // ① 原料：量出来的，没有解释空间。
            for (var i = 0; i < requireCounts.Length && n > 0; i++)
            {
                int need = requireCounts[i];

                if (need <= 0) continue;

                int can = served[i] / need;

                if (can < n) n = can;
            }

            if (n <= 0) return 0;

            // ② 输出闸：抄原版那张按 recipeType 的表。**这一道是复现，所以必须有自检。**
            for (var j = 0; j < productCounts.Length && n > 0; j++)
            {
                int cnt = productCounts[j];

                if (cnt <= 0) continue;

                int p = produced[j];
                int can;

                if (c.recipeType == ERecipeType.Smelt)
                    // 接受条件 p + k·cnt + cnt <= 100，最紧的一次是 k = n−1 → p + n·cnt <= 100
                    // 加法闸，MegaOutputGatePatches 刻意没动它（它本来就不卡巨型建筑）
                    can = (100 - p) / cnt;
                else
                    // **系数必须问 MegaOutputGatePatches 要，不能写死 9 / 19。**
                    // 那两个数是原版的，而转译器已经把巨型建筑那两档抬到了
                    // cyclesPerTick × 全局分频 − 1；这里写死就等于「批量只敢批到 20，
                    // 剩下的每一个周期都真调一次 InternalUpdate」——**不报错，只是慢几十倍**。
                    // 实测代价：生产设施 14 ms → 190 ms。
                    //
                    // 接受条件 p + k·cnt <= 系数·cnt，最紧的一次是 k = n−1
                    //   → n <= ((系数 + 1)·cnt − p) / cnt
                    // 代入原版系数 9 / 19 得回 (10·cnt − p)/cnt 和 (20·cnt − p)/cnt，
                    // 所以这是把原来那两行参数化，不是换了一套算法。
                    can = ((MegaOutputGatePatches.Scale(
                                c.recipeType == ERecipeType.Assemble ? 9 : 19, ref c) + 1) * cnt - p) / cnt;

                if (can < n) n = can;
            }

            return n > 0 ? n : 0;
        }

        /// <summary>
        /// 把量出来的那一个周期乘 n 遍落地。
        ///
        /// <b>统计寄存器用的锁和原版同一个对象</b>（原版是
        /// <c>Monitor.Enter(productRegister)</c> / <c>Enter(consumeRegister)</c>），
        /// 否则并行的装配 tick 会把统计数写坏——那是「非并发集合被并发改」那一族，
        /// 本仓库在 <c>MegaVirtualLogisticsPatches</c> 上崩过一次。
        ///
        /// <b>不碰 <c>time</c> / <c>replicating</c> / <c>speedOverride</c></b>：
        /// 调用结束时它们已经在「下一个周期随时可结算」的稳态上，见类注释。
        /// </summary>
        internal static void Apply(ref AssemblerComponent c, int n, int[] productRegister, int[] consumeRegister)
        {
            if (n <= 0) return;

            RecipeExecuteData data = c.recipeExecuteData;

            int[] served = c.served;
            int[] requires = data.requires;
            int[] requireCounts = data.requireCounts;
            int[] produced = c.produced;
            int[] products = data.products;
            int[] productCounts = data.productCounts;

            // ── 先改本组件自己的字段：无竞争，不该待在临界区里 ──────────────
            for (var i = 0; i < requireCounts.Length; i++) served[i] -= requireCounts[i] * n;

            for (var j = 0; j < productCounts.Length; j++) produced[j] += productCounts[j] * n;

            // ── 再一次性进临界区累加统计寄存器 ────────────────────────────
            //
            // **锁原本在循环里，每个原料、每个产物各抢一次。** 那两个寄存器是
            // **全局共享**的 int[12000]，而装配 tick 是按星球分线程的并行路径——
            // 9326 台 × 60 tick × 约 4 把 ≈ 每秒 220 万次 Monitor 全压在两个对象上，
            // 11 条线程互相抢。提到循环外之后每台每 tick 最多 2 次，而且临界区里
            // 只剩几条加法。
            //
            // **不能换成 Interlocked.Add**：原版自己是 `lock (productRegister)` 里做
            // 非原子的读-改-写，两种机制混用会丢更新（原版读完、我们 Interlocked 加、
            // 原版再写回，我们那一笔就没了）。锁对象必须和原版是同一个。
            if (consumeRegister != null && requires != null)
                lock (consumeRegister)
                    for (var i = 0; i < requireCounts.Length; i++)
                    {
                        int take = requireCounts[i] * n;
                        int id = requires[i];

                        if (take != 0 && id > 0 && id < consumeRegister.Length)
                            consumeRegister[id] += take;
                    }

            if (productRegister != null && products != null)
                lock (productRegister)
                    for (var j = 0; j < productCounts.Length; j++)
                    {
                        int make = productCounts[j] * n;
                        int id = products[j];

                        if (make != 0 && id > 0 && id < productRegister.Length)
                            productRegister[id] += make;
                    }

            c.cycleCount += n;

            Interlocked.Add(ref _batched, n);
        }
    }

    /// <summary>
    /// 量差值用的快照缓冲。
    ///
    /// <b>必须 <c>[ThreadStatic]</c> 且惰性创建。</b> 装配 tick 跑在
    /// <c>_assembler_parallel</c> 上、按星球分线程，共享一份静态缓冲会被并发写坏；
    /// 而 <c>[ThreadStatic]</c> 的字段初始化器<b>只在第一个线程上跑</b>，所以只能惰性建。
    /// 这条是本仓库明文记过的坑（<c>MegaVirtualLogisticsPatches</c> 当年就是这么崩的）。
    ///
    /// 复用而不是每 tick 新建：<b>tick 路径上不许分配</b>。
    /// </summary>
    internal static class BatchScratch
    {
        internal static int[] Snapshot(ref int[] slot, int[] source)
        {
            if (source == null) return null;

            int[] buf = slot;

            if (buf == null || buf.Length < source.Length)
            {
                buf = new int[source.Length];
                slot = buf;
            }

            for (var i = 0; i < source.Length; i++) buf[i] = source[i];

            return buf;
        }
    }
}
