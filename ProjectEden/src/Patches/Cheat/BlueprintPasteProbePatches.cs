using System.Text;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 蓝图粘贴被拒时，<b>把拒绝它的那道闸点名打出来</b>。只诊断，不改任何行为。
    ///
    /// ── 为什么非要有这一条 ──
    ///
    /// 「巨型建筑蓝图复制完粘不下去」报上来的时候，日志里<b>一行相关的都没有</b>——
    /// 又是本仓库那个招牌形状：每一步都成功，功能却不在。而静态读 IL 只能读出
    /// <b>候选</b>闸门，读不出这一次到底卡在哪一道。
    ///
    /// ── 闸门为什么躲开了「无条件建造」 ──
    ///
    /// 这是真正值得记下来的一点。粘贴流程有<b>两个阶段、两套拒绝机制</b>：
    ///
    /// <list type="bullet">
    /// <item><c>CheckBuildConditions</c> —— 逐个预览写 <c>BuildPreview.condition</c>。
    /// <see cref="BuildConditionCheatPatches"/> 擦的就是它。</item>
    /// <item><c>CheckBuildConditionsPrestage</c> —— <b>另一个方法</b>，在摆放阶段先跑，
    /// 拒绝走的是 <c>AddErrorMessage(条件, null)</c>，<b>压根不碰 condition</b>。</item>
    /// </list>
    ///
    /// 所以前者被擦干净了，后者一拒，你连摆放阶段都过不去——
    /// <b>「无条件建造」开着也完全不起作用，而且不会有任何日志说明为什么。</b>
    /// 这是「同一个症状两道闸，清了第一道看不出任何变化」的又一例，
    /// 只不过这次两道闸不在同一个方法里，连字段都不是同一个。
    ///
    /// 前置阶段能报出来的全部理由（IL 读出，不是猜的）：
    /// <code>
    /// BlueprintNeedTech(52)              history.blueprintLimit &lt; blueprint.buildings.Length
    /// BlueprintReformNeedTech(202)       bpReformLimit &lt; reformData.reformCount
    /// Failure(1)                         星球是气态巨星
    /// BlueprintAreaCrossTropic(45)       ┐
    /// BlueprintAreaNotEnoughSpace(46)    │ bpGratBoxConditionArr 四个分量
    /// BlueprintNotAlignTropicAnchor(47)  │ 任意一个 &gt; 0 就立刻 return false
    /// BlueprintWrongTropicRatio(48)      ┘
    /// </code>
    ///
    /// ── 挂在哪 ──
    ///
    /// <c>AddErrorMessage</c> 是<b>粘贴工具所有拒绝理由的唯一收口点</b>，两个阶段都走它，
    /// 所以一个前置就把两阶段全覆盖了，不用去复刻原版任何一段判断逻辑。
    /// 再补一个后置在 <c>CheckBuildConditionsPrestage</c> 上，把几何那四个分量的
    /// <b>具体数值</b>打出来——光知道「空间不够」没用，得知道差多少。
    ///
    /// ── 不刷屏 ──
    ///
    /// 拖动蓝图时这两个方法每帧都跑，所以<b>按条件去重</b>：同一种理由一次粘贴只报一行，
    /// 开关粘贴工具（<c>_OnOpen</c>）时清空。上限之外还有一层总帧数无关的硬上限，
    /// 免得某天有人拿它去跑一整局。
    /// </summary>
    [HarmonyPatch]
    internal static class BlueprintPasteProbePatches
    {
        /// <summary>一次粘贴里最多报多少行，防呆用。</summary>
        private const int MaxLines = 12;

        private static readonly System.Collections.Generic.HashSet<int> Seen =
            new System.Collections.Generic.HashSet<int>();

        private static int _lines;

        /// <summary>开粘贴工具就重新开始记，这样每次尝试都能看到完整的一轮。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), "_OnOpen")]
        private static void OnOpen_Postfix()
        {
            Seen.Clear();
            _lines = 0;
        }

        /// <summary>
        /// 所有拒绝理由的收口点。<b>两个阶段都经由它</b>，所以这一个前置就够。
        ///
        /// <b>参数名必须和原版一字不差：<c>_bdCondition</c> / <c>_bp</c>。</b>
        /// Harmony 是<b>按参数名</b>注入的，名字对不上不是「这个补丁不生效」，而是
        /// <c>Parameter "x" not found</c> 从 <c>PatchAll</c> 里抛出来，
        /// <b>把排在后面的所有补丁类一起废掉</b>。
        /// 这一条实际发生过：第一版写的是 <c>(EBuildCondition type, BuildPreview preview)</c>，
        /// 结果 <c>OreVeinColorPatches</c> 和 <c>VeinProtoArrayPatches</c> 都没被应用，
        /// 症状是<b>一个看起来毫不相干的原版崩溃</b>——进入行星时
        /// <c>LoadingPlanetFactoryMain</c> 里 <c>veinProtos[矿种编号]</c> 越界，
        /// 而堆栈里连 <c>(wrapper dynamic-method)</c> 都没有，因为那个方法根本没被打上补丁。
        /// <c>tools/verify_harmony.ps1</c> 现在会离线抓这一类。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.AddErrorMessage))]
        private static void AddErrorMessage_Prefix(EBuildCondition _bdCondition, BuildPreview _bp)
        {
            EBuildCondition type = _bdCondition;
            BuildPreview preview = _bp;

            var key = (int)type;

            if (_lines >= MaxLines || !Seen.Add(key)) return;

            _lines++;

            string what = "整张蓝图";

            if (preview != null)
            {
                ItemProto item = preview.item;

                what = item != null ? $"{item.name}({item.ID})" : "某个预览（item 为 null）";
            }

            ProjectEdenPlugin.Log.LogWarning(
                $"蓝图粘贴被拒：{type}（{key}），对象：{what}。"
                + "**注意 45~48 / 52 / 202 / 1 这几条走的是 AddErrorMessage，不是 BuildPreview.condition——"
                + "「无条件建造」擦的是后者，对它们无效。**");
        }

        /// <summary>
        /// <b>真正决定「点下去建不建得出来」的地方。</b>
        ///
        /// <c>CreatePrebuilds</c> 的循环体开头有<b>三道闸，而且第一道排在所有条件判断之前</b>：
        /// <code>
        /// 0030: if (bp.bpgpuiModelId &lt;= 0) continue;   ← 连 condition 都还没看
        /// 003B: if (bp.condition != Ok &amp;&amp; bp.condition != NeedGround) continue;
        /// 0196: if (bp.coverObjId != 0) continue;
        /// </code>
        ///
        /// <b>第一道是个新发现，而且它和 condition 是同一段代码写出来的。</b>
        /// <c>ArrangeOverlapBP</c> 判定两个预览重叠时，<b>四处</b>都是成对地写
        /// <c>bpgpuiModelId = -1</c> 外加 <c>condition = 51</c>。
        /// 作弊开关擦掉 <c>condition</c> 之后，<c>bpgpuiModelId</c> <b>还是 -1</b>——
        /// 于是没有红字、没有报错，点下去什么都不发生。
        /// 这正是 <c>coverObjId</c> 那个坑的翻版：<b>同一段循环写两个字段，只清一个等于没清。</b>
        ///
        /// 但这只是<b>候选</b>机制，还没验证它真的在玩家那边触发。所以这里先只打日志：
        /// 每个预览的三道闸各是什么值，按「物品 + 原因」去重。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CreatePrebuilds))]
        private static void CreatePrebuilds_Prefix(BuildTool_BlueprintPaste __instance)
        {
            if (_lines >= MaxLines) return;

            BuildPreview[] pool = __instance.bpPool;

            if (pool == null) return;

            int n = System.Math.Min(__instance.bpCursor, pool.Length);

            for (var i = 0; i < n; i++)
            {
                BuildPreview bp = pool[i];

                if (bp == null) continue;

                // <b>coverObjId != 0 对传送带不等于「被拦」，这是上一版探针的误报。</b>
                // 带子有第二条路：CreatePrebuilds @012F 五个条件全中就登记进
                // bpIdWitchWillRebuildCoverBelt，由 @07AB 另一条循环真正建出来。
                // 不区分这两者的话，一条**建得好好的**带子也会被报成「跳过了」，
                // 而那正是会把下一次排查带偏的那种日志——
                // 本仓库记过：**一个分不清「被拦」和「走了另一条路」的探针，测的不是它要测的东西。**
                bool coverBlocks = bp.coverObjId != 0 && !WillRebuildCoverBelt(bp);

                bool blocked = bp.bpgpuiModelId <= 0
                               || (bp.condition != EBuildCondition.Ok && (int)bp.condition != 2)
                               || coverBlocks;

                if (!blocked) continue;

                int item = bp.item?.ID ?? 0;

                // 「哪个物品 + 因为什么」去重，拖动时不刷屏
                int key = 0x40000 + item * 8
                          + (bp.bpgpuiModelId <= 0 ? 1 : 0)
                          + (bp.condition != EBuildCondition.Ok && (int)bp.condition != 2 ? 2 : 0)
                          + (coverBlocks ? 4 : 0);

                if (!Seen.Add(key)) continue;

                _lines++;

                ProjectEdenPlugin.Log.LogWarning(
                    $"蓝图粘贴·CreatePrebuilds 跳过了 {bp.item?.name ?? "?"}({item})："
                    + $"bpgpuiModelId={bp.bpgpuiModelId}（<=0 就跳过，**排在 condition 之前**）、"
                    + $"condition={bp.condition}({(int)bp.condition})、coverObjId={bp.coverObjId}。"
                    + "三者里哪个不对就是拦住它的那一个；bpgpuiModelId 为 -1 说明是 "
                    + "ArrangeOverlapBP 判了重叠，而它是和 condition 成对写的——只擦 condition 没用。"
                    + CoverBeltDetail(bp));

                if (_lines >= MaxLines) return;
            }
        }

        /// <summary>
        /// 逐项复现 <c>CreatePrebuilds</c> @012F–@0195 的「覆盖带重建」登记条件。
        ///
        /// <b>这不是一个近似判断，是原版那六句 <c>brfalse</c>/<c>ble</c> 的逐条翻译。</b>
        /// 探针拿它来区分「真的被 @019C 丢掉」和「走了 @07AB 那条重建路」——
        /// 两者的 <c>coverObjId</c> 都非零，靠字段本身分不开。
        /// 同一份判据也是 <see cref="BlueprintCoverBeltPatches"/> 决定要不要出手的依据。
        /// </summary>
        private static bool WillRebuildCoverBelt(BuildPreview bp)
        {
            if (bp.coverObjId <= 0) return false;
            if (bp.desc == null || !bp.desc.isBelt) return false;

            BuildPreview output = bp.output;

            if (output == null) return false;
            if (output.desc == null || !output.desc.isBelt) return false;
            if (output.coverObjId <= 0) return false;

            return output.condition == EBuildCondition.Ok;
        }

        /// <summary>
        /// 传送带被 <c>coverObjId</c> 拦下时，把那五个条件<b>逐项</b>打出来。
        ///
        /// 只说「coverObjId 非零」没法行动——四项判据都在<b>下游那条带子</b>身上，
        /// 要知道的是<b>断在哪一项</b>：没有下游、下游不是带子、下游没压着东西，
        /// 还是下游自己建不了。
        /// </summary>
        private static string CoverBeltDetail(BuildPreview bp)
        {
            if (bp.desc == null || !bp.desc.isBelt || bp.coverObjId == 0) return "";

            BuildPreview o = bp.output;

            string why = o == null ? "没有下游（output 为空）"
                : o.desc == null || !o.desc.isBelt ? "下游不是传送带"
                : o.coverObjId <= 0 ? "下游没有压在已有实体上（output.coverObjId = 0）"
                : o.condition != EBuildCondition.Ok ? $"下游自己建不了（output.condition = {o.condition}）"
                : "五项都满足——它走的是重建路径，不该出现在这里";

            return "　**这是一条压着已有带子的传送带**：原版要五项全中才登记进重建名单"
                   + "（自己压着东西 + 自己是带子 + 有下游 + 下游是带子 + 下游也压着东西 + 下游能建），"
                   + $"否则 @019C 直接丢弃。这一段断在：{why}。";
        }

        /// <summary>
        /// 前置阶段判完之后，把它依据的那几个数直接打出来。
        ///
        /// 光有一个「空间不够」的名字没法行动，要知道的是<b>差多少</b>：
        /// 蓝图里几个建筑 vs 科技给了多少容量、格框那四个分量各是多少。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditionsPrestage))]
        private static void Prestage_Postfix(BuildTool_BlueprintPaste __instance, bool __result)
        {
            if (__result || _lines >= MaxLines) return;
            if (!Seen.Add(-1)) return;

            _lines++;

            var sb = new StringBuilder("蓝图粘贴·前置阶段判定失败，依据如下：");

            BlueprintData bp = __instance.blueprint;
            GameHistoryData history = __instance.actionBuild?.history;

            sb.Append(bp?.buildings != null ? $"蓝图建筑 {bp.buildings.Length} 个" : "蓝图为空");

            if (history != null)
                sb.Append($"，蓝图容量上限 {history.blueprintLimit}，铺设容量上限 {history.bpReformLimit}");

            PlanetData planet = __instance.planet;

            sb.Append(planet != null
                ? $"，星球 {planet.displayName}（气体种类 {(planet.gasItems?.Length ?? 0)}，非 0 即气态巨星，粘贴一律拒绝）"
                : "，星球为 null");

            IntVector4[] box = __instance.bpGratBoxConditionArr;

            sb.Append($"，格框数 {__instance.gratBoxCursor}");

            if (box != null)
                for (var i = 0; i < __instance.gratBoxCursor && i < box.Length; i++)
                    sb.Append($"；#{i} 跨回归线 {box[i].x} / 空间不足 {box[i].y} / 未对齐锚点 {box[i].z} / 比例不对 {box[i].w}"
                              + "（四个都要 ≤ 0 才放行）");

            ProjectEdenPlugin.Log.LogWarning(sb.ToString());
        }
    }
}
