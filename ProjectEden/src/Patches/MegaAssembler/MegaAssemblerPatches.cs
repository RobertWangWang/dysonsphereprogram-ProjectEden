// 本文件移植自 ProjectGenesis（创世之书），属于其衍生作品。
// Portions of this file are derived from ProjectGenesis (GenesisBook).
//
//     Copyright (C) 2022-2026 Awbugl and ProjectGenesis contributors
//     https://github.com/Awbugl/ProjectGenesis
//
// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectEden.Compatibility;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 巨型建筑机的传送带直连 I/O。移植自 ProjectGenesis 的 MegaAssemblerPatches，
    /// 去掉了物流塔存储、Nebula 联机同步、物质分解特例和自定义配方类型。
    ///
    /// 判定方式沿用原作：speed >= 阈值的组装机即视为巨型，不需要独立的实体类型。
    /// </summary>
    [HarmonyPatch]
    internal static class MegaAssemblerPatches
    {
        /// <summary>
        /// 组装机的 tick 有两条路径：单线程的 FactorySystem.GameTick，和多线程的
        /// GameLogic._assembler_parallel。游戏默认走后者，所以只挂 FactorySystem.GameTick
        /// 的 Postfix 是收不到回调的——本 mod 之前就栽在这里，传送带 I/O 和物流都从未执行。
        ///
        /// 这里对两条路径都做注入：在每次 AssemblerComponent.InternalUpdate 调用之前，
        /// 插入一次带 PlanetFactory 的回调。做法与 ProjectGenesis 的
        /// AssemblerComponent_InternalUpdate_PrePatch 一致。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(FactorySystem), nameof(FactorySystem.GameTick))]
        [HarmonyPatch(typeof(GameLogic), nameof(GameLogic._assembler_parallel))]
        private static IEnumerable<CodeInstruction> AssemblerTick_Transpiler(IEnumerable<CodeInstruction> instructions,
            MethodBase original)
        {
            var matcher = new CodeMatcher(instructions);

            // 取 PlanetFactory 的方式两条路径不同：
            //   FactorySystem.GameTick   -> this.factory
            //   _assembler_parallel      -> 局部变量，用 “ldloc + ldfld entityAnimPool” 认出来
            CodeInstruction[] loadFactory;

            if (original.Name == nameof(GameLogic._assembler_parallel))
            {
                matcher.MatchForward(false,
                    new CodeMatch(OpCodes.Ldloc_S),
                    new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(PlanetFactory), nameof(PlanetFactory.entityAnimPool))));

                if (matcher.IsInvalid)
                {
                    ProjectEdenPlugin.Log.LogError(
                        "GameLogic._assembler_parallel 里找不到 PlanetFactory 局部变量，巨型建筑的多线程路径未接管");

                    return matcher.InstructionEnumeration();
                }

                loadFactory = new[] { new CodeInstruction(matcher.Instruction) };
            }
            else
            {
                loadFactory = new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(FactorySystem), nameof(FactorySystem.factory))),
                };
            }

            matcher.Start();

            var injected = 0;

            while (true)
            {
                matcher.MatchForward(false,
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(AssemblerComponent), nameof(AssemblerComponent.InternalUpdate))));

                if (matcher.IsInvalid) break;

                // 调用点往前数 4 条是组装机局部变量，紧随其后的是 power
                // 调用点之前依次是：组装机、power、productRegister、consumeRegister
                CodeInstruction component = matcher.Advance(-4).Instruction;
                CodeInstruction power = matcher.InstructionAt(1);
                CodeInstruction productRegister = matcher.InstructionAt(2);
                CodeInstruction consumeRegister = matcher.InstructionAt(3);

                matcher.InsertAndAdvance(loadFactory);
                matcher.InsertAndAdvance(new CodeInstruction(component),
                                         new CodeInstruction(power),
                                         new CodeInstruction(productRegister),
                                         new CodeInstruction(consumeRegister),
                                         new CodeInstruction(OpCodes.Call, MegaTickMethod));

                injected++;

                // 跳过刚插入的内容和原本的 4+1 条，继续找下一个调用点
                matcher.Advance(5);
            }

            if (injected == 0)
                ProjectEdenPlugin.Log.LogError($"{original.Name}：没找到 AssemblerComponent.InternalUpdate 调用点，巨型建筑不会工作");
            else
                ProjectEdenPlugin.Log.LogInfo($"{original.Name}：已在 {injected} 处组装机 tick 前注入巨型建筑逻辑");

            return matcher.InstructionEnumeration();
        }

        private static readonly MethodInfo MegaTickMethod =
            AccessTools.Method(typeof(MegaAssemblerPatches), nameof(MegaTick));

        /// <summary>供 IL 调用：每台组装机 tick 前跑一次，只对巨型建筑做事。</summary>
        internal static void MegaTick(PlanetFactory factory, ref AssemblerComponent component, float power,
            int[] productRegister, int[] consumeRegister)
        {
            if (!GenesisBookCompat.MegaAssemblerEnabled) return;
            if (factory == null) return;
            if (component.speed < MegaBuildingRegistry.MegaSpeedThreshold) return;

            MegaTickProfiler.CountBuilding();

            long tOther = MegaTickProfiler.Now();

            ApplySpeed(ref component);

            MegaTickProfiler.AddOther(tOther);

            UpdateSlots(factory, ref component);

            tOther = MegaTickProfiler.Now();

            // 催化反应器：床里没有活性催化剂就这一 tick 不许生产。
            // Gate 自己会调 Suppress 压住原版那次调用——前置钩子取消不了它后面那次。
            if (!CatalystBedPatches.Gate(factory, ref component))
            {
                MegaTickProfiler.AddOther(tOther);

                return;
            }

            MegaTickProfiler.AddOther(tOther);

            long tCycles = MegaTickProfiler.Now();

            int settled = RunExtraCycles(factory, ref component, power, productRegister, consumeRegister);

            MegaTickProfiler.AddCycles(tCycles);

            tOther = MegaTickProfiler.Now();

            // 只在**真的产出了**的 tick 扣活性。settled 是实测值；cyclesPerTick = 1 时
            // 没有补跑周期可测，才退回按原版判据推导（见 LooksProductive 的注释）。
            CatalystBedPatches.Settle(factory, ref component, settled,
                                      settled == 0 && CatalystBedPatches.LooksProductive(ref component, power));

            CatalystBedPatches.DebugTick(factory, ref component);

            // 氧化还原燃烧厂：把刚压出来的药柱直接搬进自己的燃料舱。
            // 这台建筑同时挂着组装机和发电机两个组件（EntityData 里是两个独立字段），
            // 所以「压料」和「烧料」在同一台机器上，中间不经过传送带。
            RedoxBurnerPatches.Burn(factory, ref component);

            MegaTickProfiler.AddOther(tOther);
        }

        /// <summary>
        /// 单 tick 多周期结算。
        ///
        /// 原版每 tick 只结算一个配方周期：产物写入是 produced[i] += productCounts[i]，
        /// 不乘任何周期数，time 也只扣一次 timeSpend。所以无论速度多高，上限都是 60 周期/秒。
        ///
        /// 这里不去改结算逻辑本身——产物、原料扣减、time 三处分散在不同 tick，
        /// 手工同步很容易出现「产物乘了而原料没乘」的凭空造物。
        /// 改为在同一 tick 内多跑几遍原版的 InternalUpdate：每一遍都是完整的原版流程，
        /// 自带扣料与产出核算，原料不足时它自己就不产出，结构上不可能失衡。
        ///
        /// 之所以多跑几遍就能多产出：高速下 time 会累积成一个「待结算周期」的缓冲，
        /// 每次调用消化其中一个，而 time &gt;= timeSpend 期间不再累加。
        /// </summary>
        /// <remarks>
        /// 返回<b>实际结算掉的</b>补跑周期数——每跑一遍就看一眼 <c>produced[0]</c> 动没动。
        /// 催化剂床拿它当「这一 tick 真的产出了吗」的判据：断电、缺料、产物槽满
        /// 三种情况下原版本来就不结算，这里也就数不出来，活性自然不会被扣。
        /// <b>这是实测，不是复现原版的条件</b>，所以原版将来多一道闸也不会失准。
        /// </remarks>
        private static int RunExtraCycles(PlanetFactory factory, ref AssemblerComponent component, float power,
            int[] productRegister, int[] consumeRegister)
        {
            int cycles = MegaBuildingRegistry.Config?.cyclesPerTick ?? 1;

            // ── 电力不足按供电率线性降速 ──────────────────────────
            //
            // **不做这一步，巨型建筑对缺电几乎免疫、然后一头撞死。** 那不是设计，
            // 是「10000 倍」撞上原版公式的副作用，两条指令就能看明白：
            //
            //   InternalUpdate IL 0000: if (power < 0.1f) return 0;
            //                  IL 0576: time += (int)(power * speedOverride);
            //
            // 原版 1 倍机器的 speedOverride 是 10000，time 涨得慢一半产量就慢一半——
            // **产出对供电率是线性的**。可巨型建筑是 1e8，单次调用加的 time 比任何
            // timeSpend 都大一两个数量级，于是供电率 0.11 和 1.00 结算出来<b>一模一样</b>，
            // 掉到 0.1 以下则整台停摆。玩家看到的就是「一点不减速，然后突然全死」。
            //
            // 所以节流阀只能是**每 tick 跑几个周期**，和生物温室按日照缩放同一个旋钮
            // （见 MegaLightPatches：speed 绝对不能动，降到阈值以下这台建筑就再也不被接管）。
            //
            // **曲线照抄原版自己那条：线性。** 不自己发明一条，两边才不会在边界上各说各话
            // ——这和钻头消耗「逐项照抄原版的产出表达式」是同一条规矩。
            //
            // 地板取 1：那正是原版 1 倍机器的满速。缺电该变慢，不该变成完全停工；
            // 而真正的停工由原版自己的 power < 0.1 那道闸负责，我们不重复它。
            if (MegaBuildingRegistry.Config?.powerScalesCycles == true && cycles > 1)
            {
                float p = power < 0f ? 0f : power > 1f ? 1f : power;

                // 四舍五入而不是截断：截断在 cycles 小的时候会系统性少给近一个周期
                var scaled = (int)(cycles * p + 0.5f);

                if (scaled < 1) scaled = 1;

                if (scaled < cycles) ReportThrottleOnce(cycles, scaled, p);

                cycles = scaled;
            }

            // 看天吃饭的建筑（生物温室）：周期数按日照强度缩放，满日照满产、零日照停工。
            // 缩到 0 就连原版那一次也要压住——它插在我们后面，拦不掉，只能让它结算不了
            if (MegaLightPatches.IsLightDependent(factory, component.entityId))
            {
                cycles = MegaLightPatches.ScaleCycles(factory, ref component, cycles);

                if (cycles <= 0)
                {
                    MegaLightPatches.Suppress(ref component);

                    return 0;
                }
            }

            int[] produced = component.produced;
            bool watch = produced != null && produced.Length > 0;
            int last = watch ? produced[0] : 0;
            var settled = 0;

            // ── 空转提前退出 ────────────────────────────────────────
            //
            // 原来这个循环<b>无条件跑满 cycles - 1 遍</b>。一台缺料 / 产物槽满 /
            // 没配方的巨型建筑照样把原版 InternalUpdate（693 条指令）完整跑 59 遍，
            // 约 41000 条指令、产出为零——而一颗建满的星球上任意时刻都有相当一部分
            // 巨型建筑正处在这三种状态里。
            //
            // <b>为什么「上一遍没动」就能推出「后面都不会动」：</b>这个循环内部除了
            // InternalUpdate 自己，没有任何东西会改变它的输入。原料补给（UpdateSlots）
            // 在循环<b>之前</b>就跑完了，power 是入参，配方在 tick 内不会变。所以同样的
            // 输入调第二次必然得到同样的结果。
            //
            // <b>判据必须是三项，缺一不可。</b>
            //   · produced[] 没动 —— 没有产出
            //   · time 没涨       —— 原版是 `time += (int)(power * speedOverride)`，
            //                        只有 time >= timeSpend 才结算。**不能只看产出**：
            //                        time 涨了但还没满的那几遍看起来「什么都没发生」，
            //                        实际是在攒下一个周期。speed = 1e8 时一次调用必定
            //                        溢满，所以现在看不出差别——但判据不该依赖那个数值，
            //                        否则以后谁调低 assemblerSpeed 就会静默少产。
            //   · extraTime 没涨  —— 增产额外产出是<b>独立的第二个计时器</b>
            //                        （InternalUpdate 里 time 和 extraTime 是两个互不
            //                        相干的 if），只看 time 会漏掉纯增产的那一遍。
            //
            // settled 的语义一个字没变：不结算的周期本来就计 0，而退出的前提正是
            // 「这一遍没结算」，所以催化剂床那边拿到的数完全一致。
            long prevSum = ProducedSum(produced);
            int prevTime = component.time;
            int prevExtra = component.extraTime;

            var ran = 0;
            var skipped = 0;

            // ── 批量结算：调一遍原版，量出稳态的那一个周期，再乘上去 ──────
            //
            // 见 MegaBatchSettle 的类注释。这里只负责编排：**先跑一次真的**，
            // 确认它落在稳态，再把剩下的预算一次性乘掉；任何一步不满足就原样落回
            // 下面那个逐次循环，功能完全不变、只是慢。
            //
            // 顺序很重要：`量` 必须发生在 `乘` 之前，而且量的是<b>这一台、这一刻</b>
            // 的真实差值，不是配方表的名义值——配方表只用来判断「这次差值是不是稳态」。
            if (cycles > 1 && MegaBatchSettle.CanBatch(ref component))
            {
                int[] servedBefore = BatchScratch.Snapshot(ref _servedSnap, component.served);
                int[] producedBefore = BatchScratch.Snapshot(ref _producedSnap, component.produced);

                if (servedBefore != null && producedBefore != null)
                {
                    int cycleBefore = component.cycleCount;
                    int extraBefore = component.extraCycleCount;

                    component.InternalUpdate(power, productRegister, consumeRegister);

                    ran++;

                    if (MegaBatchSettle.IsSteadyUnit(ref component, servedBefore, producedBefore,
                                                     cycleBefore, extraBefore))
                    {
                        settled++;
                        last = watch ? produced[0] : last;

                        int n = MegaBatchSettle.BatchSize(ref component, cycles - 1 - ran);

                        // 自检：抽查时先在副本上回放 n+1 遍，对不上就整局退回逐次。
                        if (n > 0 && MegaBatchAudit.Due()
                                  && !MegaBatchAudit.Verify(ref component, n, power,
                                                            productRegister, consumeRegister))
                            n = 0;

                        if (n > 0)
                        {
                            MegaBatchSettle.Apply(ref component, n, productRegister, consumeRegister);

                            settled += n;
                            ran += n;

                            if (watch) last = produced[0];
                        }

                        Tally(ran, cycles - 1 - ran);

                        return settled;
                    }

                    // 没落在稳态（换配方、产物槽满、原料刚好见底……）：不乘，
                    // 交给下面的逐次循环把剩下的预算跑完。**判据是严格相等，
                    // 所以这里不是"可能有问题"，而是"这一次确实不是那个单位"。**
                    MegaBatchSettle.CountBailShape();

                    if (watch && produced[0] != last)
                    {
                        settled++;
                        last = produced[0];
                    }

                    prevSum = ProducedSum(produced);
                    prevTime = component.time;
                    prevExtra = component.extraTime;
                }
            }
            else if (cycles > 1 && MegaBatchSettle.Enabled)
            {
                MegaBatchSettle.CountBailProliferator();
            }

            // 原本那次调用紧随其后，所以这里只补差额
            for (var i = 1 + ran; i < cycles; i++)
            {
                component.InternalUpdate(power, productRegister, consumeRegister);

                ran++;

                long sum = ProducedSum(produced);
                int nowTime = component.time;
                int nowExtra = component.extraTime;

                if (watch && produced[0] != last)
                {
                    settled++;
                    last = produced[0];
                }

                if (sum == prevSum && nowTime == prevTime && nowExtra == prevExtra)
                {
                    skipped = cycles - 1 - i;

                    break;
                }

                prevSum = sum;
                prevTime = nowTime;
                prevExtra = nowExtra;
            }

            Tally(ran, skipped);

            MegaBatchSettle.CountStepped(ran);

            return settled;
        }

        /// <summary>
        /// <c>produced[]</c> 的总和，当作「这一遍有没有产出」的变化探测器。
        ///
        /// 求和而不是快照整个数组，是因为<b>tick 路径上不许分配</b>，而快照需要一个
        /// 与产物数等长的缓冲区（这条路还是并行的，缓冲区得是 [ThreadStatic]）。
        /// 求和之所以够用：循环内部只有 <c>InternalUpdate</c> 会碰 <c>produced</c>，
        /// 而它只做 <c>produced[i] += productCounts[i]</c>——<b>只增不减</b>，
        /// 取货是循环之外 <c>UpdateStationStorage</c> 的事。只增不减的量，
        /// 总和不变就等于每一项都不变。
        /// </summary>
        private static long ProducedSum(int[] produced)
        {
            if (produced == null) return 0L;

            var sum = 0L;

            for (var i = 0; i < produced.Length; i++) sum += produced[i];

            return sum;
        }

        // ── 空转提前退出的计数与日志 ──────────────────────────────
        //
        // <b>状态行和事件行是两个问题，一个答不了另一个</b>（本仓库记过七次）：
        //   · Report()          回答「这个优化接上了没有」——每局必打，包括没省到的时候
        //   · 首次触发那一行     回答「它第一次真的退出是什么时候」
        //   · 每 60 秒那一行     回答「它到底省了多少」，这是做这件事的全部意义所在
        //
        // 三个数都在<b>并行的 tick 路径</b>上累加（_assembler_parallel），所以只用
        // Interlocked，不碰 Unity 的任何 API——Time.realtimeSinceStartup 在工作线程上
        // 不可用，节流只能放在主线程那一头。

        private static long _cyclesRan;
        private static long _cyclesSkipped;
        private static int _reportedSkipOnce;

        /// <summary>
        /// 记一笔：这台建筑这一 tick 真跑了几遍、又省下了几遍。
        ///
        /// <b>抢占放在字符串插值之前</b>，插值本身就不会落到 tick 上——和
        /// <c>ReportThrottleOnce</c>、<c>AdvancedMinerPatches.ClaimLog</c> 同一套规矩。
        /// </summary>
        private static void Tally(int ran, int skipped)
        {
            if (ran > 0) System.Threading.Interlocked.Add(ref _cyclesRan, ran);

            if (skipped <= 0) return;

            System.Threading.Interlocked.Add(ref _cyclesSkipped, skipped);

            if (System.Threading.Interlocked.Exchange(ref _reportedSkipOnce, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑·空转提前退出：第一次生效，这一台省下 {skipped} 个周期。"
                + "判据是「产出、time、extraTime 三项都没动」——那说明这一遍完全没发生"
                + "任何事，而循环内部没有东西会改变输入，所以后面每一遍也不会。"
                + "整局只报这一行，省了多少看下面每 60 秒那条。");
        }

        private static float _nextSkipReport;
        private static long _lastRan;
        private static long _lastSkipped;
        private static int _reporterEntered;

        /// <summary>
        /// 每 60 秒报一次空转提前退出省下的比例。
        ///
        /// 挂在 <c>UIGame._OnUpdate</c> 上是因为它<b>在主线程</b>：计数在并行的
        /// 装配 tick 上累加，而 <c>Time.realtimeSinceStartup</c> 只能在主线程读。
        /// 节流用 <c>realtimeSinceStartup</c> 而不是 <c>GameMain.gameTick</c>——
        /// 后者在换存档时会倒退，于是 <c>next = tick + 间隔</c> 永远不再到期、
        /// 报告静默死掉，而那看起来和「一切正常」一模一样（本仓库第 4 号坑）。
        ///
        /// 报的是<b>这 60 秒内的增量</b>，不是全局累计：累计值会被开局那几分钟的
        /// 空工厂稀释，而你要看的是「现在这一刻省了多少」。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate_SkipReport()
        {
            // 入口无条件报一行，在任何闸之前：只要这个后置被调到过，日志里就一定有话。
            // 没有这一行就只剩一种可能——补丁根本没挂上。
            if (System.Threading.Interlocked.Exchange(ref _reporterEntered, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    "巨型建筑·空转提前退出：统计挂点已跑到（UIGame._OnUpdate 的后置确实接上了），"
                    + "之后每 60 秒报一次增量。");

            float now = UnityEngine.Time.realtimeSinceStartup;

            if (now < _nextSkipReport)
                return;

            // 第一次进来只对表，不报——否则报的是「开局到现在」那一段，
            // 里面多半是还没建成的空工厂，稀释掉真正想看的数。
            if (_nextSkipReport <= 0f)
            {
                _nextSkipReport = now + 60f;
                _lastRan = System.Threading.Interlocked.Read(ref _cyclesRan);
                _lastSkipped = System.Threading.Interlocked.Read(ref _cyclesSkipped);

                return;
            }

            _nextSkipReport = now + 60f;

            long ranNow = System.Threading.Interlocked.Read(ref _cyclesRan);
            long skipNow = System.Threading.Interlocked.Read(ref _cyclesSkipped);

            long ran = ranNow - _lastRan;
            long skipped = skipNow - _lastSkipped;

            _lastRan = ranNow;
            _lastSkipped = skipNow;

            // 一个周期都没跑 = 这颗存档里没有巨型建筑，或者全在待机。
            // **照样报一行**：静默会让「没有巨型建筑」和「统计坏了」长得一样。
            if (ran <= 0 && skipped <= 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "巨型建筑·空转提前退出：过去 60 秒一个补跑周期都没有——"
                    + "要么这颗存档里没有巨型建筑，要么它们全部断电停摆。");

                return;
            }

            long total = ran + skipped;
            double saved = total > 0 ? 100.0 * skipped / total : 0.0;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑·空转提前退出：过去 60 秒实跑 {ran} 个补跑周期、省下 {skipped} 个，"
                + $"即本该跑的 {total} 个里省掉了 {saved:0.#}%。"
                + "省下的每一个周期都是一整遍 AssemblerComponent.InternalUpdate（原版 693 条指令），"
                + "落在性能面板的 Facilities 一项上。");

            ReportBatch();
        }

        /// <summary>
        /// 开机状态行：这个优化<b>接上了没有</b>。
        ///
        /// 读的是 <c>Harmony.GetAllPatchedMethods()</c>——<b>已生效的状态</b>，
        /// 而不是「我调了 PatchAll 而且没抛异常」。这两者不是一回事，本仓库为此
        /// 付过一次（QualityCraftPatches 那条）。所以它必须排在 PatchAll <b>之后</b>。
        /// </summary>
        internal static void Report()
        {
            int cycles = MegaBuildingRegistry.Config?.cyclesPerTick ?? 1;

            var hooked = false;

            foreach (MethodBase mb in Harmony.GetAllPatchedMethods())
                if (mb?.DeclaringType == typeof(UIGame) && mb.Name == "_OnUpdate")
                {
                    hooked = true;

                    break;
                }

            if (cycles <= 1)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"巨型建筑·空转提前退出：cyclesPerTick = {cycles}，没有补跑周期可省，本优化自然不生效。"
                    + "（这是配置的结果，不是故障。）");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑·空转提前退出：已启用。cyclesPerTick = {cycles}，所以一台"
                + $"缺料／产物槽满／没配方的巨型建筑原本每 tick 要白跑 {cycles - 1} 遍"
                + "原版 InternalUpdate；现在第一遍发现「产出、time、extraTime 都没动」就停。"
                + $"统计挂点 UIGame._OnUpdate={hooked}，每 60 秒报一次省下的比例。");

            if (!hooked)
                ProjectEdenPlugin.Log.LogWarning(
                    "巨型建筑·空转提前退出：优化本身照常生效（它在 tick 路径里），"
                    + "但 UIGame._OnUpdate 的统计后置没挂上，所以看不到省了多少。");
        }

        private static long _lastBatched, _lastStepped, _lastBailP, _lastBailS;

        /// <summary>
        /// 批量结算的覆盖率，跟在空转统计那一行后面报。
        ///
        /// <b>覆盖率是这件事值不值的全部依据</b>：带增产剂的建筑一律退回逐次，
        /// 而那一类占多少事先没人知道。这一行把「批量吃掉的周期 / 逐次跑掉的周期」
        /// 摆出来，顺带把两种退回的原因分开——增产（结构性，只能这样）和
        /// 形状不符（换配方、产物槽满这些，本来就该退回）。
        /// </summary>
        private static void ReportBatch()
        {
            long b = MegaBatchSettle.Batched - _lastBatched;
            long s = MegaBatchSettle.Stepped - _lastStepped;
            long bp = MegaBatchSettle.BailProliferator - _lastBailP;
            long bs = MegaBatchSettle.BailShape - _lastBailS;

            _lastBatched = MegaBatchSettle.Batched;
            _lastStepped = MegaBatchSettle.Stepped;
            _lastBailP = MegaBatchSettle.BailProliferator;
            _lastBailS = MegaBatchSettle.BailShape;

            if (!MegaBatchSettle.Enabled)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "巨型建筑·批量结算：本轮未生效（开关关着，或者自检抓到不一致后已整局退回逐次）。");

                return;
            }

            long tot = b + s;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑·批量结算：过去 60 秒批量吃掉 {b} 个周期、逐次跑掉 {s} 个"
                + (tot > 0 ? $"，覆盖 {100.0 * b / tot:0.#}%" : "")
                + $"；退回逐次的原因——带增产剂 {bp} 次、单次调用没落在稳态 {bs} 次。"
                + $"自检已回放 {MegaBatchAudit.Checks} 次，其中偏保守 {MegaBatchAudit.Conservative} 次"
                + "（偏保守只是慢一点，不影响正确性；真出问题会报 ERROR 并整局关掉批量）。");
        }

        /// <summary>
        /// 批量结算量差值用的快照缓冲，见 <see cref="BatchScratch"/>。
        /// <b>[ThreadStatic] 且不带初始化器</b>——初始化器只在第一个线程上跑，
        /// 而这条路是按星球分线程的并行 tick。
        /// </summary>
        [System.ThreadStatic] private static int[] _servedSnap;

        [System.ThreadStatic] private static int[] _producedSnap;

        private static int _reportedThrottle;

        /// <summary>
        /// 第一次真的因为缺电降速时报一行。
        ///
        /// <b>这条跑在 tick 路径上，而且是并行的</b>（_assembler_parallel），所以先用
        /// <c>Interlocked</c> 抢占再拼字符串——抢占放在字符串插值之前，插值本身就不会
        /// 落到 tick 上。和 <c>AdvancedMinerPatches.ClaimLog</c> 同一套规矩。
        ///
        /// 只报一次，是因为它要回答的是<b>一次性的问题</b>：「降速这件事到底生效了没有」。
        /// 缺电是常态，每次都报就成了刷屏。
        /// </summary>
        private static void ReportThrottleOnce(int full, int scaled, float power)
        {
            if (System.Threading.Interlocked.Exchange(ref _reportedThrottle, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑：供电率 {power:P0}，本 tick 的配方周期从 {full} 降到 {scaled}（线性，照抄原版 "
                + "time += power × speedOverride 的口径）。不降的话 10000 倍速对缺电几乎免疫——"
                + "供电率 0.11 和 1.00 结算完全一样，掉到 0.1 以下才突然整台停摆。"
                + "要关掉改 megabuildings.json 的 powerScalesCycles。");
        }

        private static int _reportedBeltQua;

        /// <summary>
        /// 传送带出货到底带不带品质，只报一次。
        ///
        /// <b>两种结果都报。</b> 只在有品质时才打一行的话，
        /// 「提纯厂没走带子」和「走了带子但品质没接上」在日志上长得一模一样——
        /// 而这次的 bug 恰好就是后者，它整整藏了一个版本。
        ///
        /// <c>Interlocked</c> 抢占在字符串插值之前：这条跑在并行 tick 路径上。
        /// </summary>
        private static void ReportBeltQualityOnce(int itemId, int units, QualityRefineryRegistry.Tier tier)
        {
            if (System.Threading.Interlocked.Exchange(ref _reportedBeltQua, 1) != 0) return;

            if (tier == null || tier.Quality <= 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"巨型建筑·传送带出货：物品 {itemId} ×{units}，不是提纯配方，不带品质（正常）。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑·传送带出货：物品 {itemId} ×{units} 带着每件 {tier.Quality} 分上了带子。"
                + "**在这之前这条路上的品质恒为 0**：品质只挂在 produced → 物流槽位 "
                + "那一步，而 produced 还有直接上带子这一条出路，于是同一台提纯厂两条出路两个答案。");
        }

        private static bool _reportedSpeed;

        /// <summary>
        /// 把已建成建筑的速度提到配置值。
        ///
        /// AssemblerComponent.speed 是建造时从 prefabDesc 取的并存进存档，
        /// 改 prefabDesc 只影响之后新建的——和站点容量、采矿机功率是同一个坑。
        ///
        /// 提速对功耗无影响：AssemblerComponent.SetPCState 用的是
        /// workEnergyPerTick × (1000 + extraPowerRatio) / 1000，与 speed 无关。
        /// </summary>
        private static void ApplySpeed(ref AssemblerComponent component)
        {
            int target = MegaBuildingRegistry.Config?.assemblerSpeed ?? 0;

            if (target <= 0 || component.speed >= target) return;

            if (!_reportedSpeed)
            {
                _reportedSpeed = true;

                ProjectEdenPlugin.Log.LogInfo(
                    $"已建成的巨型建筑速度补正：{component.speed} → {target}" +
                    $"（{component.speed / 10000.0:0.#} 倍 → {target / 10000.0:0.#} 倍）");
            }

            component.speed = target;
        }

        private static void UpdateSlots(PlanetFactory factory, ref AssemblerComponent component)
        {
            long tSlots = MegaTickProfiler.Now();

            SlotData[] slots = SlotDataStore.GetSlots(factory.planetId, component.entityId);

            UpdateOutputSlots(ref component, factory.cargoTraffic, slots, factory.entitySignPool,
                              GameMain.history.stationPilerLevel);
            UpdateInputSlots(ref component, factory.cargoTraffic, slots, factory.entitySignPool);

            MegaTickProfiler.AddSlots(tSlots);

            long tStorage = MegaTickProfiler.Now();

            // 传送带之外，再走一遍行星内物流：储物格与制造台之间搬运，运输机自动送料取货
            MegaStationPatches.UpdateStationStorage(factory, ref component);

            MegaTickProfiler.AddStorage(tStorage);
        }

        /// <summary>把产物和多余的原料推上输出带。</summary>
        private static void UpdateOutputSlots(ref AssemblerComponent __instance, CargoTraffic traffic, SlotData[] slotdata,
            SignData[] signPool, int maxPilerCount)
        {
            if (maxPilerCount < 1) maxPilerCount = 1;

            for (var index1 = 0; index1 < slotdata.Length; ++index1)
            {
                ref SlotData slotData = ref slotdata[index1];

                if (slotData.dir != IODir.Output)
                {
                    // 槽位不是输出也不是输入时，清掉残留的带子引用
                    if (slotData.dir != IODir.Input)
                    {
                        slotData.beltId = 0;
                        slotData.counter = 0;
                    }

                    continue;
                }

                int beltId = slotData.beltId;

                if (beltId <= 0) continue;

                BeltComponent beltComponent = traffic.beltPool[beltId];
                CargoPath cargoPath = traffic.GetCargoPath(beltComponent.segPathId);

                if (cargoPath == null) continue;

                int index2 = slotData.storageIdx - 1;
                var itemId = 0;

                if (index2 >= 0)
                {
                    RecipeExecuteData executeData = __instance.recipeExecuteData;

                    if (index2 < executeData.products.Length)
                    {
                        // 输出产物
                        itemId = executeData.products[index2];
                        int produced = __instance.produced[index2];

                        if (itemId > 0 && produced > 0)
                        {
                            int num = produced < maxPilerCount ? produced : maxPilerCount;

                            // **品质必须在这里也注入一次。**
                            //
                            // <c>produced[]</c> 有<b>两条</b>出路：这条直接上传送带，
                            // 以及 <c>MegaStationPatches.UpdateStationStorage</c> 那条进本建筑自己的
                            // 物流槽位。品质原先只挂在后者（<c>OnProduced</c>），于是
                            // <b>同一台提纯厂，走物流网的那份 50 分、走带子的那份 0 分</b>，
                            // 一个字也不报。玩家看到的是「提纯后是 10 不是 50」（10 是底线分）。
                            //
                            // 查的是同一张表（<c>FindTier</c>）、用的是同一个每件分数，
                            // 所以两条出路不可能再分岔。非提纯配方查不到，一次字典查找就返回。
                            QualityRefineryRegistry.Tier tier =
                                QualityRefineryRegistry.FindTier(__instance.recipeId);

                            int qua = tier == null || tier.Quality <= 0 ? 0 : tier.Quality * num;

                            // **机器自己造出来的那部分品质也要跟着走，而且必须扣掉。**
                            //
                            // 上面那一份是提纯厂凭配方等级铸出来的；这一份是
                            // <c>QualityCraftFlowPatches</c> 把带品质的<b>投料</b>结算进
                            // <c>quaProduced</c> 的。只搬件数不扣分，留在缓冲区里的那份
                            // 会在下一次出货时<b>再发一遍</b>——单件分数凭空往上涨，
                            // 和本仓库在 <c>StationStore.inc</c> 上记过的那个形状一样。
                            // 比例要拿扣减前的件数（produced）算。
                            //
                            // **先算后扣**：带子满时 <c>InsertAtHead</c> 会失败，一件货都没走，
                            // 那一刻扣掉的分就凭空没了——带子越堵丢得越快。
                            qua += QualityCraftOut.PeekSlot(ref __instance, index2, num, produced);

                            if (CargoWidening.InsertAtHead(cargoPath, itemId, num, 0, qua))
                            {
                                QualityCraftOut.DrainSlot(ref __instance, index2, num, produced);

                                __instance.produced[index2] -= num;

                                ReportBeltQualityOnce(itemId, num, tier);
                            }
                        }
                    }
                    else
                    {
                        // 输出多余的原料（槽位索引落在 products 之后即指向 requires）
                        int index3 = index2 - executeData.products.Length;

                        if (index3 < executeData.requires.Length)
                        {
                            itemId = executeData.requires[index3];
                            int served = __instance.served[index3];

                            if (itemId > 0 && served > 0)
                            {
                                int num = served < maxPilerCount ? served : maxPilerCount;
                                var inc = (int)((double)__instance.incServed[index3] * num / served);

                                // 多余原料退回带子时，品质也要按件数带走——
                                // 只扣件数不扣品质，留下的料就白白继承了整格的点数，
                                // 和本仓库在 StationStore.inc 上记过的那个形状一样。
                                int[] quaServed = QualityAccess.ServedQuaReady
                                    ? QualityAccess.GetServedQua(ref __instance)
                                    : null;

                                var qua = 0;

                                if (quaServed != null && index3 < quaServed.Length && quaServed[index3] > 0)
                                    qua = (int)((double)quaServed[index3] * num / served);

                                if (CargoWidening.InsertAtHead(cargoPath, itemId, num, inc, qua))
                                {
                                    __instance.incServed[index3] -= inc;
                                    __instance.served[index3] -= num;

                                    if (qua > 0) quaServed[index3] -= qua;
                                }
                            }
                        }
                    }
                }

                if (itemId <= 0) continue;

                // 在带子上打出物品图标，和物流塔的表现一致
                int entityId = beltComponent.entityId;
                signPool[entityId].iconType = 1U;
                signPool[entityId].iconId0 = (uint)itemId;
            }
        }

        /// <summary>从输入带取料，填进 served；也接受回流的产物。</summary>
        private static void UpdateInputSlots(ref AssemblerComponent __instance, CargoTraffic traffic, SlotData[] slotdata,
            SignData[] signPool)
        {
            for (var index = 0; index < slotdata.Length; ++index)
            {
                if (slotdata[index].dir != IODir.Input)
                {
                    if (slotdata[index].dir != IODir.Output)
                    {
                        slotdata[index].beltId = 0;
                        slotdata[index].counter = 0;
                    }

                    continue;
                }

                int beltId = slotdata[index].beltId;

                if (beltId <= 0) continue;

                BeltComponent beltComponent = traffic.beltPool[beltId];
                CargoPath cargoPath = traffic.GetCargoPath(beltComponent.segPathId);

                if (cargoPath == null) continue;

                int itemId = CargoWidening.PickAtRear(cargoPath, __instance.needs, out int needIdx, out int stack,
                    out int inc, out int qua);

                RecipeExecuteData executeData = __instance.recipeExecuteData;

                if (needIdx >= 0 && itemId > 0 && __instance.needs[needIdx] == itemId)
                {
                    __instance.served[needIdx] += stack;
                    __instance.incServed[needIdx] += inc;

                    // 品质跟着件数进 <c>quaServed</c>，和 <c>incServed</c> 逐行对应。
                    // 不接这一句的后果是：带品质的料用带子送进巨型建筑就归 0。
                    if (qua > 0 && QualityAccess.ServedQuaReady)
                    {
                        int[] quaServed = QualityAccess.GetServedQua(ref __instance);

                        if (quaServed != null && needIdx < quaServed.Length) quaServed[needIdx] += qua;
                    }

                    slotdata[index].storageIdx = executeData.products.Length + needIdx + 1;
                }

                for (var i = 0; i < executeData.products.Length; i++)
                {
                    if (__instance.produced[i] >= 50) continue;

                    // **这一笔的品质是明确丢弃的，不是忘了接。**
                    // 这是「产物回流」：把带子上的成品收回 <c>produced[]</c>。
                    // 而产物侧<b>根本没有可存品质的字段</b>（原版连 <c>incProduced</c>
                    // 都没有，孪生变换无物可镜），所以拿回来也无处可放。
                    // 要接就得给 AssemblerComponent 新增一个 producedQua，那是 preloader 的活。
                    // 丢失有界（只在玩家把成品回接进进料口时发生），写在明处。
                    itemId = CargoWidening.PickAtRear(traffic, beltId, executeData.products[i], null, out stack, out int _);

                    if (executeData.products[i] != itemId) continue;

                    __instance.produced[i] += stack;
                    slotdata[index].storageIdx = i + 1;

                    break;
                }

                if (itemId <= 0) continue;

                int entityId = beltComponent.entityId;
                signPool[entityId].iconType = 1U;
                signPool[entityId].iconId0 = (uint)itemId;
            }
        }

        /// <summary>传送带接到建筑上（建筑 → 带）时记录成输出槽。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.ApplyInsertTarget))]
        private static void PlanetFactory_ApplyInsertTarget(PlanetFactory __instance, int entityId, int insertTarget, int slotId)
        {
            if (!IsMegaAssembler(__instance, entityId)) return;

            int beltId = __instance.entityPool[insertTarget].beltId;

            if (beltId <= 0) return;

            SlotData[] slots = SlotDataStore.GetSlots(__instance.planetId, entityId);

            if (slotId < 0 || slotId >= slots.Length) return;

            slots[slotId].dir = IODir.Output;
            slots[slotId].beltId = beltId;
            slots[slotId].counter = 0;
        }

        /// <summary>传送带接到建筑上（带 → 建筑）时记录成输入槽。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.ApplyPickTarget))]
        private static void PlanetFactory_ApplyPickTarget(PlanetFactory __instance, int entityId, int pickTarget, int slotId)
        {
            if (!IsMegaAssembler(__instance, entityId)) return;

            int beltId = __instance.entityPool[pickTarget].beltId;

            if (beltId <= 0) return;

            SlotData[] slots = SlotDataStore.GetSlots(__instance.planetId, entityId);

            if (slotId < 0 || slotId >= slots.Length) return;

            slots[slotId].dir = IODir.Input;
            slots[slotId].beltId = beltId;
            slots[slotId].storageIdx = 0;
            slots[slotId].counter = 0;
        }

        /// <summary>建筑拆除时清掉槽位，避免字典无限增长。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.RemoveEntityWithComponents))]
        private static void PlanetFactory_RemoveEntityWithComponents(PlanetFactory __instance, int id)
        {
            SlotDataStore.Remove(__instance.planetId, id);
        }

        private static bool IsMegaAssembler(PlanetFactory factory, int entityId)
        {
            if (entityId <= 0) return false;
            if (!GenesisBookCompat.MegaAssemblerEnabled) return false;

            int assemblerId = factory.entityPool[entityId].assemblerId;

            if (assemblerId <= 0) return false;

            ref AssemblerComponent assembler = ref factory.factorySystem.assemblerPool[assemblerId];

            return assembler.id == assemblerId && assembler.speed >= MegaBuildingRegistry.MegaSpeedThreshold;
        }
    }
}
