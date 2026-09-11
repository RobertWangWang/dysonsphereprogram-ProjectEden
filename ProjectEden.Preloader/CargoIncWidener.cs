using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ProjectEden.Preloader
{
    /// <summary>
    /// 把 <c>Cargo.inc</c> 从 <c>Byte</c> 加宽成 <c>Int16</c>，连同承载它的那套字节 API。
    ///
    /// <b>为什么非得用 preloader。</b> Harmony 改的是方法体，改不了类型结构——
    /// 字段类型、方法签名、结构体大小都只有在 CLR 装载之前、拿着 Cecil 的元数据视图才能动。
    ///
    /// <b>为什么值得。</b> 传送带上每堆货的增产点数存在这一个字节里，存的是<b>整堆的总点数</b>，
    /// 增产剂 Mk.III 每件 4 点，于是 <c>stack × 4 ≤ 255</c> 把「还能满级增产」的层数钉死在 <b>63</b>。
    /// 本 mod 出厂 255 层，1020 点装不下。
    ///
    /// <b>为什么不改成「每件速率」（那样不用 preloader）。</b> 量过了：总点数要穿过的那套 API
    /// 是字节宽的——取出方向 23 个方法 59 个调用点、放入方向 22 个方法 50 个调用点。
    /// 改语义意味着手写 109 处换算，漏一处不会报错，只会把增产账悄悄算错。
    /// 加宽则是**类型**改动：同一批调用点由这里一次过、带断言地处理，语义一个字都不动。
    /// <b>preloader 不是额外成本，它正是让这 109 个点变便宜的东西。</b>
    ///
    /// <b>三条铁律，违反任何一条就整体放弃改写。</b>
    /// 宁可什么都不做（游戏照常跑、夹取仍在兜底），也不要交出一个改了一半的 Assembly-CSharp——
    /// 那会变成 CLR 类型加载失败，报错指向的地方和真正的原因毫无关系。
    /// 所以每一步都先断言再动手，任何一处看不懂的形状都记进 <see cref="Report.Blockers"/>。
    /// </summary>
    internal static class CargoIncWidener
    {
        /// <summary>改写结果。计数全部打进日志——匹配为零必须是响的，这是本仓库的规矩。</summary>
        internal class Report
        {
            internal bool Applied;

            /// <summary>致命问题。非空就整体放弃</summary>
            internal readonly List<string> Blockers = new List<string>();

            internal readonly List<string> Notes = new List<string>();

            internal int WidenedParams;
            internal int WidenedLocals;
            internal int FixedIndirect;
            internal int FixedConv;
            internal int CallSites;

            /// <summary>除 Cargo.inc 之外、跟着一起加宽的字段</summary>
            internal readonly List<string> ExtraFields = new List<string>();

            /// <summary>靠数据流推出来、名字上看不出来的参数</summary>
            internal readonly List<string> Propagated = new List<string>();
        }

        private const string CargoType = "Cargo";
        private const string IncField = "inc";

        /// <summary>
        /// 要加宽的 <c>Cargo</c> 字段，以及跟着它们走的参数名前缀。
        ///
        /// <c>inc</c>（整堆增产点数）和 <c>stack</c>（层数）是<b>两个独立的字节</b>，
        /// 卡的是两件不同的事：inc 卡「那么多层还能不能满级增产」，stack 卡「能堆几层」。
        ///
        /// <b>两个一起加宽不会把结构体擑大。</b> 只加宽 inc 时布局是
        /// stack(Byte)@0 、<b>空洞@1</b>、inc(Int16)@2、item(Int16)@4、pos@8、rot@20 = 36 字节。
        /// 把 stack 也换成 Int16 正好填进那个空洞——<b>还是 36 字节</b>，
        /// 所以渲染那边一行都不用改，32 字节重打包照旧用。
        /// </summary>
        private static readonly string[] Watched = { "inc", "stack" };

        /// <summary>
        /// 除 <c>Cargo</c> 之外还要加宽的字段，写成 <c>类型::字段</c>。
        ///
        /// 分拣器一次能搬几层存在
        /// <c>InserterComponent.stackInput</c> / <c>stackOutput</c>，那是两个 <b>Byte</b>。
        /// <c>GameHistoryData</c> 里对应的值是 Int32，看着能随便填，
        /// 但三条赋值路径（<c>NewInserterComponent</c> / <c>OnInserterTechChange</c> /
        /// <c>UpgradeEntityWithComponents</c>）都带 <c>conv.u1</c>——填 5000 会绕成 136，
        /// 比填 255 还差，而且不报错。
        ///
        /// <b>这一块比 Cargo 那次小一个数量级：</b>只有 9 个方法碰它们，
        /// <b>没有任何字节宽的 API 参数</b>（只在分拣器自己的 tick 里读，不传递），
        /// 而且 <c>InserterComponent</c> 不裸传 GPU（分拣器的画面走 AnimData），
        /// 所以结构体变大也无所谓。
        /// </summary>
        /// <summary>
        /// 数据流传播<b>推不到</b>、必须点名的字节参数，写成 <c>类型::方法::参数</c>。
        ///
        /// 传播是<b>倒着</b>走的：实参喂给一个已加宽的形参，那个实参也得加宽。
        /// 但 <c>PlanetFactory.InsertInto</c> 的 <c>remainInc</c> 是**出参**——
        /// 它的值是在方法<b>里面</b>算出来的（没塞进去的那部分增产点数），
        /// 不是从调用方喂进来的，所以倒着走永远碰不到它。
        ///
        /// 它和同一个方法里已经加宽的 <c>itemInc</c> 是同一个量纲，留成字节就是个新瓶颈。
        /// <b>这一条是「货物进出链上不许有 Byte 参数」那项校验抓出来的</b>，
        /// 不是想出来的——而那项校验的价值正在于它不跟改写器共用判据。
        /// </summary>
        private static readonly string[] ExtraParams =
        {
            "PlanetFactory::InsertInto::remainInc",

            // 拆传送带时货物的暂存路径。AddTempCargo 的 stack 已经加宽了，
            // 但它转手就 newobj ItemPackage::.ctor(Byte, Int16, Int32)——
            // 构造函数第一个参数还是字节，5000 层进去出来是 136，**货就这么没了**。
            // 传播推不到它：构造函数不是「已加宽方法」，得点名。
            "ItemPackage::.ctor::_stack",

            // 同一个形状，只影响显示：CargoView.stack 字段本来就是 Int32，
            // 但构造函数参数是字节，Cargo.GetCargoView 传进去就被截了。
            // 传送带窗口点「反向」时读的就是它。
            "CargoView::.ctor::_stack"
        };

        private static readonly string[] ExtraTargets =
        {
            "InserterComponent::stackInput",
            "InserterComponent::stackOutput",

            // ItemPackage 是拆传送带时的暂存格式（CargoContainer.tmpCargos）。
            // 它的 stack 是字节，而暂存的是传送带上一整堆货。
            // **不进存档**（Export 里一次都没引用 tmpCargos），所以加宽它没有存档代价。
            "ItemPackage::stack"
        };

        /// <summary>
        /// <b>先只分析，确认全看得懂了，再动手。</b>
        /// 这个方法会改写整个 Assembly-CSharp，而它是边走边改的——
        /// 走到一半才发现某个调用点形状没见过，那时已经改了一半，收不回来了。
        /// 交出半改的程序集 = CLR 类型加载失败，而且报错的位置和真正的原因毫不相干。
        /// 所以：第一趟只读不写，Blockers 非空就原样返回，什么都没动。
        /// </summary>
        internal static Report Apply(ModuleDefinition module)
        {
            Report check = Run(module, false);

            if (check.Blockers.Count > 0) return check;

            return Run(module, true);
        }

        private static Report Run(ModuleDefinition module, bool mutate)
        {
            var r = new Report();

            TypeDefinition cargo = module.GetType(CargoType);

            if (cargo == null)
            {
                r.Blockers.Add("找不到 Cargo 类型");

                return r;
            }

            var seeds = new List<FieldDefinition>();

            foreach (string want in Watched)
            {
                FieldDefinition f = cargo.Fields.FirstOrDefault(x => x.Name == want && !x.IsStatic);

                if (f == null) { r.Blockers.Add($"找不到 Cargo.{want} 字段"); return r; }

                if (f.FieldType.MetadataType == MetadataType.Int16)
                {
                    r.Notes.Add($"Cargo.{want} 已经是 Int16，跳过（重复改写无害，但不该发生）");

                    return r;
                }

                if (f.FieldType.MetadataType != MetadataType.Byte)
                {
                    r.Blockers.Add($"Cargo.{want} 的类型是 {f.FieldType.FullName}，不是预期的 Byte——" +
                                   "游戏版本可能变了，放弃改写");

                    return r;
                }

                seeds.Add(f);
            }

            foreach (string spec in ExtraTargets)
            {
                string[] parts = spec.Split(new[] { "::" }, StringSplitOptions.None);

                TypeDefinition owner = module.GetType(parts[0]);

                FieldDefinition f = owner?.Fields.FirstOrDefault(x => x.Name == parts[1] && !x.IsStatic);

                if (f == null) { r.Blockers.Add($"找不到 {spec}"); return r; }

                if (f.FieldType.MetadataType == MetadataType.Int16) continue;

                if (f.FieldType.MetadataType != MetadataType.Byte)
                {
                    r.Blockers.Add($"{spec} 的类型是 {f.FieldType.FullName}，不是预期的 Byte");

                    return r;
                }

                seeds.Add(f);
            }

            FieldDefinition inc = seeds[0];

            TypeReference int16 = module.TypeSystem.Int16;

            // 把「接住 Cargo.inc 的字节字段」一起找出来。
            //
            // 光加宽 Cargo.inc 是不够的：自动集装机把整堆点数暂存在自己的
            // PilerComponent.cacheCargoInc1/2 里，那两个是 Byte——不一起加宽的话，
            // 点数在「堆起来」这一步就被截掉了，而且它们还进存档。
            //
            // 不写死字段名：按「赋值表达式里出现过 ldfld Cargo::inc」发现。
            // 游戏版本一变，新出现的缓存字段会自己被括进来，
            // 而不是静默地漏掉。
            var fields = new Dictionary<string, FieldDefinition>();

            foreach (FieldDefinition f in seeds) fields[Key(f)] = f;

            DiscoverIncFields(module, fields, r);

            // ── 1. 先把要加宽的方法认全，**还不动手** ──────────────
            //
            // 认的依据是「参数名以 inc 开头 + 类型是 Byte 或 Byte&」。
            // split_inc 的那个字节重载参数不叫 inc，单独点名。
            var widened = new Dictionary<MethodDefinition, List<int>>();

            foreach (TypeDefinition t in AllTypes(module))
            foreach (MethodDefinition m in t.Methods)
            {
                List<int> idx = null;

                for (var i = 0; i < m.Parameters.Count; i++)
                {
                    ParameterDefinition p = m.Parameters[i];

                    if (!IsByteOrByteRef(p.ParameterType)) continue;
                    if (!Watched.Any(w => p.Name.StartsWith(w, StringComparison.Ordinal))) continue;

                    (idx ?? (idx = new List<int>())).Add(i);
                }

                if (idx != null) widened[m] = idx;
            }

            AddSplitInc(module, widened, r);

            if (r.Blockers.Count > 0) return r;

            AddExtraParams(module, widened, r);

            if (r.Blockers.Count > 0) return r;

            Propagate(module, widened, r);

            // ── 2. 动手：字段 + 签名 ──────────────────────────────
            foreach (FieldDefinition f in fields.Values)
            {
                if (mutate) f.FieldType = int16;

                if (!seeds.Contains(f)) r.ExtraFields.Add(f.FullName);
            }

            foreach (KeyValuePair<MethodDefinition, List<int>> kv in widened)
            foreach (int i in kv.Value)
            {
                ParameterDefinition p = kv.Key.Parameters[i];

                if (mutate)
                    p.ParameterType = p.ParameterType.IsByReference
                        ? (TypeReference)new ByReferenceType(int16)
                        : int16;

                r.WidenedParams++;
            }

            // ── 3. 全模块扫一遍，把跟着变的 IL 一起修 ──────────────
            foreach (TypeDefinition t in AllTypes(module))
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;

                FixBody(m, fields, widened, int16, r, mutate);
            }

            SerializationFixer.Apply(module, fields.Values, r, mutate);

            r.Applied = r.Blockers.Count == 0;

            return r;
        }

        /// <summary>
        /// 顺着<b>数据流</b>把加宽范围推到不动点。
        ///
        /// <b>为什么非要这一步：按参数名选是错的。</b>
        /// 种子集是「参数名以 inc / stack 开头」，可是同一条链上游叫的是别的名字：
        /// <code>
        ///   PlanetFactory.InsertInto(…, Byte itemCount, Byte itemInc, Byte&amp; remainInc)
        ///   CargoTraffic.TryInsertItem(…, Byte itemCount, Byte itemInc)
        ///   CargoTraffic.PutItemOnBelt(…, Byte itemInc)
        /// </code>
        /// 一个都不以 stack / inc 开头，于是全部漏掉——而它们正是采矿机 / 抽水站
        /// 往传送带吐货的那条路。实测表现：集装配到 5000，抽水站吐出来的还是一个字节装得下的量。
        /// <b>而且校验脚本用的是同一条名字规则，所以它也跟着报了「全过」。</b>
        ///
        /// 正确的判据是<b>值流到哪里</b>：一个字节参数如果被当作实参传给了
        /// 另一个已加宽的字节参数，它自己也必须加宽。反复传播直到不再扩大，
        /// 名字就只是个起点，不再是判据。
        /// </summary>
        private static void Propagate(ModuleDefinition module,
                                      IDictionary<MethodDefinition, List<int>> widened, Report r)
        {
            var changed = true;
            var rounds = 0;

            while (changed && rounds++ < 16)
            {
                changed = false;

                foreach (TypeDefinition t in AllTypes(module))
                foreach (MethodDefinition m in t.Methods)
                {
                    if (!m.HasBody) continue;

                    var code = m.Body.Instructions;

                    for (var i = 0; i < code.Count; i++)
                    {
                        if (code[i].OpCode != OpCodes.Call && code[i].OpCode != OpCodes.Callvirt) continue;

                        MethodDefinition target = ResolveTarget(code[i]);

                        if (target == null || !widened.TryGetValue(target, out List<int> idx)) continue;

                        int[] starts = ArgStarts(code, i, target.Parameters.Count);

                        if (starts == null) continue;

                        foreach (int a in idx)
                        {
                            int lastIdx = a + 1 < starts.Length ? starts[a + 1] - 1 : i - 1;

                            if (lastIdx < 0 || lastIdx < starts[a]) continue;

                            ParameterDefinition pd = ArgOf(m, code[lastIdx]);

                            if (pd == null || !IsByteOrByteRef(pd.ParameterType)) continue;

                            if (!widened.TryGetValue(m, out List<int> mine))
                            {
                                mine = new List<int>();
                                widened[m] = mine;
                            }

                            if (mine.Contains(pd.Index)) continue;

                            mine.Add(pd.Index);

                            r.Propagated.Add($"{m.DeclaringType.Name}.{m.Name} 第 {pd.Index} 参数 {pd.Name}");

                            changed = true;
                        }
                    }
                }
            }

            if (rounds >= 16) r.Blockers.Add("数据流传播 16 轮还没收敛，形状不对劲");
        }

        /// <summary>把 <see cref="ExtraParams"/> 点名的参数加进名单（所有同名重载都算）。</summary>
        private static void AddExtraParams(ModuleDefinition module,
                                           IDictionary<MethodDefinition, List<int>> widened, Report r)
        {
            foreach (string spec in ExtraParams)
            {
                string[] parts = spec.Split(new[] { "::" }, StringSplitOptions.None);

                TypeDefinition owner = module.GetType(parts[0]);

                if (owner == null) { r.Blockers.Add($"找不到类型 {parts[0]}（{spec}）"); continue; }

                var hit = 0;

                foreach (MethodDefinition m in owner.Methods)
                {
                    if (m.Name != parts[1]) continue;

                    for (var i = 0; i < m.Parameters.Count; i++)
                    {
                        if (m.Parameters[i].Name != parts[2]) continue;
                        if (!IsByteOrByteRef(m.Parameters[i].ParameterType)) continue;

                        if (!widened.TryGetValue(m, out List<int> idx))
                        {
                            idx = new List<int>();
                            widened[m] = idx;
                        }

                        if (!idx.Contains(i)) idx.Add(i);

                        hit++;
                    }
                }

                if (hit == 0) r.Blockers.Add($"{spec} 一个重载都没匹配上，签名可能变了");
            }
        }

        private static MethodDefinition ResolveTarget(Instruction ins)
        {
            if (ins.Operand is MethodDefinition md) return md;

            if (!(ins.Operand is MethodReference mr)) return null;

            try { return mr.Resolve(); } catch { return null; }
        }

        /// <summary>
        /// <c>StorageComponent.split_inc(Byte&amp;, Byte&amp;, Byte)</c>：货物进储物格时把
        /// 「整堆点数」按件数拆开的那个函数，参数名不叫 inc，所以点名加宽。
        /// <b>Int32 的那个同名重载绝对不能碰</b>——那个操作的是储物格自己的 Int32 账，本来就够宽。
        /// </summary>
        private static void AddSplitInc(ModuleDefinition module,
                                        IDictionary<MethodDefinition, List<int>> widened,
                                        Report r)
        {
            TypeDefinition storage = module.GetType("StorageComponent");

            if (storage == null)
            {
                r.Blockers.Add("找不到 StorageComponent");

                return;
            }

            MethodDefinition[] all = storage.Methods.Where(x => x.Name == "split_inc").ToArray();

            MethodDefinition b = all.FirstOrDefault(x => x.Parameters.Count == 3 &&
                                                         x.Parameters.All(p => IsByteOrByteRef(p.ParameterType)));

            if (b == null)
            {
                r.Blockers.Add($"找不到 split_inc 的全字节重载（共找到 {all.Length} 个同名方法）");

                return;
            }

            // 三个参数全是字节：count& / inc& / num，全部加宽
            widened[b] = Enumerable.Range(0, b.Parameters.Count).ToList();

            r.Notes.Add("split_inc 的字节重载已列入加宽（Int32 重载保持不动）");
        }

        private static bool IsByteOrByteRef(TypeReference t) =>
            t.MetadataType == MetadataType.Byte ||
            (t.IsByReference && ((ByReferenceType)t).ElementType.MetadataType == MetadataType.Byte);

        // ── 方法体修补 ────────────────────────────────────────────

        private static void FixBody(MethodDefinition m, IDictionary<string, FieldDefinition> fields,
                                    IDictionary<MethodDefinition, List<int>> widened,
                                    TypeReference int16, Report r, bool mutate)
        {
            var code = m.Body.Instructions;

            FixFeederLocals(m, code, fields, widened, int16, r, mutate);

            for (var i = 0; i < code.Count; i++)
            {
                Instruction ins = code[i];

                // 3a. 直接存字段：前面那条 conv.u1 会把 1020 截成字节，必须改成 conv.i2
                if (ins.OpCode == OpCodes.Stfld && IsWidened(ins.Operand, fields))
                {
                    Instruction prev = i > 0 ? code[i - 1] : null;

                    if (prev != null && prev.OpCode == OpCodes.Conv_U1)
                    {
                        if (mutate) prev.OpCode = OpCodes.Conv_I2;

                        r.FixedConv++;
                    }
                }

                // 3b. 取字段地址：后面跟着的 ldind/stind 是按字节宽度算的
                if (ins.OpCode == OpCodes.Ldflda && IsWidened(ins.Operand, fields))
                    FixIndirectRun(code, i, r, mutate);

                // 3c. 通过加宽后的 byref 参数写值
                if (ins.OpCode == OpCodes.Stind_I1 && StoresThroughWidenedParam(m, code, i, widened))
                {
                    if (mutate) ins.OpCode = OpCodes.Stind_I2;

                    r.FixedIndirect++;
                }

                // 3d. 调用加宽后的方法：out/ref 实参的那个局部变量也得跟着加宽
                if (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt)
                    FixCallSite(m, code, i, widened, int16, r, mutate);
            }

            SweepTruncations(m, code, r, mutate);
        }

        /// <summary>
        /// 收尾一遍：<b>截断到一个已经加宽的落点上，一律是错的。</b>
        ///
        /// 前面那几条规则都是按「怎么走到那里」分情况的：存字段、实参末尾、
        /// 嗂给字段的局部变量。每多一种形状就要多一条规则，而漏掉的那一种不报错、
        /// 只您默截掉高位——自动集装机和分拣器就是这么坏的。
        ///
        /// 所以最后统一按<b>不变式</b>扫一遍，不再关心值是怎么流过来的：
        /// 一条 <c>conv.u1</c> 只有在落点<b>确实还是一个字节</b>时才合法。
        /// 落点已经是 Int16，那就是把值截在了进门的路上。
        /// （合法的例子仍然有：<c>PilerComponent.cacheCdTick</c> 本来就是 Byte。）
        ///
        /// 这条和校验脚本第五项断言的是<b>同一个不变式</b>——但两边是独立实现的，
        /// 一个在改写时执行、一个在改完重读时校验。
        /// </summary>
        private static void SweepTruncations(MethodDefinition m, IList<Instruction> code, Report r, bool mutate)
        {
            for (var i = 0; i + 1 < code.Count; i++)
            {
                if (code[i].OpCode != OpCodes.Conv_U1) continue;

                Instruction n = code[i + 1];

                TypeReference dest = null;

                if ((n.OpCode == OpCodes.Stfld || n.OpCode == OpCodes.Stsfld) && n.Operand is FieldReference f)
                    dest = f.FieldType;
                else
                {
                    VariableDefinition v = VarOf(m, n, false);

                    if (v != null) dest = v.VariableType;
                }

                if (dest == null || dest.MetadataType != MetadataType.Int16) continue;

                if (mutate) code[i].OpCode = OpCodes.Conv_I2;

                r.FixedConv++;
            }
        }

        /// <summary>
        /// 修「先截成字节存进局部变量、稍后才写进加宽字段」的那种形状。
        ///
        /// 自动集装机拆堆就是这个样子：
        /// <code>
        ///   ldfld Cargo::stack ; conv.r4 ; ldc.r4 2 ; div ; ldc.r4 0.5 ; add ; conv.u1 ; stloc V_25
        ///   …
        ///   ldloc V_25 ; stfld PilerComponent::cacheCargoStack2      // 字段已经加宽了
        /// </code>
        /// 那条 <c>conv.u1</c> 不紧跟在 <c>stfld</c> 前面，而是隔着一个局部变量，
        /// 所以只看 <c>stfld</c> 前一条的规则看不见它。字段加宽了但值在路上就被截成了一个字节——
        /// 8191 层拆一半会算成 255，<b>而且不报错</b>。
        ///
        /// 做法：先找出那些「最终被写进加宽字段」的局部变量，
        /// 再把它们自己加宽、把存值前那条截断改成二字节。
        /// 这个缺陷是校验脚本拓宽到 stack 之后当场拿住的。
        /// </summary>
        private static void FixFeederLocals(MethodDefinition m, IList<Instruction> code,
                                            IDictionary<string, FieldDefinition> fields,
                                            IDictionary<MethodDefinition, List<int>> widened,
                                            TypeReference int16, Report r, bool mutate)
        {
            var feeders = new HashSet<VariableDefinition>();

            // 来源一：局部变量最后被写进一个已加宽的字段
            for (var i = 1; i < code.Count; i++)
            {
                if (code[i].OpCode != OpCodes.Stfld) continue;
                if (!IsWidened(code[i].Operand, fields)) continue;

                VariableDefinition v = VarOf(m, code[i - 1], true);

                if (v != null) feeders.Add(v);
            }

            // 来源二：局部变量被当作实参传给一个已加宽的传值参数。
            //
            // **这是第三种形状，前两条规则都盖不到。** 自动集装机就栽在这里：
            //   conv.u1 ; stloc V_17   …（隔了好几条）…   AddCargo(item, 层数, V_17)
            // 截断发生在 stloc 前面，而目的地是一个**调用实参**、不是字段，
            // 所以「字段规则」看不见它；实参末尾那条是 ldloc 而不是 conv.u1，
            // 所以「实参末尾规则」也看不见它。
            //
            // 后果：集装层数 5000 时这一格算出来约 20000，被截成 32，
            // 而余数 cacheCargoInc1 = 总量 − 32 越攒越大，最终把 Int16 撑成负数——
            // 玩家看到的就是「集装机吞物品 + 数字变负」。实测报障。
            for (var i = 0; i < code.Count; i++)
            {
                if (code[i].OpCode != OpCodes.Call && code[i].OpCode != OpCodes.Callvirt) continue;

                MethodDefinition target = ResolveTarget(code[i]);

                if (target == null || !widened.TryGetValue(target, out List<int> idx)) continue;

                int[] starts = ArgStarts(code, i, target.Parameters.Count);

                if (starts == null) continue;

                foreach (int a in idx)
                {
                    if (target.Parameters[a].ParameterType.IsByReference) continue;

                    int lastIdx = a + 1 < starts.Length ? starts[a + 1] - 1 : i - 1;

                    if (lastIdx < 0 || lastIdx < starts[a]) continue;

                    VariableDefinition v = VarOf(m, code[lastIdx], true);

                    if (v != null) feeders.Add(v);
                }
            }

            if (feeders.Count == 0) return;

            for (var i = 1; i < code.Count; i++)
            {
                VariableDefinition v = VarOf(m, code[i], false);

                if (v == null || !feeders.Contains(v)) continue;

                if (code[i - 1].OpCode == OpCodes.Conv_U1)
                {
                    if (mutate) code[i - 1].OpCode = OpCodes.Conv_I2;

                    r.FixedConv++;
                }

                if (v.VariableType.MetadataType != MetadataType.Byte) continue;

                if (mutate) v.VariableType = int16;

                r.WidenedLocals++;
            }
        }

        /// <summary>读/写局部变量的指令——短形式（ldloc.0 等）没有操作数，得按编号查。</summary>
        private static VariableDefinition VarOf(MethodDefinition m, Instruction ins, bool load)
        {
            if (ins.Operand is VariableDefinition vd)
            {
                bool isLoad = ins.OpCode == OpCodes.Ldloc || ins.OpCode == OpCodes.Ldloc_S;
                bool isStore = ins.OpCode == OpCodes.Stloc || ins.OpCode == OpCodes.Stloc_S;

                return load ? (isLoad ? vd : null) : (isStore ? vd : null);
            }

            int idx = -1;

            if (load)
            {
                if (ins.OpCode == OpCodes.Ldloc_0) idx = 0;
                else if (ins.OpCode == OpCodes.Ldloc_1) idx = 1;
                else if (ins.OpCode == OpCodes.Ldloc_2) idx = 2;
                else if (ins.OpCode == OpCodes.Ldloc_3) idx = 3;
            }
            else
            {
                if (ins.OpCode == OpCodes.Stloc_0) idx = 0;
                else if (ins.OpCode == OpCodes.Stloc_1) idx = 1;
                else if (ins.OpCode == OpCodes.Stloc_2) idx = 2;
                else if (ins.OpCode == OpCodes.Stloc_3) idx = 3;
            }

            if (idx < 0 || idx >= m.Body.Variables.Count) return null;

            return m.Body.Variables[idx];
        }

        private static string Key(FieldReference f) => f.DeclaringType.FullName + "::" + f.Name;

        private static bool IsWidened(object operand, IDictionary<string, FieldDefinition> fields) =>
            operand is FieldReference f && fields.ContainsKey(Key(f));

        /// <summary>
        /// 找出所有「被 Cargo.inc 赋值的 Byte 字段」。
        ///
        /// 形状就是一段表达式：<c>ldfld Cargo::inc … conv.u1 ; stfld 某个 Byte 字段</c>。
        /// 往回扫一小段就够，扫到分支就停——宁可漏报也不误报，
        /// 漏报的那一个会被离线校验脚本拦下来（它查的是结果，不是我们的声明）。
        /// </summary>
        private static void DiscoverIncFields(ModuleDefinition module,
                                              IDictionary<string, FieldDefinition> fields, Report r)
        {
            foreach (TypeDefinition t in AllTypes(module))
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;

                var code = m.Body.Instructions;

                for (var i = 0; i < code.Count; i++)
                {
                    if (code[i].OpCode != OpCodes.Stfld) continue;

                    if (!(code[i].Operand is FieldReference fr)) continue;
                    if (fr.FieldType.MetadataType != MetadataType.Byte) continue;
                    if (fields.ContainsKey(Key(fr))) continue;

                    var fed = false;

                    for (int j = i - 1; j >= 0 && j >= i - 8; j--)
                    {
                        if (code[j].OpCode.FlowControl == FlowControl.Branch ||
                            code[j].OpCode.FlowControl == FlowControl.Cond_Branch) break;

                        if (code[j].OpCode == OpCodes.Ldfld && code[j].Operand is FieldReference src &&
                            src.DeclaringType.Name == CargoType && Watched.Contains(src.Name))
                        {
                            fed = true;

                            break;
                        }
                    }

                    if (!fed) continue;

                    FieldDefinition fd;

                    try { fd = fr.Resolve(); } catch { fd = null; }

                    if (fd == null)
                    {
                        r.Blockers.Add($"字段 {fr.FullName} 接住了 Cargo.inc 但解析不到定义");

                        continue;
                    }

                    fields[Key(fr)] = fd;
                }
            }
        }

        /// <summary>
        /// <c>ldflda inc</c> 之后那一小段间接读写。原版形状是
        /// <c>dup ; ldind.u1 ; … ; add ; conv.u1 ; stind.i1</c>，
        /// 窗口开小一点，只认紧跟着的那几条，免得误伤别的字段。
        /// </summary>
        private static void FixIndirectRun(IList<Instruction> code, int start, Report r, bool mutate)
        {
            for (int j = start + 1; j < code.Count && j <= start + 10; j++)
            {
                Instruction ins = code[j];

                if (ins.OpCode == OpCodes.Ldind_U1) { if (mutate) ins.OpCode = OpCodes.Ldind_I2; r.FixedIndirect++; continue; }
                if (ins.OpCode == OpCodes.Conv_U1) { if (mutate) ins.OpCode = OpCodes.Conv_I2; r.FixedConv++; continue; }

                if (ins.OpCode == OpCodes.Stind_I1)
                {
                    if (mutate) ins.OpCode = OpCodes.Stind_I2;

                    r.FixedIndirect++;

                    return;
                }

                // 又碰到一次取地址，说明上一段已经结束
                if (ins.OpCode == OpCodes.Ldflda || ins.OpCode == OpCodes.Ldfld) return;
            }
        }

        /// <summary>这条 stind.i1 是不是写在某个已加宽的 byref 参数上。</summary>
        private static bool StoresThroughWidenedParam(MethodDefinition m, IList<Instruction> code, int at,
                                                      IDictionary<MethodDefinition, List<int>> widened)
        {
            if (!widened.TryGetValue(m, out List<int> idx)) return false;

            // 往回找最近的 ldarg：out 参数的写入形状是 ldarg.N ; <值> ; stind.i1
            for (int j = at - 1; j >= 0 && j >= at - 12; j--)
            {
                ParameterDefinition p = ArgOf(m, code[j]);

                if (p == null) continue;

                return idx.Contains(p.Index);
            }

            return false;
        }

        private static ParameterDefinition ArgOf(MethodDefinition m, Instruction ins)
        {
            if (ins.Operand is ParameterDefinition pd) return pd;

            int? n = ins.OpCode == OpCodes.Ldarg_0 ? 0
                : ins.OpCode == OpCodes.Ldarg_1 ? 1
                : ins.OpCode == OpCodes.Ldarg_2 ? 2
                : ins.OpCode == OpCodes.Ldarg_3 ? 3
                : (int?)null;

            if (n == null) return null;

            int shift = m.HasThis ? 1 : 0;
            int idx = n.Value - shift;

            return idx >= 0 && idx < m.Parameters.Count ? m.Parameters[idx] : null;
        }

        /// <summary>
        /// 调用一个被加宽的方法时，传给 out/ref 参数的那个东西也得跟着加宽。
        ///
        /// <b>目标方法必须按定义认，不能按「名字 + 参数个数」认。</b>
        /// <c>split_inc</c> 有两个重载，都是 3 个参数、同一个声明类型，只有参数类型不同：
        /// 字节那个是传送带侧的，Int32 那个是储物格自己的账、本来就够宽、<b>绝不能碰</b>。
        /// 第一版按名字加个数匹配，于是把 20 处 Int32 调用误判成待加宽——
        /// 幸好分析阶段不动手，它们全变成了阻塞项而不是一个改坏的程序集。
        /// 这就是 CLAUDE.md 里那条「重载解析失败会让匹配器悄悄改错东西」的同一类错。
        ///
        /// <b>实参边界必须用栈深度倒推，不能「往回扫连续的取地址指令」。</b>
        /// 第二版那么做了，于是撞上 <c>StorageComponent.AddCargo</c>：
        /// <code>
        ///   ldloc.s V_5                      // 第 0 个实参：一个 Byte&amp; 局部变量
        ///   ldarg.1 ; ldflda Cargo::inc      // 第 1 个实参：两条指令
        ///   ldloc.s V_7 ; conv.u1            // 第 2 个实参：两条指令
        ///   call split_inc(Byte&amp;,Byte&amp;,Byte)
        /// </code>
        /// 倒着扫第一条碰上的是 <c>conv.u1</c>，不是取地址指令，直接就断了。
        /// 实参是<b>表达式</b>不是单条指令，所以只能按栈深度切。
        ///
        /// 切出来之后，byref 实参的地址由该段的<b>最后一条</b>指令产生，来源有四种：
        /// 局部变量（加宽它）、byref 局部变量（加宽成 Int16&amp;）、
        /// 本方法的 byref 参数转手（它自己已在名单里）、字段地址（必须是已加宽的 Cargo.inc）。
        /// 认不出来的一律记进 Blockers，<b>绝不猜</b>。
        /// </summary>
        private static void FixCallSite(MethodDefinition m, IList<Instruction> code, int at,
                                        IDictionary<MethodDefinition, List<int>> widened,
                                        TypeReference int16, Report r, bool mutate)
        {
            MethodDefinition target = code[at].Operand as MethodDefinition;

            if (target == null && code[at].Operand is MethodReference mref)
            {
                try { target = mref.Resolve(); } catch { target = null; }
            }

            if (target == null || !widened.TryGetValue(target, out List<int> idx)) return;

            r.CallSites++;

            int[] starts = ArgStarts(code, at, target.Parameters.Count);

            if (starts == null)
            {
                r.Blockers.Add($"{m.FullName} 调用 {target.Name}：栈深度倒推切不出实参边界");

                return;
            }

            foreach (int a in idx)
            {
                int lastIdx = a + 1 < starts.Length ? starts[a + 1] - 1 : at - 1;

                if (lastIdx < 0 || lastIdx < starts[a])
                {
                    r.Blockers.Add($"{m.FullName} 调用 {target.Name} 第 {a} 参数：实参段为空");

                    continue;
                }

                // 传值参数：实参末尾那条 conv.u1 是为了塞进字节参数才加的，
                // 参数加宽了它就是纯截断。采矿机往传送带吐货就卡在这一条上。
                if (!target.Parameters[a].ParameterType.IsByReference)
                {
                    if (code[lastIdx].OpCode == OpCodes.Conv_U1)
                    {
                        if (mutate) code[lastIdx].OpCode = OpCodes.Conv_I2;

                        r.FixedConv++;
                    }

                    continue;
                }

                Classify(m, code[lastIdx], target, a, widened, int16, r, mutate);
            }
        }

        private static void Classify(MethodDefinition m, Instruction a, MethodDefinition target, int argIndex,
                                     IDictionary<MethodDefinition, List<int>> widened,
                                     TypeReference int16, Report r, bool mutate)
        {
            // 1) 局部变量的地址（ldloca）或一个本身就是 byref 的局部变量（ldloc）
            if (a.Operand is VariableDefinition v)
            {
                bool byRef = v.VariableType.IsByReference;

                TypeReference elem = byRef ? ((ByReferenceType)v.VariableType).ElementType : v.VariableType;

                if (elem.MetadataType == MetadataType.Byte)
                {
                    if (mutate)
                        v.VariableType = byRef ? (TypeReference)new ByReferenceType(int16) : int16;

                    r.WidenedLocals++;
                }
                else if (elem.MetadataType != MetadataType.Int16)
                {
                    r.Blockers.Add($"{m.FullName} 调用 {target.Name} 第 {argIndex} 参数：" +
                                   $"局部变量类型是 {v.VariableType.FullName}，既不是 Byte 也不是 Int16");
                }

                return;
            }

            // 2) 本方法的 byref 参数直接转手：它自己必须也在加宽名单里
            ParameterDefinition pd = ArgOf(m, a);

            if (pd != null)
            {
                bool ok = widened.TryGetValue(m, out List<int> mine) && mine.Contains(pd.Index);

                if (!ok)
                    r.Blockers.Add($"{m.FullName} 调用 {target.Name} 第 {argIndex} 参数：" +
                                   $"转手的是本方法的参数 {pd.Name}，但它不在加宽名单里");

                return;
            }

            // 3) 字段地址：必须是已加宽的那个字段
            if (a.OpCode == OpCodes.Ldflda || a.OpCode == OpCodes.Ldsflda)
            {
                var fr = a.Operand as FieldReference;
                bool ok = fr != null && fr.DeclaringType.Name == CargoType && Watched.Contains(fr.Name);

                if (!ok)
                    r.Blockers.Add($"{m.FullName} 调用 {target.Name} 第 {argIndex} 参数：" +
                                   $"传的是字段 {fr?.FullName} 的地址，那个字段没有被加宽");

                return;
            }

            r.Blockers.Add($"{m.FullName} 调用 {target.Name} 第 {argIndex} 参数：" +
                           $"地址来源是 {a.OpCode.Name}，形状没见过");
        }

        /// <summary>
        /// 按栈深度倒推每个实参的起始指令下标。倒着走，累计净栈效应，
        /// 每次累计到 +1 就说明刚好凑齐一个完整的值——那条就是这个实参的开头。
        /// 切不干净（走到方法开头还没凑齐）就返回 null，让调用方记阻塞项。
        /// </summary>
        private static int[] ArgStarts(IList<Instruction> code, int at, int argCount)
        {
            if (argCount == 0) return new int[0];

            var starts = new int[argCount];

            int j = at - 1;

            for (int a = argCount - 1; a >= 0; a--)
            {
                var depth = 0;
                var found = false;

                while (j >= 0)
                {
                    depth += PushCount(code[j]) - PopCount(code[j]);

                    if (depth == 1) { starts[a] = j; j--; found = true; break; }

                    j--;
                }

                if (!found) return null;
            }

            return starts;
        }

        private static int PopCount(Instruction ins)
        {
            switch (ins.OpCode.StackBehaviourPop)
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
                    if (ins.Operand is MethodReference mr)
                    {
                        int n = mr.Parameters.Count;

                        if (mr.HasThis && ins.OpCode != OpCodes.Newobj) n++;

                        return n;
                    }

                    return 0;

                default: return 0;
            }
        }

        private static int PushCount(Instruction ins)
        {
            switch (ins.OpCode.StackBehaviourPush)
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
                    if (ins.Operand is MethodReference mr)
                        return mr.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;

                    return 1;

                default: return 0;
            }
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            foreach (TypeDefinition t in module.Types)
            foreach (TypeDefinition x in WithNested(t))
                yield return x;
        }

        private static IEnumerable<TypeDefinition> WithNested(TypeDefinition t)
        {
            yield return t;

            foreach (TypeDefinition n in t.NestedTypes)
            foreach (TypeDefinition x in WithNested(n))
                yield return x;
        }
    }
}
