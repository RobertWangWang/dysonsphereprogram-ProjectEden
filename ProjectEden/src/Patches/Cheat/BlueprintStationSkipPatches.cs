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
    /// 蓝图粘贴里那段<b>逐预览遍历全星球物流站</b>的邻距检查，在「无条件建造」开着时整块跳过。
    ///
    /// <para><b>玩家报的是「粘贴大蓝图直接卡死」，实测一次 109.5 秒。</b>
    /// <c>BlueprintPasteProbe</c> 把四段拆开之后指得很清楚：</para>
    /// <code>
    /// 预览准备：  23 次共  61.87 毫秒
    /// 重叠归并：  23 次共   8.90 毫秒
    /// 建造条件：   1 次共 109497.32 毫秒   ← 每个预览 59.5 毫秒
    /// 生成预建物： 1 次共 191.32 毫秒
    /// </code>
    ///
    /// <para><b>成因是这个 mod 自己的设计撞上原版的一段 O(预览 × 站点)。</b>
    /// <c>CheckBuildConditions</c> @2C94 是 <c>if (desc.isStation)</c>，之后遍历整颗星球的
    /// <c>stationPool</c>（@2DDF / @2F17 / @301C 三重回跳）算邻距。原版没事，因为一颗星球
    /// 几十个物流站；而本 mod <b>让每座巨型建筑同时是物流站</b>，实测这颗星球
    /// <b>9,326 个站</b>（其中 8,105 座是巨型建筑），粘 1,840 座又全是站——
    /// 1,840 × 9,326 = <b>1,720 万次</b>。</para>
    ///
    /// <para><b>而这 109 秒算出来的东西全部作废。</b> 枚举过 @2C94..@3021 区间里的
    /// 每一条写入和调用，结果只有两样：</para>
    /// <code>
    /// 2DB7 / 2DC1 / 2EEF / 2EF9 / 2FF0 / 2FFA :  stfld BuildPreview::condition
    /// 2DD0 / 2F08 / 3009                       :  AddErrorMessage(条件, 预览)
    /// </code>
    /// <para>没有 <c>coverObjId</c>、没有 <c>parameters</c>、没有任何下游会读的状态——
    /// 而 <c>condition</c> 正是 <see cref="BuildConditionCheatPatches"/> 随后擦掉的那个字段。
    /// 所以开着「无条件建造」时，这一整块是纯粹的空转。</para>
    ///
    /// <para><b>代价说清楚</b>：跳过之后蓝图错误面板不再列出「距离物流站太近」那几条
    /// （<c>AddErrorMessage</c> 一起跳了）。那是个纯显示项，而且它列出来的东西本来
    /// 就会被放行。关掉「无条件建造」时这里一条指令都不改，行为和原版完全一样。</para>
    ///
    /// <para><b>只改蓝图粘贴这一个工具。</b> 单击建造一次只有一两个预览，1,840 这个乘数
    /// 不存在；按本仓库的规矩，能不碰的方法就不碰。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class BlueprintStationSkipPatches
    {
        private static CheatsConfig Config => ProjectEdenPlugin.CheatsConfig;

        private static int _rewritten = -1;

        /// <summary>
        /// 供 IL 调用：原版的 <c>desc.isStation</c> 再与上「没开无条件建造」。
        ///
        /// <b>关着的时候原样返回</b>，所以这个转译器在默认配置之外是完全惰性的。
        /// </summary>
        internal static bool StationProximityNeeded(bool isStation)
        {
            if (!isStation) return false;

            CheatsConfig c = Config;

            return c == null || !c.enabled || !c.noConditionBuild;
        }

        /// <summary>
        /// 开机状态行。<b>读的是 Harmony 自己的补丁表，不是「我调过 PatchAll 没抛异常」</b>——
        /// 状态行回答「接上了没有」，事件行回答「它决定了什么」，一个替不了另一个。
        /// 本仓库为这条付过七次账。
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
                    if (p.PatchMethod?.DeclaringType == typeof(BlueprintStationSkipPatches))
                        attached = true;
            }

            if (!attached)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "蓝图粘贴·物流站邻距跳过：**补丁没挂上**。"
                    + "粘贴大蓝图时每个预览仍要遍历全星球物流站——"
                    + "实测 1,840 个预览 × 9,326 个站 = 109 秒的卡死。");

                return;
            }

            // **「挂上了」不等于「生效了」。** 上一版这里只看 attached，于是转译器
            // 因为锚点判据不唯一整块拒绝改写（_rewritten = 0）时，状态行照样印着
            // 「开着，所以粘贴时跳过…」——**一行自相矛盾的日志**：同一份日志里
            // 上面五条 ERROR 说一处都没改，下面这条说已经跳过了。
            // 状态行必须报**已生效状态**，这是本仓库记过八次的那条。
            if (_rewritten != 1)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"蓝图粘贴·物流站邻距跳过：**未生效**——转译器改写了 {_rewritten} 处（应当 1 处），"
                    + "上面的 ERROR 说明了原因。粘贴大蓝图时每个预览仍要遍历全星球物流站。");

                return;
            }

            CheatsConfig c = Config;
            bool on = c != null && c.enabled && c.noConditionBuild;

            ProjectEdenPlugin.Log.LogInfo(
                $"蓝图粘贴·物流站邻距跳过：已改写 {_rewritten} 处（应当 1 处）。"
                + (on
                    ? "「无条件建造」开着，所以粘贴时**跳过**那段逐预览遍历全星球物流站的邻距检查"
                      + "——它唯一的产物是 BuildPreview.condition，而那个字段随后就被作弊开关擦掉了。"
                      + "代价：蓝图错误面板不再列出「距离物流站太近」那几条（纯显示项）。"
                    : "「无条件建造」关着，所以这里原样跑原版的检查，行为不变。"));
        }

        /// <summary>
        /// 锚点是 <c>ldfld PrefabDesc::isStation ; brfalse</c>，而**它在这个方法里有三处**
        /// （@2C9B / @2E29 / @2F3F），另外两处就在第一处守卫的区块<b>内部</b>。
        ///
        /// <b>所以不能按形状取，要按结构取</b>：外层那道闸的特征是
        /// <b>它的 brfalse 跳到了另外两处之后</b>——也就是「一跳就跳过整块」。
        /// 恰好满足这一条的必须只有一处，否则一处都不改。
        ///
        /// 按下标而不是按偏移判定：转译器里拿到的是 <c>Label</c>，先建一张
        /// 标签 → 下标的表再解析。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
        private static IEnumerable<CodeInstruction> CheckBuildConditions_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            MethodInfo gate = AccessTools.Method(typeof(BlueprintStationSkipPatches),
                                                 nameof(StationProximityNeeded));

            FieldInfo isStation = AccessTools.Field(typeof(PrefabDesc), nameof(PrefabDesc.isStation));

            // 解析不到就原样返回。**发 call null 会在 Harmony 的写出阶段炸**，
            // 而那个栈跟踪指向的是 Harmony 自己，离出错的这一行十万八千里。
            if (gate == null || isStation == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "蓝图粘贴·物流站邻距跳过：解析不到 StationProximityNeeded 或 isStation，放弃改写。");

                _rewritten = 0;

                return code;
            }

            // 标签 → 下标
            var labelAt = new Dictionary<Label, int>();

            for (var i = 0; i < code.Count; i++)
                foreach (Label l in code[i].labels)
                    labelAt[l] = i;

            var anchors = new List<int>();

            for (var i = 0; i + 1 < code.Count; i++)
            {
                if (!code[i].opcode.Equals(OpCodes.Ldfld)) continue;
                if (!ReferenceEquals(code[i].operand, isStation) && !Equals(code[i].operand, isStation)) continue;
                if (!code[i + 1].opcode.Equals(OpCodes.Brfalse) && !code[i + 1].opcode.Equals(OpCodes.Brfalse_S))
                    continue;

                anchors.Add(i);
            }

            // 外层那道闸：它的 brfalse 跳到所有其它锚点之后
            var outer = -1;
            var outerCount = 0;

            foreach (int a in anchors)
            {
                if (!(code[a + 1].operand is Label lab) || !labelAt.TryGetValue(lab, out int target)) continue;

                // 两个条件缺一不可：
                //   ① 跳过了所有其它锚点（target 在它们之后）
                //   ② **至少把一个其它锚点包在里面**（a < other < target）
                //
                // **只写 ① 会在最后一个锚点上假阳性**：它后面本来就没有别的锚点，
                // 于是「跳过所有其它锚点」平凡成立。实测 3 个锚点里有 2 个满足 ①，
                // 判据不唯一、整块拒绝改写——大声失败救了这一次，但判据本身是错的。
                var skipsAll = true;
                var contains = false;

                foreach (int other in anchors)
                {
                    if (other == a) continue;

                    if (other >= target) { skipsAll = false; break; }

                    if (other > a) contains = true;
                }

                if (!skipsAll || !contains) continue;

                outer = a;
                outerCount++;
            }

            if (outerCount != 1)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"蓝图粘贴·物流站邻距跳过：锚点 {anchors.Count} 处，其中「一跳跳过整块」的有 "
                    + $"{outerCount} 处（应当恰好 1 处）——**一处都不改**。"
                    + "半套改写比原样留着更难读。游戏更新过就重新读一遍 CheckBuildConditions 的结构。");

                _rewritten = 0;

                return code;
            }

            code.Insert(outer + 1, new CodeInstruction(OpCodes.Call, gate));

            _rewritten = 1;

            return code;
        }
    }
}
