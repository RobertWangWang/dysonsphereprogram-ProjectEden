#pragma warning disable 649 // TechConfig 的字段由 JSON 反序列化赋值

using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches.Tech
{
    /// <summary>
    /// **用元数据买断科技时，把没解锁的前置一并买断。**
    ///
    /// <para>原版的规则是「前置没解锁就不能买断」，两道闸：
    /// <c>GameHistoryData.BuyoutTech</c> @0056 和 <c>UITechNode.OnBuyoutButtonClick</c> @001D
    /// （后者弹「存在未解锁的前置科技」然后直接 return）。本补丁让深处的科技一次点开，
    /// **每一级仍然照常付元数据**——省的是点击次数，不是代价。</para>
    ///
    /// <para><b>前置判据是照抄的，不是按名字猜的。</b> <c>HasPreTechUnlocked</c> 的循环
    /// 是 <c>for (j = 0; j &lt; 2; j++)</c>，<c>j == 0</c> 取 <c>PreTechs</c>、
    /// <c>j == 1</c> 取 <b><c>PreTechsImplicit</c></b>，两张表都要全部 <c>unlocked</c>。
    /// 只走 <c>PreTechs</c> 会漏掉隐式前置，而后果不是报错——是原版那道闸照样拦着，
    /// 表现为「点了没反应」。</para>
    ///
    /// <para><b>不是「一次性免费解锁」。</b> 原版另有 <c>UnlockTechUnlimitedWithAllPre</c>
    /// 能白送整条链，这里刻意不用它：闭包里的每一级都走 <c>BuyoutTech</c>，
    /// 各付各的元数据，付不起就停在那一级。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class TechBuyoutCascadePatches
    {
        internal static TechConfig Config;

        private static bool Enabled => Config != null && Config.buyoutCascade;

        /// <summary>
        /// 递归深度上限。**不是性能考虑，是防挂**：科技表里一旦出现环
        /// （自定义科技写错前置就会），闭包遍历会无限下去，而游戏只会卡死，不会报错。
        /// </summary>
        private const int MaxDepth = 64;

        private static int _cascading;

        // ── 闭包 ────────────────────────────────────────────────

        /// <summary>
        /// 收集 <paramref name="techId"/> 所有**尚未解锁**的前置，**深的排前面**
        /// （拓扑序），这样逐个买断时每一级自己的前置都已经就位。
        ///
        /// <para>目标科技本身<b>不在</b>结果里。</para>
        /// </summary>
        internal static List<int> MissingPreClosure(GameHistoryData history, int techId)
        {
            var ordered = new List<int>();

            if (history?.techStates == null) return ordered;

            var seen = new HashSet<int>();

            Walk(history, techId, ordered, seen, 0);

            return ordered;
        }

        private static void Walk(GameHistoryData history, int techId,
                                 List<int> ordered, HashSet<int> seen, int depth)
        {
            if (depth > MaxDepth) return;

            TechProto proto = LDB.techs?.Select(techId);

            if (proto == null) return;

            // 两张前置表，和 HasPreTechUnlocked 的 for (j = 0; j < 2; j++) 一一对应
            for (var j = 0; j < 2; j++)
            {
                int[] pres = j == 0 ? proto.PreTechs : proto.PreTechsImplicit;

                if (pres == null) continue;

                for (var k = 0; k < pres.Length; k++)
                {
                    int pre = pres[k];

                    if (pre <= 0) continue;
                    if (!history.techStates.ContainsKey(pre)) continue;
                    if (history.techStates[pre].unlocked) continue;
                    if (!seen.Add(pre)) continue;

                    // 先递归，再把自己加进去 —— 这就是「深的排前面」
                    Walk(history, pre, ordered, seen, depth + 1);

                    ordered.Add(pre);
                }
            }
        }

        // ── 给 UI 用的两个判据 ──────────────────────────────────

        /// <summary>
        /// 替换 UI 里的 <c>HasPreTechUnlocked</c>：前置已解锁**或者**这条链可以级联买断。
        ///
        /// <para>关掉开关时逐字退回原版的答案，所以这个转译在配置关着时是空操作。</para>
        /// </summary>
        internal static bool HasPreTechUnlockedOrCascadable(GameHistoryData history, int techId)
        {
            bool vanilla = history.HasPreTechUnlocked(techId);

            if (vanilla || !Enabled) return vanilla;

            // 闭包非空就说明「缺的前置是可枚举的」，够不够元数据由下一个判据回答——
            // 两件事分开，玩家才能从弹窗知道到底卡在哪一条
            return MissingPreClosure(history, techId).Count > 0;
        }

        /// <summary>
        /// 替换 UI 里的 <c>CheckPropertyAdequateForBuyout</c>：目标**和整条链**都要付得起。
        ///
        /// <para><b>这是尽力而为的估计，不是保证。</b> <c>GetItemAvaliableProperty</c> 读的是
        /// 当前余额，而级联会边买边扣，所以「此刻每一条都付得起」不等于「顺序买完还付得起」。
        /// 真正的判定在买的那一刻逐级做，失败就停——见 <see cref="Cascade"/>。</para>
        /// </summary>
        internal static bool AdequateForCascade(GameHistoryData history, int techId)
        {
            if (!history.CheckPropertyAdequateForBuyout(techId)) return false;
            if (!Enabled) return true;

            List<int> closure = MissingPreClosure(history, techId);

            for (var i = 0; i < closure.Count; i++)
                if (!history.CheckPropertyAdequateForBuyout(closure[i]))
                    return false;

            return true;
        }

        // ── 引擎侧：买断之前先把前置买了 ────────────────────────

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameHistoryData), nameof(GameHistoryData.BuyoutTech))]
        private static void GameHistoryData_BuyoutTech(GameHistoryData __instance, int techId)
        {
            if (!Enabled) return;

            // 级联自己会对每一级再调 BuyoutTech，那会重入这里。按拓扑序买的时候
            // 每一级的前置都已经就位，所以重入天然没事可做；这个标志是第二道保险
            if (System.Threading.Interlocked.CompareExchange(ref _cascading, 1, 0) != 0) return;

            try
            {
                if (__instance.HasPreTechUnlocked(techId)) return;

                Cascade(__instance, techId);
            }
            finally
            {
                System.Threading.Volatile.Write(ref _cascading, 0);
            }
        }

        /// <summary>
        /// 按拓扑序逐级买断。
        ///
        /// <para><b>付不起就停，已经买到的保留</b>——这一点和「配方做到一半」不同：
        /// 中途停下不会浪费任何元数据，玩家实实在在拿到了那几级科技，只是没够到目标。
        /// 所以这里不做「全有或全无」的预检，那反而会在余额刚好够前几级时什么都不给。</para>
        /// </summary>
        private static void Cascade(GameHistoryData history, int techId)
        {
            List<int> closure = MissingPreClosure(history, techId);

            if (closure.Count == 0) return;

            var bought = 0;

            for (var i = 0; i < closure.Count; i++)
            {
                int pre = closure[i];

                if (history.techStates.ContainsKey(pre) && history.techStates[pre].unlocked) continue;

                if (!history.BuyoutTech(pre))
                {
                    TechProto p = LDB.techs?.Select(pre);

                    ProjectEdenPlugin.Log.LogInfo(
                        $"科技级联买断：买到第 {bought} 级时停下——「{p?.Name ?? pre.ToString()}」买不动了" +
                        "（元数据不足，或它自己还有买不起的前置）。" +
                        "**已经买到的那几级保留**，没有任何元数据被浪费。");

                    return;
                }

                bought++;
            }

            TechProto target = LDB.techs?.Select(techId);

            ProjectEdenPlugin.Log.LogInfo(
                $"科技级联买断：为「{target?.Name ?? techId.ToString()}」补齐了 {bought} 级前置，各级都照常付了元数据。");
        }

        // ── UI 侧：把两道闸换成级联版 ───────────────────────────

        /// <summary>
        /// <c>UITechNode.OnBuyoutButtonClick</c> 里两处调用各换一次：
        /// <c>HasPreTechUnlocked</c> 和 <c>CheckPropertyAdequateForBuyout</c>。
        ///
        /// <para><b>只换 operand，不增删指令</b>——两个替身的签名和被替换者逐位一致
        /// （都是 <c>(GameHistoryData, int) -&gt; bool</c>，实例方法的 this 就是第一个参数），
        /// 所以栈形状不变、分支标签不动。这是本仓库对转译器的标准要求。</para>
        ///
        /// <para><b>两处都换，不能只换一处。</b> 只换前置那处，点下去会被「元数据不足」
        /// 拦住（因为原版那个判据只算目标科技自己的花费）；只换花费那处，会被
        /// 「存在未解锁的前置科技」拦住。数目对不上就整体不改写——
        /// <b>半个改写比不改写更难查</b>。</para>
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UITechNode), "OnBuyoutButtonClick")]
        private static IEnumerable<CodeInstruction> UITechNode_OnBuyoutButtonClick(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            // **先解析，null 就整体不动。** 解析失败时把 null 当 operand 发出去，
            // 会在 Harmony 的写入阶段炸成 "Invalid argument for call NULL"，
            // 而那条栈顶指向的是 Harmony 的 writer、离出错的这一行十万八千里
            System.Reflection.MethodInfo preSrc =
                AccessTools.Method(typeof(GameHistoryData), nameof(GameHistoryData.HasPreTechUnlocked));
            System.Reflection.MethodInfo costSrc =
                AccessTools.Method(typeof(GameHistoryData), nameof(GameHistoryData.CheckPropertyAdequateForBuyout));
            System.Reflection.MethodInfo preDst =
                AccessTools.Method(typeof(TechBuyoutCascadePatches), nameof(HasPreTechUnlockedOrCascadable));
            System.Reflection.MethodInfo costDst =
                AccessTools.Method(typeof(TechBuyoutCascadePatches), nameof(AdequateForCascade));

            if (preSrc == null || costSrc == null || preDst == null || costDst == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "科技级联买断：解析不到要替换的方法，UI 那两道闸保持原版（引擎侧仍然生效）");

                return code;
            }

            var pre = 0;
            var cost = 0;

            for (var i = 0; i < code.Count; i++)
            {
                if (code[i].operand as System.Reflection.MethodInfo == preSrc)
                {
                    code[i].opcode = OpCodes.Call;
                    code[i].operand = preDst;
                    pre++;
                }
                else if (code[i].operand as System.Reflection.MethodInfo == costSrc)
                {
                    code[i].opcode = OpCodes.Call;
                    code[i].operand = costDst;
                    cost++;
                }
            }

            if (pre == 1 && cost == 1)
            {
                ProjectEdenPlugin.Log.LogInfo("科技级联买断：买断按钮的两道闸已改写（前置 1 处、花费 1 处）");

                return code;
            }

            ProjectEdenPlugin.Log.LogError(
                $"科技级联买断：买断按钮应当各改写 1 处，实际前置 {pre} 处、花费 {cost} 处——" +
                "**整体不改写**。引擎侧的前置补齐仍然生效，但按钮会照原版拦住，" +
                "表现为「点了弹前置未解锁」。游戏更新动过 UITechNode.OnBuyoutButtonClick 的话就是这里。");

            return instructions;
        }

        /// <summary>
        /// 开机状态行。**无论开关开没开都打一行**——「关着」和「这段代码没进 DLL」
        /// 在日志里必须分得开，本仓库为这条规矩付过七次学费。
        /// </summary>
        internal static void Report()
        {
            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogInfo("科技级联买断：读不到 tech.json，未启用");

                return;
            }

            if (!Config.buyoutCascade)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "科技级联买断：开关关着（tech.json 的 buyoutCascade），买断行为和原版一致");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "科技级联买断已启用：用元数据买断某个科技时，会**按拓扑序先把没解锁的前置逐级买断**，" +
                "每一级照常付元数据。付不起就停在那一级，已经买到的保留。" +
                "前置同时算 PreTechs 和 PreTechsImplicit，和原版 HasPreTechUnlocked 的判据一致。");
        }
    }

    /// <summary>
    /// <c>data/tech.json</c> 的映射类型。
    ///
    /// <para><b>单独一个文件</b>，理由和 cargoprobe.json / abnormality.json / planet.json 一样：
    /// <c>JsonHelper</c> 的磁盘覆盖<b>整文件生效</b>，为了翻一个 bool 去影子掉一份内容配置，
    /// 之后对内嵌那份的每一次修改都会被静默忽略。</para>
    /// </summary>
    [Serializable]
    internal class TechConfig
    {
        /// <summary>
        /// 买断科技时级联买断前置。**默认开。**
        ///
        /// <para>省的是点击次数，不是代价——每一级都照常付元数据。</para>
        /// </summary>
        public bool buyoutCascade;
    }
}
