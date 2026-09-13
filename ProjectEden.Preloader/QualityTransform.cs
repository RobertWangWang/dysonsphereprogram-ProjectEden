using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace ProjectEden.Preloader
{
    /// <summary>
    /// 物品品质 · 阶段 1c：把<b>主干道</b>那四个载荷的每一处访问孪生出一份品质的版本。
    /// 完整设计与全部实测数字在 <c>物品品质.md</c>。
    ///
    /// <b>范围是量出来的。</b> 形状普查（<see cref="QualityShapeCensus"/>）显示：全部 27 个载荷
    /// 剥掉寻址之后仍有 74 种核心形状、要四十来个模式才到 95%，那是一趟编译器 pass；
    /// 而主干道四个载荷只有 <b>48 种形状即 100% 覆盖</b>，是一份有限、可枚举的清单。
    ///
    /// <b>概念模型：载荷值只有四个来源、四个去处。</b> 这是从那 48 种形状里读出来的，
    /// 整个变换就建立在它上面：
    /// <list type="table">
    /// <item><term>载荷字段载入</term><description>→ 孪生字段载入</description></item>
    /// <item><term>被跟踪的局部变量</term><description>→ 孪生局部变量</description></item>
    /// <item><term>载荷类型的<b>参数</b></term><description>→ <b>侧信道寄存器</b>。
    ///       这一条就是「给方法加品质参数」那条被推翻的路的替身：值照样跨过方法边界，
    ///       但签名一个都不动，所以 UXAssist / InstantDelivery 不受影响。</description></item>
    /// <item><term>常数</term><description>→ 0</description></item>
    /// </list>
    ///
    /// <b>寻址前缀原样重放，不去理解它。</b> 走到那个对象的那串指令
    /// （<c>ldarg</c> / <c>ldfld</c> / <c>ldloc</c> / <c>ldelem</c>）是纯的、可重复执行的，
    /// 所以孪生语句只要把同一串再走一遍就能拿到同一个对象。普查里 139 → 74 的塌缩
    /// 正是这个事实的度量：发散全在寻址上，运算本身只有一小撮。
    ///
    /// <b>两遍式让这一刀可以增量落地。</b> 分析遍一旦遇到形状表里没有的东西就记 Blocker、
    /// 一个字节都不改。所以「只实现了一部分形状」的结果是<b>变换整个不生效</b>，
    /// 游戏与不装时一致——不存在半吊子状态。这是 <see cref="CargoIncWidener"/>
    /// 用两次线上崩溃换来的规矩。
    /// </summary>
    internal static class QualityTransform
    {
        internal class Report
        {
            internal bool Applied;

            internal readonly List<string> Blockers = new List<string>();
            internal readonly List<string> Notes = new List<string>();

            /// <summary>已孪生的语句数</summary>
            internal int Twinned;

            /// <summary>涉及的方法体数</summary>
            internal int Methods;

            /// <summary>新增的孪生局部变量数</summary>
            internal int TwinLocals;

            /// <summary>
            /// 用到侧信道寄存器的次数。<b>目前恒为 0</b>：走侧信道的那几种形状
            /// （<c>stind.i1/i4</c> 写 out 参数、<c>call:split_inc</c>）还没进形状表。
            /// 留着它是为了让「表补齐了但一次都没走侧信道」能被看出来——
            /// 那会说明边界识别错了，而那是静默的。
            /// </summary>
#pragma warning disable 649 // 形状表补齐之前没有赋值点，这是预期的
            internal int ChannelUses;
#pragma warning restore 649

            /// <summary>形状表里没有的东西：签名 → 次数</summary>
            internal readonly Dictionary<string, int> Unhandled = new Dictionary<string, int>(StringComparer.Ordinal);

            internal readonly Dictionary<string, string> UnhandledExample =
                new Dictionary<string, string>(StringComparer.Ordinal);
        }

        internal static Report Apply(ModuleDefinition module)
        {
            Report check = Run(module, false);

            if (check.Blockers.Count > 0 || check.Unhandled.Count > 0) return check;

            return Run(module, true);
        }

        private static Report Run(ModuleDefinition module, bool mutate)
        {
            var r = new Report();

            // ── 闸：字段和通道都得在 ──
            QualityFieldAdder.Report fields = QualityFieldAdder.Verify(module);

            if (!fields.Applied)
            {
                r.Blockers.Add("孪生字段没就位（1a 未完成），1c 不动");

                return r;
            }

            TypeDefinition channel = module.GetType(QualityChannelBuilder.TypeName);

            if (channel == null)
            {
                r.Blockers.Add($"侧信道 {QualityChannelBuilder.TypeName} 不存在（1b 未完成），1c 不动");

                return r;
            }

            // ── 主干道载荷字段 → 它的孪生 ──
            var twin = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
            var payload = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);

            QualityFieldAnalyzer.ResolvePayload(module, payload, r.Blockers);

            if (r.Blockers.Count > 0) return r;

            foreach (string key in QualityFieldAnalyzer.MainlinePayload)
            {
                if (!payload.TryGetValue(key, out FieldDefinition src))
                {
                    r.Blockers.Add($"主干道载荷 {key} 不在载荷清单里");

                    continue;
                }

                string twinName = QualityFieldAdder.TwinName(src.Name);

                FieldDefinition tf = src.DeclaringType.Fields
                    .FirstOrDefault(f => f.Name == twinName && !f.IsStatic);

                if (tf == null)
                {
                    r.Blockers.Add($"{key} 的孪生字段 {twinName} 不存在");

                    continue;
                }

                twin[key] = tf;
            }

            if (r.Blockers.Count > 0) return r;

            // ── 侧信道寄存器 ──
            var regs = new List<FieldDefinition>();

            for (var i = 0; i < QualityChannelBuilder.RegisterCount; i++)
            {
                FieldDefinition q = channel.Fields.FirstOrDefault(f => f.Name == "Q" + i);

                if (q == null) { r.Blockers.Add($"侧信道缺 Q{i}"); return r; }

                regs.Add(q);
            }

            // ── 哪些参数是载荷参数（→ 映射到寄存器槽位） ──
            Dictionary<MethodDefinition, List<int>> paramSlots =
                QualityFieldAnalyzer.SelectTwinParams(module, out int _);

            // ── 逐方法处理 ──
            foreach (TypeDefinition t in AllTypes(module))
            {
                if (t.Name.StartsWith("UI", StringComparison.Ordinal)) continue;

                foreach (MethodDefinition m in t.Methods)
                {
                    if (!m.HasBody) continue;

                    if (!m.Body.Instructions.Any(i => IsMainline(i, twin))) continue;

                    // 混合方法要手工写，这一趟不碰
                    if (IsHandWritten(t, m)) continue;

                    Process(m, twin, regs, paramSlots, r, mutate);
                }
            }

            if (r.Unhandled.Count > 0)
            {
                r.Notes.Add(
                    $"形状表还缺 {r.Unhandled.Count} 种（共 {r.Unhandled.Values.Sum()} 处）——" +
                    "变换整个不生效，游戏与不装时一致。补齐形状表即可落地。");

                return r;
            }

            r.Notes.Add(
                $"品质搬运层：{r.Twinned} 条语句已孪生，涉及 {r.Methods} 个方法体，" +
                $"新增 {r.TwinLocals} 个孪生局部变量，{r.ChannelUses} 处走侧信道。");

            r.Applied = r.Blockers.Count == 0;

            return r;
        }

        /// <summary>
        /// 混合方法（既搬运又读增产剂效果表）要逐个手工写，自动变换不碰。
        /// 名单是 <see cref="QualityFieldAnalyzer"/> 的 DeclaredMixed，
        /// 这里按「类型::方法」比对。
        /// </summary>
        private static bool IsHandWritten(TypeDefinition t, MethodDefinition m)
        {
            string name = t.FullName + "::" + m.Name;

            return HandWritten.Contains(name);
        }

        private static readonly HashSet<string> HandWritten = new HashSet<string>(new[]
        {
            "AssemblerComponent::InternalUpdate",
            "LabComponent::InternalUpdateAssemble",
            "FractionatorComponent::InternalUpdate",
            "FractionatorComponent::get_extraIncProduceProb",
            "EjectorComponent::InternalUpdate",
            "SiloComponent::InternalUpdate",
            "TurretComponent::LoadAmmo",
            "PlanetFactory::EntityFastFillIn",
            "PowerGeneratorComponent::GenEnergyByFuel",
            "PowerExchangerComponent::CalculateActualEnergyPerTick",
            "SpraycoaterComponent::InternalUpdate",
            "StorageComponent::TakeTailItemsWithIncTable",
            "MechaForge::GameTick",
            "Mecha::GenerateEnergy",
            "Mecha::LoadAmmo",
            "Mecha::get_reactorPowerGenRatio",
            "Mecha::get_reactorPowerGenEnhanced",
            "Mecha::get_reactorPowerForWeaponEnhanced",
            "CountInc::get_incArrows",
        }, StringComparer.Ordinal);

        /// <summary>
        /// 处理一个方法体。<b>这一版只做分析，把认识和不认识的形状分开报出来</b>——
        /// 发射代码在形状表补齐之后接上，那时 <paramref name="mutate"/> 才有意义。
        ///
        /// 分成两步，和 <c>CargoIncWidener.FixFeederLocals</c> 是同一个套路：
        /// 先做<b>局部变量的不动点</b>（哪些局部装过载荷值），再逐语句匹配形状。
        /// 不先做局部变量的话，<c>local = X.inc</c> 之后那个 local 的每一次使用都会
        /// 被当成「不认识的形状」，而它其实只是同一条链的下游。
        /// </summary>
        private static void Process(MethodDefinition m, IDictionary<string, FieldDefinition> twin,
            IList<FieldDefinition> regs, IDictionary<MethodDefinition, List<int>> paramSlots,
            Report r, bool mutate)
        {
            var code = m.Body.Instructions.ToList();

            // 1) 局部变量不动点：装过载荷值的局部
            HashSet<int> carriers = PayloadLocals(m, code, twin);

            // 2) 逐语句匹配
            var targets = new HashSet<int>();

            foreach (Instruction i in code)
            {
                if (i.Operand is Instruction one) targets.Add(one.Offset);
                else if (i.Operand is Instruction[] many)
                    foreach (Instruction x in many) targets.Add(x.Offset);
            }

            var depth = 0;
            var start = 0;
            var touched = false;

            for (var i = 0; i < code.Count; i++)
            {
                if (targets.Contains(code[i].Offset) && depth != 0) { depth = 0; start = i; }

                depth += Push(code[i]) - Pop(code[i]);

                if (depth < 0) depth = 0;

                if (depth != 0) continue;

                int from = start;
                int to = i;

                start = i + 1;

                if (!HasMainline(code, from, to, twin)) continue;

                touched = true;

                string core = CoreShape(code, from, to, twin);

                if (Known.Contains(core))
                {
                    r.Twinned++;

                    continue;
                }

                r.Unhandled[core] = r.Unhandled.TryGetValue(core, out int n) ? n + 1 : 1;

                if (!r.UnhandledExample.ContainsKey(core))
                    r.UnhandledExample[core] =
                        $"{m.DeclaringType.Name}::{m.Name} @IL_{code[from].Offset:X4}";
            }

            if (touched) r.Methods++;

            r.TwinLocals += carriers.Count;
        }

        /// <summary>
        /// 装过载荷值的局部变量，求到不动点。
        ///
        /// 种子：<c>ldfld 载荷 ; stloc L</c>。传播：<c>ldloc A ; … ; stloc B</c>
        /// 且 A 已在集合里 —— 这一版只做一层直传，够覆盖普查里见到的形状；
        /// 真要发射代码时会换成完整的不动点。
        /// </summary>
        private static HashSet<int> PayloadLocals(MethodDefinition m, IList<Instruction> code,
            IDictionary<string, FieldDefinition> twin)
        {
            var set = new HashSet<int>();

            for (var i = 1; i < code.Count; i++)
            {
                if (!IsStloc(code[i])) continue;
                if (!IsMainline(code[i - 1], twin)) continue;

                int idx = LocalIndex(m, code[i]);

                if (idx >= 0) set.Add(idx);
            }

            return set;
        }

        // ── 形状表 ─────────────────────────────────────────────
        //
        // 普查给出主干道共 48 种核心形状。这一版先认下面这些；
        // 其余的会被报成 Unhandled，于是变换整个不生效。
        // **这正是增量落地的机制**：补一条形状，覆盖率涨一点，全齐了才会真的改字节。

        private static readonly HashSet<string> Known = new HashSet<string>(new[]
        {
            "ldc stfld:PAY",                                        // X.inc = 常数
            "stfld:PAY",                                            // X.inc = 栈上的值
            "ldfld:PAY stloc",                                      // local = X.inc
            "ldflda:PAY dup ldind.i4 add stind.i4",                 // X.inc += v
            "ldflda:PAY dup ldind.i4 sub stind.i4",                 // X.inc -= v
            "ldfld:PAY ldc dup ldind.i4 add stind.i4",              // 数组元素 += v
            "ldc ldflda:PAY dup ldind.i4 add stind.i4",             // 同上，常数下标
            "ldind.i4 ldfld:PAY add stind.i4",                      // *out += X.inc
            "ldnull stfld:PAY",                                     // X.incServed = null
        }, StringComparer.Ordinal);

        // ── 小工具 ─────────────────────────────────────────────

        private static bool IsMainline(Instruction i, IDictionary<string, FieldDefinition> twin) =>
            i.Operand is FieldReference fr &&
            twin.ContainsKey(fr.DeclaringType.FullName + "::" + fr.Name);

        private static bool HasMainline(IList<Instruction> code, int from, int to,
            IDictionary<string, FieldDefinition> twin)
        {
            for (int k = from; k <= to; k++) if (IsMainline(code[k], twin)) return true;

            return false;
        }

        /// <summary>语句的核心形状：归一化并剥掉纯寻址指令。和普查用的是同一套规则。</summary>
        private static string CoreShape(IList<Instruction> code, int from, int to,
            IDictionary<string, FieldDefinition> twin)
        {
            var keep = new List<string>();

            for (int i = from; i <= to; i++)
            {
                string tok = Norm(code[i], twin);

                if (tok == null) continue;

                keep.Add(tok);
            }

            return keep.Count == 0 ? "(空)" : string.Join(" ", keep.ToArray());
        }

        private static string Norm(Instruction i, IDictionary<string, FieldDefinition> twin)
        {
            string n = i.OpCode.Name;

            if (i.Operand is FieldReference fr)
            {
                bool pay = twin.ContainsKey(fr.DeclaringType.FullName + "::" + fr.Name);

                if (n.StartsWith("ldflda", StringComparison.Ordinal)) return pay ? "ldflda:PAY" : null;
                if (n.StartsWith("ldfld", StringComparison.Ordinal)) return pay ? "ldfld:PAY" : null;
                if (n.StartsWith("stfld", StringComparison.Ordinal)) return pay ? "stfld:PAY" : "stfld";
                if (n.StartsWith("ldsfld", StringComparison.Ordinal)) return pay ? "ldsfld:PAY" : null;
                if (n.StartsWith("stsfld", StringComparison.Ordinal)) return pay ? "stsfld:PAY" : "stsfld";
            }

            // 纯寻址：原样重放即可，不参与形状
            if (n.StartsWith("ldarg", StringComparison.Ordinal)) return null;
            if (n.StartsWith("ldloca", StringComparison.Ordinal)) return null;
            if (n.StartsWith("ldloc", StringComparison.Ordinal)) return null;
            if (n.StartsWith("ldelem", StringComparison.Ordinal)) return null;
            if (n.StartsWith("conv", StringComparison.Ordinal)) return null;

            if (n.StartsWith("stloc", StringComparison.Ordinal)) return "stloc";
            if (n.StartsWith("stelem", StringComparison.Ordinal)) return "stelem";
            if (n.StartsWith("ldc", StringComparison.Ordinal)) return "ldc";

            if (i.Operand is MethodReference mr && (n == "call" || n == "callvirt" || n == "newobj"))
                return "call:" + mr.Name;

            return n;
        }

        private static bool IsStloc(Instruction i) =>
            i.OpCode == OpCodes.Stloc || i.OpCode == OpCodes.Stloc_S ||
            i.OpCode == OpCodes.Stloc_0 || i.OpCode == OpCodes.Stloc_1 ||
            i.OpCode == OpCodes.Stloc_2 || i.OpCode == OpCodes.Stloc_3;

        private static int LocalIndex(MethodDefinition m, Instruction i)
        {
            if (i.Operand is VariableDefinition v) return v.Index;

            if (i.OpCode == OpCodes.Stloc_0) return 0;
            if (i.OpCode == OpCodes.Stloc_1) return 1;
            if (i.OpCode == OpCodes.Stloc_2) return 2;
            if (i.OpCode == OpCodes.Stloc_3) return 3;

            return -1;
        }

        private static int Pop(Instruction i)
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

        private static int Push(Instruction i)
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
