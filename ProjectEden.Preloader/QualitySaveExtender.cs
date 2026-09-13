using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace ProjectEden.Preloader
{
    /// <summary>
    /// 物品品质 · 阶段 1d：<b>让品质进存档。</b>
    ///
    /// 1c 之后品质会随货物流动，但四个主干道载荷的 <c>Export</c> 一个字都不写它，
    /// <c>Import</c> 一侧被 1c 统一置零——所以<b>每次读档品质归零</b>。这一刀把它补上。
    ///
    /// <b>做法是「贴着原版那一笔写」，不是另起一份账。</b> 品质字段和它的 <c>inc</c> 就在
    /// 同一个结构体里，所以写存档时紧跟着原版那次 <c>Write</c> 再写一次，读的时候紧跟着
    /// 原版那次读再读一次——下标对齐是<b>构造上保证</b>的，不需要任何一方去维护。
    /// 换成自己开一块存档、按池下标存一份平行数组，就得赌 vanilla 的 Import 不会重排下标,
    /// 而那是赌不起的：赌输了不报错，只是所有品质错位到别的货上。
    ///
    /// <b>老存档靠版本分流。</b> 每个容器的 <c>Export</c> 开头都写一个版本号，
    /// <c>Import</c> 把它读进局部——只要那个局部在，就能写 `if (版本 &gt;= 新版本) 读品质`。
    /// 读不到品质的老存档走 1c 已经铺好的那条路：品质置零。
    ///
    /// <b>自动集装机不在这一刀里，而且那是量出来的。</b> <c>PilerComponent.Import</c>
    /// **根本没把版本号存进局部**（见 <c>tools</c> 下那份普查），所以它没有可分流的地方。
    /// 代价有界：每台集装机最多损失「正在叠的那一堆」的品质，落地的货一件不受影响——
    /// 和加宽那一期对同一个类改用截断而不是分支，是同一个判断。
    /// </summary>
    internal static class QualitySaveExtender
    {
        internal class Report
        {
            internal bool Applied;

            internal readonly List<string> Blockers = new List<string>();
            internal readonly List<string> Notes = new List<string>();

            /// <summary>写存档时新增的品质写入点</summary>
            internal int Writes;

            /// <summary>读存档时新增的版本分流读入点</summary>
            internal int Reads;

            /// <summary>各容器的新版本号</summary>
            internal readonly Dictionary<string, int> Versions =
                new Dictionary<string, int>(StringComparer.Ordinal);
        }

        /// <summary>
        /// 一个要扩的容器：谁的 <c>Export</c>/<c>Import</c>，以及里面装的是哪个载荷字段。
        ///
        /// <c>Owner</c> 和 <c>Type</c> 可以不同——<c>StorageComponent</c> 存的是
        /// <c>StorageComponent/GRID</c> 里的 <c>inc</c>。
        /// </summary>
        private sealed class Target
        {
            internal string Type;
            internal string Owner;
            internal string Payload;
        }

        private static readonly Target[] Targets =
        {
            new Target { Type = "CargoContainer", Owner = "Cargo", Payload = "inc" },
            new Target { Type = "StorageComponent", Owner = "StorageComponent/GRID", Payload = "inc" },
            new Target { Type = "StationStore", Owner = "StationStore", Payload = "inc" },
            new Target { Type = "AssemblerComponent", Owner = "AssemblerComponent", Payload = "incServed" },
        };

        internal static Report Apply(ModuleDefinition module)
        {
            Report check = Run(module, false);

            if (check.Blockers.Count > 0) return check;

            return Run(module, true);
        }

        /// <summary>
        /// 和别的几刀一样：<b>先只分析一遍，零阻塞才动手。</b>
        /// 存档格式改到一半是救不回来的——写出去的存档会缺半截字段，而读它的代码已经在等那半截。
        /// </summary>
        private static Report Run(ModuleDefinition module, bool mutate)
        {
            var r = new Report();

            foreach (Target t in Targets)
            {
                TypeDefinition owner = module.GetType(t.Owner);
                TypeDefinition holder = module.GetType(t.Type);

                if (owner == null || holder == null)
                {
                    r.Blockers.Add($"{t.Type} / {t.Owner} 找不到");

                    continue;
                }

                string twinName = QualityFieldAdder.TwinName(t.Payload);
                FieldDefinition qua = owner.Fields.FirstOrDefault(f => f.Name == twinName && !f.IsStatic);

                if (qua == null)
                {
                    r.Blockers.Add($"{t.Owner}::{twinName} 不存在——1a 没跑或清单对不上");

                    continue;
                }

                MethodDefinition export = Body(holder, "Export");
                MethodDefinition import = Body(holder, "Import");

                if (export == null || import == null)
                {
                    r.Blockers.Add($"{t.Type} 缺 Export 或 Import");

                    continue;
                }

                int version = BumpVersion(export, t, r, mutate);

                if (version < 0) continue;

                r.Versions[t.Type] = version;

                int w0 = r.Writes, d0 = r.Reads;

                ExtendExport(module, export, owner, qua, t, r, mutate);
                ExtendImport(module, import, owner, qua, version, t, r, mutate);

                // **写几次就得读几次，一个容器之内必须相等。**
                // 存档是位置流：多写一处，读的人就会在后面某个字段上读到别人的字节,
                // 而且从那一刻起后面全错。这条断言是唯一能在离线抓住它的东西——
                // 写盘、重读、分支目标检查全都看不出流对不对得上。
                if (r.Writes - w0 != r.Reads - d0)
                    r.Blockers.Add(
                        $"{t.Type}：写 {r.Writes - w0} 处、读 {r.Reads - d0} 处，" +
                        "两边对不上——存档流会错位，整刀不做");
            }

            if (r.Blockers.Count > 0) return r;

            r.Notes.Add(
                $"品质进存档：{r.Writes} 处写入、{r.Reads} 处按版本分流读入，涉及 " +
                string.Join("、", r.Versions.Select(kv => $"{kv.Key}→v{kv.Value}").ToArray()) +
                "。老存档读不到品质，按 0 处理。");

            r.Applied = true;

            return r;
        }

        // ── 版本号 ─────────────────────────────────────────────

        /// <summary>
        /// 把 <c>Export</c> 开头那个版本常量加一，返回新版本号；找不到返回 −1。
        ///
        /// <b>要认两种写法。</b> 长度会变的改写前面必须 <c>SimplifyMacros</c>，
        /// 而它会把 <c>ldc.i4.2</c> 展开成 <c>ldc.i4 2</c>——只认 macro 形式的匹配器
        /// 会在**动手那一趟**才失败，那正是两遍式设计要防的「改了一半」。
        /// </summary>
        private static int BumpVersion(MethodDefinition m, Target t, Report r, bool mutate)
        {
            var code = m.Body.Instructions;

            for (var i = 0; i + 1 < code.Count && i < 12; i++)
            {
                if (!IsConstInt(code[i], out int v)) continue;
                if (!IsWriterCall(code[i + 1], "Int32")) continue;

                if (mutate)
                {
                    code[i].OpCode = OpCodes.Ldc_I4;
                    code[i].Operand = v + 1;
                }

                return v + 1;
            }

            r.Blockers.Add($"{t.Type}.Export 开头找不到版本号常量");

            return -1;
        }

        // ── 写 ─────────────────────────────────────────────────

        /// <summary>
        /// 每处「写 <c>inc</c>」后面补一处「写 <c>qua</c>」。
        ///
        /// 值那一侧**原样重放再把字段换成孪生**：数组元素那种写法里，下标表达式
        /// （<c>ldfld incServed ; ldloc i ; ldelem.i4</c>）也一并抄过来，
        /// 所以循环不用动，一条都不用新建。
        /// </summary>
        private static void ExtendExport(ModuleDefinition module, MethodDefinition m,
            TypeDefinition owner, FieldDefinition qua, Target t, Report r, bool mutate)
        {
            m.Body.SimplifyMacros();

            var code = m.Body.Instructions;
            var sites = new List<int>();

            for (var i = 0; i < code.Count; i++)
            {
                if (!IsWriterCall(code[i], null)) continue;

                int[] a = Args(code, i, 2);

                if (a == null) continue;
                if (!Mentions(code, a[1], i - 1, owner, t.Payload)) continue;

                // **写长度不是写点数。** `w.Write(incServed.Length)` 的值表达式里同样出现
                // incServed，照抄一份就会多写一个「quaServed.Length」到流里，
                // 而读那一侧并不会去读它——流从此错开一个 int，后面全部读歪。
                if (Has(code, a[1], i - 1, OpCodes.Ldlen)) continue;

                sites.Add(i);
            }

            if (sites.Count == 0)
            {
                r.Blockers.Add($"{t.Type}.Export 里找不到写 {t.Payload} 的地方");

                m.Body.OptimizeMacros();

                return;
            }

            if (mutate)
            {
                ILProcessor il = m.Body.GetILProcessor();

                // 倒着插：正着插会让后面每个下标错位
                for (int s = sites.Count - 1; s >= 0; s--)
                {
                    int at = sites[s];
                    int[] a = Args(code, at, 2);
                    var emit = new List<Instruction>();

                    // writer 那一侧原样重放
                    for (int k = a[0]; k < a[1]; k++) emit.Add(Clone(code[k]));

                    // 值那一侧重放，字段换孪生
                    for (int k = a[1]; k < at; k++) emit.Add(Swap(code[k], owner, t.Payload, qua));

                    emit.Add(Instruction.Create(OpCodes.Callvirt, Writer(module, "Int32")));

                    Instruction anchor = code[at];

                    foreach (Instruction ins in emit)
                    {
                        il.InsertAfter(anchor, ins);

                        anchor = ins;
                    }
                }
            }

            r.Writes += sites.Count;

            m.Body.OptimizeMacros();
        }

        // ── 读 ─────────────────────────────────────────────────

        /// <summary>
        /// 每处 1c 发射的「<c>qua = 0</c>」后面补一句
        /// <c>if (版本 &gt;= 新版本) qua = reader.ReadInt32();</c>。
        ///
        /// <b>锚点是 1c 那一笔置零，不是原版那一笔读 inc。</b> 两者紧挨着，
        /// 而插在原版那一笔后面的话，1c 的零会把刚读出来的品质盖掉——
        /// 顺序错了不会报错，只会让品质永远是 0，和没做这一刀一模一样。
        /// </summary>
        private static void ExtendImport(ModuleDefinition module, MethodDefinition m,
            TypeDefinition owner, FieldDefinition qua, int version, Target t, Report r, bool mutate)
        {
            m.Body.SimplifyMacros();

            var code = m.Body.Instructions;

            VariableDefinition ver = VersionLocal(m);

            if (ver == null)
            {
                r.Blockers.Add($"{t.Type}.Import 没有把版本号存进局部，没法做分流");

                m.Body.OptimizeMacros();

                return;
            }

            ParameterDefinition reader = m.Parameters
                .FirstOrDefault(p => p.ParameterType.FullName == "System.IO.BinaryReader");

            if (reader == null)
            {
                r.Blockers.Add($"{t.Type}.Import 的参数里找不到 BinaryReader");

                m.Body.OptimizeMacros();

                return;
            }

            // **锚点是原版那次「从流里读出来存进载荷」，不是 1c 的置零。**
            //
            // 试过锚在置零上，不成立：同一个 Import 里有好几处 `inc = 0`（清空池的循环、
            // 老版本分支），它们和真正的读长得一模一样；而真正那一处在 CargoContainer 里
            // 压根没有对应的置零——1c 在那里判定「容器是本方法 newarr 出来的，天生是 0」,
            // 一条指令都没发。按置零去配，会同时**漏掉真的、配上假的**。
            //
            // 位置流上多读一次或少读一次，后面所有字段都会读歪，而且不报错。
            var sites = new List<int>();

            for (var i = 0; i < code.Count; i++)
            {
                if (!StoresPayload(code, i, owner, t.Payload)) continue;
                if (!ReadsStream(code, i, owner, t.Payload)) continue;

                // 1c 的置零要是紧跟在后面，就插在它之后——否则零会把刚读出来的品质盖掉。
                int at = i;

                for (int k = i + 1; k < code.Count && k <= i + 12; k++)
                    if (StoresTwin(code, k, qua)) { at = k; break; }

                sites.Add(at);
            }

            if (sites.Count == 0)
            {
                r.Blockers.Add($"{t.Type}.Import 里找不到 1c 发射的 {qua.Name} 置零——1c 没跑？");

                m.Body.OptimizeMacros();

                return;
            }

            if (mutate)
            {
                ILProcessor il = m.Body.GetILProcessor();

                for (int s = sites.Count - 1; s >= 0; s--)
                {
                    int at = sites[s];
                    bool elem = code[at].OpCode.Name.StartsWith("stelem", StringComparison.Ordinal);
                    int[] a = Args(code, at, elem ? 3 : 2);

                    // 目的地要写的是**孪生**字段，哪怕锚在原版那一条上
                    FieldDefinition dest = qua;

                    if (a == null)
                    {
                        r.Blockers.Add($"{t.Type}.Import 的 {qua.Name} 置零处数不出实参边界");

                        m.Body.OptimizeMacros();

                        return;
                    }

                    // 目的地表达式（对象 / 数组+下标）原样重放
                    var emit = new List<Instruction>();

                    for (int k = a[0]; k < a[a.Length - 1]; k++)
                        emit.Add(Swap(code[k], owner, t.Payload, qua));

                    emit.Add(Instruction.Create(OpCodes.Ldarg, reader));
                    emit.Add(Instruction.Create(OpCodes.Callvirt, Reader(module)));
                    emit.Add(elem
                        ? Instruction.Create(OpCodes.Stelem_I4)
                        : Instruction.Create(OpCodes.Stfld, dest));

                    // 前面加一句版本判断，跳过整段
                    Instruction after = at + 1 < code.Count ? code[at + 1] : null;

                    if (after == null)
                    {
                        r.Blockers.Add($"{t.Type}.Import 的 {qua.Name} 置零是方法最后一条，插不进去");

                        m.Body.OptimizeMacros();

                        return;
                    }

                    var guard = new List<Instruction>
                    {
                        Instruction.Create(OpCodes.Ldloc, ver),
                        Instruction.Create(OpCodes.Ldc_I4, version),
                        Instruction.Create(OpCodes.Blt, after),
                    };

                    Instruction anchor = code[at];

                    foreach (Instruction ins in guard.Concat(emit))
                    {
                        il.InsertAfter(anchor, ins);

                        anchor = ins;
                    }
                }
            }

            r.Reads += sites.Count;

            m.Body.OptimizeMacros();
        }

        // ── 小工具 ─────────────────────────────────────────────

        private static MethodDefinition Body(TypeDefinition t, string name) =>
            t.Methods.FirstOrDefault(x => x.Name == name && x.HasBody);

        /// <summary>版本号那个局部：方法开头 <c>ReadInt32()</c> 之后紧跟的那次 <c>stloc</c>。</summary>
        private static VariableDefinition VersionLocal(MethodDefinition m)
        {
            var code = m.Body.Instructions;

            for (var i = 0; i + 1 < code.Count && i < 12; i++)
            {
                if (!(code[i].Operand is MethodReference mr)) continue;
                if (mr.Name != "ReadInt32") continue;
                if (!code[i + 1].OpCode.Name.StartsWith("stloc", StringComparison.Ordinal)) continue;

                return VarOf(m, code[i + 1]);
            }

            return null;
        }

        /// <summary>
        /// 这条指令是不是在写孪生字段<b>的值</b>。
        ///
        /// 两种写法：标量字段直接 <c>stfld</c>；数组是 <c>stelem</c>，而 stelem 本身不带字段名,
        /// 要往回看数组表达式里有没有它。
        ///
        /// <b>数组字段自己的 stfld 不算。</b> `quaServed = new int[n]` 是分配不是赋值,
        /// 在那里插一句「读一个 int 存进去」是把 int 写进 int[] 字段——IL 类型都不合法。
        /// </summary>
        private static bool StoresTwin(IList<Instruction> code, int at, FieldDefinition qua)
        {
            Instruction i = code[at];

            if (i.OpCode == OpCodes.Stfld)
                return i.Operand is FieldReference fr && fr.FullName == qua.FullName
                       && !(qua.FieldType is ArrayType);

            if (!i.OpCode.Name.StartsWith("stelem", StringComparison.Ordinal)) return false;
            if (!(qua.FieldType is ArrayType)) return false;

            int[] a = Args(code, at, 3);

            if (a == null) return false;

            for (int k = a[0]; k < a[1]; k++)
                if (code[k].Operand is FieldReference af && af.FullName == qua.FullName)
                    return true;

            return false;
        }

        /// <summary>这条指令是不是在写<b>原版</b>的载荷字段（标量 stfld 或数组元素 stelem）。</summary>
        private static bool StoresPayload(IList<Instruction> code, int at, TypeDefinition owner,
            string payload)
        {
            Instruction i = code[at];

            if (i.OpCode == OpCodes.Stfld)
                return i.Operand is FieldReference fr && fr.Name == payload
                       && fr.DeclaringType.FullName == owner.FullName
                       && !(fr.FieldType is ArrayType);

            if (!i.OpCode.Name.StartsWith("stelem", StringComparison.Ordinal)) return false;

            int[] a = Args(code, at, 3);

            if (a == null) return false;

            return Mentions(code, a[0], a[1] - 1, owner, payload);
        }

        /// <summary>
        /// 这一次写入的值是不是<b>从存档流里读出来的</b>。
        ///
        /// 先按实参边界精确判；数不出来（值那一侧是个版本分支，后向回溯会撞上分支目标）
        /// 就退回往前扫一小段——扫到上一处载荷写入为止，不会串到别的字段上去。
        /// </summary>
        private static bool ReadsStream(IList<Instruction> code, int at, TypeDefinition owner,
            string payload)
        {
            bool elem = code[at].OpCode.Name.StartsWith("stelem", StringComparison.Ordinal);
            int[] a = Args(code, at, elem ? 3 : 2);

            if (a != null)
            {
                for (int k = a[a.Length - 1]; k < at; k++)
                    if (IsReaderCall(code[k]))
                        return true;

                return false;
            }

            for (int k = at - 1; k >= 0 && k > at - 32; k--)
            {
                if (StoresPayload(code, k, owner, payload)) return false;
                if (IsReaderCall(code[k])) return true;
            }

            return false;
        }

        private static bool IsReaderCall(Instruction i) =>
            i.Operand is MethodReference mr && mr.DeclaringType.FullName == "System.IO.BinaryReader";

        private static bool Has(IList<Instruction> code, int from, int to, OpCode op)
        {
            for (int k = from; k <= to && k < code.Count; k++)
                if (code[k].OpCode == op)
                    return true;

            return false;
        }

        private static bool Mentions(IList<Instruction> code, int from, int to,
            TypeDefinition owner, string field)
        {
            for (int k = from; k <= to && k < code.Count; k++)
                if (code[k].Operand is FieldReference fr
                    && fr.Name == field && fr.DeclaringType.FullName == owner.FullName)
                    return true;

            return false;
        }

        private static Instruction Swap(Instruction i, TypeDefinition owner, string field,
            FieldDefinition qua)
        {
            if (i.Operand is FieldReference fr && fr.Name == field
                && fr.DeclaringType.FullName == owner.FullName)
                return Instruction.Create(i.OpCode, (FieldReference)qua);

            return Clone(i);
        }

        private static bool IsConstInt(Instruction i, out int v)
        {
            v = 0;

            string n = i.OpCode.Name;

            if (i.OpCode == OpCodes.Ldc_I4 && i.Operand is int big) { v = big; return true; }
            if (i.OpCode == OpCodes.Ldc_I4_S && i.Operand is sbyte sb) { v = sb; return true; }

            if (n.StartsWith("ldc.i4.", StringComparison.Ordinal)
                && int.TryParse(n.Substring(7), out int small)) { v = small; return true; }

            return false;
        }

        private static bool IsWriterCall(Instruction i, string argType) =>
            (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call)
            && i.Operand is MethodReference mr
            && mr.Name == "Write"
            && mr.DeclaringType.FullName == "System.IO.BinaryWriter"
            && (argType == null || (mr.Parameters.Count == 1
                                    && mr.Parameters[0].ParameterType.Name == argType));

        // 走 System.Reflection 拿方法再 ImportReference，和 SerializationFixer 同一个写法。
        // 试过从模块自己的 corlib 引用里解析，那条路在这里返回 null 并在 Apply 中途炸——
        // preloader 跑在 BepInEx 的 AppDomain 里，这两个类型本进程就有，没必要绕。
        private static MethodReference Writer(ModuleDefinition module, string argType) =>
            module.ImportReference(typeof(System.IO.BinaryWriter)
                .GetMethod("Write", new[] { argType == "Int32" ? typeof(int) : typeof(short) }));

        private static MethodReference Reader(ModuleDefinition module) =>
            module.ImportReference(
                typeof(System.IO.BinaryReader).GetMethod("ReadInt32", Type.EmptyTypes));

        /// <summary>实参边界靠后向栈深模拟，理由和 QualityTransform 里那份一样。</summary>
        private static int[] Args(IList<Instruction> code, int at, int argc)
        {
            var starts = new int[argc];
            var depth = 0;
            int need = argc;

            for (int i = at - 1; i >= 0 && need > 0; i--)
            {
                depth += Push(code[i]) - Pop(code[i]);

                if (depth <= 0) continue;

                starts[--need] = i;
                depth = 0;
            }

            return need == 0 ? starts : null;
        }

        private static int Push(Instruction i)
        {
            switch (i.OpCode.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1_push1: return 2;
                case StackBehaviour.Varpush:
                    return i.Operand is MethodReference mr && mr.ReturnType.FullName != "System.Void" ? 1 : 0;
                default: return 1;
            }
        }

        private static int Pop(Instruction i)
        {
            switch (i.OpCode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1:
                case StackBehaviour.Popi:
                case StackBehaviour.Popref: return 1;
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

                    return m.Parameters.Count + (m.HasThis && i.OpCode != OpCodes.Newobj ? 1 : 0);
                default: return 0;
            }
        }

        private static VariableDefinition VarOf(MethodDefinition m, Instruction i)
        {
            if (i.Operand is VariableDefinition v) return v;

            int idx = i.OpCode == OpCodes.Stloc_0 ? 0
                : i.OpCode == OpCodes.Stloc_1 ? 1
                : i.OpCode == OpCodes.Stloc_2 ? 2
                : i.OpCode == OpCodes.Stloc_3 ? 3
                : -1;

            return idx >= 0 && idx < m.Body.Variables.Count ? m.Body.Variables[idx] : null;
        }

        private static Instruction Clone(Instruction i)
        {
            switch (i.Operand)
            {
                case null: return Instruction.Create(i.OpCode);
                case FieldReference f: return Instruction.Create(i.OpCode, f);
                case MethodReference me: return Instruction.Create(i.OpCode, me);
                case TypeReference t: return Instruction.Create(i.OpCode, t);
                case VariableDefinition v: return Instruction.Create(i.OpCode, v);
                case ParameterDefinition p: return Instruction.Create(i.OpCode, p);
                case int n: return Instruction.Create(i.OpCode, n);
                case sbyte sb: return Instruction.Create(i.OpCode, sb);
                case byte b: return Instruction.Create(i.OpCode, b);
                default: return Instruction.Create(i.OpCode);
            }
        }
    }
}
