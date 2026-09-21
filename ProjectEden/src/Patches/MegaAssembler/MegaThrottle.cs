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

            if (i < 0) return globalCycles;

            int c = _entries[i].Cycles;

            return c > 0 ? c : globalCycles;
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
        internal static Verdict Decide(PlanetFactory factory, int entityId, long gameTick, float powerRatio)
        {
            int i = IndexOf(factory, entityId);

            if (i < 0) return Verdict.Free;

            int divider = EffectiveDivider(i, factory, powerRatio);

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

            return i < 0 ? 1 : EffectiveDivider(i, factory, 1f);
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
            else
                component.extraTime = -component.extraSpeed - 1;
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
