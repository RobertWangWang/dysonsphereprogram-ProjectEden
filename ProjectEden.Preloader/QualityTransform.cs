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

            // **插一块只有「真的改完了」才存在的招牌。**
            //
            // 插件那一侧要能分清「品质恒为 0 是因为没矿」和「因为 1c 整体放弃了」,
            // 而这两种在字段和侧信道都还在的情况下长得一模一样。按字段存不存在去猜
            // 是不行的：1a 放了字段，1c 失败时它们照样在。
            // 所以让 1c 自己在成功那一刻留个标记，插件反射探它。
            if (mutate && r.Applied) MarkFlowing(module, channel);

            return r;
        }

        /// <summary>1c 真的应用之后，在侧信道类型上留一个静态字段当招牌。</summary>
        internal const string FlowFieldName = "Flowing";

        private static void MarkFlowing(ModuleDefinition module, TypeDefinition channel)
        {
            if (channel.Fields.Any(f => f.Name == FlowFieldName)) return;

            channel.Fields.Add(new FieldDefinition(FlowFieldName,
                FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Int32));
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
            var ctx = new Ctx { Method = m, Twin = twin, Regs = regs, ChannelSplit = chSplit, AllParamSlots = paramSlots };

            if (paramSlots.TryGetValue(m, out List<int> slots))
                for (var s = 0; s < slots.Count && s < regs.Count; s++)
                    ctx.ParamSlot[m.Parameters[slots[s]]] = s;

            // 先把语句切出来并算好形状，再据此判定载荷局部——**顺序不能反**。
            // 「一个局部只有在它的每一处赋值都能发射时才可用」这条规则要知道每条语句的形状，
            // 而形状的计算不依赖载荷局部，所以这一步拆得开。
            List<Stmt> stmts = Split(m, code, twin);

            // 载荷局部的零初始化不含载荷字段，切不出来，得反过来补——理由见 ConstInits。
            stmts.AddRange(ConstInits(ctx, m, code, stmts));
            stmts.Sort((x, y) => x.To.CompareTo(y.To));

            // **但「形状对」不等于「拼得出来」**，而拼不拼得出来又要先知道载荷局部——这是个环。
            // 形状是 ldfld:PAY stloc 却拼不出来的语句实测有 10 处；只按形状收，
            // 它们赋值的局部会被当成可用载荷，而它的孪生在那条路径上永远是 0,
            // 读的人把 0 当真值 —— 不报错，品质凭空归零。正是这一期反复在修的那种自欺。
            //
            // 解法是**从乐观集合出发跑不动点**：每轮拿当前集合分类，把定义语句拼不出来的
            // 局部剔掉再来一轮。集合只减不增，所以一定收敛，最多跑 |集合| 轮。
            var cand = new HashSet<VariableDefinition>(SafeCarriers(m, code, stmts));

            // 「每件品质分」只看定义式的形状，和哪些局部最终可用无关，所以在不动点之前就能定。
            // 放在之后定就太晚了：乘法的决胜正发生在不动点里面。
            ctx.PerItem.Clear();

            foreach (Stmt st in stmts)
            {
                if (st.Core.IndexOf("div", StringComparison.Ordinal) < 0) continue;

                VariableDefinition pv = VarOf(m, code[st.To]);

                if (pv != null) ctx.PerItem.Add(pv);
            }
            var carriers = new HashSet<VariableDefinition>(cand);
            var dead = new HashSet<VariableDefinition>();

            // **每一轮都从候选全集重算作废集合，不是往里累加。**
            //
            // 「哪个局部不能用」不是单调的：`V_4 = V_3 × 每件品质分` 在 V_3 还算载荷时
            // 拼不出来（两侧都带品质 = 不是缩放，含义不明），而 V_3 一被剔掉它就拼得出来了。
            // 只往里加的写法会把 V_4 永久判死，表现是下游那四处 `X.inc -= V_4` 一直是缺口,
            // 而原因在两条语句之前、上一轮就已经解决了。
            //
            // 收敛不保证，所以设了轮数上限；到顶还没稳就用最后一轮的结果，那只会更保守。
            for (var round = 0; round < 8; round++)
            {
                ctx.Locals.Clear();

                foreach (VariableDefinition v in carriers)
                    ctx.Locals[v] = null; // 占位：分类只关心「是不是载荷局部」

                var fresh = new HashSet<VariableDefinition>();

                foreach (Stmt st in stmts)
                {
                    if (!DefinesCarrier.Contains(st.Core)) continue;

                    VariableDefinition dv = DefOf(m, code, st);

                    if (dv == null || !cand.Contains(dv)) continue;

                    // **判定一条赋值能不能发射时，目的地必须在场。**
                    // 上一轮把它剔掉之后，这一轮它自己的赋值语句会因为「目的地不是载荷局部」
                    // 直接失败，于是永远回不来——而它上一轮失败的原因（某个来源还在）
                    // 早就消失了。这一条差点让 `X.inc -= 件数 × 每件品质分` 那一族永久缺口。
                    bool had = ctx.Locals.ContainsKey(dv);

                    if (!had) ctx.Locals[dv] = null;

                    bool fail = !Emitted.Contains(st.Core)
                                || Build(ctx, code, new Job
                                {
                                    Core = st.Core, From = st.From, To = st.To,
                                    TrueFrom = st.TrueFrom, Dst = st.Dst,
                                }) == null;

                    if (!had) ctx.Locals.Remove(dv);

                    if (fail)
                    {
                        fresh.Add(dv);

                        if (Environment.GetEnvironmentVariable("EDEN_QUALITY_DEBUG") == m.Name)
                            Console.Error.WriteLine($"[fail] {m.Name} 第{round}轮 V_{dv.Index} "
                                                    + $"IL_{code[st.From].Offset:X4}-{code[st.To].Offset:X4} {st.Core}");
                    }
                }

                if (fresh.SetEquals(dead)) break;

                dead = fresh;
                carriers = new HashSet<VariableDefinition>(cand);
                carriers.ExceptWith(dead);
            }

            ctx.Locals.Clear();

            foreach (VariableDefinition v in carriers)
                ctx.Locals[v] = null;

            // 合成语句随它服务的局部一起消失——留着只会以「拼不出来」的身份挡住变换,
            // 而它描述的那件事其实已经不需要做了。**只按目的地剪**：
            // `V_4 = V_3 × 每件品质分` 里 V_3 只是个件数，原样重放当系数就行。
            stmts.RemoveAll(st => st.Synth && Orphan(m, code, st.To, carriers));

            // 诊断：EDEN_QUALITY_DEBUG=<方法名> 时，把这个方法体的分类结果原样打出来。
            // 缺口报告只说「这个形状拼不出来」，而原因几乎总在别的语句上——
            // 没有这一段，每查一处都要重写一遍切分逻辑。
            string dbg = Environment.GetEnvironmentVariable("EDEN_QUALITY_DEBUG");

            if (!string.IsNullOrEmpty(dbg) && m.Name == dbg)
            {
                r.Notes.Add($"[dbg] {m.DeclaringType.Name}::{m.Name} 候选 "
                            + string.Join(",", cand.Select(v => "V_" + v.Index).ToArray())
                            + " / 可用 " + string.Join(",", carriers.Select(v => "V_" + v.Index).ToArray())
                            + " / 作废 " + string.Join(",", dead.Select(v => "V_" + v.Index).ToArray()));

                foreach (Stmt st in stmts)
                    r.Notes.Add($"[dbg]   IL_{code[st.TrueFrom].Offset:X4}/{code[st.From].Offset:X4}"
                                + $"-{code[st.To].Offset:X4} {(st.Synth ? "合成 " : "")}{st.Core}");
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
                        var job = new Job
                        {
                            Core = core, From = from, To = to, TrueFrom = st.TrueFrom, Dst = st.Dst,
                        };

                        if (Build(ctx, code, job) == null)
                        {
                            // 拼不出来的最常见原因是值不由载荷推导（比如机甲自己变出来的弹药）。
                            // 那是个**语义问题**而不是形状问题，所以单独标出来等人决定，
                            // 绝不悄悄按 0 处理——那等于静默丢品质。
                            r.Recognized++;

                            string why = job.TypeError
                                         ?? (ctx.TypeErrors.TryGetValue(to, out string te) ? te : null);

                            string tag = core + (why != null ? " [类型不合法：" + why + "]" : " [opaque]");

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

            /// <summary>见 <see cref="Stmt.TrueFrom"/>。</summary>
            internal int TrueFrom;

            /// <summary>见 <see cref="Stmt.Dst"/>。</summary>
            internal VariableDefinition Dst;

            /// <summary>栈类型检查的说法，拼出来但类型不合法时填。报缺口时要把它带上。</summary>
            internal string TypeError;

            /// <summary>
            /// 插入点的<b>指令对象</b>，由发射器在拼的时候记下来。
            ///
            /// 下标会过期：同一条语句上可能挂着两个 Job（往被调方传进去、从出参接回来）,
            /// 先处理的那个一插入，后处理的那个手里的下标就错位了。指令对象不会。
            /// </summary>
            internal Instruction Anchor;

            /// <summary>
            /// 孪生语句插在哪条指令<b>之后</b>。<c>-1</c> 表示默认的「整条语句之后」。
            ///
            /// 绝大多数形状都插在语句末尾，那样原语句上挂的跳转标签一个都不用动。
            /// 唯一的例外是<b>把品质传进被调方</b>：寄存器必须在 <c>call</c> 执行之前写好，
            /// 所以那一族插在 call 的前一条之后。实参已经压完栈，这时插入是栈中性的。
            /// </summary>
            internal int Insert = -1;
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

            foreach (Job job in work.OrderByDescending(j => j.Insert < 0 ? j.To : j.Insert))
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

                // Build 可能把插入点挪到 call 前后（见 Job.Insert / Job.Anchor）
                Instruction at = job.Anchor ?? code[job.Insert < 0 ? job.To : job.Insert];

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

            /// <summary>
            /// 控制流汇合之前、栈上一次真正归零的位置。
            ///
            /// <c>[merge]</c> 语句的 <c>From</c> 是汇合点，而**目的地对象是在汇合之前压栈的**
            /// （`storage[k].inc = 条件 ? a : b` 里那个 `storage[k]`）——从 From 往回走会
            /// 撞上分支目标而认不出来。要重放那个对象表达式，就得从这里开始往前找。
            /// </summary>
            internal int TrueFrom;

            /// <summary>
            /// 这条语句定义的局部，当它<b>不是</b>由末尾的 stloc 定义时。
            /// 目前只有一种：被调方通过 <c>out</c> 形参写回来的那个局部——
            /// 它的名字在 <c>ldloca</c> 上，语句末尾是 call，不是 stloc。
            /// </summary>
            internal VariableDefinition Dst;
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
            var hard = 0;   // 汇合点不重置它——这才是栈意义上的语句开头
            var merge = false;

            for (var i = 0; i < code.Count; i++)
            {
                if (targets.Contains(code[i].Offset) && depth != 0) { depth = 0; start = i; merge = true; }

                depth += Push(code[i]) - Pop(code[i]);

                if (depth < 0) depth = 0;

                if (depth != 0) continue;

                int from = start;
                int to = i;
                int trueFrom = hard;
                bool isMerge = merge;

                start = i + 1;
                merge = false;

                // **汇合点切出来的那一截不算「栈意义上的语句」**，所以它不能推进 hard。
                // 推进了的话，`storage[k].inc = 条件 ? a : b` 里那条只有一个 nop 的空语句
                // 会把 hard 顶到 stfld 自己身上，于是目的地对象再也找不回来——
                // 表现是整族「拼不出来」，而对象就在四条指令之前。
                if (!isMerge) hard = i + 1;

                if (!HasMainline(code, from, to, twin)) continue;

                string core = CoreShape(code, from, to, twin);

                if (isMerge || Merges(code, targets, from, to)) core += " [merge]";

                outp.Add(new Stmt { From = from, To = to, TrueFrom = trueFrom, Core = core });
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
            "ldc stloc",            // local = 0（零初始化，孪生值也是 0）
            "recalc stloc",         // local = 与品质无关的重算（夹取／下标／封顶，品质不动）
            "calc stloc",           // local = 纯算术（件数 × 每件品质分）
            "ldloc stloc",          // local = 另一个载荷局部（复制传播）
            "ldarg:PAY stloc",      // local = 载荷参数（品质在侧信道寄存器里）
            "call:out",             // local 由被调方通过 out 形参写回
            "call:get_Value ldfld:PAY stloc",
            "ldfld:PAY mul sub stloc",
            "ldfld:PAY div mul ldc add stloc",
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
        private static List<Stmt> ConstInits(Ctx ctx, MethodDefinition m, IList<Instruction> code,
            List<Stmt> stmts)
        {
            var extra = new List<Stmt>();
            var cand = new HashSet<VariableDefinition>();

            foreach (Stmt st in stmts)
            {
                if (!DefinesCarrier.Contains(st.Core)) continue;

                VariableDefinition v = DefOf(m, code, st);

                if (v != null) cand.Add(v);
            }

            // **不能在这里因为「没有种子」提前返回。** 载荷参数复制进局部（`V_1 = inc`）
            // 自己就是种子，不需要先有一条含载荷字段的语句——而
            // StationComponent::DispatchSupplyShip 恰好是这样：它的局部全部来自参数和
            // split_inc，一条载荷字段语句都没有，于是提前返回把整个方法的品质流断在了开头。
            // **先把真语句占掉的位置记下来。** 不记的话，同一条 `V_2 = X.inc - 等级 × 件数`
            // 会既作为真语句（含载荷字段，切分切得出来）又作为合成的 `calc stloc` 各来一遍,
            // 于是孪生语句发射两次——品质凭空翻倍，而两遍各自都是对的，没有任何一处会报错。
            var taken = new HashSet<int>();

            foreach (Stmt st in stmts) taken.Add(st.To);

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

                // 载荷**参数**复制进局部：`V_1 = inc`。和局部之间的复制是两回事——
                // 参数那一侧的品质在侧信道寄存器里，不在某个孪生局部里。
                // StationComponent::DispatchSupplyShip 开头就是这个形状，它不通，
                // 后面 split_inc 和五处 `X.inc -= 份额` 全都拼不出来。
                for (var i = 1; i < code.Count; i++)
                {
                    if (taken.Contains(i) || !IsStloc(code[i])) continue;

                    VariableDefinition adst = VarOf(m, code[i]);

                    // 载荷参数是 `ref int` 时中间多一条解引用：`ldarg ; ldind ; stloc`
                    int af = i - 1;

                    if (af - 1 >= 0 && IsLdind(code[af])) af--;

                    ParameterDefinition ap = IsLdarg(code[af]) ? ParamOf(m, code[af]) : null;

                    if (adst == null || ap == null || !ctx.ParamSlot.ContainsKey(ap)) continue;

                    taken.Add(i);

                    extra.Add(new Stmt { From = af, To = i, Core = "ldarg:PAY stloc", Synth = true });

                    if (cand.Add(adst)) grew = true;
                }

                // 纯算术得出的载荷局部：`V_4 = 件数 × 每件品质分`。
                //
                // 这类语句里**一个载荷字段都没有**（两个操作数都是局部），所以按载荷切分
                // 看不见它——而它正是下游 `X.inc -= V_4` 那一族的被减数。
                //
                // 能不能带品质由 TwinValue 自己判（量纲规则在 TwinArith 里），
                // 这里只负责把语句补进来；判不出来就不是载荷局部，下游自然也拼不出来。
                for (var i = 1; i < code.Count; i++)
                {
                    if (taken.Contains(i) || !IsStloc(code[i])) continue;

                    VariableDefinition cdst = VarOf(m, code[i]);

                    if (cdst == null || !IsArith(code[i - 1])) continue;

                    int[] va = ArgStarts(code, i, 1);

                    if (va == null || !PureRange(code, va[0], i - 1)) continue;

                    // 品质来源不止「另一个载荷局部」——载荷字段和载荷参数同样算。
                    // 只认局部的话，`V_1 = *inc / *count` 这种（两个操作数都是参数解引用）
                    // 就被漏掉，而它是整条链的第一环。
                    if (!CarriesQuality(m, code, va[0], i - 1, cand, ctx)) continue;

                    taken.Add(i);

                    extra.Add(new Stmt { From = va[0], To = i, Core = "calc stloc", Synth = true });

                    if (cand.Add(cdst)) grew = true;
                }

                // 被调方通过 `out` 形参写回来的品质：`TakeItem(id, n, out inc)`。
                //
                // **这是侧信道的第三条路**，前两条是「调用方写进去」和「载荷参数读出来」。
                // 缺了它，被调方明明把品质写进了寄存器，调用方却没人去接——
                // 那个局部于是不算载荷，下游 `X.inc = 它` 一族全断。
                // 孪生语句插在 call **之后**：那时寄存器已经被被调方写好了。
                for (var i = 0; i < code.Count; i++)
                {
                    if (code[i].OpCode != OpCodes.Call && code[i].OpCode != OpCodes.Callvirt) continue;
                    if (!(code[i].Operand is MethodReference cmr)) continue;

                    MethodDefinition cd;

                    try { cd = cmr.Resolve(); }
                    catch { continue; }

                    if (cd == null || ctx.AllParamSlots == null
                        || !ctx.AllParamSlots.TryGetValue(cd, out List<int> cs)) continue;

                    int cargc = cd.Parameters.Count + (cd.HasThis ? 1 : 0);
                    int[] ca = ArgStarts(code, i, cargc);

                    if (ca == null) continue;

                    foreach (int pi in cs)
                    {
                        if (!cd.Parameters[pi].ParameterType.IsByReference) continue;

                        int ai = pi + (cd.HasThis ? 1 : 0);
                        int aEnd = ai + 1 < cargc ? ca[ai + 1] - 1 : i - 1;

                        if (ca[ai] != aEnd) continue;
                        if (code[aEnd].OpCode != OpCodes.Ldloca && code[aEnd].OpCode != OpCodes.Ldloca_S)
                            continue;
                        if (!(code[aEnd].Operand is VariableDefinition ov)) continue;

                        extra.Add(new Stmt
                        {
                            From = ca[0], To = i, TrueFrom = ca[0],
                            Core = "call:out", Dst = ov, Synth = true,
                        });

                        if (cand.Add(ov)) grew = true;
                    }
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

            // (2) 剩下的每一处赋值——**这一趟要等上面收敛之后再跑**。
            //
            // 判据是「右边带不带品质」：右边有载荷字段、载荷参数或另一个载荷局部，
            // 才是真的在改这个局部所代表的品质；否则它只是在**重算一个和品质无关的量**。
            //
            //   `V = 0`            声明时的零初始化 → 孪生也是 0。
            //   `if (lv > 10) lv = 10`   夹取，给增产表当下标 → 品质那边没有这张表。
            //   `lv = lv + 1`            走表的下标自增。
            //   `V_11 = served * 10`     把点数夹到「每件最多 10 点」。
            //
            // 后三种的孪生都是**什么都不做**：物品没变，它们所代表的品质就没变。
            // 清成 0 会在只是夹一下的路径上把品质抹掉，而那正是「品质分／件」最该保住的量。
            //
            // 要等收敛是因为「右边带不带品质」依赖完整的载荷局部集合：先跑会把
            // `V_a = V_b`（V_b 稍后才成为载荷）误判成与品质无关。
            for (var i = 1; i < code.Count; i++)
            {
                if (taken.Contains(i) || !IsStloc(code[i])) continue;

                VariableDefinition v = VarOf(m, code[i]);

                if (v == null || !cand.Contains(v)) continue;

                if (code[i - 1].OpCode == OpCodes.Ldc_I4_0)
                {
                    extra.Add(new Stmt { From = i - 1, To = i, Core = "ldc stloc", Synth = true });

                    continue;
                }

                int[] va = ArgStarts(code, i, 1);

                if (va == null || !PureRange(code, va[0], i - 1)) continue;

                // **自增一步**：`lv = lv + 1`。它唯一的「品质来源」是自己，而它在做的事是
                // 沿增产表往上走一格下标——StorageComponent::TakeTailItemsByIncTable 里
                // 同一个局部既当下标又当「每件点数」，是局部变量层面的「同一个值两种含义」。
                // 品质那边只认后一种：每件品质分不会因为下标走了一格就变。
                //
                // 认得很窄：右边必须正好是「自己 + 常量」（后面可以跟一个 dup），
                // 换成别的表达式就不是走下标，而可能是真的在累加点数。
                if (!IsSelfStep(m, code, va[0], i - 1, VarOf(m, code[i]))
                    && CarriesQuality(m, code, va[0], i - 1, cand, ctx)) continue;

                extra.Add(new Stmt { From = va[0], To = i, Core = "recalc stloc", Synth = true });
            }

            if (Environment.GetEnvironmentVariable("EDEN_QUALITY_DEBUG") == m.Name)
                foreach (Stmt x in extra)
                    Console.Error.WriteLine($"[sweep] {m.Name} IL_{code[x.From].Offset:X4}-"
                                            + $"{code[x.To].Offset:X4} {x.Core}");

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

                // 托管指针存在局部里（`ref` 局部）：取它的值要解引用，宽度按指向的类型。
                if (IsLdloc(code[to]) && VarOf(ctx.Method, code[to]) is VariableDefinition rv
                    && rv.VariableType is ByReferenceType bt)
                    return new List<Instruction>
                    {
                        Instruction.Create(OpCodes.Ldloc, rv),
                        Instruction.Create(DerefFor(bt.ElementType)),
                    };
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

        /// <summary>
        /// 汇合语句的<b>目的地对象表达式</b>：从栈意义上的语句开头往后走，
        /// 走到栈深第一次到 1 为止。中间必须全是纯取值，不然重放会把副作用做第二遍。
        /// </summary>
        private static List<Instruction> MergeObject(Ctx ctx, IList<Instruction> code, int from, int to)
        {
            var outp = new List<Instruction>();
            var depth = 0;

            for (int k = from; k < to; k++)
            {
                if (!IsStructural(code[k], false)) return null;

                depth += Push(code[k]) - Pop(code[k]);

                outp.Add(CloneSwap(ctx, code[k]));

                if (depth == 1) return outp;
            }

            return null;
        }

        /// <summary>按指向的类型挑解引用指令——宽度错了读出来的是别的字节。</summary>
        private static OpCode DerefFor(TypeReference t)
        {
            switch (t.MetadataType)
            {
                case MetadataType.SByte: return OpCodes.Ldind_I1;
                case MetadataType.Byte: return OpCodes.Ldind_U1;
                case MetadataType.Int16: return OpCodes.Ldind_I2;
                case MetadataType.UInt16: return OpCodes.Ldind_U2;
                default: return OpCodes.Ldind_I4;
            }
        }

        private static bool IsLdind(Instruction i) =>
            i.OpCode.Name.StartsWith("ldind.", StringComparison.Ordinal);

        private static bool IsStind(Instruction i) =>
            i.OpCode.Name.StartsWith("stind.", StringComparison.Ordinal);

        /// <summary>
        /// 把这条语句里那个 <c>call</c> 的载荷实参翻译成品质，写进<b>被调方槽位</b>的寄存器。
        ///
        /// 槽位按**被调方**的形参编号算，不是调用方的——所以要的是全模块那份表。
        /// 认不出来就整条放弃：被调方解析不出来、它没有载荷形参、实参边界数不出来、
        /// 或者 call 本身是跳转目标（那样插在它前面会被跳过去，等于没写）。
        /// </summary>
        private static List<Instruction> BuildForwardToCallee(Ctx ctx, IList<Instruction> code, Job job)
        {
            // **往前扫，不靠 job.To。** 同一条语句上可能还挂着一个「从出参接回来」的 Job,
            // 它先插入的话这里的 To 就过期了——而 call 本身还在，往前扫找得到。
            // **要找的是「有载荷形参的那个 call」，不是第一个 call。**
            // 一条语句里常常先有个取值器（`player.package.AddItemStacked(...)` 里的 get_package）,
            // 拿第一个 call 会解析到它，然后因为「它没有载荷形参」整条放弃——
            // 表现是这一族全部拼不出来，而真正的调用就在后面两条。
            var callAt = -1;
            MethodDefinition callee = null;
            List<int> slots = null;

            for (int k = job.From; k < code.Count && callAt < 0; k++)
            {
                if (code[k].OpCode != OpCodes.Call && code[k].OpCode != OpCodes.Callvirt) continue;
                if (!(code[k].Operand is MethodReference cmr)) continue;

                MethodDefinition cd;

                try { cd = cmr.Resolve(); }
                catch { continue; }

                if (cd == null || ctx.AllParamSlots == null
                    || !ctx.AllParamSlots.TryGetValue(cd, out List<int> cs)) continue;

                callAt = k;
                callee = cd;
                slots = cs;
            }

            bool dbg0 = Environment.GetEnvironmentVariable("EDEN_QUALITY_DEBUG") == ctx.Method.Name;

            if (callAt <= job.From || callee == null || slots == null)
            {
                if (dbg0) Console.Error.WriteLine($"[fwd] 找不到带载荷形参的 call from={job.From} callAt={callAt}");

                return null;
            }

            // call 是跳转目标的话，插在它前面的代码会被跳过去——寄存器没写而调用照常发生，
            // 表现是那条路径上的品质凭空变成上一次残留的值。宁可认不出来。
            if (IsBranchTarget(code, code[callAt]))
            {
                if (dbg0) Console.Error.WriteLine("[fwd] call 是跳转目标");

                return null;
            }

            int argc = callee.Parameters.Count + (callee.HasThis ? 1 : 0);
            int[] a = ArgStarts(code, callAt, argc);

            if (a == null)
            {
                if (dbg0) Console.Error.WriteLine($"[fwd] 实参边界数不出来 argc={argc} callAt={callAt}");

                return null;
            }

            var outp = new List<Instruction>();

            for (var si = 0; si < slots.Count && si < ctx.Regs.Count; si++)
            {
                // **byref 的载荷形参是出口不是入口**（`out remainInc`），传不进去。
                // 它的品质由被调方写进同一个槽位，调用方要在 call **之后**才接得回来。
                // 那一步还没做——现在只是把它跳过，而不是让整条语句因为它拼不出来。
                // 实测这几处的出参之后都没有再被当成载荷用，所以跳过不丢东西；
                // 哪天有了，它会以「没识别的形状」露出来，而不是悄悄少一笔。
                if (callee.Parameters[slots[si]].ParameterType.IsByReference) continue;

                int ai = slots[si] + (callee.HasThis ? 1 : 0);

                bool dbgf = Environment.GetEnvironmentVariable("EDEN_QUALITY_DEBUG") == ctx.Method.Name;

                if (ai >= argc || a[ai] < job.TrueFrom)
                {
                    if (dbgf) Console.Error.WriteLine($"[fwd] 越界 ai={ai} argc={argc} trueFrom={job.TrueFrom}");

                    return null;
                }

                int end = ai + 1 < argc ? a[ai + 1] - 1 : callAt - 1;

                List<Instruction> v = TwinValue(ctx, code, a[ai], end);

                if (v == null)
                {
                    if (dbgf) Console.Error.WriteLine($"[fwd] 实参{ai} 拼不出来 [{a[ai]}..{end}] "
                                                      + $"末指令 {code[end].OpCode.Name}");

                    return null;
                }

                outp.AddRange(v);
                outp.Add(Instruction.Create(OpCodes.Stsfld, ctx.Regs[si]));
            }

            if (outp.Count == 0) return null;

            job.Insert = callAt - 1;
            job.Anchor = code[callAt - 1];

            return outp;
        }

        private static bool IsBranchTarget(IList<Instruction> code, Instruction target)
        {
            foreach (Instruction i in code)
            {
                if (ReferenceEquals(i.Operand, target)) return true;

                if (i.Operand is Instruction[] many)
                    foreach (Instruction x in many)
                        if (ReferenceEquals(x, target)) return true;
            }

            return false;
        }

        private static bool IsArith(Instruction i) =>
            i.OpCode == OpCodes.Add || i.OpCode == OpCodes.Sub
            || i.OpCode == OpCodes.Mul || i.OpCode == OpCodes.Div;

        /// <summary>
        /// 二元算术表达式的品质版。
        ///
        /// <b>加减：两侧各取品质</b>，认不出来的一侧按 0 算（没有品质来源就是没有品质）。
        /// <b>乘除：只有一侧能带品质</b>，另一侧原样重放当系数——`等级 × 件数` 的品质版是
        /// `品质分 × 件数`。两侧都能带品质就说明这不是缩放，含义不明，宁可认不出来。
        ///
        /// 任何一侧要原样重放时都必须是纯取值：重放一条带副作用的指令是静默的。
        /// </summary>
        private static List<Instruction> TwinArith(Ctx ctx, IList<Instruction> code, int from, int to)
        {
            int[] a = ArgStarts(code, to, 2);

            if (a == null || a[0] < from) return null;

            int lf = a[0], lt = a[1] - 1, rf = a[1], rt = to - 1;

            if (lt < lf || rt < rf) return null;

            // **常量先摘出来，不能交给 TwinValue。**
            //
            // TwinValue 对常量的答案是「品质 0」，那在「X.inc = 4」这种整条语句上是对的,
            // 可一旦常量只是算式里的一项，它就既错了类型也错了含义：
            // `(点数/件数) × 新件数 + 0.5f` 的孪生被写成 `(品质/件数) × 新件数 + (int)0`,
            // 于是 `add` 一边 float 一边 int32 —— **IL 类型不合法，游戏在 PatchAll 时就炸**。
            // 实测就是这一条把 PilerComponent::InternalUpdate 打成了 InvalidProgramException。
            //
            // 常量本来就不是品质来源：乘除里它是系数，加减里它是同一个算式的一部分（四舍五入项）,
            // 两种情况都该**原样重放**。摘出来之后 mul/div 那边的「两侧都带品质=含义不明」
            // 也不会再被常量误触发。
            bool lc = ConstOnly(code, lf, lt);
            bool rc = ConstOnly(code, rf, rt);

            List<Instruction> lv = lc ? null : TwinValue(ctx, code, lf, lt);
            List<Instruction> rv = rc ? null : TwinValue(ctx, code, rf, rt);

            if (code[to].OpCode == OpCodes.Mul || code[to].OpCode == OpCodes.Div)
            {
                // 两侧都能带品质时，靠「谁是每件品质分」决胜：每件品质分 × 件数 = 品质。
                // 原版在这里同一个局部既当件数又当点数（`rem` 既是余下的点数又是件数），
                // 所以光看类型分不开，只能看它是**怎么来的**——带除法出身的那个才是每件量。
                if (lv != null && rv != null)
                {
                    bool lp = lt == lf && IsLdloc(code[lf])
                              && ctx.PerItem.Contains(VarOf(ctx.Method, code[lf]));
                    bool rp = rt == rf && IsLdloc(code[rf])
                              && ctx.PerItem.Contains(VarOf(ctx.Method, code[rf]));

                    if (lp == rp) return null;

                    if (lp) rv = null;
                    else lv = null;
                }

                if (code[to].OpCode == OpCodes.Div && lv == null) return null;
                var outp = new List<Instruction>();

                if (lv != null) outp.AddRange(lv);
                else if (!PureRange(code, lf, lt)) return null;
                else for (int k = lf; k <= lt; k++) outp.Add(Clone(code[k]));

                if (rv != null) outp.AddRange(rv);
                else if (!PureRange(code, rf, rt)) return null;
                else for (int k = rf; k <= rt; k++) outp.Add(Clone(code[k]));

                outp.Add(Instruction.Create(code[to].OpCode));

                return outp;
            }

            // **加减要求两侧都带品质**，一侧记 0 是错的。
            //
            // 判据是量纲：`点数 + 点数` 有意义，`件数 - 点数` 没有——而原版里正有这种混用。
            // StorageComponent::TakeTailItemsByIncTable 的 `n = count - rem` 就是：
            // rem 是余下的点数，n 是「带 level 点的件数」，结果是**件数**不是点数。
            // 按「认不出的一侧记 0」去孪生它，会得到一个假的品质值往下游流，
            // 而下游拿它去乘、去减真实品质——不报错，品质凭空多出来或少掉。
            // 例外：一侧是**常量**时原样重放，而不是要求它也带品质。
            // `(点数/件数) × 新件数 + 0.5` 里那个 0.5 是四舍五入项，品质那边同样要加——
            // 它不是另一个量纲，是同一个算式的一部分。
            if (lc && rv != null) lv = new List<Instruction> { Clone(code[lf]) };
            if (rc && lv != null) rv = new List<Instruction> { Clone(code[rf]) };

            // **加法可以把认不出来的一侧当 0，减法不行。** 这不是对称的：
            //   `a + b`，b 的品质不明 → 取 0 是**少算**，品质只会丢不会凭空出现。
            //   `a - b`，b 的品质不明 → 取 0 是**少减**，等于凭空多出品质。
            // 原版在这一带本来就混用件数和点数（`rem -= count`），所以「不明」是常态,
            // 方向选保守的那一边。减法照旧两侧都要有品质，`件数 - 点数` 仍然被挡住。
            // 那个 0 的**类型要跟着另一侧走**。整数算式里是 `ldc.i4.0`，浮点算式里必须是
            // `ldc.r4 0`——写错一边就是上面那条 InvalidProgramException，而它在离线
            // 结构校验里完全看不出来（分支目标都对、写盘重读都干净）。
            if (code[to].OpCode == OpCodes.Add)
            {
                if (lv == null && rv != null && PureRange(code, lf, lt))
                    lv = new List<Instruction> { Zero(IsFloatRange(code, rf, rt)) };

                if (rv == null && lv != null && PureRange(code, rf, rt))
                    rv = new List<Instruction> { Zero(IsFloatRange(code, lf, lt)) };
            }

            if (lv == null || rv == null) return null;

            var sum = new List<Instruction>();

            sum.AddRange(lv);
            sum.AddRange(rv);
            sum.Add(Instruction.Create(code[to].OpCode));

            return sum;
        }

        /// <summary>
        /// 对**一条孪生语句**做栈类型模拟：算出每条指令压的是什么，并检查二元运算两边对不对得上。
        ///
        /// <b>这是那次崩溃直接换来的检查。</b> 离线校验原来只验结构——分支目标可解析、
        /// 写盘重读干净——而 `float + int32` 这种在结构上完全正常，
        /// 只有 CLR 在 JIT 的时候才会说 `InvalidProgramException: IL_0588: add`,
        /// 而那时游戏已经起不来了，栈里连我们的名字都没有（报的是 Harmony 的 DMD）。
        ///
        /// 只认自己发射的那点指令集合；遇到不认识的一律记成「不确定」并放行——
        /// 这个检查的职责是**抓住确定的错**，不是证明整条 IL 合法。
        /// </summary>
        private static string TypeError(Ctx ctx, IList<Instruction> emit)
        {
            var stack = new List<char>();   // 'i' 整数 / 'f' 浮点 / '?' 不确定

            foreach (Instruction i in emit)
            {
                string n = i.OpCode.Name;

                if (n.StartsWith("ldc.r", StringComparison.Ordinal)) { stack.Add('f'); continue; }
                if (n.StartsWith("ldc.", StringComparison.Ordinal)) { stack.Add('i'); continue; }
                if (n == "conv.r4" || n == "conv.r8") { Pop(stack); stack.Add('f'); continue; }
                if (n.StartsWith("conv.", StringComparison.Ordinal)) { Pop(stack); stack.Add('i'); continue; }

                if (n.StartsWith("ldloc", StringComparison.Ordinal)
                    || n.StartsWith("ldarg", StringComparison.Ordinal)
                    || n.StartsWith("ldsfld", StringComparison.Ordinal)
                    || n.StartsWith("ldfld", StringComparison.Ordinal)
                    || n.StartsWith("ldelem", StringComparison.Ordinal)
                    || n.StartsWith("ldind", StringComparison.Ordinal)
                    || n == "ldnull" || n == "ldlen" || n == "newarr")
                {
                    // 取字段/取元素会先弹掉对象，取局部取参数不会——这里只关心栈顶的类型,
                    // 弹几个由 Push/Pop 表算，免得两套规则各说各话。
                    for (var p = 0; p < Pop(i); p++) Pop(stack);

                    stack.Add(TypeOf(ctx, i));

                    continue;
                }

                if (n == "dup") { stack.Add(stack.Count > 0 ? stack[stack.Count - 1] : '?'); continue; }

                if (n == "add" || n == "sub" || n == "mul" || n == "div")
                {
                    char b = Pop(stack), a = Pop(stack);

                    if (a != '?' && b != '?' && a != b)
                        return $"{n} 两边类型对不上（{(a == 'f' ? "float" : "int")} 与 " +
                               $"{(b == 'f' ? "float" : "int")}）";

                    stack.Add(a == '?' ? b : a);

                    continue;
                }

                for (var p = 0; p < Pop(i); p++) Pop(stack);

                if (Push(i) > 0) stack.Add('?');
            }

            return null;
        }

        private static char Pop(List<char> s)
        {
            if (s.Count == 0) return '?';

            char c = s[s.Count - 1];

            s.RemoveAt(s.Count - 1);

            return c;
        }

        /// <summary>这条取值指令压上来的是整数还是浮点。</summary>
        private static char TypeOf(Ctx ctx, Instruction i)
        {
            switch (i.Operand)
            {
                case FieldReference f: return Float(f.FieldType) ? 'f' : 'i';
                case VariableDefinition v: return Float(v.VariableType) ? 'f' : 'i';
                case ParameterDefinition p: return Float(p.ParameterType) ? 'f' : 'i';
            }

            string n = i.OpCode.Name;

            if (n == "ldind.r4" || n == "ldind.r8") return 'f';
            if (n == "ldnull" || n == "newarr") return '?';

            return 'i';
        }

        private static bool Float(TypeReference t) =>
            t.MetadataType == MetadataType.Single || t.MetadataType == MetadataType.Double;

        /// <summary>这一段是不是<b>只有一条常量指令</b>。</summary>
        private static bool ConstOnly(IList<Instruction> code, int from, int to) =>
            from == to && code[from].OpCode.Name.StartsWith("ldc", StringComparison.Ordinal);

        /// <summary>类型对得上的零。</summary>
        private static Instruction Zero(bool asFloat) =>
            asFloat ? Instruction.Create(OpCodes.Ldc_R4, 0f) : Instruction.Create(OpCodes.Ldc_I4_0);

        /// <summary>这一段算出来的是浮点吗——只要里面出现过浮点常量或转换就是。</summary>
        private static bool IsFloatRange(IList<Instruction> code, int from, int to)
        {
            for (int k = from; k <= to; k++)
            {
                string n = code[k].OpCode.Name;

                if (n == "conv.r4" || n == "conv.r8" || n == "ldc.r4" || n == "ldc.r8") return true;
            }

            return false;
        }

        private static bool PureRange(IList<Instruction> code, int from, int to)
        {
            for (int k = from; k <= to; k++)
                if (!IsPureLoad(code[k]) && !IsArith(code[k]))
                    return false;

            return true;
        }

        /// <summary>这条指令引用了一个<b>已经作废</b>的载荷局部吗？</summary>
        private static bool Orphan(MethodDefinition m, IList<Instruction> code, int at,
            ICollection<VariableDefinition> carriers)
        {
            if (!IsStloc(code[at]) && !IsLdloc(code[at])) return false;

            VariableDefinition v = VarOf(m, code[at]);

            return v != null && !carriers.Contains(v);
        }

        /// <summary>这个方法体里有没有新建<paramref name="t"/> 的容器（<c>newarr</c>/<c>newobj</c>）。</summary>
        private static bool AllocatesFresh(MethodDefinition m, TypeReference t)
        {
            foreach (Instruction i in m.Body.Instructions)
            {
                if (i.OpCode == OpCodes.Newarr && i.Operand is TypeReference at
                    && at.FullName == t.FullName) return true;

                if (i.OpCode == OpCodes.Newobj && i.Operand is MethodReference mr
                    && mr.DeclaringType.FullName == t.FullName) return true;
            }

            return false;
        }

        /// <summary>这一段里有没有 <c>BinaryReader</c> 的读调用——即「值来自存档」。</summary>
        private static bool ReadsSave(IList<Instruction> code, int from, int to)
        {
            for (int k = Math.Max(0, from); k <= to; k++)
                if (code[k].Operand is MethodReference mr
                    && mr.DeclaringType.FullName == "System.IO.BinaryReader")
                    return true;

            return false;
        }

        /// <summary>目的地对象（从汇合之前取）+ 0 + 写进孪生字段。</summary>
        private static List<Instruction> ZeroInto(Ctx ctx, IList<Instruction> code, Job job,
            FieldDefinition tf)
        {
            List<Instruction> obj = MergeObject(ctx, code, job.TrueFrom, job.To);

            if (obj == null) return null;

            obj.Add(Instruction.Create(OpCodes.Ldc_I4_0));
            obj.Add(Instruction.Create(OpCodes.Stfld, tf));

            return obj;
        }

        /// <summary>右边是不是正好「自己 ± 常量」（后面允许一个 dup）。</summary>
        private static bool IsSelfStep(MethodDefinition m, IList<Instruction> code, int from, int to,
            VariableDefinition dst)
        {
            if (dst == null) return false;

            if (to > from && code[to].OpCode == OpCodes.Dup) to--;

            if (to - from != 2) return false;
            if (code[to].OpCode != OpCodes.Add && code[to].OpCode != OpCodes.Sub) return false;
            if (!IsLdloc(code[from]) || !ReferenceEquals(VarOf(m, code[from]), dst)) return false;

            return code[from + 1].OpCode.Name.StartsWith("ldc", StringComparison.Ordinal);
        }

        /// <summary>
        /// 这段表达式里有没有<b>品质来源</b>：主干道载荷字段、载荷参数，或另一个载荷局部。
        ///
        /// 「带不带品质」不能用「TwinValue 拼不拼得出来」来判——常量拼得出来（是 0），
        /// 可它并不是一个品质来源。两者混同会把「重算一个与品质无关的量」当成
        /// 「把品质改成 0」。
        /// </summary>
        private static bool CarriesQuality(MethodDefinition m, IList<Instruction> code, int from, int to,
            ICollection<VariableDefinition> cand, Ctx ctx)
        {
            for (int k = from; k <= to; k++)
            {
                if (code[k].Operand is FieldReference fr
                    && ctx.Twin.ContainsKey(fr.DeclaringType.FullName + "::" + fr.Name)) return true;

                if (IsLdloc(code[k]) && VarOf(m, code[k]) is VariableDefinition v && cand.Contains(v))
                    return true;

                if (IsLdarg(code[k]) && ParamOf(m, code[k]) is ParameterDefinition p
                    && ctx.ParamSlot.ContainsKey(p)) return true;
            }

            return false;
        }

        /// <summary>这条语句定义了哪个局部。绝大多数看末尾的 stloc，出参那种看 Dst。</summary>
        private static VariableDefinition DefOf(MethodDefinition m, IList<Instruction> code, Stmt st) =>
            st.Dst ?? VarOf(m, code[st.To]);

        private static bool IsLdarg(Instruction i) =>
            i.OpCode == OpCodes.Ldarg || i.OpCode == OpCodes.Ldarg_S ||
            i.OpCode == OpCodes.Ldarg_0 || i.OpCode == OpCodes.Ldarg_1 ||
            i.OpCode == OpCodes.Ldarg_2 || i.OpCode == OpCodes.Ldarg_3;

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

                VariableDefinition v = DefOf(m, code, st);

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
        /// <summary>
        /// 拼孪生语句，并在交出去之前<b>过一遍栈类型</b>。
        ///
        /// 类型检查放在这个唯一出口上，而不是各个发射器里：分类遍、不动点、发射遍
        /// 走的都是这里，三处的判断因此永远一致。**类型不合法等同于拼不出来**——
        /// 报成缺口，而不是发射出去让 CLR 在玩家那边说 InvalidProgramException。
        /// </summary>
        private static List<Instruction> Build(Ctx ctx, IList<Instruction> code, Job job)
        {
            List<Instruction> emit = BuildCore(ctx, code, job);

            if (emit == null || emit.Count == 0) return emit;

            string bad = TypeError(ctx, emit);

            if (bad == null) return emit;

            job.TypeError = bad;
            ctx.TypeErrors[job.To] = bad;

            return null;
        }

        private static List<Instruction> BuildCore(Ctx ctx, IList<Instruction> code, Job job)
        {
            Instruction store = code[job.To];

            // **目标字段只对 stfld 那两种形状有意义。** 第一版把它当成所有形状的前置，
            // 于是 `ldfld:PAY stloc`（末指令是 stloc）和读-改-写（末指令是 stind.i4）
            // 一律在这里就返回 null —— 表现是新写的五个发射器全部落成 [opaque]，
            // 看起来像「这些形状拼不出来」，其实是取字段那一步就错了。
            // **而且只在末指令真的是「写字段」时才取。** 不加这个限制的话，末指令下标一旦过期
            // （同一条语句上挂着两个 Job，先插入的那个会把后面的下标顶走），
            // 这里会读到刚插进去的 `ldsfld Q0`——它也带着一个 FieldReference，只是不在孪生表里,
            // 于是整条语句在进 switch 之前就返回 null。表现是「分析遍认得、发射遍拼不出来」,
            // 而两遍的代码完全一样。
            FieldDefinition tf = null;

            if (store.OpCode == OpCodes.Stfld || store.OpCode == OpCodes.Stsfld)
                if (store.Operand is FieldReference sf
                    && !ctx.Twin.TryGetValue(sf.DeclaringType.FullName + "::" + sf.Name, out tf))
                    return null;

            switch (job.Core)
            {
                // X.inc = 常数  →  X.qua = 0
                case "ldc stfld:PAY":
                case "call:get_package ldc stfld:PAY":
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
                case "ldfld:PAY ldfld:PAY add stfld:PAY":
                {
                    if (tf == null) return null;

                    // 从存档读进来、而且两个版本分支各读一种宽度：值那一侧是个 phi，
                    // 往回数实参会撞上分支目标，重放也会把读操作做第二遍。
                    // 这一族的品质本来就是 0（写侧还没进存档），所以只要把目的地对象
                    // 重放出来就够——对象在汇合**之前**压栈，从 TrueFrom 往后找。
                    // 判据是这条语句里真的有 BinaryReader 调用，不是「拼不出来就当 0」。
                    // 往回多看一小段：版本分支把语句切碎了，TrueFrom 只落在 stfld 自己身上,
                    // 而那条 BinaryReader 调用就在前面几条指令。窗口写死，宁可看不见也不乱认。
                    if (ReadsSave(code, Math.Min(job.TrueFrom, job.From - 16), job.To))
                    {
                        List<Instruction> z = ZeroInto(ctx, code, job, tf);

                        if (z != null) return z;

                        // 目的地对象拼不回来（值那一侧是版本分支的 phi，栈上的对象在汇合之前
                        // 就压好了，后向回溯跨不过去）。**但这里确实什么都不用做**：
                        // 这个方法自己 newarr 出了容器，每个元素的孪生字段天生就是 0。
                        // 这是核实过的依据，不是「拼不出来就当没事」——核实不了就照样报缺口。
                        if (AllocatesFresh(ctx.Method, tf.DeclaringType)) return new List<Instruction>();

                        return null;
                    }

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

                // *outInc = <值>  →  Q<槽位> = <值的品质版>
            //
            // 和 `*outInc += ...` 同一族，只是赋值不是累加。目的地是 `ref` 出参，
            // 品质那边写进侧信道寄存器——出参那条路本来就是被推翻的「加参数」方案的替身。
            //
            // **宽度不用跟着原版走。** 原版这里是 stind.i2（Cargo.inc 被加宽成 Int16 之后的样子），
            // 而孪生槽位是静态 Int32 字段，写它用 stsfld，没有解引用这回事。
            // 一条语句里写了**两个**出参：`*outStack = cargo.stack; *outInc = cargo.inc; return true;`
            // ——`return true` 的那个 `ldc.i4.1` 先压栈，栈深一路不归零，于是整段被切成一条语句。
            // 只有带载荷的那一次 stind 要孪生，另一次和末尾的 stloc 都不用动。
            case "stind.i2 ldfld:PAY stind.i2 stloc":
            {
                for (int k = job.From; k <= job.To; k++)
                {
                    if (!IsStind(code[k])) continue;

                    int[] sa2 = ArgStarts(code, k, 2);

                    if (sa2 == null || sa2[0] < job.From) continue;
                    if (!HasMainline(code, sa2[1], k - 1, ctx.Twin)) continue;
                    if (sa2[1] - sa2[0] != 1) continue;

                    ParameterDefinition tp = ParamOf(ctx.Method, code[sa2[0]]);

                    if (tp == null || !ctx.ParamSlot.TryGetValue(tp, out int tslot)
                                   || tslot >= ctx.Regs.Count) continue;

                    List<Instruction> tv2 = TwinValue(ctx, code, sa2[1], k - 1);

                    if (tv2 == null) return null;

                    tv2.Add(Instruction.Create(OpCodes.Stsfld, ctx.Regs[tslot]));

                    return tv2;
                }

                return null;
            }

            case "ldfld:PAY stind.i2":
            case "ldfld:PAY stind.i4":
            case "ldflda:PAY ldind.i4 call:split_inc stind.i4":
            {
                if (!IsStind(store)) return null;

                ParameterDefinition sp2 = ParamOf(ctx.Method, code[job.From]);

                if (sp2 == null || !ctx.ParamSlot.TryGetValue(sp2, out int sslot)
                                || sslot >= ctx.Regs.Count) return null;

                List<Instruction> sv = TwinValue(ctx, code, job.From + 1, job.To - 1);

                if (sv == null) return null;

                sv.Add(Instruction.Create(OpCodes.Stsfld, ctx.Regs[sslot]));

                return sv;
            }

            // X.inc = 条件 ? a : b  →  X.qua = 0
            //
            // 五处全在 StationComponent::UpdateKeepMode——站点「保留 N 份」那个设置，
            // 它是**凭空生成**物品（按 keepIncRatio 顺带生成增产点数），不是搬运。
            // 生成出来的东西没有品质来源，所以品质是 0，而不是「拼不出来」。
            //
            // 值那一侧是 phi，后向栈回溯拼不出来也不需要拼；要拼的只有目的地对象，
            // 而它在汇合**之前**压栈，所以从 TrueFrom 往后找而不是从 From 往回找。
            case "stfld:PAY [merge]":
            {
                if (tf == null) return null;

                List<Instruction> obj = MergeObject(ctx, code, job.TrueFrom, job.From);

                if (obj == null) return null;

                obj.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                obj.Add(Instruction.Create(OpCodes.Stfld, tf));

                return obj;
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
                // **内层那条 stelem 不一定在 To-1。** `served[i] = (incServed[i] = 0)` 的尾巴是
                // `stelem.i4 ; ldloc 副本 ; stelem.i4`——按 To-1 找会落在中间那条 ldloc 上。
                var inner = -1;

                for (int k = job.From; k < job.To; k++)
                    if (code[k].OpCode.Name.StartsWith("stelem", StringComparison.Ordinal))
                    {
                        inner = k;

                        break;
                    }

                if (inner < 0) return null;

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

            // 把品质**传进被调方**：在 call 之前写好侧信道寄存器。
            //
            // <b>这是侧信道缺的那一半。</b> 在这之前只有「被调方把结果写出来」
            // （出参那一族 `Q = ...`），没有「调用方把品质送进去」——寄存器有人读没人写，
            // 品质在第一个方法边界上就断了，而报告里一切正常。
            //
            // 插在 call 的前一条之后：实参已经压完栈，这时插入是栈中性的，
            // 而寄存器在 call 执行前就位。
            case "ldfld:PAY ldc ldc call:TryAddItemToPackage stloc":
            case "ldfld:PAY ldc ldc ldc call:TryAddItemToPackage stloc":
            case "ldfld:PAY call:AddItemStacked bge.s":
            case "ldfld:PAY call:AddItemStacked stloc":
                return BuildForwardToCallee(ctx, code, job);

            // 被调方通过 out 形参写回来：twinLocal = Q<槽位>，插在 call 之后。
            case "call:out":
            {
                if (job.Dst == null || !ctx.Locals.TryGetValue(job.Dst, out VariableDefinition otv))
                    return null;

                // 同样：要找有载荷形参的那个 call，不是第一个
                var ocall = -1;

                for (int k = job.From; k < code.Count && ocall < 0; k++)
                {
                    if (code[k].OpCode != OpCodes.Call && code[k].OpCode != OpCodes.Callvirt) continue;
                    if (!(code[k].Operand is MethodReference kmr)) continue;

                    MethodDefinition kd;

                    try { kd = kmr.Resolve(); }
                    catch { continue; }

                    if (kd != null && ctx.AllParamSlots != null && ctx.AllParamSlots.ContainsKey(kd))
                        ocall = k;
                }

                if (ocall < 0 || !(code[ocall].Operand is MethodReference omr)) return null;

                MethodDefinition ocd;

                try { ocd = omr.Resolve(); }
                catch { return null; }

                if (ocd == null || ctx.AllParamSlots == null
                    || !ctx.AllParamSlots.TryGetValue(ocd, out List<int> oslots)) return null;

                int oargc = ocd.Parameters.Count + (ocd.HasThis ? 1 : 0);
                int[] oa = ArgStarts(code, ocall, oargc);

                if (oa == null) return null;

                for (var si2 = 0; si2 < oslots.Count && si2 < ctx.Regs.Count; si2++)
                {
                    int ai2 = oslots[si2] + (ocd.HasThis ? 1 : 0);

                    if (ai2 >= oargc) continue;
                    if (!(code[oa[ai2]].Operand is VariableDefinition av)
                        || !ReferenceEquals(av, job.Dst)) continue;

                    job.Insert = ocall;
                    job.Anchor = code[ocall];

                    return otv == null
                        ? new List<Instruction>()
                        : new List<Instruction>
                        {
                            Instruction.Create(OpCodes.Ldsfld, ctx.Regs[si2]),
                            Instruction.Create(OpCodes.Stloc, otv),
                        };
                }

                return null;
            }

            // local = <载荷参数>  →  twinLocal = Q<槽位>
            case "ldarg:PAY stloc":
            {
                ParameterDefinition qp = ParamOf(ctx.Method, code[job.From]);

                VariableDefinition qdst = VarOf(ctx.Method, store);

                if (qp == null || qdst == null) return null;
                if (!ctx.ParamSlot.TryGetValue(qp, out int qslot) || qslot >= ctx.Regs.Count) return null;
                if (!ctx.Locals.TryGetValue(qdst, out VariableDefinition qtv)) return null;

                var qout = new List<Instruction> { Instruction.Create(OpCodes.Ldsfld, ctx.Regs[qslot]) };

                if (qtv == null) return qout;

                qout.Add(Instruction.Create(OpCodes.Stloc, qtv));

                return qout;
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
                // V = <与品质无关的重算>：夹取、走表下标、按件数封顶。品质不动。
                // **空列表不是失败**——这条语句确实什么都不用发射，而那和「拼不出来」是两回事。
                case "recalc stloc":
                    return VarOf(ctx.Method, store) is VariableDefinition cv
                           && ctx.Locals.ContainsKey(cv)
                        ? new List<Instruction>()
                        : null;

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
                case "call:get_Value ldfld:PAY stloc":   // V = kvp.Value.inc（取值器已核实是纯读字段）
                case "calc stloc":                       // V = 件数 × 每件品质分（纯算术）
                case "ldfld:PAY mul sub stloc":          // V = X.inc - 等级 × 件数
                case "ldfld:PAY div mul ldc add stloc":   // V = (点数/件数) × 新件数 + 0.5（自动集装机）
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
                case "ldflda:PAY dup ldind.i2 add stind.i2":
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

            // 加宽之后传送带那侧是 `add ; conv.i2 ; stind.i2`——收窄那一条要跳过。
            // 孪生字段是 Int32，本来就不需要收窄，所以只是不复制它。
            int opAt = job.To - 1;

            if (opAt > job.From && code[opAt].OpCode.Name.StartsWith("conv.", StringComparison.Ordinal))
                opAt--;

            Instruction op = code[opAt];

            if (op.OpCode != OpCodes.Add && op.OpCode != OpCodes.Sub) return null;

            // 找 dup（它后面紧跟 ldind.i4）
            var dupAt = -1;

            for (int k = job.From; k < opAt; k++)
                if (code[k].OpCode == OpCodes.Dup && IsLdind(code[k + 1]))
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

            List<Instruction> val = TwinValue(ctx, code, dupAt + 2, opAt - 1);

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

            /// <summary>
            /// 哪条语句拼出来的 IL 类型不合法（语句末指令下标 → 说法）。
            ///
            /// 记在上下文里而不是 Job 上：类型错误最先是在**不动点**里被发现的，
            /// 那一轮会把它的目的地局部判死，等分类遍再来时它已经因为「局部不可用」而失败,
            /// 于是报成一句没有信息量的 [opaque]——原因在两步之前就被丢掉了。
            /// </summary>
            internal readonly Dictionary<int, string> TypeErrors = new Dictionary<int, string>();

            /// <summary>
            /// <b>全模块</b>的「哪个方法的哪几个形参是载荷」。
            /// 往被调方传品质时要按<b>被调方</b>的槽位写寄存器，所以这里要的是全局表，
            /// 不是当前方法那一份。
            /// </summary>
            internal IDictionary<MethodDefinition, List<int>> AllParamSlots;
            internal Dictionary<VariableDefinition, VariableDefinition> Locals =
                new Dictionary<VariableDefinition, VariableDefinition>();
            /// <summary>
            /// 「每件品质分」那一类局部：定义式里带除法（<c>X.inc / 件数</c>）。
            ///
            /// 乘法里两侧都能带品质时靠它决胜：**每件品质分 × 件数 = 品质**，
            /// 所以带除法出身的那一侧才是品质，另一侧是件数、原样重放。
            /// 没有它就只能判「含义不明」而放弃，那会把
            /// `X.inc -= 件数 × 每件点数` 这一族卡住——原版在那里同一个局部既当件数又当点数。
            /// </summary>
            internal readonly HashSet<VariableDefinition> PerItem = new HashSet<VariableDefinition>();

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
            // (0.1) 收尾的转换指令原样带走：`X.inc ; conv.r4` 的品质版就是 `X.qua ; conv.r4`。
            // 自动集装机那条是浮点算式（`inc / stack * newStack + 0.5f`），不放行就整条拼不出来。
            if (to > from && code[to].OpCode.Name.StartsWith("conv.", StringComparison.Ordinal))
            {
                List<Instruction> inner = TwinValue(ctx, code, from, to - 1);

                if (inner == null) return null;

                inner.Add(Clone(code[to]));

                return inner;
            }

            // (0.2) `*incParam`：载荷参数是 `ref int` 时，读它的值写作 `ldarg ; ldind`。
            // 品质那一侧在侧信道寄存器里，不需要（也没有）解引用这一步。
            if (to == from + 1 && IsLdind(code[to]) && IsLdarg(code[from]))
            {
                ParameterDefinition rp = ParamOf(ctx.Method, code[from]);

                if (rp != null && ctx.ParamSlot.TryGetValue(rp, out int rslot) && rslot < ctx.Regs.Count)
                    return new List<Instruction> { Instruction.Create(OpCodes.Ldsfld, ctx.Regs[rslot]) };
            }

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

            // (0.5) 算术：加减各取两侧的品质，乘除把品质按同一个系数缩放。
            //
            // 这是**表达式递归**，不是又一个形状：`X.inc + Y.inc`、`X.inc - 等级 * 件数`
            // 都是同一条规则的实例。加减法里认不出品质的那一侧记 0——那一侧没有品质来源，
            // 0 就是它的品质，不是「拼不出来」；但它必须是纯取值，否则重放会把副作用做两遍。
            if (to > from && IsArith(code[to]))
            {
                List<Instruction> ar = TwinArith(ctx, code, from, to);

                if (ar != null) return ar;
            }

            // (0.7) 载荷**数组元素**：`incServed[i]` 的品质版是 `incServedQua[i]`。
            // 数组表达式里那个载荷字段换成孪生字段，下标原样重放。
            if (to > from && code[to].OpCode.Name.StartsWith("ldelem", StringComparison.Ordinal))
            {
                int[] ea = ArgStarts(code, to, 2);

                if (ea != null && ea[0] >= from && HasMainline(code, ea[0], ea[1] - 1, ctx.Twin))
                {
                    var el = new List<Instruction>();
                    var ok = true;

                    for (int k = ea[0]; k < to; k++)
                    {
                        if (!IsStructural(code[k], false)) { ok = false; break; }

                        el.Add(CloneSwap(ctx, code[k]));
                    }

                    if (ok)
                    {
                        el.Add(Instruction.Create(OpCodes.Ldelem_I4));

                        return el;
                    }
                }
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
                   || n == "dup" || n.StartsWith("conv.", StringComparison.Ordinal)
                   // 解引用是纯读：`*count` 这种实参在 split_inc 的第三个位置上很常见,
                   // 不放行的话整条 split 都拼不出来。
                   || n.StartsWith("ldind.", StringComparison.Ordinal)
                   || IsTrivialGetter(i);
        }

        /// <summary>
        /// 这条 <c>call</c> 是不是一个<b>只读一个字段的取值器</b>——例如
        /// <c>KeyValuePair&lt;,&gt;::get_Value</c>、<c>Player::get_package</c>。
        ///
        /// <b>这是核实过的，不是按名字猜的。</b> 把方法解析出来看方法体：
        /// 只有 <c>ldarg.0 ; ldfld ; ret</c> 这种形状才算数。按 <c>get_</c> 前缀放行会
        /// 放进带副作用的属性（惰性初始化、计数器），而重放一条带副作用的指令是静默的。
        /// 解析不出来（跨程序集拿不到方法体）就当不纯——宁可认不出来。
        /// </summary>
        private static bool IsTrivialGetter(Instruction i)
        {
            if (i.OpCode != OpCodes.Call && i.OpCode != OpCodes.Callvirt) return false;
            if (!(i.Operand is MethodReference mr) || mr.HasParameters || !mr.HasThis) return false;

            MethodDefinition md;

            try { md = mr.Resolve(); }
            catch { return false; }

            if (md?.Body == null) return false;

            IList<Instruction> b = md.Body.Instructions;

            if (b.Count != 3) return false;

            return b[0].OpCode == OpCodes.Ldarg_0
                   && (b[1].OpCode == OpCodes.Ldfld || b[1].OpCode == OpCodes.Ldflda)
                   && b[2].OpCode == OpCodes.Ret;
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
            "recalc stloc",

            // localB = localA → twinB = twinA。同样是补进去的。
            "ldloc stloc",

            // local = 载荷参数 → twinLocal = Q<槽位>。和上一条是两回事：
            // 参数那一侧的品质在侧信道寄存器里，不在某个孪生局部里。
            "ldarg:PAY stloc",
            "call:out",

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

            // 出参赋值一族（累加那一族的兄弟），以及站点「保留 N 份」的凭空生成
            "stind.i2 ldfld:PAY stind.i2 stloc",
            "ldfld:PAY stind.i2",
            "ldfld:PAY stind.i4",
            "ldflda:PAY ldind.i4 call:split_inc stind.i4",
            "stfld:PAY [merge]",
            "ldflda:PAY dup ldind.i2 add stind.i2",

            // 表达式递归（加减乘除）打通的几种：取值器前缀、两个载荷相加、按系数缩放
            "call:get_Value ldfld:PAY stloc",
            "calc stloc",
            "ldfld:PAY div mul ldc add stloc",
            "call:get_package ldc stfld:PAY",
            "ldfld:PAY ldfld:PAY add stfld:PAY",
            "ldfld:PAY mul sub stloc",

            // 把品质传进被调方：在 call 之前写好寄存器。侧信道缺的那一半。
            "ldfld:PAY ldc ldc call:TryAddItemToPackage stloc",
            "ldfld:PAY ldc ldc ldc call:TryAddItemToPackage stloc",
            "ldfld:PAY call:AddItemStacked bge.s",
            "ldfld:PAY call:AddItemStacked stloc",

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
            "ldc stloc",                                            // local = 0（零初始化）
            "recalc stloc",                                         // local = 与品质无关的重算
            "ldloc stloc",                                          // local = 另一个载荷局部
            "ldarg:PAY stloc",                                      // local = 载荷参数
            "call:out",                                             // local 由被调方 out 形参写回
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
            "call:get_Value ldfld:PAY stloc",                       // V = kvp.Value.inc
            "calc stloc",                                           // V = 件数 × 每件品质分
            "call:get_package ldc stfld:PAY",                       // package.X.inc = 常数
            "ldfld:PAY ldfld:PAY add stfld:PAY",                    // X.inc = A.inc + B.inc
            "ldfld:PAY mul sub stloc",                              // V = X.inc - 等级 × 件数
            "ldfld:PAY ldc ldc call:TryAddItemToPackage stloc",     // 把品质传进被调方
            "ldfld:PAY ldc ldc ldc call:TryAddItemToPackage stloc",
            "ldfld:PAY call:AddItemStacked bge.s",
            "ldfld:PAY call:AddItemStacked stloc",
            "stind.i2 ldfld:PAY stind.i2 stloc",                    // 一条语句写两个出参
            "ldfld:PAY stind.i2",                                   // *out = X.inc
            "ldfld:PAY stind.i4",
            "ldflda:PAY ldind.i4 call:split_inc stind.i4",          // *out = split_inc(...)
            "stfld:PAY [merge]",                                    // X.inc = 条件 ? a : b（保留模式凭空生成）
            "ldflda:PAY dup ldind.i2 add stind.i2",                 // 加宽之后的字节读-改-写
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

            // **只是拿去显示。** 悬浮面板的物品汇总、生产统计面板的投入格——
            // 它们接过载荷数组或点数只为了画出来，一个点数都没有搬走。
            // 品质面板是第 4 阶段的事，1c 在这里什么都不用做。
            "ldfld:PAY call:set_incServed",                         // 统计面板接过 incServed 数组
            "ldfld:PAY call:Add",                                   // 悬浮面板的物品汇总
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
