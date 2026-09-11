using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using System.Threading;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 本 mod 新增的矿脉在星球表面用什么颜色画。
    ///
    /// <b>结论：原版着色器做不到「同模型不同色」，所以新矿脉按铁矿脉的样子画。</b>
    ///
    /// 原版把矿种编号塞进 AnimData.state：
    ///     animPool[vein.hashAddress].state = (uint)veinData.type;
    /// 着色器再按这个编号取色。ProjectGenesis 的做法是把 state 当成打包的 RGBA
    /// （a&lt;&lt;24 | b&lt;&lt;16 | g&lt;&lt;8 | r）写进去——<b>但那要配套换掉矿脉的着色器</b>
    /// （它的 SwapShaderPatches 会在 VFPreload.SaveMaterial 阶段把矿脉材质换成自带的
    /// 自定义 shader）。只抄打包那一半的后果是：原版 shader 把 40 多亿当矿种索引读，
    /// 矿脉在地表<b>直接画不出来</b>——数据全在、统计也对，就是地上什么都没有。
    /// 这个坑实测踩过一次。
    ///
    /// 本 mod 不带 AssetBundle，也不想为了染色去替换原版材质，所以退一步：
    /// 把自定义矿脉的 state 报成<b>铁矿脉</b>（矿种 1）。渲染路径和铁矿脉完全一致，
    /// 保证看得见；代价是地表上新矿和铁长得一样，靠标签和面板区分。
    /// 物品图标那一侧是独立的，矿石／锭<b>仍然是染过色的</b>。
    ///
    /// 两个赋值点的栈形状不同，所以是两个签名不同的替换函数，已核对过 IL：
    ///     PlanetModelingManager.LoadingPlanetFactoryMain：ldelema VeinData → 托管指针 → ref 版
    ///     PlanetFactory.AddVeinData(VeinData)            ：ldarg.1        → 值      → 值版
    /// </summary>
    [HarmonyPatch]
    internal static class OreVeinColorPatches
    {
        private static int _logged;

        [HarmonyPatch(typeof(PlanetModelingManager), nameof(PlanetModelingManager.LoadingPlanetFactoryMain))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> LoadingPlanetFactoryMain_Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Replace(instructions, nameof(RefVeinTypeToAnimState), "PlanetModelingManager.LoadingPlanetFactoryMain");

        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.AddVeinData))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> AddVeinData_Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Replace(instructions, nameof(VeinTypeToAnimState), "PlanetFactory.AddVeinData");

        /// <summary>
        /// 把「读 VeinData.type 然后写进 AnimData.state」这一对里的读操作换成我们的函数。
        /// 按指令特征匹配，不按偏移；原地改 opcode/operand，避免丢跳转标签。
        /// </summary>
        private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, string replacementName, string label)
        {
            FieldInfo veinType = AccessTools.Field(typeof(VeinData), nameof(VeinData.type));
            FieldInfo animState = AccessTools.Field(typeof(AnimData), nameof(AnimData.state));
            MethodInfo replacement = AccessTools.Method(typeof(OreVeinColorPatches), replacementName);

            var code = new List<CodeInstruction>(instructions);

            // AccessTools 解析失败会安静地返回 null，之后就会改错指令
            if (veinType == null || animState == null || replacement == null)
            {
                ProjectEdenPlugin.Log.LogError($"{label}：VeinData.type / AnimData.state / {replacementName} 没解析出来，矿脉颜色未接管");

                return code;
            }

            var count = 0;

            for (var i = 0; i < code.Count - 1; i++)
            {
                if (!code[i].LoadsField(veinType)) continue;
                if (!code[i + 1].StoresField(animState)) continue;

                code[i].opcode = OpCodes.Call;
                code[i].operand = replacement;
                count++;
            }

            if (count == 0)
                ProjectEdenPlugin.Log.LogError($"{label}：没找到 VeinData.type → AnimData.state 的赋值，新矿脉会和铁矿脉同色");
            else
                ProjectEdenPlugin.Log.LogInfo($"{label}：矿脉颜色已接管 {count} 处");

            return code;
        }

        // ── 替换函数 ──────────────────────────────────────────
        // public 是因为它们要被生成的 IL 直接调用

        public static uint RefVeinTypeToAnimState(ref VeinData data) => Pack(data.type, data.modelIndex);

        public static uint VeinTypeToAnimState(VeinData data) => Pack(data.type, data.modelIndex);

        /// <summary>
        /// <b>modelIndex &gt; 2 的矿脉外观来自它自己的材质</b>，不走「按矿种编号取色」那条路——
        /// 原版的可燃冰、分形硅石这些特殊矿脉就是这么画的。新矿脉用的是克隆并染过色的专属模型
        /// （见 OreRegistry.CloneVeinModel），ID 必然大于 2，所以这里<b>照原版把矿种编号传下去</b>
        /// 就行，颜色由材质决定。
        ///
        /// 只有在模型克隆失败、退回铁矿脉那个通用矿堆模型（ID ≤ 2）时才需要兜底：
        /// 那条路按矿种编号查色表，矿种 15 不在表里，直接传会什么都画不出来，所以报成铁。
        /// </summary>
        private static uint Pack(EVeinType type, short modelIndex)
        {
            if (!OreRegistry.IsCustomVein((int)type)) return (uint)type;

            // 专属模型：走原版路径，外观由材质给
            if (modelIndex > 2) return (uint)type;

            if (Interlocked.Exchange(ref _logged, 1) == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    "新矿脉用的是通用矿堆模型（克隆专属模型失败？），只能按铁矿脉的外观渲染——" +
                    "原版着色器按矿种编号取色，矿种 15 不在色表里，照实传会画不出来。");

            return (uint)EVeinType.Iron;
        }
    }
}
