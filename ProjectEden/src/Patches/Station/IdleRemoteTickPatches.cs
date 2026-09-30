using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches.Station
{
    // 空船站仍须补曲速器、维护泊位和优先级锁；仅绕过飞行参数及在途船循环。
    [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.InternalTickRemote))]
    internal static class IdleRemoteTickPatches
    {
        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => Rewrite(instructions, generator, typeof(StationComponent));

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator, Type stationType)
        {
            var codes = new List<CodeInstruction>(instructions);
            var max = AccessTools.Field(stationType, "warperMaxCount");
            var render = AccessTools.Method(stationType, "ShipRenderersOnTick");
            int gate = -1, tail = -1, hits = 0, renders = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].LoadsField(max) && i + 1 < codes.Count &&
                    (codes[i + 1].opcode == OpCodes.Bge || codes[i + 1].opcode == OpCodes.Bge_S) && codes[i + 1].operand is Label)
                {
                    var label = (Label)codes[i + 1].operand;
                    gate = codes.FindIndex(c => c.labels.Contains(label)); hits++;
                }
                if (codes[i].Calls(render)) { renders++; tail = i - 4; }
            }
            // 尾部只接收 this、星体数组和两个引用参数，不依赖被绕过的局部变量。
            bool valid = hits == 1 && renders == 1 && gate > 0 && tail > gate &&
                codes[gate].opcode == OpCodes.Ldc_I4_0 && codes[gate - 1].opcode == OpCodes.Endfinally &&
                codes[gate].blocks.Count == 0 &&
                codes[tail].IsLdarg(0) && codes[tail + 1].IsLdarg(7) &&
                codes[tail + 2].IsLdarg(8) && codes[tail + 3].IsLdarg(9);
            if (!valid)
            {
                ProjectEdenPlugin.Log.LogWarning("星际空转优化：原版边界不匹配，保留原方法。");
                return codes;
            }
            var destination = generator.DefineLabel();
            codes[tail].labels.Add(destination);
            var first = new CodeInstruction(OpCodes.Ldarg_0);
            first.labels.AddRange(codes[gate].labels); codes[gate].labels.Clear();
            codes.InsertRange(gate, new[] {
                first,
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(stationType, "workShipCount")),
                new CodeInstruction(OpCodes.Brfalse, destination)
            });
            ProjectEdenPlugin.Log.LogInfo("星际空转优化：无在途运输船时跳过飞行参数与飞行循环，保留曲速器补充、泊位对账/渲染和优先级锁更新。");
            return codes;
        }
    }
}
