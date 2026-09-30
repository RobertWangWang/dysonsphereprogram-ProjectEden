using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
    internal static class BlueprintAssemblerCollisionSkip
    {
        internal static bool NeedsCollider(PrefabDesc desc)
        {
            var c = ProjectEdenPlugin.CheatsConfig;
            // 普通制造建筑的碰撞/覆盖结论本来就被无条件建造清除；连接类和采矿类保留原检查。
            if (c != null && c.enabled && c.noConditionBuild && desc.isAssembler &&
                !desc.isBelt && !desc.isInserter && !desc.multiLevel && desc.addonType == EAddonType.None &&
                !desc.veinMiner && !desc.oilMiner && !desc.isTank && !desc.isStorage && !desc.isLab && !desc.isSplitter)
                return false;
            return desc.hasBuildCollider;
        }
        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var field = AccessTools.Field(typeof(PrefabDesc), nameof(PrefabDesc.hasBuildCollider));
            var hits = code.Where(c => c.LoadsField(field)).ToList();
            if (hits.Count != 1) { ProjectEdenPlugin.Log.LogWarning("蓝图制造建筑碰撞跳过：入口不匹配，保留原检查。"); return code; }
            hits[0].opcode = OpCodes.Call;
            hits[0].operand = AccessTools.Method(typeof(BlueprintAssemblerCollisionSkip), nameof(NeedsCollider));
            ProjectEdenPlugin.Log.LogInfo("蓝图制造建筑碰撞跳过：无条件建造时跳过普通制造建筑的碰撞检查；保留传送带、分拣器、堆叠和采矿连接路径。");
            return code;
        }
    }
}
