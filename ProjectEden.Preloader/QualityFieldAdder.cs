using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace ProjectEden.Preloader
{
    /// <summary>
    /// 物品品质 · 阶段 1a：<b>只加字段，一条 IL 都不改。</b>
    ///
    /// 设计稿在仓库根目录的 <c>物品品质.md</c>。阶段一整体是「数据层 + 搬运层，效果层留空」，
    /// 这里只做其中最小、最危险的那一刀：给 30 个载荷字段各加一个 Int32 孪生。
    ///
    /// <b>为什么单独切这一刀。</b> 把参数和访问的改写排在后面，是因为它们会改方法体，
    /// 而改方法体在这个 preloader 上已经炸过两次，两次都只有进游戏才看得见
    /// （短分支被撑长后位移截断、零件数上传仍校验 stride）。只加字段是纯元数据改动：
    /// <b>没有分支截断的可能，没有栈形状的可能</b>，剩下唯一的未知数是结构体布局——
    /// <c>Cargo</c> 会从 36 字节长到 40。而那一条运行时是安全的，已核实：
    /// <c>CargoWidening.UploadRepacked</c> 的 stride 和全部偏移都走
    /// <c>Marshal.SizeOf</c> / <c>Marshal.OffsetOf</c>，当初就是为这种情况写的。
    ///
    /// <b>这一步之后品质恒为 0（数组字段恒为 null），游戏行为零变化。</b>
    /// 所以存档读写也不在这里改——没有东西可存。等 1c 真的有代码去写这些字段时，
    /// 再连同数组分配和版本分支一起做。
    ///
    /// <b>分析不通过就一个字段都不加。</b> <see cref="QualityFieldAnalyzer"/> 是闸：
    /// 它管着「清单和程序集对不对得上」「有没有冒出未声明的混合方法」，
    /// 那些问题在加字段这一步看不出来，但会让后面两刀踩空。
    /// </summary>
    internal static class QualityFieldAdder
    {
        internal class Report
        {
            internal bool Applied;

            internal readonly List<string> Blockers = new List<string>();
            internal readonly List<string> Notes = new List<string>();

            /// <summary>新增的孪生字段：<c>类型::原名 → 新名 (类型)</c></summary>
            internal readonly List<string> Added = new List<string>();

            /// <summary>已经存在的（重复执行时）</summary>
            internal int AlreadyThere;
        }

        /// <summary>
        /// 期望新增的孪生字段数。
        ///
        /// <b>它是算出来的，不是写死的。</b> 原先是个常量 29，而它的唯一职责是
        /// 「清单和程序集对不对得上」——于是<b>每次往清单里加一个载荷，它就把 1a 挡住</b>，
        /// 而报错看上去像「新加的字段有问题」。实际上字段建得好好的，
        /// 只是数字没跟着改——这是本仓库反复记过的「同一事实的第二份手维护拷贝」。
        ///
        /// 现在直接数清单：<b>就是声明的载荷总数，不减任何东西。</b>
        /// GPU 那三个（<c>TrashObject</c> / <c>DroneData</c> / <c>CourierData</c>，加字段会让
        /// <c>ComputeBuffer</c> 的 stride 和游戏里写死的那个对不上，实测启动即崩）
        /// <b>本来就不在 DeclaredPayload 里</b>——它们是并列的另一张表，不是这张表的子集。
        /// 第一版把两者相减，于是 30 条清单期望出 27 个字段，1a 被自己的断言挡死。
        /// 理由和后续路线见 <c>QualityFieldAnalyzer.GpuUploaded</c>。
        ///
        /// 断言本身保留：它拦的是「游戏更新把某个字段改名了」，那时清单长度不变而
        /// 实际建出来的会少一个——那才是真问题。
        /// </summary>
        internal static int ExpectedFields => QualityFieldAnalyzer.DeclaredPayloadCount
                                              + QualityFieldAnalyzer.ExtraFieldCount;

        internal static Report Apply(ModuleDefinition module)
        {
            Report check = Run(module, false);

            if (check.Blockers.Count > 0) return check;

            return Run(module, true);
        }

        private static Report Run(ModuleDefinition module, bool mutate)
        {
            var r = new Report();

            // ── 闸：分析必须先过 ──
            QualityFieldAnalyzer.Report a = QualityFieldAnalyzer.Analyze(module);

            if (!a.Clean)
            {
                r.Blockers.Add($"分析未通过（{a.Blockers.Count} 条），不加任何字段");

                foreach (string b in a.Blockers) r.Blockers.Add("  " + b);

                return r;
            }

            var payload = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);

            QualityFieldAnalyzer.ResolvePayload(module, payload, r.Blockers);

            if (r.Blockers.Count > 0) return r;

            TypeReference int32 = module.TypeSystem.Int32;

            foreach (FieldDefinition src in payload.Values.OrderBy(f => f.FullName, StringComparer.Ordinal))
            {
                string twinName = TwinName(src.Name);

                if (twinName == null)
                {
                    r.Blockers.Add(
                        $"{src.DeclaringType.FullName}::{src.Name} 的名字推不出孪生名——" +
                        "TwinName 只认 inc / _inc / inc* / *Inc 四种形状，加字段前先决定它该叫什么");

                    continue;
                }

                TypeDefinition owner = src.DeclaringType;

                FieldDefinition exist = owner.Fields.FirstOrDefault(f => f.Name == twinName);

                if (exist != null)
                {
                    // 重复执行无害，但不该发生——preloader 一个进程只跑一次
                    r.AlreadyThere++;

                    if (exist.FieldType.FullName != TwinType(src, int32).FullName)
                        r.Blockers.Add(
                            $"{owner.FullName}::{twinName} 已存在但类型是 {exist.FieldType.FullName}，" +
                            "和预期的孪生类型不符");

                    continue;
                }

                TypeReference twinType = TwinType(src, int32);

                if (mutate)
                {
                    var f = new FieldDefinition(twinName, FieldAttributes.Public, twinType);

                    owner.Fields.Add(f);
                }

                r.Added.Add($"{owner.FullName}::{src.Name} → {twinName} ({twinType.Name})");
            }

            // ── 品质独有的新槽位：没有对应的 inc，所以名字和类型都由清单直接给 ──
            foreach (string spec in QualityFieldAnalyzer.ExtraFields)
            {
                string[] half = spec.Split(new[] { "::" }, StringSplitOptions.None);

                if (half.Length != 2) { r.Blockers.Add($"新槽位写法不对：{spec}"); continue; }

                int colon = half[1].LastIndexOf(':');

                if (colon <= 0) { r.Blockers.Add($"新槽位缺类型：{spec}"); continue; }

                string fieldName = half[1].Substring(0, colon);
                string kind = half[1].Substring(colon + 1);

                TypeDefinition owner = module.GetType(half[0]);

                if (owner == null) { r.Blockers.Add($"新槽位找不到类型 {half[0]}（{spec}）"); continue; }

                // 目前只用得到 Int32 和 Int32[]，不搞通用解析——**认不出就报错，不猜**
                TypeReference ft = kind == "Int32" ? int32
                    : kind == "Int32[]" ? new ArrayType(int32)
                    : null;

                if (ft == null) { r.Blockers.Add($"新槽位的类型 {kind} 不认识（{spec}）"); continue; }

                FieldDefinition exist0 = owner.Fields.FirstOrDefault(f => f.Name == fieldName);

                if (exist0 != null)
                {
                    r.AlreadyThere++;

                    if (exist0.FieldType.FullName != ft.FullName)
                        r.Blockers.Add($"{half[0]}::{fieldName} 已存在但类型是 {exist0.FieldType.FullName}");

                    continue;
                }

                if (mutate) owner.Fields.Add(new FieldDefinition(fieldName, FieldAttributes.Public, ft));

                r.Added.Add($"{half[0]}::（新槽位）{fieldName} ({ft.Name})");
            }

            if (r.Blockers.Count > 0) return r;

            // ── 断言：数目对不上就整个不算数 ──
            int total = r.Added.Count + r.AlreadyThere;

            if (total != ExpectedFields)
            {
                r.Blockers.Add(
                    $"孪生字段数是 {total}，期望 {ExpectedFields}——" +
                    "载荷清单和程序集对不上，先跑 tools\\verify_quality.ps1 看分析报告");

                return r;
            }

            r.Notes.Add(
                $"品质孪生字段：新增 {r.Added.Count} 个" +
                (r.AlreadyThere > 0 ? $"，{r.AlreadyThere} 个已存在（重复执行？）" : "") +
                "。此刻它们恒为 0 / null，游戏行为零变化——写它们的代码在 1b / 1c。");

            r.Applied = true;

            return r;
        }

        /// <summary>
        /// 对<b>改写后的模块</b>重新推导一遍：每个载荷字段是不是真的有一个类型正确的孪生。
        ///
        /// <b>这不是把 <see cref="Report.Added"/> 再读一遍。</b> 那样只能证明变换和它自己
        /// 说法一致；这里是拿写盘之后重新读回来的模块，从载荷清单**重新推导**孪生名
        /// 再去找——核对的是结果，不是我自己的contribution。
        /// 校验脚本调它而不是解析报告文本：文本格式一变，校验会「静默通过」。
        /// </summary>
        internal static Report Verify(ModuleDefinition module)
        {
            var r = new Report();

            var payload = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);

            QualityFieldAnalyzer.ResolvePayload(module, payload, r.Blockers);

            if (r.Blockers.Count > 0) return r;

            foreach (FieldDefinition src in payload.Values.OrderBy(f => f.FullName, StringComparer.Ordinal))
            {
                string twinName = TwinName(src.Name);

                if (twinName == null)
                {
                    r.Blockers.Add($"{src.DeclaringType.FullName}::{src.Name} 推不出孪生名");

                    continue;
                }

                FieldDefinition twin =
                    src.DeclaringType.Fields.FirstOrDefault(f => f.Name == twinName && !f.IsStatic);

                if (twin == null)
                {
                    r.Blockers.Add($"{src.DeclaringType.FullName}::{twinName} 写盘之后不见了");

                    continue;
                }

                bool wantArray = src.FieldType is ArrayType;
                bool isArray = twin.FieldType is ArrayType;

                if (wantArray != isArray)
                {
                    r.Blockers.Add(
                        $"{src.DeclaringType.FullName}::{twinName} 的数组性和原字段不一致" +
                        $"（原 {src.FieldType.Name} / 孪生 {twin.FieldType.Name}）");

                    continue;
                }

                TypeReference elem = isArray ? ((ArrayType)twin.FieldType).ElementType : twin.FieldType;

                if (elem.MetadataType != MetadataType.Int32)
                {
                    r.Blockers.Add($"{src.DeclaringType.FullName}::{twinName} 的元素类型是 {elem.Name}，不是 Int32");

                    continue;
                }

                r.Added.Add($"{src.DeclaringType.FullName}::{twinName}");
            }

            // **新槽位也要核。** 漏了这一段，核到的数会比 ExpectedFields 少，
            // 于是 1a 明明干完了、Verify 却报「没就位」，1c 整个不动——
            // 而报错的措辞会把人引向清单，实际是核验少数了一族。
            foreach (string spec in QualityFieldAnalyzer.ExtraFields)
            {
                string[] half = spec.Split(new[] { "::" }, StringSplitOptions.None);

                if (half.Length != 2) { r.Blockers.Add($"新槽位写法不对：{spec}"); continue; }

                int colon = half[1].LastIndexOf(':');
                string fieldName = colon > 0 ? half[1].Substring(0, colon) : half[1];

                TypeDefinition owner = module.GetType(half[0]);
                FieldDefinition got = owner?.Fields.FirstOrDefault(f => f.Name == fieldName && !f.IsStatic);

                if (got == null)
                {
                    r.Blockers.Add($"{half[0]}::{fieldName} 写盘之后不见了（新槽位）");

                    continue;
                }

                r.Added.Add($"{half[0]}::{fieldName}");
            }

            if (r.Added.Count != ExpectedFields)
                r.Blockers.Add($"写盘后核到 {r.Added.Count} 个孪生字段，期望 {ExpectedFields}");

            r.Applied = r.Blockers.Count == 0;

            return r;
        }

        /// <summary>
        /// 孪生字段的类型：标量一律 <b>Int32</b>，数组保持数组。
        ///
        /// <b>为什么不跟着原字段的宽度走。</b> 品质点数 = 件数 × 单件分数，
        /// 物流站单格容量本仓库配到 10,005,000，单件分数上限 100 → 约 1.0e9，
        /// 对 Int32 上限还有 2.1 倍余量；Int16 连一格都装不下。
        /// 原字段是 Byte 还是 Int16 和这个无关——那是增产剂自己的预算。
        /// </summary>
        private static TypeReference TwinType(FieldDefinition src, TypeReference int32) =>
            src.FieldType is ArrayType ? new ArrayType(int32) : int32;

        /// <summary>
        /// 由原字段名推孪生名：把 <c>inc</c> 那个词换成 <c>qua</c>，其余部分原样保留。
        /// 推不出来返回 null —— <b>宁可停下，也不要自动编一个名字</b>，
        /// 名字对不上会让后面两刀找不到该配对的字段，而那是静默的。
        /// </summary>
        internal static string TwinName(string name)
        {
            // **自动属性的后备字段：剥壳再套回去。**
            //
            // <c>Player.inhandItemInc</c>（鼠标手上那一格）在 IL 里叫
            // <c>&lt;inhandItemInc&gt;k__BackingField</c>——下面四条规则一条都不匹配：
            // 它不以 inc 开头、不以 Inc 结尾、后缀也不是数字。
            //
            // 代价是玩家报上来的那个：<b>凡是经过鼠标手的货都掉品质</b>。
            // 50 个 50 分的铜拖到 50 个 0 分的铜上，合并后是 0 而不是 25；
            // 再拖别的堆，那些堆也一样变 0。
            //
            // 这是<b>第四次</b>被名字启发式漏掉（前三次：`_stack`、`itemInc`、
            // `cacheCargoInc1`）——**按名字挑，就会按名字漏**。
            if (name.Length > 2 && name[0] == '<')
            {
                int close = name.IndexOf('>');

                if (close > 1)
                {
                    string inner = TwinName(name.Substring(1, close - 1));

                    if (inner != null) return "<" + inner + ">" + name.Substring(close + 1);
                }

                return null;
            }

            if (name == "inc") return "qua";
            if (name == "_inc") return "_qua";

            // incServed → quaServed
            if (name.StartsWith("inc", StringComparison.Ordinal) && name.Length > 3 && char.IsUpper(name[3]))
                return "qua" + name.Substring(3);

            // itemInc → itemQua、fuelInc → fuelQua、fluidInputInc → fluidInputQua
            if (name.EndsWith("Inc", StringComparison.Ordinal) && name.Length > 3)
                return name.Substring(0, name.Length - 3) + "Qua";

            // cacheCargoInc1 → cacheCargoQua1。**编号后缀是这一族的第三种写法**,
            // 而它躲过了上面两条：既不以 inc 开头，也不以 Inc 结尾。
            // 自动集装机那两个缓存字段就是这么被整套名字启发式漏掉的——
            // 和加宽那一期被 `_stack` / `itemInc` 漏掉是同一个坑：**按名字挑，就会按名字漏**。
            int at = name.LastIndexOf("Inc", StringComparison.Ordinal);

            if (at > 0 && at + 3 < name.Length && AllDigits(name, at + 3))
                return name.Substring(0, at) + "Qua" + name.Substring(at + 3);

            return null;
        }

        private static bool AllDigits(string s, int from)
        {
            for (int i = from; i < s.Length; i++)
                if (!char.IsDigit(s[i]))
                    return false;

            return true;
        }
    }
}
