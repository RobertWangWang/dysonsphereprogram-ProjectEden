using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 品质的<b>唯一来源</b>：提纯厂。设计稿第 5.2 节，所有者拍板。
    ///
    /// <b>采出来的矿品质是 0。</b> 这不是「还没做」，是定下来的规则——品质不是采出来的，
    /// 是炼出来的。之前采矿站按每件 10 分铸造那一版是阶段 2 的临时脚手架，
    /// 它让「品质从哪来」有了两个答案，所以随这一步删掉。
    ///
    /// 真正的注入点在 <see cref="QualityRefineryPatches"/>：提纯配方的产物落进
    /// 提纯厂自己的物流站槽位时，按配方声明的「每件品质分」注入。
    ///
    /// 这个类现在只剩报状态。**留着它是因为「没有品质」和「品质功能没装」在日志上
    /// 长得一模一样**，而本仓库已经为这个形状付过五次往返。
    /// </summary>
    internal static class QualitySourcePatches
    {
        private static int _reported;

        /// <summary>启动时说清楚现在处于哪种状态。<b>三种状态都要打。</b></summary>
        internal static void Report()
        {
            if (!QualityWidening.FieldsPresent)
            {
                ProjectEdenPlugin.Log.LogInfo("物品品质：孪生字段不在，品质功能整体不启用。");

                return;
            }

            if (!QualityAccess.Ready)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质：孪生字段在，但 StationStore.qua 的存取器没建起来——" +
                    "品质会一直是 0，提纯厂也注入不了。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "物品品质：来源只有提纯厂。采出来的矿品质为 0，" +
                "要有品质就得把矿送进提纯厂跑一道提纯配方。");

            // **侧信道闸门的状态必须打出来，两种都打。**
            //
            // 它挡的是这样一件事：preloader 把游戏的搬运方法改写成「从侧信道寄存器读品质」,
            // 而这条协议只在**游戏自己的**调用点上接好了。本 mod 直接调那些方法时
            // 不写寄存器，于是它们消费上一个人留下的值——品质凭空长出来
            // （实测单件涨到 1010，上限是 100）。
            //
            // 闸门没建起来时那个洞是敞开的，而症状（某处品质莫名变大）离病因十万八千里。
            // 只在成功时打一行，会让「闸门没建起来」和「这段代码没进 DLL」长得一模一样。
            if (QualityAccess.ChannelClearable)
                ProjectEdenPlugin.Log.LogInfo(
                    $"物品品质：侧信道闸门已就位（{QualityAccess.Registers} 个寄存器）——" +
                    "本 mod 自己调游戏搬运方法时前后各清一次，品质在那几条路上会被丢掉、" +
                    "但不会凭空长出来。");
            else
                ProjectEdenPlugin.Log.LogError(
                    "物品品质：**侧信道闸门没建起来**——找不到 ProjectEdenQualityChannel 或它的 Q0。" +
                    "本 mod 自己调游戏搬运方法时会消费上一个人留在寄存器里的品质，" +
                    "症状是品质在不相干的地方凭空变大。");
        }

        /// <summary>一次性日志的抢占，供提纯厂那边复用（并行 tick 上必须用 Interlocked）。</summary>
        internal static bool ClaimLog() => Interlocked.Exchange(ref _reported, 1) == 0;
    }
}
