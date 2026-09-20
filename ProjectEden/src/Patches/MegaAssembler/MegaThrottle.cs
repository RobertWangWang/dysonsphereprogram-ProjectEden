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

        /// <summary>
        /// 这一 tick 轮不轮得到这台建筑结算。
        ///
        /// <paramref name="powerRatio"/> 用来拉长分频数——缺电时降的是频率不是周期数，
        /// 理由见类注释。<c>power</c> 在原版里是 0..1 的供电率。
        /// </summary>
        internal static bool Skip(PlanetFactory factory, int entityId, long gameTick, float powerRatio)
        {
            int i = IndexOf(factory, entityId);

            if (i < 0) return false;

            int divider = _entries[i].Divider;

            if (divider <= 1) return false;

            // ── 就地生产加成：建在原料产地的星系里就快 ──────────────
            //
            // **驱动的是「建在哪」，不是恒星有多重。** 黑洞系只有 1 颗行星（写死无随机），
            // 放不下整条产线也没有扩张余地，所以玩家要在「运矿出去敞开了建」和
            // 「挤在那一颗上换加成」之间分配——那才是决策。恒星质量刻意没参与：
            // 一局只有一个黑洞，挂在它上面就是抽种子不是做选择（见 MegaBuildingEntry 的注释）。
            EStarType[] bonus = _entries[i].BonusStars;

            if (bonus != null)
            {
                StarData star = factory.planet?.star;

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

            // **错帧**：用 entityId 当相位，同款建筑的不同实例均匀摊在 divider 个 tick 上。
            // 不错帧的话那一帧的尖峰等于没分频，而逻辑帧看的是尖峰。抄的是原版物流站
            // 派机那条 `timeGene % interval != id % interval`。
            return gameTick % divider != entityId % divider;
        }
    }
}
