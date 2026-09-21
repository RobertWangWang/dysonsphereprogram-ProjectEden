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
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 集装层数。
    ///
    /// 光把运行时的 GameHistoryData 值改成 50 是不够的——**物流站面板上那个「输出货物集装数量」
    /// 的滑条上限，是 UIStationWindow 按集装科技的 UnlockValues 现算出来的**，
    /// 不看运行时值。所以必须改科技的解锁值本身，面板才会跟着变。
    ///
    /// 另外自动集装机（PilerComponent）把合并上限硬编码成了 4，也要一并替换。
    ///
    /// 做法移植自 ProjectGenesis 的 PilerLevelPatches。
    /// </summary>
    [HarmonyPatch]
    internal static class PilerLevelPatches
    {
        /// <summary>
        /// 绝对硬上限 255：<c>Cargo.stack</c> 是 byte，这是它装得下的最大值。
        /// <c>CargoContainer.AddItemStackToCargo</c> 有 <c>stack >= maxStack</c> 的护栏，
        /// 所以只要不超过 255，层数本身永远不会溢出。
        ///
        /// <b>但「安全」只针对没喷增产剂的货。</b> <c>Cargo.inc</c> 同样是 byte，
        /// 存的是<b>整堆</b>的增产点数总和，而它<b>没有任何护栏</b>——
        /// 增产剂 Mk.III 每件 4 点，于是：
        /// <list type="bullet">
        /// <item><b>63 层</b>：63 × 4 = 252，正好塞得下，满 4 级不打折</item>
        /// <item><b>64 层</b>：256 回绕成 0，<b>增产点数全没</b></item>
        /// <item><b>255 层</b>：1020 回绕成 252，每件 252 ÷ 255 = <b>0 级</b></item>
        /// </list>
        /// 注意这是<b>悬崖不是斜坡</b>：63 完美、64 归零，中间没有缓冲。
        /// 而且它静默发生——不报错、不提示，只是增产剂白喷了。
        ///
        /// 所以：<b>全线喷涂的存档应该把配置留在 63</b>；开到更高只对
        /// 「不往传送带上喷增产剂」的玩法有意义。要两者兼得，得给 inc 另开一本账，
        /// 见 CLAUDE.md 的 cargo stacking ceiling 一节（骨架已验证，尚未实施）。
        /// </summary>
        private static int AbsoluteMax =>
            CargoWidening.StackIsWide
                ? short.MaxValue / Cargo.kSprayIncMax   // 8191
                : 255;

        /// <summary>
        /// 分拣器堆叠的硬上限，<b>和集装层数不是同一个天花板</b>。
        ///
        /// <c>GameHistoryData.inserterStackInput/Output</c> 是 Int32，看着能随便设；
        /// 但它们最终要落进 <c>InserterComponent.stackInput/stackOutput</c>，那是 <b>Byte</b>，
        /// 而且三条赋值路径（<c>NewInserterComponent</c> / <c>OnInserterTechChange</c> /
        /// <c>UpgradeEntityWithComponents</c>）全都带 <c>conv.u1</c>。
        /// 所以填 5000 不是"分拣器一次搬 5000"，而是 <b>5000 &amp; 255 = 136</b>——
        /// 比填 255 还差，而且<b>不报错</b>。
        ///
        /// preloader 只加宽了 <c>Cargo</c> 的两个字段，没碰分拣器，所以这里必须自己夹住。
        /// 真要全链路突破，得再把这两个字节字段也加宽一轮。
        /// </summary>
        /// <summary>
        /// preloader 把这两个字段也加宽之后，分拣器就和传送带同一个天花板了；
        /// 没加宽（或整体放弃改写）时仍然是 255。<b>查的是结果，不是我们的声明。</b>
        /// </summary>
        private static int InserterAbsoluteMax
        {
            get
            {
                // 没加宽时仍是字节：stackInput/stackOutput 本身就装不下超过 255
                if (!CargoWidening.InserterIsWide) return 255;

                // **stackInput 数的是「抓几垛」，不是「抓几件」。**
                //
                // InserterComponent.InternalUpdate 的取货循环判的是
                //   if (stackCount >= stackInput) 停
                // 每抓一垛就 stackCount++，而一垛是传送带上一整堆货。
                // 所以一次摆臂搬的件数 ≈ stackInput × 传送带集装层数。
                //
                // 装这个总数的 itemCount / itemInc 都是 **Int16**，而且**我们没有加宽它们**
                // （preloader 只动了 Cargo 和 stackInput/stackOutput）。约束是：
                //   itemInc = itemCount × 每件点数(≤4) ≤ 32767  →  itemCount ≤ 8191
                //   itemCount ≈ stackInput × 集装层数           →  stackInput ≤ 8191 / 集装层数
                //
                // 原版这事不成立：4 垛 × 4 件 = 16，离 Int16 远得很。
                // 集装调到 5000 之后一垛就 5000 件，**一次只搬得下一垛**——
                // 硬填 5000 的后果就是 itemCount 冲到两千多万、回绕成负数，
                // 面板上显示 -25745，货物凭空消失。实测报障。
                int belt = GetMaxPilerStack();

                if (belt < 1) belt = 1;

                int perSwing = short.MaxValue / Cargo.kSprayIncMax;   // 8191 件
                int max = perSwing / belt;

                return max < 1 ? 1 : max;
            }
        }

        /// <summary>GameHistoryData.UnlockTechFunction 的解锁函数编号</summary>
        private const int FuncStationPilerLevel = 29,   // stationPilerLevel += value，基础值 1（累加型）
                          FuncInserterStackOutput = 39, // inserterStackOutput = value（赋值型）
                          FuncInserterStackInput = 41;  // inserterStackInput = value（赋值型）

        private static StationsConfig Config => ProjectEdenPlugin.StationsConfig;

        private static int Target
        {
            get
            {
                int wanted = Config?.stationPilerLevel ?? 0;

                if (wanted <= 0) return 0;

                return wanted > AbsoluteMax ? AbsoluteMax : wanted;
            }
        }

        /// <summary>自动集装机能堆到的层数，跟随当前集装科技等级。供 IL 调用。</summary>
        internal static int GetMaxPilerStack()
        {
            int level = GameMain.history?.stationPilerLevel ?? 4;

            if (level < 1) return 1;

            return level > AbsoluteMax ? AbsoluteMax : level;
        }

        internal static float GetMaxPilerStackFloat() => GetMaxPilerStack();

        /// <summary>分拣器堆叠层数的目标值，夹在 255 并在配置超标时响一声。</summary>
        internal static int InserterTarget
        {
            get
            {
                int wanted = Config?.inserterStackInput ?? 0;
                int other = Config?.inserterStackOutput ?? 0;

                if (other > wanted) wanted = other;

                if (wanted <= InserterAbsoluteMax) return wanted;

                if (Interlocked.Exchange(ref _inserterWarned, 1) == 0)
                {
                    int cap = InserterAbsoluteMax;

                    ProjectEdenPlugin.Log.LogInfo(
                        CargoWidening.InserterIsWide
                            ? $"分拣器堆叠按 {cap} 垛生效（配置写的是 {wanted}）。" +
                              $"这个数是推出来的：stackInput 数的是「一次抓几垛」而不是「几件」，" +
                              $"而承载总件数的 itemCount/itemInc 是 Int16，一次摆臂最多装 {short.MaxValue / Cargo.kSprayIncMax} 件；" +
                              $"传送带集装 {GetMaxPilerStack()} 层时 {cap} 垛就顶满了。" +
                              "填更大不会更快，只会让 itemCount 回绕成负数、货物凭空消失。"
                            : $"stations.json 的分拣器堆叠层数填了 {wanted}，已夹到 {cap}——" +
                              "InserterComponent.stackInput/stackOutput 仍是 Byte（preloader 没生效）。");
                }

                return InserterAbsoluteMax;
            }
        }

        private static int _inserterWarned;

        /// <summary>
        /// 供 IL 调用：把原版算出的堆叠层数抬到集装科技等级。
        /// 紧跟在计算之后、原版钳制之前，所以最终仍会被 [1, 集装等级] 夹住。
        ///
        /// <b>它同时要封顶，而不是只抬高。</b> 原版算的是
        /// <c>(36000000 / period × miningSpeed) / 1800 + 1</c>——<c>period</c> 在<b>分母</b>上，
        /// 所以把采矿周期调小（见 <c>AdvancedMinerPatches.ResolveMinerPeriod</c>）会让这个数
        /// 成比例地涨。而它最终写进 <c>Cargo.stack</c>，preloader 把那个字段加宽到了 Int16，
        /// 上限是 <see cref="AbsoluteMax"/>（8191，卡在增产点数上）。
        /// 只抬不封的话，period 一小就会算出两万多层，Int16 回绕成负数——
        /// 正是本仓库记过的「自动集装机吃货、面板显示负数」那一类，而且一个字都不报。
        ///
        /// 封顶在这里而不是在调用点：这是<b>唯一</b>的出口，逐个调用点加夹子必然漏一个。
        /// </summary>
        internal static int RaiseStack(int current)
        {
            int max = GetMaxPilerStack();
            int value = current < max ? max : current;
            int ceiling = AbsoluteMax;

            return value > ceiling ? ceiling : value;
        }

        // ── 一、科技解锁值 ──────────────────────────────────────

        /// <summary>
        /// 在 LDBTool 的 PreAddDataAction 阶段改写集装相关科技的解锁值。
        /// 面板滑条上限由此现算，因此会自动跟随。
        /// </summary>
        internal static void ModifyPilerTeches()
        {
            int max = Target;

            if (max <= 0) return;

            // 函数 29 是逐级累加、基础值 1，所以各级之和应为 max - 1
            List<TechProto> stationTeches = TechesWithFunction(FuncStationPilerLevel);

            if (stationTeches.Count == 0)
                ProjectEdenPlugin.Log.LogWarning("找不到物流塔集装科技（解锁函数 29），集装层数将保持原样");

            int total = max - 1;

            for (var i = 0; i < stationTeches.Count; i++)
            {
                int value = total / stationTeches.Count + (i == stationTeches.Count - 1 ? total % stationTeches.Count : 0);

                SetUnlockValue(stationTeches[i], FuncStationPilerLevel, value);
            }

            // 函数 39 / 41 是直接赋值，逐级递增，最后一级达到上限。
            // **这里用的是分拣器自己的上限，不是 max**——见 InserterAbsoluteMax 的注释。
            int inserterMax = InserterTarget;

            ModifyAssignTeches(FuncInserterStackOutput, inserterMax);
            ModifyAssignTeches(FuncInserterStackInput, inserterMax);

            ProjectEdenPlugin.Log.LogInfo(
                $"集装科技解锁值已改写：传送带集装 {max} 层，分拣器堆叠 {inserterMax} 层" +
                (inserterMax < max
                    ? $"（分拣器一次抓的是「垛」不是「件」，{inserterMax} 垛 × {max} 层已经顶满 itemCount 的 Int16）"
                    : ""));
        }

        private static void ModifyAssignTeches(int func, int max)
        {
            List<TechProto> teches = TechesWithFunction(func);

            for (var i = 0; i < teches.Count; i++)
            {
                int value = 1 + (max - 1) * (i + 1) / teches.Count;

                SetUnlockValue(teches[i], func, value);
            }
        }

        private static List<TechProto> TechesWithFunction(int func)
        {
            var result = new List<TechProto>();

            foreach (TechProto tech in LDB.techs.dataArray)
            {
                if (tech?.UnlockFunctions == null) continue;

                foreach (int f in tech.UnlockFunctions)
                {
                    if (f != func) continue;

                    result.Add(tech);

                    break;
                }
            }

            result.Sort((a, b) => a.ID.CompareTo(b.ID));

            return result;
        }

        private static void SetUnlockValue(TechProto tech, int func, double value)
        {
            int length = tech.UnlockFunctions.Length < tech.UnlockValues.Length
                ? tech.UnlockFunctions.Length
                : tech.UnlockValues.Length;

            for (var i = 0; i < length; i++)
                if (tech.UnlockFunctions[i] == func)
                    tech.UnlockValues[i] = value;
        }

        // ── 二、采矿机 / 抽水站往传送带吐货的堆叠上限 ──────────

        /// <summary>
        /// MinerComponent 往传送带吐货时把堆叠上限硬编码成了 4：
        ///     int stack = (x - 0.01) / 1800 + 1;
        ///     stack = stack >= 4 ? 4 : (stack &lt; 1 ? 1 : stack);
        /// 两处 4 都替换成当前集装科技等级，这样采矿机、抽水站就和物流站、
        /// 巨型建筑用同一套集装层数，而不是各自为政。
        ///
        /// 这段代码是三条采集分支共用的，所以矿脉、原油、抽水一并受益。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(MinerComponent), nameof(MinerComponent.InternalUpdate))]
        private static IEnumerable<CodeInstruction> MinerComponent_InternalUpdate(IEnumerable<CodeInstruction> instructions)
        {
            var matcher = new CodeMatcher(instructions);

            // 用 1800 这个除数作锚点定位到堆叠上限那一段
            matcher.MatchForward(false, new CodeMatch(i => i.opcode == OpCodes.Ldc_I4 && i.operand is int v && v == 1800));

            if (matcher.IsInvalid)
            {
                ProjectEdenPlugin.Log.LogError("MinerComponent.InternalUpdate 没找到堆叠上限那一段，采矿机 / 抽水站仍是 4 层");

                return matcher.InstructionEnumeration();
            }

            // 先在算出层数之后直接覆盖。原版这个式子只看 period 和 miningSpeed：
            //     rate = 36000000 / period * miningSpeed;
            //     矿脉分支再乘 veinCount，原油分支再乘储量，抽水分支没有任何乘数
            //     stack = (int)(rate - 0.01) / 1800 + 1
            // MinerComponent.speed 完全不参与，所以本 mod 提速再多，抽水站也只算出 2。
            // 光抬高上限没用——2 < 50 时钳制根本不触发，必须改算出来的值本身。
            CodeInstruction storeStack = matcher.InstructionAt(4); // ldc.i4 1800; div; ldc.i4.1; add; stloc

            if (storeStack.opcode == OpCodes.Stloc_S || storeStack.opcode == OpCodes.Stloc)
            {
                matcher.Advance(5).InsertAndAdvance(
                    new CodeInstruction(OpCodes.Ldloc_S, storeStack.operand),
                    new CodeInstruction(OpCodes.Call, RaiseStackMethod),
                    new CodeInstruction(storeStack.opcode, storeStack.operand));

                matcher.Start();
                matcher.MatchForward(false, new CodeMatch(i => i.opcode == OpCodes.Ldc_I4 && i.operand is int v2 && v2 == 1800));
            }
            else
            {
                ProjectEdenPlugin.Log.LogWarning("没能定位堆叠层数的局部变量，只调整上限");
            }

            var replaced = 0;

            // 紧随其后的两个 ldc.i4.4：一个是比较值、一个是赋值
            for (var i = 0; i < 2; i++)
            {
                matcher.MatchForward(false, new CodeMatch(OpCodes.Ldc_I4_4));

                if (matcher.IsInvalid) break;

                matcher.Set(OpCodes.Call, GetMaxPilerStackMethod);
                matcher.Advance(1);

                replaced++;
            }

            if (replaced < 2)
                ProjectEdenPlugin.Log.LogError($"采矿机堆叠上限只替换了 {replaced} 处（应为 2 处），行为可能不一致");
            else
                ProjectEdenPlugin.Log.LogInfo("采矿机 / 抽水站往传送带吐货的堆叠上限已改为跟随集装科技");

            return matcher.InstructionEnumeration();
        }

        // ── 三、自动集装机的硬编码 4 ────────────────────────────

        private static readonly FieldInfo CacheCargoStack1 =
                                              AccessTools.Field(typeof(PilerComponent), nameof(PilerComponent.cacheCargoStack1)),
                                          CacheCargoStack2 =
                                              AccessTools.Field(typeof(PilerComponent), nameof(PilerComponent.cacheCargoStack2));

        private static readonly MethodInfo GetMaxPilerStackMethod =
                                               AccessTools.Method(typeof(PilerLevelPatches), nameof(GetMaxPilerStack)),
                                           GetMaxPilerStackFloatMethod =
                                               AccessTools.Method(typeof(PilerLevelPatches), nameof(GetMaxPilerStackFloat)),
                                           RaiseStackMethod =
                                               AccessTools.Method(typeof(PilerLevelPatches), nameof(RaiseStack));

        /// <summary>
        /// <c>PilerComponent.InternalUpdate</c> 把集装等级硬编码成 4，一共<b>四处</b>，
        /// 而且必须<b>一起</b>替换，少一处就会凭空吞货或凭空造货：
        /// <code>
        ///   @0312  if (stack1 + stack2 &lt;= 4)        // 合并判定：够不够一整堆
        ///   float  b = inc / stack * 4f + 0.5f        // 吐出那堆应带多少增产点数
        ///   @0359  AddCargo(item, 4, b)               // **吐出的层数**
        ///   @0371  cacheCargoStack1 = 总数 - 4        // **从缓存扣掉的层数**
        /// </code>
        ///
        /// <b>第一版只替换了前两处，后果是玩家报的「集装机吞货物 + 数字变负」。</b>
        /// 集装等级 5000 时：点数按 5000 层算出来并从缓存扣掉，层数却只吐 4、只扣 4——
        /// 点数比层数快 1250 倍被抽干，<c>cacheCargoInc1</c> 冲成负数。
        /// 等级等于 4 时四处一致，所以这个 bug 在原版数值下根本不存在，
        /// 只有把等级调走才会现形。
        ///
        /// 所以这里<b>不做顺序匹配，直接全方法扫</b>：这个方法里的每一个 4 都是集装等级
        /// （已逐条核对过，整数 3 个、浮点 1 个），数量对不上就响——
        /// 游戏更新改了形状时，宁可不改也别改一半。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(PilerComponent), nameof(PilerComponent.InternalUpdate))]
        private static IEnumerable<CodeInstruction> PilerComponent_InternalUpdate(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            // 原版是 3 个整数 4 加 1 个浮点 4f。
            //
            // **品质搬运层接通之后，浮点那个会变成两个，而且两个都必须替换。** 原版那句
            // `b = inc / stack * 4f + 0.5f` 算的是「吐出来那一叠该带多少增产点数」，里面的
            // 4f 就是**吐出来的层数**（和 `AddCargo(item, 4, b)` 里的 4 是同一个数）；
            // 品质那条孪生语句 `bq = qua / stack * 4f + 0.5f` 算的是同一叠该带多少品质分，
            // 那个 4f 当然也是同一个层数。只改一个就会让品质按 4 层算而货按 5000 层吐。
            //
            // 所以期望值跟着品质是否接通走，而不是写死——写死的话，接通品质之后这条守卫
            // 每次启动都报错，而它报的其实是一件正确的事，真出问题时反倒没人信它了。
            int expectInt = 3;
            int expectFloat = Patches.QualityWidening.TransportWired ? 2 : 1;

            var ints = 0;
            var floats = 0;

            foreach (CodeInstruction ins in code)
            {
                if (ins.opcode == OpCodes.Ldc_I4_4)
                {
                    // 原地改，保住可能落在它身上的跳转标签
                    ins.opcode = OpCodes.Call;
                    ins.operand = GetMaxPilerStackMethod;

                    ints++;

                    continue;
                }

                if (ins.opcode == OpCodes.Ldc_R4 && ins.operand is float f && f == 4f)
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = GetMaxPilerStackFloatMethod;

                    floats++;
                }
            }

            if (ints != expectInt || floats != expectFloat)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"自动集装机：集装等级常量找到 {ints} 个整数 / {floats} 个浮点，预期 {expectInt} / {expectFloat}。" +
                    "形状和核对过的不一致，**已按找到的全部替换**，但请重新核对 PilerComponent.InternalUpdate——" +
                    "四处只改一部分会导致吐出的层数和扣掉的层数对不上，表现为吞货物或凭空造货。");

                return code;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"自动集装机的集装等级已接管（整数 {ints} 处、浮点 {floats} 处，全部跟随集装科技）");

            return code;
        }
    }
}
