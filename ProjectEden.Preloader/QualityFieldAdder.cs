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
        /// 期望新增的孪生字段数。和 <see cref="QualityFieldAnalyzer"/> 的清单长度一致。
        ///
        /// 31 → 29：<c>TrashObject</c> / <c>DroneData</c> / <c>CourierData</c> 会被原样上传到
        /// <c>ComputeBuffer</c>，加字段会让 stride 和游戏里写死的那个对不上（实测启动即崩）。
        /// 理由和后续路线见 <c>QualityFieldAnalyzer.GpuUploaded</c>。
        /// </summary>
        internal const int ExpectedFields = 29;

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
