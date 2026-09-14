using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 小型速采机：一台<b>产量被钉死</b>的采矿机。
    ///
    /// 它和大型采矿机的关系不是「小一号」，而是<b>另一种曲线</b>：
    /// 大型采矿机跟着「矿物利用」系列科技一路涨，早期弱、后期强；
    /// 这一台<b>从头到尾都是每分钟固定那么多</b>——开局就能用，而且永远不会更快。
    ///
    /// <b>「钉死」是它全部的实现难度。</b> 原版每 tick 的累加是
    /// <code>
    /// time += (int)(power × speedDamper × speed × miningSpeed × veinCount)
    /// </code>
    /// 一个 <c>period</c> 出一颗矿。其中 <c>miningSpeed</c> 是被科技放大过的、
    /// <c>veinCount</c> 是脚下压着几条矿脉——<b>两个都不是常数</b>。
    /// 想让产量和它们都无关，唯一的办法是每 tick 反解 <c>speed</c> 把它们除掉：
    /// <code>
    /// speed = 目标每 tick 产量 × period / (miningSpeed × veinCount)
    /// </code>
    /// 于是乘回去之后 <c>miningSpeed</c> 和 <c>veinCount</c> 正好约掉。
    ///
    /// <b>为什么不能只写 prefabDesc 里的 speed。</b> 那个数在建造时被抄进存档，
    /// 之后每 tick 还要乘 <c>miningSpeed</c>——科技一升产量就跟着涨，
    /// 而面板上的数看着还是对的。这正是本仓库第 3 号坑（界面和逻辑读的不是同一个源）。
    ///
    /// <b>它必须排在 <see cref="AdvancedMinerPatches"/> 之后。</b>
    /// 那一个也前置在同一个方法上，会按自己的规则改 <c>speed</c> / <c>miningRate</c> /
    /// <c>speedDamper</c>。两个前置的相对顺序不写就是不确定的，而症状是
    /// 「产量偶尔对、偶尔跟着科技涨」——所以这里显式取 <c>Priority.Last</c>，
    /// 最后写的那个说了算。
    /// </summary>
    [HarmonyPatch]
    internal static class MiniMinerPatches
    {
        /// <summary>一分钟 3600 tick。</summary>
        private const float TicksPerMinute = 3600f;

        /// <summary>
        /// 写进 <c>MinerComponent.speed</c> 的定值。这个字段的单位是百分之一，
        /// 所以 10000 在采矿面板上读作<b>开采速度 100%</b>——一台定速机器本来就该这么显示。
        /// 真正承担产量比例的是 <c>miningSpeed</c>，见下面为什么。
        /// </summary>
        private const int PanelSpeed = 10000;

        private static readonly Dictionary<int, MachineMinerEntry> ByProto =
            new Dictionary<int, MachineMinerEntry>();

        internal static bool Ready => ByProto.Count > 0;

        /// <summary>
        /// 建表。<b>三种状态都打</b>——「没配这种机器」和「这段代码没进 DLL」
        /// 在日志上本来长得一模一样。
        /// </summary>
        internal static void OnPostAddData()
        {
            ByProto.Clear();

            foreach (MachineRegistry.Machine m in MachineRegistry.Machines)
            {
                if (!m.IsMiner) continue;

                MachineMinerEntry cfg = m.Entry.miner;

                if (cfg == null || cfg.oresPerMinute <= 0)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"{m.Entry.displayName} 是 miner 型但没配 miner.oresPerMinute，" +
                        "产量不会被钉死——它会退化成一台跟着科技涨的普通采矿机。");

                    continue;
                }

                ByProto[m.ItemId] = cfg;
            }

            if (ByProto.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo("小型速采机：machines.json 里没有 miner 型的条目。");
                DebugLog = false;

                return;
            }

            DebugLog = false;

            foreach (KeyValuePair<int, MachineMinerEntry> pair in ByProto)
            {
                if (pair.Value.debugLog) DebugLog = true;

                ProjectEdenPlugin.Log.LogInfo(
                    $"小型速采机已就绪：物品 {pair.Key}，固定 {pair.Value.oresPerMinute} 矿/分钟" +
                    $"（≈ {pair.Value.oresPerMinute / TicksPerMinute:0.###} 矿/tick），" +
                    $"不吃采矿速度科技，矿脉{(pair.Value.consumeVeins ? "会" : "不会")}消耗。");
            }

            // 开关状态**两种都打**。只在打开时说话的诊断，关着的时候和「这段代码根本没进 DLL」
            // 在日志上长得一模一样——本仓库已经为这个形状付过好几次往返了。
            ProjectEdenPlugin.Log.LogInfo(DebugLog
                ? "小型速采机：逐台状态行**已开**，每台每 10 秒一行。" +
                  "查完把 machines.json 里 miner.debugLog 改回 false。"
                : "小型速采机：逐台状态行未开（machines.json 的 miner.debugLog = false）。" +
                  "产量对不上时把它打开，一次启动就能分清是哪一段。");
        }

        private static MachineMinerEntry Find(ref MinerComponent miner, PlanetFactory factory)
        {
            if (ByProto.Count == 0 || factory?.entityPool == null) return null;

            int entityId = miner.entityId;

            if (entityId <= 0 || entityId >= factory.entityPool.Length) return null;

            return ByProto.TryGetValue(factory.entityPool[entityId].protoId, out MachineMinerEntry cfg)
                ? cfg
                : null;
        }

        /// <summary>
        /// 把这一台的产量钉死。<b>Priority.Last</b>：AdvancedMinerPatches 也前置在这里，
        /// 它按自己的规则写过的 speed / miningRate / speedDamper 在这里被覆盖掉。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(MinerComponent), nameof(MinerComponent.InternalUpdate))]
        private static void MinerComponent_InternalUpdate_Prefix(ref MinerComponent __instance,
            PlanetFactory factory, float power, ref float miningRate, ref float miningSpeed)
        {
            MachineMinerEntry cfg = Find(ref __instance, factory);

            if (cfg == null) return;

            // **读档之后 CreateEntityLogicComponents 不会再跑**（存档是直接还原组件池的），
            // 所以那张 pcId 表在读档后是空的——功率覆盖和「参考速率」的换算都会静默失效。
            // 在这里补登记一次是自愈的：建造、读档、蓝图粘贴三条路都覆盖到了。
            if (__instance.pcId > 0 && !PcOfMini.ContainsKey(__instance.pcId))
                PcOfMini[__instance.pcId] = cfg;

            // 矿脉不消耗。0 是「这一 tick 一点储量都不扣」，不是「扣得少」。
            if (!cfg.consumeVeins) miningRate = 0f;

            // **节流关掉。** 原版按缓存/仓位占比把速度降到 2%，那是给「产量会溢出」
            // 设计的背压；这一台的产量是个常数，让它随仓位起伏就不叫「钉死」了。
            // 仓位满了自然会停——StationComponent.UpdateVeinCollection 第一句就是
            // 「本地供应量 ≥ max 就直接返回」，那一道闸在这之外，不受影响。
            __instance.speedDamper = 1f;

            // 每 tick 要累加多少 time 才刚好是目标产量
            float wantPerTick = cfg.oresPerMinute / TicksPerMinute * __instance.period;

            // **反解的是 miningSpeed（float），不是 speed（int）。**
            //
            // 两个都能把科技和矿脉数除掉，但 speed 是整数：把整个比例塞进它，
            // 矿脉压得越多、科技越高，除出来的数越小，截断误差就越大——
            // 实测量级是「10 条矿脉 + 满级科技」时低 10%。
            // 一台卖点是「产量钉死」的机器，产量随矿脉数浮动 10% 就不叫钉死了。
            //
            // 所以 speed 固定成 10000（这个字段的单位是百分之一，10000 = 100%，
            // 面板上的「开采速度」因此读作 100%，对一台定速机器来说这正是该显示的），
            // 由浮点的 miningSpeed 去承担那个比例。
            __instance.speed = PanelSpeed;

            // 乘回去正好抵消：
            //   time += power × 1 × PanelSpeed × miningSpeed × veins
            //         = power × 每 tick 目标 time
            // ——科技放大过的那个 miningSpeed 在这一行被整个替换掉了，
            // 这就是第 8 条要的「不享受采矿利用率的科技加成」。
            float solved = Solve(ref __instance, cfg);

            if (solved <= 0f) return;

            float techSpeed = miningSpeed;

            miningSpeed = solved;

            ReportOnce(ref __instance, cfg, techSpeed, __instance.veinCount, miningSpeed);

            if (DebugLog) BeginTick(ref __instance, power, techSpeed, miningSpeed, wantPerTick);
        }

        /// <summary>
        /// 反解出这台机器这一 tick 该用的 <c>miningSpeed</c>。
        ///
        /// <b>结算和面板共用这一个方法，这不是顺手而是必须。</b> 面板要显示的产量
        /// 和机器实际产的产量是同一个事实，两处各写一遍公式就一定会分叉——本仓库
        /// 为「同一个事实被手工抄成两份」已经付过好几次账（图标颜色和建筑色调、
        /// README 里的巨型建筑座数）。
        ///
        /// 用 <c>miner.speed</c> 而不是 <see cref="PanelSpeed"/> 当除数，是为了
        /// 「乘回去正好抵消」这句话对**当前实际写进去的那个 speed** 成立，而不是
        /// 对一个常量成立。
        /// </summary>
        private static float Solve(ref MinerComponent miner, MachineMinerEntry cfg)
        {
            int veins = miner.veinCount > 0 ? miner.veinCount : 1;
            float divisor = (float)miner.speed * veins;

            if (divisor <= 0f) return 0f;

            return cfg.oresPerMinute / TicksPerMinute * miner.period / divisor;
        }

        // ── 面板读数 ──────────────────────────────────────────

        /// <summary>
        /// 面板算产量用的是**另一条公式**，而且它读的不是我们改的那个参数：
        /// <code>
        /// 每分钟 = 60 × (600000/period) × (speed/10000) × speedDamper × power
        ///        × GameMain.history.miningSpeedScale     ← 字段，不是参数
        ///        × veinCount
        /// </code>
        /// 结算那一行读的是 <c>ldarg.s miningSpeed</c>（<c>InternalUpdate</c> IL 0049），
        /// 我们替换的正是这个参数——**面板走的是字段，永远看不见那次替换**，
        /// 于是一台实产 10000 的机器在面板上写着 1080（= 100% × 科技 1.5 × 12 条矿脉）。
        /// 这就是本仓库第 3 号坑：界面和逻辑读的不是同一个源。
        ///
        /// 两条公式除了这一项之外**逐项相同**，所以只要把这一项换成这台机器实际用的值，
        /// 面板立刻就对了，不必重算任何别的东西。
        /// </summary>
        private static float _displayScale;

        /// <summary>
        /// 插在 <c>ldfld miningSpeedScale</c> 之后。不是我们的机器时原样返回，
        /// 所以对其它采矿机完全透明。
        /// </summary>
        internal static float MapDisplayScale(float scale) => _displayScale > 0f ? _displayScale : scale;

        /// <summary>
        /// 三个面板的前置都调它。
        ///
        /// <b>为什么用前置定值、而不是把采矿机压到栈上传给 <see cref="MapDisplayScale"/>。</b>
        /// 三个面板都有 <c>_minerId</c> + <c>factory</c> 两个字段，前置里一句就查到了；
        /// 而在 IL 里现取采矿机要按方法逐个找它是第几个局部、是值还是托管指针，
        /// 三处形状还不一样——那是能静默错、且游戏一更新就会错的那种代码。
        ///
        /// 面板都在主线程上跑，所以一个普通静态字段就够；<b>前置无条件写它</b>
        /// （不是我们的机器就写 0），所以不会有上一台的值漏给下一台。
        /// </summary>
        private static void SetDisplayScale(int minerId, PlanetFactory factory)
        {
            _displayScale = 0f;

            if (ByProto.Count == 0 || minerId <= 0) return;

            MinerComponent[] pool = factory?.factorySystem?.minerPool;

            if (pool == null || minerId >= pool.Length) return;

            ref MinerComponent miner = ref pool[minerId];

            if (miner.id != minerId) return;

            MachineMinerEntry cfg = Find(ref miner, factory);

            if (cfg == null) return;

            _displayScale = Solve(ref miner, cfg);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIVeinCollectorPanel), "_OnUpdate")]
        private static void UIVeinCollectorPanel_OnUpdate_Prefix(UIVeinCollectorPanel __instance)
            => SetDisplayScale(__instance._minerId, __instance.factory);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIControlPanelVeinCollectorPanel), "_OnUpdate")]
        private static void UIControlPanelVeinCollectorPanel_OnUpdate_Prefix(
            UIControlPanelVeinCollectorPanel __instance)
            => SetDisplayScale(__instance._minerId, __instance.factory);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIMinerWindow), "_OnUpdate")]
        private static void UIMinerWindow_OnUpdate_Prefix(UIMinerWindow __instance)
            => SetDisplayScale(__instance._minerId, __instance.factory);

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIVeinCollectorPanel), "_OnUpdate")]
        private static IEnumerable<CodeInstruction> UIVeinCollectorPanel_OnUpdate_Transpiler(
            IEnumerable<CodeInstruction> instructions)
            => MapScaleAtRateSite(instructions, "UIVeinCollectorPanel._OnUpdate");

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIControlPanelVeinCollectorPanel), "_OnUpdate")]
        private static IEnumerable<CodeInstruction> UIControlPanelVeinCollectorPanel_OnUpdate_Transpiler(
            IEnumerable<CodeInstruction> instructions)
            => MapScaleAtRateSite(instructions, "UIControlPanelVeinCollectorPanel._OnUpdate");

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIMinerWindow), "_OnUpdate")]
        private static IEnumerable<CodeInstruction> UIMinerWindow_OnUpdate_Transpiler(
            IEnumerable<CodeInstruction> instructions)
            => MapScaleAtRateSite(instructions, "UIMinerWindow._OnUpdate");

        private static readonly MethodInfo MapDisplayScaleMethod =
            AccessTools.Method(typeof(MiniMinerPatches), nameof(MapDisplayScale));

        private static readonly FieldInfo MiningSpeedScaleField =
            AccessTools.Field(typeof(GameHistoryData), nameof(GameHistoryData.miningSpeedScale));

        /// <summary>
        /// 每个面板都读**两次** <c>miningSpeedScale</c>，只能改其中一次。
        ///
        /// 另一次在「预计可采 X 小时」那一段，它是拿采矿速率去**除**的——
        /// 在那里换成 13.9 会让预估时间凭空缩短一个数量级。而且这台机器
        /// <c>miningRate=0</c>、矿脉根本不消耗，那段本来就不适用于它，
        /// 更不该被我们改得更离谱。
        ///
        /// 判据取自公式本身而不是位置：产量那一段是从 <c>ldc.r8 0.0001</c>
        /// （即 <c>speed/10000</c>）起头的，「预计可采」那一段没有这个常数。
        /// 实测三个面板都正好各命中 1 处，命中数不等于 1 就整段放弃并报错——
        /// 改错一处比不改坏得多。
        /// </summary>
        private static IEnumerable<CodeInstruction> MapScaleAtRateSite(
            IEnumerable<CodeInstruction> instructions, string where)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);

            // 先解析、解析不到就原样返回。绝不把 null 当操作数发射出去——
            // 那会活过转译、活过编译，到 Harmony 写方法体时才炸，栈上指的还是别人。
            if (MapDisplayScaleMethod == null || MiningSpeedScaleField == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"小型速采机：{where} 的面板读数改写放弃——解析不到" +
                    $"{(MapDisplayScaleMethod == null ? " MapDisplayScale" : "")}" +
                    $"{(MiningSpeedScaleField == null ? " miningSpeedScale" : "")}。" +
                    "机器产量不受影响，只是面板上的每分钟会偏小。");

                return code;
            }

            int patched = 0;

            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldfld) continue;
                if (!ReferenceEquals(code[i].operand, MiningSpeedScaleField) &&
                    (code[i].operand as FieldInfo) != MiningSpeedScaleField) continue;

                if (!HasSpeedDivisorBefore(code, i)) continue;

                // 插在后面：ldfld 自己身上的标签不动，新指令不带标签，跳转目标不受影响。
                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, MapDisplayScaleMethod));
                patched++;
                i++;
            }

            if (patched != 1)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"小型速采机：{where} 里「产量那一处」的 miningSpeedScale 命中 {patched} 次，" +
                    "预期 1 次——游戏可能改过这个面板。整段放弃改写，面板会显示偏小的每分钟，" +
                    "机器实际产量不受影响。");

                return instructions;
            }

            ProjectEdenPlugin.Log.LogInfo($"小型速采机：{where} 的每分钟读数已接管 1 处");

            return code;
        }

        // ── 参考速率 / 理论产能 ───────────────────────────────

        /// <summary>
        /// 这两个面板和上面三个不一样：科技倍率在**进入采矿机循环之前**就被取进局部变量，
        /// 全星球的采矿机共用那一个数。所以不能在取值处替换——那会把这一台的数
        /// 算到别的采矿机头上。必须在循环**内部**、拿到具体那台机器的地方换。
        ///
        /// 好在那三处用它的地方形状完全一样，而且采矿机就在旁边：
        /// <code>
        /// ldc.r8 3600 ; ldloc 采矿机 ; ldfld period ; conv.r8 ; div
        /// ldloc 科技倍率      ← 在这后面插
        /// mul
        /// ldloc 采矿机 ; ldfld speed ...
        /// </code>
        /// 那个采矿机局部实测就是 <c>MinerComponent&amp;</c>，所以把它那条 <c>ldloc</c>
        /// 原样复制一份压栈，就是现成的 <c>ref</c> 实参，不必去数它是第几个局部、
        /// 也不必判断它是值还是托管指针。
        ///
        /// 三处分别是矿脉 / 原油 / 水的分支；本机器只可能是矿脉那一支，
        /// 另外两处照样改是无害的（不是我们的机器就原样返回），而且**三处一起改
        /// 才能让命中数成为一个可断言的常数**。
        /// </summary>
        internal static double MapReferenceScale(double scale, ref MinerComponent miner)
        {
            if (PcOfMini.Count == 0) return scale;

            // 按 pcId 认机器：这两个方法都拿不到 PlanetFactory，查不了 protoId。
            if (!PcOfMini.TryGetValue(miner.pcId, out MachineMinerEntry cfg) || cfg == null) return scale;

            float solved = Solve(ref miner, cfg);

            return solved > 0f ? solved : scale;
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIReferenceSpeedTip), nameof(UIReferenceSpeedTip.AddEntryDataWithFactory))]
        private static IEnumerable<CodeInstruction> UIReferenceSpeedTip_Transpiler(
            IEnumerable<CodeInstruction> instructions)
            => MapScaleInMinerLoop(instructions, "UIReferenceSpeedTip.AddEntryDataWithFactory");

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(ProductionExtraInfoCalculator), nameof(ProductionExtraInfoCalculator.CalculateFactory))]
        private static IEnumerable<CodeInstruction> ProductionExtraInfoCalculator_Transpiler(
            IEnumerable<CodeInstruction> instructions)
            => MapScaleInMinerLoop(instructions, "ProductionExtraInfoCalculator.CalculateFactory");

        private static readonly MethodInfo MapReferenceScaleMethod =
            AccessTools.Method(typeof(MiniMinerPatches), nameof(MapReferenceScale));

        private static readonly FieldInfo MinerSpeedField =
            AccessTools.Field(typeof(MinerComponent), nameof(MinerComponent.speed));

        private static IEnumerable<CodeInstruction> MapScaleInMinerLoop(
            IEnumerable<CodeInstruction> instructions, string where)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);

            if (MapReferenceScaleMethod == null || MinerSpeedField == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"小型速采机：{where} 的换算改写放弃——解析不到" +
                    $"{(MapReferenceScaleMethod == null ? " MapReferenceScale" : "")}" +
                    $"{(MinerSpeedField == null ? " MinerComponent.speed" : "")}。" +
                    "机器产量不受影响，只是这个面板上的速率会偏小。");

                return code;
            }

            int patched = 0;

            for (int i = 0; i + 4 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Div) continue;
                if (!IsLdloc(code[i + 1])) continue;
                if (code[i + 2].opcode != OpCodes.Mul) continue;
                if (!IsLdloc(code[i + 3])) continue;
                if ((code[i + 4].operand as FieldInfo) != MinerSpeedField) continue;

                // 复制采矿机那条 ldloc（新建实例，不带标签，跳转目标不受影响）
                CodeInstruction loadMiner = new CodeInstruction(code[i + 3].opcode, code[i + 3].operand);

                code.InsertRange(i + 2, new[]
                {
                    loadMiner,
                    new CodeInstruction(OpCodes.Call, MapReferenceScaleMethod),
                });

                patched++;
                i += 4;
            }

            if (patched != 3)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"小型速采机：{where} 里采矿机速率的换算点命中 {patched} 处，预期 3 处" +
                    "（矿脉 / 原油 / 水各一）——游戏可能改过这个算法。整段放弃改写，" +
                    "这个面板会显示偏小的速率，机器实际产量不受影响。");

                return instructions;
            }

            ProjectEdenPlugin.Log.LogInfo($"小型速采机：{where} 的速率换算已接管 3 处");

            return code;
        }

        private static bool IsLdloc(CodeInstruction ins) =>
            ins.opcode == OpCodes.Ldloc || ins.opcode == OpCodes.Ldloc_S ||
            ins.opcode == OpCodes.Ldloc_0 || ins.opcode == OpCodes.Ldloc_1 ||
            ins.opcode == OpCodes.Ldloc_2 || ins.opcode == OpCodes.Ldloc_3;

        /// <summary>产量那一段以 <c>0.0001</c>（= speed/10000）起头，「预计可采」那段没有。</summary>
        private static bool HasSpeedDivisorBefore(List<CodeInstruction> code, int index)
        {
            for (int j = index - 1; j >= 0 && j >= index - 30; j--)
                if (code[j].opcode == OpCodes.Ldc_R8 && code[j].operand is double d &&
                    System.Math.Abs(d - 0.0001) < 1e-12)
                    return true;

            return false;
        }

        // ── 逐台状态行 ────────────────────────────────────────

        /// <summary>
        /// machines.json 里任何一条 miner 打开了 <c>debugLog</c>。
        /// 关着的时候 <see cref="BeginTick"/> / 后置里的整段都不跑。
        /// </summary>
        private static bool DebugLog;

        /// <summary>
        /// <b>不能用 <c>Time.realtimeSinceStartup</c> 计时。</b> 这条路跑在
        /// <c>_miner_parallel</c> 的工作线程上，而 Unity 的那个属性只能在主线程调，
        /// 在别的线程上会直接抛。<c>Stopwatch</c> 是线程安全的。
        ///
        /// （也不能用 <c>GameMain.gameTick</c>：换存档时它会往回跳，
        /// 于是「下次报告时刻」永远不会到来，诊断静默死掉——本仓库
        /// <c>CargoLedgerProbe</c> 栽过这一条。）
        /// </summary>
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private const long WindowMs = 10000;

        /// <summary>一台机器一个窗口的累计。每台只被自己星球那一个线程碰。</summary>
        private sealed class Probe
        {
            internal long WindowStartMs;
            internal bool Armed;

            internal int TimeBefore;
            internal int ProductBefore;

            internal int Ticks;
            internal long Produced;
            internal long TimeIncrement;

            internal float Power;
            internal float TechSpeed;
            internal float Solved;
            internal float WantPerTick;
        }

        /// <summary>
        /// <b>并发容器。</b> 同一颗星球上的采矿机由同一个线程处理，但不同星球是
        /// 不同线程同时在写这张表（本仓库第 4 号坑）。
        /// </summary>
        private static readonly ConcurrentDictionary<int, Probe> Probes =
            new ConcurrentDictionary<int, Probe>();

        private static void BeginTick(ref MinerComponent miner, float power,
            float techSpeed, float solved, float wantPerTick)
        {
            int entityId = miner.entityId;

            if (entityId <= 0) return;

            if (!Probes.TryGetValue(entityId, out Probe p))
            {
                p = new Probe { WindowStartMs = Clock.ElapsedMilliseconds };
                p = Probes.GetOrAdd(entityId, p);
            }

            p.TimeBefore = miner.time;
            p.ProductBefore = miner.productCount;
            p.Power = power;
            p.TechSpeed = techSpeed;
            p.Solved = solved;
            p.WantPerTick = wantPerTick;
            p.Armed = true;
        }

        /// <summary>
        /// 结算之后读一次真实的终态。
        ///
        /// <b>要量的是「每 tick 真正累加了多少 time」</b>，而不是我们写进去了什么——
        /// 写进去的值日志里早就有了，它证明不了那个值到达了乘法那一行。
        /// 原版把 <c>time</c> 按产出件数扣掉（<c>time -= period × 产出</c>，IL 03E2），
        /// 所以本 tick 的真实累加量要把扣掉的那部分加回来：
        /// <code>
        /// 累加量 = (结算后 time + period × 本 tick 产出) − 结算前 time
        /// </code>
        /// 这个数是整件事的判据：
        /// <list type="bullet">
        /// <item>≈ <c>speed × 科技速率 × 矿脉数</c>（本例 18 万）→ 我们写的值没到乘法那一行；</item>
        /// <item>≈ 目标值（本例 167 万）但产量还是不够 → 乘法对了，卡在后面；</item>
        /// <item>≈ 0 → 前面某道闸（没电、<c>time &gt; period</c>、缓存满）一直在拦。</item>
        /// </list>
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MinerComponent), nameof(MinerComponent.InternalUpdate))]
        private static void MinerComponent_InternalUpdate_Postfix(ref MinerComponent __instance,
            PlanetFactory factory)
        {
            if (!DebugLog) return;

            int entityId = __instance.entityId;

            if (entityId <= 0 || !Probes.TryGetValue(entityId, out Probe p) || !p.Armed) return;

            p.Armed = false;

            int produced = __instance.productCount - p.ProductBefore;

            if (produced < 0) produced = 0;

            p.Ticks++;
            p.Produced += produced;
            p.TimeIncrement += (long)__instance.time + (long)__instance.period * produced - p.TimeBefore;

            long now = Clock.ElapsedMilliseconds;
            long elapsed = now - p.WindowStartMs;

            if (elapsed < WindowMs) return;

            // **抢占在拼字符串之前。** 这一段跑在 tick 路径上，本仓库禁止在那里分配。
            p.WindowStartMs = now;

            int ticks = p.Ticks;
            long producedSum = p.Produced;
            long incSum = p.TimeIncrement;

            p.Ticks = 0;
            p.Produced = 0;
            p.TimeIncrement = 0;

            if (ticks <= 0) return;

            Report(ref __instance, factory, p, ticks, producedSum, incSum, elapsed);
        }

        private static void Report(ref MinerComponent miner, PlanetFactory factory, Probe p,
            int ticks, long produced, long incSum, long elapsedMs)
        {
            double perMinute = produced * 60000.0 / elapsedMs;
            double avgInc = (double)incSum / ticks;

            int slotCount = -1;
            int slotMax = -1;

            int entityId = miner.entityId;
            EntityData[] pool = factory?.entityPool;

            if (pool != null && entityId > 0 && entityId < pool.Length)
            {
                int stationId = pool[entityId].stationId;
                StationComponent[] stations = factory.transport?.stationPool;

                if (stationId > 0 && stations != null && stationId < stations.Length &&
                    stations[stationId]?.storage != null && stations[stationId].storage.Length > 0)
                {
                    slotCount = stations[stationId].storage[0].count;
                    slotMax = stations[stationId].storage[0].max;
                }
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"小型速采机 #{entityId}：最近 {elapsedMs / 1000.0:0.#} 秒实产 {produced} 矿" +
                $"（折合 {perMinute:0} 矿/分钟），结算了 {ticks} tick。\n" +
                $"  power={p.Power:0.###} damper={miner.speedDamper:0.###} speed={miner.speed} " +
                $"veinCount={miner.veinCount} period={miner.period} productId={miner.productId} " +
                $"workstate={miner.workstate} productCount={miner.productCount} " +
                $"储物格={slotCount}/{slotMax}\n" +
                $"  科技给的 miningSpeed={p.TechSpeed:0.#####} → 我们写进去 {p.Solved:0.#####}；" +
                $"每 tick 实际累加 time={avgInc:0}，目标 {p.WantPerTick:0}" +
                $"（若只有约 {miner.speed * (double)p.TechSpeed * miner.veinCount:0}，" +
                "说明写进去的值没到那一行乘法）。");
        }

        /// <summary>
        /// 功率钉死。
        ///
        /// <b>原版的采矿机功率是速度的平方</b>（<c>SetPCState</c> 里
        /// <c>speedDamper × speed² / 1e8</c>），而上面为了钉死产量把 speed 反解成了
        /// 一个随科技变化的数——照原版公式算，功率会跟着科技上下跳，
        /// 而且科技越高 speed 越小、功率反而越低。那讲不通，也不是配置里写的那个数。
        ///
        /// 所以这里直接写 <c>requiredEnergy</c>，绕开那条平方公式。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(MinerComponent), nameof(MinerComponent.SetPCState))]
        private static void MinerComponent_SetPCState_Postfix(ref MinerComponent __instance,
            PowerConsumerComponent[] pcPool)
        {
            if (ByProto.Count == 0 || pcPool == null) return;

            // SetPCState 拿不到 factory，所以按「这台机器的 speed 是我们写的那种」认不出来。
            // 改用 pcId 反查：功耗组件上记着 workEnergyPerTick，那是我们在 prefabDesc 里
            // 设好的值，直接用它当工作功率即可——省掉一次实体查表，也不会认错别的采矿机。
            int pcId = __instance.pcId;

            if (pcId <= 0 || pcId >= pcPool.Length) return;

            if (!PcOfMini.ContainsKey(pcId)) return;

            // **只换量纲，不替原版做「在不在干活」的判断。** 原版刚刚算完的
            // requiredEnergy 是 0 还是正数，已经表达了它这一 tick 有没有在采矿；
            // 照抄那个结论、只把数值换成配置里的功率，比自己再判一遍矿脉、仓位、
            // 电量要稳得多——那几条判据将来一变，自己判的那份就会悄悄和原版分叉。
            pcPool[pcId].requiredEnergy = pcPool[pcId].requiredEnergy > 0
                ? (int)pcPool[pcId].workEnergyPerTick
                : (int)pcPool[pcId].idleEnergyPerTick;
        }

        /// <summary>
        /// 哪些功耗组件属于小型速采机。
        ///
        /// <b>必须是并发容器。</b> 写它的是建造（主线程），读它的是
        /// <c>SetPCState</c>——那条路跑在 <c>_miner_parallel</c> 上，约 31 个工作线程。
        /// 普通 <c>HashSet</c> 在这种读写并发下会在几分钟内损坏（本仓库第 4 号坑，
        /// <c>MegaVirtualLogisticsPatches</c> 在线上炸过一次）。
        /// </summary>
        private static readonly ConcurrentDictionary<int, MachineMinerEntry> PcOfMini =
            new ConcurrentDictionary<int, MachineMinerEntry>();

        /// <summary>
        /// 建造时把这台机器的 pcId 记下来，<c>SetPCState</c> 才认得出它。
        ///
        /// <b>为什么不在 SetPCState 里现查。</b> 那个方法只拿得到 <c>MinerComponent</c>
        /// 和功耗池，没有 <c>PlanetFactory</c>，查不到 protoId；而它每 tick 每台都跑，
        /// 不适合再兜一圈。建造时记一次是最省的。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.CreateEntityLogicComponents))]
        private static void PlanetFactory_CreateEntityLogicComponents(PlanetFactory __instance, int entityId)
        {
            if (ByProto.Count == 0 || __instance?.entityPool == null) return;
            if (entityId <= 0 || entityId >= __instance.entityPool.Length) return;

            ref EntityData entity = ref __instance.entityPool[entityId];

            if (entity.minerId <= 0 || entity.powerConId <= 0) return;
            if (!ByProto.TryGetValue(entity.protoId, out MachineMinerEntry cfg)) return;

            PcOfMini[entity.powerConId] = cfg;
        }

        private static int _reported;

        private static void ReportOnce(ref MinerComponent miner, MachineMinerEntry cfg,
            float techSpeed, int veins, float solved)
        {
            if (Interlocked.Exchange(ref _reported, 1) != 0) return;

            // 一次性日志的抢占要在拼字符串**之前**——这个方法跑在 _miner_parallel 上，
            // 不抢的话每个线程都会拼一遍那串字（本仓库记过的 tick 路径禁分配）。
            ProjectEdenPlugin.Log.LogInfo(
                $"小型速采机第一次结算：period={miner.period}，脚下矿脉 {veins} 条，" +
                $"科技给的采矿速率 {techSpeed:0.###}（**已被整个替换掉**），" +
                $"speed 固定 {miner.speed}（面板 100%），反解出 miningSpeed={solved:0.#####}，" +
                $"折合 {cfg.oresPerMinute} 矿/分钟。科技再升、矿脉再多，这个数都不变——这是它的设计。");
        }
    }
}
