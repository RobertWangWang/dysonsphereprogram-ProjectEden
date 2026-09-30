using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;

namespace ProjectEden.Patches.Diagnostics
{
    // 仅蓝图专项探针开启时记录。按IL循环区域排他计时，子循环时间不重复归到父循环。
    [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
    internal static class BlueprintConditionDetailProbe
    {
        internal sealed class Frame
        {
            internal Frame Previous;
            internal long Start, Last, Switches;
            internal int Region;
            internal long[] Ticks;
            internal string[] Names;
        }
        [ThreadStatic] private static Frame _current;
        private static string[] _names = { "循环外/补丁" };
        private struct Loop { internal int Head, Tail; }

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        internal static void Begin(out Frame __state)
        {
            __state = null;
            if (BlueprintPasteProbe.Config?.enabled != true) return;
            var names = _names;
            long now = Stopwatch.GetTimestamp();
            __state = new Frame { Previous = _current, Names = names, Ticks = new long[names.Length], Start = now, Last = now };
            _current = __state;
        }
        internal static void Mark(int region)
        {
            var f = _current;
            if (f == null || region == f.Region || region >= f.Ticks.Length) return;
            long now = Stopwatch.GetTimestamp();
            f.Ticks[f.Region] += now - f.Last;
            f.Last = now; f.Region = region; f.Switches++;
        }
        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        internal static void End(Frame __state, Exception __exception)
        {
            if (__state == null) return;
            long now = Stopwatch.GetTimestamp();
            __state.Ticks[__state.Region] += now - __state.Last;
            _current = __state.Previous; // 先释放上下文，异常和日志不得污染下一次。
            double total = (now - __state.Start) * 1000.0 / Stopwatch.Frequency;
            if (total < BlueprintPasteProbe.Config.SpikeMs() && __exception == null) return;
            var sb = new StringBuilder($"[蓝图条件细分] 总计 {total:0.###} ms；区域切换 {__state.Switches} 次；异常={__exception != null}。排他耗时前12项（包含调用及探针开销）：");
            foreach (int id in Enumerable.Range(0, __state.Ticks.Length).OrderByDescending(i => __state.Ticks[i]).Take(12))
                if (__state.Ticks[id] > 0)
                    sb.Append($"\n  {__state.Names[id]}：{__state.Ticks[id] * 1000.0 / Stopwatch.Frequency:0.###} ms");
            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }

        [HarmonyTranspiler, HarmonyPriority(Priority.Last)]
        internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            // 带异常区域的未知版本不做控制流插桩，避免改变异常边界。
            if (code.Any(c => c.blocks.Count != 0)) return code;
            var labels = new Dictionary<Label, int>();
            for (int i = 0; i < code.Count; i++) foreach (var label in code[i].labels) labels[label] = i;
            var loops = new List<Loop>();
            for (int i = 0; i < code.Count; i++)
                if (code[i].operand is Label target && labels.TryGetValue(target, out int head) && head < i)
                    loops.Add(new Loop { Head = head, Tail = i });
            loops = loops.OrderBy(l => l.Head).ThenByDescending(l => l.Tail).ToList();
            if (loops.Count == 0 || loops.Count > 128)
            {
                ProjectEdenPlugin.Log.LogWarning("蓝图条件细分：循环形状不匹配，保留原方法。");
                return code;
            }
            var names = new List<string> { "循环外/补丁" };
            foreach (var loop in loops)
            {
                // I是当前Harmony指令序号，不是原版IL字节偏移；同时给出末尾条件字段便于定位。
                string fields = string.Join("/", code.Skip(Math.Max(loop.Head, loop.Tail - 12)).Take(Math.Min(13, loop.Tail - loop.Head + 1))
                    .Where(c => c.operand is FieldInfo).Select(c => ((FieldInfo)c.operand).Name).Distinct());
                names.Add($"循环{names.Count}[I{loop.Head}..{loop.Tail};边界={fields}]");
            }
            var region = new int[code.Count];
            for (int i = 0; i < code.Count; i++)
            {
                int width = int.MaxValue;
                for (int j = 0; j < loops.Count; j++)
                    if (loops[j].Head <= i && i <= loops[j].Tail && loops[j].Tail - loops[j].Head < width)
                    { region[i] = j + 1; width = loops[j].Tail - loops[j].Head; }
            }
            var points = new HashSet<int>();
            for (int i = 1; i < code.Count; i++) if (region[i] != region[i - 1]) points.Add(i);
            for (int i = 0; i < code.Count; i++)
            {
                var targets = code[i].operand is Label one ? new[] { one } : code[i].operand as Label[];
                if (targets == null) continue;
                foreach (var label in targets)
                    if (labels.TryGetValue(label, out int to) && region[to] != region[i]) points.Add(to);
            }
            _names = names.ToArray();
            var mark = AccessTools.Method(typeof(BlueprintConditionDetailProbe), nameof(Mark));
            foreach (int at in points.OrderByDescending(i => i))
            {
                var load = new CodeInstruction(OpCodes.Ldc_I4, region[at]);
                load.labels.AddRange(code[at].labels); code[at].labels.Clear();
                code.InsertRange(at, new[] { load, new CodeInstruction(OpCodes.Call, mark) });
            }
            ProjectEdenPlugin.Log.LogInfo($"蓝图条件细分：识别 {loops.Count} 个循环、{points.Count} 个跨区域入口；blueprintprobe.enabled 控制计时。");
            return code;
        }
    }
}
