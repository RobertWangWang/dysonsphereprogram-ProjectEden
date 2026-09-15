using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 屏蔽原版的「数据异常」判定（<c>abnormality.json</c>，<b>默认开启</b>）。
    ///
    /// <b>先说清楚这不是一条作弊开关，虽然它长得像。</b>
    /// 原版有二十个 <c>ABN_*</c> 判定器，其中 <c>ABN_ProtoData.CheckProto</c> 干的事是：
    /// <code>
    /// if (!protoTable.Signature.Equals(ProtoSignature_0_10_30_3100.CalculateSignature(protoTable)))
    ///     abnormalData.TriggerAbnormality(protoId, 0, new long[] { protoType });
    /// </code>
    /// 也就是对 <c>LDB.items / techs / recipes / veges / veins</c> 各算一个签名，
    /// 和表里烘死的那个比。注册时机是 <c>onGameBegin</c> 和 <c>beforeGameSave</c>，
    /// 而 <c>minRecordVersion</c> 传的是 <b>0</b>，所以没有任何版本闸能挡住它。
    ///
    /// 本 mod 往 items 塞了近百个物品、往 recipes 塞了八十多个配方、往 veins 塞了九种矿脉，
    /// 这三张表的签名都不可能对上；<b>实测还有第四张：科技表</b>——没有任何地方「加」科技，
    /// 但集装等级和研究速度那几条科技的 <c>UnlockValues</c> 是就地改写的，而签名算的是表的
    /// <b>内容</b>不是长度，<b>改一条和加一条触发的是同一道检查</b>。每次读档各响四次。
    /// 这不是「你作弊了」，是「你装了内容 mod」，任何内容 mod 都一样。
    ///
    /// <b>被卡住的不只是成就。</b> <c>NothingAbnormal()</c> 有九个消费方（全程序集枚举出来的）：
    /// <list type="bullet">
    /// <item><c>AchievementLogic.get_active</c> —— 成就</item>
    /// <item><c>PropertyLogic.get_active</c> —— <b>元数据</b>，对玩家多半比 Steam 成就更疼</item>
    /// <item><c>GameSave.SaveCurrentGame</c> —— 干净存档才会去做银河系上传登录</item>
    /// <item><c>UIAchievementPanel</c> / <c>UIPropertyWindow</c> / <c>UIAbnormalityTip</c> /
    ///       <c>UIAbnormalityCheckInfo</c>（两处）—— 那几行警告</item>
    /// <item><c>TestAbnormalityCheck.Update</c> —— 第九个，官方留下的测试类，正常游戏里不跑</item>
    /// </list>
    ///
    /// <b>三处补丁，每一处都是从 IL 里找出来的收口点。</b>
    ///
    /// <b>(1) 前置 <c>TriggerAbnormality</c>。</b> 二十个判定器<b>全部</b>经由这一个方法落盘
    /// （枚举过：二十个 <c>ABN_*::Check*</c> / <c>On*</c> 无一例外），所以一个前置就够，
    /// 不需要逐个去关判定器。它管的是「以后不再往存档里记」。
    ///
    /// <b>(2) 后置 <c>NothingAbnormal</c> 返回 true。</b> 这一条管的是<b>已经被标脏的存档</b>——
    /// 而任何装过本 mod 的存档都已经被标脏了，所以<b>缺了它这个功能对老存档完全无效</b>，
    /// 正是本仓库反复记的「两道闸一个症状：清了第一道，看不出任何变化」。
    ///
    /// <b>而且它是非破坏性的，这是刻意选的。</b> <c>ClearAbnormality(0)</c> 能一把洗掉
    /// <c>runtimeDatas</c> 全部 3000 格（protoId 传 0 就走「整个数组」那条分支），
    /// 看着更彻底——但那是<b>不可逆</b>地改写玩家存档里的记录，而后置返回值同样覆盖全部九个
    /// 消费方，开关一关全都回来。<b>能用返回值解决的，就不要去改别人的数据。</b>
    ///
    /// <b>(3) 后置 <c>IsAbnormalTriggerred</c> 返回 false。</b> 成就面板 <c>_OnOpen</c> 里
    /// 除了 <c>NothingAbnormal()</c> 还有一次逐条查询，漏了它面板上会自相矛盾。
    ///
    /// <b>不要改成前置 <c>AbnormalityLogic.InitDeterminators</c> 返回 false。</b>
    /// 那个方法头两条指令才 <c>new</c> 出 <c>determinators</c> 字典（IL 0000–0006），
    /// 跳过它字典是 null，而 <c>AbnormalityLogic.GameTick</c> 第二条指令就
    /// <c>callvirt GetEnumerator()</c>——<b>每 tick 一个空引用</b>。
    /// 就算改成后置 <c>Clear()</c> 也<b>仍然不完整</b>：事件驱动的那几个判定器
    /// （<c>beforeGameSave</c> / <c>onGameBegin</c> / <c>onUnlockTech</c> / <c>onUseConsole</c>）
    /// 在 <c>DeterminatorBase.Init</c> 里已经订阅出去了，清字典拦不住它们——
    /// 而 <c>ABN_ProtoData</c>（我们唯一确定会踩的那个）恰恰就是事件驱动的。
    ///
    /// <b>沙盒模式不是绕路。</b> <c>TriggerAbnormality</c> 第一条指令确实是
    /// <c>if (gameDesc.isSandboxMode) return;</c>，但 <c>AchievementLogic.get_active</c>
    /// 里同样查 <c>isSandboxMode</c>，沙盒下成就照样关着。
    ///
    /// 同类 mod 的做法可以对照：soarqin/DSP_Mods 的 CheatEnabler 带 <c>AbnormalDisabler</c>，
    /// Acceyuriko 的 EnableAchievements 直接禁掉检查，PhantomGamers 的 AchievementsEnabler
    /// 则是<b>本地解锁但仍拦住往 Steamworks / Railworks 的上传</b>。
    /// <b>本开关不做最后那一层</b>：开着它，成就会真的进 Steam。
    /// </summary>
    [HarmonyPatch]
    internal static class AbnormalityPatches
    {
        private static bool Enabled => ProjectEdenPlugin.AbnormalityConfig?.enabled == true;

        /// <summary>
        /// 每个异常 proto 只报一次。<c>TriggerAbnormality</c> 的 protoId 被原版自己限在
        /// <c>(0, 100)</c>（IL 001D–0024），这里照同一个上界开数组。
        ///
        /// 用 <c>Interlocked</c> 而不是普通 bool：<c>ABN_MechaPosition</c> /
        /// <c>ABN_StarValue</c> / <c>ABN_RecipeUnlockCondition</c> 都从 <c>OnGameTick</c> 调过来，
        /// <b>而且必须在拼字符串之前认领</b>，否则字符串插值本身就落在 tick 路径上了。
        /// </summary>
        private static readonly int[] Seen = new int[100];

        /// <summary>
        /// 二十个判定器唯一的落盘入口。前置返回 false = 这次异常不进存档。
        ///
        /// 这里不复刻原版自己的那三道闸（沙盒、<c>checkVersion</c>、protoId 范围）——
        /// 它们的结论全都是「不要记」，和我们一致，所以没有分歧可言。
        /// 但 protoId 的范围<b>我们自己要查</b>：本前置跑在原版那道闸之前，
        /// 拿它去索引 <see cref="Seen"/> 必须先自己兜住。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ABN.GameAbnormalityData_0925),
                      nameof(ABN.GameAbnormalityData_0925.TriggerAbnormality))]
        private static bool TriggerAbnormality_Prefix(int protoId)
        {
            if (!Enabled) return true;

            ReportOnce(protoId);

            return false;
        }

        /// <summary>
        /// 九个消费方的总闸。<b>这一条才是让老存档也生效的那一条。</b>
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ABN.GameAbnormalityData_0925),
                      nameof(ABN.GameAbnormalityData_0925.NothingAbnormal))]
        private static void NothingAbnormal_Postfix(ref bool __result)
        {
            if (Enabled) __result = true;
        }

        /// <summary>
        /// 成就面板的逐条查询（<c>UIAchievementPanel._OnOpen</c>）。
        /// 漏了它，面板会一边说「一切正常」一边把某几条标成已触发。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ABN.GameAbnormalityData_0925),
                      nameof(ABN.GameAbnormalityData_0925.IsAbnormalTriggerred))]
        private static void IsAbnormalTriggerred_Postfix(ref bool __result)
        {
            if (Enabled) __result = false;
        }

        /// <summary>
        /// 报一行「我们到底踩了哪一条」。<b>这是这个补丁唯一产出的新信息</b>——
        /// 屏蔽之后玩家再也看不到游戏自己的那条警告，如果不打这行，
        /// 「本 mod 触发了哪些判定」就变成一个谁也答不上来的问题了。
        /// </summary>
        private static void ReportOnce(int protoId)
        {
            if (protoId <= 0 || protoId >= Seen.Length) return;
            if (Interlocked.Exchange(ref Seen[protoId], 1) != 0) return;

            string name = protoId.ToString();
            string determinator = "?";

            AbnormalityProto proto = LDB.abnormalities?.Select(protoId);

            if (proto != null)
            {
                // 比的是 Name（原始键）不是 name（翻译后），本仓库的老规矩
                if (!string.IsNullOrEmpty(proto.Name)) name = $"{proto.Name}({protoId})";
                if (!string.IsNullOrEmpty(proto.DeterminatorName)) determinator = proto.DeterminatorName;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"数据异常屏蔽：拦下一条 {name}，判定器 {determinator}。" +
                "**已拦下，没有写进存档，成就和元数据不受影响。** " +
                "装了内容 mod 必然会踩 ABN_ProtoData（物品/科技/配方/矿脉四张表的签名都变了——" +
                "科技那张不是加出来的，是集装等级等几条科技的 UnlockValues 被就地改写），" +
                "那几条是误伤不是作弊。要放行原版判定就把 abnormality.json 的 enabled 改成 false。");
        }
    }
}
