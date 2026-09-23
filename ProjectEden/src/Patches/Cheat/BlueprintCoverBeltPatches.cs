// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 堆叠粘贴时，压在已有传送带上的那些带子不再凭空消失。
    ///
    /// <para>报障：「蓝图粘贴，堆叠建造模式下，传送带可能会出现不建造的情况」。
    /// <b>「可能」是这条的指纹</b>——同一张蓝图里有的带子建得出来、有的建不出来，
    /// 而拦住它的判据里有四项是关于<b>下游那条带子</b>的，所以结果逐段不同。</para>
    ///
    /// <h3>原版对「压在已有带子上的带子」有两条路，而不是一条</h3>
    ///
    /// <para><c>BuildTool_BlueprintPaste.CreatePrebuilds</c> 的循环体里：</para>
    /// <code>
    /// 012F: if (bp.coverObjId &gt; 0            // 我盖住了一条已有的实体
    ///        &amp;&amp; bp.desc.isBelt              // 我是传送带
    ///        &amp;&amp; bp.output != null           // 我有下游
    ///        &amp;&amp; bp.output.desc.isBelt       // 下游也是传送带
    ///        &amp;&amp; bp.output.coverObjId &gt; 0   // ← 下游也压在已有实体上
    ///        &amp;&amp; bp.output.condition == Ok) // ← 而且下游能建
    ///        bpIdWitchWillRebuildCoverBelt[cursor++] = i;   // 登记「这条覆盖带要重建」
    ///
    /// 0196: if (bp.coverObjId != 0) continue;   // ← 否则到此为止
    /// </code>
    ///
    /// <para>登记进那个数组的，会在 <b>@07AB 另一条循环</b>里真正建成 <c>PrebuildData</c>；
    /// <b>没登记上的，就在 @019C 被静默丢掉</b>——不报错、不红字、什么都不发生。
    /// 这正是玩家看到的「传送带不建造」。</para>
    ///
    /// <h3>为什么偏偏是堆叠模式</h3>
    ///
    /// <para>那五个条件是给「把蓝图粘回它自己原来的位置上」设计的：整条带子链都压在老链上，
    /// 于是每一段的下游也都 <c>coverObjId &gt; 0</c>，条件成立，整链重建。
    /// <b>链一旦断开，判据就塌了</b>：</para>
    /// <list type="bullet">
    ///   <item>链尾那段没有下游（<c>output == null</c>）→ 不登记 → 丢掉</item>
    ///   <item>下游接的是建筑不是带子 → 不登记 → 丢掉</item>
    ///   <item>下游伸出了老带子的范围（那一段 <c>coverObjId == 0</c>）→ 不登记 → 丢掉</item>
    /// </list>
    ///
    /// <para>所以正常粘贴时它多半整链成立、看不出问题；而堆叠模式下玩家反复往同一片地方叠，
    /// 链的断口远比平时多，于是「有的建、有的不建」。</para>
    ///
    /// <h3>这一半是本仓库自己挖的，必须写清楚</h3>
    ///
    /// <para><c>BuildConditionCheatPatches</c> 在无碰撞开着时会清掉预览的 <c>coverObjId</c>，
    /// <b>正是为了让建筑能叠</b>——那道字段是 <c>CreatePrebuilds</c> 的静默闸门。
    /// 但它对<b>传送带和分拣器例外</b>（<c>IsConnectionCarrier</c>），因为对那两类来说
    /// <c>coverObjId</c> 是「接到这条上去」的意图本身，清掉会把带子断成互不连通的独立段
    /// ——那是两次报障换来的。</para>
    ///
    /// <para>于是缺口就在这里：<b>无碰撞让建筑能叠，却把带子留在原版那套「要么整链重建、
    /// 要么整段丢弃」的规则里。</b> 这是 CLAUDE.md 反复记的那个形状——
    /// <i>本仓库为某个功能加的规则，悄悄限制了后来加的另一个功能</i>——
    /// 而这次<b>两边都是我们自己的</b>。</para>
    ///
    /// <h3>修法：只碰原版确定会丢掉的那些</h3>
    ///
    /// <para>前置里把原版那五个条件<b>逐项复现</b>，只对「<c>coverObjId != 0</c> 而且
    /// <b>没有</b>进重建名单」的传送带清 <c>coverObjId</c>，让它走回正常路径建一条新的。
    /// 会走重建路径的那些<b>一根手指都不碰</b>——那条路是连接复用，正是
    /// <c>IsConnectionCarrier</c> 要保住的东西。</para>
    ///
    /// <para><b>判据必须是复现而不是近似</b>：这是本仓库的钻头教训——
    /// <i>一个在原版做决定之前跑的钩子，必须复现那个决定，而不是它的名义形状</i>。
    /// 少一项就会碰到本该重建的带子，多一项就会漏掉本该救的。</para>
    ///
    /// <para><b>代价要说在前面</b>：粘贴到已有带子上时，原版的「复用老带子、不建新的」
    /// 在这种情况下会变成「叠一条新的上去」。这在堆叠模式下正是玩家要的，
    /// 所以开关跟着无碰撞走；无碰撞关着时这里整个不生效，行为和原版一模一样。</para>
    ///
    /// <para><b>不用还原。</b> 预览的 <c>coverObjId</c> 每帧由 <c>CheckBuildConditions</c>
    /// 重算，而 <c>CreatePrebuilds</c> 只在按下去那一下跑——不存在「改了之后留在那儿」。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class BlueprintCoverBeltPatches
    {
        private static int _reported;
        private static int _freedTotal;

        /// <summary>
        /// 跟着「是什么让建筑能叠」走，和 <see cref="BlueprintOverlapPatches"/> 同一个判据。
        ///
        /// <b>刻意不新开配置项</b>：这不是一个新功能，是把无碰撞已经给了建筑、
        /// 却因为传送带例外而漏掉的那一半补齐。多一个开关只会让「我开了 A 没开 B」
        /// 变成又一种说不清的状态。
        /// </summary>
        private static bool Enabled
            => ProjectEdenPlugin.CheatsConfig != null
               && ProjectEdenPlugin.CheatsConfig.enabled
               && ProjectEdenPlugin.CheatsConfig.noCollision;

        /// <summary>
        /// 开机状态行。<b>「没开」也要打</b>——本仓库为「只在开着时打日志」这条形状付过七次账，
        /// 因为那样「开关关着」和「这段代码没进 DLL」在日志里长得完全一样。
        /// </summary>
        internal static void Report()
        {
            if (!Enabled)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "蓝图·覆盖传送带：**没开**（跟随无碰撞）。压在已有传送带上的带子仍按原版规则走："
                    + "整条覆盖链成立才重建，链一断那一段就被静默丢掉。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "蓝图·覆盖传送带：已接管。原版 CreatePrebuilds 对「压着已有带子的带子」有两条路——"
                + "@012F 五个条件全中才登记进重建名单，否则 @019C 直接 continue，"
                + "**不报错、不红字、什么都不发生**。那五个条件里有四项是关于**下游那条带子**的，"
                + "所以堆叠粘贴时链一断就逐段消失。现在只对「确定会被丢掉」的那些清 coverObjId，"
                + "让它们建成新的一条；会走重建路径的完全不碰。真放行了才会有下一行「本次放行 N 段」。");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CreatePrebuilds))]
        private static void CreatePrebuilds_Prefix(BuildTool_BlueprintPaste __instance)
        {
            if (!Enabled) return;

            BuildPreview[] pool = __instance?.bpPool;

            if (pool == null) return;

            int n = __instance.bpCursor < pool.Length ? __instance.bpCursor : pool.Length;

            var freed = 0;

            for (var i = 0; i < n; i++)
            {
                BuildPreview bp = pool[i];

                if (bp == null || bp.coverObjId == 0) continue;
                if (bp.desc == null || !bp.desc.isBelt) continue;

                // 原版还会在 @0030 / @003C 先把它筛掉，那两关不归这里管
                // （重叠预览由 BlueprintOverlapPatches 还原，condition 由建造条件作弊清）。
                // 这里只回答一个问题：**它会不会进重建名单**。
                if (WillRebuild(bp)) continue;

                bp.coverObjId = 0;
                bp.willRemoveCover = false;
                bp.willReconstructCover = false;

                freed++;
            }

            if (freed == 0) return;

            _freedTotal += freed;

            if (Interlocked.Exchange(ref _reported, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"蓝图·覆盖传送带：本次粘贴放行了 {freed} 段压在已有传送带上、"
                    + "但**不满足原版重建条件**的带子（原版会把它们静默丢掉）。"
                    + "判据是逐项复现 CreatePrebuilds @012F 的五个条件，"
                    + "满足的那些一根手指都没碰。之后不再逐次刷屏。");
        }

        /// <summary>
        /// 逐项复现 <c>CreatePrebuilds</c> @012F–@0195 的登记条件。
        ///
        /// <para><b>顺序和取值都照 IL 抄</b>，不做等价改写：</para>
        /// <code>
        /// 0136: coverObjId &gt; 0        ble.s → 跳过
        /// 0143: desc.isBelt           brfalse.s → 跳过
        /// 014B: output != null        brfalse.s → 跳过
        /// 015D: output.desc.isBelt    brfalse.s → 跳过
        /// 016B: output.coverObjId &gt; 0 ble.s → 跳过
        /// 0178: output.condition == 0 brtrue.s → 跳过（非 0 就不登记）
        /// </code>
        ///
        /// <para><b>最后一项是 <c>brtrue</c> 不是 <c>beq Ok</c></b>：原版比的是
        /// 「<c>condition</c> 为零」，而 <c>EBuildCondition.Ok</c> 正好是 0。
        /// 写成 <c>== Ok</c> 在语义上一致，但要记住闸门真正测的是零值——
        /// 哪天原版给 <c>Ok</c> 换个数字，这里会跟着错。</para>
        /// </summary>
        private static bool WillRebuild(BuildPreview bp)
        {
            if (bp.coverObjId <= 0) return false;
            if (bp.desc == null || !bp.desc.isBelt) return false;

            BuildPreview output = bp.output;

            if (output == null) return false;
            if (output.desc == null || !output.desc.isBelt) return false;
            if (output.coverObjId <= 0) return false;

            return output.condition == EBuildCondition.Ok;
        }
    }
}
