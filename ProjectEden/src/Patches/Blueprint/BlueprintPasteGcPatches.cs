using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    // 蓝图落地末尾主动全堆回收会随存档堆大小产生停顿；保留运行时自动 GC。
    [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CreatePrebuilds))]
    internal static class BlueprintPasteGcPatches
    {
        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var collect = AccessTools.Method(typeof(GC), nameof(GC.Collect), Type.EmptyTypes);
            int at = -1, hits = 0;
            for (int i = 0; i < codes.Count; i++)
                if (codes[i].Calls(collect)) { at = i; hits++; }
            // 只接受唯一的、紧邻方法返回的无参数回收；版本或其他补丁改形状时保留原逻辑。
            int next = at + 1;
            while (next < codes.Count && codes[next].opcode == OpCodes.Nop) next++;
            if (hits != 1 || next != codes.Count - 1 || codes[next].opcode != OpCodes.Ret || codes[at].blocks.Count != 0)
            {
                ProjectEdenPlugin.Log.LogWarning("蓝图落地回收优化：末尾 GC 形状不匹配，保留原方法。");
                return codes;
            }
            // 原地替换，保留跳转标签，不改物品扣除、建筑连接、事件通知或预建体创建。
            codes[at].opcode = OpCodes.Nop;
            codes[at].operand = null;
            ProjectEdenPlugin.Log.LogInfo("蓝图落地回收优化：取消 CreatePrebuilds 末尾的强制全堆 GC，保留运行时自动回收。");
            return codes;
        }
    }
}
