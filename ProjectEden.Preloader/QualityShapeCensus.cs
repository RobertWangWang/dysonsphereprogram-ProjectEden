using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ProjectEden.Preloader
{
    /// <summary>
    /// 物品品质 · 1c 的<b>形状普查</b>。只统计，不改任何东西。
    ///
    /// <b>它回答的是一个决定 1c 可不可行的问题。</b> 孪生一处载荷访问不是复制一条指令：
    /// <c>ldfld Cargo::inc</c> 压栈之后，周围可能是 <c>inc1 + inc2</c>、
    /// 也可能是 <c>inc * n / count</c>，孪生必须镜像<b>整个表达式</b>。
    /// 通用做法是表达式子图复制——那是一趟编译器级的活。
    ///
    /// 但如果实际出现的语句形状只有那么十几二十种，1c 就退化成
    /// 「按形状匹配 + 遇到没见过的形状就整个不改」，而那是本仓库做惯了的事。
    /// <b>所以先数形状，再决定怎么写。</b>
    ///
    /// 做法：把方法体按<b>栈深归零</b>切成语句，取出含载荷访问的那些，
    /// 把指令序列归一化成一个签名，然后做直方图。
    /// 栈深在分支目标处重置——这是个近似，但普查只需要知道数量级和分布，
    /// 不需要精确到每一条；真正动手时的判据会是精确匹配。
    /// </summary>
    internal static class QualityShapeCensus
    {
        internal class Report
        {
            internal readonly List<string> Notes = new List<string>();

            internal int Statements;
            internal int Accesses;
            internal int UnsplitMethods;

            /// <summary>形状签名 → 出现次数</summary>
            internal readonly Dictionary<string, int> Shapes = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>每个形状的一个例子，便于核对</summary>
            internal readonly Dictionary<string, string> Example = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>
            /// 把<b>寻址前缀</b>归一化掉之后的形状 → 次数。
            ///
            /// 完整形状有 139 种，但发散的是「怎么走到那个对象」
            /// （<c>ldarg</c> / <c>ldarg ldfld ldloc ldelem</c> / <c>ldloc ldloc ldelem</c>…），
            /// 而寻址指令是<b>纯的、可重复执行的</b>——孪生时原样再走一遍就行，不用理解它。
            /// 真正要镜像的是「对载荷做了什么运算」。这一栏就是把前者去掉之后剩下的东西，
            /// 它的种数才是 1c 真正要处理的形状数。
            /// </summary>
            internal readonly Dictionary<string, int> Core = new Dictionary<string, int>(StringComparer.Ordinal);

            internal readonly Dictionary<string, string> CoreExample = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        internal static Report Run(ModuleDefinition module) => Run(module, false);

        /// <param name="mainlineOnly">
        /// 只数主干道那四个载荷（<see cref="QualityFieldAnalyzer.MainlinePayload"/>）。
        /// 阶段 1c 的实际范围就是它们，所以决定「要处理几种形状」的是这个数，
        /// 而不是全局那个。
        /// </param>
        internal static Report Run(ModuleDefinition module, bool mainlineOnly)
        {
            var r = new Report();

            var payload = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
            var blockers = new List<string>();

            QualityFieldAnalyzer.ResolvePayload(module, payload, blockers);

            if (blockers.Count > 0)
            {
                r.Notes.Add("载荷清单解析失败，普查跳过");

                return r;
            }

            if (mainlineOnly)
            {
                var keep = new HashSet<string>(QualityFieldAnalyzer.MainlinePayload, StringComparer.Ordinal);

                foreach (string k in payload.Keys.ToList())
                    if (!keep.Contains(k)) payload.Remove(k);

                r.Notes.Add($"只数主干道：{string.Join("、", payload.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray())}");
            }

            QualityFieldAnalyzer.Report a = QualityFieldAnalyzer.Analyze(module);

            // 只数「纯搬运」那一类：混合的要手工写，显示层的不动，纯效果的根本不碰
            var handled = new HashSet<string>(
                a.MixedFound.Select(x => x.Split(new[] { "  " }, StringSplitOptions.None)[0]),
                StringComparer.Ordinal);

            foreach (TypeDefinition t in AllTypes(module))
            {
                if (t.Name.StartsWith("UI", StringComparison.Ordinal)) continue;

                foreach (MethodDefinition m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    if (handled.Contains(t.FullName + "::" + m.Name)) continue;

                    var code = m.Body.Instructions.ToList();

                    if (!code.Any(i => IsPayload(i, payload))) continue;

                    Census(m, code, payload, r);
                }
            }

            r.Notes.Add(
                $"普查：{r.Accesses} 处载荷访问落在 {r.Statements} 条语句里，" +
                $"共 {r.Shapes.Count} 种不同形状；{r.UnsplitMethods} 个方法没能干净地按栈深切开。");

            return r;
        }

        private static void Census(MethodDefinition m, List<Instruction> code,
            IDictionary<string, FieldDefinition> payload, Report r)
        {
            // 分支目标：到这里栈深重置为 0（近似）
            var targets = new HashSet<int>();

            foreach (Instruction i in code)
            {
                if (i.Operand is Instruction one) targets.Add(one.Offset);
                else if (i.Operand is Instruction[] many)
                    foreach (Instruction x in many) targets.Add(x.Offset);
            }

            var depth = 0;
            var start = 0;
            var clean = true;

            for (var i = 0; i < code.Count; i++)
            {
                if (targets.Contains(code[i].Offset) && depth != 0) { depth = 0; start = i; clean = false; }

                depth += PushCount(code[i]) - PopCount(code[i]);

                if (depth < 0) { depth = 0; clean = false; }

                if (depth != 0) continue;

                // start..i 是一条语句
                int from = start;
                int to = i;

                start = i + 1;

                var hits = 0;

                for (int k = from; k <= to; k++) if (IsPayload(code[k], payload)) hits++;

                if (hits == 0) continue;

                r.Statements++;
                r.Accesses += hits;

                string sig = Signature(code, from, to, payload);

                r.Shapes[sig] = r.Shapes.TryGetValue(sig, out int n) ? n + 1 : 1;

                if (!r.Example.ContainsKey(sig))
                    r.Example[sig] = $"{m.DeclaringType.Name}::{m.Name} @IL_{code[from].Offset:X4}";

                string core = CoreOf(sig);

                r.Core[core] = r.Core.TryGetValue(core, out int c2) ? c2 + 1 : 1;

                if (!r.CoreExample.ContainsKey(core))
                    r.CoreExample[core] = $"{m.DeclaringType.Name}::{m.Name} @IL_{code[from].Offset:X4}";
            }

            if (!clean) r.UnsplitMethods++;
        }

        /// <summary>
        /// 把寻址前缀从形状里去掉，只留「对载荷做了什么」。
        ///
        /// 纯寻址指令（<c>ldarg</c> / <c>ldloc</c> / <c>ldelem</c> / 非载荷的 <c>ldfld</c>）
        /// 是<b>无副作用且可重复执行</b>的：孪生时把同一串再走一遍就能拿到同一个对象，
        /// 完全不需要理解它走的是哪条路。所以它们不该算进「有多少种形状要处理」。
        /// </summary>
        private static string CoreOf(string sig)
        {
            var keep = new List<string>();

            foreach (string tok in sig.Split(' '))
            {
                if (tok == "ldarg" || tok == "ldloc" || tok == "ldelem" || tok == "ldfld" ||
                    tok == "ldsfld" || tok == "ldloca" || tok == "ldflda" || tok == "conv")
                    continue;

                keep.Add(tok);
            }

            return keep.Count == 0 ? "(空)" : string.Join(" ", keep.ToArray());
        }

        /// <summary>把一条语句归一化成可比较的形状签名。</summary>
        private static string Signature(List<Instruction> code, int from, int to,
            IDictionary<string, FieldDefinition> payload)
        {
            var sb = new StringBuilder();

            for (int i = from; i <= to; i++)
            {
                if (sb.Length > 0) sb.Append(' ');

                sb.Append(Norm(code[i], payload));
            }

            return sb.ToString();
        }

        private static string Norm(Instruction i, IDictionary<string, FieldDefinition> payload)
        {
            string n = i.OpCode.Name;

            if (n.StartsWith("ldarg", StringComparison.Ordinal)) return "ldarg";
            if (n.StartsWith("ldloca", StringComparison.Ordinal)) return "ldloca";
            if (n.StartsWith("ldloc", StringComparison.Ordinal)) return "ldloc";
            if (n.StartsWith("stloc", StringComparison.Ordinal)) return "stloc";
            if (n.StartsWith("ldc", StringComparison.Ordinal)) return "ldc";
            if (n.StartsWith("conv", StringComparison.Ordinal)) return "conv";
            if (n.StartsWith("ldelem", StringComparison.Ordinal)) return "ldelem";
            if (n.StartsWith("stelem", StringComparison.Ordinal)) return "stelem";
            if (n.StartsWith("br", StringComparison.Ordinal) || n.StartsWith("b", StringComparison.Ordinal) &&
                (n.Contains("eq") || n.Contains("ne") || n.Contains("lt") || n.Contains("gt") ||
                 n.Contains("le") || n.Contains("ge"))) return "branch";

            if (i.Operand is FieldReference fr)
            {
                bool pay = payload.ContainsKey(fr.DeclaringType.FullName + "::" + fr.Name);

                if (n.StartsWith("ldflda", StringComparison.Ordinal)) return pay ? "ldflda:PAY" : "ldflda";
                if (n.StartsWith("ldfld", StringComparison.Ordinal)) return pay ? "ldfld:PAY" : "ldfld";
                if (n.StartsWith("stfld", StringComparison.Ordinal)) return pay ? "stfld:PAY" : "stfld";
                if (n.StartsWith("ldsfld", StringComparison.Ordinal)) return pay ? "ldsfld:PAY" : "ldsfld";
                if (n.StartsWith("stsfld", StringComparison.Ordinal)) return pay ? "stsfld:PAY" : "stsfld";
            }

            if (i.Operand is MethodReference mr && (n == "call" || n == "callvirt" || n == "newobj"))
                return "call:" + mr.Name;

            return n;
        }

        private static bool IsPayload(Instruction i, IDictionary<string, FieldDefinition> payload) =>
            i.Operand is FieldReference fr &&
            payload.ContainsKey(fr.DeclaringType.FullName + "::" + fr.Name);

        // ── 栈深：够用即可的近似 ──────────────────────────────

        private static int PopCount(Instruction i)
        {
            switch (i.OpCode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Popi:
                case StackBehaviour.Popref:
                case StackBehaviour.Pop1: return 1;
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi:
                case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8:
                case StackBehaviour.Popref_popi_popref: return 3;
                case StackBehaviour.Varpop:
                    if (!(i.Operand is MethodReference m)) return 0;

                    int n = m.Parameters.Count;

                    if (m.HasThis && i.OpCode != OpCodes.Newobj) n++;

                    return n;
                default: return 0;
            }
        }

        private static int PushCount(Instruction i)
        {
            switch (i.OpCode.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1:
                case StackBehaviour.Pushi:
                case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8:
                case StackBehaviour.Pushref: return 1;
                case StackBehaviour.Push1_push1: return 2;
                case StackBehaviour.Varpush:
                    if (!(i.Operand is MethodReference m)) return 0;

                    if (i.OpCode == OpCodes.Newobj) return 1;

                    return m.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
                default: return 0;
            }
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module) =>
            module.Types.SelectMany(WithNested);

        private static IEnumerable<TypeDefinition> WithNested(TypeDefinition t)
        {
            yield return t;

            foreach (TypeDefinition n in t.NestedTypes)
            foreach (TypeDefinition x in WithNested(n))
                yield return x;
        }
    }
}
