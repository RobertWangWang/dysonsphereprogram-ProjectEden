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

            /// <summary>
            /// 因为<b>还没进存档</b>而被跳过的语句数：读写存档的那一族。
            ///
            /// 和上面的 Dropped 分开计，因为两者的性质完全不同：Dropped 是主干道之外
            /// 没有槽位可去，是这一期范围的边界；这一个是**还没做**，做法也已经清楚
            /// （四个载荷各自的 Export/Import 加版本分支），只是不在最短路径上。
            /// 混在一起计会让「已知代价」和「待办」长得一样。
            /// </summary>
            internal int SaveSkipped;

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

            /// <summary>
            /// 认得但没发射的形状，各举一个出处。
            ///
            /// <b>没有出处的缺口查不动。</b> 「10 处 ldfld:PAY stloc 拼不出来」本身不指向任何地方，
            /// 要去读那 10 处的 IL 才知道卡在哪——而找出它们又得再写一遍切分逻辑。
            /// 缺口报告里带上出处，这一步就免了。
            /// </summary>
            internal readonly Dictionary<string, string> PendingExample =
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

            // ── 侧信道的 split：品质那边的「按比例分走」 ──
            MethodDefinition chSplit = channel.Methods
                .FirstOrDefault(x => x.Name == QualityChannelBuilder.SplitName);

            if (chSplit == null)
            {
                r.Blockers.Add($"侧信道缺 {QualityChannelBuilder.SplitName}");

                return r;
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

                    Process(m, twin, regs, paramSlots, chSplit, r, mutate);
                }
            }

            // **缺口不许沉默，而且必须排在提前返回之前。**
            // 第一版把这一段写在「形状表没补齐就返回」的后面，结果丢弃信息在
            // 补齐之前从来不打印——正好违反了它自己要执行的那条规矩。
            if (r.SaveSkipped > 0)
                r.Notes.Add(
                    $"**品质还没进存档**：{r.SaveSkipped} 处写存档的语句跳过了，读存档一侧一律置零，" +
                    "所以品质每次读档归零。四个主干道载荷各自的 Export/Import 加版本分支是独立的一步。");

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
            MethodDefinition chSplit, Report r, bool mutate)
        {
            var code = m.Body.Instructions.ToList();
            var work = new List<Job>();

            // **两遍用同一个上下文。** 分类要调发射器判断「拼不拼得出来」，
            // 所以局部变量映射和参数槽位在分析遍就得建好；
            // 真正往方法体里加孪生局部只在 mutate 那一遍做。
            var ctx = new Ctx { Method = m, Twin = twin, Regs = regs, ChannelSplit = chSplit };

            if (paramSlots.TryGetValue(m, out List<int> slots))
                for (var s = 0; s < slots.Count && s < regs.Count; s++)
                    ctx.ParamSlot[m.Parameters[slots[s]]] = s;

            // 先把语句切出来并算好形状，再据此判定载荷局部——**顺序不能反**。
            // 「一个局部只有在它的每一处赋值都能发射时才可用」这条规则要知道每条语句的形状，
            // 而形状的计算不依赖载荷局部，所以这一步拆得开。
            List<Stmt> stmts = Split(m, code, twin);

            // 载荷局部的零初始化不含载荷字段，切不出来，得反过来补——理由见 ConstInits。
            stmts.AddRange(ConstInits(m, code, stmts));
            stmts.Sort((x, y) => x.To.CompareTo(y.To));

            // **但「形状对」不等于「拼得出来」**，而拼不拼得出来又要先知道载荷局部——这是个环。
            // 形状是 ldfld:PAY stloc 却拼不出来的语句实测有 10 处；只按形状收，
            // 它们赋值的局部会被当成可用载荷，而它的孪生在那条路径上永远是 0,
            // 读的人把 0 当真值 —— 不报错，品质凭空归零。正是这一期反复在修的那种自欺。
            //
            // 解法是**从乐观集合出发跑不动点**：每轮拿当前集合分类，把定义语句拼不出来的
            // 局部剔掉再来一轮。集合只减不增，所以一定收敛，最多跑 |集合| 轮。
            var carriers = new HashSet<VariableDefinition>(SafeCarriers(m, code, stmts));

            while (true)
            {
                ctx.Locals.Clear();

                foreach (VariableDefinition v in carriers)
                    ctx.Locals[v] = null; // 占位：分类只关心「是不是载荷局部」

                var doomed = new HashSet<VariableDefinition>();

                foreach (Stmt st in stmts)
                {
                    if (!DefinesCarrier.Contains(st.Core)) continue;

                    VariableDefinition dv = VarOf(m, code[st.To]);

                    if (dv == null || !carriers.Contains(dv)) continue;

                    if (!Emitted.Contains(st.Core)
                        || Build(ctx, code, new Job { Core = st.Core, From = st.From, To = st.To }) == null)
                        doomed.Add(dv);
                }

                if (doomed.Count == 0) break;

                carriers.ExceptWith(doomed);

                // 合成语句随它服务的局部一起消失——留着只会以「拼不出来」的身份挡住变换
                stmts.RemoveAll(st => st.Synth && (Orphan(m, code, st.To, carriers)
                                                   || Orphan(m, code, st.From, carriers)));
            }

            // 2) 逐语句匹配
            var touched = false;

            foreach (Stmt st in stmts)
            {
                int from = st.From;
                int to = st.To;
                string core = st.Core;

                touched = true;

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

                            if (!r.PendingExample.ContainsKey(tag))
                                r.PendingExample[tag] =
                                    $"{m.DeclaringType.Name}::{m.Name} @IL_{code[from].Offset:X4}";

                            continue;
                        }

                        // 记下来，等这一遍扫完再统一插入——边扫边插会让后面的下标全错位
                        work.Add(job);

                        r.Twinned++;

                        continue;
                    }

                    r.Recognized++;

                    r.Pending[core] = r.Pending.TryGetValue(core, out int p) ? p + 1 : 1;

                    if (!r.PendingExample.ContainsKey(core))
                        r.PendingExample[core] =
                            $"{m.DeclaringType.Name}::{m.Name} @IL_{code[from].Offset:X4}";

                    continue;
                }

                if (NoTwinShapes.Contains(core)) { r.NoTwinNeeded++; continue; }

                if (SaveWriteShapes.Contains(core)) { r.SaveSkipped++; continue; }

                if (DropShapes.Contains(core))
                {
                    r.Dropped++;

                    string where = $"{m.DeclaringType.Name}::{m.Name}";

                    r.DropSites[where] = r.DropSites.TryGetValue(where, out int d) ? d + 1 : 1;

                    continue;
                }

                r.Unhandled[core] = r.Unhandled.TryGetValue(core, out int n) ? n + 1 : 1;

                // **一个出处不够。** 同一个形状出现在语义完全不同的方法里是常事
                // （`stfld:PAY [merge]` 里既有「按比例保留」也有「凭空生成」），
                // 只看第一个出处会按那一个的语义去写发射器，然后在别处悄悄做错事。
                string site = $"{m.DeclaringType.Name}::{m.Name} @IL_{code[from].Offset:X4}";

                r.UnhandledExample[core] = r.UnhandledExample.TryGetValue(core, out string had)
                    ? (had.Split(' ').Length > 12 ? had : had + "; " + site)
                    : site;
            }

            if (touched) r.Methods++;

            // 孪生局部的计数在 Emit 里做——那里才是真正建出来的地方。
            // 在这里按 ctx.Locals 数会把分析遍也算进去，导致数字翻倍。
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

        /// <summary>一条含载荷访问的语句：区间和它的核心形状。</summary>
        private sealed class Stmt
        {
            internal int From;
            internal int To;
            internal string Core;

            /// <summary>
            /// <b>合成语句</b>：不是按载荷切出来的，是认定载荷局部之后反过来补进来的
            /// （零初始化、复制、split_inc）。它只因为那个局部可用才存在，
            /// 所以局部一旦作废，它必须跟着消失——否则它会以「拼不出来」的身份留在报告里，
            /// 挡住整个变换，而它描述的那件事其实已经不需要做了。
            /// </summary>
            internal bool Synth;
        }

        /// <summary>
        /// 把方法体按<b>栈深归零</b>切成语句，只留含载荷访问的那些，并算好核心形状。
        ///
        /// 在分支目标处栈还没归零 = 有值从别的路径流过来，那是控制流汇合（phi）：
        /// 切分点重置的同时把这件事记下来，被切出来的那条语句标成 <c>[merge]</c>——
        /// 它的值不是一条线性表达式，后向栈回溯拼不出来，也不该拼。
        /// </summary>
        private static List<Stmt> Split(MethodDefinition m, IList<Instruction> code,
            IDictionary<string, FieldDefinition> twin)
        {
            var targets = new HashSet<int>();

            foreach (Instruction i in code)
            {
                if (i.Operand is Instruction one) targets.Add(one.Offset);
                else if (i.Operand is Instruction[] many)
                    foreach (Instruction x in many) targets.Add(x.Offset);
            }

            var outp = new List<Stmt>();
            var depth = 0;
            var start = 0;
            var merge = false;

            for (var i = 0; i < code.Count; i++)
            {
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

                string core = CoreShape(code, from, to, twin);

                if (isMerge || Merges(code, targets, from, to)) core += " [merge]";

                outp.Add(new Stmt { From = from, To = to, Core = core });
            }

            return outp;
        }

        /// <summary>
        /// 定义一个<b>可用载荷局部</b>的形状：这些语句的孪生赋值一定会被发射，
        /// 所以由它们赋值的局部，它的孪生局部一定有真值。
        /// </summary>
        private static readonly HashSet<string> DefinesCarrier = new HashSet<string>(new[]
        {
            "ldfld:PAY stloc",      // local = X.inc
            "ldfld:PAY div stloc",  // local = X.inc / count（求等级）
            "ldc stloc",            // local = 常量（零初始化，孪生值恒为 0）
            "ldloc stloc",          // local = 另一个载荷局部（复制传播）
            "call:split_inc stloc",  // local = split_inc(ref n, ref m, p)
        }, StringComparer.Ordinal);

        /// <summary>
        /// 把「载荷局部的<b>常量初始化</b>」补进语句表。
        ///
        /// 这些语句里没有任何载荷字段，所以按载荷切分时压根看不见它们——
        /// 而「一个局部的每一处赋值都要能发射」那条规则看得见，于是
        /// <c>int num = 0;</c> 这一行就足以把整个局部判死。实测
        /// <c>StationComponent::InternalTickLocal</c> 的 V_3 正是这样：
        /// 三处真正的 <c>V_3 = storage[i].inc</c> 全被开头那条 <c>ldc.i4.0</c> 拖下水。
        ///
        /// <b>顺序上它必须在载荷语句之后算</b>：谁是候选载荷局部，要先看载荷语句。
        /// </summary>
        private static List<Stmt> ConstInits(MethodDefinition m, IList<Instruction> code,
            List<Stmt> stmts)
        {
            var extra = new List<Stmt>();
            var cand = new HashSet<VariableDefinition>();

            foreach (Stmt st in stmts)
            {
                if (!DefinesCarrier.Contains(st.Core)) continue;

                VariableDefinition v = VarOf(m, code[st.To]);

                if (v != null) cand.Add(v);
            }

            if (cand.Count == 0) return extra;

            var taken = new HashSet<int>();

            // (1) 复制传播与 split_inc，一起跑到不动点——它们互相喂：
            //     `V_44 = V_3`（复制）→ `V_45 = split_inc(ref n, ref V_44, p)`（分走）
            //     → `X.inc -= V_45`（下游那 9 处 sub）。中间断一环，后面全塌。
            //
            // 这条以前是**明确拒绝**的，因为一个「标记成载荷却从不发射」的局部会让
            // 读它的地方拿到 0 并当成真值。现在拒绝的理由消失了：这里不只是把 B 标记成
            // 载荷，同时把 `Bq = Aq` 这条语句一起补进语句表，赋值和标记是一起发生的。
            //
            // 它是 split_inc 那一族的前置：`V_44 = V_3` 这一步不通，
            // 后面 `split_inc(ref n, ref V_44, p)` 就没有孪生可动。
            bool grew;

            do
            {
                grew = false;

                for (var i = 1; i < code.Count; i++)
                {
                    if (taken.Contains(i) || !IsStloc(code[i])) continue;

                    VariableDefinition dst = VarOf(m, code[i]);
                    VariableDefinition src = IsLdloc(code[i - 1]) ? VarOf(m, code[i - 1]) : null;

                    if (dst == null || src == null || !cand.Contains(src)) continue;

                    taken.Add(i);

                    extra.Add(new Stmt { From = i - 1, To = i, Core = "ldloc stloc", Synth = true });

                    if (cand.Add(dst)) grew = true;
                }

                for (var i = 1; i < code.Count; i++)
                {
                    if (taken.Contains(i) || !IsStloc(code[i])) continue;

                    VariableDefinition dst = VarOf(m, code[i]);

                    if (dst == null) continue;

                    int[] a = SplitIncArgs(code, i - 1);

                    // 这里**只认形状不判可行**：ref m 到底能不能给出孪生地址（载荷局部？
                    // 载荷参数？载荷字段？）由发射器说了算，说不行时后面的收缩不动点
                    // 会把这个局部再剔掉。在这里重判一遍等于把同一条规则写两份。
                    if (a == null) continue;

                    taken.Add(i);

                    extra.Add(new Stmt { From = a[0], To = i, Core = "call:split_inc stloc", Synth = true });

                    if (cand.Add(dst)) grew = true;
                }
            }
            while (grew);

            // (2) 零初始化。`ldc ; stloc` 自成一条语句：ldc 压一个、stloc 弹一个，
            // 栈深进出都是零，所以只看前一条指令就够，不必再跑一遍切分。
            for (var i = 1; i < code.Count; i++)
            {
                if (taken.Contains(i) || !IsStloc(code[i])) continue;

                VariableDefinition v = VarOf(m, code[i]);

                if (v == null || !cand.Contains(v)) continue;

                if (!code[i - 1].OpCode.Name.StartsWith("ldc", StringComparison.Ordinal)) continue;

                extra.Add(new Stmt { From = i - 1, To = i, Core = "ldc stloc", Synth = true });
            }

            return extra;
        }

        /// <summary>
        /// 认一次 <c>split_inc(ref n, ref m, p)</c> 调用：<paramref name="at"/> 是那条 call。
        /// 认出来就返回每个实参表达式的起点（实例方法多一个 this 在最前）。
        ///
        /// 按<b>参数类型</b>分，绝不按名字和个数——原版有两个同名重载
        /// （Byte 那个是传送带侧、Int32 那个是仓储账本），这正是 CargoIncWidener 踩过的坑。
        /// </summary>
        private static int[] SplitIncArgs(IList<Instruction> code, int at)
        {
            if (!IsSplitIncCall(code[at])) return null;

            var mr = (MethodReference)code[at].Operand;
            int argc = mr.Parameters.Count + (mr.HasThis ? 1 : 0);

            return ArgStarts(code, at, argc);
        }

        private static bool IsSplitIncCall(Instruction i)
        {
            if (i.OpCode != OpCodes.Call && i.OpCode != OpCodes.Callvirt) return false;
            if (!(i.Operand is MethodReference mr) || mr.Name != "split_inc") return false;
            if (mr.Parameters.Count != 3) return false;

            return mr.Parameters[0].ParameterType.IsByReference
                   && mr.Parameters[1].ParameterType.IsByReference;
        }

        /// <summary>
        /// <c>split_inc(ref n, ref m, p)</c> 这个表达式的品质版：
        /// <c>Split(n 调用后的值, ref m 的孪生, p)</c>，在栈上留下一个 int。
        ///
        /// 三件事各自都可能认不出来，认不出来就整条放弃：
        ///
        /// <b>n 取的是值不是址。</b> 原版调用返回时 n 已经被减掉了 p，而侧信道的
        /// <c>Split</c> 要的正是这个「减完之后的件数」——所以孪生这边不需要再减一次，
        /// 也**绝不能改成再调一次原版 split_inc**：那会把 n 再减一次 p，等于凭空吃掉物品。
        ///
        /// <b>m 要的是孪生的地址</b>，三种来源：载荷局部 → 孪生局部；载荷参数 → 侧信道
        /// 寄存器（<c>ldsflda</c>，静态字段一样能取址）；载荷字段 → 孪生字段。
        ///
        /// <b>p 原样重放</b>，所以必须是纯取值。
        /// </summary>
        private static List<Instruction> SplitTwin(Ctx ctx, IList<Instruction> code, int at, int lo)
        {
            if (ctx.ChannelSplit == null) return null;

            int[] a = SplitIncArgs(code, at);

            if (a == null || a.Length < 3 || a[a.Length - 3] < lo) return null;

            int nAt = a[a.Length - 3], mAt = a[a.Length - 2], pAt = a[a.Length - 1];

            List<Instruction> n = SplitCountAfter(ctx, code, nAt, mAt - 1);
            List<Instruction> mref = SplitTwinAddress(ctx, code, mAt, pAt - 1);

            if (n == null || mref == null) return null;

            var outp = new List<Instruction>();

            outp.AddRange(n);
            outp.AddRange(mref);

            for (int k = pAt; k < at; k++)
            {
                if (!IsPureLoad(code[k])) return null;

                outp.Add(Clone(code[k]));
            }

            outp.Add(Instruction.Create(OpCodes.Call,
                ctx.Method.Module.ImportReference(ctx.ChannelSplit)));

            return outp;
        }

        /// <summary><c>ref n</c> 那个实参在调用之后的<b>值</b>。</summary>
        private static List<Instruction> SplitCountAfter(Ctx ctx, IList<Instruction> code, int from, int to)
        {
            if (from == to)
            {
                if (code[to].OpCode == OpCodes.Ldloca || code[to].OpCode == OpCodes.Ldloca_S)
                {
                    if (!(code[to].Operand is VariableDefinition v)) return null;

                    return new List<Instruction> { Instruction.Create(OpCodes.Ldloc, v) };
                }

                ParameterDefinition p = ParamOf(ctx.Method, code[to]);

                if (code[to].OpCode == OpCodes.Ldarga || code[to].OpCode == OpCodes.Ldarga_S)
                    return p == null ? null : new List<Instruction> { Instruction.Create(OpCodes.Ldarg, p) };
            }

            // 字段地址：把地址表达式原样重放再解引用。地址表达式是纯取值，重放没有副作用。
            if (!code[to].OpCode.Name.StartsWith("ldflda", StringComparison.Ordinal)) return null;

            var outp = new List<Instruction>();

            for (int k = from; k <= to; k++)
            {
                if (!IsStructural(code[k], false)) return null;

                outp.Add(Clone(code[k]));
            }

            outp.Add(Instruction.Create(OpCodes.Ldind_I4));

            return outp;
        }

        /// <summary><c>ref m</c> 那个实参对应的<b>孪生地址</b>。</summary>
        private static List<Instruction> SplitTwinAddress(Ctx ctx, IList<Instruction> code, int from, int to)
        {
            if (from == to)
            {
                if (code[to].OpCode == OpCodes.Ldloca || code[to].OpCode == OpCodes.Ldloca_S)
                {
                    if (!(code[to].Operand is VariableDefinition v)) return null;
                    if (!ctx.Locals.TryGetValue(v, out VariableDefinition tv)) return null;

                    // 分类遍里孪生局部还没建，能走到这一步就说明拼得出来
                    return tv == null
                        ? new List<Instruction>()
                        : new List<Instruction> { Instruction.Create(OpCodes.Ldloca, tv) };
                }

                if (code[to].OpCode == OpCodes.Ldarga || code[to].OpCode == OpCodes.Ldarga_S)
                {
                    ParameterDefinition p = ParamOf(ctx.Method, code[to]);

                    if (p == null || !ctx.ParamSlot.TryGetValue(p, out int slot)
                                  || slot >= ctx.Regs.Count) return null;

                    return new List<Instruction> { Instruction.Create(OpCodes.Ldsflda, ctx.Regs[slot]) };
                }
            }

            if (!code[to].OpCode.Name.StartsWith("ldflda", StringComparison.Ordinal)) return null;
            if (!(code[to].Operand is FieldReference fr)) return null;
            if (!ctx.Twin.ContainsKey(fr.DeclaringType.FullName + "::" + fr.Name)) return null;

            var outp = new List<Instruction>();

            for (int k = from; k <= to; k++)
            {
                if (!IsStructural(code[k], false)) return null;

                outp.Add(CloneSwap(ctx, code[k]));
            }

            return outp;
        }

        /// <summary>
        /// 这条指令能不能原样重放？<paramref name="last"/> 为真时额外放行那条收尾的写指令
        /// （<c>stfld</c> / <c>stelem</c> / <c>Array.Resize</c>）——它写的是孪生字段，
        /// 由调用方保证。
        ///
        /// <b>放行清单是白名单而不是黑名单。</b> 认不出来的指令一律拒绝：重放一条带副作用的
        /// 指令等于把那个副作用做两遍，而那是静默的——数量对不上要很久以后才看得出来。
        /// </summary>
        private static bool IsStructural(Instruction i, bool last)
        {
            if (IsPureLoad(i)) return true;

            string n = i.OpCode.Name;

            if (n == "ldnull" || n == "newarr" || n == "ldlen"
                || n.StartsWith("conv.", StringComparison.Ordinal)
                || n.StartsWith("ldflda", StringComparison.Ordinal)) return true;

            if (!last) return false;

            if (n.StartsWith("stfld", StringComparison.Ordinal)
                || n.StartsWith("stelem", StringComparison.Ordinal)) return true;

            return (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                   && i.Operand is MethodReference mr
                   && mr.Name == "Resize"
                   && mr.DeclaringType.FullName == "System.Array";
        }

        /// <summary>原样克隆一条指令，但把主干道载荷字段换成它的孪生。</summary>
        private static Instruction CloneSwap(Ctx ctx, Instruction i)
        {
            if (i.Operand is FieldReference fr
                && ctx.Twin.TryGetValue(fr.DeclaringType.FullName + "::" + fr.Name,
                    out FieldDefinition tf))
                return Instruction.Create(i.OpCode, (FieldReference)tf);

            return Clone(i);
        }

        /// <summary>
        /// 「把 0 写进孪生槽位」：目的地表达式原样重放（载荷字段换孪生），值一律 0。
        /// 用在从存档读进来的那一族上——原版读到什么不重要，品质那边就是没有。
        /// </summary>
        private static List<Instruction> BuildZeroWrite(Ctx ctx, IList<Instruction> code, Job job)
        {
            Instruction last = code[job.To];
            bool toField = last.OpCode.Name.StartsWith("stfld", StringComparison.Ordinal);

            if (!toField && !last.OpCode.Name.StartsWith("stelem", StringComparison.Ordinal)) return null;

            int argc = toField ? 2 : 3;
            int[] a = ArgStarts(code, job.To, argc);

            if (a == null || a[0] < job.From) return null;

            var outp = new List<Instruction>();

            for (int k = a[0]; k < a[argc - 1]; k++)
            {
                if (!IsStructural(code[k], false)) return null;

                outp.Add(CloneSwap(ctx, code[k]));
            }

            outp.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            outp.Add(CloneSwap(ctx, last));

            return outp;
        }

        /// <summary>这条指令引用了一个<b>已经作废</b>的载荷局部吗？</summary>
        private static bool Orphan(MethodDefinition m, IList<Instruction> code, int at,
            ICollection<VariableDefinition> carriers)
        {
            if (!IsStloc(code[at]) && !IsLdloc(code[at])) return false;

            VariableDefinition v = VarOf(m, code[at]);

            return v != null && !carriers.Contains(v);
        }

        private static bool IsLdloc(Instruction i) =>
            i.OpCode == OpCodes.Ldloc || i.OpCode == OpCodes.Ldloc_S ||
            i.OpCode == OpCodes.Ldloc_0 || i.OpCode == OpCodes.Ldloc_1 ||
            i.OpCode == OpCodes.Ldloc_2 || i.OpCode == OpCodes.Ldloc_3;

        /// <summary>
        /// 可用的载荷局部：<b>它的每一处赋值都必须来自会被发射的形状</b>。
        ///
        /// <b>这条约束不是保守，是正确性。</b> 只要有一处赋值不会发射孪生，
        /// 那个孪生局部在那条路径上就是 0，而读它的地方会把 0 当成真值 ——
        /// 不报错，只是品质凭空归零。所以只要有一处不合格，整个局部作废。
        ///
        /// 这也解释了依赖链：<c>X.inc -= 等级</c> 那 11 处一直拼不出来，
        /// 是因为「等级」那个局部由 <c>ldfld:PAY div stloc</c> 赋值，
        /// 而那个形状在有发射器之前不算数。**先解上游，下游自己就通了。**
        /// </summary>
        private static IEnumerable<VariableDefinition> SafeCarriers(MethodDefinition m,
            IList<Instruction> code, List<Stmt> stmts)
        {
            var good = new HashSet<VariableDefinition>();
            var bad = new HashSet<VariableDefinition>();

            // 先把「由合格形状赋值」的局部收进来
            foreach (Stmt st in stmts)
            {
                if (!DefinesCarrier.Contains(st.Core) || !Emitted.Contains(st.Core)) continue;

                VariableDefinition v = VarOf(m, code[st.To]);

                if (v != null) good.Add(v);
            }

            // 再把「还有别的赋值来源」的局部整个剔掉
            var byStmt = new HashSet<int>(stmts.Where(s => DefinesCarrier.Contains(s.Core)).Select(s => s.To));

            for (var i = 0; i < code.Count; i++)
            {
                if (!IsStloc(code[i]) || byStmt.Contains(i)) continue;

                VariableDefinition v = VarOf(m, code[i]);

                if (v != null) bad.Add(v);
            }

            good.ExceptWith(bad);

            return good;
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

            // **目标字段只对 stfld 那两种形状有意义。** 第一版把它当成所有形状的前置，
            // 于是 `ldfld:PAY stloc`（末指令是 stloc）和读-改-写（末指令是 stind.i4）
            // 一律在这里就返回 null —— 表现是新写的五个发射器全部落成 [opaque]，
            // 看起来像「这些形状拼不出来」，其实是取字段那一步就错了。
            FieldDefinition tf = null;

            if (store.Operand is FieldReference sf &&
                !ctx.Twin.TryGetValue(sf.DeclaringType.FullName + "::" + sf.Name, out tf))
                return null;

            switch (job.Core)
            {
                // X.inc = 常数  →  X.qua = 0
                case "ldc stfld:PAY":
                {
                    if (tf == null) return null;

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
                case "call:split_inc stfld:PAY":
                {
                    if (tf == null) return null;

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

                // 从存档读进来的那一族：X.inc = reader.ReadXxx()  →  X.qua = 0
            //
            // **必须真的写个 0，不能什么都不做。** Import 可能读进一个复用的结构体，
            // 那时字段里留着的是上一个实体的品质——不报错，只是品质串了。
            // 这是「品质还没进存档」这个缺口的读侧，写侧见 SaveWriteShapes。
            case "call:ReadInt32 stfld:PAY":
            case "call:ReadByte stfld:PAY":
            case "ldfld:PAY call:ReadInt32 stelem":
                return BuildZeroWrite(ctx, code, job);

            // *outInc += X.inc  →  Q<槽位> += X.qua
            //
            // 这是**把值带出方法**的那一族：被加的地址是一个 `ref int` 出参。
            // 出参那条路正是被推翻的「给方法加参数」方案要走的，所以品质这边改走侧信道：
            // 写进静态寄存器 Q，调用方读回来。**只认地址就是那个出参本身**——
            // 换成别的地址表达式就没有对应的寄存器可写，宁可认不出来。
            case "ldind.i4 ldfld:PAY add stind.i4":
            case "ldind.i4 ldflda:PAY call:split_inc add stind.i4":
            case "ldind.i4 ldflda:PAY ldind.i4 call:split_inc add stind.i4":
            {
                if (job.To - job.From < 4) return null;
                if (code[job.To].OpCode != OpCodes.Stind_I4) return null;
                if (code[job.To - 1].OpCode != OpCodes.Add) return null;
                if (code[job.From + 2].OpCode != OpCodes.Ldind_I4) return null;

                ParameterDefinition op = ParamOf(ctx.Method, code[job.From]);

                if (op == null || op != ParamOf(ctx.Method, code[job.From + 1])) return null;
                if (!ctx.ParamSlot.TryGetValue(op, out int oslot) || oslot >= ctx.Regs.Count) return null;

                List<Instruction> add = TwinValue(ctx, code, job.From + 3, job.To - 2);

                if (add == null) return null;

                var oout = new List<Instruction> { Instruction.Create(OpCodes.Ldsfld, ctx.Regs[oslot]) };

                oout.AddRange(add);
                oout.Add(Instruction.Create(OpCodes.Add));
                oout.Add(Instruction.Create(OpCodes.Stsfld, ctx.Regs[oslot]));

                return oout;
            }

            // ── 数组本身的那一族：分配、清零、改长度 ──
            //
            // 这几种语句碰的不是点数值，而是**装点数的那个数组**。孪生数组和原数组
            // 永远等长、同生共死，所以孪生语句就是同一条语句把字段换成孪生字段。
            // 值那一侧一律照抄（<c>new int[n]</c>、<c>null</c>、<c>= 0</c>），
            // 因为新数组的品质本来就该是零。
            //
            // **前提是整条语句除了那个载荷字段之外没有别的副作用**——否则照抄会把副作用
            // 做第二遍。所以每条指令都要过一遍纯度检查，不纯就拒绝。
            case "ldnull stfld:PAY":
            case "newarr stfld:PAY":
            case "ldlen newarr stfld:PAY":
            case "ldflda:PAY call:Resize":
            case "ldfld:PAY ldc stelem":
            {
                var aout = new List<Instruction>();

                for (int k = job.From; k <= job.To; k++)
                {
                    if (!IsStructural(code[k], k == job.To)) return null;

                    aout.Add(CloneSwap(ctx, code[k]));
                }

                return aout;
            }

            // served[i] = (incServed[i] = 0)
            //
            // 一条语句里有**两个** stelem，其中只有一个是载荷。整条照抄会连
            // `served[i] = 0` 一起做第二遍——那是往真实计数里写东西，不是多写一条孪生。
            // 所以这里只取载荷那一半：`incServedQua[i] = 0`。
            case "ldfld:PAY ldc dup stloc stelem stelem":
            {
                int inner = job.To - 1;

                if (code[inner].OpCode != OpCodes.Stelem_I4 && code[inner].OpCode != OpCodes.Stelem_Any)
                    return null;

                int[] ea = ArgStarts(code, inner, 3);

                if (ea == null) return null;

                // 值那一侧必须是常量——它后面挂着 `dup ; stloc`，照抄会重复写那个局部
                if (!code[ea[2]].OpCode.Name.StartsWith("ldc", StringComparison.Ordinal)) return null;

                var eout = new List<Instruction>();

                for (int k = ea[0]; k < ea[2]; k++)
                {
                    if (!IsStructural(code[k], false)) return null;

                    eout.Add(CloneSwap(ctx, code[k]));
                }

                eout.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                eout.Add(Clone(code[inner]));

                return eout;
            }

            // local = split_inc(ref n, ref m, p)  →  twin = Split(n, ref mTwin, p)
            //
            // 原版这一行是「从 m 点数里按比例分走 p 件的份额」，同时把 n 减掉 p、把 m 减掉份额。
            // 品质那边一模一样：侧信道的 Split 镜像了同一段算式，**但第一个参数取值不取址**
            // ——因为原版调用返回时 n 已经减过了，这时读那个局部正是 Split 要的 countAfter。
            //
            // 不能改成再调一次原版 split_inc：它会把 n **再**减一次 p，那是凭空吃掉物品。
            case "call:split_inc stloc":
            {
                VariableDefinition sdst = VarOf(ctx.Method, store);

                if (sdst == null || !ctx.Locals.TryGetValue(sdst, out VariableDefinition sdt)) return null;

                List<Instruction> sval = SplitTwin(ctx, code, job.To - 1, job.From);

                if (sval == null) return null;

                // 分类遍里孪生局部还没建，能走到这一步就说明拼得出来
                if (sdt == null || sval.Count == 0) return new List<Instruction>();

                sval.Add(Instruction.Create(OpCodes.Stloc, sdt));

                return sval;
            }

            // localB = localA  →  twinB = twinA
            case "ldloc stloc":
            {
                VariableDefinition psrc = VarOf(ctx.Method, code[job.From]);
                VariableDefinition pdst = VarOf(ctx.Method, store);

                if (psrc == null || pdst == null) return null;
                if (!ctx.Locals.TryGetValue(psrc, out VariableDefinition pst)) return null;
                if (!ctx.Locals.TryGetValue(pdst, out VariableDefinition pdt)) return null;

                // 分类遍里两个孪生局部都还没建，能走到这一步就说明拼得出来
                if (pst == null || pdt == null) return new List<Instruction>();

                return new List<Instruction>
                {
                    Instruction.Create(OpCodes.Ldloc, pst),
                    Instruction.Create(OpCodes.Stloc, pdt),
                };
            }

            // local = <常量>  →  twinLocal = 0
                //
                // C# 编译器给每个声明的局部都会在方法开头放一条 `ldc.i4.0 ; stloc`，
                // 而这条语句里没有任何载荷字段，所以它**不在按载荷切出来的语句表里**。
                // 它不发射的后果不是少发一条，而是整个局部作废：
                // 「每一处赋值都要能发射」这条规则会因为这一处而把 V_3 判死，
                // 连带它后面三处真正的 `V_3 = storage[i].inc` 全部拼不出来。
                // 实测 StationComponent::InternalTickLocal 就是这样卡住的。
                case "ldc stloc":
                {
                    VariableDefinition cdst = VarOf(ctx.Method, store);

                    if (cdst == null || !ctx.Locals.TryGetValue(cdst, out VariableDefinition ctv)) return null;

                    var cout = new List<Instruction> { Instruction.Create(OpCodes.Ldc_I4_0) };

                    if (ctv == null) return cout;

                    cout.Add(Instruction.Create(OpCodes.Stloc, ctv));

                    return cout;
                }

                // local = X.inc  →  twinLocal = X.qua
                case "ldfld:PAY stloc":
                {
                    VariableDefinition dst = VarOf(ctx.Method, store);

                    if (dst == null || !ctx.Locals.TryGetValue(dst, out VariableDefinition tv)) return null;

                    List<Instruction> val = TwinValue(ctx, code, job.From, job.To - 1);

                    if (val == null) return null;

                    // 分类遍里孪生局部还没建，能走到这一步就说明拼得出来
                    if (tv == null) return val;

                    val.Add(Instruction.Create(OpCodes.Stloc, tv));

                    return val;
                }

                // local = X.inc / count  →  twinLocal = X.qua / count
                //
                // 原版是拿它求「单件增产等级」；品质那边同样的算式给出「单件品质分」。
                // **这一条是依赖链的上游**：`X.inc -= 等级` 那一族要用到它定义的局部，
                // 在它有发射器之前，那个局部不算可用载荷，于是下游整族都拼不出来。
                case "ldfld:PAY div stloc":
                {
                    VariableDefinition dst = VarOf(ctx.Method, store);

                    if (dst == null || !ctx.Locals.TryGetValue(dst, out VariableDefinition dv)) return null;

                    // div 在 to-1；被除数表达式到 divIdx 之前，除数在中间
                    if (job.To - 1 < job.From || code[job.To - 1].OpCode != OpCodes.Div) return null;

                    int[] a = ArgStarts(code, job.To - 1, 2);

                    if (a == null) return null;

                    List<Instruction> num = TwinValue(ctx, code, a[0], a[1] - 1);

                    if (num == null) return null;

                    var outp = new List<Instruction>(num);

                    // 除数（件数）原样重放
                    for (int k = a[1]; k < job.To - 1; k++)
                    {
                        if (!IsPureLoad(code[k])) return null;

                        outp.Add(Clone(code[k]));
                    }

                    outp.Add(Instruction.Create(OpCodes.Div));

                    if (dv == null) return outp;

                    outp.Add(Instruction.Create(OpCodes.Stloc, dv));

                    return outp;
                }

                // 读-改-写：X.inc += v / -= v
                //
                // `ldflda PAY; dup; ldind.i4; <v>; add; stind.i4` 和
                // `ldfld PAY(数组); <下标>; ldelema; dup; ldind.i4; <v>; add; stind.i4`
                // **结构上是同一个形状**——都是「取地址 → dup ldind → 值 → 运算 → stind」，
                // 差别只在地址表达式怎么写，而地址表达式是原样重放的。所以一个发射器全包。
                case "ldflda:PAY dup ldind.i4 add stind.i4":
                case "ldflda:PAY dup ldind.i4 sub stind.i4":
                case "ldfld:PAY ldc dup ldind.i4 add stind.i4":
                case "ldc ldflda:PAY dup ldind.i4 add stind.i4":

                // 值那一侧换成 split_inc(...) 或另一个载荷字段，目的地一侧完全没变——
                // **值和目的地是正交的**，所以这里只是把形状名加进来，代码一行都不用改。
                case "ldflda:PAY dup ldind.i4 call:split_inc add stind.i4":
                case "ldflda:PAY dup ldind.i4 ldflda:PAY call:split_inc add stind.i4":
                case "ldflda:PAY dup ldind.i4 ldfld:PAY add stind.i4":
                    return BuildReadModifyWrite(ctx, code, job);

                default: return null;
            }
        }

        /// <summary>
        /// 「取地址 → <c>dup ldind.i4</c> → 值 → <c>add</c>/<c>sub</c> → <c>stind.i4</c>」这一族。
        ///
        /// 靠 <c>dup</c> 把语句切成两半：它前面是地址表达式（原样重放，其中载荷字段换成孪生），
        /// 它后面到运算符之间是值（翻成品质版）。这样就不必为每种地址写法各写一个发射器。
        /// </summary>
        private static List<Instruction> BuildReadModifyWrite(Ctx ctx, IList<Instruction> code, Job job)
        {
            // stind.i4 在 to，运算符在 to-1
            if (job.To - 1 < job.From) return null;

            Instruction op = code[job.To - 1];

            if (op.OpCode != OpCodes.Add && op.OpCode != OpCodes.Sub) return null;

            // 找 dup（它后面紧跟 ldind.i4）
            var dupAt = -1;

            for (int k = job.From; k < job.To - 1; k++)
                if (code[k].OpCode == OpCodes.Dup && code[k + 1].OpCode == OpCodes.Ldind_I4)
                {
                    dupAt = k;

                    break;
                }

            if (dupAt < 0) return null;

            var outp = new List<Instruction>();

            // 地址表达式：原样重放，载荷字段换成孪生
            for (int k = job.From; k < dupAt; k++)
            {
                if (!IsPureLoad(code[k])) return null;

                if (code[k].Operand is FieldReference fr2 &&
                    ctx.Twin.TryGetValue(fr2.DeclaringType.FullName + "::" + fr2.Name, out FieldDefinition tw))
                    outp.Add(Instruction.Create(code[k].OpCode, tw));
                else
                    outp.Add(Clone(code[k]));
            }

            outp.Add(Instruction.Create(OpCodes.Dup));
            outp.Add(Instruction.Create(OpCodes.Ldind_I4));

            List<Instruction> val = TwinValue(ctx, code, dupAt + 2, job.To - 2);

            if (val == null) return null;

            outp.AddRange(val);
            outp.Add(Instruction.Create(op.OpCode));
            outp.Add(Instruction.Create(OpCodes.Stind_I4));

            return outp;
        }

        /// <summary>
        /// 可用的载荷局部变量：<b>它的每一处赋值都必须是发射得出来的</b>。
        ///
        /// <b>这条约束不是保守，是正确性。</b> 第一版还沿 <c>ldloc A ; stloc B</c> 传播过
        /// （A 装过载荷，B 也就装过），看着很自然，其实是个静默错误：
        /// <c>B = A</c> 这条语句<b>根本不含载荷字段</b>，所以它永远不会被分类、
        /// 也就永远不会发射孪生赋值 —— 于是 <c>B</c> 的孪生局部恒为 0，
        /// 而读它的地方会拿到 0 并当成真值。不报错，只是品质凭空归零。
        ///
        /// 所以只认一种种子：<c>&lt;载荷载入&gt; stloc L</c>，也就是形状
        /// <c>ldfld:PAY stloc</c>——那一种有发射器，孪生赋值一定会被写出来。
        /// 等以后 <c>ldfld:PAY div stloc</c>（求等级）这类也有了发射器，
        /// 再把它们加进种子，那时才是安全的。
        ///
        /// <b>「一个局部只有在每一处赋值都能发射时才可用」</b>是这套变换的通用规则，
        /// 违反它的表现一律是静默的零。
        /// </summary>
        private static IEnumerable<VariableDefinition> PayloadCarriers(MethodDefinition m,
            IList<Instruction> code, IDictionary<string, FieldDefinition> twin)
        {
            var set = new HashSet<VariableDefinition>();
            var unsafeSet = new HashSet<VariableDefinition>();

            for (var i = 1; i < code.Count; i++)
            {
                if (!IsStloc(code[i])) continue;

                VariableDefinition dst = VarOf(m, code[i]);

                if (dst == null) continue;

                // 直接由载荷字段赋值 —— 这一种有发射器
                if (IsMainline(code[i - 1], twin)) { set.Add(dst); continue; }

                // 同一个局部还有别的赋值来源，而那些来源不保证能发射孪生。
                // **只要有一处不能，这个局部整个不可用**——否则它会在某条路径上读到 0。
                unsafeSet.Add(dst);
            }

            set.ExceptWith(unsafeSet);

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

            /// <summary>侧信道的 <c>Split(countAfter, ref qua, p)</c>——原版 split_inc 的品质版。</summary>
            internal MethodDefinition ChannelSplit;
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
            // (0) split_inc(ref n, ref m, p) 这个**表达式**的品质版。
            //
            // 放在 TwinValue 里而不是各写一个发射器，是因为它出现在五种不同的目的地上
            // （加进字段、加进出参、直接赋值、存进局部、写进另一个字段），
            // 而那五种目的地本来就已经各有发射器了——值和目的地是正交的。
            if (to >= from && IsSplitIncCall(code[to]))
            {
                List<Instruction> sp = SplitTwin(ctx, code, to, from);

                if (sp != null) return sp;
            }

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

            // local = X.inc  →  twinLocal = X.qua。局部变量孪生的第一个用户。
            "ldfld:PAY stloc",

            // local = <常量> → twinLocal = 0。**它不是按载荷切出来的**，
            // 是载荷局部认定之后反过来补进语句表的，理由见 Build 里那一段。
            "ldc stloc",

            // localB = localA → twinB = twinA。同样是补进去的。
            "ldloc stloc",

            // local = split_inc(ref n, ref m, p)。**split_inc 一族的入口**：
            // 站点内搬运那 9 处 `X.inc -= 份额` 的被减数就是它定义的局部。
            "call:split_inc stloc",

            // local = X.inc / count（求单件等级）。**依赖链的上游**：
            // `X.inc -= 等级` 那一族要用它定义的局部，它一通，下游整族跟着通。
            "ldfld:PAY div stloc",

            // 读-改-写一族。这四种在 IL 结构上是同一个形状——都是
            // 「取地址 → dup ldind.i4 → 值 → 运算 → stind.i4」，差别只在地址表达式
            // 怎么写，而地址表达式是原样重放的，所以一个发射器全包。
            "ldflda:PAY dup ldind.i4 add stind.i4",
            "ldflda:PAY dup ldind.i4 sub stind.i4",
            "ldfld:PAY ldc dup ldind.i4 add stind.i4",
            "ldc ldflda:PAY dup ldind.i4 add stind.i4",

            // *outInc += X.inc → Q<槽位> += X.qua。把值带出方法的那一族，走侧信道。
            "ldind.i4 ldfld:PAY add stind.i4",

            // split_inc 一族：值那一侧是「按比例分走点数」，目的地照旧。
            // 它是剩下缺口里最大的一块，而**值和目的地正交**，所以真正新写的只有
            // TwinValue 里那一个分支，五种目的地一起通。
            "ldflda:PAY dup ldind.i4 call:split_inc add stind.i4",
            "ldflda:PAY dup ldind.i4 ldflda:PAY call:split_inc add stind.i4",
            "ldflda:PAY dup ldind.i4 ldfld:PAY add stind.i4",
            "ldind.i4 ldflda:PAY call:split_inc add stind.i4",
            "ldind.i4 ldflda:PAY ldind.i4 call:split_inc add stind.i4",
            "call:split_inc stfld:PAY",

            // 存档读侧：读进来的品质一律 0（写侧见 SaveWriteShapes，还没进存档）
            "call:ReadInt32 stfld:PAY",
            "call:ReadByte stfld:PAY",
            "ldfld:PAY call:ReadInt32 stelem",

            // 数组本身的一族：分配、清零、改长度。孪生数组与原数组等长同生死，
            // 所以孪生语句就是同一条语句换个字段——**前提是整条语句没有别的副作用**。
            "ldnull stfld:PAY",
            "newarr stfld:PAY",
            "ldlen newarr stfld:PAY",
            "ldflda:PAY call:Resize",
            "ldfld:PAY ldc stelem",
            "ldfld:PAY ldc dup stloc stelem stelem",
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
            "ldc stloc",                                            // local = 常量（载荷局部的零初始化）
            "ldloc stloc",                                          // local = 另一个载荷局部
            "call:split_inc stloc",                                 // local = split_inc(...)
            "ldflda:PAY dup ldind.i4 add stind.i4",                 // X.inc += v
            "ldflda:PAY dup ldind.i4 sub stind.i4",                 // X.inc -= v
            "ldfld:PAY ldc dup ldind.i4 add stind.i4",              // 数组元素 += v
            "ldc ldflda:PAY dup ldind.i4 add stind.i4",             // 同上，常数下标
            "ldind.i4 ldfld:PAY add stind.i4",                      // *out += X.inc
            "ldflda:PAY dup ldind.i4 call:split_inc add stind.i4",  // X.inc += split_inc(...)
            "ldflda:PAY dup ldind.i4 ldflda:PAY call:split_inc add stind.i4",
            "ldflda:PAY dup ldind.i4 ldfld:PAY add stind.i4",       // X.inc += Y.inc
            "ldind.i4 ldflda:PAY call:split_inc add stind.i4",      // *out += split_inc(..., ref X.inc, ...)
            "ldind.i4 ldflda:PAY ldind.i4 call:split_inc add stind.i4",
            "call:split_inc stfld:PAY",                             // X.inc = split_inc(...)
            "call:ReadInt32 stfld:PAY",                             // 从存档读：品质置零
            "call:ReadByte stfld:PAY",
            "ldfld:PAY call:ReadInt32 stelem",
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

        /// <summary>
        /// <b>品质还没进存档</b>：写的时候不写，读的时候置零。
        ///
        /// 四个主干道载荷各有自己的 Export/Import，要加字段就要各自加版本分支——
        /// 那是一次独立的、会改变存档格式的改动（本仓库的既定政策是「存档跟着 mod 走」，
        /// 所以做得了，只是不该顺手做）。在那之前品质**每次读档归零**，
        /// 这是说出来的代价，不是静默的：报告里单列一档，日志里看得见。
        ///
        /// 读那一侧必须发射「置零」而不是什么都不做：Import 有可能读进一个复用的结构体，
        /// 那时字段里留着的是上一个实体的品质——不报错，只是品质串了。
        /// </summary>
        private static readonly HashSet<string> SaveWriteShapes = new HashSet<string>(new[]
        {
            "ldfld:PAY call:Write",                                 // writer.Write(X.inc)
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
