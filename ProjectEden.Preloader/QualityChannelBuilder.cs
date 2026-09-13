using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace ProjectEden.Preloader
{
    /// <summary>
    /// 物品品质 · 阶段 1b：在 <c>Assembly-CSharp</c> 里合成一个<b>线程静态寄存器组</b>，
    /// 让品质点数能跨方法边界传递而<b>不动任何签名</b>。
    ///
    /// <b>为什么不是加参数。</b> 前一版 1b 给 90 个方法追加了品质尾参，实测之后被推翻：
    /// profile 里 UXAssist 有 12 处、InstantDelivery 有 2 处直接调用那些方法，
    /// 它们都是按原版签名编译的，运行时会抛 <c>MissingMethodException</c>；
    /// 而 InstantDelivery 还是本 mod manifest 里声明的依赖，被打掉的正是它的全部功能。
    /// 改别人的 mod 做不到，合成旧签名转发方法又会让按名字打补丁的 Harmony patch
    /// 抛 <c>AmbiguousMatchException</c>（那会当场带走整个 mod）。所以：签名一个都不动。
    /// 完整推导在 <c>物品品质.md</c>。
    ///
    /// <b>这一刀非常小，而那是设计变更的诚实后果。</b> 侧信道没有签名要改、没有调用点要补——
    /// 读写寄存器的那些指令本来就是 1c 访问孪生的一部分（<c>inc</c> 跨边界的地方，
    /// 品质就写/读一次寄存器）。所以 1b 只剩「把寄存器造出来」。
    ///
    /// <b>纪律（1c 必须遵守，否则侧信道会静默错）：</b>
    /// <list type="number">
    /// <item><b>入参方向</b>：调用方在 <c>call</c> 指令<b>正前方</b>写寄存器。
    ///       实参那时已经在栈上了，所以求值过程不可能再覆盖它。</item>
    /// <item><b>出参方向</b>：被调方在<b>每一条 <c>ret</c> 之前</b>写寄存器，
    ///       调用方在 <c>call</c> <b>正后方</b>读。两者之间只隔一次返回。</item>
    /// <item><b>被调方一进来就把入参寄存器读进局部变量</b>，一出去才写出参寄存器——
    ///       中间它自己调别人会覆盖寄存器，这是唯一真实的窟窿，靠这条纪律封住。</item>
    /// </list>
    ///
    /// <b>为什么是 ThreadStatic 而不是普通 static。</b> DSP 的工厂 tick 跑在约 31 个工作线程上
    /// （<c>_assembler_parallel</c> / <c>PlanetTransport.GameTick</c> 等），普通静态会被并发撕碎。
    /// 一条调用链永远在同一个线程内，所以线程静态既够用又无锁。
    ///
    /// <b>为什么是四个独立字段而不是一个数组。</b> 数组要判空、要分配，而
    /// <c>[ThreadStatic]</c> 的初始化<b>只在第一个线程上跑</b>（C# 里的经典陷阱），
    /// 其余线程拿到 null。四个 int 默认就是 0，没有初始化、没有分配、没有边界检查——
    /// 这是 tick 路径，本仓库的规矩是「绝不在 tick 路径上分配」。
    /// 四个是实测出来的余量：单个方法最多 2 个载荷参数位（<c>DeliveryPackage.AddItem</c>）。
    /// </summary>
    internal static class QualityChannelBuilder
    {
        /// <summary>合成类型的名字。放在全局命名空间，IL 引用最短。</summary>
        internal const string TypeName = "ProjectEdenQualityChannel";

        /// <summary>寄存器个数。实测单方法最多 2 个槽位，取 4 留一倍余量。</summary>
        internal const int RegisterCount = 4;

        internal class Report
        {
            internal bool Applied;

            internal readonly List<string> Blockers = new List<string>();
            internal readonly List<string> Notes = new List<string>();

            internal int Registers;
            internal bool AlreadyThere;
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

            // 槽位数必须装得下，否则同一次调用里两个品质会互相覆盖——而那是静默的
            QualityFieldAnalyzer.Report a = QualityFieldAnalyzer.Analyze(module);

            if (a.MaxParamSlots > RegisterCount)
            {
                r.Blockers.Add(
                    $"有方法带 {a.MaxParamSlots} 个载荷参数位（{a.MaxParamSlotsAt}），" +
                    $"超过寄存器数 {RegisterCount}——跨边界传递时会互相覆盖。把 RegisterCount 调大");

                return r;
            }

            TypeDefinition exist = module.GetType(TypeName);

            if (exist != null)
            {
                r.AlreadyThere = true;
                r.Registers = exist.Fields.Count(f => f.IsStatic);
                r.Applied = true;

                r.Notes.Add($"品质侧信道：{TypeName} 已存在（{r.Registers} 个寄存器），跳过");

                return r;
            }

            MethodReference threadStaticCtor = ResolveThreadStaticCtor(module, r);

            if (threadStaticCtor == null) return r;

            if (mutate)
            {
                var type = new TypeDefinition(
                    string.Empty, TypeName,
                    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed |
                    TypeAttributes.BeforeFieldInit,
                    module.TypeSystem.Object);

                for (var i = 0; i < RegisterCount; i++)
                {
                    var f = new FieldDefinition(
                        "Q" + i,
                        FieldAttributes.Public | FieldAttributes.Static,
                        module.TypeSystem.Int32);

                    // 没有字段初始化器，所以不会踩「ThreadStatic 初始化只跑一次」那个坑
                    f.CustomAttributes.Add(new CustomAttribute(threadStaticCtor));

                    type.Fields.Add(f);
                }

                module.Types.Add(type);
            }

            r.Registers = RegisterCount;
            r.Applied = true;

            r.Notes.Add(
                $"品质侧信道：已合成 {TypeName}，{RegisterCount} 个 [ThreadStatic] Int32 寄存器" +
                $"（实测单方法最多 {a.MaxParamSlots} 个槽位）。" +
                "签名一个都没动，其它 mod 不受影响。");

            return r;
        }

        /// <summary>
        /// 拿到 <c>System.ThreadStaticAttribute</c> 的构造函数引用。
        ///
        /// <b>手工构造而不是 <c>ImportReference(typeof(ThreadStaticAttribute))</c>。</b>
        /// 后者会把 patcher 自己那套 .NET Framework 的 mscorlib 身份带进来，
        /// 而游戏跑在 Mono 上、用的是它自己的 mscorlib——引用对不上时不会在写盘时报错，
        /// 要等 CLR 解析那个特性时才炸，而那是「指不到你代码」的那类错误。
        /// 这里改成从目标模块<b>自己的</b>程序集引用表里找 mscorlib，照它的身份建引用。
        /// </summary>
        private static MethodReference ResolveThreadStaticCtor(ModuleDefinition module, Report r)
        {
            AssemblyNameReference corlib =
                module.AssemblyReferences.FirstOrDefault(x => x.Name == "mscorlib")
                ?? module.AssemblyReferences.FirstOrDefault(x => x.Name == "System.Runtime")
                ?? module.AssemblyReferences.FirstOrDefault(x => x.Name == "netstandard");

            if (corlib == null)
            {
                r.Blockers.Add("在目标模块的程序集引用里找不到 mscorlib / System.Runtime，无法建 ThreadStatic 引用");

                return null;
            }

            var attr = new TypeReference("System", "ThreadStaticAttribute", module, corlib);

            return new MethodReference(".ctor", module.TypeSystem.Void, attr) { HasThis = true };
        }

        /// <summary>
        /// 对<b>写盘之后重新读回来</b>的模块核对：寄存器组在不在、个数对不对、
        /// 是不是真的带着 <c>ThreadStatic</c>。
        ///
        /// 最后一条尤其要核：漏掉那个特性，四个寄存器就变成全局共享的，
        /// 而 DSP 的工厂 tick 跑在三十多个线程上——表现是品质在高负载下<b>偶尔</b>串味，
        /// 既不崩也不报错，是这套设计里最难查的失败形态。
        /// </summary>
        internal static Report Verify(ModuleDefinition module)
        {
            var r = new Report();

            TypeDefinition t = module.GetType(TypeName);

            if (t == null)
            {
                r.Blockers.Add($"写盘之后找不到 {TypeName}");

                return r;
            }

            foreach (FieldDefinition f in t.Fields)
            {
                if (!f.IsStatic)
                {
                    r.Blockers.Add($"{TypeName}::{f.Name} 不是静态字段");

                    continue;
                }

                if (f.FieldType.MetadataType != MetadataType.Int32)
                {
                    r.Blockers.Add($"{TypeName}::{f.Name} 的类型是 {f.FieldType.Name}，不是 Int32");

                    continue;
                }

                if (!f.CustomAttributes.Any(c => c.AttributeType.Name == "ThreadStaticAttribute"))
                {
                    r.Blockers.Add(
                        $"{TypeName}::{f.Name} 没有 ThreadStatic 特性——" +
                        "三十多个 tick 线程会共用同一个寄存器，品质会在高负载下偶尔串味且不报错");

                    continue;
                }

                r.Registers++;
            }

            if (r.Registers != RegisterCount)
                r.Blockers.Add($"寄存器数是 {r.Registers}，期望 {RegisterCount}");

            r.Applied = r.Blockers.Count == 0;

            return r;
        }
    }
}
