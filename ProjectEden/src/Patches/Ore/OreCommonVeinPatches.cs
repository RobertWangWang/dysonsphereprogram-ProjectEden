using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让本 mod 新增的矿脉在界面上按<b>普通矿物</b>显示，而不是「珍奇」。
    ///
    /// 原版把矿种分成两类，判据就是编号：<b>1~6 是普通</b>（铁、铜、硅、钛、石、煤），
    /// <b>7 及以后按珍奇处理</b>（原油、可燃冰、分形硅石……）。散落在几处 UI 里：
    /// <code>
    ///   UIStarDetail.OnStarDataSet：            SetInfo(..., type &gt;= 7, ...)   // 高亮
    ///   UIStarDetail.OnStarDataSet：            if (!observed &amp;&amp; type &gt;= 7) 跳过
    ///   UIPlanetDetail.RefreshDynamicProperties：if (refId &gt; 7) 标成「未知珍奇信号」
    /// </code>
    /// 本 mod 的矿种从 15 起，于是一路被当成珍奇。
    ///
    /// <b>改法不是逐个改比较，而是在比较前把编号归一化。</b> 这些判定的形状都是
    /// 「栈上是矿种编号 → 压 7 → 比较」，所以只要在压 7 之前插一次
    /// <see cref="NormalizeVeinType"/>，把它们映射成 6（煤矿，普通类里最大的那个），
    /// 所有 &lt; 7 / &gt; 7 的判定就自动把它当普通矿物。好处是：
    /// 不用逐个判断每处比较到底是「高亮」「跳过」还是「标签」，也不会改到别的矿种。
    ///
    /// 只改「紧跟着比较指令」的那个 7。这几个方法里其他的 7 是
    /// 数字格式化参数、gasSpeeds 下标之类，后面跟的不是比较指令，不会被误伤。
    /// </summary>
    [HarmonyPatch]
    internal static class OreCommonVeinPatches
    {
        /// <summary>归一化成哪个矿种：6 = 煤矿，普通那一类里编号最大的。</summary>
        private const int CommonType = 6;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(UIPlanetDetail), nameof(UIPlanetDetail.OnPlanetDataSet));
            yield return AccessTools.Method(typeof(UIPlanetDetail), nameof(UIPlanetDetail.RefreshDynamicProperties));
            yield return AccessTools.Method(typeof(UIStarDetail), nameof(UIStarDetail.OnStarDataSet));
            yield return AccessTools.Method(typeof(UIStarDetail), nameof(UIStarDetail.RefreshDynamicProperties));
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo normalize = AccessTools.Method(typeof(OreCommonVeinPatches), nameof(NormalizeVeinType));

            var code = new List<CodeInstruction>(instructions);

            if (normalize == null)
            {
                ProjectEdenPlugin.Log.LogError("新矿脉普通化：NormalizeVeinType 没解析出来，补丁未生效");

                return code;
            }

            var count = 0;

            for (var i = 0; i < code.Count - 1; i++)
            {
                if (!code[i].LoadsConstant(7)) continue;
                if (!IsComparison(code[i + 1].opcode)) continue;

                // 栈上此刻是「矿种编号」，在压 7 之前先把它归一化
                code.Insert(i, new CodeInstruction(OpCodes.Call, normalize));

                count++;
                i += 2;
            }

            string name = original.DeclaringType?.Name + "." + original.Name;

            if (count == 0)
                ProjectEdenPlugin.Log.LogWarning($"{name}：没找到「与 7 比较」的珍奇判定，新矿脉在这里仍按珍奇显示");
            else
                ProjectEdenPlugin.Log.LogInfo($"{name}：珍奇判定已接管 {count} 处，新矿脉按普通矿物显示");

            return code;
        }

        private static bool IsComparison(OpCode opcode) =>
            opcode == OpCodes.Clt || opcode == OpCodes.Clt_Un ||
            opcode == OpCodes.Cgt || opcode == OpCodes.Cgt_Un ||
            opcode == OpCodes.Ceq ||
            opcode == OpCodes.Blt || opcode == OpCodes.Blt_S ||
            opcode == OpCodes.Ble || opcode == OpCodes.Ble_S ||
            opcode == OpCodes.Bgt || opcode == OpCodes.Bgt_S ||
            opcode == OpCodes.Bge || opcode == OpCodes.Bge_S ||
            opcode == OpCodes.Beq || opcode == OpCodes.Beq_S ||
            opcode == OpCodes.Bne_Un || opcode == OpCodes.Bne_Un_S;

        /// <summary>
        /// 只动本 mod 加的矿种，原版矿种原样返回——原版的珍奇分类一点不受影响。
        /// public 是因为要被生成的 IL 直接调用。
        /// </summary>
        public static int NormalizeVeinType(int type) =>
            OreRegistry.IsCustomVein(type) ? CommonType : type;
    }
}
