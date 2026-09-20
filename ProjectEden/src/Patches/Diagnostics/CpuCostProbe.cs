using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches.Diagnostics
{
    /// <summary>
    /// 把「统计面板 → 性能测试」那张分项耗时表**打进日志**。
    ///
    /// <b>为什么要它：这个仓库的全部诊断方法都建立在日志上，而唯一能回答「该优化哪里」
    /// 的那张表偏偏只在界面里。</b> 上一轮性能问题连猜六次全错，最后是靠分段计时量出来的；
    /// 而要人肉抄十行毫秒数再贴回来，每问一次就是一个来回。
    ///
    /// <b>它默认关闭，而且这不是保守，是因为打开它真的要钱。</b>
    /// <c>PerformanceMonitor.SummarizeCpuStats</c> 开头就是 <c>if (!CpuProfilerOn) return;</c>，
    /// 所以原版在你翻开那一页之前一分钱都不花（<c>UIPerformancePanel._OnOpen</c> 的
    /// 第一件事就是 <c>SetCpuProfilerActive(true)</c>）。本探针打开 = 常驻开着那一页。
    ///
    /// <b>单位是量出来的，不是猜的。</b> <c>timeCostsAve</c> 里存的是
    /// 「计数器差 ÷ <c>performanceFrequency</c>」（<c>SummarizeCpuStats</c> @0137–013E），
    /// 而 <c>performanceFrequency = PerformanceCounter.Frequency</c>
    /// （<c>BeginRenderingFrame</c> @0000–0006），即每秒计数——所以那个 Double 的单位是**秒**，
    /// ×1000 才是面板上的毫秒。
    ///
    /// **这里刻意不自己计时。** 本仓库的 <c>MegaTickProfiler</c> 因为拿
    /// <c>Stopwatch.Frequency</c> 当真，报出过 12 倍的假数；这里读的是原版自己算好、
    /// 自己显示的那个值，和玩家在面板上看到的是同一个数，没有第二套口径可以对不上。
    /// </summary>
    [HarmonyPatch]
    internal static class CpuCostProbe
    {
        private static float _nextPoll;
        private static int _entered;
        private static bool _armed;

        /// <summary>
        /// 上一次采到的 <c>Total</c> 和 <c>aveFrame</c>，只为回答「这张表到底有没有在动」。
        ///
        /// <b>这一对是被自己坑了一次之后补的。</b> 第一版只在 <c>Total == 0</c> 时报警，
        /// 结果五次采样打出**一模一样**的 22.398 / 12.409 / 5.889 / 5.207——而那两种情况
        /// 探针分不开：滑动平均的窗口太长（数是真的，只是阻尼大），还是
        /// <c>SummarizeCpuStats</c> 压根没被调（数是死的）。
        ///
        /// 判据来自它唯一的调用者：<c>PerformanceMonitor.SummarizeCpuStats</c> 全程序集**只有一处**
        /// 调用，在 <c>DeepProfilerLateScript.LateUpdate</c> @005F，而那个脚本挂在
        /// <c>UIRoot.instance.deepProfiler</c> 上——它没在跑，这张表就永远停在最后一次的值上。
        /// <c>aveFrame</c> 是 <c>SlideAverage</c> 每次进来就 +1 的计数器，所以**它不动 = 没在采样**，
        /// 这是个和被测值无关的独立信号。
        /// </summary>
        private static double _lastTotal = -1;
        private static long _lastAveFrame = -1;

        /// <summary>上一次替原版跑汇总的那一帧，用来保证每帧只跑一次（aveFrame 是逐次 +1 的）。</summary>
        private static int _lastFrameSummarized = -1;

        /// <summary>
        /// 只报这些分项。**不是全部 48 个**——一屏几十行等于没报，而这十几个正是
        /// <c>行星工厂</c> 那棵树上真正会动的叶子，和普查那张对象数量表一一对应。
        /// </summary>
        private static readonly ECpuWorkEntry[] Watched =
        {
            ECpuWorkEntry.Total,
            ECpuWorkEntry.LogicTick,
            ECpuWorkEntry.Icarus,
            ECpuWorkEntry.LogisticsSchedule,
            ECpuWorkEntry.PlanetFactory,
            ECpuWorkEntry.PowerSystem,
            ECpuWorkEntry.ConstructionSystem,
            ECpuWorkEntry.LogisticsTransport,
            ECpuWorkEntry.Facilities,
            ECpuWorkEntry.Inserters,
            ECpuWorkEntry.CargoPaths,
            ECpuWorkEntry.CargoTrafficMisc,
            ECpuWorkEntry.StorageSystem,
            ECpuWorkEntry.DysonSphere,
            ECpuWorkEntry.Statistics,
            ECpuWorkEntry.Trash,
        };

        /// <summary>
        /// 挂 <c>UIGame._OnUpdate</c> 的理由和普查同一条：要读
        /// <c>Time.realtimeSinceStartup</c>，那只在主线程可用；而节流不能用
        /// <c>GameMain.gameTick</c>，它换存档会倒退，报告会静默死掉。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate_Cpu()
        {
            if (!_armed) return;

            if (Interlocked.Exchange(ref _entered, 1) == 0)
            {
                PerformanceMonitor.SetCpuProfilerActive(true);

                ProjectEdenPlugin.Log.LogInfo(
                    "CPU 分项耗时：分析器已打开，并且**每帧替原版跑一次汇总**"
                    + "（SummarizeCpuStats 平时只有 DeepProfilerLateScript.LateUpdate 会调，"
                    + "而那个脚本不活动——只置 CpuProfilerOn 的话，读到的是最后一次汇总的残值）。"
                    + $"之后每 {Config.perfProbeSeconds:0} 秒报一次。"
                    + "**它自己也要钱**，量完请把 perfprobe.json 的 enabled 改回 false。");
            }

            // **汇总这一步得我们自己驱动，采样不用。**
            // DeepProfiler.Init() 由 GlobalObject.Initialize 调，采样本身
            //（DeepProfiler.FrameStart/FrameEnd + 三个相机的 OnPreCull → BeginMajorSample）
            // 每帧都在跑，和任何界面无关。被卡住的只有 SummarizeCpuStats——它全程序集只有
            // 一处调用，在 DeepProfilerLateScript.LateUpdate，而那个脚本挂在
            // UIRoot.instance.deepProfiler 上、平时不活动。
            //
            // 实测后果：aveFrame 停在 30（窗口 60 帧），也就是说读出来的是**读档期间那 30 帧
            // 的残值**，而它看起来是一组非常稳定、非常可信的毫秒数。
            //
            // 每帧只调一次：aveFrame 是每次进来就 +1 的，调两次就等于把同一帧的数据
            // 当成两帧塞进滑动平均。
            int fc = Time.frameCount;

            if (fc != _lastFrameSummarized)
            {
                _lastFrameSummarized = fc;
                PerformanceMonitor.SummarizeCpuStats();
            }

            float now = Time.realtimeSinceStartup;

            if (now < _nextPoll) return;

            _nextPoll = now + Config.perfProbeSeconds;

            double[] ave = PerformanceMonitor.timeCostsAve;

            if (ave == null || ave.Length < (int)ECpuWorkEntry.Max)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "CPU 分项耗时：timeCostsAve 还没建好（PerformanceMonitor.Awake 没跑过？），这次跳过");

                return;
            }

            // Total 为 0 = 分析器实际上没在采样。**必须把这种情况说出来**，
            // 否则「全是 0」和「这一帧真的不花时间」在日志里长得一模一样。
            double total = ave[(int)ECpuWorkEntry.Total] * 1000.0;

            if (total <= 0.0)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "CPU 分项耗时：分析器开着但 Total 是 0——多半是还没进游戏，或者 DeepProfiler 没初始化。"
                    + "SummarizeCpuStats 开头两道闸是 CpuProfilerOn 和 DeepProfiler.inited");

                return;
            }

            // **先回答「这张表在动吗」，再给数。** aveFrame 是 SlideAverage 每次进来就 +1 的
            // 计数器，和被测的毫秒数完全无关——它不动就说明 SummarizeCpuStats 没被调，
            // 那么下面每一行都是上一次的残值，而残值和「负载稳定」长得一模一样。
            long aveFrame = PerformanceMonitor.aveFrame;
            long aveWindow = PerformanceMonitor.aveWindow;
            long frames = _lastAveFrame < 0 ? -1 : aveFrame - _lastAveFrame;

            if (frames == 0)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"CPU 分项耗时：**这张表仍然没有在更新**（aveFrame 停在 {aveFrame}）。"
                    + "本探针已经每帧替原版调了 SummarizeCpuStats，它还是不动，"
                    + "那就是方法开头那四道闸里有一道没过：CpuProfilerOn（我们置过）、"
                    + "GameMain.logic 非空、DeepProfiler.inited、threadController.threadManager 非空。"
                    + "本次不报表——残值和「负载非常稳定」长得一模一样。");

                _lastAveFrame = aveFrame;

                return;
            }

            var sb = new StringBuilder();

            sb.Append("CPU 分项耗时（原版自己的口径，和「统计面板 → 性能测试」同一个数）：");

            // 把「采了多少帧」和「和上次比变了没有」摆在最前面：读数能不能信，先于读数本身
            sb.Append("\n  本次采样：距上次 ").Append(frames < 0 ? "首次" : frames + " 帧")
              .Append("　平均窗口 ").Append(aveWindow).Append(" 帧");

            if (_lastTotal >= 0)
            {
                double delta = total - _lastTotal;

                sb.Append("　总计较上次 ").Append(delta >= 0 ? "+" : "").Append(delta.ToString("0.000")).Append(" ms");

                if (System.Math.Abs(delta) < 1e-9)
                    sb.Append("　⚠ **一点没变**——滑动平均阻尼太大或者根本没在采样，"
                              + "这一栏的数别当实测用");
            }

            _lastTotal = total;
            _lastAveFrame = aveFrame;

            // 缩进照原版的层级表来，读起来就是面板上那棵树；`行星工厂` 是容器不是开销，
            // 它等于底下那几行之和（本仓库已经在这上面栽过一次）
            foreach (ECpuWorkEntry e in Watched)
            {
                var idx = (int)e;
                double ms = ave[idx] * 1000.0;

                if (ms <= 0.0005) continue;

                int level = PerformanceMonitor.cpuWorkLevels != null
                            && idx < PerformanceMonitor.cpuWorkLevels.Length
                    ? PerformanceMonitor.cpuWorkLevels[idx]
                    : 0;

                string name = PerformanceMonitor.cpuWorkNames != null
                              && idx < PerformanceMonitor.cpuWorkNames.Length
                    ? PerformanceMonitor.cpuWorkNames[idx]
                    : e.ToString();

                sb.Append('\n').Append(' ', 2 + level * 2)
                  .Append(name).Append('：').Append(ms.ToString("0.000")).Append(" ms");

                if (total > 0.0) sb.Append("　（").Append((ms / total * 100.0).ToString("0.0")).Append("%）");

                // 本帧值。**平均值阻尼太大时，只有它会动**——两个数并排摆着，
                // 「这张表死了」和「负载真的很稳」就不再需要猜
                double[] frame = PerformanceMonitor.timeCostsFrame;

                if (frame != null && idx < frame.Length)
                    sb.Append("　本帧 ").Append((frame[idx] * 1000.0).ToString("0.000"));
            }

            sb.Append("\n  判读：**「行星工厂」是容器不是开销**，它等于底下那几行之和。");
            sb.Append("一颗星球是一个并行工作项，所以单颗星球加核心没用——");
            sb.Append("要么这颗星球上的对象更少，要么把工厂摊到更多星球。");

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>
        /// 开机状态行。**每一种状态都要打**——配置读不到、开关关着、已打开，三种在日志里
        /// 必须分得开，否则「探针没生效」和「这局没开」看起来一模一样。本文件记过七次的那条。
        /// </summary>
        internal static void Report()
        {
            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "CPU 分项耗时：读不到 perfprobe.json，探针未启用");

                return;
            }

            if (!Config.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "CPU 分项耗时：已关闭（默认值）。它把「统计面板 → 性能测试」那张表打进日志，"
                    + "省掉人肉抄十行毫秒数。**默认关是因为它真的要钱**——原版在你翻开那一页之前"
                    + "根本不采样。要量就改这里："
                    + $"{Utils.JsonHelper.OverridePath("perfprobe")}");

                return;
            }

            // 「开关开着」和「挂点真的接上了」是两回事，而它们的症状一模一样：日志里没有那张表。
            // 判据只能是 Harmony 自己的补丁表——「我调了 PatchAll 而且没抛异常」不等于这一条挂上了
            // （参数名写错会让它后面的整批补丁被跳过，本文件记过那个坑）。
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
                + "这会让 CPU 分析器常驻，本身有开销；量完记得改回 false。");
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
