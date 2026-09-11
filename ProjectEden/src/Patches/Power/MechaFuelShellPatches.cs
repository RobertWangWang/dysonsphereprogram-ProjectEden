using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让本 mod 的「满蓄电器」在机甲里烧完之后<b>把空壳还给玩家</b>，和原版一样。
    ///
    /// <b>问题在哪。</b> <c>Mecha.GenerateEnergy</c> 取下一块燃料之前有这么一段：
    /// <code>
    /// if (this.reactorItemId == 2207) {          // 刚烧完的是「蓄电器（满）」
    ///     player.TryAddItemToPackage(2206, 1, reactorItemInc, ...);   // 还一个空壳
    ///     UIItemup.Up(2206, 1);
    /// }
    /// </code>
    /// <b>2206 / 2207 是写死的</b>，所以克隆出来的满蓄电器烧完直接消失——
    /// 一次白扔掉一整个蓄电器的造价。原版那个「烧完还壳」的语义必须补上。
    ///
    /// <b>改法：把读到的值换掉，不碰真实数据。</b> 和 MegaStationWindowPatches / HubCourierPatches
    /// 一个路子——
    /// <list type="bullet">
    /// <item>在 <c>ldfld reactorItemId</c> 之后插一次 <see cref="NormalizeFullId"/>：
    /// 本 mod 的满版本一律报成 2207，于是原版那个 <c>== 2207</c> 判断照常成立；</item>
    /// <item>两处 <c>ldc.i4 2206</c> 换成 <see cref="ShellIdFor"/>，按当前燃料查出配对的空壳。</item>
    /// </list>
    /// <see cref="ShellIdFor"/> 是<b>无状态</b>的：它现读 <c>mecha.reactorItemId</c>，
    /// 而那个字段要到本段之后（IL 0x01F8）才被换成新燃料，此刻仍是刚烧完的那块满电池。
    /// </summary>
    [HarmonyPatch(typeof(Mecha), nameof(Mecha.GenerateEnergy))]
    internal static class MechaFuelShellPatches
    {
        /// <summary>原版蓄电器（满）/ 蓄电器。原版燃料仍然走原来的路径</summary>
        private const int VanillaFull = 2207;
        private const int VanillaEmpty = 2206;

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var normalize = AccessTools.Method(typeof(MechaFuelShellPatches), nameof(NormalizeFullId));
            var shell = AccessTools.Method(typeof(MechaFuelShellPatches), nameof(ShellIdFor));
            var reactorItemId = AccessTools.Field(typeof(Mecha), nameof(Mecha.reactorItemId));

            if (normalize == null || shell == null || reactorItemId == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "机甲燃料空壳补丁：解析不出目标方法或字段，未改写。满蓄电器烧完不会返还空壳");

                return code;
            }

            var normalized = 0;
            var shells = 0;

            // 倒着走：插入会改变后面的下标，从尾部改就不用维护偏移
            for (int i = code.Count - 1; i >= 0; i--)
            {
                CodeInstruction ins = code[i];

                // ① 还壳时写死的 2206 → 按当前燃料查出的空壳。
                //    就地改这条指令的 opcode/operand 而不是换对象，免得丢掉挂在它上面的跳转标签
                if (Value(ins) == VanillaEmpty)
                {
                    ins.opcode = OpCodes.Ldarg_0;
                    ins.operand = null;

                    code.Insert(i + 1, new CodeInstruction(OpCodes.Call, shell));

                    shells++;

                    continue;
                }

                // ② reactorItemId 读出来之后先过一次映射，让原版的 == 2207 对我们也成立
                if (i + 1 < code.Count && ins.opcode == OpCodes.Ldfld
                                       && Equals(ins.operand, reactorItemId)
                                       && Value(code[i + 1]) == VanillaFull)
                {
                    code.Insert(i + 1, new CodeInstruction(OpCodes.Call, normalize));

                    normalized++;
                }
            }

            if (normalized == 0 || shells == 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"机甲燃料空壳补丁：匹配失败（映射点 {normalized}，还壳点 {shells}，期望 1 和 2）。" +
                    "满蓄电器在机甲里烧完不会返还空壳——原版的 IL 形状可能变了");

                return code;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"机甲燃料空壳补丁已生效：映射点 {normalized} 处，还壳点 {shells} 处");

            return code;
        }

        /// <summary>
        /// 取 ldc.i4* 携带的整数值，不是这类指令就返回 <c>int.MinValue</c>（一个不可能的常量值）。
        ///
        /// <b>注意 ldc.i4.s 的 operand 是 sbyte、ldc.i4.0~8 根本没有 operand</b>——
        /// 直接 <c>operand is int</c> 判断会漏掉它们，这个坑在自定义矿脉那边扫「硬编码 15」时踩过。
        /// 这里只需要认 2206 / 2207 这种大数，所以两条就够。
        /// </summary>
        private static int Value(CodeInstruction ins)
        {
            if (ins.opcode == OpCodes.Ldc_I4) return (int)ins.operand;
            if (ins.opcode == OpCodes.Ldc_I4_S) return (sbyte)ins.operand;

            return int.MinValue;
        }

        /// <summary>本 mod 的满版本一律报成原版的 2207，其余原样放行。</summary>
        internal static int NormalizeFullId(int reactorItemId) =>
            MachineRegistry.EmptyForFull(reactorItemId) > 0 ? VanillaFull : reactorItemId;

        /// <summary>当前烧的这块满电池配对的空壳。不是本 mod 的就还原版的 2206。</summary>
        internal static int ShellIdFor(Mecha mecha)
        {
            int empty = MachineRegistry.EmptyForFull(mecha?.reactorItemId ?? 0);

            return empty > 0 ? empty : VanillaEmpty;
        }
    }
}
