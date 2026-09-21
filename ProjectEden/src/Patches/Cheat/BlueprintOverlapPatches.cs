// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 蓝图里重叠的建筑，复制几座就粘贴几座。
    ///
    /// <para>报障：「建筑可以堆叠，蓝图框选也框得到，但一粘贴就只建出来一座」。</para>
    ///
    /// <para><b>蓝图数据本身是全的，被关掉的是粘贴预览。</b>
    /// <c>BuildTool_BlueprintPaste.ArrangeOverlapBP</c> 专门找同一位置的多个预览，
    /// 半径判据是 <c>(a.pos − b.pos).sqrMagnitude &lt; 0.25f</c>（0.5 米），命中之后
    /// <b>成对</b>写两个字段（@0244 / @025F / @037C / @0397，四处形状一致）：</para>
    /// <code>
    /// V_3.coverbp       = V_8;                          // 幸存的那个记下它盖住了谁
    /// V_8.bpgpuiModelId = -1;                           // ← 真正拦住建造的是这个
    /// V_8.condition     = EBuildCondition.BlueprintBPOverlap;   // 51
    /// </code>
    ///
    /// <para><b>而 <c>CreatePrebuilds</c> 的第一道闸就是它，排在 condition 检查之前：</b></para>
    /// <code>
    /// 0030: if (bp.bpgpuiModelId &lt;= 0) continue;    // ← 先跳过
    /// 003B: if (bp.condition != Ok &amp;&amp; bp.condition != NotEnoughItem) continue;
    /// </code>
    ///
    /// <para>所以<b>只清 condition 是没用的</b>——已有的「建造条件放行」作弊清的正是 condition，
    /// 这也是为什么那个开关开着也照样只建一座。这对字段必须一起恢复，和
    /// <c>coverObjId</c> 那一处、以及 CLAUDE.md 记过的「一个值同时是赋值又是比较」是同一族。</para>
    ///
    /// <para><b>恢复成什么值，是快照来的，不是猜的。</b> <c>bpgpuiModelId</c> 是这个预览用来
    /// GPU 实例化绘制的模型号，由 <c>BuildTool_BlueprintCopy.GetBuildPreview</c> 在建预览时赋上；
    /// 除它以外只有 <c>.ctor</c> / <c>ResetAll</c>（都写 −1）和 <c>Clone</c>（原样复制）会碰它。
    /// 所以正确的原值就是「<c>ArrangeOverlapBP</c> 跑之前的那个」——前置拍一张快照，
    /// 后置按快照还原，不需要去猜、也不需要反查 <c>coverbp</c>。</para>
    ///
    /// <para><b>而 <c>coverbp</c> 必须留着不动，这一条是反直觉的，也是这个修法能成立的原因。</b>
    /// 「成对写的字段要成对还原」这条规矩会让人想把 <c>ArrangeOverlapBP</c> 写的三个字段全清掉，
    /// 那样反而会坏——<c>BuildTool_BlueprintPaste.CheckBuildConditions</c> @256D–2584
    /// 拿它当<b>豁免</b>用：</para>
    /// <code>
    /// 2564: if (两个预览的距离² &gt;= 阈值) goto skip;      // 离得够远，不用管
    /// 256D: if (a.coverbp == b) goto skip;               // ← 已知的重叠对，互相不判
    /// 257B: if (b.coverbp == a) goto skip;
    /// 2589: ...否则照常判碰撞/间距
    /// </code>
    /// <para>也就是说，<c>coverbp</c> 正是「这两个预览是故意叠在一起的」这件事的记号。
    /// 清掉它，刚还原的那个会立刻在 <c>CheckBuildConditions</c> 里被重新判成重叠而打回去。
    /// <b>所以只还原那两个真正拦路的字段，第三个留着当通行证。</b></para>
    ///
    /// <para><b>为什么不直接前置返回 false 跳过整个方法。</b> 那 700 条指令里还有传送带、
    /// 分拣器一类的重叠归并，是蓝图粘贴正常工作的一部分；整个跳掉就是拿一个更大的毛病
    /// 换掉这一个——本仓库为「withhold in a postfix, never by returning false from a prefix」
    /// 已经付过账（见 StackedRenderPatches）。</para>
    ///
    /// <para><b>开关跟着「是什么让建筑能堆起来」走</b>：<c>cheats.json</c> 的无碰撞，
    /// 或 <c>advancedminer.json</c> 的 <c>allowMinerOverlap</c>。两个都没开的话，
    /// 重叠的建筑本来就摆不下去，这时候放行只会粘出一片建不成的预览——比现在更糟。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class BlueprintOverlapPatches
    {
        /// <summary>前置快照：下标对齐 <c>bpPool</c>，只在粘贴时用，复用同一个数组。</summary>
        private static int[] _snapshot;

        private static int _reported;
        private static int _restoredTotal;

        private static bool Enabled
        {
            get
            {
                // 无碰撞让任意建筑能叠；allowMinerOverlap 只管采矿机。
                // 两个都关的话重叠建筑压根摆不下去，放行没有意义。
                bool noCollision = ProjectEdenPlugin.CheatsConfig != null
                                   && ProjectEdenPlugin.CheatsConfig.enabled
                                   && ProjectEdenPlugin.CheatsConfig.noCollision;

                bool minerOverlap = ProjectEdenPlugin.MinerConfig != null
                                    && ProjectEdenPlugin.MinerConfig.allowMinerOverlap;

                return noCollision || minerOverlap;
            }
        }

        /// <summary>
        /// 开机状态行。<b>「没开」也要打</b>——只在开着时打的话，
        /// 「开关关着」和「这段代码没进 DLL」在日志里长得一模一样，
        /// 本仓库为这条形状付过七次账。
        /// </summary>
        internal static void Report()
        {
            if (!Enabled)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "蓝图·重叠建筑：**没开**。原版粘贴时会把同一位置的多余预览关掉（只建一座），"
                    + "这里不去动它——因为无碰撞和 allowMinerOverlap 都没开，重叠建筑本来也摆不下去。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "蓝图·重叠建筑：已接管。原版 ArrangeOverlapBP 会把 0.5 米内的重复预览成对标掉"
                + "（bpgpuiModelId = −1 且 condition = BlueprintBPOverlap），而 CreatePrebuilds "
                + "**先查 bpgpuiModelId 再查 condition**，所以只清 condition 不管用。"
                + "现在两个一起还原——复制几座就粘几座。真放行了才会有下一行「已还原 N 座」。");
        }

        /// <summary>
        /// 前置：把当前所有预览的 <c>bpgpuiModelId</c> 拍下来。
        ///
        /// 这一步必须紧贴着 <c>ArrangeOverlapBP</c>，不能挪到更早的地方——
        /// 预览是在 <c>DeterminePreviewsPrestage</c> 里边建边改的，早一点拍到的可能还是 −1。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.ArrangeOverlapBP))]
        private static void ArrangeOverlapBP_Prefix(BuildTool_BlueprintPaste __instance)
        {
            if (!Enabled) return;

            BuildPreview[] pool = __instance?.bpPool;

            if (pool == null) return;

            int n = __instance.bpCursor < pool.Length ? __instance.bpCursor : pool.Length;

            if (_snapshot == null || _snapshot.Length < n) _snapshot = new int[pool.Length];

            for (var i = 0; i < n; i++) _snapshot[i] = pool[i]?.bpgpuiModelId ?? -1;
        }

        /// <summary>
        /// 后置：凡是被标成 <c>BlueprintBPOverlap</c> 的，把那对字段一起还原。
        ///
        /// <b>两个都要动。</b> 只清 <c>condition</c> 的话 <c>CreatePrebuilds</c> 在更前面的
        /// <c>bpgpuiModelId &lt;= 0</c> 那一关就跳过了，表现是「没有报错、点下去也没反应」；
        /// 只还原 <c>bpgpuiModelId</c> 的话过不了后面的 condition 检查。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.ArrangeOverlapBP))]
        private static void ArrangeOverlapBP_Postfix(BuildTool_BlueprintPaste __instance)
        {
            if (!Enabled || _snapshot == null) return;

            BuildPreview[] pool = __instance?.bpPool;

            if (pool == null) return;

            int n = __instance.bpCursor < pool.Length ? __instance.bpCursor : pool.Length;

            if (n > _snapshot.Length) n = _snapshot.Length;

            var restored = 0;

            for (var i = 0; i < n; i++)
            {
                BuildPreview bp = pool[i];

                if (bp == null || bp.condition != EBuildCondition.BlueprintBPOverlap) continue;

                // 快照里本来就是 −1 的不碰：那说明它在被标之前就没有模型号，
                // 还原过去照样过不了 CreatePrebuilds 那一关，只会多出一个假的放行记录。
                if (_snapshot[i] <= 0) continue;

                bp.bpgpuiModelId = _snapshot[i];
                bp.condition = EBuildCondition.Ok;

                restored++;
            }

            if (restored == 0) return;

            _restoredTotal += restored;

            // 一次性事件行：状态行回答「接上了没有」，这一行回答「它放行了什么」
            if (Interlocked.Exchange(ref _reported, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"蓝图·重叠建筑：本次粘贴还原了 {restored} 座被原版关掉的重叠预览"
                    + "（原版判据是 0.5 米内算重复，成对写 bpgpuiModelId = −1 与 "
                    + "condition = BlueprintBPOverlap）。之后不再逐次刷屏。");
        }
    }
}
