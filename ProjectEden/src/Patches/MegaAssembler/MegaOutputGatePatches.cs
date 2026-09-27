// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 巨型建筑真正的产量天花板——<b>原版的产出闸</b>，不是速度，也不是 <c>cyclesPerTick</c>。
    ///
    /// <para><b>先说清楚这两个旋钮为什么是死的，否则很容易再去拧它们。</b></para>
    /// <list type="bullet">
    /// <item><c>assemblerSpeed</c> 是 1e8，早就比任何配方的 <c>timeSpend</c> 高一两个数量级，
    /// 一次调用必定填满，再高只是更浪费的整数。</item>
    /// <item><c>cyclesPerTick</c> 是 60，而十六座巨型建筑里<b>只有冶铸熔炉一座能到 60</b>，
    /// 其余十五座被下面这个闸卡在 10 或 20。</item>
    /// </list>
    ///
    /// <para><b>闸在哪（IL 重读确认，不是从注释里抄的）。</b>
    /// <c>AssemblerComponent.InternalUpdate</c> @0138–0184 按 <c>recipeType</c> 三分：</para>
    /// <code>
    /// 0142: if (recipeType == 1) goto Smelt
    /// 0147: if (recipeType == 4) goto Assemble
    /// 014A: else                 goto 其余
    ///
    /// Smelt     0159: produced[0] + productCounts[0] &lt;= 100   否则 return 0   -> 上限 ~100/单次产量
    /// Assemble  016B: produced[0] &lt;= productCounts[0] *  9    否则 return 0   -> 上限 10
    /// 其余      017E: produced[0] &lt;= productCounts[0] * 19    否则 return 0   -> 上限 20
    /// </code>
    ///
    /// <para><b>枚举过再写，这是本仓库的规矩，而且这次正好清清爽爽。</b> 全方法里 9 / 19 / 100
    /// 一共出现 <b>9 次</b>，按形状分类是 <b>7 处乘法闸 + 2 处 Smelt 加法闸 + 0 处其他用途</b>
    /// （单产物路径 3 处、多产物路径 6 处——原版把多产物那段展开了）。
    /// 所以匹配数 <b>必须是 7</b>，不是 7 就一处都不改。</para>
    ///
    /// <para><b>Smelt 那两处故意不动。</b> 它的闸是「加法」形状，上限 = 100 ÷ 单次产量，
    /// 常见的单次产量 1 时是 100，已经高于 <c>cyclesPerTick</c>——也就是说冶铸熔炉本来就被
    /// <c>cyclesPerTick</c> 卡着而不是被闸卡着，改它没有收益，却要多算一层「乘以单次产量」。
    /// 少改一半就少一半出错面。</para>
    ///
    /// <para><b>这个方法服务着存档里每一台普通装配机，所以不能盲改常量。</b>
    /// 判据用本仓库一贯的那个：<c>speed &gt;= megaSpeedThreshold</c>。普通装配机拿回原值，
    /// 一个字节都不受影响。</para>
    ///
    /// <para><b>而且只抬不降。</b> 闸是个天花板，不是目标值；真要减产有 <c>cyclesPerTick</c>、
    /// 日照缩放和分频三个现成的旋钮，让天花板也去做节流阀只会多出一条互相打架的路径。</para>
    ///
    /// <para><b>抬了之后下一个瓶颈是背压，这一点要说在前面。</b> 这个闸同时是 <c>produced[]</c>
    /// 的背压——「闸 20」的意思就是产物缓冲攒到 20 倍单次产量就停手。抬到 60 意味着缓冲要能装三倍，
    /// 而下游是物流站槽位和取货速度。所以这次抬的是「机器算得慢」，抬完之后卡住的会是「货运不走」。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class MegaOutputGatePatches
    {
        /// <summary>按形状数出来的乘法闸处数。对不上就一处都不改。</summary>
        private const int ExpectedSites = 7;

        private static int _rewritten = -1;

        /// <summary>
        /// 供 IL 调用：把原版的闸系数换成巨型建筑该用的那个。
        ///
        /// 栈形状是 <c>[produced[j], productCounts[j], 系数]</c>，我们在系数之后插
        /// <c>ldarg.0 ; call</c>，于是这里拿到 <c>(系数, ref 组件)</c> 并原位换掉系数。
        ///
        /// 原版系数是「上限 − 1」（<c>produced &lt;= counts × 9</c> 意味着最多 10 个周期），
        /// 所以目标系数是 <c>cyclesPerTick − 1</c>。
        /// </summary>
        internal static int Scale(int vanilla, ref AssemblerComponent component)
        {
            // 普通装配机原样返回。判据和 MegaTick 认巨型建筑用的是同一个，
            // 所以「哪些机器算巨型」这件事全仓库只有一个答案。
            if (component.speed < MegaBuildingRegistry.MegaSpeedThreshold) return vanilla;

            int cycles = MegaBuildingRegistry.Config?.cyclesPerTick ?? 1;

            if (cycles < 1) cycles = 1;

            // **全局分频开着时闸门要跟着放大。** 每 G 个 tick 才轮到一次，轮到时要跑 G 倍周期
            // （MegaThrottle.CyclesFor 的补偿），闸门不放大就会把那 G 倍卡回 1 倍——
            // 表现是「开了全局分频，产能掉成 1/G」，而配置上看不出任何理由。
            // 闸门只是天花板，真正的循环上限在 RunExtraCycles，所以放大它不会让谁多跑。
            int wanted = cycles * MegaThrottle.GlobalDivider - 1;

            // 只抬不降：闸是天花板，减产另有三个旋钮（cyclesPerTick / 日照 / 分频）
            return wanted > vanilla ? wanted : vanilla;
        }

        /// <summary>
        /// 开机状态行。转译器自己会报改写处数，但那行在 <c>PatchAll</c> 里打，
        /// <b>分不清「补丁没挂上」和「挂上了但一处没匹配」</b>——后者会走 loud-fail，
        /// 前者则一行都没有。所以这里再读一次 Harmony 自己的补丁表，报的是<b>已生效状态</b>。
        /// </summary>
        internal static void Report()
        {
            var attached = false;

            foreach (MethodBase patched in Harmony.GetAllPatchedMethods())
            {
                if (patched.DeclaringType != typeof(AssemblerComponent) ||
                    patched.Name != nameof(AssemblerComponent.InternalUpdate)) continue;

                // 写全名：HarmonyLib.Patches 和本仓库自己的 ProjectEden.Patches 命名空间同名
                HarmonyLib.Patches info = Harmony.GetPatchInfo(patched);

                if (info?.Transpilers == null) continue;

                foreach (Patch p in info.Transpilers)
                    if (p.PatchMethod?.DeclaringType == typeof(MegaOutputGatePatches))
                        attached = true;
            }

            if (!attached)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "巨型建筑产出闸：**补丁没挂上**，巨型建筑仍被原版卡在每 tick 10 / 20 个周期。");

                return;
            }

            // **报的是闸实际抬到哪，不是 cyclesPerTick。** 全局分频开着时两者差 G 倍，
            // 而这一行曾经直接印 cyclesPerTick——那就成了这个数的又一份手抄件。
            int cycles = (MegaBuildingRegistry.Config?.cyclesPerTick ?? 1) * MegaThrottle.GlobalDivider;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑产出闸：已改写 {_rewritten} 处（应当 {ExpectedSites} 处）。"
                + $"原版按配方类型把每 tick 的结算数卡在 装配 10 / 其余 20，"
                + $"现在巨型建筑一律放到 {cycles}——装配类 ×{cycles / 10.0:0.#}、其余 ×{cycles / 20.0:0.#}。"
                + "普通装配机拿的仍是原值。冶铸熔炉走的是另一条加法闸，本来就没被它卡住，没有改。");
        }

        private static readonly ConcurrentDictionary<int, byte> _stallReported =
            new ConcurrentDictionary<int, byte>();

        /// <summary>
        /// 上一次结算<b>被产出闸拒绝过</b>吗——也就是「产物出不去，这台机器本该停着」。
        ///
        /// <para><b>为什么需要问这个：产出闸的拒绝路径会留下一个过期的标记，而我们的节流
        /// 恰好会去读它。</b> 重读 IL（不是从注释里抄的）：</para>
        /// <code>
        /// 0101: if (time &lt; timeSpend) goto 0383      ← Hold / Suppress 把 time 压成负数，走的就是这条
        /// 0129: replicating = false                   ← **先**抹标记
        /// 0138-02E8: 产出闸，拒绝时 `ldc.i4.0 ; ret`   ← **再**判闸，既不恢复标记也不扣 time
        /// 0375: time -= timeSpend
        /// 0383: if (replicating) goto 0555            ← 这里读到的是那个过期的 false
        /// 038E: 扣一整份原料
        /// 054E: replicating = true
        /// </code>
        ///
        /// <para><b>纯原版是自洽的</b>：拒绝时 <c>time</c> 没被扣，所以下一次调用照样从 0101
        /// 进结算块、再被拒一次，**永远走不到 0383**。那个过期的 false 没有读者。</para>
        ///
        /// <para><b>而 <see cref="MegaThrottle.Hold"/> 把 <c>time</c> 写成
        /// <c>-speedOverride - 1</c> 正好就是通往 0383 的那条路。</b> 于是产物槽满的巨型建筑
        /// 每个压制 tick 都会<b>为一个产物永远发不出去的周期再扣一次料</b>——
        /// 原料凭空消失、产量一件不涨，而且<b>一个字都不报</b>。
        /// <c>globalTickDivider</c> 默认 2，所以每座巨型建筑每隔一 tick 就来一次；
        /// 离线复现（<c>tools/sim_throttle.py</c>）：7000 tick 白吃 <b>3379</b> 份原料，约 29 份/秒。
        /// 玩家报的就是这个：「物流站属性存满了产物，巨型建筑还是会继续生产，不会停止」。</para>
        ///
        /// <para><b>修法是「不插手」，不是「把标记补回去」。</b> 这一 tick 不压、不放、不补跑，
        /// 原版自己那一次调用就会照原版的方式重新被拒——也就是原版在产物出不去时的正确行为。
        /// 补写 <c>replicating = true</c> 同样能止住扣料，但它会让 <see cref="MegaThrottle.Release"/>
        /// 继续往 <c>extraTime</c> 里推增产进度，而 <c>RewindExtra</c> 每次倒回一整个
        /// <c>extraSpeed</c>、一个周期却只该分到 <c>share</c>（远小于 extraSpeed），于是每个放行
        /// tick 净掉一次差额；<c>extraTime</c> 是<b>存档字段</b>，那个负值会永久留下并让
        /// <c>MegaBatchSettle.CanBatch</c> 把这台永久踢出批量结算（同一个坑在
        /// <c>RewindExtra</c> 上实际发作过：生产设施 14 ms 变 210 ms）。离线量到的漂移是
        /// −6.7e11。**不写字段就没有这一类后果**，所以选不插手。</para>
        ///
        /// <para><b>判据是实测末态，不是复现闸门条件</b>——复现会随原版多一道闸而失准
        /// （钻头那条教训）。「!replicating 且 time ≥ timeSpend」只有闸拒绝这一种收尾会留下：
        /// 缺料留下的是 <c>time == 0</c>（IL 03E6）、刚建好的是 0、被压住的是约 −1、
        /// 正常跑着的 <c>replicating</c> 恒为 true。</para>
        ///
        /// <para><b>烧料那两座不受影响。</b> <c>MegaTick</c> 只跳过节流和补跑周期，
        /// <c>RedoxBurnerPatches.Burn</c> / <c>FusionBurnerPatches.Burn</c> 照常跑——
        /// 对它们来说燃料舱<b>就是</b>产物的出口，跳掉就等于把这两座永久锁死。</para>
        /// </summary>
        internal static bool SettleRefused(PlanetFactory factory, ref AssemblerComponent component)
        {
            // 正常跑着的机器这个标记恒为 true（IL 054E 在扣料之后置上），
            // 所以绝大多数调用在这一句就返回，tick 路径上不多花钱。
            if (component.replicating) return false;

            RecipeExecuteData data = component.recipeExecuteData;

            if (data == null || component.recipeId <= 0 || data.timeSpend <= 0) return false;

            if (component.time < data.timeSpend) return false;

            ReportStallOnce(factory, component.entityId);

            return true;
        }

        /// <summary>
        /// 事件行：**按建筑类型各报一次**，不是全局一次——十七种巨型建筑里只报最先撞上的
        /// 那一种，剩下的是好是坏在日志里查不到（<c>MegaStationPatches</c> 的储物格转储栽过同一条）。
        /// 组装机 tick 跑在 <c>_assembler_parallel</c> 上，所以用 <c>ConcurrentDictionary</c> 领号。
        ///
        /// <b>这一行是这个功能的「它决定了什么」。</b> 没有它，「产物槽满时真的停了」和
        /// 「这段代码根本没进 DLL」在日志里长得一模一样——本仓库为这条付过七次账。
        /// </summary>
        private static void ReportStallOnce(PlanetFactory factory, int entityId)
        {
            EntityData[] pool = factory?.entityPool;

            if (pool == null || entityId <= 0 || entityId >= pool.Length) return;

            int protoId = pool[entityId].protoId;

            if (!_stallReported.TryAdd(protoId, 0)) return;

            string name = LDB.items.Select(protoId)?.name ?? protoId.ToString();

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑产出闸：「{name}」的产物出不去（物流槽位和传送带都满了），"
                + "这一 tick 起停产——不压制、不补跑周期，交给原版自己拒绝结算。"
                + "**期间一份原料都不会再扣**：1.12.16 及以前这里每 2 个 tick 白吃一份、"
                + "约 29 份/秒，而且一个字都不报。把产物取走就自己恢复。"
                + "这一行每种建筑只打一次。");
        }

        /// <summary>
        /// 锚点是四条紧挨着的指令 <c>ldelem.i4 ; ldc.i4.s {9|19} ; mul ; ble*</c>。
        ///
        /// <b>按形状匹配而不是按常量值</b>：9 和 19 这两个数在别处完全可能出现，
        /// 而这个形状（读数组元素、乘、和产物数比、不过就 return 0）只属于产出闸。
        /// 离线对着发出去的程序集数过：命中 7 处，误报 0 处。
        ///
        /// 锚点内没有任何 call，所以品质改写往方法体里插的那些
        /// <c>ldsfld Q ; stloc</c> 落不进来——不需要 <c>SkipChannelNoise</c>。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(AssemblerComponent), nameof(AssemblerComponent.InternalUpdate))]
        private static IEnumerable<CodeInstruction> InternalUpdate_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            MethodInfo scale = AccessTools.Method(typeof(MegaOutputGatePatches), nameof(Scale));

            // 解析不到就原样返回：发 call null 会在 Harmony 的写出阶段炸，
            // 而那个栈跟踪指向的是 Harmony 自己，离出错的这一行十万八千里。
            if (scale == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "巨型建筑产出闸：解析不到 Scale，放弃改写（巨型建筑仍被卡在 10 / 20）");

                _rewritten = 0;

                return code;
            }

            var hits = new List<int>();

            for (var i = 1; i + 2 < code.Count; i++)
            {
                if (!code[i].opcode.Equals(OpCodes.Ldc_I4_S) && !code[i].opcode.Equals(OpCodes.Ldc_I4)) continue;

                int value = Convert(code[i].operand);

                if (value != 9 && value != 19) continue;

                if (!code[i - 1].opcode.Equals(OpCodes.Ldelem_I4)) continue;
                if (!code[i + 1].opcode.Equals(OpCodes.Mul)) continue;
                if (!code[i + 2].opcode.Equals(OpCodes.Ble) && !code[i + 2].opcode.Equals(OpCodes.Ble_S)) continue;

                hits.Add(i);
            }

            if (hits.Count != ExpectedSites)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"巨型建筑产出闸：应当匹配 {ExpectedSites} 处，实际 {hits.Count} 处——"
                    + "**一处都不改**。半套改写比原样留着更难读，而且产量会变成一半建筑一个口径。"
                    + "游戏更新过就重新数一遍（tools 里那个按形状分类的脚本）。");

                _rewritten = 0;

                return code;
            }

            // 从后往前插，前面的下标才不会被顶走
            for (int k = hits.Count - 1; k >= 0; k--)
            {
                int at = hits[k];

                code.Insert(at + 1, new CodeInstruction(OpCodes.Call, scale));
                code.Insert(at + 1, new CodeInstruction(OpCodes.Ldarg_0));
            }

            _rewritten = hits.Count;

            return code;
        }

        /// <summary>
        /// <c>ldc.i4.s</c> 的操作数是 <b>sbyte</b>，<c>ldc.i4</c> 的是 int。
        /// 直接 <c>(int)operand</c> 在前者上会抛 <c>InvalidCastException</c>——
        /// 本仓库为「按 <c>Operand -is [int]</c> 判定」栽过一次，这里按类型转。
        /// </summary>
        private static int Convert(object operand)
        {
            if (operand is sbyte sb) return sb;
            if (operand is byte b) return b;
            if (operand is int n) return n;

            return int.MinValue;
        }
    }
}
