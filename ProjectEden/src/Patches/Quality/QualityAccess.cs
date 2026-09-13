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

        internal static bool GridReady => GetGridQua != null;

        internal static bool GridWritable => GetGridQua != null && SetGridQua != null;

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
