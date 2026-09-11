using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 回答一个离线查不出来的问题：<b>编译好的货物着色器到底拿 <c>Cargo.inc</c> 干什么。</b>
    ///
    /// <b>为什么只剩做实验这一条路。</b> <c>CargoContainer.Draw</c> 一共只往材质上挂三样东西——
    /// <c>_Buffer</c>（整个 <c>Cargo[]</c> 裸传）、<c>_MainTex</c>（图标图集）、<c>_IndexBuffer</c>。
    /// 没有第二条通道：<c>Cargo.fastIncArrowTable</c> 那张「增产箭头」表<b>只有 UI 类在读</b>
    /// （UIItemTip / UIBeltWindow / UIStorageGrid …），一次也没上过 GPU，
    /// 全程也没有任何 <c>SetGlobal*</c> 传过增产数据。
    /// 所以着色器关于增产的全部信息就是那一个 <c>inc</c> 字节，
    /// 而着色器是编译过的资产，读不出源码。
    ///
    /// <b>怎么让画面静下来——这里踩过一个坑，值得记住。</b>
    /// 第一版用的是「游戏暂停」，理由看着很充分：暂停时工厂不 tick，改 <c>cargoPool</c> 只影响渲染。
    /// <b>但暂停的时候工厂根本不画。</b> <c>FactoryModel.OnCameraPostRender</c> 的<b>第一条指令</b>就是：
    /// <code>
    ///   0000: call GameMain::get_isPaused()
    ///   0005: brfalse.s IL_0008     // 没暂停才往下走
    ///   0007: ret                    // 暂停就直接返回
    /// </code>
    /// <c>CargoContainer.Draw</c> 挂在它下游（<c>DrawInstancedBatches</c>），于是暂停期间货物压根不上屏，
    /// 写进 <c>cargoPool</c> 的任何东西都到不了 GPU。
    ///
    /// 正确的冻结方式是 <b><c>Time.timeScale = 0</c></b>：工厂 tick 挂在
    /// <c>GameMain.FixedUpdate → GameLogic.LogicFrame()</c> 上，timeScale 归零 Unity 就不再调 FixedUpdate，
    /// 货物位置冻住；而渲染走 <c>Update</c> / <c>OnCameraPostRender</c>，不受 timeScale 影响，
    /// <c>_paused</c> 也仍然是 false，工厂照画。要的正是这个组合。
    ///
    /// <b>设计里最重要的是阳性对照。</b> 只测「改 inc 画面变不变」是不够的——
    /// 摄像机没对着传送带、星球没加载、抓帧失败，统统表现为「没变化」，
    /// 和「着色器不读 inc」长得一模一样。所以先整体平移一次货物位置：
    /// 那个<b>必然</b>改变画面，它要是也没动静，就说明这次采样无效，<b>不下结论</b>，隔几秒重试。
    /// 上面那个暂停坑就是被这条拦下来的——否则它会理直气壮地报「着色器不读 inc」。
    ///
    /// <b>怎么区分 f(inc) 和 f(inc/stack)。</b> 在两个不同的 <c>stack</c> 下各扫一遍 inc，
    /// 找出画面开始变化的那个 inc 阈值：阈值不随 stack 动 → 着色器读的是<b>原始 inc</b>；
    /// 阈值大致按 stack 成比例放大 → 着色器自己做了 <c>inc / stack</c>。
    ///
    /// <b>结论用在哪。</b> 只对<b>方案一</b>（inc 改存每件速率）有意义——那会让着色器看到的字节
    /// 含义整个变掉。<b>方案二</b>（加宽 Int16 + 32 字节重打包）不需要这个答案：
    /// 重打包时把 <c>min(255, 真值)</c> 写进渲染副本，着色器拿到的字节和今天<b>逐位相同</b>。
    /// </summary>
    [HarmonyPatch]
    internal static class CargoShaderIncProbe
    {
        private static bool Enabled => ProjectEdenPlugin.CargoProbeConfig?.enabled == true;

        /// <summary>扫哪些 inc。0 是每组的参照帧，其余都跟它比</summary>
        private static readonly int[] IncLadder = { 0, 1, 2, 3, 4, 8, 16, 32, 64, 128, 200, 252, 255 };

        /// <summary>在这两个层数下各扫一遍。两次阈值的比值就是答案</summary>
        private static readonly int[] StackLadder = { 1, 50 };

        /// <summary>判定「画面动了」的倍数，相对<b>掩膜内</b>的噪声底。取高一点，宁可漏报不误报</summary>
        private const double SignalRatio = 5.0;

        /// <summary>算作「这个像素变了」的通道差。低于它的当成抖动</summary>
        private const int PixelDelta = 24;

        /// <summary>掩膜至少要这么多像素，实验才算得上有样本</summary>
        private const int MinMaskPixels = 100;

        /// <summary>信号的绝对下限。掩膜内噪声可能接近 0，光靠倍数会把抖动放大成「信号」</summary>
        private const double SignalFloor = 1.5;

        /// <summary>采样无效时隔多久再试一次（真实秒，timeScale 归零也照走）</summary>
        private const float RetrySeconds = 5f;

        private static bool _started;
        private static bool _done;

        /// <summary>无效采样已经报过几次。前三次每次都报，之后每十次报一次，免得刷屏又不至于彻底沉默</summary>
        private static int _complained;

        // ── 触发：进游戏就挂上，条件够了自己跑 ────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameMain), nameof(GameMain.LateUpdate))]
        private static void GameMain_LateUpdate()
        {
            if (!Enabled || _started || _done) return;
            if (GameMain.instance == null || GameMain.localPlanet == null) return;

            _started = true;

            var host = new GameObject("ProjectEdenCargoShaderProbe") { hideFlags = HideFlags.HideAndDontSave };

            UnityEngine.Object.DontDestroyOnLoad(host);

            host.AddComponent<Host>().StartCoroutine(Driver(host));
        }

        /// <summary>协程宿主。用自己的 GameObject，免得依赖别人的生命周期</summary>
        private class Host : MonoBehaviour { }

        /// <summary>
        /// 反复尝试，直到<b>阳性对照通过</b>为止。
        /// 这样玩家什么都不用按——走到一条有货的传送带前、让画面里看得见货物就行。
        /// </summary>
        private static IEnumerator Driver(GameObject host)
        {
            ProjectEdenPlugin.Log.LogInfo(
                "着色器 inc 探针：已挂上，正在等一个能看见传送带货物的画面。" +
                "走到一条有货的传送带前、让摄像机对着它就行，不用按任何键。");

            while (!_done)
            {
                yield return new WaitForSecondsRealtime(RetrySeconds);

                if (!Ready(out CargoContainer container)) continue;

                yield return Attempt(container);
            }

            UnityEngine.Object.Destroy(host);
        }

        /// <summary>现在这一刻适不适合做实验。暂停时工厂不画，所以暂停期间必须跳过。</summary>
        private static bool Ready(out CargoContainer container)
        {
            container = null;

            // 加宽之后本文件里的 byte[] 快照跟 Cargo.inc 的真实宽度对不上，
            // 而且它要回答的问题已经答完了（着色器不读 inc）。
            // 守卫放在这里而不是 Snapshot 里：那几个方法就永远不会被 JIT。
            if (CargoWidening.IsActive) return false;

            if (GameMain.instance == null || GameMain.isPaused || GameMain.inOtherScene) return false;

            container = GameMain.localPlanet?.factory?.cargoTraffic?.container;

            return container?.cargoPool != null && container.cursor > 1;
        }

        // ── 实验本体 ──────────────────────────────────────────

        private static IEnumerator Attempt(CargoContainer container)
        {
            int w = Mathf.Max(160, Mathf.Min(960, Screen.width / 2));
            int h = Mathf.Max(90, Mathf.Min(540, Screen.height / 2));

            RenderTexture rt = null;
            Texture2D tex = null;

            int[] ids = null;
            byte[] stack0 = null;
            byte[] inc0 = null;
            Vector3[] pos0 = null;

            float timeScale0 = Time.timeScale;

            var report = new StringBuilder();

            try
            {
                rt = new RenderTexture(w, h, 0) { hideFlags = HideFlags.HideAndDontSave };
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };

                Snapshot(container, out ids, out stack0, out inc0, out pos0);

                if (ids.Length == 0) yield break;

                // 冻结工厂但保留渲染：tick 在 FixedUpdate 上，渲染不在
                Time.timeScale = 0f;

                // 丢掉第一帧：RenderTexture 这时还没被写过，读出来是未初始化内容。
                // 第一版没丢，于是「基准帧」是一坨垃圾，跟后面每一帧都差得一样多——
                // 噪声底和阳性对照因此打印出**逐位相同**的数值，那正是上一次失败的指纹。
                Color32[] discard = null;

                yield return Grab(rt, tex, x => discard = x);

                Color32[] baseA = null;
                Color32[] baseB = null;

                yield return Grab(rt, tex, x => baseA = x);
                yield return Grab(rt, tex, x => baseB = x);

                // —— 阳性对照：整体平移货物。它同时干两件事 ——
                // 1) 证明画面里确实看得见货物（看不见就不下结论）
                // 2) **圈出货物在屏幕上的像素**，后面所有测量都只在这个掩膜里做
                //
                // 第二件事才是关键。52 个货物盒子在 960×540 里只占千分之几，
                // 拿整幅图的均值当度量，货物就算整个消失也淹没在噪声里——
                // 上一次失败正是这样：阳性对照 1.0494 对噪声底 1.0607，比噪声还低。
                for (var i = 0; i < ids.Length; i++)
                    container.cargoPool[ids[i]].position = pos0[i] + new Vector3(0f, 200f, 0f);

                Color32[] moved = null;

                yield return Grab(rt, tex, x => moved = x);

                for (var i = 0; i < ids.Length; i++) container.cargoPool[ids[i]].position = pos0[i];

                bool[] mask = Mask(baseA, moved, out int maskPixels);

                if (maskPixels < MinMaskPixels)
                {
                    _complained++;

                    if (_complained <= 3 || _complained % 10 == 0)
                        ProjectEdenPlugin.Log.LogInfo(
                            $"着色器 inc 探针：第 {_complained} 次采样无效——把货物整体挪走，只有 {maskPixels} 个像素变化" +
                            $"（至少要 {MinMaskPixels} 个）。画面里现在基本看不见传送带货物。" +
                            $"走近一条有货的传送带、让它占住一部分屏幕，{RetrySeconds:F0} 秒后自动重试。");

                    yield break;
                }

                double noise = DiffIn(baseA, baseB, mask, maskPixels);

                report.AppendLine($"着色器 inc 探针：样本 {ids.Length} 件货，抓帧 {w}×{h}");
                report.AppendLine($"  阳性对照圈出货物像素 {maskPixels} 个 ← 采样有效，以下全部只在这些像素里测");
                report.AppendLine($"  掩膜内噪声底（静止两帧之差）= {noise:F4}");

                // —— 主实验：两个 stack 下各扫一遍 inc ——
                var threshold = new double[StackLadder.Length];

                for (var s = 0; s < StackLadder.Length; s++)
                {
                    int st = StackLadder[s];

                    threshold[s] = -1;

                    Write(container, ids, (byte)st, 0);

                    Color32[] refFrame = null;

                    yield return Grab(rt, tex, x => refFrame = x);

                    report.AppendLine();
                    report.AppendLine($"  stack = {st}：");

                    foreach (int inc in IncLadder)
                    {
                        if (inc == 0) continue;

                        Write(container, ids, (byte)st, (byte)inc);

                        Color32[] frame = null;

                        yield return Grab(rt, tex, x => frame = x);

                        double d = DiffIn(refFrame, frame, mask, maskPixels);
                        bool hit = d > Math.Max(noise * SignalRatio, SignalFloor);

                        if (hit && threshold[s] < 0) threshold[s] = inc;

                        report.AppendLine($"    inc={inc,3}  差异={d,8:F4}  {(hit ? "← 变了" : "")}");
                    }
                }

                report.AppendLine();
                report.AppendLine(Verdict(threshold));

                ProjectEdenPlugin.Log.LogWarning(report.ToString());

                _done = true;
            }
            finally
            {
                // 无论怎么退出都写回去：这是直接改渲染数据的代码，绝不能留痕
                if (ids != null) Restore(container, ids, stack0, inc0, pos0);

                Time.timeScale = timeScale0;

                if (tex != null) UnityEngine.Object.Destroy(tex);
                if (rt != null) UnityEngine.Object.Destroy(rt);
            }
        }

        /// <summary>从阈值随 stack 怎么动，读出着色器用的是哪个量。</summary>
        private static string Verdict(double[] threshold)
        {
            double t1 = threshold[0];
            double t2 = threshold[1];

            int s1 = StackLadder[0];
            int s2 = StackLadder[1];

            if (t1 < 0 && t2 < 0)
                return "  结论：**着色器不读 inc**。两个 stack 下把 inc 从 0 拉到 255，画面都没超过噪声底。\n" +
                       "  → 方案一（inc 改存每件速率）在画面上是免费的，传送带外观不依赖 inc。\n" +
                       "  注意这只说明「传送带上的货物」不受影响；界面里的增产箭头走的是另一条路，\n" +
                       "  读的是 Cargo.fastIncArrowTable，按 inc/stack 还原每件等级，不受渲染结论影响。";

            if (t1 < 0 || t2 < 0)
                return $"  结论：**不确定**。stack={s1} 阈值 {(t1 < 0 ? "未触发" : t1.ToString())}，" +
                       $"stack={s2} 阈值 {(t2 < 0 ? "未触发" : t2.ToString())}。\n" +
                       "  只有一边触发，阈值多半落在梯子的格子之间。把 IncLadder 加密一点重跑。";

            double ratio = t2 / t1;
            double stackRatio = (double)s2 / s1;

            if (ratio >= stackRatio * 0.5)
                return $"  结论：**着色器读的是 inc/stack（每件等级）**。阈值 {t1} → {t2}，" +
                       $"随 stack（{s1} → {s2}，×{stackRatio:F0}）成比例放大了 ×{ratio:F1}。\n" +
                       "  → 方案一可行，而且语义天然：着色器本来就在还原「每件多少级」。\n" +
                       "    但 inc 改存每件速率之后，得把着色器那次除法补回去，\n" +
                       "    否则等级会被再除一次 stack，堆得越高看起来喷得越淡。";

            return $"  结论：**着色器读的是原始 inc（整堆总点数）**。阈值 {t1} → {t2}，" +
                   $"stack 放大 ×{stackRatio:F0} 而阈值几乎没动（×{ratio:F1}）。\n" +
                   "  → 方案一会改变传送带外观：inc 从「整堆 ≤252」变成「每件 ≤4」，\n" +
                   "    着色器按原始值判断，喷过的货会看起来像没喷。要走方案一就得同时接管渲染，\n" +
                   "    那已经是方案二的工作量了——这种情况下直接选方案二。";
        }

        // ── 小工具 ────────────────────────────────────────────

        private static void Snapshot(CargoContainer c, out int[] ids, out byte[] stack, out byte[] inc, out Vector3[] pos)
        {
            var list = new List<int>();

            for (var i = 1; i < c.cursor && i < c.cargoPool.Length; i++)
                if (c.cargoPool[i].item > 0) list.Add(i);

            ids = list.ToArray();
            stack = new byte[ids.Length];
            inc = new byte[ids.Length];
            pos = new Vector3[ids.Length];

            for (var i = 0; i < ids.Length; i++)
            {
                stack[i] = c.cargoPool[ids[i]].stack;
                inc[i] = c.cargoPool[ids[i]].inc;
                pos[i] = c.cargoPool[ids[i]].position;
            }
        }

        private static void Restore(CargoContainer c, int[] ids, byte[] stack, byte[] inc, Vector3[] pos)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                c.cargoPool[ids[i]].stack = stack[i];
                c.cargoPool[ids[i]].inc = inc[i];
                c.cargoPool[ids[i]].position = pos[i];
            }
        }

        private static void Write(CargoContainer c, int[] ids, byte stack, byte inc)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                c.cargoPool[ids[i]].stack = stack;
                c.cargoPool[ids[i]].inc = inc;
            }
        }

        /// <summary>抓一帧。必须等到帧末，否则截到的是上一帧的合成结果。</summary>
        private static IEnumerator Grab(RenderTexture rt, Texture2D tex, Action<Color32[]> sink)
        {
            yield return new WaitForEndOfFrame();

            ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);

            RenderTexture prev = RenderTexture.active;

            RenderTexture.active = rt;

            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            tex.Apply(false);

            RenderTexture.active = prev;

            sink(tex.GetPixels32());
        }

        /// <summary>
        /// 圈出两帧之间真正变了的像素。
        /// 拿阳性对照（货物挪走）来生成，得到的就是<b>货物在屏幕上占的那些像素</b>，
        /// 后面所有测量都只在这里面做——否则小目标会被整幅图的均值淹掉。
        /// </summary>
        private static bool[] Mask(Color32[] a, Color32[] b, out int count)
        {
            count = 0;

            if (a == null || b == null || a.Length != b.Length) return new bool[0];

            var mask = new bool[a.Length];

            for (var i = 0; i < a.Length; i++)
            {
                int dr = Math.Abs(a[i].r - b[i].r);
                int dg = Math.Abs(a[i].g - b[i].g);
                int db = Math.Abs(a[i].b - b[i].b);

                if (Math.Max(dr, Math.Max(dg, db)) <= PixelDelta) continue;

                mask[i] = true;

                count++;
            }

            return mask;
        }

        /// <summary>掩膜内的平均绝对差（0~255）。</summary>
        private static double DiffIn(Color32[] a, Color32[] b, bool[] mask, int maskPixels)
        {
            if (a == null || b == null || a.Length != b.Length || maskPixels <= 0) return 0.0;
            if (mask == null || mask.Length != a.Length) return 0.0;

            long sum = 0;

            for (var i = 0; i < a.Length; i++)
            {
                if (!mask[i]) continue;

                sum += Math.Abs(a[i].r - b[i].r);
                sum += Math.Abs(a[i].g - b[i].g);
                sum += Math.Abs(a[i].b - b[i].b);
            }

            return sum / (double)maskPixels / 3.0;
        }

        /// <summary>整幅图的平均绝对差。只留着做参考，主判定不用它。</summary>
        private static double Diff(Color32[] a, Color32[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length == 0) return 0.0;

            long sum = 0;

            for (var i = 0; i < a.Length; i++)
            {
                sum += Math.Abs(a[i].r - b[i].r);
                sum += Math.Abs(a[i].g - b[i].g);
                sum += Math.Abs(a[i].b - b[i].b);
            }

            return sum / (double)a.Length / 3.0;
        }
    }

#pragma warning disable 649 // 字段由 JSON 反序列化赋值

    /// <summary>
    /// data/cargoprobe.json 的映射类型。
    ///
    /// <b>为什么这个开关不塞进 stations.json。</b> <c>JsonHelper</c> 的磁盘覆盖是<b>整文件</b>生效的，
    /// 所以把一个开发用开关放进 stations.json，就意味着为了翻一个布尔值必须影子掉整份物流配置
    /// （集装层数、格子容量、充能功率都在里面），此后对内嵌 stations.json 的每一次修改都会被静默忽略。
    /// 那正是 CLAUDE.md 里记着的 LDBTool <c>CustomID.cfg</c> 同款陷阱，而且代价更大。
    /// 单独一个文件，影子掉的就只有它自己。
    /// </summary>
    [Serializable]
    internal class CargoProbeConfig
    {
        public bool enabled;
    }

#pragma warning restore 649
}
