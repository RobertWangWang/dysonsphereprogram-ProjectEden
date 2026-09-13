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

            /// <summary>已孪生的语句数（形状认得<b>而且</b>发射代码写好了）</summary>
            internal int Twinned;

            /// <summary>形状认得、但发射代码还没写的语句数</summary>
            internal int Recognized;

            /// <summary>认得但没实现的形状 → 次数</summary>
            internal readonly Dictionary<string, int> Pending = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>确认不需要孪生的语句数（取数组长度、判空、循环边界）</summary>
            internal int NoTwinNeeded;

            /// <summary>品质被<b>明确丢弃</b>的语句数</summary>
            internal int Dropped;

            /// <summary>丢弃发生在哪些方法里——这条要打进日志，缺口不许沉默</summary>
            internal readonly Dictionary<string, int> DropSites = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>涉及的方法体数</summary>
            internal int Methods;

            /// <summary>新增的孪生局部变量数</summary>
            internal int TwinLocals;

            /// <summary>
            /// 用到侧信道寄存器的次数，也就是「载荷参数 → <c>Q&lt;槽位&gt;</c>」那条路走了几次。
            ///
            /// <b>它必须会动。</b> 恒为 0 就说明跨方法边界那条路一次都没走通，
            /// 而那是静默的：品质照样是 0，报告照样说孪生成功。
            /// </summary>
            internal int ChannelUses;

            /// <summary>形状表里没有的东西：签名 → 次数</summary>
            internal readonly Dictionary<string, int> Unhandled = new Dictionary<string, int>(StringComparer.Ordinal);

            internal readonly Dictionary<string, string> UnhandledExample =
                new Dictionary<string, string>(StringComparer.Ordinal);
        }

        internal static Report Apply(ModuleDefinition module)
        {
            Report check = Run(module, false);

            if (check.Blockers.Count > 0 || check.Unhandled.Count > 0 || check.Pending.Count > 0)
                return check;

            return Run(module, true);
        }

        /// <summary>
        /// <b>只给离线校验用：把已经写好发射器的那些形状真的发射出去</b>，
        /// 哪怕还有别的形状没实现。
        ///
        /// <b>为什么需要它。</b> <see cref="Apply"/> 在形状表补齐之前拒绝改写——这是对的，
        /// 但副作用是<b>发射代码一次都不会被执行</b>。写完四十几个发射器再一起发现
        /// 全都产出非法 IL，代价太大；每写一个就让它真的跑一遍、写盘、重读、断言，
        /// 错误才会在写下它的那一刻被抓住。
        ///
        /// <b>Patcher 永远不调它。</b> 部分发射出来的程序集在语义上是半截的
        /// （品质只在一部分路径上流动），只配拿来验证 IL 合不合法。
        /// </summary>
        internal static Report ApplyPartial(ModuleDefinition module)
        {
            Report check = Run(module, false);

            if (check.Blockers.Count > 0) return check;

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

            // **缺口不许沉默，而且必须排在提前返回之前。**
            // 第一版把这一段写在「形状表没补齐就返回」的后面，结果丢弃信息在
            // 补齐之前从来不打印——正好违反了它自己要执行的那条规矩。
            if (r.Dropped > 0)
                r.Notes.Add(
                    $"**品质在 {r.Dropped} 处被明确丢弃**（主干道之外没有孪生槽位可去）：" +
                    string.Join("、", r.DropSites
                        .OrderByDescending(kv => kv.Value)
                        .Select(kv => $"{kv.Key}×{kv.Value}")
                        .ToArray()));

            CheckEmitters(r);

            if (r.Pending.Count > 0)
                r.Notes.Add(
                    $"形状已识别但**发射代码还没写**：{r.Pending.Count} 种、共 {r.Recognized} 处。" +
                    "它们和「没识别」一样会挡住变换——认得不等于做得到。");

            if (r.Unhandled.Count > 0 || r.Pending.Count > 0)
            {
                if (r.Unhandled.Count > 0)
                    r.Notes.Add(
                        $"形状表还缺 {r.Unhandled.Count} 种（共 {r.Unhandled.Values.Sum()} 处）。");

                r.Notes.Add("变换整个不生效，游戏与不装时一致。补齐并实现形状即可落地。");

                return r;
            }

            r.Notes.Add(
                $"品质搬运层：{r.Twinned} 条语句已孪生、{r.NoTwinNeeded} 条确认不需要孪生，" +
                $"涉及 {r.Methods} 个方法体，新增 {r.TwinLocals} 个孪生局部变量，" +
                $"{r.ChannelUses} 处走侧信道。");

            r.Applied = r.Blockers.Count == 0;

            return r;
        }

        /// <summary>
        /// <b>发射器表不许比形状表超前。</b> <see cref="Emitted"/> 里出现了
        /// <see cref="TwinShapes"/> 没有的形状，说明写了一个永远不会被调用的发射器——
        /// 它看起来像「已经做了」，实际一次都不会跑。
        /// </summary>
        private static void CheckEmitters(Report r)
        {
            foreach (string e in Emitted)
                if (!TwinShapes.Contains(e))
                    r.Blockers.Add(
                        $"发射器表里有 {e}，但形状表里没有——这个发射器永远不会被调用");
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
            var work = new List<Job>();

            // **两遍用同一个上下文。** 分类要调发射器判断「拼不拼得出来」，
            // 所以局部变量映射和参数槽位在分析遍就得建好；
            // 真正往方法体里加孪生局部只在 mutate 那一遍做。
            var ctx = new Ctx { Method = m, Twin = twin, Regs = regs };

            if (paramSlots.TryGetValue(m, out List<int> slots))
                for (var s = 0; s < slots.Count && s < regs.Count; s++)
                    ctx.ParamSlot[m.Parameters[slots[s]]] = s;

            foreach (VariableDefinition v in PayloadCarriers(m, code, twin))
                ctx.Locals[v] = null; // 占位：分类只关心「是不是载荷局部」

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
            var merge = false;

            for (var i = 0; i < code.Count; i++)
            {
                // 在分支目标处栈还没归零 = 有值从别的路径流过来，这是控制流汇合。
                // 重置切分点的同时**把这件事记住**——被切出来的那条语句是个 phi 汇合，
                // 它的值不是一条线性表达式，后向栈回溯拼不出来。
                if (targets.Contains(code[i].Offset) && depth != 0) { depth = 0; start = i; merge = true; }

                depth += Push(code[i]) - Pop(code[i]);

                if (depth < 0) depth = 0;

                if (depth != 0) continue;

                int from = start;
                int to = i;
                bool isMerge = merge;

                start = i + 1;
                merge = false;

                if (!HasMainline(code, from, to, twin)) continue;

                touched = true;

                string core = CoreShape(code, from, to, twin);

                // **控制流汇合（phi）单独成一类。** 典型是三元：
                //   inc = 条件 ? <长表达式> : 0
                // 两条值路径汇进同一条 stfld，后向栈回溯拼不出它，也不该拼——
                // 那需要一个孪生局部加上两条路径各存一次，是另一种形状。
                // **在分类阶段就分出去**，而不是等发射时变成 Blocker：
                // 形状表反映的应该是真实情况，不是「先当成简单形状、到时候再说」。
                if (isMerge || Merges(code, targets, from, to)) core += " [merge]";

                // **只有写好发射代码的形状才算认得。** 光在 TwinShapes 里不算——
                // 那会让变换报成功却什么都不做，品质恒为 0 而日志说一切正常。
                if (TwinShapes.Contains(core))
                {
                    if (Emitted.Contains(core))
                    {
                        // **分类直接问发射器能不能拼**，而不是先假定能、发射时再发现不能。
                        // 这样「认得」就字面等于「拼得出来」，两者之间不再有缝——
                        // 而那条缝正是这一期开头修掉的那种自欺的来源。
                        var job = new Job { Core = core, From = from, To = to };

                        if (Build(ctx, code, job) == null)
                        {
                            // 拼不出来的最常见原因是值不由载荷推导（比如机甲自己变出来的弹药）。
                            // 那是个**语义问题**而不是形状问题，所以单独标出来等人决定，
                            // 绝不悄悄按 0 处理——那等于静默丢品质。
                            r.Recognized++;

                            string tag = core + " [opaque]";

                            r.Pending[tag] = r.Pending.TryGetValue(tag, out int q) ? q + 1 : 1;

                            continue;
                        }

                        // 记下来，等这一遍扫完再统一插入——边扫边插会让后面的下标全错位
                        work.Add(job);

                        r.Twinned++;

                        continue;
                    }

                    r.Recognized++;

                    r.Pending[core] = r.Pending.TryGetValue(core, out int p) ? p + 1 : 1;

                    continue;
                }

                if (NoTwinShapes.Contains(core)) { r.NoTwinNeeded++; continue; }

                if (DropShapes.Contains(core))
                {
                    r.Dropped++;

                    string where = $"{m.DeclaringType.Name}::{m.Name}";

                    r.DropSites[where] = r.DropSites.TryGetValue(where, out int d) ? d + 1 : 1;

                    continue;
                }

                r.Unhandled[core] = r.Unhandled.TryGetValue(core, out int n) ? n + 1 : 1;

                if (!r.UnhandledExample.ContainsKey(core))
                    r.UnhandledExample[core] =
                        $"{m.DeclaringType.Name}::{m.Name} @IL_{code[from].Offset:X4}";
            }

            if (touched) r.Methods++;

            r.TwinLocals += carriers.Count;

            if (mutate && work.Count > 0) Emit(m, ctx, work, r);
        }

        /// <summary>一条待发射的孪生语句：原语句在方法体里的区间，以及它的核心形状。</summary>
        private sealed class Job
        {
            internal string Core;
            internal int From;
            internal int To;
        }

        /// <summary>
        /// 把记下来的孪生语句发射进方法体。
        ///
        /// <b>倒着插。</b> 正着插会让后面每一条 Job 的下标全部错位；倒着插时，
        /// 还没处理的那些都在前面，下标不受影响。
        ///
        /// <b>插在原语句之后，而不是之前。</b> 于是原语句上挂的跳转标签、异常块边界
        /// 一个都不用动——跳到这条语句的分支照样落在它自己头上，执行完再自然流进孪生语句。
        /// 换成插在前面就得把标签搬过去，而搬漏一个是静默的。
        ///
        /// <b>方法体长度会变，所以进来先 SimplifyMacros、改完 OptimizeMacros。</b>
        /// 不这么做的话，被撑开的短分支位移会被 Cecil 截断写进去，产出一条跳到半条指令
        /// 中间的分支——不在改写时报、不在写盘时报，等 Harmony 读它时才炸。实测过一次。
        /// </summary>
        private static void Emit(MethodDefinition m, Ctx ctx, List<Job> work, Report r)
        {
            m.Body.SimplifyMacros();

            // SimplifyMacros 会改写方法体，下标要按新的指令表重算
            var code = m.Body.Instructions;
            ILProcessor il = m.Body.GetILProcessor();

            // 分类遍放的是占位 null，这里把真正的孪生局部建出来并回填
            foreach (VariableDefinition v in ctx.Locals.Keys.ToList())
            {
                var tv = new VariableDefinition(m.Module.TypeSystem.Int32);

                m.Body.Variables.Add(tv);
                m.Body.InitLocals = true;

                ctx.Locals[v] = tv;

                r.TwinLocals++;
            }

            foreach (Job job in work.OrderByDescending(j => j.To))
            {
                if (job.To >= code.Count) continue;

                List<Instruction> emit = Build(ctx, code, job);

                if (emit == null)
                {
                    // 分析遍认得、发射遍却拼不出来——这必须是 Blocker 而不是静默跳过，
                    // 否则会得到「报告说孪生了 N 条、实际只写进去 M 条」，
                    // 而那正是这一期开头修掉的那种自欺。
                    r.Blockers.Add(
                        $"{m.DeclaringType.Name}::{m.Name} 里形状「{job.Core}」认得但拼不出孪生语句" +
                        $"（IL_{code[job.From].Offset:X4}）");

                    continue;
                }

                Instruction at = code[job.To];

                foreach (Instruction ins in emit)
                {
                    // 数一下真的走了侧信道的次数。**这个计数器必须会动**——
                    // 它恒为 0 就说明「载荷参数 → 寄存器」那条路一次都没走通，
                    // 而那是静默的：品质照样是 0，报告照样说孪生成功。
                    if (ins.Operand is FieldDefinition fd && ctx.Regs.Contains(fd)) r.ChannelUses++;

                    il.InsertAfter(at, ins);

                    at = ins;
                }
            }

            m.Body.OptimizeMacros();
        }

        /// <summary>语句内部（第一条之后）有没有分支目标——有就是控制流汇合。</summary>
        private static bool Merges(IList<Instruction> code, HashSet<int> targets, int from, int to)
        {
            for (int k = from + 1; k <= to; k++)
                if (targets.Contains(code[k].Offset))
                    return true;

            return false;
        }

        /// <summary>按形状拼出孪生语句。拼不出来返回 null。</summary>
        private static List<Instruction> Build(Ctx ctx, IList<Instruction> code, Job job)
        {
            Instruction store = code[job.To];

            if (!(store.Operand is FieldReference sf)) return null;

            if (!ctx.Twin.TryGetValue(sf.DeclaringType.FullName + "::" + sf.Name, out FieldDefinition tf))
                return null;

            switch (job.Core)
            {
                // X.inc = 常数  →  X.qua = 0
                case "ldc stfld:PAY":
                {
                    var outp = new List<Instruction>();

                    for (int k = job.From; k <= job.To - 2; k++)
                    {
                        if (!IsPureLoad(code[k])) return null;

                        outp.Add(Clone(code[k]));
                    }

                    outp.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                    outp.Add(Instruction.Create(OpCodes.Stfld, tf));

                    return outp;
                }

                // X.inc = <栈上的值>  →  X.qua = <那个值的品质版>
                case "stfld:PAY":
                {
                    int[] a = ArgStarts(code, job.To, 2);

                    if (a == null) return null;

                    int objFrom = a[0], valFrom = a[1];

                    var outp = new List<Instruction>();

                    // 对象表达式原样重放
                    for (int k = objFrom; k < valFrom; k++)
                    {
                        if (!IsPureLoad(code[k])) return null;

                        outp.Add(Clone(code[k]));
                    }

                    List<Instruction> val = TwinValue(ctx, code, valFrom, job.To - 1);

                    if (val == null) return null;

                    outp.AddRange(val);
                    outp.Add(Instruction.Create(OpCodes.Stfld, tf));

                    return outp;
                }

                default: return null;
            }
        }

        /// <summary>
        /// 装过载荷值的局部变量。种子是 <c>&lt;载荷载入&gt; stloc L</c>，
        /// 然后沿 <c>ldloc A ; stloc B</c> 传播到不动点——A 装过载荷，B 也就装过。
        /// </summary>
        private static IEnumerable<VariableDefinition> PayloadCarriers(MethodDefinition m,
            IList<Instruction> code, IDictionary<string, FieldDefinition> twin)
        {
            var set = new HashSet<VariableDefinition>();
            bool grew = true;

            while (grew)
            {
                grew = false;

                for (var i = 1; i < code.Count; i++)
                {
                    if (!IsStloc(code[i])) continue;

                    VariableDefinition dst = VarOf(m, code[i]);

                    if (dst == null || set.Contains(dst)) continue;

                    Instruction src = code[i - 1];

                    bool carries = IsMainline(src, twin);

                    if (!carries)
                    {
                        VariableDefinition sv = VarOf(m, src);

                        carries = sv != null && set.Contains(sv);
                    }

                    if (!carries) continue;

                    set.Add(dst);
                    grew = true;
                }
            }

            return set;
        }

        // ── 基础设施一：后向栈深度找实参边界 ──────────────────
        //
        // 要把「对象」和「值」分开，只能靠栈。`stfld` 弹两个：对象和值，
        // 但它们各自是**表达式**而不是单条指令（`ldarg.0 ldfld foo` 是一个值），
        // 所以往回数固定条数是错的——CargoIncWidener 当初就是在
        // StorageComponent.AddCargo 上被这一点绊住的。

        /// <summary>
        /// <paramref name="at"/> 这条指令弹掉的 <paramref name="argc"/> 个值，
        /// 各自的表达式从哪条指令开始。返回值按<b>压栈顺序</b>排（第一个实参在前）。
        /// 认不出来（遇到分支目标、栈没归零）就返回 null——<b>宁可不改</b>。
        /// </summary>
        private static int[] ArgStarts(IList<Instruction> code, int at, int argc)
        {
            var starts = new int[argc];
            var depth = 0;
            int need = argc;
            int i = at - 1;

            for (; i >= 0 && need > 0; i--)
            {
                depth += Push(code[i]) - Pop(code[i]);

                if (depth <= 0) continue;

                // 这条指令把第 need 个实参的净值推了上来，它就是那个表达式的开头
                starts[--need] = i;
                depth = 0;
            }

            return need == 0 ? starts : null;
        }

        // ── 基础设施二：载荷值的四个来源 ──────────────────────

        /// <summary>一个方法体内的孪生上下文：局部变量映射、参数槽位、孪生字段表。</summary>
        private sealed class Ctx
        {
            internal MethodDefinition Method;
            internal IDictionary<string, FieldDefinition> Twin;
            internal IList<FieldDefinition> Regs;
            internal Dictionary<VariableDefinition, VariableDefinition> Locals =
                new Dictionary<VariableDefinition, VariableDefinition>();
            internal Dictionary<ParameterDefinition, int> ParamSlot =
                new Dictionary<ParameterDefinition, int>();
        }

        /// <summary>
        /// 发射一个载荷值表达式 <c>[from, to]</c> 的<b>品质版本</b>。
        /// 认不出来返回 null，调用方据此放弃整条语句。
        ///
        /// 四个来源各对应一种发射，见类注释的概念模型。<b>第三条（载荷参数 → 侧信道寄存器）
        /// 是「给方法加品质参数」那条被推翻的路的替身</b>：值照样跨过方法边界，签名一个不动。
        /// </summary>
        private static List<Instruction> TwinValue(Ctx ctx, IList<Instruction> code, int from, int to)
        {
            var outp = new List<Instruction>();

            Instruction last = code[to];

            // (a) 载荷字段：重放寻址前缀，换成孪生字段
            if (last.Operand is FieldReference fr &&
                ctx.Twin.TryGetValue(fr.DeclaringType.FullName + "::" + fr.Name, out FieldDefinition tf) &&
                (last.OpCode == OpCodes.Ldfld || last.OpCode == OpCodes.Ldsfld))
            {
                for (int k = from; k < to; k++)
                {
                    if (!IsPureLoad(code[k])) return null;

                    outp.Add(Clone(code[k]));
                }

                outp.Add(Instruction.Create(last.OpCode, tf));

                return outp;
            }

            // 其余三种都只认单条指令的表达式——多条的先不碰，报成未实现比猜着改安全
            if (from != to) return null;

            // (b) 装过载荷的局部变量 → 它的孪生
            VariableDefinition v = VarOf(ctx.Method, last);

            if (v != null && ctx.Locals.TryGetValue(v, out VariableDefinition tv))
            {
                // 分类遍里孪生局部还没建（占位是 null）。这时只要能走到这一步，
                // 就说明「拼得出来」——真正的指令由 mutate 那一遍发射。
                if (tv == null) return outp;

                outp.Add(Instruction.Create(OpCodes.Ldloc, tv));

                return outp;
            }

            // (c) 载荷参数 → 侧信道寄存器
            ParameterDefinition p = ParamOf(ctx.Method, last);

            if (p != null && ctx.ParamSlot.TryGetValue(p, out int slot) && slot < ctx.Regs.Count)
            {
                outp.Add(Instruction.Create(OpCodes.Ldsfld, ctx.Regs[slot]));

                return outp;
            }

            // (d) 常数 → 0
            if (last.OpCode.Name.StartsWith("ldc", StringComparison.Ordinal))
            {
                outp.Add(Instruction.Create(OpCodes.Ldc_I4_0));

                return outp;
            }

            return null;
        }

        /// <summary>纯载入：可以原样重放而没有副作用。</summary>
        private static bool IsPureLoad(Instruction i)
        {
            string n = i.OpCode.Name;

            return n.StartsWith("ldarg", StringComparison.Ordinal)
                   || n.StartsWith("ldloc", StringComparison.Ordinal)
                   || n.StartsWith("ldfld", StringComparison.Ordinal)
                   || n.StartsWith("ldsfld", StringComparison.Ordinal)
                   || n.StartsWith("ldelem", StringComparison.Ordinal)
                   || n.StartsWith("ldc", StringComparison.Ordinal)
                   || n == "dup" || n == "conv.i4" || n == "conv.u1" || n == "conv.i2";
        }

        private static VariableDefinition VarOf(MethodDefinition m, Instruction i)
        {
            if (i.Operand is VariableDefinition v) return v;

            if (i.OpCode == OpCodes.Ldloc_0 || i.OpCode == OpCodes.Stloc_0) return At(m, 0);
            if (i.OpCode == OpCodes.Ldloc_1 || i.OpCode == OpCodes.Stloc_1) return At(m, 1);
            if (i.OpCode == OpCodes.Ldloc_2 || i.OpCode == OpCodes.Stloc_2) return At(m, 2);
            if (i.OpCode == OpCodes.Ldloc_3 || i.OpCode == OpCodes.Stloc_3) return At(m, 3);

            return null;
        }

        private static VariableDefinition At(MethodDefinition m, int i) =>
            i < m.Body.Variables.Count ? m.Body.Variables[i] : null;

        private static ParameterDefinition ParamOf(MethodDefinition m, Instruction i)
        {
            if (i.Operand is ParameterDefinition p) return p;

            int idx = i.OpCode == OpCodes.Ldarg_0 ? 0
                : i.OpCode == OpCodes.Ldarg_1 ? 1
                : i.OpCode == OpCodes.Ldarg_2 ? 2
                : i.OpCode == OpCodes.Ldarg_3 ? 3
                : -1;

            if (idx < 0) return null;

            // 实例方法的 ldarg.0 是 this，不是形参
            if (m.HasThis)
            {
                if (idx == 0) return null;

                idx--;
            }

            return idx < m.Parameters.Count ? m.Parameters[idx] : null;
        }

        /// <summary>
        /// 复制一条指令。<b>不能直接复用原对象</b>——同一个 <c>Instruction</c> 出现在两个位置，
        /// Cecil 的偏移计算和分支目标都会错乱。操作数原样带过去（局部变量、参数、字段
        /// 都是引用，指向同一个东西正是我们要的）。
        /// </summary>
        private static Instruction Clone(Instruction i) =>
            i.Operand == null
                ? Instruction.Create(i.OpCode)
                : CloneWithOperand(i);

        private static Instruction CloneWithOperand(Instruction i)
        {
            switch (i.Operand)
            {
                case FieldReference f: return Instruction.Create(i.OpCode, f);
                case MethodReference me: return Instruction.Create(i.OpCode, me);
                case TypeReference t: return Instruction.Create(i.OpCode, t);
                case VariableDefinition v: return Instruction.Create(i.OpCode, v);
                case ParameterDefinition p: return Instruction.Create(i.OpCode, p);
                case string s: return Instruction.Create(i.OpCode, s);
                case int n: return Instruction.Create(i.OpCode, n);
                case sbyte sb: return Instruction.Create(i.OpCode, sb);
                case byte b: return Instruction.Create(i.OpCode, b);
                case long l: return Instruction.Create(i.OpCode, l);
                case float fl: return Instruction.Create(i.OpCode, fl);
                case double d: return Instruction.Create(i.OpCode, d);
                default: return Instruction.Create(i.OpCode);
            }
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

        // ── 形状表：每一种形状必须落进三档之一 ────────────────
        //
        // 普查给出主干道共 48 种核心形状。三档之外的一律报成 Unhandled，
        // 于是变换整个不生效。**这正是增量落地的机制**：
        // 补一条形状，覆盖率涨一点，全齐了才会第一次真的改字节。

        /// <summary>
        /// <b>已经写好发射代码</b>的形状。<see cref="TwinShapes"/> 里只有出现在这里的，
        /// 才算真的「认得」。
        ///
        /// <b>这条约束是补上去的，而它修的是我自己埋的雷。</b> 两遍式只保证
        /// 「形状表没齐就不改字节」——它保证不了「表齐了但发射器是空的」。
        /// 那种情况下变换会<b>报成功、实际什么都不做</b>：品质恒为 0，而日志说一切正常。
        /// 这正是这个仓库最怕的失败形态（每一步都成功、功能却不在），
        /// 所以「认得」必须等于「有发射器」，由 <see cref="CheckEmitters"/> 每次核对。
        /// </summary>
        private static readonly HashSet<string> Emitted = new HashSet<string>(new[]
        {
            // X.inc = <常数>  →  X.qua = 0
            //
            // 第一个落地的发射器，挑它是因为它**自包含**：不依赖参数到寄存器的映射、
            // 也不依赖局部变量孪生，而那两样是后面绝大多数形状都要用的。
            // 先用它把发射机制本身（前缀提取、插入位置、标签处理）跑通并验证。
            "ldc stfld:PAY",

            // X.inc = <栈上的值>  →  X.qua = <那个值的品质版>
            //
            // 第二个。它是两块基础设施的第一个用户：要靠**后向栈深度**把「对象」和「值」
            // 分开（往回数固定条数是错的，值本身是个表达式），
            // 再靠**四个来源**把那个值翻译成品质版——其中「载荷参数 → 侧信道寄存器」
            // 就是被推翻的「加参数」那条路的替身。
            "stfld:PAY",
        }, StringComparer.Ordinal);

        /// <summary>
        /// 声明「打算孪生」的形状。<b>但只有同时出现在 <see cref="Emitted"/> 里的才真的算数</b>，
        /// 其余的会被当成未处理，于是变换整个不生效。
        /// 两张表分开，是为了让「已识别」和「已实现」这两件事在报告里分得开。
        /// </summary>
        private static readonly HashSet<string> TwinShapes = new HashSet<string>(new[]
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
            "ldlen newarr stfld:PAY",                               // X.incServed = new int[n]
            "newarr stfld:PAY",                                     // 同上，长度在栈上
            "ldflda:PAY call:Resize",                               // Array.Resize(ref X.incServed, n)
            "ldfld:PAY div stloc",                                  // local = X.inc / count（求等级）
            "ldfld:PAY div mul ldc add stloc",                      // 同上，再做一次换算
            "ldfld:PAY ldc stelem",                                 // arr[i] = X.inc
            "ldfld:PAY ldc dup stloc stelem stelem",                // 同上，顺手留个副本
        }, StringComparer.Ordinal);

        /// <summary>
        /// <b>不需要孪生</b>：这条语句碰的是载荷<b>数组对象本身</b>（取长度、判空、
        /// 拿它做循环边界），而不是里面的点数值。孪生数组和原数组永远等长、同生共死，
        /// 所以这里什么都不用做。
        ///
        /// <b>单独列一档而不是塞进「要孪生」</b>：如果哪天这里真的需要动，
        /// 它会以「形状消失了」的方式暴露出来，而不是被一条无害的孪生语句盖住。
        /// </summary>
        private static readonly HashSet<string> NoTwinShapes = new HashSet<string>(new[]
        {
            "ldfld:PAY ldlen blt.s",                                // for (i < incServed.Length)
            "ldfld:PAY ldlen beq.s",
            "ldfld:PAY ldlen call:Write",                           // 存长度，不是存点数
            "ldfld:PAY brfalse.s",                                  // if (incServed == null)
            "ldfld:PAY ldc ble.s",
        }, StringComparer.Ordinal);

        /// <summary>
        /// <b>品质在这里被丢弃</b>——而且是<b>说出来的丢弃</b>，不是静默的。
        ///
        /// 这些语句把载荷值送进主干道之外的地方（<c>PilerComponent</c> 自己的缓存字段、
        /// 垃圾堆、临时货包……）。主干道只有四个载荷，值一旦离开就没有对应的孪生槽位可去。
        ///
        /// <b>其中自动集装机那几条值得单独看一眼。</b> <c>PilerComponent.cacheCargoInc1/2</c>
        /// 是「正在叠的那一堆」的暂存，而集装正是这个 mod 传送带的核心（5000 层）。
        /// 品质在那里丢掉意味着<b>叠过的货会掉品质</b>。这是当前范围的已知代价，
        /// 变换会把丢弃次数报出来，日志里看得见——要消掉它，就得把那两个缓存字段
        /// 也加进主干道，那是一次明确的范围扩张，不该顺手做。
        /// </summary>
        private static readonly HashSet<string> DropShapes = new HashSet<string>(new[]
        {
            "ldfld:PAY stfld",                                      // 缓存字段 = X.inc
            "ldfld:PAY add stfld",
            "ldfld:PAY sub stfld",
            "ldfld:PAY call:AddTrashOnPlanet",                      // 扔到地上（TrashObject 没有孪生字段）
            "ldfld:PAY mul call:AddTrashOnPlanet",
            "ldfld:PAY call:AddTempCargo",                          // 拆传送带时的临时货包
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
