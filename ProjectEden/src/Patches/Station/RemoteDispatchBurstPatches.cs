// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 星际物流运输船：一次派船评估放出多艘，而不是一艘。
    ///
    /// <b>先纠正 CLAUDE.md 里两条没量过的说法。</b> 那一节写着
    /// <c>DetermineDispatch</c>「是没有回跳的直线代码」、「一次调用至多放出一艘船」，
    /// 并据此给出的杠杆是「用可重入保护的后置把它多调 N−1 次」。
    /// 实测（1250 条 IL）：
    ///
    /// <list type="bullet">
    /// <item><b>有回跳，两条</b>：<c>0D0C bne.un → 0109</c>（外层配对环）和
    ///       <c>0B65 bne.un → 0873</c>（需求侧的内层环）。</item>
    /// <item><b>方法体里一条 <c>idleShipCount</c> / <c>workShipCount</c> 的 <c>stfld</c> 都没有</b>——
    ///       放船是 <c>DispatchSupplyShip</c> / <c>DispatchDemandShip</c> 里做的。</item>
    /// </list>
    ///
    /// 「一次至多一艘」这个<b>效果</b>是对的，机制不是。真正的形状和行星内那边
    /// <b>一模一样</b>：
    ///
    /// <code>
    /// 0078  if (段内配对数 &lt;= 0) return;           // 唯一的提前返回，游标不动
    /// 0109  do {                                   // ← 环扫描的头
    ///           ref pair = remotePairs[游标];
    ///           ...
    ///           // 这一对定下来了 → br     0D11    ← **跳出环**
    ///           // 内层扫到并放船了 → brtrue 0D11  ← 跳出环
    ///           // 电力比例 &lt;= 0.1 → brfalse 0D11 ← 跳出环（**不动**）
    ///           // 这一对没活儿 →
    /// 0CD8      游标++ ; 到段尾就回绕
    /// 0D0C  } while (进入时的游标 != 游标);
    /// 0D11  游标++ ; 到段尾就回绕
    /// 0D3A  ret
    /// </code>
    ///
    /// 两段「游标 +1 带回绕」一模一样，一段给续扫、一段给跳出——
    /// 和 <see cref="LocalDispatchBurstPatches"/> 的 <c>1175</c> / <c>11A5</c> 同一个模子。
    /// <b>所以改法也同一个</b>：把「定下来了 → 跳出」改成「定下来了 → 问一句还能不能再放」，
    /// 能就跳到 <c>0CD8</c>，那正是原版自己的续扫路径。
    /// 回跳测试 <c>进入时的游标 != 游标</c> 原封不动，所以<b>一次调用仍然最多走一圈环</b>。
    ///
    /// <b>为什么不用 CLAUDE.md 说的「多调 N−1 次」。</b> 那一招要求游标在每条提前返回上
    /// 都前进过，这里成立（<c>0078</c> 那条早退根本没进环，游标不动也对）。
    /// 但它每次都要重走方法开头：<c>_tmp_iter_remote++</c>、重算段边界、
    /// 重新取 <c>remotePairOffsets</c>，还要重新过一遍 <c>Monitor</c>。
    /// 改三条分支只多走「环里下一格」，便宜得多，而且不会和外面
    /// <c>GalacticTransport.GameTick</c> 的六段优先级轮转互相干扰。
    ///
    /// <b>越界风险在这里不存在，而这正是和行星内那边最大的不同。</b>
    /// 行星内那边 <c>idleDroneCount &lt;= 0</c> 的闸在<b>循环外</b>，循环体里从不复查，
    /// 所以连发不复查就是 <c>workDroneDatas</c> 越界。星际这边原版自己就复查了两道：
    ///
    /// <list type="bullet">
    /// <item>环<b>体内</b>的 <c>03F5 / 07F9</c>：<c>if (idleShipCount == 0) …</c> 以及
    ///       <c>if (energy &gt; 6000000)</c>——每一格配对都重新过。</item>
    /// <item><c>DispatchSupplyShip</c> @0017 / <c>DispatchDemandShip</c> @0007 开头就是
    ///       <c>QueryIdleShip(nextShipIndex)</c>，返回 &lt; 0 直接 <c>return false</c>。
    ///       而 <c>QueryIdleShip</c> 扫的是 <c>idleShipIndices</c> 这个 UInt64 位图，
    ///       它和 <c>idleShipCount</c> 由 <c>IdleShipGetToWork</c> 同步维护——
    ///       所以「有闲置船」和「<c>workShipDatas</c> 有空位」是同一件事。</item>
    /// </list>
    ///
    /// <see cref="BurstContinue"/> 仍然把那两道闸<b>逐字抄进来</b>（<c>idleShipCount &gt; 0</c>、
    /// <c>energy &gt; 6000000</c>）：不是为了防越界，是为了<b>早点停</b>——没船了还接着扫环
    /// 是纯浪费，而抄原版的判据不会和它在边界上分家。
    ///
    /// <b>三处改写里有一处不是严格的「放出去了」，这一点要说准。</b>
    /// <c>0BD6</c>（需求侧）和 <c>0494</c>（供给侧）前面那个 <c>V_43</c> / <c>V_35</c>
    /// 在「电够、试过了」时就置 1，<b>不管 <c>Dispatch*Ship</c> 返回真假</b>
    /// （IL <c>045E</c> / <c>0BA0</c> 是两条路的汇合点）。也就是说原版在「这一对
    /// 电够但没船」时也会跳出。那种情况下继续扫是白扫一格——但 <see cref="BurstContinue"/>
    /// 第一条查的就是 <c>idleShipCount &gt; 0</c>，而「没船」正是 <c>Dispatch*Ship</c> 失败的
    /// <b>唯一</b>原因，所以那一格根本不会被扫到。两边判据一致，不需要额外插局部变量去
    /// 抓返回值。
    ///
    /// <b>线程。</b> 调用链是 <c>GameLogic.OnGameLogicFrame → GalacticTransportGameTick
    /// → GalacticTransport.GameTick</c>，一重对 <c>stationPool</c> 的串行循环，
    /// <b>没有 _Parallel 变体</b>（对比行星那边的 <c>FactoryTransportGameTick_Parallel</c>）。
    /// 所以额度用普通静态 int 就够，不需要 <c>[ThreadStatic]</c>。
    /// 这条是量出来的，不是假设的——写在这里是为了下次有人想加并行时先看见它。
    ///
    /// <b>还有第三个去处：同一条线连发。</b> 只改上面那三处跳转的第一版**每放一艘就往下一对走**，
    /// 于是一次评估的上限变成「这一圈里有几对有活儿」——进游戏实测 **2.28×，而额度 10 一次都没用满**
    /// （被额度打断 0 次）。也就是说瓶颈已经不在额度上，而在「同一条线一次只出一艘」。
    /// 现在 <see cref="BurstMode"/> 有三个返回值：同一对再来一艘（<b>跳回环头，游标不动</b>）、
    /// 换下一对（原版续扫口）、停（出口）。
    ///
    /// 重试这条路**绕过了 `进入时的游标 != 游标` 那句环界测试**，所以兜底换成了
    /// 额度和每对上限——而每放一艘都必过 <see cref="BurstMode"/>，所以仍然是有界的。
    /// 它也不会空转：`Dispatch*Ship` 各自只有一条 `return false`，只被
    /// `QueryIdleShip(...) &lt; 0` 跳到，而那正是护栏第二条查的；货的那一侧由原版自己的
    /// 记账收敛（见 <see cref="SameRouteDefault"/>）。
    ///
    /// <b>另一根杠杆没有动，而且它比这条更猛。</b> 原版把配对分成六段，
    /// <c>DetermineFramingDispatchTime(tick, t)</c>：t=1 → <c>tick%10</c>（6 次/秒）、
    /// t=2,3 → <c>%30</c>（2 次/秒）、其余 → <c>%60</c>（1 次/秒）。
    /// 而 <c>GalacticTransport.GameTick</c> 的第三个调用点（IL 0146–0167）是
    /// <c>V_9 == 0 且 routePriority == 1</c>——<c>ERemoteRoutePriority.Ignore = 1</c>，
    /// 就是默认值。**所以默认站点一秒才被评估一次**，这才是「每 1 秒一艘」里的那个「1 秒」。
    /// 把取货那一端的站点设成「优先」(<c>Prioritize = 2</c>) 会把它的配对挪进 t=1 那一段，
    /// 直接 6 倍——<b>不用改一行代码</b>，所以这条仍然不打补丁，只在日志里报出来。
    /// 两条杠杆是乘起来的：优先 + 连发 = 6 × 本额度。
    /// </summary>
    [HarmonyPatch]
    internal static class RemoteDispatchBurstPatches
    {
        /// <summary>
        /// 原版环体内那道能量地板，IL 03F5 / 07F9 的字面量（<c>energy &gt; 6000000</c>）。
        /// <b>抄的，不是挑的。</b>每一趟按距离算的精确成本由环体内原版自己的
        /// <c>blt</c>（0424 / 0A9D / 0B75）再把关一次，那几条保持原样。
        /// </summary>
        private const long EnergyFloor = 6000000L;

        /// <summary>
        /// 一次派船评估最多放几艘。
        ///
        /// <b>这里原本是 64，而那个 64 是从 <c>idleShipIndices</c> 是 <c>UInt64</c>、
        /// 按 <c>1L &lt;&lt; (index &amp; 63)</c> 索引推出来的——1.12.14 起那条推导的前提没了</b>
        /// （<see cref="StationShipBank"/> 把八个翻位方法整体换成了旁挂位图）。留着它的后果
        /// 正是本仓库最讨厌的那一种：配置里写 256，日志里读起来像生效，实际被静默夹回 64。
        ///
        /// 现在这个数和 <c>MachineRegistry</c> 里泊位的那道夹子同源、同理由：**夹的是内存和
        /// 停泊环，不是位宽**。真正让连发停下来的从来不是它，而是环体内原版自己的两道闸
        /// （<c>idleShipCount &gt; 0</c> 和能量地板），所以额度配大了只是够不到，不会出事。
        /// </summary>
        private const int HardCap = 4096;

        /// <summary>
        /// 同一条运输线（同一对供需配对）一次评估最多连发几艘。1 = 逐对轮转，
        /// 也就是 1.10.7 的行为。
        ///
        /// <b>这不是安全护栏，是公平性旋钮——安全由原版自己的记账保证。</b>
        /// 每一次派船都当场把两端扣掉（<c>DispatchSupplyShip</c> @034A
        /// <c>storage[supplyIndex].count -= carryCnt</c> 加需求端 <c>remoteOrder += carryCnt</c>；
        /// <c>DispatchDemandShip</c> 两端 <c>remoteOrder</c> 一加一减），而
        /// <c>remoteDemandCount = max − (count + remoteOrder)</c>。所以同一对再看一次读到的是
        /// 已经扣过的数，扣光了就走 @0351/@0359 那两道门跳 <c>0499 → 04A9 br → 0CD8</c>
        /// （续扫口），**原版自己把游标推到下一对**。重试构造上不会空转。
        ///
        /// 那为什么还要这个上限：**本 mod 把物流站格容量抬到了 1000 万**，所以
        /// 「需求扣光」在这个存档里基本不会发生——一条线能把整次评估的额度吃干净，
        /// 后面的配对这一轮一艘都轮不上。游标本身仍然保证跨评估的轮转
        /// （额度用尽时走的是出口，出口照样 <c>游标++</c>），但一次评估内的分散度就没了。
        /// 默认 4 配额度 10，保证一次评估至少照顾到 ⌈10/4⌉ = **3 条不同的线**。
        ///
        /// 又一次「本仓库为某个功能加的规则，悄悄限制了后来加的另一个功能」——
        /// 和催化剂槽位撞 <c>SyncStorageLayout</c>、钻头槽位撞 <c>StorageCount</c> 同形。
        /// </summary>
        private const int SameRouteDefault = 4;

        /// <summary>
        /// 这一次 <c>DetermineDispatch</c> 还能再放几艘。
        ///
        /// 普通静态即可：见类注释，星际物流的 tick 是主线程串行的。
        /// 前置每次调用都会重置它。
        /// </summary>
        private static int _left;

        private static int _max = -1;

        /// <summary>同一对配对一次评估最多连发几艘。解析一次。</summary>
        private static int _sameRouteMax = -1;

        /// <summary>这一对已经连发了几艘。</summary>
        private static int _sameRoute;

        /// <summary>上一次看到的游标；和现在不一样就说明原版把配对推走了，计数要清。</summary>
        private static int _lastCursor = -1;

        /// <summary>放出去的总艘次。每一艘恰好调一次 <see cref="BurstContinue"/>。</summary>
        private static long _ships;

        /// <summary>
        /// 「至少放出过一艘」的评估次数 —— <b>这就是原版会放出的艘次</b>，
        /// 因为原版放一艘就跳出环。判据是 <c>_left == _max</c>：
        /// 前置把额度重置成 max，只有这一次的<b>第一艘</b>看到的 <c>_left</c> 还没被扣过。
        ///
        /// 行星内那条补丁第一版把倍率算成了 <c>1 + 允许继续的次数 / 总艘次</c>，
        /// 而只要额度不吃紧那个比值恒等于 ≈2.00，<b>无论真实倍率是多少</b>。
        /// 读数不随被测量的东西变化，就等于没在测——这里从一开始就按这个计数器算。
        /// </summary>
        private static long _rounds;

        /// <summary>连发被「额度用完」终止的次数。小 = 调大配置也没用。</summary>
        private static long _stopBudget;

        /// <summary>连发被「没有闲置运输船了」终止的次数。</summary>
        private static long _stopIdle;

        /// <summary>连发被「电不够」终止的次数。</summary>
        private static long _stopEnergy;

        /// <summary>其中有多少艘是「同一条线接着再来一艘」放出去的。</summary>
        private static long _sameRouteShips;

        /// <summary>同一条线撞到每对上限、被迫换下一对的次数。</summary>
        private static long _stopSameRoute;

        /// <summary>配置值，解析一次。0 或缺配置 = 1 = 原版行为。</summary>
        private static int Resolve()
        {
            int n = ProjectEdenPlugin.StationsConfig?.remoteShipsPerDispatch ?? 0;

            if (n <= 0) return 1;

            return n > HardCap ? HardCap : n;
        }

        /// <summary>
        /// 同一条线一次评估最多几艘。**0 或缺配置 = 1 = 逐对轮转**（1.10.7 的行为），
        /// 和 <see cref="Resolve"/> 那条「0 或 1 = 原版」的约定保持一致，
        /// 而且**不可能超过总额度**——超了就是个读起来像生效、实际不生效的数。
        ///
        /// 随包发出去的那份 <c>stations.json</c> 写的是 <see cref="SameRouteDefault"/>；
        /// 这里不拿它当兜底，否则「配置里显式写 0」和「配置里没这一项」会得到不同结果，
        /// 而这两种情况在日志里长得一模一样。
        /// </summary>
        private static int ResolveSameRoute(int max)
        {
            int n = ProjectEdenPlugin.StationsConfig?.remoteSameRouteMax ?? 0;

            if (n < 1) n = 1;

            return n > max ? max : n;
        }

        /// <summary>
        /// 重置这一次评估的额度和「这一对连发了几艘」。
        /// 无参前置，只写三个静态 int。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.DetermineDispatch))]
        private static void DetermineDispatch_Prefix()
        {
            int max = _max;

            if (max < 0)
            {
                max = _max = Resolve();
                _sameRouteMax = ResolveSameRoute(max);
            }

            _left = max;
            _sameRoute = 0;
            _lastCursor = -1;
        }

        /// <summary>
        /// 「刚定下一对、放出一艘，接下来怎么走」。三个去处，见下面三个常量。
        ///
        /// 前三条闸里后两条是原版环体内那两道（IL 03F5 / 07F9）的逐字副本。
        /// 见类注释：这里它们<b>不是</b>防越界用的——原版自己在环体内和
        /// <c>Dispatch*Ship</c> 开头各复查一次——而是为了没船 / 没电时早点停。
        ///
        /// <b><see cref="ModeRetry"/> 为什么不会空转，是量出来的，不是设计出来的。</b>
        /// <c>DispatchSupplyShip</c> / <c>DispatchDemandShip</c> 各自**只有一条
        /// <c>return false</c>**，而且只被开头那句 <c>QueryIdleShip(...) &lt; 0</c> 跳到
        /// （`001F blt` / `000F blt`，全方法再无第二个来源）——也就是说
        /// **「没有闲置船」是它唯一的失败原因**，而那正是上面第二道闸查的东西。
        /// 至于「还有没有货」：每一次派船当场就把两端扣掉，而
        /// <c>remoteDemandCount = max − (count + remoteOrder)</c>，所以重试读到的是扣过的数；
        /// 扣光了走 @0351/@0359 两道门跳 `0499 → 04A9 br → 0CD8`，
        /// **原版自己把游标推到下一对**。
        /// </summary>
        /// <summary>
        /// 停：走出口，游标 +1 后返回。
        /// <b>这三个值就是转译器里那条 <c>switch</c> 的下标，改了要一起改。</b>
        /// </summary>
        private const int ModeStop = 0;

        /// <summary>换下一对：走原版的续扫口（游标 +1、回绕、再过一次环界测试）。</summary>
        private const int ModeNext = 1;

        /// <summary>同一对再来一艘：直接跳回环头，**不动游标**。</summary>
        private const int ModeRetry = 2;

        private static int BurstMode(StationComponent station, int priorityIndex)
        {
            int left = _left;

            _ships++;

            // 这一次评估的第一艘 —— 也就是原版会放出、然后跳出环的那一艘。
            if (left == _max) _rounds++;

            // 刚刚那一艘就是这次额度里的一艘，所以 <=1 意味着用完了。
            if (left <= 1)
            {
                _stopBudget++;

                return ModeStop;
            }

            _left = left - 1;

            if (station == null) return ModeStop;

            if (station.idleShipCount <= 0)
            {
                _stopIdle++;

                return ModeStop;
            }

            if (station.energy <= EnergyFloor)
            {
                _stopEnergy++;

                return ModeStop;
            }

            if (_sameRouteMax <= 1) return ModeNext;

            // 原版可能在我们不知情的时候把游标推走了（「这一对没活儿」那条路不经过这里），
            // 所以判据是游标本身，不是我们自己的计数。
            int[] cursors = station.remotePairProcesses;

            int cursor = cursors != null && priorityIndex >= 0 && priorityIndex < cursors.Length
                ? cursors[priorityIndex]
                : -1;

            if (cursor != _lastCursor)
            {
                _lastCursor = cursor;
                _sameRoute = 0;
            }

            _sameRoute++;

            if (_sameRoute < _sameRouteMax)
            {
                _sameRouteShips++;

                return ModeRetry;
            }

            // 段长为 1 时游标永远是 0，光靠游标比较认不出「换过一对」，所以这里也显式清。
            _sameRoute = 0;
            _stopSameRoute++;

            return ModeNext;
        }

        /// <summary>
        /// 把三处「定下来了 → 跳出环」改成「定下来了 → 问一句」。
        ///
        /// <b>定位靠形状，不靠偏移</b>，而且这里比行星内那边多一步：
        /// 「游标 +1 带回绕」的锚点在这个方法里有 <b>6 处</b>（不是 2 处），
        /// 所以不能像那边一样按出现顺序取。判据改成三条，每条都是可验证的事实：
        ///
        /// <list type="number">
        /// <item><b>出口</b> = 唯一一处「往后 24 条指令内先遇到 <c>ret</c>、
        ///       而不是先遇到回跳」的锚点。实测唯一命中 <c>0D11</c>。</item>
        /// <item><b>续扫口</b> = 下标小于出口的锚点里最大的那个（<c>0CD8</c>）。</item>
        /// <item><b>断言</b>：这两者之间恰好有 <b>1</b> 条回跳，且它跳到续扫口<b>之前</b>
        ///       ——那就是环头 <c>0109</c>。</item>
        /// </list>
        ///
        /// 然后按分支种类分类跳到出口的那几条：<c>br</c> ×2（这一对定下来了）、
        /// <c>brtrue</c> ×1（内层扫描放船了）要改；<c>brfalse</c> ×1（电力比例 ≤ 0.1）
        /// <b>不动</b>。以上整套规则在打补丁之前先对着发行版程序集离线跑过一遍，
        /// 结果是 6 个锚点 / 出口唯一 / 1 条回跳 / 2-1-1-0，三个改写点都在
        /// 任何 try-handler 区间之外。数目对不上就整条不改、大声报错。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.DetermineDispatch))]
        private static IEnumerable<CodeInstruction> DetermineDispatch_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);

            FieldInfo cursor = AccessTools.Field(typeof(StationComponent), "remotePairProcesses");

            // 先解析、判空、再建匹配器。解析不到就发 call null，而那会在 Harmony 的
            // 写出阶段抛 ArgumentNullException，堆栈指向 Harmony 而不是这一行。
            MethodInfo cont = AccessTools.Method(
                typeof(RemoteDispatchBurstPatches), nameof(BurstMode));

            if (cursor == null || cont == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "星际连发派船：解析不到 StationComponent.remotePairProcesses 或 BurstMode，"
                    + "整条转译放弃，派船保持原版的一次一艘。");

                return code;
            }

            // priorityIndex 是第 4 个形参（实例方法，ldarg.s 4）。**按名字核对一次**，
            // 而不是信下标——游戏更新调换参数不会报错，只会让「同一条线连发」
            // 拿着别的数去查游标，然后静默地永远认不出换过配对。
            ParameterInfo[] ps = original?.GetParameters();

            if (ps == null || ps.Length < 4 || ps[3].Name != "priorityIndex")
            {
                ProjectEdenPlugin.Log.LogError(
                    "星际连发派船：DetermineDispatch 的第 4 个形参不叫 priorityIndex（实际是 "
                    + (ps != null && ps.Length >= 4 ? ps[3].Name : "参数表太短")
                    + "），原版签名变了。整条转译放弃，派船保持原版的一次一艘。");

                return code;
            }

            // 标签 → 下标，用来判断一条分支是往前跳还是往后跳。
            var labelAt = new Dictionary<Label, int>();

            for (var i = 0; i < code.Count; i++)
                foreach (Label lab in code[i].labels)
                    labelAt[lab] = i;

            // 锚点：ldarg.0 ; ldfld remotePairProcesses ; ldarg.s priorityIndex ;
            //       ldelema ; dup ; ldind.i4 ; ldc.i4.1 ; add ; stind.i4
            var anchors = new List<int>();

            for (var i = 0; i + 8 < code.Count; i++)
                if (code[i].opcode == OpCodes.Ldarg_0
                 && code[i + 1].opcode == OpCodes.Ldfld && cursor.Equals(code[i + 1].operand)
                 && code[i + 3].opcode == OpCodes.Ldelema
                 && code[i + 4].opcode == OpCodes.Dup
                 && code[i + 5].opcode == OpCodes.Ldind_I4
                 && code[i + 6].opcode == OpCodes.Ldc_I4_1
                 && code[i + 7].opcode == OpCodes.Add
                 && code[i + 8].opcode == OpCodes.Stind_I4)
                    anchors.Add(i);

            if (anchors.Count < 2)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"星际连发派船：「游标 +1」的锚点只找到 {anchors.Count} 处（至少要 2 处）——"
                    + "原版 DetermineDispatch 的形状变了。整条转译放弃，派船保持原版的一次一艘。");

                return code;
            }

            // 出口 = 往后 24 条内先遇到 ret（而不是先遇到回跳）的那个锚点。
            var exits = new List<int>();

            foreach (int a in anchors)
                for (int j = a; j < a + 24 && j < code.Count; j++)
                {
                    if (code[j].opcode == OpCodes.Ret)
                    {
                        exits.Add(a);

                        break;
                    }

                    if (code[j].operand is Label back
                     && labelAt.TryGetValue(back, out int t) && t < a)
                        break;
                }

            if (exits.Count != 1)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"星际连发派船：「后面紧跟 ret 的游标块」应当唯一，实际 {exits.Count} 处。"
                    + "整条转译放弃，派船保持原版的一次一艘。");

                return code;
            }

            int iExit = exits[0];
            var iContinue = -1;

            foreach (int a in anchors)
                if (a < iExit && a > iContinue)
                    iContinue = a;

            if (iContinue < 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    "星际连发派船：出口游标块前面找不到续扫游标块。"
                    + "整条转译放弃，派船保持原版的一次一艘。");

                return code;
            }

            // 断言：续扫口和出口之间恰好一条回跳，且它跳到续扫口之前（环头）。
            // 那条回跳的**目标**就是环头，「同一条线连发」要跳的就是它。
            var backward = 0;
            var iLoopHead = -1;

            for (int j = iContinue; j < iExit; j++)
                if (code[j].operand is Label lab
                 && labelAt.TryGetValue(lab, out int t) && t < j)
                {
                    backward++;

                    if (t < iContinue) iLoopHead = t;
                }

            if (backward != 1 || iLoopHead < 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"星际连发派船：续扫口和出口之间应当恰好有 1 条跳回环头的回跳，"
                    + $"实际 {backward} 条、环头下标 {iLoopHead}。"
                    + "整条转译放弃，派船保持原版的一次一艘。");

                return code;
            }

            // 出口那条指令身上挂着的标签，就是各处跳出用的标签。
            var exitLabels = new HashSet<Label>(code[iExit].labels);

            if (exitLabels.Count == 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    "星际连发派船：出口那条指令身上一个标签都没有，说明锚点认错了。"
                    + "整条转译放弃，派船保持原版的一次一艘。");

                return code;
            }

            Label lContinue = generator.DefineLabel();
            code[iContinue].labels.Add(lContinue);

            Label lExit = generator.DefineLabel();
            code[iExit].labels.Add(lExit);

            // 环头：同一条线再来一艘就跳这里，**不经过续扫口，所以游标不动**。
            // 回跳测试也被绕过了，环界不再兜底——兜底的是额度和每对上限，
            // 而两者都在 BurstMode 里，每一艘都要过一遍。
            Label lRetry = generator.DefineLabel();
            code[iLoopHead].labels.Add(lRetry);

            var unconditional = new List<int>();
            var conditional = new List<int>();
            var giveUp = 0;
            var unknown = 0;

            for (var i = 0; i < code.Count; i++)
            {
                if (i == iContinue || i == iExit) continue;

                if (!(code[i].operand is Label lab)) continue;

                if (!exitLabels.Contains(lab)) continue;

                if (code[i].opcode == OpCodes.Br || code[i].opcode == OpCodes.Br_S)
                    unconditional.Add(i);
                else if (code[i].opcode == OpCodes.Brtrue || code[i].opcode == OpCodes.Brtrue_S)
                    conditional.Add(i);
                else if (code[i].opcode == OpCodes.Brfalse || code[i].opcode == OpCodes.Brfalse_S)
                    giveUp++;   // 电力比例 <= 0.1，保持原样
                else
                    unknown++;
            }

            if (unconditional.Count != 2 || conditional.Count != 1 || giveUp != 1 || unknown != 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    "星际连发派船：跳到出口的分支应当是 2 条 br（这一对定下来了）、"
                    + "1 条 brtrue（内层扫描放船了）、1 条 brfalse（电力不足，不动）、0 条其它；"
                    + $"实际 {unconditional.Count}/{conditional.Count}/{giveUp}/{unknown}。"
                    + "原版 DetermineDispatch 的形状变了，"
                    + "整条转译放弃，派船保持原版的一次一艘。");

                return code;
            }

            // 从后往前改，前面的下标才不会被插入顶走。
            var sites = new List<int>();
            sites.AddRange(unconditional);
            sites.AddRange(conditional);
            sites.Sort();
            sites.Reverse();

            // 三个去处共用的尾巴：调 BurstMode，按返回值分流。
            //
            // **用 switch，不用 dup、也不用新局部。** dup 出来的那一份在第一个 beq
            // 不成立时还留在栈上，而跳转目标处的栈必须是空的——那是无效 IL，
            // 而且只在 JIT 时才说话（本文件已经为「结构有效不等于类型有效」付过一次代价）。
            // switch 一条指令弹掉那个 int 并三路分发，每个目标上栈都是空的；
            // 下标越界会落到紧跟的 br（构造上到不了，留着是因为 IL 必须有后继）。
            // 跳转表**按常量下标填**，而不是按书写顺序排——这样 ModeXxx 的值和
            // switch 的分支构造上就不可能分家。
            var targets = new Label[3];
            targets[ModeStop] = lExit;
            targets[ModeNext] = lContinue;
            targets[ModeRetry] = lRetry;

            CodeInstruction[] Tail()
            {
                return new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_S, (byte)4),      // priorityIndex
                    new CodeInstruction(OpCodes.Call, cont),
                    new CodeInstruction(OpCodes.Switch, targets),
                    new CodeInstruction(OpCodes.Br, lExit)
                };
            }

            foreach (int i in sites)
            {
                CodeInstruction[] tail = Tail();

                if (conditional.Contains(i))
                {
                    // 栈上是「内层扫描放船了没有」这个 bool。原版：为真就跳出。
                    // 改成：为假就跳过这一段（走原版的下一条），为真就问一句。
                    Label notDispatched = generator.DefineLabel();

                    // 改写现有指令而不是换一个新的——换掉会丢掉它身上的标签。
                    code[i].opcode = OpCodes.Brfalse;
                    code[i].operand = notDispatched;

                    code.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));

                    for (var k = 0; k < tail.Length; k++) code.Insert(i + 2 + k, tail[k]);

                    code[i + 2 + tail.Length].labels.Add(notDispatched);

                    continue;
                }

                // 无条件跳出：栈上是空的，直接问一句。
                code[i].opcode = OpCodes.Ldarg_0;
                code[i].operand = null;

                for (var k = 0; k < tail.Length; k++) code.Insert(i + 1 + k, tail[k]);
            }

            _patched = sites.Count;

            ProjectEdenPlugin.Log.LogInfo(
                $"星际连发派船：已改写 {sites.Count} 处「定下来了就跳出环」"
                + "（2 条 br + 1 条 brtrue），1 条「电力不足」的跳出保持原样。"
                + "每处三个去处：同一对再来一艘（跳环头，游标不动）／换下一对（原版续扫口）／停（出口）。");

            return code;
        }

        private static int _patched;

        private static float _nextReport;
        private static long _lastShips;
        private static long _lastRounds;
        private static long _lastStopBudget;
        private static long _lastSameRoute;
        private static long _lastCapped;
        private static int _entered;
        private static float _windowStart;

        /// <summary>首次统计窗口（秒）。之后每 <see cref="ReportSeconds"/> 秒一次。</summary>
        private const float FirstReportSeconds = 20f;

        private const float ReportSeconds = 60f;

        /// <summary>
        /// 每 60 秒报一次增量，外加全星系站点的 <c>routePriority</c> 分布。
        ///
        /// 后面那半是**为了分清两道闸**，和行星内那条补丁报派机间隔是同一个用意：
        /// 如果绝大多数站点还是默认的「忽略」优先级，那每个站点一秒才被评估一次，
        /// 瓶颈里有一半在<b>评估频率</b>上，而那一半是玩家一键可改的（设成「优先」= 6 倍），
        /// 本补丁管不着。没有这一行，「连发生效了但还是慢」和「连发没生效」
        /// 在日志里长得一样。
        ///
        /// 挂 <c>UIGame._OnUpdate</c> 是因为它在主线程，而且
        /// <c>Time.realtimeSinceStartup</c> 只能主线程读；用 <c>GameMain.gameTick</c>
        /// 节流会在换存档时倒退，报告会静默死掉（第 4 号坑）。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate_RemoteBurstReport()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;

            if (now < _nextReport) return;

            if (_nextReport <= 0f)
            {
                _nextReport = now + FirstReportSeconds;
                _windowStart = now;
                _lastShips = _ships;
                _lastRounds = _rounds;
                _lastStopBudget = _stopBudget;
                _lastSameRoute = _sameRouteShips;
                _lastCapped = _stopSameRoute;

                // **统计挂点的「我还活着」行。** 没有它，「这一局没待够窗口」和
                // 「这个统计压根没跑到」在日志里长得一模一样。
                if (System.Threading.Interlocked.Exchange(ref _entered, 1) == 0)
                    ProjectEdenPlugin.Log.LogInfo(
                        $"星际连发派船：统计挂点已跑到，{FirstReportSeconds:0} 秒后报第一次、"
                        + $"之后每 {ReportSeconds:0} 秒一次。");

                return;
            }

            _nextReport = now + ReportSeconds;

            // 报的是实测窗口，不是写死的 60——首次窗口是 20 秒，而且掉帧时 _OnUpdate
            // 也可能晚到。写死一个数就等于在日志里撒谎。
            float window = now - _windowStart;
            _windowStart = now;

            long shipsNow = _ships;
            long roundsNow = _rounds;
            long stopBudgetNow = _stopBudget;

            // 这两个只写不读就只是个主张，所以照样报出来。
            long starved = _stopIdle;
            long browned = _stopEnergy;

            long sameRouteNow = _sameRouteShips;
            long cappedNow = _stopSameRoute;
            long sameRoute = sameRouteNow - _lastSameRoute;
            long capped = cappedNow - _lastCapped;

            _lastSameRoute = sameRouteNow;
            _lastCapped = cappedNow;

            long ships = shipsNow - _lastShips;
            long rounds = roundsNow - _lastRounds;
            long stopBudget = stopBudgetNow - _lastStopBudget;

            _lastShips = shipsNow;
            _lastRounds = roundsNow;
            _lastStopBudget = stopBudgetNow;

            string routes = DescribeRoutes();

            if (ships <= 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"星际连发派船：过去 {window:0} 秒一艘都没放出去——"
                    + "要么这个存档里没有星际物流站，要么所有星际配对都没有活儿可干。"
                    + routes);

                return;
            }

            // 倍率 = 总艘次 / 「原版也会放」的评估次数。**分母才是原版的产出**，
            // 因为原版放一艘就跳出环，一次评估至多贡献一艘。
            double ratio = rounds > 0 ? (double)ships / rounds : 1.0;

            // 撞到额度上限的比例：它小，就说明调大 remoteShipsPerDispatch 不会有效果。
            double capBite = rounds > 0 ? 100.0 * stopBudget / rounds : 0.0;

            ProjectEdenPlugin.Log.LogInfo(
                $"星际连发派船：过去 {window:0} 秒放出 {ships} 艘次，"
                + $"分布在 {rounds} 次派船评估里（原版这 {rounds} 次每次只会放 1 艘），"
                + $"**实测倍率 {ratio:0.##}×**，每次评估上限 {_max}。"
                + $"其中 {stopBudget} 次（{capBite:0.#}%）是被额度打断的——"
                + "这个比例低就说明调大 remoteShipsPerDispatch 不会再有效果，"
                + "限制在「没货可送」或者下面那条评估频率上。"
                + $" 这 {ships} 艘里有 **{sameRoute} 艘是同一条线接着再来的**"
                + $"（每对上限 {_sameRouteMax}），另有 {capped} 次是撞到每对上限被迫换下一对——"
                + "后面这个数大就说明调大 remoteSameRouteMax 还有空间，"
                + "它小就说明是货扣光了（原版自己收的），再调也没用。"
                + (starved > 0 || browned > 0
                    ? $" 另有累计 {starved} 次是「闲置运输船用光」、{browned} 次是「电不够」被护栏拦下的"
                      + "（这两道闸是逐字抄原版环体内那两道的，拦下来就是原版也不会放）。"
                    : "")
                + routes);
        }

        /// <summary>
        /// 全星系星际站点的 <c>routePriority</c> 分布，主线程读，零 tick 路径开销。
        ///
        /// 为什么报这个：默认值是 <c>Ignore</c>（**枚举值是 1，不是 0**，量出来的），
        /// 而 <c>GalacticTransport.GameTick</c> 只在 <c>tick % 60 == 0</c> 那一趟处理它，
        /// 所以默认站点一秒才被评估一次。改成「优先」会把它挪进 <c>tick % 10</c> 那一段，
        /// 直接 6 倍，而且不用改一行代码——这是本补丁之外的另一根杠杆，两根是乘起来的。
        /// </summary>
        private static string DescribeRoutes()
        {
            GalacticTransport gt = GameMain.data?.galacticTransport;

            if (gt == null) return "（星际物流还没初始化，优先级分布这次不报。）";

            StationComponent[] pool = gt.stationPool;

            if (pool == null) return "（星系里没有星际物流站。）";

            int cursor = gt.stationCursor;
            var n = 0;
            var ignore = 0;
            var fast = 0;
            var pairs = 0;
            var idle = 0;

            for (var i = 1; i < cursor && i < pool.Length; i++)
            {
                StationComponent s = pool[i];

                if (s == null || s.id <= 0 || s.gid != i) continue;

                n++;
                idle += s.idleShipCount;

                // 这个站点自己的星际配对总数。**没有 remotePairCount 这个字段**
                // （那个在 GalacticTransport 上，是全星系的），站点这一侧只有
                // remotePairOffsets 这张六段分界表，最后一个元素就是段尾 = 总数。
                int[] offsets = s.remotePairOffsets;

                if (offsets != null && offsets.Length > 0) pairs += offsets[offsets.Length - 1];

                if (s.routePriority == ERemoteRoutePriority.Ignore) ignore++;
                else fast++;
            }

            if (n == 0) return "（星系里没有星际物流站。）";

            return $" 全星系 {n} 个星际站点：默认（忽略）优先级 {ignore} 个"
                 + $"（{(100.0 * ignore / n):0.#}%）、设过优先级的 {fast} 个，"
                 + $"闲置运输船共 {idle} 艘，星际配对共 {pairs} 对。"
                 + "**默认优先级的站点原版一秒只被评估一次**"
                 + "（GalacticTransport.GameTick 的第三个调用点，tick % 60）；"
                 + "把取货那一端设成「优先」会挪到 tick % 10 那一段，评估频率直接 6 倍，"
                 + "不用改任何配置。它和本补丁的额度是乘起来的。";
        }

        /// <summary>
        /// 开机状态行：这条补丁**接上了没有**，以及额度是多少。
        ///
        /// 读 <c>Harmony.GetAllPatchedMethods()</c>——已生效的状态，而不是
        /// 「我调了 PatchAll 而且没抛异常」。必须排在 PatchAll 之后。
        /// </summary>
        internal static void Report()
        {
            if (_max < 0)
            {
                _max = Resolve();
                _sameRouteMax = ResolveSameRoute(_max);
            }

            var hooked = false;

            foreach (MethodBase mb in Harmony.GetAllPatchedMethods())
                if (mb?.DeclaringType == typeof(StationComponent)
                 && mb.Name == nameof(StationComponent.DetermineDispatch))
                {
                    hooked = true;

                    break;
                }

            if (!hooked)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "星际连发派船：补丁没挂到 StationComponent.DetermineDispatch 上，"
                    + "整局都会保持原版的一次评估一艘。");

                return;
            }

            if (_patched != 3)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"星际连发派船：挂上了，但转译只改写了 {_patched} 处（应当 3 处），"
                    + "派船保持原版的一次评估一艘。上面应当有一条 ERROR 说明原因。");

                return;
            }

            int raw = ProjectEdenPlugin.StationsConfig?.remoteShipsPerDispatch ?? 0;

            if (_max <= 1)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"星际连发派船：已接上，但 stations.json 的 remoteShipsPerDispatch = {raw}，"
                    + "按原版行为跑（一次派船评估最多一艘）。把它调大即可生效，不用重新编译——"
                    + "配置文件放在 BepInEx/config/ProjectEden/stations.json。");

                return;
            }

            int rawSame = ProjectEdenPlugin.StationsConfig?.remoteSameRouteMax ?? 0;

            ProjectEdenPlugin.Log.LogInfo(
                $"星际连发派船：已启用，一次派船评估最多放 {_max} 艘"
                + (raw > HardCap ? $"（配置写的是 {raw}，夹到了硬上限 {HardCap}）" : "")
                + $"，其中**同一条运输线最多连发 {_sameRouteMax} 艘**"
                + (rawSame > _max ? $"（配置写的是 {rawSame}，不可能超过总额度，夹到 {_sameRouteMax}）" : "")
                + (_sameRouteMax <= 1 ? "（= 1，逐对轮转，和 1.10.7 一样）" : "")
                + "。原版是一艘——它的配对扫描本来就会走遍整段配对环，"
                + "只是定下一对就跳出去了；现在改成问一句：还有额度就要么同一对再来一艘"
                + "（跳回环头，游标不动），要么换下一对（原版自己的续扫口）。"
                + "vanilla 的派船判断一条都没重写，「电力不足」那处跳出也保持原样；"
                + "同一对能不能再来一艘由原版自己的记账收敛——每次派船当场扣两端，扣光了它自己换对。"
                + "注意另一半在评估频率上：默认优先级的站点一秒才被评估一次，"
                + "把取货端设成「优先」是额外的 6 倍，三者相乘。"
                + "每 60 秒报一次实际倍率、同线连发占比和优先级分布。");
        }
    }
}
