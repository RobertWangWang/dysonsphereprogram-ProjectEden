using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace ProjectEden.Preloader
{
    /// <summary>
    /// 物品品质 · 阶段 1b：给 90 个方法各长出孪生参数，并把 722 个调用点补齐。
    ///
    /// 设计稿在仓库根目录的 <c>物品品质.md</c>。1a 只加字段、一条 IL 都没动；
    /// 这一刀开始动方法体，所以下面每一条注意事项都是<b>已经炸过</b>或<b>已经量过</b>的。
    ///
    /// <b>为什么新参数一律追加在末尾。</b> <c>CargoIncWidener</c> 当初要<b>改</b>已有参数的类型，
    /// 所以必须用后向栈深度模拟去找每个实参的边界（「参数是表达式不是指令，
    /// 往回扫操作码会在 <c>StorageComponent.AddCargo</c> 上断掉」）。
    /// 而<b>追加</b>参数不需要那一套：新实参排在所有旧实参之后，
    /// 压栈点就是 <c>call</c> 指令<b>正前方</b>，一次分析都不用做。
    ///
    /// <b>但「正前方」不能用 Insert 实现。</b> 分支目标是对象引用；一条指向 <c>call</c> 的
    /// <c>br</c> 在插入之后仍然指着 <c>call</c>，于是<b>跳过</b>了新插的压栈指令——
    /// 栈上少了实参，而这在写盘时不报错。做法是把原 <c>call</c> 对象<b>就地</b>改写成
    /// 第一条压栈指令，再在它后面补上其余压栈和一条新的 <c>call</c>：
    /// 指向它的分支落在压栈上，自然地流到 call。异常处理块的边界同理，也是对象引用。
    ///
    /// <b>方法体一变长，短分支就可能装不下位移，而 Cecil 不会自动加宽</b>——
    /// 它会把超出一字节的位移截断写进去，产出一个跳到半条指令中间的分支。
    /// 这个错不在改写时报、不在写盘时报，要等 Harmony/MonoMod 读那个方法体时
    /// 才招一个指不到原因的异常。实测炸过一次。所以每个要动的方法体
    /// 进来先 <c>SimplifyMacros</c>、改完再 <c>OptimizeMacros</c>。
    ///
    /// <b>目标集里没有虚方法、没有 ldftn 来源</b>——实测唯一的那一族（<c>Player/DItemNotify</c>
    /// 委托及其两个处理器）已经作为「通知汇」摘掉了，见
    /// <see cref="QualityFieldAnalyzer"/>。这里仍然逐个复查并在命中时阻塞，
    /// 因为那是游戏更新最可能改变的一件事，而它的失败是运行时的。
    ///
    /// <b>此刻传进去的品质恒为 0。</b> 1b 只负责把管道接通，让值真的流动是 1c。
    /// </summary>
    internal static class QualityParamAdder
    {
        internal class Report
        {
            internal bool Applied;

            internal readonly List<string> Blockers = new List<string>();
            internal readonly List<string> Notes = new List<string>();

            internal int Methods;
            internal int Slots;
            internal int CallSites;
            internal int ByRefSlots;

            /// <summary>补过参的方法体数量（一个方法体里可能有多个调用点）</summary>
            internal int TouchedBodies;
        }

        internal static Report Apply(ModuleDefinition module)
        {
            Report check = Run(module, false);

            if (check.Blockers.Count > 0) return check;

            return Run(module, true);
        }

        private static Report Run(ModuleDefinition module, bool mutate)
        {
            var r = new Report();

            // ── 闸：1a 的字段必须已经在，否则 1b 接的是一根不存在的管子 ──
            QualityFieldAdder.Report fields = QualityFieldAdder.Verify(module);

            if (!fields.Applied)
            {
                r.Blockers.Add("孪生字段还没就位（1a 未完成或失败），1b 不动");

                foreach (string b in fields.Blockers) r.Blockers.Add("  " + b);

                return r;
            }

            Dictionary<MethodDefinition, List<int>> targets =
                QualityFieldAnalyzer.SelectTwinParams(module, out int _);

            if (targets.Count == 0)
            {
                r.Blockers.Add("一个要长孪生参数的方法都没选出来——选取规则或程序集变了");

                return r;
            }

            // ── 1. 先复查禁止形状，还不动手 ──
            foreach (MethodDefinition m in targets.Keys)
            {
                if (m.IsVirtual || m.IsAbstract || m.HasOverrides)
                    r.Blockers.Add(
                        $"{m.FullName} 是虚方法/抽象方法/带显式覆盖——改签名会断掉覆盖链，" +
                        "而那是运行时才炸。先决定它该怎么处理");

                if (!m.HasBody && !m.IsAbstract)
                    r.Blockers.Add($"{m.FullName} 没有方法体，形状不认识");
            }

            FindFunctionPointers(module, targets, r);

            if (r.Blockers.Count > 0) return r;

            // ── 2. 记下每个目标要追加几个参数、其中几个是引用 ──
            //
            // **先记数再改签名。** 改完签名之后 Parameters.Count 就变了，
            // 那时再去数「原本有几个 inc 参数」会把刚加的也数进去。
            var plan = new Dictionary<MethodDefinition, List<bool>>();

            foreach (KeyValuePair<MethodDefinition, List<int>> kv in targets)
            {
                var byRef = new List<bool>();

                foreach (int i in kv.Value)
                {
                    ParameterDefinition p = kv.Key.Parameters[i];

                    string twinName = QualityFieldAdder.TwinName(p.Name);

                    if (twinName == null)
                    {
                        r.Blockers.Add($"{kv.Key.FullName} 的参数 {p.Name} 推不出孪生名");

                        continue;
                    }

                    if (kv.Key.Parameters.Any(x => x.Name == twinName))
                    {
                        r.Blockers.Add($"{kv.Key.FullName} 已经有一个叫 {twinName} 的参数");

                        continue;
                    }

                    byRef.Add(p.ParameterType.IsByReference);
                }

                plan[kv.Key] = byRef;
            }

            if (r.Blockers.Count > 0) return r;

            // ── 3. 动手：追加参数 ──
            TypeReference int32 = module.TypeSystem.Int32;

            foreach (KeyValuePair<MethodDefinition, List<int>> kv in targets)
            {
                MethodDefinition m = kv.Key;

                foreach (int i in kv.Value)
                {
                    ParameterDefinition p = m.Parameters[i];

                    string twinName = QualityFieldAdder.TwinName(p.Name);

                    TypeReference ty = p.ParameterType.IsByReference
                        ? (TypeReference)new ByReferenceType(int32)
                        : int32;

                    if (mutate)
                        m.Parameters.Add(new ParameterDefinition(twinName, ParameterAttributes.None, ty));

                    r.Slots++;

                    if (p.ParameterType.IsByReference) r.ByRefSlots++;
                }

                r.Methods++;
            }

            // ── 4. 动手：调用点补参 ──
            FixCallSites(module, plan, int32, r, mutate);

            if (r.Blockers.Count > 0) return r;

            r.Notes.Add(
                $"品质孪生参数：{r.Methods} 个方法 / {r.Slots} 个参数位" +
                $"（其中引用型 {r.ByRefSlots} 个），补齐 {r.CallSites} 个调用点、" +
                $"涉及 {r.TouchedBodies} 个方法体。传进去的品质此刻恒为 0——接线在 1c。");

            r.Applied = true;

            return r;
        }

        /// <summary>
        /// 对<b>改写并写盘之后重新读回来</b>的模块做核对。
        ///
        /// 核的是 1b 唯一真正危险的那件事：<b>有没有哪个调用点漏补了实参</b>。
        /// 漏补的表现是栈上少一个值，而这在改写时不报、在 Cecil 写盘时也不报。
        ///
        /// 检查方式是精确的而不是估计的：我们插入的形状是固定的
        /// （值型一条 <c>ldc.i4.0</c>；引用型三条 <c>ldc.i4.0 / stloc / ldloca</c>），
        /// 所以每个目标调用点的正前方必须恰好是这些指令，数目和顺序都对得上。
        /// <b>不做通用栈深度模拟</b>——那要处理分支汇合，复杂度远高于它能多抓到的东西。
        /// </summary>
        internal static Report Verify(ModuleDefinition module)
        {
            var r = new Report();

            Dictionary<MethodDefinition, List<int>> targets =
                QualityFieldAnalyzer.SelectTwinParams(module, out int _);

            var plan = new Dictionary<MethodDefinition, List<bool>>();

            foreach (KeyValuePair<MethodDefinition, List<int>> kv in targets)
            {
                var byRef = new List<bool>();

                foreach (int i in kv.Value) byRef.Add(kv.Key.Parameters[i].ParameterType.IsByReference);

                plan[kv.Key] = byRef;

                // 签名上必须真的多出了对应数量的 Int32 / Int32& 尾参
                int want = byRef.Count;
                int have = 0;

                for (int i = kv.Key.Parameters.Count - 1; i >= 0 && have < want; i--)
                {
                    TypeReference ty = kv.Key.Parameters[i].ParameterType;
                    TypeReference elem = ty is ByReferenceType br ? br.ElementType : ty;

                    if (elem.MetadataType != MetadataType.Int32) break;

                    have++;
                }

                if (have < want)
                    r.Blockers.Add($"{kv.Key.FullName} 尾部只有 {have} 个 Int32 参数，期望至少 {want}");

                r.Methods++;
                r.Slots += want;
                r.ByRefSlots += byRef.Count(b => b);
            }

            foreach (TypeDefinition t in AllTypes(module))
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;

                var code = m.Body.Instructions;

                for (var i = 0; i < code.Count; i++)
                {
                    List<bool> shape = ShapeOf(code[i], plan);

                    if (shape == null) continue;

                    r.CallSites++;

                    if (!PrecededByPushes(code, i, shape))
                        r.Blockers.Add(
                            $"{m.FullName} 在 IL_{code[i].Offset:X4} 调用 " +
                            $"{((MethodReference)code[i].Operand).Name}，" +
                            "但正前方不是我们补的品质实参——这个调用点漏补了，栈上少一个值");
                }
            }

            r.Applied = r.Blockers.Count == 0;

            return r;
        }

        /// <summary>调用点正前方是不是恰好是 <paramref name="shape"/> 对应的那几条压栈指令。</summary>
        private static bool PrecededByPushes(IList<Instruction> code, int callAt, IList<bool> shape)
        {
            int at = callAt - 1;

            // 倒着核对：最后一个实参离 call 最近
            for (int k = shape.Count - 1; k >= 0; k--)
            {
                if (shape[k])
                {
                    if (at - 2 < 0) return false;
                    if (!IsLdloca(code[at])) return false;
                    if (!IsStloc(code[at - 1])) return false;
                    if (!IsLdcI4Zero(code[at - 2])) return false;

                    at -= 3;
                }
                else
                {
                    if (at < 0) return false;
                    if (!IsLdcI4Zero(code[at])) return false;

                    at -= 1;
                }
            }

            return true;
        }

        private static bool IsLdcI4Zero(Instruction i) =>
            i.OpCode == OpCodes.Ldc_I4_0 ||
            (i.OpCode == OpCodes.Ldc_I4 && i.Operand is int n && n == 0) ||
            (i.OpCode == OpCodes.Ldc_I4_S && i.Operand is sbyte s && s == 0);

        private static bool IsStloc(Instruction i) =>
            i.OpCode == OpCodes.Stloc || i.OpCode == OpCodes.Stloc_S ||
            i.OpCode == OpCodes.Stloc_0 || i.OpCode == OpCodes.Stloc_1 ||
            i.OpCode == OpCodes.Stloc_2 || i.OpCode == OpCodes.Stloc_3;

        private static bool IsLdloca(Instruction i) =>
            i.OpCode == OpCodes.Ldloca || i.OpCode == OpCodes.Ldloca_S;

        /// <summary>
        /// 目标方法有没有被 <c>ldftn</c> / <c>ldvirtftn</c> 取过函数指针。
        ///
        /// 取了就意味着有个委托类型的签名和它绑在一起，而改了方法签名、
        /// 委托却没改，<b>要到运行时构造委托那一刻才炸</b>。实测目标集里唯一的一族
        /// 已经作为通知汇摘掉，但这项复查要留着——游戏更新最容易改变的就是这个，
        /// 而它没有任何编译期或写盘期的信号。
        /// </summary>
        private static void FindFunctionPointers(ModuleDefinition module,
            Dictionary<MethodDefinition, List<int>> targets, Report r)
        {
            var set = new HashSet<MethodDefinition>(targets.Keys);

            foreach (TypeDefinition t in AllTypes(module))
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;

                foreach (Instruction ins in m.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Ldftn && ins.OpCode != OpCodes.Ldvirtftn) continue;

                    MethodDefinition target = (ins.Operand as MethodReference)?.Resolve();

                    if (target != null && set.Contains(target))
                        r.Blockers.Add(
                            $"{m.FullName} 对目标方法 {target.FullName} 取了函数指针——" +
                            "改签名会让绑上去的委托在运行时失配。先把它排除或连委托一起改");
                }
            }
        }

        private static void FixCallSites(ModuleDefinition module,
            Dictionary<MethodDefinition, List<bool>> plan, TypeReference int32, Report r, bool mutate)
        {
            foreach (TypeDefinition t in AllTypes(module))
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;

                // 先扫一遍：这个方法体里到底有没有要补的调用点。
                // 没有就绝不碰它——SimplifyMacros/OptimizeMacros 会重写整个方法体，
                // 对两万多个方法无差别做一遍既慢又平白扩大了出错面。
                var sites = 0;
                var needsLocal = false;

                foreach (Instruction ins in m.Body.Instructions)
                {
                    List<bool> shape = ShapeOf(ins, plan);

                    if (shape == null) continue;

                    sites++;

                    if (shape.Any(b => b)) needsLocal = true;
                }

                if (sites == 0) continue;

                r.TouchedBodies++;
                r.CallSites += sites;

                if (!mutate) continue;

                m.Body.SimplifyMacros();

                VariableDefinition dummy = null;

                if (needsLocal)
                {
                    dummy = new VariableDefinition(int32);

                    m.Body.Variables.Add(dummy);
                    m.Body.InitLocals = true;
                }

                var il = m.Body.GetILProcessor();

                // 倒着走：就地改写会在当前位置后面插入指令，正着走会重新访问到它们
                for (int i = m.Body.Instructions.Count - 1; i >= 0; i--)
                {
                    Instruction ins = m.Body.Instructions[i];

                    List<bool> shape = ShapeOf(ins, plan);

                    if (shape == null) continue;

                    Rewrite(il, ins, shape, dummy);
                }

                m.Body.OptimizeMacros();
            }
        }

        /// <summary>这条指令是不是一个要补参的调用点；是的话返回要补的形状（每一位：是否引用型）。</summary>
        private static List<bool> ShapeOf(Instruction ins, Dictionary<MethodDefinition, List<bool>> plan)
        {
            // newobj 也要认：StationStore / CountInc / ItemPackage / CargoView / TrashObject /
            // IDCNTINC / IDCNTMAX 的构造函数都带 inc 参数，而它们是 newobj 出来的，不是 call。
            if (ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt && ins.OpCode != OpCodes.Newobj)
                return null;

            MethodDefinition target = (ins.Operand as MethodReference)?.Resolve();

            if (target == null) return null;

            return plan.TryGetValue(target, out List<bool> shape) && shape.Count > 0 ? shape : null;
        }

        /// <summary>
        /// 在 <paramref name="call"/> 正前方补上实参。
        ///
        /// <b>就地改写 call 对象，而不是在它前面 Insert</b>——理由见类注释：
        /// 分支目标和异常处理块边界都是对象引用，Insert 会让它们跳过新压的实参。
        ///
        /// 引用型实参要先把那个哑元局部<b>清零</b>再取地址。哑元在一个方法体里是共用的，
        /// 上一个调用点可能已经往里写过东西；不清零的话，一个 <c>ref</c>（而非 <c>out</c>）
        /// 形参读到的是上一次调用的残值。值本身在 1b 里没人用，但<b>确定性比省三条指令重要</b>。
        /// </summary>
        private static void Rewrite(ILProcessor il, Instruction call, List<bool> shape, VariableDefinition dummy)
        {
            var pushes = new List<Instruction>();

            foreach (bool byRef in shape)
            {
                if (byRef)
                {
                    pushes.Add(il.Create(OpCodes.Ldc_I4_0));
                    pushes.Add(il.Create(OpCodes.Stloc, dummy));
                    pushes.Add(il.Create(OpCodes.Ldloca, dummy));
                }
                else
                {
                    pushes.Add(il.Create(OpCodes.Ldc_I4_0));
                }
            }

            // 原 call 对象变成第一条压栈指令，其余压栈和新 call 依次跟在后面。
            // 于是所有指向原对象的分支/处理块边界都落在压栈的开头。
            var moved = il.Create(call.OpCode, (MethodReference)call.Operand);

            Instruction first = pushes[0];

            call.OpCode = first.OpCode;
            call.Operand = first.Operand;

            Instruction at = call;

            for (var k = 1; k < pushes.Count; k++)
            {
                il.InsertAfter(at, pushes[k]);

                at = pushes[k];
            }

            il.InsertAfter(at, moved);
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
