using System.Collections.Generic;
using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 读写 preloader 新加的那几个孪生字段。
    ///
    /// <b>这份代码是按没有这些字段的程序集编译的</b>，所以不能直接写 <c>store.qua</c>——
    /// 那条 MemberRef 在编译期就解析不了，和 <see cref="CargoWidening"/> 的委托方案同一个理由。
    ///
    /// <b>不用反射的 <c>FieldInfo.SetValue</c>。</b> 目标是结构体数组里的一个元素
    /// （<c>station.storage[i]</c>），反射那条路要先装箱、改完再拆回去写回数组，
    /// 而这些方法跑在 tick 路径上——本仓库明确禁止在 tick 上分配。
    /// 这里改为在启动时用 <see cref="DynamicMethod"/> 现编一对
    /// <c>ldarg.0 ; ldfld/stfld ; ret</c> 的委托，之后每次访问都是一条指令，零分配。
    ///
    /// 字段不存在时委托是 null，所有调用点都先问 <see cref="Ready"/>。
    /// </summary>
    internal static class QualityAccess
    {
        internal delegate int StoreGet(ref StationStore s);

        internal delegate void StoreSet(ref StationStore s, int v);

        internal static readonly StoreGet GetStationQua = MakeGet();

        internal static readonly StoreSet SetStationQua = MakeSet();

        /// <summary>孪生字段可读可写。<b>探的是结果</b>，不是「我以为 preloader 装了」。</summary>
        internal static bool Ready => GetStationQua != null && SetStationQua != null;

        private static FieldInfo Field() => AccessTools.Field(typeof(StationStore), "qua");

        private static StoreGet MakeGet()
        {
            FieldInfo f = Field();

            if (f == null || f.FieldType != typeof(int)) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_GetStationQua", typeof(int),
                    new[] { typeof(StationStore).MakeByRefType() }, typeof(StationStore), true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, f);
                il.Emit(OpCodes.Ret);

                return (StoreGet)dm.CreateDelegate(typeof(StoreGet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static StoreSet MakeSet()
        {
            FieldInfo f = Field();

            if (f == null || f.FieldType != typeof(int)) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_SetStationQua", null,
                    new[] { typeof(StationStore).MakeByRefType(), typeof(int) },
                    typeof(StationStore), true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, f);
                il.Emit(OpCodes.Ret);

                return (StoreSet)dm.CreateDelegate(typeof(StoreSet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ── 侧信道 ────────────────────────────────────────────

        internal delegate int ChannelGet();

        /// <summary>
        /// preloader 的品质侧信道 0 号寄存器。
        ///
        /// <b>协议是「调用方在调用前写，被调方在体内读」。</b> 改写后的
        /// <c>StationComponent.AddItem</c> 体内是
        /// <c>storage[i].qua += ProjectEdenQualityChannel.Q0</c>（实测 IL 0070~007D）——
        /// 品质没法加进方法签名（那是结构性改动），所以走一个 <c>[ThreadStatic]</c> 寄存器。
        ///
        /// <b>任何 <c>return false</c> 顶掉这类方法的前缀，都必须自己把这一步补上。</b>
        /// 不补有两重后果：这一次的品质丢了（看起来像「品质怎么越搬越少」），
        /// 而寄存器里那个值<b>留在原地</b>，被下一个读它的方法当成自己的——
        /// 那才是难查的那一半，症状是品质在某个不相干的地方凭空变大。
        /// </summary>
        internal static readonly ChannelGet GetChannel0 = MakeChannelGet();

        internal delegate void ChannelClear();

        /// <summary>
        /// 把四个寄存器全部清零。
        ///
        /// <b>这是「本 mod 自己调游戏的搬运方法」之前必须做的一步。</b>
        /// 侧信道的协议是「调用方在调用前写，被调方在体内读」——而这条协议
        /// preloader 只在<b>游戏自己的</b>调用点上接好了。我们的代码直接调
        /// <c>PlanetFactory.InsertInto</c>（实测读 11 次）、<c>StorageComponent.AddItem</c>
        /// （读 3 次）这些方法时，从来没写过寄存器，于是它们消费的是
        /// <b>上一个人留在里面的值</b>——品质就这么凭空长出来。
        ///
        /// 清零的语义是「这一笔我不带品质」。这会让那几条路径上的品质被<b>丢掉</b>，
        /// 是一笔损失；但损失是有界的、可解释的，而凭空增长不是。
        /// 等阶段 3 把那几条路接上真正的品质，再把清零换成赋值。
        ///
        /// <b>调用前后各清一次。</b> 取货类的方法是反过来的（被调方写、调用方读），
        /// 只清前面的话，它写进去的那个值会留给下一个读它的人。
        /// </summary>
        internal static readonly ChannelClear ClearChannel = MakeChannelClear();

        internal static bool ChannelReady => GetChannel0 != null;

        /// <summary>清零器可用。<b>探的是结果</b>，不是「我以为 preloader 装了」。</summary>
        internal static bool ChannelClearable => ClearChannel != null;

        /// <summary>
        /// 从 <paramref name="at"/> 往后<b>跳过 preloader 插进来的品质指令</b>，
        /// 返回第一条不是我们自己插的指令的下标。
        ///
        /// <b>这不是便利函数，是一整类回归的解药。</b> 1c 把品质语句插进了 <b>201 个原版方法体</b>，
        /// 而本 mod 自己有一批转译器是靠「紧挨着的那几条指令」定位的——两者一撞，
        /// 转译器就<b>静默失配</b>：形状还在，只是中间多了几条别人的指令。
        ///
        /// 实测的第一例：<c>PowerGeneratorComponent.GameTick_Gamma</c> 的两个传送带取货口，
        /// <c>PickFrom</c> 之后原本紧跟 <c>ldarg.0 ; ldfld catalystId</c>，
        /// 现在中间多了 <c>ldsfld Q0 ; stloc</c>（接回出参的品质），
        /// 于是活性透镜整条传送带入口喂不进去，而日志里只有一行「应当 7 处、实际 5 处」。
        ///
        /// 只跳<b>认得出是我们自己的</b>两种成对形状，别的一律停下：
        /// <c>ldsfld Q* ; stloc</c>（把品质接回局部）和 <c>ldc.i4.0 ; stsfld Q*</c>（调用后擦除）。
        /// <b>认不出就停——宁可失配，也绝不跳过一条真正的原版指令</b>，
        /// 那会让转译器改到错的位置上，而那是不报错的。
        /// </summary>
        internal static int SkipChannelNoise(IList<CodeInstruction> code, int at)
        {
            Type ch = AccessTools.TypeByName("ProjectEdenQualityChannel");

            if (ch == null || code == null) return at;

            while (at >= 0 && at + 1 < code.Count)
            {
                if (IsChannelField(code[at], OpCodes.Ldsfld, ch) && code[at + 1].IsStloc())
                {
                    at += 2;

                    continue;
                }

                if (IsZeroConst(code[at]) && IsChannelField(code[at + 1], OpCodes.Stsfld, ch))
                {
                    at += 2;

                    continue;
                }

                break;
            }

            return at;
        }

        private static bool IsChannelField(CodeInstruction ins, OpCode want, Type channel) =>
            ins.opcode == want && ins.operand is FieldInfo f
                               && f.DeclaringType == channel && f.IsStatic;

        /// <summary>常数 0 的三种写法都要认——擦除发射的是 <c>ldc.i4.0</c>，但别的编码也合法。</summary>
        private static bool IsZeroConst(CodeInstruction ins)
        {
            if (ins.opcode == OpCodes.Ldc_I4_0) return true;

            if (ins.opcode != OpCodes.Ldc_I4 && ins.opcode != OpCodes.Ldc_I4_S) return false;

            return ins.operand is int i && i == 0 || ins.operand is sbyte s && s == 0;
        }

        private static ChannelGet MakeChannelGet()
        {
            Type t = AccessTools.TypeByName("ProjectEdenQualityChannel");

            FieldInfo f = t != null ? AccessTools.Field(t, "Q0") : null;

            if (f == null || !f.IsStatic || f.FieldType != typeof(int)) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_GetQ0", typeof(int), new Type[0], t, true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldsfld, f);
                il.Emit(OpCodes.Ret);

                return (ChannelGet)dm.CreateDelegate(typeof(ChannelGet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 现编一个把 <c>Q0…Qn</c> 全写 0 的方法。
        ///
        /// <b>寄存器个数是数出来的，不是写死 4。</b> preloader 那边的注释说「4 个是实测出来的
        /// 余量」——余量会变，而这里写死一个数的后果是：将来加到第 5 个寄存器时，
        /// 多出来那个永远不清，漏法和现在一模一样，且没有任何提示。
        /// </summary>
        private static ChannelClear MakeChannelClear()
        {
            Type t = AccessTools.TypeByName("ProjectEdenQualityChannel");

            if (t == null) return null;

            var fields = new List<FieldInfo>();

            for (var i = 0; i < 32; i++)
            {
                FieldInfo f = AccessTools.Field(t, "Q" + i);

                if (f == null || !f.IsStatic || f.FieldType != typeof(int)) break;

                fields.Add(f);
            }

            if (fields.Count == 0) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_ClearChannel", null, new Type[0], t, true);

                ILGenerator il = dm.GetILGenerator();

                foreach (FieldInfo f in fields)
                {
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Stsfld, f);
                }

                il.Emit(OpCodes.Ret);

                Registers = fields.Count;

                return (ChannelClear)dm.CreateDelegate(typeof(ChannelClear));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>数出来的寄存器个数，只用于开机那行状态。</summary>
        internal static int Registers;

        internal delegate void ChannelSet(int v);

        /// <summary>
        /// 往 0 号寄存器写一笔品质，<b>紧接着</b>调游戏的入库方法。
        /// 这是把品质真的送过去，而不是 <see cref="ClearChannel"/> 那样丢掉它。
        /// </summary>
        internal static readonly ChannelSet SetChannel0 = MakeChannelSet();

        private static ChannelSet MakeChannelSet()
        {
            Type t = AccessTools.TypeByName("ProjectEdenQualityChannel");

            FieldInfo f = t != null ? AccessTools.Field(t, "Q0") : null;

            if (f == null || !f.IsStatic || f.FieldType != typeof(int)) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_SetQ0", null, new[] { typeof(int) }, t, true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stsfld, f);
                il.Emit(OpCodes.Ret);

                return (ChannelSet)dm.CreateDelegate(typeof(ChannelSet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ── 手写搬运的两个口子 ────────────────────────────────

        /// <summary>
        /// 从这一格<b>按件数比例</b>取走品质并扣掉，返回取走的那一份。
        ///
        /// <b>必须在 <c>count -= take</c> 之前调</b>：比例要拿扣减前的件数算。
        ///
        /// 这是本仓库所有手写搬运的必修课。只扣件数不扣品质，剩下的货就顶着整格的点数——
        /// 一格 100 件 100 分的铜块拿走 80 件，剩下 20 件却还挂着 10000 点，
        /// <b>单件从 100 跳到 500</b>。实测报上来的 694 分就是这么来的，
        /// 而它和 <c>StationStore.inc</c> 上那条「每次搬运都白送一次增产」是同一个形状。
        /// </summary>
        internal static int TakeStationQua(ref StationStore store, int take)
        {
            if (!Ready || take <= 0) return 0;

            int qua = GetStationQua(ref store);

            if (qua <= 0 || store.count <= 0) return 0;

            long share = (long)qua * take / store.count;

            if (share > qua) share = qua;

            SetStationQua(ref store, qua - (int)share);

            return (int)share;
        }

        internal static void GiveStationQua(ref StationStore store, int qua)
        {
            if (!Ready || qua <= 0) return;

            SetStationQua(ref store, GetStationQua(ref store) + qua);
        }

        // ── 储物格（储物箱 / 背包 / 物流塔共用的那张表）────────────

        internal delegate int GridGet(ref StorageComponent.GRID g);

        internal delegate void GridSet(ref StorageComponent.GRID g, int v);

        /// <summary>
        /// <c>StorageComponent.GRID.qua</c> 的读取器。
        ///
        /// 搬运一律由 preloader 改写出来的游戏方法完成，这边<b>不参与搬运</b>——
        /// 读是给显示层用的，写只有一个用途，见 <see cref="SetGridQua"/>。
        /// </summary>
        internal static readonly GridGet GetGridQua = MakeGridGet();

        /// <summary>
        /// <b>唯一的用途是存档修复</b>（<see cref="QualityRepairPatches"/>）：
        /// 把越界的整格点数夹回上限。
        ///
        /// 写入器是后来才加的，加它之前这里写着「只读是有意的」——那句话当时对，
        /// 直到发现<b>已经胀出去的存量自己不会好</b>：品质按比例跟着货走，
        /// 一格 502 分的铜块会一路 502 下去，哪怕病因早就修掉了。
        /// 搬运仍然一句都不走这里。
        /// </summary>
        internal static readonly GridSet SetGridQua = MakeGridSet();

        internal delegate int[] ServedQuaGet(ref AssemblerComponent comp);

        /// <summary>
        /// <c>AssemblerComponent.quaServed</c>——投入侧的品质数组（<c>incServed</c> 的孪生）。
        ///
        /// 拿的是<b>数组引用</b>而不是元素：数组是引用类型，拿到之后读写都是真的，
        /// 不用再给每个下标各做一个访问器。<c>AssemblerComponent</c> 是结构体，
        /// 所以参数必须是 <c>ref</c>，否则读的是一份拷贝。
        ///
        /// 本程序集是对着<b>没加孪生字段</b>的 Assembly-CSharp 编译的，
        /// 所以只能运行时绑——和 <see cref="GetGridQua"/> 同一套做法。
        /// </summary>
        internal static readonly ServedQuaGet GetServedQua = MakeServedQuaGet();

        internal static bool ServedQuaReady => GetServedQua != null;

        private static ServedQuaGet MakeServedQuaGet()
        {
            FieldInfo f = AccessTools.Field(typeof(AssemblerComponent), "quaServed");

            if (f == null || f.FieldType != typeof(int[])) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_GetServedQua", typeof(int[]),
                    new[] { typeof(AssemblerComponent).MakeByRefType() },
                    typeof(AssemblerComponent), true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, f);
                il.Emit(OpCodes.Ret);

                return (ServedQuaGet)dm.CreateDelegate(typeof(ServedQuaGet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static bool GridReady => GetGridQua != null;

        internal static bool GridWritable => GetGridQua != null && SetGridQua != null;

        // ── 传送带上那一堆货 ────────────────────────────────────

        internal delegate int CargoGet(ref Cargo c);

        /// <summary>
        /// <c>Cargo.qua</c> 的读取器。**只给诊断用**——搬运一律由改写出来的游戏方法完成。
        ///
        /// 上限巡检故意不扫传送带（货在带上停留以秒计，扫它是在追一个正在流动的影子），
        /// 但「带子上到底有没有品质」是个<b>一次性的问题</b>，而它恰好能把
        /// 「生产端没把品质放上带子」和「取货端把品质丢了」这两种分开——
        /// 这两种的症状一模一样：手捡起来是 0。
        /// </summary>
        internal static readonly CargoGet GetCargoQua = MakeCargoGet("qua");

        /// <summary>
        /// <c>Cargo.stack</c> 的读取器。<b>必须运行时绑，不能直接写 <c>cargo.stack</c>。</b>
        ///
        /// 这个字段被本仓库自己的 preloader 从 <c>Byte</c> 加宽成了 <c>Int16</c>，
        /// 而插件是按**未加宽**的程序集编译的——直接访问会发出
        /// <c>ldfld byte Cargo::stack</c>，运行时解析不到，抛
        /// <c>MissingFieldException: Field not found: byte Cargo.stack</c>。
        ///
        /// <b>CLAUDE.md 为方法签名写过这条</b>（所以本 mod 调游戏搬运方法全走运行时委托），
        /// 而我在字段上原样犯了一遍，代价是一次线上崩溃。
        /// **规矩推广一句：凡是 preloader 动过宽度的东西，插件侧一律运行时绑。**
        /// 现在这一族有：<c>Cargo.inc</c>、<c>Cargo.stack</c>。
        /// </summary>
        internal static readonly CargoGet GetCargoStack = MakeCargoGet("stack");

        /// <summary>
        /// 按字段**实际的宽度**发射读取代码：Byte / Int16 / Int32 都收下，统一返回 Int32。
        /// 写死任何一种宽度都会在另一种下静默失配——而这正是上面那次崩溃的成因。
        /// </summary>
        private static CargoGet MakeCargoGet(string name)
        {
            FieldInfo f = AccessTools.Field(typeof(Cargo), name);

            if (f == null) return null;

            Type ft = f.FieldType;

            if (ft != typeof(int) && ft != typeof(short) && ft != typeof(byte)
                && ft != typeof(ushort) && ft != typeof(sbyte)) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_GetCargo_" + name, typeof(int),
                    new[] { typeof(Cargo).MakeByRefType() }, typeof(Cargo), true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, f);

                // ldfld 已经按字段宽度取好并提升到 int32 了，这里补一条只是把符号性写明白
                if (ft != typeof(int)) il.Emit(OpCodes.Conv_I4);

                il.Emit(OpCodes.Ret);

                return (CargoGet)dm.CreateDelegate(typeof(CargoGet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ── 制造机的两个品质数组 ────────────────────────────────

        /// <summary><c>AssemblerComponent</c> 是<b>结构体</b>，所以取字段要按引用传。</summary>
        internal delegate int[] AsmArrayGet(ref AssemblerComponent a);

        internal delegate void AsmArraySet(ref AssemblerComponent a, int[] v);

        /// <summary>投料格的品质（<c>incServed</c> 的孪生）。物流层已经在往里填了。</summary>
        internal static readonly AsmArrayGet GetQuaServed = MakeAsmGet("quaServed");

        /// <summary>
        /// 产物缓冲区的品质。<b>它不是孪生，是品质独有的新槽位</b>——
        /// 原版没有 <c>incProduced</c>，增产点数不进产物缓冲（喷过的料造出来的东西是干净的）。
        /// 所以这个字段的每一次读写都是手写的，1c 不碰它。
        /// </summary>
        internal static readonly AsmArrayGet GetQuaProduced = MakeAsmGet("quaProduced");

        /// <summary>产物品质数组要现分配：1a 只加字段，没人替它 <c>newarr</c>。</summary>
        internal static readonly AsmArraySet SetQuaProduced = MakeAsmSet("quaProduced");

        internal delegate int AsmIntGet(ref AssemblerComponent a);

        internal delegate void AsmIntSet(ref AssemblerComponent a, int v);

        /// <summary>
        /// 在途池：已经扣了料、产物还没出来的那部分品质。
        /// 理由见 <c>QualityFieldAnalyzer.ExtraFields</c> 里那段注释——
        /// 一个 cycle 的两端不在同一 tick。
        /// </summary>
        internal static readonly AsmIntGet GetQuaPending = MakeAsmIntGet("quaPending");

        internal static readonly AsmIntSet SetQuaPending = MakeAsmIntSet("quaPending");

        /// <summary>
        /// 在途池的**分母**：那些点数是多少件料带来的。
        /// 产物的每件分数 = 在途点数 ÷ 在途件数，两个数必须同时跨 tick。
        /// </summary>
        internal static readonly AsmIntGet GetQuaPendingItems = MakeAsmIntGet("quaPendingItems");

        internal static readonly AsmIntSet SetQuaPendingItems = MakeAsmIntSet("quaPendingItems");

        /// <summary>全都绑上了才算数——少一个，制造那一环就什么都别做。</summary>
        internal static bool CraftReady =>
            GetQuaServed != null && GetQuaProduced != null && SetQuaProduced != null
            && GetQuaPending != null && SetQuaPending != null
            && GetQuaPendingItems != null && SetQuaPendingItems != null;

        private static AsmIntGet MakeAsmIntGet(string name)
        {
            FieldInfo f = AccessTools.Field(typeof(AssemblerComponent), name);

            if (f == null || f.FieldType != typeof(int)) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_Get_" + name, typeof(int),
                    new[] { typeof(AssemblerComponent).MakeByRefType() },
                    typeof(AssemblerComponent), true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, f);
                il.Emit(OpCodes.Ret);

                return (AsmIntGet)dm.CreateDelegate(typeof(AsmIntGet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static AsmIntSet MakeAsmIntSet(string name)
        {
            FieldInfo f = AccessTools.Field(typeof(AssemblerComponent), name);

            if (f == null || f.FieldType != typeof(int)) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_Set_" + name, null,
                    new[] { typeof(AssemblerComponent).MakeByRefType(), typeof(int) },
                    typeof(AssemblerComponent), true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, f);
                il.Emit(OpCodes.Ret);

                return (AsmIntSet)dm.CreateDelegate(typeof(AsmIntSet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static AsmArrayGet MakeAsmGet(string name)
        {
            FieldInfo f = AccessTools.Field(typeof(AssemblerComponent), name);

            if (f == null || f.FieldType != typeof(int[])) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_Get_" + name, typeof(int[]),
                    new[] { typeof(AssemblerComponent).MakeByRefType() },
                    typeof(AssemblerComponent), true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, f);
                il.Emit(OpCodes.Ret);

                return (AsmArrayGet)dm.CreateDelegate(typeof(AsmArrayGet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static AsmArraySet MakeAsmSet(string name)
        {
            FieldInfo f = AccessTools.Field(typeof(AssemblerComponent), name);

            if (f == null || f.FieldType != typeof(int[])) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_Set_" + name, null,
                    new[] { typeof(AssemblerComponent).MakeByRefType(), typeof(int[]) },
                    typeof(AssemblerComponent), true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, f);
                il.Emit(OpCodes.Ret);

                return (AsmArraySet)dm.CreateDelegate(typeof(AsmArraySet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ── 鼠标手上那一格 ────────────────────────────────────

        internal delegate int PlayerIntGet(Player p);

        /// <summary>
        /// 手上那一格的品质。
        ///
        /// <b>字段名带尖括号，写成 <c>inhandItemQua</c> 永远解析不到，而且不报错。</b>
        /// 它是自动属性 <c>Player.inhandItemInc</c> 的孪生，而 1a 是对**后备字段**
        /// <c>&lt;inhandItemInc&gt;k__BackingField</c> 建的孪生，所以名字是
        /// <c>&lt;inhandItemQua&gt;k__BackingField</c>——这正是名字启发式漏掉这整条路的那一次。
        ///
        /// 用途是**量玩家身上少了多少分**（<see cref="QualityFeedPatches"/>），不参与搬运。
        /// </summary>
        internal static readonly PlayerIntGet GetInhandQua = MakePlayerGet("<inhandItemQua>k__BackingField");

        internal delegate void PlayerIntSet(Player p, int v);

        /// <summary>
        /// 手上那一格品质的**写入器**，唯一的用途是<b>把原版漏扣的那一份补扣掉</b>
        /// （<see cref="QualityHandUsePatches"/>），不参与搬运。
        ///
        /// <b>「一个字段的孪生只能有一个写入口」那条规矩在这里不算破例</b>：
        /// 这不是第二条搬运路径，是事后修正——和 <see cref="SetGridQua"/> 给存量夹上限
        /// 是同一类。真正的搬运仍然只走 <c>set_inhandItemInc</c> 的孪生。
        /// </summary>
        internal static readonly PlayerIntSet SetInhandQua = MakePlayerSet("<inhandItemQua>k__BackingField");

        private static PlayerIntSet MakePlayerSet(string name)
        {
            FieldInfo f = AccessTools.Field(typeof(Player), name);

            if (f == null || f.FieldType != typeof(int)) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_SetPlayer_" + name, null,
                    new[] { typeof(Player), typeof(int) }, typeof(Player), true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, f);
                il.Emit(OpCodes.Ret);

                return (PlayerIntSet)dm.CreateDelegate(typeof(PlayerIntSet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static PlayerIntGet MakePlayerGet(string name)
        {
            FieldInfo f = AccessTools.Field(typeof(Player), name);

            if (f == null || f.FieldType != typeof(int)) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_GetPlayer_" + name, typeof(int),
                    new[] { typeof(Player) }, typeof(Player), true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, f);
                il.Emit(OpCodes.Ret);

                return (PlayerIntGet)dm.CreateDelegate(typeof(PlayerIntGet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static GridGet MakeGridGet()
        {
            FieldInfo f = AccessTools.Field(typeof(StorageComponent.GRID), "qua");

            if (f == null || f.FieldType != typeof(int)) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_GetGridQua", typeof(int),
                    new[] { typeof(StorageComponent.GRID).MakeByRefType() },
                    typeof(StorageComponent.GRID), true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, f);
                il.Emit(OpCodes.Ret);

                return (GridGet)dm.CreateDelegate(typeof(GridGet));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static GridSet MakeGridSet()
        {
            FieldInfo f = AccessTools.Field(typeof(StorageComponent.GRID), "qua");

            if (f == null || f.FieldType != typeof(int)) return null;

            try
            {
                var dm = new DynamicMethod("ProjectEden_SetGridQua", null,
                    new[] { typeof(StorageComponent.GRID).MakeByRefType(), typeof(int) },
                    typeof(StorageComponent.GRID), true);

                ILGenerator il = dm.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, f);
                il.Emit(OpCodes.Ret);

                return (GridSet)dm.CreateDelegate(typeof(GridSet));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
