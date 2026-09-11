using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把「矿种循环」的上界从写死的 15 抬到实际矿种数。
    ///
    /// <b>这是新矿种能不能生成、能不能显示的总闸门。</b> 原型注册好只是让 LDB 里多了一条记录；
    /// 真正决定矿脉铺不铺、面板列不列的是这些形如
    ///     for (int type = 1; type &lt; 15; type++)
    /// 的循环——15 就是原版 EVeinType.Max。自定义矿种（15 起）刚好卡在界外，
    /// 于是矿脉一颗不生成，星球详情面板自然也就没有它。
    ///
    /// 上界取 <see cref="PlanetModelingManager.veinProtos"/> 的长度，不写死 16：
    /// 这个数组是 PrepareWorks 按 LDB.veins 最后一个原型的 ID + 1 分配的，
    /// GenerateVeins 里那三个 spots/counts/opacity 数组也是按它的长度分配的，
    /// 所以用它当上界，索引范围天然对齐，以后再加矿种也不用改这里。
    ///
    /// 面板那两处不会越界：UIPlanetDetail / UIStarDetail 的 veinCounts、veinAmounts
    /// 都是 new [64]，而且循环体里 LDB.veins.Select(type) 为空就跳过、
    /// 条目是 GetEntry() 动态取的 List，不是定长控件数组。
    /// </summary>
    [HarmonyPatch]
    internal static class OreVeinRangePatches
    {
        /// <summary>
        /// 五个 GenerateVeins（基类 + 四个特殊星球算法）各有一处，
        /// 两个详情面板各有一处。都是同一个指令特征。
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlanetAlgorithm), nameof(PlanetAlgorithm.GenerateVeins));
            yield return AccessTools.Method(typeof(PlanetAlgorithm7), nameof(PlanetAlgorithm7.GenerateVeins));
            yield return AccessTools.Method(typeof(PlanetAlgorithm11), nameof(PlanetAlgorithm11.GenerateVeins));
            yield return AccessTools.Method(typeof(PlanetAlgorithm12), nameof(PlanetAlgorithm12.GenerateVeins));
            yield return AccessTools.Method(typeof(PlanetAlgorithm13), nameof(PlanetAlgorithm13.GenerateVeins));
            yield return AccessTools.Method(typeof(UIPlanetDetail), nameof(UIPlanetDetail.OnPlanetDataSet));
            yield return AccessTools.Method(typeof(UIStarDetail), nameof(UIStarDetail.OnStarDataSet));
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo bound = AccessTools.Method(typeof(OreVeinRangePatches), nameof(VeinTypeBound));

            var code = new List<CodeInstruction>(instructions);

            if (bound == null)
            {
                ProjectEdenPlugin.Log.LogError("矿种循环上界：VeinTypeBound 没解析出来，补丁未生效");

                return code;
            }

            var count = 0;

            for (var i = 0; i < code.Count - 1; i++)
            {
                // 只认「常量 15 紧跟一个比较跳转」，别的 15 一律不碰
                // ——RefreshDynamicProperties 里也有 15，那是珍奇矿的判定，动了会出错
                if (!code[i].LoadsConstant(15)) continue;
                if (!IsRangeBranch(code[i + 1].opcode)) continue;

                // 原地改，保住可能挂在这条指令上的跳转标签
                code[i].opcode = OpCodes.Call;
                code[i].operand = bound;
                count++;
            }

            string name = original.DeclaringType?.Name + "." + original.Name;

            if (count == 0)
                ProjectEdenPlugin.Log.LogError($"{name}：没找到写死的矿种上界 15，新矿脉在这里不会生效");
            else
                ProjectEdenPlugin.Log.LogInfo($"{name}：矿种循环上界已接管 {count} 处");

            return code;
        }

        /// <summary>只有序关系的分支才是循环上界；相等判断另有含义，不能碰。</summary>
        private static bool IsRangeBranch(OpCode opcode) =>
            opcode == OpCodes.Blt || opcode == OpCodes.Blt_S ||
            opcode == OpCodes.Bge || opcode == OpCodes.Bge_S ||
            opcode == OpCodes.Blt_Un || opcode == OpCodes.Blt_Un_S ||
            opcode == OpCodes.Bge_Un || opcode == OpCodes.Bge_Un_S;

        /// <summary>
        /// 矿种循环的新上界。永远不小于原版的 15，避免任何情况下反而比原版少跑。
        /// </summary>
        public static int VeinTypeBound()
        {
            VeinProto[] protos = PlanetModelingManager.veinProtos;

            return protos == null || protos.Length < 15 ? 15 : protos.Length;
        }
    }
}
