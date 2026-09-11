using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace ProjectEden.Preloader
{
    /// <summary>
    /// 加宽之后把存档读写一起改掉，<b>并且让老存档还能开</b>。
    ///
    /// <b>为什么这一步不能省。</b> <c>CargoContainer.Export</c> 是<b>逐字段</b>写的
    /// （<c>Write(Int16 item)</c> / <c>Write(Byte stack)</c> / <c>Write(Byte inc)</c>），
    /// 不是整块内存倒出去。所以加宽了字段而不改这里，存档时 1020 会被截回一个字节——
    /// 玩到 255 层满级增产，一存一读就打回原形，而且不报错。
    ///
    /// <b>老存档怎么办：货物块开头有一个版本号。</b>
    /// <c>Export</c> 第一条就是 <c>Write((int)2)</c>，<c>Import</c> 把它读进 <c>V_0</c>。
    /// 所以这里把版本写成 <b>3</b>，读的时候按版本分流：
    /// <code>
    ///   version >= 3 ? reader.ReadInt16() : reader.ReadByte()
    /// </code>
    /// 于是<b>装上本 mod 之前存的档照样能开</b>（按字节读，值本来也不会超过 255），
    /// 而新存的档带满宽度。反过来不行——版本 3 的存档卸载本 mod 之后读不了，
    /// 这正是「存档以当前 mod 为准」那条已经拍过板的政策。
    ///
    /// <b>自动集装机那两个缓存字段走的是另一条路：夹取，不改格式。</b>
    /// <c>PilerComponent</c> 自己的版本号在 <c>Import</c> 里没留下来，没法按版本分流；
    /// 而它们存的只是「正在叠的那一堆」的暂存值，跨存档丢一点精度的影响是有界的
    /// （每台集装机一堆货）。所以 <c>Export</c> 前面插一个 <c>Math.Min(x, 255)</c>——
    /// <b>夹取而不是截断</b>：截断会回绕成垃圾值，夹取是确定的降级。
    /// </summary>
    internal static class SerializationFixer
    {
        /// <summary>加宽后的货物块版本号。原版是 2</summary>
        private const int NewVersion = 3;

        /// <summary>跟 <c>CargoIncWidener.Watched</c> 保持一致：这两个字段的读写都要改</summary>
        private static readonly string[] Fields = { "inc", "stack" };

        internal static void Apply(ModuleDefinition module, ICollection<FieldDefinition> widenedFields,
                                   CargoIncWidener.Report r, bool mutate)
        {
            // 这三个方法是整个改写里唯一会改变字节长度的地方（插指令 / 换长常量）。
            //
            // **长度一变，原有的短分支就可能装不下位移了**，而 Cecil **不会**自动把
            // br.s 换成 br——它会把一个超过一字节的位移截断写进去。
            // 结果是一个跳到半条指令中间的分支，重新读回来目标就是 null。
            //
            // 这个错不会在改写时报，也不会在 Cecil 写盘时报，而是等到 Harmony/MonoMod
            // 去读那个方法体时招一个 NullReferenceException，栈里只看得到
            // 「DMD<CargoContainer::Import> @ IL_01b9: br.s」——指不到真正的原因。实测过。
            //
            // SimplifyMacros 先把所有短分支换成长形式，改完再 OptimizeMacros 收回去。
            foreach (MethodDefinition m in Resizable(module))
                if (mutate) m.Body.SimplifyMacros();

            FixCargoExport(module, r, mutate);
            FixCargoImport(module, r, mutate);
            ClampByteWrites(module, widenedFields, r, mutate);

            foreach (MethodDefinition m in Resizable(module))
                if (mutate) m.Body.OptimizeMacros();
        }

        /// <summary>会被改变字节长度的那几个方法。</summary>
        private static IEnumerable<MethodDefinition> Resizable(ModuleDefinition module)
        {
            MethodDefinition a = Method(module, "CargoContainer", "Export");
            MethodDefinition b = Method(module, "CargoContainer", "Import");
            MethodDefinition c = Method(module, "PilerComponent", "Export");
            MethodDefinition d = Method(module, "InserterComponent", "Export");

            if (a != null) yield return a;
            if (b != null) yield return b;
            if (c != null) yield return c;
            if (d != null) yield return d;
        }

        // ── 写 ────────────────────────────────────────────────

        private static void FixCargoExport(ModuleDefinition module, CargoIncWidener.Report r, bool mutate)
        {
            MethodDefinition m = Method(module, "CargoContainer", "Export");

            if (m == null) { r.Blockers.Add("找不到 CargoContainer.Export"); return; }

            var code = m.Body.Instructions;

            // 1) 版本号：开头那条 ldc.i4.2，后面紧跟 Write(Int32)
            var bumped = false;

            for (var i = 0; i + 1 < code.Count && i < 8; i++)
            {
                // SimplifyMacros 会把 ldc.i4.2 展开成 ldc.i4 2，所以两种形式都要认。
                // **这个匹配器绝不能失败**：它跑在 mutate 那一趟，而那时 SimplifyMacros
                // 已经改过方法体了——分析趟通过、动手趟才失败，就等于留下一个改了一半的程序集。
                bool isTwo = code[i].OpCode == OpCodes.Ldc_I4_2 ||
                             (code[i].OpCode == OpCodes.Ldc_I4 && code[i].Operand is int n && n == 2);

                if (!isTwo) continue;
                if (!IsWrite(code[i + 1], "Int32")) continue;

                if (mutate)
                {
                    code[i].OpCode = OpCodes.Ldc_I4;
                    code[i].Operand = NewVersion;
                }

                bumped = true;

                break;
            }

            if (!bumped) { r.Blockers.Add("CargoContainer.Export 开头找不到版本号常量 2"); return; }

            // 2) 写 inc / stack 的那两条：Write(Byte) → Write(Int16)
            foreach (string name in Fields)
            {
                var fixedWrite = 0;

                for (var i = 0; i + 1 < code.Count; i++)
                {
                    if (code[i].OpCode != OpCodes.Ldfld) continue;
                    if (!IsCargoField(code[i].Operand, name)) continue;
                    if (!IsWrite(code[i + 1], "Byte")) continue;

                    if (mutate) code[i + 1].Operand = WriterMethod(module, "Int16");

                    fixedWrite++;
                }

                if (fixedWrite != 1)
                {
                    r.Blockers.Add($"CargoContainer.Export 里写 {name} 的 Write(Byte) 找到 {fixedWrite} 处，预期 1 处");

                    return;
                }
            }

            r.Notes.Add($"存档：货物块版本 2 → {NewVersion}，inc / stack 均按 Int16 写");
        }

        // ── 读 ────────────────────────────────────────────────

        private static void FixCargoImport(ModuleDefinition module, CargoIncWidener.Report r, bool mutate)
        {
            MethodDefinition m = Method(module, "CargoContainer", "Import");

            if (m == null) { r.Blockers.Add("找不到 CargoContainer.Import"); return; }

            var code = m.Body.Instructions;

            foreach (string name in Fields)
            {
                if (!PatchOneRead(m, name, r, mutate)) return;
            }

            r.Notes.Add(mutate
                ? "存档：Import 已按版本分流读 inc / stack（老存档按字节，新存档按 Int16）"
                : "存档：Import 将按版本分流读 inc / stack");
        }

        /// <summary>
        /// 把一个字段的 <c>ReadByte()</c> 换成按版本分流。
        /// 每个字段单独做一次：改完一个之后指令表变了，所以重新扫。
        /// </summary>
        private static bool PatchOneRead(MethodDefinition m, string name, CargoIncWidener.Report r, bool mutate)
        {
            var code = m.Body.Instructions;

            var targets = new List<int>();

            for (var i = 1; i + 1 < code.Count; i++)
            {
                if (!IsRead(code[i], "Byte")) continue;
                if (code[i + 1].OpCode != OpCodes.Stfld || !IsCargoField(code[i + 1].Operand, name)) continue;

                targets.Add(i);
            }

            if (targets.Count != 1)
            {
                r.Blockers.Add($"CargoContainer.Import 里读 {name} 的 ReadByte 找到 {targets.Count} 处，预期 1 处");

                return false;
            }

            if (!mutate) return true;

            ModuleDefinition module = m.Module;

            int at = targets[0];

            ILProcessor il = m.Body.GetILProcessor();

            Instruction readByte = code[at];

            // 旧存档（版本 < 3）仍然只有一个字节，照旧读；新存档读 Int16。
            // 栈形状两边一致：进来是 [.., reader]，出去是 [.., value]。
            Instruction legacy = Instruction.Create(OpCodes.Callvirt, ReaderMethod(module, "Byte"));
            Instruction done = Instruction.Create(OpCodes.Nop);

            var seq = new[]
            {
                Instruction.Create(OpCodes.Ldloc_0),                      // 版本号
                Instruction.Create(OpCodes.Ldc_I4, NewVersion),
                Instruction.Create(OpCodes.Blt, legacy),
                Instruction.Create(OpCodes.Callvirt, ReaderMethod(module, "Int16")),
                Instruction.Create(OpCodes.Br, done),
                legacy,
                done
            };

            // 原地换掉那条 callvirt：它身上可能落着跳转标签，替换对象会丢标签
            readByte.OpCode = seq[0].OpCode;
            readByte.Operand = seq[0].Operand;

            Instruction prev = readByte;

            for (var k = 1; k < seq.Length; k++)
            {
                il.InsertAfter(prev, seq[k]);

                prev = seq[k];
            }

            return true;
        }

        // ── 集装机缓存：夹取而不是截断 ────────────────────────

        /// <summary>
        /// 已加宽的字段里，有些仍然按<b>字节</b>写进存档（格式故意不改）。
        /// 那些地方插一个 <c>Math.Min(x, 255)</c>：<b>夹取而不是截断</b>——
        /// 截断会把 5000 绕成 136，夹取是确定的降级。
        ///
        /// 两处用到：<c>PilerComponent</c> 的四个缓存字段（它自己的版本号在 Import 里没留下来，
        /// 没法按版本分流），以及 <c>InserterComponent</c> 的两个堆叠层数。
        /// 后者丢精度是<b>无害的</b>：真值存在 <c>GameHistoryData</c>（Int32）里，
        /// 读档后由 <c>OnInserterTechChange</c> 重新刷回每台分拣器。
        /// </summary>
        private static void ClampByteWrites(ModuleDefinition module, ICollection<FieldDefinition> widenedFields,
                                            CargoIncWidener.Report r, bool mutate)
        {
            MethodReference min = module.ImportReference(
                typeof(Math).GetMethod("Min", new[] { typeof(int), typeof(int) }));

            var clamped = 0;

            foreach (FieldDefinition wf in widenedFields)
            {
                MethodDefinition m = Method(module, wf.DeclaringType.Name, "Export");

                if (m == null) continue;

                var code = m.Body.Instructions;

                ILProcessor il = m.Body.GetILProcessor();

                for (var i = 0; i + 1 < code.Count; i++)
                {
                    if (code[i].OpCode != OpCodes.Ldfld) continue;
                    if (!(code[i].Operand is FieldReference fr)) continue;
                    if (fr.Name != wf.Name || fr.DeclaringType.FullName != wf.DeclaringType.FullName) continue;
                    if (!IsWrite(code[i + 1], "Byte")) continue;

                    if (mutate)
                    {
                        il.InsertAfter(code[i], Instruction.Create(OpCodes.Call, min));
                        il.InsertAfter(code[i], Instruction.Create(OpCodes.Ldc_I4, 255));

                        i += 2;
                    }

                    clamped++;
                }
            }

            if (clamped > 0)
                r.Notes.Add($"存档：仍按字节写的 {clamped} 处已改为夹在 255（格式不变，只丢精度不回绕）");
        }

        // ── 小工具 ────────────────────────────────────────────

        private static MethodDefinition Method(ModuleDefinition module, string type, string name)
        {
            TypeDefinition t = module.GetType(type);

            return t?.Methods.FirstOrDefault(x => x.Name == name && x.HasBody);
        }

        private static bool IsCargoField(object operand, string name) =>
            operand is FieldReference f && f.Name == name && f.DeclaringType.Name == "Cargo";

        private static bool IsWrite(Instruction ins, string argType) =>
            ins.Operand is MethodReference mr && mr.Name == "Write" &&
            mr.DeclaringType.Name == "BinaryWriter" &&
            mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.Name == argType;

        private static bool IsRead(Instruction ins, string kind) =>
            ins.Operand is MethodReference mr && mr.Name == "Read" + kind &&
            mr.DeclaringType.Name == "BinaryReader" && mr.Parameters.Count == 0;

        private static MethodReference WriterMethod(ModuleDefinition module, string argType)
        {
            Type t = argType == "Int16" ? typeof(short) : typeof(byte);

            return module.ImportReference(typeof(System.IO.BinaryWriter).GetMethod("Write", new[] { t }));
        }

        private static MethodReference ReaderMethod(ModuleDefinition module, string kind) =>
            module.ImportReference(typeof(System.IO.BinaryReader).GetMethod("Read" + kind, Type.EmptyTypes));
    }
}
