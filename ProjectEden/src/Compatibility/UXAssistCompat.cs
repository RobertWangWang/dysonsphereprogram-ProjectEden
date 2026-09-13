using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Compatibility
{
    /// <summary>
    /// UXAssist 与本 mod 的两处冲突。<b>两处都不修，都只是说清楚</b>——而这不是偷懒，
    /// 是各自有一条不能绕过的理由。
    ///
    /// <b>一、传送带信号购买：从外部修不了，也关不掉。</b>
    /// 本 mod 的 preloader 把传送带那条链上 <b>33 个签名</b>的 <c>byte inc/stack</c>
    /// 加宽成了 <c>Int16</c>（那是 5000 层集装的前提）。UXAssist 按原版签名编译，
    /// 它 <c>BeltSignalsForBuyOut</c> 里那条
    /// <c>CargoPath.TryInsertItem(int,int,byte,byte)</c> 于是解析不上。
    ///
    /// 试过三轮：按方法名匹配（没中）、按 <c>operand == null</c> 匹配（中了、换了垫片，
    /// 写回仍然在同一条指令上抛）、再退一步只加个前置把它停掉（同样失败）。
    /// 诊断把每条调用指令原样打出来之后结论才清楚：<b>HarmonyX 打任何补丁都要把
    /// 原方法体重新发射一遍</b>，而那个方法体里有一条指向已不存在签名的 MemberRef——
    /// 读进来是 null，写回就炸，换成什么补丁都一样。**这不是没找到办法，是这条路不存在。**
    ///
    /// <b>二、矿脉保护：修得了，但修好更糟。</b>
    /// 它的前置<b>返回 false、完整重实现了 <c>MinerComponent.InternalUpdate</c></b>，
    /// 而本 mod 的 <see cref="Patches.AdvancedMinerPatches"/> 是<b>转译那同一个方法体</b>的——
    /// 矿石→锭替换、机内缓存上限、钻头消耗、小型采矿机节流全在里面。让它跑起来
    /// 只会买到一个<b>静默的功能互斥</b>。
    ///
    /// 而这两个功能本来就重叠：<c>advancedminer.json</c> 的 <c>forceMiningCostRate=0</c>
    /// 早就让大型采矿机 / 抽水站 / 采油站「矿脉完全不消耗」，
    /// 唯一的缺口小型采矿机也已由 <c>protectSmallMinerVeins</c> 补上。
    /// 所以关掉 UXAssist 那个开关<b>不损失任何效果</b>，这里只负责把话说到。
    /// </summary>
    internal static class UXAssistCompat
    {
        internal const string Guid = "org.soardev.uxassist";

        internal static bool Installed => CompatibilityRegistry.IsLoaded(Guid);

        private static int _limitReported;
        private static int _minerConflictChecked;

        internal static void ApplyPatches(Harmony harmony)
        {
            if (harmony == null) return;

            if (!Installed)
            {
                ProjectEdenPlugin.Log.LogInfo("UXAssist 没装，跳过它的兼容检查");

                return;
            }

            if (!Patches.CargoWidening.IsActive)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "UXAssist 兼容：Cargo.inc 没有被加宽（preloader 没装或放弃了改写），" +
                    "传送带 API 还是原版签名，UXAssist 的功能都正常");

                return;
            }

            ReportBeltSignalLimit();
        }

        /// <summary>
        /// 把「传送带信号购买用不了」说清楚，一次。
        ///
        /// <b>只说不修，是因为修不了</b>（见类注释）。这条日志的意义是：
        /// 让玩家在<b>用到它之前</b>就看到一条能对上的解释，而不是某天吃一个
        /// 指不到原因的 <c>MissingMethodException</c>。
        /// </summary>
        private static void ReportBeltSignalLimit()
        {
            if (Interlocked.Exchange(ref _limitReported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogWarning(
                "已知限制：UXAssist 的「传送带信号购买」在本 mod 下用不了。" +
                "本 mod 的 preloader 把传送带 API 的 byte 参数加宽成了 Int16" +
                "（那是 5000 层集装的前提），而 UXAssist 按原版签名编译，那条调用解析不上。");

            ProjectEdenPlugin.Log.LogWarning(
                "这个**从外部修不了也关不掉**：Harmony 打任何补丁都要重发射整个方法体，" +
                "而方法体里那条引用已经不存在了。要用它就得卸掉本 mod 的 preloader" +
                "（代价：集装层数从 5000 退回 63）。UXAssist 的其余功能不受影响。");
        }

        /// <summary>
        /// 检测 UXAssist 的「矿脉保护」有没有真的挂在 <c>MinerComponent.InternalUpdate</c> 上，
        /// 挂了就把互斥说清楚。
        ///
        /// <b>检测点必须在采矿 tick 上，不能在启动时。</b> 那是个可以在游戏里随时勾的开关，
        /// UXAssist 是在勾选的那一刻才打补丁的——启动时查一定查不到。实测过。
        ///
        /// <b>查的是 Harmony 的实际补丁表，不是对方的配置项。</b> 别人的配置字段名会变，
        /// 而「这个方法上到底挂了谁」是结果本身。
        ///
        /// 采矿 tick 跑在 <c>_miner_parallel</c> 上，一次性标志必须用 Interlocked 抢，
        /// 否则三十多个线程各打一行。
        /// </summary>
        internal static void CheckMinerConflictOnce()
        {
            if (!Installed) return;

            if (Interlocked.Exchange(ref _minerConflictChecked, 1) != 0) return;

            try
            {
                MethodInfo target = AccessTools.Method(typeof(MinerComponent), nameof(MinerComponent.InternalUpdate));

                if (target == null) return;

                // 写全名：HarmonyLib.Patches 和本仓库自己的 ProjectEden.Patches 命名空间同名
                HarmonyLib.Patches info = Harmony.GetPatchInfo(target);

                if (info?.Prefixes == null) return;

                bool theirs = info.Prefixes.Any(p =>
                    p.PatchMethod?.DeclaringType?.Name == "ProtectVeinsFromExhaustion");

                if (!theirs) return;

                ProjectEdenPlugin.Log.LogWarning(
                    "检测到 UXAssist 的「矿脉保护」已挂在采矿机上，而它和本 mod 的采矿机改造**互斥**：" +
                    "它的前置返回 false、整个跳过原版方法体，而本 mod 的矿石→锭替换、机内缓存上限、" +
                    "钻头消耗、小型采矿机节流全都在那个方法体里——这些会静默失效。");

                ProjectEdenPlugin.Log.LogWarning(
                    "建议关掉 UXAssist 的矿脉保护：本 mod 已经提供同样的效果——" +
                    "大型采矿机 / 抽水站 / 采油站由 advancedminer.json 的 forceMiningCostRate=0 覆盖，" +
                    "小型采矿机由 protectSmallMinerVeins 覆盖，两者都是矿脉完全不消耗。");
            }
            catch (Exception e)
            {
                ProjectEdenPlugin.Log.LogWarning($"检查 UXAssist 采矿机冲突时出错（不影响游戏）：{e.Message}");
            }
        }
    }
}
