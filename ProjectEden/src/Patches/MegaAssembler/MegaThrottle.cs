// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Collections.Generic;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 逐建筑的节流阀：**每 tick 跑几个周期**，以及**几个 tick 才跑一次**。
    ///
    /// <b>为什么要有它：「巨型建筑但不是万倍速」在这之前做不到，而原因是反直觉的。</b>
    ///
    /// <c>speed</c> 绝对不能降——降到 <c>megaSpeedThreshold</c> 以下，<c>MegaTick</c> 就再也
    /// 认不出这台建筑（它靠 <c>speed &gt;= 阈值</c> 认身份），那台建筑会永远退回原版行为。
    /// 所以节流阀只能在周期数上。可是：
    ///
    /// <code>
    /// speed = 1e8，timeSpend = 配方秒数 × 60 × 10000
    /// 35 秒配方 → timeSpend = 21,000,000
    /// 每 tick   time += 1e8 ≫ 21,000,000     → 一个 tick 就填满
    /// </code>
    ///
    /// **一旦 speed 是 1e8，配方自己的时间就完全不起作用了。** 每 tick 结算一个周期
    /// = 60 次/秒，无论配方写 8 秒还是 35 秒。也就是说 <c>cyclesPerTick = 1</c> 对一条
    /// 35 秒的配方而言是 <b>2100 倍速</b>，不是 1 倍速。真要慢下来，只能**跳 tick**。
    ///
    /// <b>跳 tick 的做法是现成的</b>：<see cref="MegaLightPatches.Suppress"/> 把
    /// <c>time</c> 预置成 <c>-speedOverride - 1</c>，让原版那一次调用加完也够不着
    /// <c>timeSpend</c>。生物温室晚上停产走的就是这条，而且**两条计时线都压**
    /// （<c>time</c> 主产物、<c>extraTime</c> 增产剂额外产出，是两个独立的 <c>if</c>，
    /// 只压一个会漏产）。这里直接复用，不另写一份。
    ///
    /// <b>错帧是抄原版的，不是自己发明的。</b> 分频之后如果所有建筑都在同一个 tick 上结算，
    /// 那一帧的尖峰会等于「不分频」，只是平均值降下来了——而逻辑帧看的是尖峰不是平均。
    /// 原版给物流站派机用的就是错帧：<c>timeGene % droneTaskInterval != id % droneTaskInterval</c>。
    /// 这里照抄，用 <c>entityId</c> 当相位，于是同一座建筑的不同实例会均匀摊在 N 个 tick 上。
    ///
    /// <b>和供电缩放的关系：分频数是被缩放的那个量，不是周期数。</b>
    /// <c>powerScalesCycles</c> 缩周期数、地板是 1，所以 <c>cyclesPerTick</c> 一旦是 1，
    /// 它就没东西可缩了——建筑会满速跑到供电掉破 10%，然后被原版
    /// <c>if (power &lt; 0.1f) return 0;</c> 直接打死，玩家看到的是「完全不降速，然后突然全死」。
    /// 而这四座反物质建筑恰恰是最容易掉电的（视界蒸发炉一座 3 GW）。所以缺电时拉长分频数，
    /// 降的方向和原版自己那条线性一致（<c>InternalUpdate</c> IL 0576
    /// <c>time += power × speedOverride</c>），而且它还有得降。
    /// </summary>
    internal static class MegaThrottle
    {
        private struct Entry
        {
            internal int Cycles;              // 0 = 跟全局
            internal int Divider;             // <=1 = 每 tick 都跑
            internal EStarType[] BonusStars;  // null = 建在哪都一样
            internal float BonusSpeedup;      // <=1 = 无加成
        }

        /// <summary>
        /// <c>protoId</c> → 节流参数。**用数组线性扫而不是 Dictionary**：
        /// 这条在 tick 路径上，每台建筑每 tick 都要过一次，而表长只有十几；
        /// 线性扫没有哈希、没有装箱、没有分配，和 <see cref="MegaLightPatches"/> 同一套做法。
        /// </summary>
        private static int[] _protoIds = new int[0];

        private static Entry[] _entries = new Entry[0];

        /// <summary>分频数的上限。再大就不是「慢」而是「基本不动」了，且容易被误配。</summary>
        private const int MaxDivider = 3600;

        /// <summary>注册期调一次，把配置里逐建筑的两个旋钮收集起来。</summary>
        internal static void Collect()
        {
            MegaBuildingEntry[] buildings = MegaBuildingRegistry.Config?.buildings;

            if (buildings == null)
            {
                _protoIds = new int[0];
                _entries = new Entry[0];

                return;
            }

            var ids = new List<int>();
            var entries = new List<Entry>();
            var names = new List<string>();

            foreach (MegaBuildingEntry b in buildings)
            {
                if (b == null) continue;

                int cycles = b.cyclesPerTick;
                int divider = b.tickDivider;

                EStarType[] bonusStars = ParseStarTypes(b.bonusStarTypes, b.displayName);
                float speedup = b.bonusSpeedup;

                bool hasBonus = bonusStars != null && speedup > 1f;

                if (cycles <= 0 && divider <= 1 && !hasBonus) continue;   // 没配就不占表位

                if (divider > MaxDivider) divider = MaxDivider;

                ids.Add(b.itemId);
                entries.Add(new Entry
                {
                    Cycles = cycles,
                    Divider = divider,
                    BonusStars = hasBonus ? bonusStars : null,
                    BonusSpeedup = hasBonus ? speedup : 0f,
                });

                names.Add($"{b.displayName}（{(cycles > 0 ? cycles + " 周期/tick" : "周期跟全局")}"
                          + $"{(divider > 1 ? $"，{divider} tick 结算一次" : "")}"
                          + $"{(hasBonus ? $"，建在 {string.Join("/", System.Array.ConvertAll(bonusStars, s => s.ToString()))} 系提速 ×{speedup:0.##}" : "")}）");
            }

            _protoIds = ids.ToArray();
            _entries = entries.ToArray();

            // **全局分频单独报一行，开和关都报。** 它是个纯性能旋钮，产能不变，
            // 所以玩家看不出它开没开；而「开了但没生效」和「本来就没开」要是长得一样，
            // 下一次量 ns/次 就没法判断量的是哪一种状态。
            int g = GlobalDivider;

            if (g > 1)
                ProjectEdenPlugin.Log.LogInfo(
                    $"巨型建筑全局分频：G = {g}，每座建筑每 {g} 个 tick 才摸一次、轮到时跑 {g} 倍周期。"
                    + "**吞吐不变**（周期 ÷ 分频 = 基准 ÷ 每座，与 G 无关），单次产出变成 "
                    + $"{g} 倍、最长等 {g * 1000 / 60} ms。产出闸已同步放大 {g} 倍。"
                    + "它治的是单次 InternalUpdate 随「本星球台数」超线性涨的那个成本——"
                    + "实测 103 台 143 ns、1982 台 841 ns、7240 台 10959 ns（指令数完全相同，"
                    + "所以是访存/工作集，不是常数开销）。"
                    + $"进料缓冲已同步备到 {(MegaBuildingRegistry.Config?.cyclesPerTick ?? 1) * g} 个周期的量"
                    + "——**备货少于一次结算的周期数时，批量会被原料卡住而且不报错**，"
                    + "只表现为覆盖率掉、「没落在稳态」变多。");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    "巨型建筑全局分频：未启用（globalTickDivider = 1）。"
                    + "一颗星球上巨型建筑过多导致卡顿时，把它调到 2 或 4 可以按比例缩小每帧摸到的台数，产能不变。");

            // **没有活儿也要报一行。** 「一座都没配」和「这段代码压根没跑」在日志里
            // 长得一样，而这条规矩本仓库已经付过六次账。
            if (_protoIds.Length == 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "巨型建筑逐台节流：没有任何一座配了 cyclesPerTick 或 tickDivider，"
                    + "十几座全部按 megabuildings.json 顶层的 cyclesPerTick 跑。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑逐台节流：{_protoIds.Length} 座单配了速度 —— {string.Join("、", names.ToArray())}。"
                + "**注意 cyclesPerTick = 1 不是 1 倍速**：speed = 1e8 之下配方时间不起作用，"
                + "一 tick 结算一个周期就是 60 次/秒（35 秒的配方 = 2100 倍）。要真的慢下来靠 tickDivider。");
        }

        /// <summary>
        /// 星体类型名 → 枚举。写法和 <c>ores.json</c> 的 <c>placement.starTypes</c> 完全一致，
        /// 解析不出来的逐个报错并跳过（**不是整条丢掉**：写错一个还剩别的能用）。
        /// </summary>
        private static EStarType[] ParseStarTypes(string[] names, string who)
        {
            if (names == null || names.Length == 0) return null;

            var types = new List<EStarType>();

            foreach (string name in names)
            {
                if (System.Enum.IsDefined(typeof(EStarType), name ?? ""))
                {
                    types.Add((EStarType)System.Enum.Parse(typeof(EStarType), name));

                    continue;
                }

                ProjectEdenPlugin.Log.LogError(
                    $"{who} 的 bonusStarTypes 里「{name}」不是合法的星体类型，"
                    + $"合法值：{string.Join("、", System.Enum.GetNames(typeof(EStarType)))}");
            }

            return types.Count > 0 ? types.ToArray() : null;
        }

        /// <summary>表里有没有这台建筑。没有就走全局那套，一个分支都不多花。</summary>
        private static int IndexOf(PlanetFactory factory, int entityId)
        {
            if (_protoIds.Length == 0) return -1;
            if (factory == null) return -1;

            EntityData[] pool = factory.entityPool;

            if (pool == null || entityId <= 0 || entityId >= pool.Length) return -1;

            int protoId = pool[entityId].protoId;

            for (var i = 0; i < _protoIds.Length; i++)
                if (_protoIds[i] == protoId)
                    return i;

            return -1;
        }

        /// <summary>这台建筑这一 tick 的周期上限。没单配就返回传进来的全局值。</summary>
        internal static int CyclesFor(PlanetFactory factory, int entityId, int globalCycles)
        {
            int i = IndexOf(factory, entityId);

            int c = globalCycles;

            if (i >= 0 && _entries[i].Cycles > 0) c = _entries[i].Cycles;

            // **全局分频的补偿就在这一句。** 每 G 个 tick 才轮到一次，所以轮到时要跑 G 倍的周期，
            // 吞吐才和 G 无关——推导见 GlobalDivider 的注释。少了这一句，开全局分频就是
            // 直接按 G 砍产能，而那不是这条优化的目的。
            return c * GlobalDivider;
        }

        /// <summary>这一 tick 这台建筑该怎么办。</summary>
        internal enum Verdict
        {
            /// <summary>没配分频，照常跑。</summary>
            Free,

            /// <summary>这一 tick 轮不到它：压住原版那次调用。</summary>
            Hold,

            /// <summary>轮到它了：放行**一个**周期（见 <see cref="Release"/>）。</summary>
            Release,
        }

        /// <summary>
        /// 这一 tick 轮不轮得到这台建筑结算。
        ///
        /// <paramref name="powerRatio"/> 用来拉长分频数——缺电时降的是频率不是周期数，
        /// 理由见类注释。<c>power</c> 在原版里是 0..1 的供电率。
        /// </summary>
        /// <summary>
        /// 全局分频：对**所有**巨型建筑生效，和每座自己那个相乘。<c>1</c> = 关。
        ///
        /// <para><b>它是为缓存加的，不是为减产能加的，而且产能按构造不变。</b>
        /// 实测三颗星球（同一会话、同一份代码，只有每颗星球的巨型建筑数不同）：</para>
        /// <code>
        /// 70 台   →     95 ns/次   ← 693 条 IL 该有的量级
        /// 1,354 台 →   544 ns/次
        /// 4,948 台 → 15,543 ns/次  ← 台数只多 3.65 倍，单次耗时却是 28.6 倍
        /// </code>
        /// <para>单次耗时在星球之间差 <b>164 倍</b>，而代码一模一样——所以贵的不是指令，
        /// 是等内存；按每座约 500 字节估，1,354 台约 680 KB（L2 装得下）、4,948 台约 2.5 MB
        /// （掉出 L2），悬崖的位置对得上。结论：<b>该减的是「这一 tick 摸了多少台」，
        /// 不是「每台跑几个周期」</b>——后者本仓库已经压到每台每 tick 约 2 次调用了。</para>
        ///
        /// <para><b>产能不变是算出来的，不是估的。</b> 合并分频 = 每座分频 × 全局分频，
        /// 而周期数同时乘上全局分频（见 <see cref="CyclesFor"/>），于是对<b>每一座</b>都有
        /// <c>吞吐 = 周期/分频 = (基准×G)/(每座×G) = 基准/每座</c>，和 G 无关。
        /// 反物质那四座（<c>cyclesPerTick 1 / tickDivider 70</c>）也照此成立，
        /// 不会因为开了全局分频就偷偷变快。</para>
        /// </summary>
        internal static int GlobalDivider
        {
            get
            {
                int g = MegaBuildingRegistry.Config?.globalTickDivider ?? 1;

                return g < 1 ? 1 : g;
            }
        }

        internal static Verdict Decide(PlanetFactory factory, int entityId, long gameTick, float powerRatio)
        {
            int i = IndexOf(factory, entityId);

            // **全局分频要排在 i < 0 之前。** 普通巨型建筑压根不在 _entries 里
            // （那张表只收配了 cyclesPerTick 或 tickDivider 的），照老写法会在这里直接 Free，
            // 于是全局分频对它们一个都不生效——而它们正是这条优化要治的那一群。
            int divider = (i < 0 ? 1 : EffectiveDivider(i, factory, powerRatio)) * GlobalDivider;

            if (divider <= 1) return Verdict.Free;

            // **错帧**：用 entityId 当相位，同款建筑的不同实例均匀摊在 divider 个 tick 上。
            // 不错帧的话那一帧的尖峰等于没分频，而逻辑帧看的是尖峰。抄的是原版物流站
            // 派机那条 `timeGene % interval != id % interval`。
            return gameTick % divider != entityId % divider ? Verdict.Hold : Verdict.Release;
        }

        /// <summary>
        /// 这台建筑实际的分频数：配置值，先按星系加成缩短，再按供电率拉长。
        /// </summary>
        private static int EffectiveDivider(int i, PlanetFactory factory, float powerRatio)
        {
            int divider = _entries[i].Divider;

            if (divider <= 1) return 1;

            // ── 就地生产加成：建在原料产地的星系里就快 ──────────────
            //
            // **驱动的是「建在哪」，不是恒星有多重。** 黑洞系只有 1 颗行星（写死无随机），
            // 放不下整条产线也没有扩张余地，所以玩家要在「运矿出去敞开了建」和
            // 「挤在那一颗上换加成」之间分配——那才是决策。恒星质量刻意没参与：
            // 一局只有一个黑洞，挂在它上面就是抽种子不是做选择（见 MegaBuildingEntry 的注释）。
            EStarType[] bonus = _entries[i].BonusStars;

            if (bonus != null)
            {
                StarData star = factory?.planet?.star;

                if (star != null && System.Array.IndexOf(bonus, star.type) >= 0)
                {
                    var faster = (int)(divider / _entries[i].BonusSpeedup);

                    divider = faster < 1 ? 1 : faster;
                }
            }

            // 缺电就拉长间隔。照抄原版的线性：供电率减半，间隔翻倍。
            // powerRatio 低于 0.1 的时候原版自己会整台停摆，所以这里不用管下界。
            //
            // 放在加成**之后**：两者都作用在分频数上，而缺电是当下的状态、加成是位置属性，
            // 先算位置再算状态，读起来和「这台机器本来多快 → 现在电不够打几折」一致。
            if (powerRatio > 0.01f && powerRatio < 1f)
            {
                var scaled = (int)(divider / powerRatio);

                divider = scaled > MaxDivider ? MaxDivider : scaled;
            }

            return divider;
        }

        /// <summary>
        /// 这台建筑的分频数，供显示用（参考速率 / 理论产能）。没配分频返回 1。
        /// 供电率按满载算——面板报的是「能跑多快」，不是「此刻多快」。
        /// </summary>
        internal static int DividerFor(PlanetFactory factory, int entityId)
        {
            int i = IndexOf(factory, entityId);

            // 面板那一侧也得乘上全局分频，否则参考速率会按「不分频」报，
            // 而那正是本仓库记过的「面板报一个引擎不允许的数」。
            // 注意它和 CyclesFor 的 ×G 是一对：面板算的是 周期/分频，两处都乘 G 才抵消掉。
            return (i < 0 ? 1 : EffectiveDivider(i, factory, 1f)) * GlobalDivider;
        }

        /// <summary>
        /// 轮到这台建筑的那一 tick：把**已经付过料的那一个周期**放出来结算。
        ///
        /// <para><b>为什么非要显式放行——只压不放是漏掉的那一半，整条产线因此一件都不出。</b></para>
        /// 原版 <c>AssemblerComponent.InternalUpdate</c> 是**上一次攒、这一次结**的流水线：
        ///
        /// <code>
        /// 0101: if (time &lt; timeSpend) goto 0383;   // ← 结算，用的是**上一次调用**攒的 time
        /// 0383: if (replicating) goto 0555;        // 否则扣料、replicating = true
        /// 055D: if (time &gt;= timeSpend) 不再累加
        /// 056F: time += power × speedOverride;      // ← 攒，给**下一次调用**用
        /// </code>
        ///
        /// 所以「攒满」和「结算」永远落在相邻两次调用上。而分频把相邻那一次变成了
        /// Hold tick，<see cref="MegaLightPatches.Suppress"/> 在原版调用之前把 <c>time</c>
        /// 写成大负数——**刚攒满的那一个周期就这么被抹掉了**，每一次都如此。
        /// 净效果：第一 tick 扣掉一整份料、<c>replicating</c> 置真，此后永远结算不了，
        /// 玩家看到的正是「料喂进去了，一件产物都没有」。
        ///
        /// <para><b>判据是 <c>replicating</c>，而它恰好就是「这一份料付过了」。</b></para>
        /// 原版只在 IL 054E 把它置真，而那一句紧跟在扣料（含够不够的检查）之后；
        /// 结算时 IL 0127 又把它置假。所以 <c>replicating == true</c> ⟺
        /// 「一整份原料已经扣掉、对应的产物还没发出来」。只在这个前提下强制
        /// <c>time = timeSpend</c>，**结构上不可能凭空造物**——顶多是把一个
        /// 已经付过账的周期提前兑现。少了这道判据就会变成无料生产。
        ///
        /// <para><b>增产剂那条线同样要放，而且「一次结算给一份额外产出」就是原版在
        /// 万倍速下的行为，不是我们加的倍率。</b></para>
        /// <c>extraTimeSpend = TimeSpend × 100000</c>、
        /// <c>extraSpeed = speed × incTableMilli × 10</c>，在 <c>speed = 1e8</c> 下
        /// 后者永远大于前者（最慢的 35 秒配方也是 2.5e8 &gt; 2.1e8），
        /// 所以不分频的巨型建筑本来就是**每结算一个周期就出一份额外产出**。
        /// <c>extraSpeed &gt; 0</c> 是原版自己「这一份料喷过增产剂」的判据
        /// （IL 034D 结算时清零，扣料时按 <c>incServed</c> 重算），照抄它。
        /// </summary>
        internal static void Release(PlanetFactory factory, ref AssemblerComponent component)
        {
            RecipeExecuteData data = component.recipeExecuteData;

            if (data == null) return;

            RewindExtra(ref component);

            // 没有付过料的周期就没有可放的——这时候什么都不做，等下一个周期
            // （原版这一次调用会去扣料并置 replicating，于是下一次轮到它时就有得放了）
            if (!component.replicating)
            {
                ReportOnce(factory, component.entityId, false, 0);

                return;
            }

            int before = component.time;

            if (component.time < data.timeSpend) component.time = data.timeSpend;

            // 增产进度按**一个周期的份额**往前推，不是直接推到门槛。
            // 原版每累加一次：time 涨 speedOverride、extraTime 涨 extraSpeed，
            // 而一个周期只花掉 timeSpend 的 time，所以一个周期分到的增产进度是
            // extraSpeed × timeSpend / speedOverride。代进原版自己的两个定义
            // （extraSpeed = speed × m × 10，extraTimeSpend = timeSpend × 10）正好是
            // **每周期 m 份额外产出**，m 就是 incTableMilli —— 也就是增产剂标称的那个百分比。
            //
            // **直接推到门槛是错的，而且错得不小：**那样每个周期都出一份额外产出，
            // 是原版同配方同喷涂下的 4 倍（离线复现：1.00 对 0.25）。
            if (component.extraSpeed > 0 && component.speedOverride > 0)
            {
                long share = (long)component.extraSpeed * data.timeSpend / component.speedOverride;

                long now = component.extraTime + share;

                // 夹住：喷涂档位在两次调用之间变了的话，倒回的量和加上的量会对不齐，
                // 夹一下就不会越滚越远（上界是门槛，多出来的那一份本来也要被结算掉）
                if (now > data.extraTimeSpend) now = data.extraTimeSpend;

                component.extraTime = (int)now;
            }

            ReportOnce(factory, component.entityId, true, before);
        }

        /// <summary>
        /// 把增产计时器倒回「真实进度」。
        ///
        /// <b>原版在每次调用的底部（IL 0586）无条件加一个 <c>extraSpeed</c></b>，
        /// 那一笔是给「下一次调用」用的，不是这一个周期该得的。两条钩子都要先把它减回去，
        /// 否则 <see cref="Release"/> 看到的是「进度 + 一整个 extraSpeed」，
        /// 当场就越过门槛——离线复现里比例会是 1.00 而不是原版的 0.25。
        ///
        /// 没有在制周期（<c>replicating == false</c>）时没有进度可留，压到负数即可，
        /// 那正是 <see cref="MegaLightPatches.Suppress"/> 对这条线的做法。
        /// </summary>
        private static void RewindExtra(ref AssemblerComponent component)
        {
            if (component.replicating && component.extraSpeed > 0)
                component.extraTime -= component.extraSpeed;
            else if (component.extraSpeed > 0)
                component.extraTime = -component.extraSpeed - 1;
            else
                // **没喷增产剂时什么都不写，写 0 也不写 −1。**
                // 那个 `-extraSpeed - 1` 的形状是为了「再加一次 extraSpeed 也够不着门槛」，
                // 而 extraSpeed == 0 时加的是 0、0 本来就够不着，所以哨兵毫无作用——
                // 它只是往一个**存档字段**里塞了个永远不会被清掉的 −1
                // （原版推进它的唯一一处 IL 0586 乘的正是 extraSpeed）。
                // 代价不在这里显形：MegaBatchSettle.CanBatch 会因此把这台建筑永久踢出
                // 批量结算，一局开过分频就够让整个存档慢几十倍。
                component.extraTime = 0;
        }

        /// <summary>
        /// 这一 tick 轮不到它：压住原版紧随其后的那次调用。
        ///
        /// <c>time</c> 的压法和 <see cref="MegaLightPatches.Suppress"/> 一样——预置成
        /// 「加完也够不着门槛」。<c>extraTime</c> 则**不能清零**：清了的话增产进度永远
        /// 攒不到门槛，喷了增产剂等于白喷。见 <see cref="RewindExtra"/>。
        /// </summary>
        internal static void Hold(ref AssemblerComponent component)
        {
            component.time = -component.speedOverride - 1;

            RewindExtra(ref component);
        }

        /// <summary>
        /// 每种建筑各报一次「它到底放行了没有」。
        ///
        /// <para><b>状态行回答「接上了没有」，事件行回答「它决定了什么」，一个替不了另一个</b>
        /// ——本仓库为这条付过七次账。<see cref="Collect"/> 那行只说明配置读到了，
        /// 说明不了运行时有没有真的放出周期；而这一族建筑上一版正是「每一行日志都正常、
        /// 一件产物都没有」。</para>
        ///
        /// <b>一次 = 每种建筑一次，不是每局一次。</b> 分频的建筑有四种，只报第一种的话
        /// 剩下三种是好是坏都看不出来（<c>MegaStationPatches</c> 的储物格转储栽过同一条）。
        /// 组装机 tick 跑在 <c>_assembler_parallel</c> 上，所以用 <c>ConcurrentDictionary</c> 领号。
        /// </summary>
        private static void ReportOnce(PlanetFactory factory, int entityId, bool released, int timeBefore)
        {
            EntityData[] pool = factory?.entityPool;

            if (pool == null || entityId <= 0 || entityId >= pool.Length) return;

            int protoId = pool[entityId].protoId;

            if (!_reported.TryAdd((protoId, released), 0)) return;

            string name = LDB.items.Select(protoId)?.name ?? protoId.ToString();

            if (released)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"巨型建筑逐台节流·放行：{name} 第一次放出一个周期（放行前 time = {timeBefore}）。"
                    + "原版是「上一次攒、这一次结」，而分频会让相邻那一次变成压制 tick —— "
                    + "不显式放行的话攒满的周期每次都被抹掉，表现为「料喂进去了、一件产物都没有」。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑逐台节流·空放：{name} 轮到它了，但机器里没有待结算的周期"
                + "（replicating = false）。**这多半是正常的**：刚建好、刚换配方、"
                + "或者原料不够扣不出一整份，都会是这一行。原料齐了之后应当出现「第一次放出一个周期」。");
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, bool), byte> _reported =
            new System.Collections.Concurrent.ConcurrentDictionary<(int, bool), byte>();
    }
}
