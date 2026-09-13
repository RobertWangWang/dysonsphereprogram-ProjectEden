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

        // ── 储物格（储物箱 / 背包 / 物流塔共用的那张表）────────────

        internal delegate int GridGet(ref StorageComponent.GRID g);

        /// <summary>
        /// <c>StorageComponent.GRID.qua</c> 的读取器。
        ///
        /// <b>只读，没有写入器</b>，这是有意的：这条路只服务显示层。品质的搬运在
        /// preloader 的孪生改写里，由游戏自己的方法完成；显示层要是能写，
        /// 就多了一条谁都想不到的旁路。真需要写的时候再加，并在这里写明理由。
        /// </summary>
        internal static readonly GridGet GetGridQua = MakeGridGet();

        internal static bool GridReady => GetGridQua != null;

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
    }
}
