using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches.Diagnostics
{
    /// <summary>
    /// 把逐任务的 CPU 耗时**打进日志**，代替人肉抄「统计面板 → 性能测试」那十行。
    ///
    /// <b>为什么要它：这个仓库的全部诊断方法都建立在日志上，而唯一能回答「该优化哪里」
    /// 的那张表偏偏只在界面里。</b> 上一轮性能问题连猜六次全错，最后是靠分段计时量出来的。
    ///
    /// <b>它默认关闭，而且这不是保守——打开它真的要钱。</b> 见 <see cref="Arm"/>。
    ///
    /// <para><b>走的不是面板那条路，这一点是两次失败换来的。</b></para>
    ///
    /// 第一版只调 <c>PerformanceMonitor.SetCpuProfilerActive(true)</c> 然后读
    /// <c>timeCostsAve</c>，结果五次采样一字不差——那 8 条指令只是置一个 bool，
    /// 它不驱动任何东西。第二版自己每帧调 <c>SummarizeCpuStats</c>，结果全是 0。
    ///
    /// 真正的链条是（全部从 IL 读出来的）：
    /// <list type="number">
    /// <item><c>DeepProfiler.watchEnabled</c> 是总闸。每个 <c>Begin*/End*Sample</c> 的
    /// <b>第 0 条指令</b>就是 <c>ldsfld watchEnabled</c>，而
    /// <c>GameThreadController.LogicFrame</c> @005B–0060 每逻辑帧把它拷进
    /// <c>ThreadManager.samplePerformanceCounters</c>。它是 false 的时候，采样和计数器**两样都没有**。</item>
    /// <item>它唯一的外部写入点是 <c>DeepProfilerLateScript.LateUpdate</c> @00D5，
    /// 而那个脚本挂在 <c>UIRoot.instance.deepProfiler</c> 上、平时不活动——所以默认就是关的。</item>
    /// <item>逐任务耗时来自 <c>ThreadManager.performanceCountersOn*Task*</c>，
    /// 那是**和 DeepProfiler 采样池并列的另一套数据**：
    /// <c>PerformanceMonitor.GetThreadTaskTime_MainToAll</c> 只读这四个数组，
    /// 既不碰采样池，也不需要 <c>FrameEnd</c> 的时序。</item>
    /// </list>
    ///
    /// 所以这里只做两件事：把总闸打开，然后调原版自己的换算函数。**不自己计时**——
    /// 本仓库的 <c>MegaTickProfiler</c> 因为拿 <c>Stopwatch.Frequency</c> 当真，
    /// 报出过 12 倍的假数；这里的秒数是原版除以它自己的 <c>performanceFrequency</c> 得到的。
    /// </summary>
    [HarmonyPatch]
    internal static class CpuCostProbe
    {
        private static float _nextPoll;
        private static int _entered;
        private static bool _armed;

        /// <summary>
        /// 上一次的计数器指纹，**只为回答「这些数到底有没有在动」**。
        ///
        /// 第一版只在 <c>Total == 0</c> 时报警，于是五次一模一样的读数照样打了出来——
        /// 那只挡住「一个字都没采到」，挡不住「采过一次然后停了」，而后者才是默认状态。
        /// 判据必须是一个**不参与计算**的旁证：这里取几个任务的结束计数器之和，
        /// 它每帧都该变；不变就是没在采样。
        /// </summary>
        private static long _lastFingerprint = -1;

        /// <summary>要报哪些任务。名字是中文的，因为日志是中文的。</summary>
        private static readonly (EGameLogicTask Task, string Name)[] Watched =
        {
            (EGameLogicTask.Player, "伊卡洛斯"),
            (EGameLogicTask.GalacticTransport, "星际物流"),
            (EGameLogicTask.FactoryPowerSystem, "电力系统"),
            (EGameLogicTask.FactoryConstructionSystem, "建设系统"),
            (EGameLogicTask.FactoryTransportInput, "物流站·入库"),
            (EGameLogicTask.FactoryFacility, "生产设施"),
            (EGameLogicTask.FactoryLabResearch, "研究站·科研"),
            (EGameLogicTask.FactoryTransport, "物流运输"),
            (EGameLogicTask.FactoryLabOutput, "研究站·出货"),
            (EGameLogicTask.FactoryInserter, "分拣器"),
            (EGameLogicTask.FactoryStorage, "储物"),
            (EGameLogicTask.FactoryTank, "储液罐"),
            (EGameLogicTask.FactoryCargoPath, "传送带"),
            (EGameLogicTask.FactorySplitter, "分流器"),
            (EGameLogicTask.FactoryCargoTrafficMisc, "传送带附属设施"),
            (EGameLogicTask.FactoryTransportOutput, "物流站·出库"),
            (EGameLogicTask.FactoryPresentCargo, "货物呈现"),
            (EGameLogicTask.TrashSystem, "废弃物"),
            (EGameLogicTask.Statistics, "数据统计"),
        };

        /// <summary>
        /// 挂 <c>UIGame._OnUpdate</c> 的理由和普查同一条：要读
        /// <c>Time.realtimeSinceStartup</c>，那只在主线程可用；而节流不能用
        /// <c>GameMain.gameTick</c>，它换存档会倒退，报告会静默死掉（本仓库第 4 号坑）。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate_Cpu()
        {
            if (!_armed) return;

            if (Interlocked.Exchange(ref _entered, 1) == 0) Arm();

            float now = Time.realtimeSinceStartup;

            if (now < _nextPoll) return;

            _nextPoll = now + Config.perfProbeSeconds;

            Report();
        }

        /// <summary>
        /// 打开总闸。**这是有代价的**：<c>watchEnabled</c> 一开，全游戏所有
        /// <c>DeepProfiler.Begin*/End*Sample</c> 都会真的去取计数器，
        /// 而它们遍布逻辑帧和渲染路径。原版把它默认关掉不是随手为之。
        /// </summary>
        private static void Arm()
        {
            DeepProfiler.watchEnabled = true;
            PerformanceMonitor.SetCpuProfilerActive(true);

            ProjectEdenPlugin.Log.LogInfo(
                "CPU 分项耗时：已打开 DeepProfiler.watchEnabled（**真正的总闸**——"
                + "GameThreadController.LogicFrame 每逻辑帧把它拷进 ThreadManager.samplePerformanceCounters，"
                + "而每个 Begin/EndSample 的第 0 条指令就是读它）。"
                + $"之后每 {Config.perfProbeSeconds:0} 秒报一次。"
                + "**它自己也要钱**，量完请把 perfprobe.json 的 enabled 改回 false。");
        }

        private static void Report()
        {
            ThreadManager tm = GameMain.logic?.threadController?.threadManager;

            if (tm == null)
            {
                ProjectEdenPlugin.Log.LogInfo("CPU 分项耗时：还没进游戏（threadManager 是空的），本次跳过");

                return;
            }

            if (!tm.samplePerformanceCounters)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "CPU 分项耗时：ThreadManager.samplePerformanceCounters 仍是 false——"
                    + "GameThreadController.LogicFrame 每帧从 DeepProfiler.watchEnabled 拷过来，"
                    + "所以要么那一帧还没跑，要么有别的东西把 watchEnabled 关回去了。本次跳过");

                return;
            }

            // GetThreadTaskTime_MainToAll 读的是 instance.currentThreadManager，
            // 而那个字段平时由 SummarizeCpuStats 填——那条路我们不走，所以自己填。
            if (PerformanceMonitor.instance != null)
                PerformanceMonitor.instance.currentThreadManager = tm;

            // 换算要除以它。BeginRenderingFrame / ResetStatistics 会设，但两者都不保证跑过
            if (PerformanceMonitor.performanceFrequency <= 0.0)
                PerformanceMonitor.performanceFrequency = PerformanceCounter.Frequency;

            // **先回答「这些数在动吗」，再给数。** 指纹取几个任务的结束计数器之和——
            // 它和毫秒数无关，每帧都该变；不变就是没在采样，而那和「负载非常稳定」
            // 在日志里长得一模一样
            long fingerprint = Fingerprint(tm);

            if (fingerprint == _lastFingerprint)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"CPU 分项耗时：**计数器一个都没动**（指纹仍是 {fingerprint}）——"
                    + "线程管理器没有在写逐任务计数器，下面的数会是残值。本次不报表");

                return;
            }

            bool first = _lastFingerprint < 0;

            _lastFingerprint = fingerprint;

            var rows = new System.Collections.Generic.List<(string Name, double Ms)>();
            var sum = 0.0;

            foreach ((EGameLogicTask task, string name) in Watched)
            {
                double ms = PerformanceMonitor.GetThreadTaskTime_MainToAll(task) * 1000.0;

                if (ms <= 0.0005) continue;

                rows.Add((name, ms));
                sum += ms;
            }

            if (rows.Count == 0)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "CPU 分项耗时：计数器在动，但每一项都是 0——多半是任务枚举和线程管理器对不上"
                    + "（GameLogicTaskUtils.TaskIndex 返回的下标越界或全指向同一格）");

                return;
            }

            rows.Sort((a, b) => b.Ms.CompareTo(a.Ms));

            var sb = new StringBuilder();

            sb.Append("CPU 逐任务耗时（本帧，原版 GetThreadTaskTime_MainToAll 的口径，含工作线程）：")
              .Append(first ? "首次采样" : "计数器已更新")
              .Append("　合计 ").Append(sum.ToString("0.000")).Append(" ms");

            foreach ((string name, double ms) in rows)
                sb.Append('\n').Append("  ").Append(name).Append('：')
                  .Append(ms.ToString("0.000")).Append(" ms　（")
                  .Append((ms / sum * 100.0).ToString("0.0")).Append("%）");

            sb.Append("\n  判读：这是**一帧**的数，不是平均值，所以逐次会抖——看排序和占比，别抠小数。");
            sb.Append("一颗星球是一个并行工作项，单颗星球加核心没用；");
            sb.Append("要么这颗星球上的对象更少，要么把工厂摊到更多星球。");

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());

            // 紧跟着报「物流运输」那一栏的劈半，好让两张表说的是同一段时间。
            // 逐任务表只能告诉你哪个任务贵，而本 mod 有六个后置就挂在最贵的那个任务里面——
            // 那一刀原版的分析器结构上切不下去
            TransportSplitProbe.ReportWindow();
        }

        /// <summary>
        /// 一个**不参与计算**的活性指纹：几个任务的结束计数器之和。取多个是因为单个任务
        /// 可能这一帧恰好没活儿干。
        /// </summary>
        private static long Fingerprint(ThreadManager tm)
        {
            long[] ends = tm.performanceCountersOnWorkersTaskEnd;

            if (ends == null) return -1;

            long acc = 0;

            foreach ((EGameLogicTask task, string _) in Watched)
            {
                int idx = GameLogicTaskUtils.TaskIndex(task, tm);

                if (idx >= 0 && idx < ends.Length) acc += ends[idx];
            }

            return acc;
        }

        /// <summary>
        /// 开机状态行。**每一种状态都要打**——配置读不到、开关关着、挂点没挂上、已开启，
        /// 四种在日志里必须分得开，否则「探针没生效」和「这局没开」看起来一模一样。
        /// 本文件记过七次的那条。
        /// </summary>
        internal static void ReportStatus()
        {
            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogWarning("CPU 分项耗时：读不到 perfprobe.json，探针未启用");

                return;
            }

            if (!Config.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "CPU 分项耗时：已关闭（默认值）。它把逐任务的 CPU 耗时打进日志，"
                    + "省掉人肉抄「统计面板 → 性能测试」那十行。**默认关是因为它真的要钱**——"
                    + "它要打开 DeepProfiler.watchEnabled，而那是全游戏所有采样点的总闸。要量就改这里："
                    + $"{Utils.JsonHelper.OverridePath("perfprobe")}");

                return;
            }

            // 「开关开着」和「挂点真的接上了」是两回事，而症状一模一样：日志里没有那张表。
            // 判据只能是 Harmony 自己的补丁表——「我调了 PatchAll 而且没抛异常」不等于这一条挂上了
            var hooked = false;

            foreach (System.Reflection.MethodBase mb in Harmony.GetAllPatchedMethods())
                if (mb?.DeclaringType == typeof(UIGame) && mb.Name == "_OnUpdate")
                {
                    hooked = true;

                    break;
                }

            if (!hooked)
            {
                ProjectEdenPlugin.Log.LogError(
                    "CPU 分项耗时：开关是开的，但 UIGame._OnUpdate 的后置没挂上——这张表整局都不会出现");

                return;
            }

            _armed = true;

            ProjectEdenPlugin.Log.LogWarning(
                $"CPU 分项耗时：**已开启**，每 {Config.perfProbeSeconds:0} 秒一行。"
                + "它会打开 DeepProfiler.watchEnabled（全游戏采样点的总闸），本身有开销；"
                + "量完记得改回 false。");
        }

        internal static PerfProbeConfig Config { get; private set; }

        internal static void Load()
        {
            Config = Utils.JsonHelper.Load<PerfProbeConfig>("perfprobe");
        }
    }

    /// <summary>
    /// 单独一个文件，理由和 <c>cargoprobe.json</c> 一样：<c>JsonHelper</c> 的磁盘覆盖是
    /// **整份文件**的，把一个开发开关塞进 <c>stations.json</c> 就意味着为了翻一个 bool
    /// 而遮住整份物流配置，之后对内嵌那份的每一次修改都静默失效。
    /// </summary>
    internal class PerfProbeConfig
    {
        public bool enabled = false;
        public float perfProbeSeconds = 20f;
    }
}
