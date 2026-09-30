using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    [HarmonyPatch(typeof(AssemblerComponent), nameof(AssemblerComponent.InternalUpdate))]
    internal static class MegaProliferatorTimingPatches
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var sites = new List<int>();
            var time = AccessTools.Field(typeof(AssemblerComponent), nameof(AssemblerComponent.time));
            var extra = AccessTools.Field(typeof(AssemblerComponent), nameof(AssemblerComponent.extraTime));
            for (int i = 2; i < code.Count; i++)
                if (code[i].opcode == OpCodes.Stfld && (Equals(code[i].operand, time) || Equals(code[i].operand, extra))
                    && code[i-1].opcode == OpCodes.Add && code[i-2].opcode == OpCodes.Conv_I4)
                    sites.Add(i-1);
            if (sites.Count != 2 || !Equals(code[sites[0]+1].operand, time) || !Equals(code[sites[1]+1].operand, extra))
            {
                MegaProliferatorTiming.Enabled = false;
                ProjectEdenPlugin.Log.LogError($"巨型增产计时：应匹配两个推进点，实际{sites.Count}；保留旧逻辑。");
                return code;
            }
            for (int n = sites.Count - 1; n >= 0; n--)
            {
                int at = sites[n];
                var first = new CodeInstruction(OpCodes.Ldarg_0);
                first.labels.AddRange(code[at].labels); code[at].labels.Clear();
                var insert = new List<CodeInstruction> { first };
                if (n == 1) insert.Add(new CodeInstruction(OpCodes.Ldarg_1));
                insert.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MegaProliferatorTiming), n == 0 ? "MainStep" : "ExtraStep")));
                code.InsertRange(at, insert);
            }
            MegaProliferatorTiming.Enabled = true;
            ProjectEdenPlugin.Log.LogInfo("巨型增产计时：按配方周期累计增产进度，加速倍率进入周期预算；普通机器保持原版。");
            return code;
        }
    }
}
