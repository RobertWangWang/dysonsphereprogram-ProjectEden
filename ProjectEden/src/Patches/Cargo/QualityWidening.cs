using System;
using System.Reflection;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 物品品质在<b>运行时</b>的状态探测。设计稿在仓库根目录的 <c>物品品质.md</c>。
    ///
    /// 品质点数是一族由 preloader 新加的 Int32 字段（<c>Cargo.qua</c>、
    /// <c>StationStore.qua</c>、<c>AssemblerComponent.quaServed</c> …），
    /// 和 <c>inc</c> 平行、语义相同：可加量，合并相加、拆分按比例、单件分数 = 点数 ÷ 件数。
    ///
    /// <b>这份代码是按没有这些字段的程序集编译的</b>，所以一律走
    /// <see cref="AccessTools"/> 反射探测，不能直接写 <c>cargo.qua</c>——
    /// 那条 MemberRef 在编译期就解析不了。和 <see cref="CargoWidening"/> 同一个理由。
    ///
    /// <b>当前是阶段 1a：字段已加，但没有任何代码写它们，所以恒为 0。</b>
    /// 这个类现在唯一的职责就是<b>把这件事报出来</b>——
    /// 只在「生效」时才打印的状态行，会让「preloader 没装」和「装了但这一期还没接线」
    /// 在日志上长得一模一样，而这个仓库已经为同一个形状付过五次往返。
    /// </summary>
    internal static class QualityWidening
    {
        /// <summary>preloader 是否已经把孪生字段加上。探的是<b>结果</b>，不是「我以为我装了」。</summary>
        internal static readonly bool FieldsPresent = Probe();

        /// <summary>
        /// 传送带 API 是否已经长出品质尾参（阶段 1b）。
        ///
        /// <b>和 <see cref="FieldsPresent"/> 分开探，不是一件事。</b> 1a 和 1b 是两刀，
        /// 任何一刀可能单独失败；而 <see cref="CargoWidening"/> 的委托要按<b>签名</b>绑，
        /// 绑错的表现是运行时 <c>MissingMethodException</c>、巨型建筑的传送带收发停摆。
        /// 探的是<b>结果</b>——那个方法的最后一个参数到底是不是 <c>out int</c>。
        /// </summary>
        internal static readonly bool BeltParamsPresent = ProbeBeltParams();

        /// <summary>
        /// 搬运层是否已接线。<b>1a / 1b 恒为 false</b>，1c 落地后改为按实际能力探测。
        /// 留成独立的一条，是为了让日志能把「管子接好了但还没通水」和「字段都不在」分开。
        ///
        /// 写成 <c>static readonly</c> 而不是 <c>const</c>：后者会让编译器证明下面那条
        /// 分支不可达而报 CS0162，而本仓库是 0 警告构建。这里要的是一个<b>运行时</b>的值。
        /// </summary>
        internal static readonly bool TransportWired = false;

        private static bool Probe()
        {
            try
            {
                FieldInfo f = AccessTools.Field(typeof(Cargo), "qua");

                return f != null && f.FieldType == typeof(int);
            }
            catch (Exception)
            {
                // 反射失败也算没有——绝不让状态探测本身把启动搞挂
                return false;
            }
        }

        private static bool ProbeBeltParams()
        {
            try
            {
                MethodInfo m = AccessTools.Method(typeof(CargoPath), "TryPickItemAtRear");

                if (m == null) return false;

                ParameterInfo[] ps = m.GetParameters();

                if (ps.Length == 0) return false;

                Type last = ps[ps.Length - 1].ParameterType;

                return last.IsByRef && last.GetElementType() == typeof(int);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void Report()
        {
            if (!FieldsPresent)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "物品品质：Cargo.qua 不存在——preloader 没装，或它整体放弃了改写。" +
                    "品质功能当不存在，其余一切照常。");

                return;
            }

            if (!TransportWired)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"物品品质：孪生字段已就位（Cargo 现在 {CargoWidening.Stride} 字节），" +
                    $"传送带 API 品质尾参{(BeltParamsPresent ? "已加上" : "**没有**")}。" +
                    "搬运层尚未接线，品质点数恒为 0——这是阶段 1a / 1b 的预期状态，不是故障。" +
                    "游戏行为与不装 preloader 时一致。");

                if (FieldsPresent && !BeltParamsPresent)
                    ProjectEdenPlugin.Log.LogWarning(
                        "物品品质：字段加上了但传送带 API 没有品质尾参——" +
                        "说明 preloader 的 1b 那一刀失败了。看它自己那几行 ERROR。");

                return;
            }

            ProjectEdenPlugin.Log.LogWarning("物品品质：搬运层已接线，品质点数开始随货物流动。");
        }
    }
}
