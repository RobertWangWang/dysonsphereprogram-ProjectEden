// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 蓝图粘贴里那 <b>6 个「预览对预览」的邻距循环</b>，在「无条件建造」开着时一起停掉。
    ///
    /// <para><b>这是「粘贴大蓝图卡死」的真正原因，而它是 O(预览²)。</b>
    /// 三个数据点（同一存档，逐次加大蓝图）：</para>
    /// <code>
    /// 预览 1,840 → 109,497 毫秒 → 每个  59.5 毫秒
    /// 预览 3,364 → 342,489 毫秒 → 每个 101.8 毫秒
    /// 预览 6,076 → 1,040,983 毫秒 → 每个 171.3 毫秒
    /// </code>
    /// <para><c>log(3.04)/log(1.806) = 1.88</c>——**每个预览的成本随预览数线性上涨**，
    /// 所以是 O(n²)，不是 O(n)。</para>
    ///
    /// <para><b>前面猜错过三次，每一次都是「机制能解释症状」被当成了「它就是原因」。</b>
    /// 三个嫌疑都是量掉的，不是想掉的：</para>
    /// <code>
    /// Physics.OverlapBoxNonAlloc : 800 次共 198.94 毫秒   → 0.06%
    /// AddErrorMessage            : 6726 次共 6.48 毫秒    → 0.0006%
    /// 物流站邻距那一块（已单独跳过）                      → 约 6%（六分之一，正好）
    /// </code>
    ///
    /// <para><b>结构是离线枚举出来的</b>（<c>tools/check_bp_nest.ps1</c>）：外层
    /// <c>017B..4BB1</c> 是逐预览循环，里面**嵌着 6 个同样以 <c>bpCursor</c> 为界的循环</b>——
    /// 炮塔间距、发电间距、物流站间距、<c>coverbp</c> 豁免等等，每一个都是
    /// 「这个预览 vs 其它每一个预览」。</para>
    ///
    /// <para><b>而这 6 个循环的产物，枚举过每一条写入指令</b>
    /// （<c>tools/check_bp_inner.ps1</c>）：</para>
    /// <code>
    /// 24E3..2681  writes: BuildPreview::condition        calls: AddErrorMessage
    /// 2996..2A30  writes: BuildPreview::condition        calls: AddErrorMessage
    /// 2BF5..2C8F  writes: BuildPreview::condition        calls: AddErrorMessage
    /// 2F24..301C  writes: BuildPreview::condition        calls: AddErrorMessage
    /// 3283..3333  writes: BuildPreview::condition        calls: AddErrorMessage
    /// 3725..379A  writes: (无)                           calls: (无)
    /// </code>
    /// <para><b>一个例外都没有：只有 <c>condition</c>。</b> 而 <c>condition</c> 正是
    /// <see cref="BuildConditionCheatPatches"/> 随后擦掉的那个字段。所以开着
    /// 「无条件建造」时，这 6 个循环加起来的 17 分钟，产物 100% 作废。</para>
    ///
    /// <para><b>手法是把循环上界置零，而不是给 6 处各加一道闸。</b>
    /// 6 个循环的上界都是同一个字段 <c>bpCursor</c>，所以一条统一的规则就能全覆盖，
    /// 不用去认 6 种不同的形状——**少一种要维护的形状，就少一类会静默失配的锚点**。
    /// 外层那个循环的上界必须留着（它是「遍历每个预览」本身），判据是
    /// 「这个 <c>bpCursor</c> 属不属于一个嵌套在外层里的回跳」。</para>
    ///
    /// <para><b>代价</b>：蓝图错误面板不再列出这些邻距类的拒绝理由。纯显示项，而且
    /// 它列出来的东西本来就会被作弊开关放行。关掉「无条件建造」时这里一条指令都不改。</para>
    ///
    /// <para><b>方法整体不能短路</b>，这一点也是枚举出来的
    /// （<c>tools/check_bp_writes.ps1</c>）：它还写 <c>parameters</c> / <c>paramCount</c>
    /// （钻头门禁要读）、<c>coverObjId</c>、以及整套传送带和分拣器的连接信息
    /// （<c>input</c> / <c>output</c> / <c>inputObjId</c> / <c>outputToSlot</c> …）。
    /// 能停的只有这 6 个循环。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class BlueprintPairLoopPatches
    {
        private static CheatsConfig Config => ProjectEdenPlugin.CheatsConfig;

        /// <summary>离线数出来的嵌套循环个数。对不上就一处都不改。</summary>
        private const int ExpectedLoops = 6;

        private static int _rewritten = -1;

        /// <summary>
        /// 供 IL 调用：那 6 个内层循环的上界。开着「无条件建造」时返回 0，
        /// 于是 <c>for (i = 0; i &lt; 0; i++)</c> 一次都不进。
        ///
        /// <b>关着的时候原样返回</b>，所以这个转译器在默认配置之外完全惰性。
        /// </summary>
        internal static int InnerLoopBound(int bpCursor)
        {
            CheatsConfig c = Config;

            return c != null && c.enabled && c.noConditionBuild ? 0 : bpCursor;
        }

        /// <summary>
        /// 开机状态行，读的是 Harmony 自己的补丁表加上**转译器实际改了几处**。
        ///
        /// <b>「挂上了」不等于「生效了」</b>——上一个功能（物流站邻距跳过）就是
        /// 转译器整块拒绝改写、状态行却照样说「已跳过」，同一份日志里自相矛盾。
        /// 本仓库为这条记过八次。
        /// </summary>
        internal static void Report()
        {
            var attached = false;

            foreach (MethodBase patched in Harmony.GetAllPatchedMethods())
            {
                if (patched.DeclaringType != typeof(BuildTool_BlueprintPaste)
                    || patched.Name != nameof(BuildTool_BlueprintPaste.CheckBuildConditions)) continue;

                HarmonyLib.Patches info = Harmony.GetPatchInfo(patched);

                if (info?.Transpilers == null) continue;

                foreach (Patch p in info.Transpilers)
                    if (p.PatchMethod?.DeclaringType == typeof(BlueprintPairLoopPatches))
                        attached = true;
            }

            if (!attached || _rewritten != ExpectedLoops)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"蓝图粘贴·预览邻距循环：**未生效**（挂上={attached}，改写 {_rewritten} 处，应当 {ExpectedLoops} 处）。"
                    + "粘贴大蓝图时那 6 个 O(预览²) 的邻距循环照跑——"
                    + "实测 6,076 个预览要 **17 分钟**。");

                return;
            }

            CheatsConfig c = Config;
            bool on = c != null && c.enabled && c.noConditionBuild;

            ProjectEdenPlugin.Log.LogInfo(
                $"蓝图粘贴·预览邻距循环：已改写 {_rewritten} 处（应当 {ExpectedLoops} 处）。"
                + (on
                    ? "「无条件建造」开着，所以粘贴时这 6 个「预览对预览」的邻距循环**一次都不进**。"
                      + "它们唯一的产物是 BuildPreview.condition，而那个字段随后就被作弊开关擦掉了"
                      + "（每一条写入指令都枚举过，见 tools/check_bp_inner.ps1）。"
                      + "实测 6,076 个预览从 17 分钟降下来。代价：蓝图错误面板不再列出邻距类的拒绝理由。"
                    : "「无条件建造」关着，所以这里原样跑原版的检查，行为不变。"));
        }

        /// <summary>
        /// 把那 6 个内层循环的上界换成 <see cref="InnerLoopBound"/>。
        ///
        /// <b>判据是结构，不是形状</b>：先找出所有回跳，最宽的那个是外层的逐预览循环；
        /// 然后凡是<b>严格嵌套在它里面</b>、且自己的上界也是 <c>bpCursor</c> 的回跳，
        /// 就是要停掉的那一个。外层自己的上界不能动——那是「遍历每个预览」本身。
        ///
        /// 数目必须恰好是离线数出来的 6，否则一处都不改：半套改写比原样留着更难读，
        /// 而且会让「粘贴到底还卡不卡」变成一个看运气的问题。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
        private static IEnumerable<CodeInstruction> CheckBuildConditions_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            MethodInfo bound = AccessTools.Method(typeof(BlueprintPairLoopPatches), nameof(InnerLoopBound));
            FieldInfo cursor = AccessTools.Field(typeof(BuildTool_BlueprintPaste),
                                                 nameof(BuildTool_BlueprintPaste.bpCursor));

            // 解析不到就原样返回：发 call null 会在 Harmony 的写出阶段炸，
            // 而那个栈跟踪指向的是 Harmony 自己。
            if (bound == null || cursor == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "蓝图粘贴·预览邻距循环：解析不到 InnerLoopBound 或 bpCursor，放弃改写。");

                _rewritten = 0;

                return code;
            }

            var labelAt = new Dictionary<Label, int>();

            for (var i = 0; i < code.Count; i++)
                foreach (Label l in code[i].labels)
                    labelAt[l] = i;

            // 所有回跳 = (头, 尾)
            var heads = new List<int>();
            var tails = new List<int>();

            for (var i = 0; i < code.Count; i++)
            {
                if (!(code[i].operand is Label lab)) continue;
                if (!labelAt.TryGetValue(lab, out int head) || head >= i) continue;

                heads.Add(head);
                tails.Add(i);
            }

            if (heads.Count == 0)
            {
                ProjectEdenPlugin.Log.LogError("蓝图粘贴·预览邻距循环：一个回跳都没找到，放弃改写。");

                _rewritten = 0;

                return code;
            }

            // 最宽的回跳 = 外层的逐预览循环
            var outer = 0;

            for (var k = 1; k < heads.Count; k++)
                if (tails[k] - heads[k] > tails[outer] - heads[outer])
                    outer = k;

            int outerHead = heads[outer];
            int outerTail = tails[outer];

            // 严格嵌套在外层里、且读 bpCursor 的回跳
            var targets = new List<int>();

            for (var k = 0; k < heads.Count; k++)
            {
                if (k == outer) continue;
                if (heads[k] <= outerHead || tails[k] >= outerTail) continue;

                // 这个循环体里那条读 bpCursor 的指令
                var found = -1;

                for (int i = heads[k]; i <= tails[k]; i++)
                {
                    if (!code[i].opcode.Equals(OpCodes.Ldfld)) continue;
                    if (!Equals(code[i].operand, cursor)) continue;

                    found = i;
                }

                if (found >= 0 && !targets.Contains(found)) targets.Add(found);
            }

            if (targets.Count != ExpectedLoops)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"蓝图粘贴·预览邻距循环：应当 {ExpectedLoops} 个嵌套循环，实际 {targets.Count} 个"
                    + "——**一处都不改**。游戏更新过就重跑 tools/check_bp_nest.ps1 重新数，"
                    + "顺带用 tools/check_bp_inner.ps1 确认它们仍然只写 condition。");

                _rewritten = 0;

                return code;
            }

            targets.Sort();

            // 从后往前插，前面的下标才不会被顶走
            for (int k = targets.Count - 1; k >= 0; k--)
                code.Insert(targets[k] + 1, new CodeInstruction(OpCodes.Call, bound));

            _rewritten = targets.Count;

            return code;
        }
    }
}
