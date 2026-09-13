using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

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

                type.Methods.Add(BuildSplit(module));

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

        /// <summary>合成的按比例拆分函数名。</summary>
        internal const string SplitName = "Split";

        /// <summary>
        /// 合成 <c>static int Split(int countAfter, ref int qua, int p)</c>：
        /// <b>品质点数版的 <c>StorageComponent.split_inc</c></b>。
        ///
        /// <b>它是照抄的，不是新发明的。</b> 原版那一支的数学（IL 000F–003E）是：
        /// <code>
        ///   split_inc(ref int n, ref int m, int p)     // n 件数、m 总点数、p 取走几件
        ///       level = m / n ;  rem = m - level * n
        ///       n -= p
        ///       rem -= n
        ///       ret = rem &gt; 0 ? level * p + rem : level * p
        ///       m -= ret ;  return ret
        /// </code>
        /// 这正是可加量的标准按比例拆分 —— 也就是品质这套设计从头要的那条规则，
        /// 所以品质的孪生就是同一套数学作用在 <c>(件数, 品质点数)</c> 上。
        ///
        /// <b>为什么不能直接再调一次原版的 <c>split_inc</c>。</b>
        /// 它会顺手把件数减掉（<c>n -= p</c>）。同一个调用点调两次，件数会被扣两次，
        /// 而那会<b>凭空销毁物品</b>。所以这里的签名收下的是「<b>已经减过的</b>件数」，
        /// 内部把它加回去还原 <c>n</c>：孪生语句发射在原调用之后，那时件数正好是减过的。
        ///
        /// 除此之外每一步都和原版逐条对应，包括 <c>n &lt;= 0</c> 时把点数清零并返回 0
        /// 这个分支 —— 少一条都会让两边在边界上分叉，而分叉是静默的。
        /// </summary>
        private static MethodDefinition BuildSplit(ModuleDefinition module)
        {
            TypeReference i32 = module.TypeSystem.Int32;

            var m = new MethodDefinition(
                SplitName,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                i32);

            m.Parameters.Add(new ParameterDefinition("countAfter", ParameterAttributes.None, i32));
            m.Parameters.Add(new ParameterDefinition("qua", ParameterAttributes.None, new ByReferenceType(i32)));
            m.Parameters.Add(new ParameterDefinition("p", ParameterAttributes.None, i32));

            var n = new VariableDefinition(i32);
            var level = new VariableDefinition(i32);
            var rem = new VariableDefinition(i32);
            var ret = new VariableDefinition(i32);

            m.Body.Variables.Add(n);
            m.Body.Variables.Add(level);
            m.Body.Variables.Add(rem);
            m.Body.Variables.Add(ret);
            m.Body.InitLocals = true;

            ILProcessor il = m.Body.GetILProcessor();

            // 两个跳转目标先建出来，后面再插进去
            Instruction live = il.Create(OpCodes.Ldarg_1);   // n > 0 的主路
            Instruction skip = il.Create(OpCodes.Ldarg_1);   // rem <= 0 时跳过 ret += rem

            // n = countAfter + p
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Add));
            il.Append(il.Create(OpCodes.Stloc, n));

            // if (n > 0) goto live
            il.Append(il.Create(OpCodes.Ldloc, n));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Bgt, live));

            // *qua = 0 ; return 0
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stind_I4));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ret));

            // live: level = *qua / n
            il.Append(live);                                  // ldarg.1
            il.Append(il.Create(OpCodes.Ldind_I4));
            il.Append(il.Create(OpCodes.Ldloc, n));
            il.Append(il.Create(OpCodes.Div));
            il.Append(il.Create(OpCodes.Stloc, level));

            // rem = *qua - level * n
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldind_I4));
            il.Append(il.Create(OpCodes.Ldloc, level));
            il.Append(il.Create(OpCodes.Ldloc, n));
            il.Append(il.Create(OpCodes.Mul));
            il.Append(il.Create(OpCodes.Sub));
            il.Append(il.Create(OpCodes.Stloc, rem));

            // rem -= countAfter   （原版这里减的是已经扣过 p 的 n）
            il.Append(il.Create(OpCodes.Ldloc, rem));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Sub));
            il.Append(il.Create(OpCodes.Stloc, rem));

            // ret = level * p
            il.Append(il.Create(OpCodes.Ldloc, level));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Mul));
            il.Append(il.Create(OpCodes.Stloc, ret));

            // if (rem <= 0) goto skip
            il.Append(il.Create(OpCodes.Ldloc, rem));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ble, skip));

            // ret += rem
            il.Append(il.Create(OpCodes.Ldloc, ret));
            il.Append(il.Create(OpCodes.Ldloc, rem));
            il.Append(il.Create(OpCodes.Add));
            il.Append(il.Create(OpCodes.Stloc, ret));

            // skip: *qua -= ret ; return ret
            il.Append(skip);                                  // ldarg.1
            il.Append(il.Create(OpCodes.Dup));
            il.Append(il.Create(OpCodes.Ldind_I4));
            il.Append(il.Create(OpCodes.Ldloc, ret));
            il.Append(il.Create(OpCodes.Sub));
            il.Append(il.Create(OpCodes.Stind_I4));
            il.Append(il.Create(OpCodes.Ldloc, ret));
            il.Append(il.Create(OpCodes.Ret));

            return m;
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

            MethodDefinition split = t.Methods.FirstOrDefault(x => x.Name == SplitName);

            if (split == null)
                r.Blockers.Add($"{TypeName}::{SplitName} 写盘之后不见了");
            else if (!split.IsStatic || split.Parameters.Count != 3 ||
                     !split.Parameters[1].ParameterType.IsByReference ||
                     split.ReturnType.MetadataType != MetadataType.Int32)
                r.Blockers.Add(
                    $"{TypeName}::{SplitName} 的签名不对：应为 static int {SplitName}(int, ref int, int)");
            else if (!split.HasBody || split.Body.Instructions.Count < 20)
                r.Blockers.Add($"{TypeName}::{SplitName} 的方法体太短，像是没写完");

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
