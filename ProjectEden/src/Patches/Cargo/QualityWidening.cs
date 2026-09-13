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
        /// 跨方法边界传递品质的<b>线程静态寄存器组</b>在不在（阶段 1b）。
        ///
        /// 它是 preloader 合成进 <c>Assembly-CSharp</c> 的一个类型，所以这里只能按名字反射探。
        /// <b>和 <see cref="FieldsPresent"/> 分开探</b>：1a 和 1b 是两刀，各自可能单独失败，
        /// 而「字段在、通道不在」和「两样都不在」需要不同的处理。
        ///
        /// 它<b>不改任何方法签名</b>——这正是它存在的理由，见 <c>物品品质.md</c>：
        /// 前一版给 90 个方法加尾参，实测会打死 UXAssist 和 InstantDelivery。
        /// </summary>
        internal static readonly bool ChannelPresent = ProbeChannel();

        /// <summary>
        /// 搬运层是否已接线。<b>阶段 1a / 1b 恒为 false</b>，搬运层落地后改为按实际能力探测。
        /// 留成独立的一条，是为了让日志能把「字段和通道都在但还没通水」和「什么都不在」分开。
        ///
        /// 写成 <c>static readonly</c> 而不是 <c>const</c>：后者会让编译器证明下面那条
        /// 分支不可达而报 CS0162，而本仓库是 0 警告构建。这里要的是一个<b>运行时</b>的值。
        /// </summary>
        internal static readonly bool TransportWired = ProbeTransport();

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

        /// <summary>
        /// 搬运层到底通没通。
        ///
        /// <b>探的是 1c 成功那一刻留下的招牌，不是「字段在不在」。</b>
        /// 1a 放的孪生字段在 1c 失败时照样在，所以按字段去猜会把
        /// 「1c 整体放弃了」说成「已接线」——而那两种状态下品质都是 0，日志一模一样。
        /// preloader 在 1c 真的改完之后才往侧信道类型上加 <c>Flowing</c> 这个静态字段,
        /// 这里探的就是它。
        /// </summary>
        private static bool ProbeTransport()
        {
            try
            {
                Type t = AccessTools.TypeByName("ProjectEdenQualityChannel");

                return t != null && AccessTools.Field(t, "Flowing") != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ProbeChannel()
        {
            try
            {
                Type t = AccessTools.TypeByName("ProjectEdenQualityChannel");

                if (t == null) return false;

                FieldInfo q0 = AccessTools.Field(t, "Q0");

                return q0 != null && q0.IsStatic && q0.FieldType == typeof(int);
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
                    $"跨方法通道{(ChannelPresent ? "已合成" : "**没有**")}。" +
                    "搬运层尚未接线，所以品质点数恒为 0——这是阶段 1a / 1b 的预期状态，不是故障。" +
                    "游戏行为与不装 preloader 时一致。");

                if (!ChannelPresent)
                    ProjectEdenPlugin.Log.LogWarning(
                        "物品品质：字段加上了但 ProjectEdenQualityChannel 不存在——" +
                        "说明 preloader 的 1b 那一刀失败了，看它自己那几行 ERROR。");

                return;
            }

            ProjectEdenPlugin.Log.LogWarning("物品品质：搬运层已接线，品质点数开始随货物流动。");
        }
    }
}
