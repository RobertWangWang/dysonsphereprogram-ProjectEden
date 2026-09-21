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

            int wanted = cycles - 1;

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

            int cycles = MegaBuildingRegistry.Config?.cyclesPerTick ?? 1;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑产出闸：已改写 {_rewritten} 处（应当 {ExpectedSites} 处）。"
                + $"原版按配方类型把每 tick 的结算数卡在 装配 10 / 其余 20，"
                + $"现在巨型建筑一律放到 {cycles}——装配类 ×{cycles / 10.0:0.#}、其余 ×{cycles / 20.0:0.#}。"
                + "普通装配机拿的仍是原值。冶铸熔炉走的是另一条加法闸，本来就没被它卡住，没有改。");
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
