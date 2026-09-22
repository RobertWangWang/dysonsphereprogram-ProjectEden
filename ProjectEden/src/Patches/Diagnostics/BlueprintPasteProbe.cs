// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

#pragma warning disable 649 // 配置字段由 JSON 反序列化赋值

using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches.Diagnostics
{
    /// <summary>
    /// 蓝图粘贴卡顿的分段计时。<b>默认关</b>，开关在 <c>blueprintprobe.json</c>。
    ///
    /// <para><b>起因。</b> 1.12.8 让重叠的建筑「复制几座就粘几座」之后，堆得多了粘贴会卡。
    /// 这不奇怪——在那之前原版把重叠预览全关掉了（只建一座），所以这条路上每个
    /// 以 <c>bpCursor</c> 为边界的循环，n 都是 1；现在 n 是几百。</para>
    ///
    /// <para><b>候选是枚举出来的，不是猜的。</b> 蓝图粘贴这条路上有四个方法带循环、
    /// 且循环边界读 <c>bpCursor</c>：</para>
    ///
    /// <list type="table">
    /// <item><term><c>CheckBuildConditions</c></term><description>10356 条指令、59 条回边、13 处读 bpCursor</description></item>
    /// <item><term><c>CreatePrebuilds</c></term><description>922 / 12 / 6</description></item>
    /// <item><term><c>ArrangeOverlapBP</c></term><description>700 / 5 / 4</description></item>
    /// <item><term><c>DeterminePreviewsPrestage</c></term><description>265 / 2 / 11</description></item>
    /// </list>
    ///
    /// <para><b>报 n、ms、ms/n 和 ms/n²，这是这个探针的重点。</b> 只报一个毫秒数没法区分
    /// 「线性但常数大」和「平方」，而这两者的对策完全不同——前者要减少每座的开销，
    /// 后者要换算法。<c>ms/n</c> 基本不变就是线性，<c>ms/n²</c> 基本不变就是平方。</para>
    ///
    /// <para><b>还要分清「移动光标就卡」和「按下去才卡」。</b> 前三段<b>每帧</b>都跑
    /// （蓝图挂在光标上就一直在跑），<c>CreatePrebuilds</c> 只在真正建造那一下跑。
    /// 两者的成因和对策都不一样，所以报告里分开列。</para>
    ///
    /// <para><b>尖峰立刻报，不等定时。</b> 卡顿是突发的：只按固定窗口汇总的探针会整段错过它——
    /// 本仓库为这条付过账（秒建那个探针要 10 秒窗口，而建造只持续几秒，前两次一行没打）。</para>
    ///
    /// <para><b>探针自己的开销。</b> 每帧四对时间戳，对着一个已经在花几十毫秒的帧，
    /// 是可以忽略的量级；重叠分组那一步是 O(n²)，所以<b>只在出报告时算一次</b>，
    /// 而不是每帧算——否则探针会变成它要测的那个问题。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class BlueprintPasteProbe
    {
        internal static BlueprintProbeConfig Config;

        /// <summary>一段的累计。</summary>
        private struct Stage
        {
            internal long Ticks;
            internal int Calls;
            internal long WorstTicks;
            internal int WorstN;

            internal void Add(long ticks, int n)
            {
                Ticks += ticks;
                Calls++;

                if (ticks <= WorstTicks) return;

                WorstTicks = ticks;
                WorstN = n;
            }

            internal void Reset()
            {
                Ticks = 0;
                Calls = 0;
                WorstTicks = 0;
                WorstN = 0;
            }
        }

        private static Stage _prestage, _arrange, _check, _create;

        private static float _nextReport;
        private static int _lastN;
        private static bool _sawActivity;

        /// <summary>
        /// 最近一次计时拿到的工具实例。<b>直接留住 postfix 手里那个，而不是去 player 那边找</b>——
        /// 按字段名找一个句柄是「按名字挑就会按名字漏」那一族，而且拿不到时这份报告会静默少一行，
        /// 看起来和「没有重叠」长得一样。这里留的就是刚刚被计时的那一个，不可能指错。
        /// </summary>
        private static BuildTool_BlueprintPaste _tool;

        private static bool On => Config != null && Config.enabled;

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// 开机状态行，<b>三种状态都打</b>：没配置 / 关着 / 开着。
        /// 只在开着时打的话，「关着」和「这段代码没进 DLL」在日志里长得一模一样——
        /// 本仓库为这条形状付过账不止一次。
        /// </summary>
        internal static void Report()
        {
            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogInfo("蓝图粘贴探针：没读到 blueprintprobe.json，不启用。");

                return;
            }

            if (!Config.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "蓝图粘贴探针：**关着**（默认）。粘贴堆叠建筑卡顿时，把 blueprintprobe.json 的 "
                    + "enabled 改成 true——它会把蓝图粘贴那四段分别计时，并报 n / ms / ms÷n / ms÷n²，"
                    + "好把「线性但常数大」和「平方」分开。");

                return;
            }

            ProjectEdenPlugin.Log.LogWarning(
                $"蓝图粘贴探针：**已开启**。单次超过 {Config.SpikeMs():0.#} 毫秒立刻报，"
                + $"否则每 {Config.ReportSeconds():0.#} 秒汇总一次（蓝图不在光标上时不打）。"
                + "四段分别是：预览准备 / 重叠归并 / 建造条件 / 生成预建物——前三段每帧都跑，"
                + "最后一段只在按下去那一下跑。");
        }

        // ── 四段计时。都是 prefix 记起点、postfix 记耗时 ──────

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.DeterminePreviewsPrestage))]
        private static void Prestage_Prefix(out long __state) => __state = On ? Stopwatch.GetTimestamp() : 0L;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.DeterminePreviewsPrestage))]
        private static void Prestage_Postfix(BuildTool_BlueprintPaste __instance, long __state)
            => Record(ref _prestage, __state, __instance);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.ArrangeOverlapBP))]
        private static void Arrange_Prefix(out long __state) => __state = On ? Stopwatch.GetTimestamp() : 0L;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.ArrangeOverlapBP))]
        private static void Arrange_Postfix(BuildTool_BlueprintPaste __instance, long __state)
            => Record(ref _arrange, __state, __instance);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
        private static void Check_Prefix(out long __state) => __state = On ? Stopwatch.GetTimestamp() : 0L;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
        private static void Check_Postfix(BuildTool_BlueprintPaste __instance, long __state)
            => Record(ref _check, __state, __instance, "建造条件");

        // ── 「建造条件」里面到底是谁，两个嫌疑分开计时 ────────────────────
        //
        // 实测一次粘贴 1,840 个预览花了 **109 秒**，每个预览 59.5 ms。读 IL 有两个嫌疑：
        //
        //   ① 站点邻距循环：@2C94 的 `if (desc.isStation)` 之后遍历全星球物流站
        //      （@2DDF / @2F17 / @301C 三重回跳）。本星球 9,326 个站 × 1,840 个预览
        //      = 1,720 万次——**但那只解释 6.4 µs/站，比距离平方比较慢三千倍**。
        //   ② Physics.OverlapBoxNonAlloc：每个预览一次，而场上有 1,840 个预览
        //      自己的碰撞体，一次返回上千个命中的宽相查询是微秒级的。
        //
        // **两个我分不出来，所以量。** 物理查询是 Unity 的静态方法，能直接挂钩；
        // 它占掉多少，剩下的就是站点循环那一侧。这一招这一轮已经连中四次
        // （CanBatch 四条、IsSteadyUnit 六条、extraTime、RebuildOne 三态）。
        internal static long PhysicsTicks;

        internal static int PhysicsCalls;

        internal static int PhysicsHits;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UnityEngine.Physics), nameof(UnityEngine.Physics.OverlapBoxNonAlloc),
            typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector3), typeof(UnityEngine.Collider[]),
            typeof(UnityEngine.Quaternion), typeof(int), typeof(UnityEngine.QueryTriggerInteraction))]
        private static void Overlap_Prefix(out long __state) => __state = On ? Stopwatch.GetTimestamp() : 0L;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UnityEngine.Physics), nameof(UnityEngine.Physics.OverlapBoxNonAlloc),
            typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector3), typeof(UnityEngine.Collider[]),
            typeof(UnityEngine.Quaternion), typeof(int), typeof(UnityEngine.QueryTriggerInteraction))]
        private static void Overlap_Postfix(long __state, int __result)
        {
            if (!On || __state == 0L) return;

            PhysicsTicks += Stopwatch.GetTimestamp() - __state;
            PhysicsCalls++;
            PhysicsHits += __result;
        }

        // ── 第二个嫌疑：AddErrorMessage ────────────────────────────────
        //
        // 物理查询量完了：342 秒里只占 **199 毫秒（0.06%）**，而站点邻距那一块跳过之后
        // 只省了约 6%。**两个嫌疑都排除了**，而两个数据点给出了真正的形状：
        //
        //   n=1,840 → 每个预览 59.5 毫秒
        //   n=3,364 → 每个预览 101.8 毫秒
        //   log(3.128)/log(1.828) = 1.89  →  **O(n²)**，里面有一层预览对预览的循环
        //
        // AddErrorMessage 是下一个嫌疑：3,364 个预览全被拒，而它如果对错误列表做
        // 线性去重（「这条消息已经有了吗」），就正好是每个预览 O(n)、总体 O(n²)。
        // 量它比再读一遍 10,356 条指令便宜得多，而这一轮我已经在这个方法上猜错两次了。
        internal static long ErrMsgTicks;

        internal static int ErrMsgCalls;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.AddErrorMessage))]
        private static void ErrMsg_Prefix(out long __state) => __state = On ? Stopwatch.GetTimestamp() : 0L;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.AddErrorMessage))]
        private static void ErrMsg_Postfix(long __state)
        {
            if (!On || __state == 0L) return;

            ErrMsgTicks += Stopwatch.GetTimestamp() - __state;
            ErrMsgCalls++;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CreatePrebuilds))]
        private static void Create_Prefix(out long __state) => __state = On ? Stopwatch.GetTimestamp() : 0L;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CreatePrebuilds))]
        private static void Create_Postfix(BuildTool_BlueprintPaste __instance, long __state)
            => Record(ref _create, __state, __instance, "生成预建物");

        private static void Record(ref Stage stage, long start, BuildTool_BlueprintPaste tool,
            string spikeLabel = null)
        {
            if (!On || start == 0L) return;

            long ticks = Stopwatch.GetTimestamp() - start;
            int n = tool?.bpCursor ?? 0;

            _lastN = n;
            _sawActivity = true;
            _tool = tool;

            stage.Add(ticks, n);

            double ms = Ms(ticks);

            // 尖峰立刻报。带标签的两段（最贵的那两段）才报，否则一次卡顿会刷四行。
            if (spikeLabel == null || ms < Config.SpikeMs()) return;

            ProjectEdenPlugin.Log.LogWarning(
                $"蓝图粘贴·尖峰：{spikeLabel} 一次 {ms:0.##} 毫秒，预览 {n} 个"
                + $"（每个 {Per(ms, n):0.####} 毫秒，n² 摊 {PerSq(ms, n):0.######} 毫秒）。"
                + "两个摊薄值里哪个在不同 n 之间基本不变，哪个就是它的复杂度。");
        }

        private static double Per(double ms, int n) => n > 0 ? ms / n : 0.0;

        private static double PerSq(double ms, int n) => n > 0 ? ms / ((double)n * n) : 0.0;

        /// <summary>
        /// 定时汇总，挂在 UI 帧上。用 <c>Time.realtimeSinceStartup</c> 而不是
        /// <c>GameMain.gameTick</c>——后者换存档会倒退，「下次报告时刻」就永远等不到，
        /// 报告悄无声息地死掉，而那看起来和「一切正常」一模一样。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), nameof(UIGame._OnUpdate))]
        private static void UIGame_OnUpdate_Postfix()
        {
            if (!On) return;

            float now = Time.realtimeSinceStartup;

            if (now < _nextReport) return;

            _nextReport = now + Config.ReportSeconds();

            // 蓝图不在光标上的时候什么都不打，否则一整局都在刷空行
            if (!_sawActivity) return;

            _sawActivity = false;

            int n = _lastN;

            // **先把「建造条件」跑没跑过记下来。** 下面那行的结论只有在它真的跑过时才成立，
            // 而 Line() 会把计数清零、且字符串拼接是从左到右求值的——所以必须在这之前取。
            //
            // 上一版没取，于是玩家只是把蓝图挂在光标上（没按下去）时，物理查询 0 次
            // 被印成了「那就说明全在站点邻距循环那一侧」——**一个把「压根没跑」
            // 当成「跑了但没命中」的读数，不是测量**。本文件为这条记过账。
            int checkCalls = _check.Calls;

            string PhysicsLine()
            {
                double ms = Ms(PhysicsTicks);
                int calls = PhysicsCalls;
                int hits = PhysicsHits;

                PhysicsTicks = 0;
                PhysicsCalls = 0;
                PhysicsHits = 0;

                if (checkCalls == 0)
                    return "  其中物理重叠查询：**本窗口「建造条件」根本没跑过，所以这一项什么也说明不了**"
                           + "——前两段是「蓝图挂在光标上」就会跑的，而建造条件只在**按下鼠标粘贴**那一下跑。"
                           + "要分清那几十秒花在哪，得真按一次。";

                if (calls == 0)
                    return "  其中物理重叠查询：建造条件跑了，而物理查询**一次都没有**——"
                           + "所以那些时间全在站点邻距循环那一侧"
                           + "（@2C94 的 isStation 之后遍历全星球物流站，本存档 9,326 个）。";

                return $"  其中物理重叠查询（Physics.OverlapBoxNonAlloc）：{calls} 次共 {ms:0.##} 毫秒，"
                       + $"平均每次 {ms / calls:0.####} 毫秒、返回 {(double)hits / calls:0.#} 个碰撞体。"
                       + "**拿它和上面「建造条件」那一行相减**，差额就是别处。";
            }

            string ErrMsgLine()
            {
                double ms = Ms(ErrMsgTicks);
                int calls = ErrMsgCalls;

                ErrMsgTicks = 0;
                ErrMsgCalls = 0;

                if (checkCalls == 0) return "  其中错误消息：（建造条件没跑过，无从谈起）";

                if (calls == 0)
                    return "  其中错误消息（AddErrorMessage）：**一次都没调**——那这一项排除，"
                           + "剩下的时间在别处。";

                return $"  其中错误消息（AddErrorMessage）：{calls} 次共 {ms:0.##} 毫秒，"
                       + $"平均每次 {ms / calls:0.####} 毫秒。"
                       + "**它如果占了大头，就是那层 O(n²)**——每个被拒的预览都要在错误列表里线性找一遍。";
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"── 蓝图粘贴分段（预览 {n} 个，重叠情况：{Overlaps(n)}）──\n"
                + Line("预览准备", ref _prestage, n) + "\n"
                + Line("重叠归并", ref _arrange, n) + "\n"
                + Line("建造条件", ref _check, n) + "\n"
                + Line("生成预建物", ref _create, n) + "\n"
                + PhysicsLine() + "\n"
                + ErrMsgLine() + "\n"
                + "  前三段**每帧都跑**（蓝图挂在光标上就一直跑），最后一段只在按下去那一下跑——"
                + "「移动光标就卡」和「按下去才卡」是两件事，对策也不一样。");

            _prestage.Reset();
            _arrange.Reset();
            _check.Reset();
            _create.Reset();
        }

        private static string Line(string label, ref Stage s, int n)
        {
            if (s.Calls == 0) return $"  {label}：本窗口没跑过";

            double total = Ms(s.Ticks);
            double worst = Ms(s.WorstTicks);

            return $"  {label}：{s.Calls} 次共 {total:0.##} 毫秒，最坏一次 {worst:0.##} 毫秒"
                   + $"（那次 n={s.WorstN}，每个 {Per(worst, s.WorstN):0.####}、n² 摊 {PerSq(worst, s.WorstN):0.######} 毫秒）";
        }

        /// <summary>
        /// 重叠分组：一共几组、最大一组几座。
        ///
        /// <b>这是 O(n²)，所以只在出报告时算一次</b>，不在每帧的计时路径上——
        /// 否则探针本身就变成了它要测的那个问题（本仓库为「探针和被测对象同量级」记过一次）。
        /// n 很大时直接放弃，宁可少一个数也不要让诊断自己制造卡顿。
        /// </summary>
        private static string Overlaps(int n)
        {
            BuildPreview[] pool = _tool?.bpPool;

            if (pool == null || n <= 0) return "没读到预览池";

            if (n > 3000) return $"{n} 个，太多就不分组了（这一步是 O(n²)，不值得为一行日志再卡一次）";

            int count = n < pool.Length ? n : pool.Length;
            var seen = new bool[count];
            var groups = 0;
            var largest = 1;
            var coincident = 0;

            for (var i = 0; i < count; i++)
            {
                if (seen[i] || pool[i] == null) continue;

                var size = 1;

                seen[i] = true;

                for (int j = i + 1; j < count; j++)
                {
                    if (seen[j] || pool[j] == null) continue;

                    // 原版 ArrangeOverlapBP 用的就是这个判据：0.5 米（sqrMagnitude < 0.25）
                    if ((pool[j].lpos - pool[i].lpos).sqrMagnitude >= 0.25f) continue;

                    seen[j] = true;
                    size++;
                }

                if (size <= 1) continue;

                groups++;
                coincident += size;

                if (size > largest) largest = size;
            }

            return groups == 0
                ? "没有重叠"
                : $"{groups} 组重叠、共 {coincident} 座，最大一组 {largest} 座";
        }

    }

    /// <summary>blueprintprobe.json 的结构。</summary>
    [Serializable]
    internal class BlueprintProbeConfig
    {
        public bool enabled;

        public float spikeMs;

        public float reportSeconds;

        internal float SpikeMs() => spikeMs > 0f ? spikeMs : 20f;

        internal float ReportSeconds() => reportSeconds >= 1f ? reportSeconds : 5f;
    }
}
