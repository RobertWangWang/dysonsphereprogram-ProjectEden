using System;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把行星做大（<c>planet.json</c>，<b>默认关</b>）。
    ///
    /// <para><b>可建造格子数本来就是从半径推出来的，这是这件事能便宜做成的全部原因。</b>
    /// <c>PlanetAuxData..ctor</c> @0015 一句：</para>
    /// <code>
    /// mainGrid = new PlanetGrid(type, (int)(radius / 4f + 0.1f) * 4, identity)
    /// </code>
    /// 而 <c>PlanetGrid.SnapTo</c> 里纬度是 <c>lat/(2π) × segment</c>、再「×5 取整 ÷5」细分五格，
    /// 经度由 <c>DetermineLongitudeSegmentCount</c> 按 <c>cos(纬度) × segment</c> 给。
    /// 所以<b>格位数 ∝ segment² ∝ radius²，而每格的物理尺寸恒定</b>——半径 200→400
    /// 就是实打实 4 倍可建造面积，建筑占格一格不变。这一块一行代码都不用写。
    ///
    /// <para><b>入口只有一个。</b></para>
    /// <c>PlanetData.radius</c> 全程只有三个写入点：<c>PlanetGen.CreatePlanet</c> @0751
    /// （气态巨星，80，<c>scale=10</c>）、@094F（其余全部，200）和 <c>PlanetData..ctor</c>
    /// 的默认值。所以一个 <c>CreatePlanet</c> 后置就够，而 <c>PlanetAuxData</c> 是在
    /// 建模/扫描线程上才构造的（<c>PlanetComputeThreadMain</c> @015C、
    /// <c>PlanetScanThreadMain</c> @0180），远在其后——地形生成、建造网格、物理、
    /// 网格体全都看得见这次改动。15 个 <c>PlanetAlgorithm*</c> 里那 183 处 radius 读取
    /// 都是地形生成器，自己会适配。
    ///
    /// <para><b>不用管存档，因为这四个数根本不存。</b></para>
    /// 全模块扫 <c>Export</c>/<c>Import</c>，<c>radius</c>/<c>scale</c>/<c>precision</c>/
    /// <c>segment</c> 一个都不进存档，只有 <c>PlanetData.modData</c> 在里面。星系每次读档
    /// 都从种子重新生成，所以这个后置每次读档确定性重放。
    ///
    /// <para><b>但它把存档锁死，而且比 preloader 那条更硬——这是本开关的真正代价。</b></para>
    /// <list type="number">
    /// <item>建筑坐标存的是行星局部坐标、<b>模长 ≈ radius</b>。半径一改，所有星球上
    /// 已有的建筑全部在错误的高度。preloader 那条是「卸了就打不开」，这条是
    /// <b>「配置里这个数一改就等于重开」</b>。</item>
    /// <item><c>PlanetRawData.InitModData</c> 只有 16 条指令，@0003 就是 <c>stfld</c>，
    /// <b>把存档里的 modData 数组按引用直接装上、不查长度</b>。modData 长度
    /// <c>= dataLength/2 = (precision+1)²×2</c>，老存档那份 80,802 字节装进 precision=400
    /// 的星球（要 321,602）→ 第一次读地形就越界抛异常。<b>是崩，不是降级。</b></item>
    /// </list>
    ///
    /// <para><b>三个数必须一起动。</b></para>
    /// 只改大 <c>radius</c>，高度图样本数 <c>(precision+1)²×4</c> 摊在 4 倍表面上就是
    /// 四分之一密度，地基和地形起伏会变成建筑的两倍大。而 <c>precision/segment</c> 是
    /// 每块地形网格的顶点边长（<c>ModelingPlanetMain</c> @0869、
    /// <c>AddHeightMapModLevel</c> @0013），原版 200/5 = 40。
    /// <see cref="ResolveRadius"/> 的换算让它恒等于 40，代价是<b>半径必须是 40 的倍数</b>。
    ///
    /// <para><b>只认原版的那组数，不认「我改过了」。</b></para>
    /// 守卫查的是 <c>radius==200 &amp;&amp; precision==200 &amp;&amp; segment==5</c>——
    /// 也就是原版 @094F 那一支的**末态**，而不是「这颗星是不是气态巨星」。
    /// 这一条同时覆盖三件事：气态巨星（80/64/2）自动被排除、<c>EPlanetType.None</c>
    /// 自动被排除、以及<b>任何别的 mod 已经动过这三个数时我们让开</b>。
    /// 这是本文件记过的那条——<b>核对末态，而不是核对自己那一份贡献</b>。
    /// </summary>
    [HarmonyPatch]
    internal static class PlanetRadiusPatches
    {
        /// <summary>原版普通行星的那组数，三个一起作为「没人动过」的判据。</summary>
        private const float StockRadius = 200f;
        private const int StockPrecision = 200;
        private const int StockSegment = 5;

        /// <summary>每块地形网格的顶点边长，原版 200/5。半径必须是它的倍数。</summary>
        private const int TileVerts = 40;

        /// <summary>
        /// <b>原版 <c>PlanetData.kMaxMeshCnt</c> 是 <c>const int = 100</c>，也就是字面量，
        /// 被内联烘死在 <c>PlanetData..ctor</c> 的四个 <c>newarr</c> 上</b>
        /// （@0061 <c>Mesh</c>、@006E <c>MeshRenderer</c>、@007B <c>MeshCollider</c>、
        /// @0088 <c>Boolean</c> 也就是 <c>dirtyFlags</c>）。
        ///
        /// <para><b>而它恰好等于 segment=5 时的实际块数，一点余量都没有。</b>
        /// 块数公式是 <see cref="MeshCount"/>，从 <c>ModelingPlanetMain</c> 的三层循环量出来的：
        /// 外层 <c>V_21 &lt; 4</c>（@0EAC，四个象限——<c>PlanetRawData</c> 的贴图是
        /// 2(precision+1) × 2(precision+1)，即 2×2 个象限），内两层 <c>V_24</c>/<c>V_25</c>
        /// 以 <c>precision/segment</c> 为步长走到 <c>precision</c>（@0E8B/@0E9B），
        /// 各 segment 次。所以 <c>4 × segment²</c>：segment=5 → 正好 100。</para>
        ///
        /// <para><b>这就是第一次试玩崩掉的原因</b>——segment 提到 10 要 400 块，
        /// 而数组还是 100 长，写到第 100 块时
        /// <c>ModelingPlanetMain</c> @107A 的 <c>stelem.ref</c> 越界。
        /// 本文件早先的勘察结论「kMaxMeshCnt 全程零引用，不构成闸」是**错的**：
        /// <b>零引用恰恰说明它是 const，被内联到了分配点上</b>——按字段引用去搜，
        /// 搜不到的正是最该担心的那一类。</para>
        ///
        /// <para>其余消费方都安全，这是数过的：<c>ModelingPlanetMain</c> 里那三个 100 是
        /// <c>new List&lt;&gt;(100)</c> 的<b>初始容量</b>（会自己长，不是上限），
        /// <c>UnloadMeshes</c>/<c>UpdateDirtyMeshes</c>/<c>PlanetFactory.PlanetReformAll</c>/
        /// <c>PlanetReformRevert</c> 全是 <c>ldlen</c> 兜底，
        /// <c>GetUnloadedCopy</c> 是 <c>MemberwiseClone</c> 之后把这四个置空。</para>
        /// </summary>
        private const int StockSegmentMeshCount = 100;

        /// <summary>
        /// <b>真正的硬墙是 655.35，来自 <c>heightData</c>。</b>
        /// 它是 <c>UInt16</c>、单位「高度 × 0.01」，而写入侧
        /// （<c>PlanetAlgorithm*.GenerateTerrain</c>，例如 <c>PlanetAlgorithm7</c> @02C0–02E1）是
        /// <c>heightData[i] = (ushort)((radius + 起伏) * 100)</c>——<b><c>conv.u2</c> 直接截断，
        /// 没有任何钳位</b>。超过就回绕到接近 0，整颗星的地形炸掉，比任何其他失败都严重。
        /// 所以约束是 <c>半径 + 最高山 &lt; 655.35</c>。
        ///
        /// <para><b>起伏不随半径缩放</b>，这是查过的：噪声的采样坐标是
        /// <c>vertices[i].xyz * radius</c>（即世界坐标，<c>PlanetAlgorithm7</c> @00BA/@00DC/@00FE），
        /// 所以地貌的<b>物理尺寸恒定</b>，半径变大只是同样大小的山更多座；
        /// 而振幅那几个系数和 radius 无关（整个方法只读了 4 次 radius，三次是采样坐标、
        /// 一次是最后的相加）。于是余量 = 655.35 − 最高山，与半径无关。</para>
        ///
        /// <para><b>上界取 600（3×）是留余量，不是硬限。</b>理论上限在 640 附近，
        /// 取决于这一局最高的山有多高——而那个数只有量了才知道
        /// （<c>probe</c> 打开后会把「全星球高度极值」和推算出的半径上限打进日志）。
        /// 600 给最高山留了 55 格，vanilla 的山大约 10–15 格，够宽。</para>
        ///
        /// <para><b>一条被否掉的上限：<c>segmentTable</c> 不构成闸。</b>
        /// 本文件早先写着「480，因为 <c>DetermineLongitudeSegmentCount</c> 在 ≥500 时离开
        /// <c>segmentTable</c>」——重读 @002C–0045 之后这是错的：<c>segmentTable[n]</c>
        /// <b>只在 <c>n &lt; 500</c> 时才取</b>，而数组长 512，所以那一支永远不越界；
        /// <c>n ≥ 500</c> 走「进位到百」的另一支，同样能跑。<b>看着像上限的数，
        /// 在找到执行它的代码之前只是个主张。</b></para>
        /// </summary>
        private const int MinRadius = 200;
        private const int MaxRadius = 600;

        /// <summary>吸附并夹取之后的结果，Load 时算一次。0 = 不生效。</summary>
        internal static int Radius;
        internal static int Precision;
        internal static int Segment;

        private static long _resized;
        private static long _skippedNonStock;
        private static long _modDataFixed;
        private static int _firstLogged;
        private static int _galaxyLogged;
        private static int _modDataLogged;
        private static int _birthLogged;
        private static float _flattenBefore = float.NaN;
        private static int _standLogged;
        private static float _standSince;

        /// <summary>
        /// 15 格内 <c>heightData</c> 的最低值，供下面的几何对质做判据。
        /// 探针是一次性、单线程（<c>UIGame._OnUpdate</c>）的，所以一个静态字段够用。
        /// </summary>
        private static float _dataMinNear = float.NaN;

        private static bool _blockedByGs2;

        internal static PlanetRadiusConfig Config;

        private static bool Enabled => Config != null && Config.enabled && Radius > 0 && !_blockedByGs2;

        /// <summary>给同包的 <see cref="PlanetModPlanePatches"/> 用的只读状态。</summary>
        internal static bool Active => Enabled;

        /// <summary>
        /// 诊断探针总开关（<c>planet.json</c> 的 <c>probe</c>，默认关）。
        /// 探针会全量扫 64 万个顶点再读四块网格的顶点数组，一次性但不便宜，
        /// 所以平时关着；出「地形看着不对」的问题时再打开。
        /// </summary>
        private static bool Probing => Enabled && Config != null && Config.probe;

        /// <summary>这个精度是不是我们放大出来的（<see cref="PlanetRawData"/> 那边拿不到 PlanetData）。</summary>
        internal static bool IsResizedPrecision(int precision) =>
            Enabled && Precision > 0 && precision == Precision;

        /// <summary>
        /// 把倍率吸附到最近的合法半径。<b>合法 = 40 的倍数</b>，因为
        /// <c>precision/segment</c>（每块地形网格的顶点边长）必须整除：不整除的话
        /// 分块铺不满球面，表现是接缝或者更糟。
        ///
        /// <para>顺带说明为什么是 <c>precision = radius</c>：原版恰好 200 == 200，
        /// 所以「单位面积上的地形细腻度不变」就是「precision 跟半径同比例」。</para>
        /// </summary>
        internal static int ResolveRadius(float multiplier)
        {
            if (multiplier <= 0f || float.IsNaN(multiplier)) return 0;

            double wanted = StockRadius * multiplier;

            int snapped = (int)Math.Round(wanted / TileVerts) * TileVerts;

            if (snapped < MinRadius) snapped = MinRadius;
            if (snapped > MaxRadius) snapped = MaxRadius;

            return snapped;
        }

        /// <summary>
        /// 地形网格块数 = <b>4 个象限 × segment × segment</b>，见
        /// <see cref="StockSegmentMeshCount"/> 的注释里那三层循环。
        /// 每块的顶点数是 <c>(precision/segment + 1)²</c>，本换算下恒为 41² = 1681，
        /// 和原版一样，所以不存在 Unity 16 位索引的问题——**变多的是块数，不是每块的大小**。
        /// </summary>
        internal static int MeshCount(int segment) => 4 * segment * segment;

        /// <summary>
        /// 把 <c>PlanetData..ctor</c> 按字面量 100 分配的四个数组撑到实际需要的长度。
        ///
        /// <para>只在**不够长**时重新分配，并把原内容拷过去：<c>CreatePlanet</c> 这一刻
        /// 它们还是空的，拷贝等于没拷，但这样写在任何调用时机下都不会丢东西。
        /// 原版尺寸（segment=5 → 100）下这个方法是个空操作。</para>
        /// </summary>
        private static bool EnsureMeshArrays(PlanetData p, int segment)
        {
            int need = MeshCount(segment);

            if (p.meshes != null && p.meshes.Length >= need) return false;

            var meshes = new UnityEngine.Mesh[need];
            var renderers = new UnityEngine.MeshRenderer[need];
            var colliders = new UnityEngine.MeshCollider[need];
            var dirty = new bool[need];

            if (p.meshes != null) Array.Copy(p.meshes, meshes, p.meshes.Length);
            if (p.meshRenderers != null) Array.Copy(p.meshRenderers, renderers, p.meshRenderers.Length);
            if (p.meshColliders != null) Array.Copy(p.meshColliders, colliders, p.meshColliders.Length);
            if (p.dirtyFlags != null) Array.Copy(p.dirtyFlags, dirty, p.dirtyFlags.Length);

            p.meshes = meshes;
            p.meshRenderers = renderers;
            p.meshColliders = colliders;
            p.dirtyFlags = dirty;

            return true;
        }

        internal static void Load()
        {
            Config = Utils.JsonHelper.Load<PlanetRadiusConfig>("planet");

            if (Config == null) return;

            Radius = ResolveRadius(Config.radiusMultiplier);

            if (Radius <= 0) return;

            Precision = Radius;
            Segment = Radius / TileVerts;

            // 装了银河尺度就让开：GS2 自己就在决定每颗星的半径，我们再乘一道
            // 等于把它的尺寸逻辑和 scale 一起打乱。两个都想改同一个数的时候，
            // 让先到的那个说了算。
            _blockedByGs2 =
                Compatibility.CompatibilityRegistry.IsLoaded(
                    Compatibility.GalacticScaleCompat.MODGUID);
        }

        /// <summary>
        /// <c>PlanetGen.CreatePlanet</c> 没有重载，所以按名字挂是安全的；
        /// 只取 <c>__result</c>（Harmony 的保留名），不碰原版的形参名。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetGen), nameof(PlanetGen.CreatePlanet))]
        private static void Resize(PlanetData __result)
        {
            if (!Enabled || __result == null) return;

            // **核对末态，不是核对自己那一份贡献。**
            // 气态巨星（80/64/2）、EPlanetType.None，以及任何别的 mod 已经动过
            // 这三个数的情况，全部在这一条里让开。
            if (__result.radius != StockRadius
                || __result.precision != StockPrecision
                || __result.segment != StockSegment)
            {
                Interlocked.Increment(ref _skippedNonStock);

                return;
            }

            __result.radius = Radius;
            __result.precision = Precision;
            __result.segment = Segment;

            // **必须和上面三个数一起做，否则 ModelingPlanetMain @107A 直接越界。**
            // 原版那四个数组是按 const kMaxMeshCnt = 100 的字面量分配的，而 100
            // 恰好只够 segment=5。理由见 StockSegmentMeshCount 的注释。
            EnsureMeshArrays(__result, Segment);

            Interlocked.Increment(ref _resized);

            if (Interlocked.Exchange(ref _firstLogged, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"行星放大：首颗生效于「{__result.displayName}」——"
                    + $"半径 {StockRadius:0} → {Radius}（{Radius / StockRadius:0.00}×）、"
                    + $"高度图精度 {StockPrecision} → {Precision}、地形分块 {StockSegment} → {Segment}"
                    + $"（每块顶点边长 {Precision / Segment}，原版 {StockPrecision / StockSegment}）。"
                    + $"地形网格数组 {StockSegmentMeshCount} → {__result.meshes.Length} 块"
                    + $"（4 象限 × {Segment}²，原版 const kMaxMeshCnt=100 正好只够 segment=5）。"
                    + $"建造网格 segment 由原版自己从半径推出，应为 {(int)(Radius / 4f + 0.1f) * 4}，"
                    + $"即可建造格位约 {(Radius / StockRadius) * (Radius / StockRadius):0.0} 倍");
        }

        /// <summary>
        /// <b>长度对不上的 <c>modData</c> 必崩，把它改成大声降级。</b>
        ///
        /// <para><c>PlanetRawData.InitModData</c> 只有 16 条指令，@0003 就是 <c>stfld</c>：
        /// <b>传进来的数组非 null 就按引用直接装上，不查长度</b>。而 <c>modData</c> 的长度
        /// 必须正好是 <c>dataLength/2 = (precision+1)²×2</c>——
        /// <c>GetModLevel</c>/<c>GetModPlane</c> 是 <c>modData[index >> 1]</c> 取半字节，
        /// 下标上界 <c>dataLength-1</c>，<b>一点余量都没有</b>。</para>
        ///
        /// <para><b>原版的每一条路都是自洽的，这是数过的</b>：
        /// <c>PlanetComputeThreadMain</c> @012E / <c>PlanetScanThreadMain</c> @014D /
        /// <c>RegenerateRawDataImmediately</c> @000E 都是先
        /// <c>new PlanetRawData(this.precision)</c> 再 <c>InitModData</c>；
        /// <c>PlanetFactory.Init</c> @015C 拿的是<b>同一颗星</b>的 <c>GetUnloadedCopy</c>
        /// （<c>MemberwiseClone</c>，precision 一致）。
        /// <b>唯一能产生长度不符的入口是 <c>PlanetData.ImportRuntime</c> @000A——
        /// 也就是存档里那一份。</b></para>
        ///
        /// <para>所以这个后置**不挂在开关上**：两个方向都会炸——用放大开关开一份放大前的
        /// 存档（短数组），以及把开关关掉去开一份放大后的存档（长数组）。原版自己永远
        /// 不会触发它，代价是每颗星加载时多一次整数比较。</para>
        ///
        /// <para>旧内容<b>不拷贝</b>：长度不同意味着地形分辨率不同，旧的半字节布局在新
        /// 分辨率下没有任何意义。代价是<b>那颗星的地基/地形改造归零</b>，这在两个方向上
        /// 都是无法避免的——而崩溃的替代品不是"保住数据"，是"根本进不去"。</para>
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetRawData), nameof(PlanetRawData.InitModData))]
        private static void FixModDataLength(PlanetRawData __instance, ref byte[] __result)
        {
            int need = __instance.dataLength / 2;

            if (__result != null && __result.Length == need) return;

            int was = __result == null ? -1 : __result.Length;

            var fresh = new byte[need];

            __instance.modData = fresh;
            __result = fresh;

            long n = Interlocked.Increment(ref _modDataFixed);

            // 第一次打完整解释，之后每颗星一条短的带累计数——行星加载本来就不频繁，
            // 不会刷屏，而「到底波及了几颗」是只有累计数才答得出来的
            if (Interlocked.Exchange(ref _modDataLogged, 1) != 0)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"行星放大：又一颗星的地形改造数据长度对不上（{was} → {need} 字节），"
                    + $"已重新分配。本次会话累计 {n} 颗");

                return;
            }

            ProjectEdenPlugin.Log.LogError(
                $"行星放大：地形改造数据长度对不上——拿到 {was} 字节，当前精度 "
                + $"{__instance.precision} 需要 {need} 字节"
                + $"（旧的那份对应精度约 {ImpliedPrecision(was)}）。"
                + "**已改成重新分配一份空的，避免 GetModLevel 直接越界崩掉**，"
                + "代价是这些星球的地基/地形改造归零。\n"
                + "原因几乎只有一个：**这份存档和当前的 planet.json 不是同一个半径**。"
                + "原版所有路径都是按同一个 precision 自洽的，只有存档读回来那一份不是。\n"
                + "**而且地形归零还不是最严重的**：建筑坐标的模长约等于半径，"
                + "所以这份存档里每一座建筑都站在错误的高度。"
                + "请用和存档一致的 radiusMultiplier，或者开一局新档");
        }

        /// <summary>
        /// <b>出生点诊断。</b>玩家报「出生点在水面上」，而两个高度在原版里就不是同一个数：
        /// <list type="bullet">
        /// <item><c>GenBirthPoints</c> 判定「这里是不是陆地」用的是
        /// <c>QueryHeight(p) &gt; realRadius + <b>0.2</b></c>（@028C–02A3，三点采样；
        /// 后面 @03A2–03C8 还有四点，共七点），那个 0.2 是<b>写死的</b>；</item>
        /// <item>而真正画出来的海面在 <c>realRadius + <b>waterHeight</b></c>
        /// （<c>ModelingPlanetMain</c> @07F8–0809，乘 2 得到海洋球直径），
        /// <c>waterHeight</c> 直接来自 <c>ThemeProto.WaterHeight</c>
        /// （<c>SetPlanetTheme</c> @033C），是个<b>绝对偏移</b>。</item>
        /// </list>
        /// 两者不等时就会选中「高于 0.2、低于真实海面」的点——人站在浅水里，
        /// 而不是掉进大海。**这一条在原版半径下就存在**，只是地形起伏相对行星更大时
        /// 撞上的概率低。
        ///
        /// <para>所以这里只<b>量</b>不改：把两个高度、出生点的实际地形高度、以及
        /// 是不是走了北极兜底（<c>new Vector3(0, realRadius + 5, 0)</c>，@03F7–0413）
        /// 一次性打出来。**先有数字再动手**——这一条如果按猜的去改，
        /// 改的很可能是没错的那一半。</para>
        ///
        /// <para><c>GenBirthPoints</c> <b>有两个重载</b>，所以必须写出参数类型；
        /// 按裸名字挂会 <c>AmbiguousMatchException</c> 并把整个 mod 带下水。
        /// 这两个参数类型都没被本仓库的 preloader 加宽过，写死是安全的。</para>
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetData), nameof(PlanetData.GenBirthPoints),
            new[] { typeof(PlanetRawData), typeof(int) })]
        private static void ProbeBirthPoint(PlanetData __instance, PlanetRawData rawData)
        {
            if (!Probing || rawData == null) return;

            // **一次性日志要「每种东西一次」，不是「每次会话一次」。**
            // 上一版这里是个全局标志，于是它记下的是第一颗跑到 GenBirthPoints 的星
            //（DenebKaitos I），而不是玩家真正的出生星——报出来的数字全对，
            // 对的却是另一颗星。本仓库为同一个形状付过账（MegaStationPatches 的储物格转储）。
            if (Interlocked.Increment(ref _birthLogged) > 3) return;

            float real = __instance.realRadius;
            UnityEngine.Vector3 bp = __instance.birthPoint;

            float landTest = real + 0.2f;
            float ocean = real + __instance.waterHeight;

            float here = rawData.QueryHeight(bp.normalized);

            bool pole = UnityEngine.Mathf.Abs(bp.x) < 0.001f
                        && UnityEngine.Mathf.Abs(bp.z) < 0.001f;

            ProjectEdenPlugin.Log.LogWarning(
                $"行星放大·出生点诊断「{__instance.displayName}」："
                + $"半径 {__instance.radius}（realRadius {real:0.00}）、精度 {__instance.precision}、"
                + $"分块 {__instance.segment}。\n"
                + $"  出生点地形高度 = {here:0.000}\n"
                + $"  原版判陆地的门槛 = realRadius + 0.2 = {landTest:0.000}（写死的 0.2）\n"
                + $"  真实海面 = realRadius + waterHeight({__instance.waterHeight:0.000}) = {ocean:0.000}\n"
                + $"  出生点 {(here > ocean ? "**在海面之上**" : "**在海面之下**，深 " + (ocean - here).ToString("0.000"))}"
                + $"，{(pole ? "**走了北极兜底**（256 次没找到陆地）" : "是搜索选出来的，不是兜底")}\n"
                + "  两个高度不相等就说明：原版判陆地用 0.2、画海面用 waterHeight，本来就不是同一个数");
        }

        /// <summary>
        /// <b>把出生点判定的采样偏移按半径缩回去，让它检查的地面范围和原版一样大。</b>
        ///
        /// <para><c>GenBirthPoints</c> 判定「这里能不能出生」用的是一组<b>角度</b>偏移，
        /// 数清楚一共十处（枚举出来的，不是估的）：</para>
        /// <list type="bullet">
        /// <item><c>ldc.r4 <b>0.03</b></c> × <b>8</b>（@02C9 @02DD @02F2 @0307 @031B @032F @0344 @0359）
        /// —— 八个陆地采样点 V_23…V_30，四对正负；</item>
        /// <item><c>ldc.r4 <b>0.06</b></c> × <b>2</b>（@01C8 @01EB）
        /// —— 两个初始资源点 <c>birthResourcePoint0/1</c>，它们同时也参与陆地判定
        /// （@02AB @02BA）。</item>
        /// </list>
        /// 角度乘半径才是地面距离，所以半径 200→400 之后，原本 6 / 12 格的检查半径变成
        /// <b>12 / 24 格</b>——<b>中心点测了、远处也测了，中间那一圈没人测</b>，
        /// 一个十几格宽的水洼正好藏在里面，人就落在水中央。实测站立高度 400.622、
        /// 海面 400.000，物理上确实站在干地的小土包上，而四周是水。
        ///
        /// <para><b>刻意不动 <c>ldc.r4 0.5</c>（@00A9 @00CC）</b>：那两个是候选点在星球上的
        /// 撒布范围，不是检查用的 footprint，缩它等于缩小搜索区域。
        /// 这正是本文件反复记的「同一个常量两种含义」——按值一刀切会把它们一起改掉。</para>
        ///
        /// <para><b>直接改写操作数，不插指令。</b>倍率在 <see cref="Load"/> 里就算好了，
        /// 而 <c>Load</c> 早于 <c>PatchAll</c>，所以补丁期就知道该填什么。指令数一条不变，
        /// <b>分支标签完全不动</b>——本仓库为「插指令撑爆短跳转」付过一次线上崩溃的账。</para>
        ///
        /// <para>数目不对就<b>大声失败且一处都不改</b>：改一半会让采样点半径不一致，
        /// 那种错不报错，只会让出生点偶尔更差。</para>
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(PlanetData), nameof(PlanetData.GenBirthPoints),
            new[] { typeof(PlanetRawData), typeof(int) })]
        private static System.Collections.Generic.IEnumerable<CodeInstruction> ScaleBirthFootprint(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
        {
            var code = new System.Collections.Generic.List<CodeInstruction>(instructions);

            if (!Enabled) return code;

            float k = StockRadius / Radius;

            int n3 = 0;
            int n6 = 0;

            foreach (CodeInstruction c in code)
            {
                if (c.opcode != System.Reflection.Emit.OpCodes.Ldc_R4) continue;
                if (!(c.operand is float f)) continue;

                if (f == 0.03f) n3++;
                else if (f == 0.06f) n6++;
            }

            if (n3 != 8 || n6 != 2)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"行星放大：出生点采样偏移的常量数目不对（0.03 找到 {n3} 处、应为 8；"
                    + $"0.06 找到 {n6} 处、应为 2），**一处都不改**。游戏版本可能变了——"
                    + "出生点仍按原版角度偏移判定，在放大的星球上会检查两倍大的范围，"
                    + "可能重新出现「出生在水里」");

                return code;
            }

            foreach (CodeInstruction c in code)
            {
                if (c.opcode != System.Reflection.Emit.OpCodes.Ldc_R4) continue;
                if (!(c.operand is float f)) continue;

                if (f == 0.03f) c.operand = 0.03f * k;
                else if (f == 0.06f) c.operand = 0.06f * k;
            }

            ProjectEdenPlugin.Log.LogWarning(
                $"行星放大：出生点采样偏移已按 {StockRadius:0}/{Radius} = {k:0.000} 缩回"
                + $"（0.03 → {0.03f * k:0.0000} 共 8 处、0.06 → {0.06f * k:0.0000} 共 2 处）。"
                + $"陆地判定检查的地面范围因此仍是原版的 {0.03f * StockRadius:0.0} / {0.06f * StockRadius:0.0} 格，"
                + $"而不是随半径涨到 {0.03f * Radius:0.0} / {0.06f * Radius:0.0} 格——"
                + "**中间那圈没人测的环带正是「出生在水里」的成因**");

            return code;
        }

        /// <summary>
        /// <b>量玩家落地后的真实高度——这是唯一一个不经过 <c>QueryHeight</c> 的测量。</b>
        ///
        /// <para>到目前为止所有"出生点在海面之上"的结论都建立在
        /// <c>PlanetRawData.QueryHeight</c> 上，而它正是嫌疑本身：
        /// <c>CalcVerts</c> 的记忆化快路径只认 precision <b>200</b>（@0000）和 <b>80</b>（@0043），
        /// 400 走 @0083 的通用路径重算 <c>indexMap</c>；<c>QueryHeight</c> 又是靠
        /// <c>PositionHash</c> → <c>indexMap</c> 找邻近顶点的。它要是算错了，
        /// <b>选点和诊断会一起错，而且错得一模一样</b>——本仓库记过的
        /// 「两个独立测量共用同一条失败路径」。</para>
        ///
        /// <para>玩家的位置是物理/渲染那一侧给的，和 <c>indexMap</c> 无关。
        /// 所以「玩家脚下的高度」和「<c>QueryHeight</c> 说的高度」一旦对不上，
        /// 就直接证明是 <c>QueryHeight</c> 在撒谎；两者一致却仍然泡在水里，
        /// 那就说明水不是海面，得换方向查。<b>先让两个独立的数打架，再决定修哪个。</b></para>
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void ProbeStandingHeight()
        {
            if (!Probing || _standLogged != 0) return;

            PlanetData p = GameMain.localPlanet;

            if (p == null || p.data == null || p.factory == null) return;

            Player pl = GameMain.mainPlayer;

            if (pl == null) return;

            // 等落地站稳：刚进游戏那几帧还在下落，量到的是空中高度
            if (_standSince <= 0f)
            {
                _standSince = UnityEngine.Time.realtimeSinceStartup;

                return;
            }

            if (UnityEngine.Time.realtimeSinceStartup - _standSince < 5f) return;

            if (Interlocked.Exchange(ref _standLogged, 1) != 0) return;

            UnityEngine.Vector3 pos = pl.position;
            float stand = pos.magnitude;
            float real = p.realRadius;
            float ocean = real + p.waterHeight;
            float queried = p.data.QueryHeight(pos.normalized);

            var sb = new System.Text.StringBuilder();

            sb.Append($"行星放大·站立高度实测「{p.displayName}」：\n");
            sb.Append($"  realRadius = {real:0.000}，海面 = {ocean:0.000}（waterHeight {p.waterHeight:0.000}）\n");
            sb.Append($"  玩家实际高度 = {stand:0.000}");
            sb.Append(stand < ocean
                ? $"（**低于海面 {ocean - stand:0.000}**）\n"
                : $"（高于海面 {stand - ocean:0.000}）\n");
            sb.Append($"  同一点 QueryHeight = {queried:0.000}，两者相差 {stand - queried:+0.000;-0.000;0.000}"
                      + "（约等于机甲站立身位，说明 QueryHeight 和物理地面是一致的）\n");

            // **环带扫描：这是「未采样环带」那条假说的判据。**
            // GenBirthPoints 的七个采样点用的是角度偏移 0.03 / 0.06 弧度，
            // 在 realRadius 下换算成格数就是 0.03*real / 0.06*real——半径翻倍它们也翻倍，
            // 于是中心和远处采样点之间空出一圈没人测的环带。水洼要是正好落在那一圈里，
            // 七个点会全部通过，而人就站在水中央的小土包上。
            sb.Append($"  采样点在本半径下的实际距离：0.03 rad = {0.03f * real:0.0} 格，"
                      + $"0.06 rad = {0.06f * real:0.0} 格（原版 200 时是 6.0 / 12.0 格）\n");
            sb.Append("  环带扫描（每圈 16 个方向取最低点）：\n");

            UnityEngine.Vector3 up = pos.normalized;
            UnityEngine.Vector3 t1 = UnityEngine.Vector3.Cross(up, UnityEngine.Vector3.up);

            if (t1.sqrMagnitude < 1e-6f) t1 = UnityEngine.Vector3.Cross(up, UnityEngine.Vector3.right);

            t1 = t1.normalized;

            UnityEngine.Vector3 t2 = UnityEngine.Vector3.Cross(t1, up).normalized;

            foreach (float dist in new[] { 2f, 4f, 6f, 8f, 12f, 18f, 24f, 32f })
            {
                float ang = dist / real;
                float lo = float.MaxValue;
                float hi = float.MinValue;

                for (int k = 0; k < 16; k++)
                {
                    float th = k * UnityEngine.Mathf.PI * 2f / 16f;
                    UnityEngine.Vector3 d =
                        (up + (UnityEngine.Mathf.Cos(th) * t1 + UnityEngine.Mathf.Sin(th) * t2) * ang).normalized;
                    float h = p.data.QueryHeight(d);

                    if (h < lo) lo = h;
                    if (h > hi) hi = h;
                }

                sb.Append($"    {dist,4:0} 格：最低 {lo:0.000}、最高 {hi:0.000}"
                          + $"  {(lo < ocean ? $"← **这一圈有 {ocean - lo:0.000} 深的水**" : "全在海面之上")}\n");
            }

            // ---- 绕开 indexMap 的独立对质 ----
            // 上面每一个数都出自 QueryHeight，而它是靠 PositionHash -> indexMap 找邻近顶点的；
            // QueryIndex 走同一条路，所以**不能拿来互证**。这里改成暴力扫 vertices[]：
            // 直接找角度最近的顶点、直接读 heightData，和 indexMap 一点关系都没有。
            // 一次全量扫描约 64 万个顶点，一次性诊断，代价可以接受。
            //
            // **这一条才是「机甲是站在干地上还是浮在水面上」的判据。**
            // 之前那句「玩家高度 − QueryHeight = 1.019 ≈ 机甲身位，所以 QueryHeight 是对的」
            // 是循环论证：身位本身就是拿 QueryHeight 算出来的。
            UnityEngine.Vector3[] verts = p.data.vertices;
            ushort[] hd = p.data.heightData;
            int n = p.data.dataLength;

            if (verts != null && hd != null && verts.Length >= n && hd.Length >= n)
            {
                float cosLimit = UnityEngine.Mathf.Cos(15f / real);
                int inRange = 0;
                int belowSea = 0;
                float minH = float.MaxValue;
                float maxH = float.MinValue;
                float bestDot = -2f;
                int bestIdx = -1;

                for (int i = 0; i < n; i++)
                {
                    float d = UnityEngine.Vector3.Dot(verts[i], up);

                    if (d > bestDot)
                    {
                        bestDot = d;
                        bestIdx = i;
                    }

                    if (d < cosLimit) continue;

                    inRange++;

                    float h = hd[i] * 0.01f * p.scale;

                    if (h < minH) minH = h;
                    if (h > maxH) maxH = h;
                    if (h < ocean) belowSea++;
                }

                float nearestH = bestIdx >= 0 ? hd[bestIdx] * 0.01f * p.scale : float.NaN;
                float nearestDist = bestIdx >= 0
                    ? UnityEngine.Mathf.Acos(UnityEngine.Mathf.Clamp(bestDot, -1f, 1f)) * real
                    : float.NaN;

                // 全星球的高度极值：这是「半径最多能放大几倍」的唯一未知量。
                // heightData 是 UInt16 × 0.01 且**写入时没有钳位**
                //（GenerateTerrain 末尾是 conv.u2，见 PlanetAlgorithm7 @02E0），
                // 所以硬墙是 (半径 + 最高山) × 100 < 65536，即 655.35。
                // 而起伏是绝对值、不随半径缩放（噪声采样坐标是 vertices*radius，
                // 也就是世界坐标，所以地貌的物理尺寸恒定），于是余量 = 655.35 - 最高山。
                float gLo = float.MaxValue;
                float gHi = float.MinValue;

                for (int i = 0; i < n; i++)
                {
                    float h = hd[i] * 0.01f * p.scale;

                    if (h < gLo) gLo = h;
                    if (h > gHi) gHi = h;
                }

                sb.Append("  ── 全星球高度极值（决定半径上限）──\n");
                sb.Append($"    最低 {gLo:0.000}、最高 {gHi:0.000}，"
                          + $"相对半径 {gLo - real:+0.000;-0.000;0.000} … {gHi - real:+0.000;-0.000;0.000}\n");
                sb.Append($"    heightData 的硬墙是 655.35（UInt16 × 0.01，写入无钳位），"
                          + $"本星最高山占 {gHi - real:0.00} 格 → 半径理论上限约 "
                          + $"{(int)((655.35f - (gHi - real)) / 40f) * 40}"
                          + $"（{(655.35f - (gHi - real)) / StockRadius:0.0}× 附近，取 40 的倍数）\n");

                sb.Append("  ── 绕开 indexMap 的独立对质（暴力扫 vertices，直接读 heightData）──\n");
                sb.Append($"    最近顶点：#{bestIdx}，距玩家 {nearestDist:0.00} 格，heightData 高度 = {nearestH:0.000}\n");
                sb.Append($"    与同点 QueryHeight({queried:0.000}) 相差 {nearestH - queried:+0.000;-0.000;0.000}"
                          + "（**差得多就说明 QueryHeight 的 indexMap 查错了**）\n");
                sb.Append($"    15 格内共 {inRange} 个顶点：最低 {minH:0.000}、最高 {maxH:0.000}，"
                          + $"低于海面的有 {belowSea} 个"
                          + $"{(belowSea > 0 ? $"（最深 {ocean - minH:0.000}，小水洼是正常地形）" : "")}\n");

                // **这里只陈述事实，不下判决。**
                // 上一版在这里写死了「有水 => QueryHeight 在撒谎」，可 heightData 自己
                // 报出小水洼时那句话就是自相矛盾的——判据必须是**两侧对比**，
                // 而不是单侧的「有没有水」。真正的判决在下面 AppendMeshProbe 里，
                // 拿几何和这里的 minH 对质。
                _dataMinNear = minH;
            }
            else
            {
                sb.Append("  ── 独立对质跳过：vertices/heightData 取不到或长度不足 ──\n");

                _dataMinNear = float.NaN;
            }

            // ---- 数据 vs 几何：读真正画出来 / 用来碰撞的那份网格顶点 ----
            // heightData 已经量过是干的（≥ 海面），但玩家「走过去掉下去、出不去」，
            // 说明脚下那块**几何**和数据对不上。这一段直接读 planet.meshes 的顶点，
            // 和 heightData 对质——**这是整条链上最后一个没被量过的环节**。
            //
            // 先用 mesh.bounds.center 的方向挑出离玩家最近的几块（便宜，不复制顶点数组），
            // 再只对那几块取 vertices。
            AppendMeshProbe(sb, p, up, ocean);

            sb.Append("  → 若「近圈有水、远圈无水」，就证明水洼正落在未采样环带里，"
                      + "而那圈的宽度是跟着半径线性放大的");

            ProjectEdenPlugin.Log.LogWarning(sb.ToString());
        }

        /// <summary>
        /// <b>新手引导会在出生点挖一块 10×10 的平地，方形水洼就是它。</b>
        /// <c>GuideMissionStandardMode.OnPlanetFactoryLoad</c> @004C：
        /// <code>
        /// factory.FlattenTerrain(targetPos, targetRot,
        ///     new Bounds(Vector3.zero, new Vector3(10, 5, 10)),   // 10x10 的方块
        ///     6f, 1f, removeVein: false, lift: true, emitEffects: false, autoRefresh: true,
        ///     default(Bounds));
        /// </code>
        /// 而 <c>targetPos</c> 就是 <c>localPlanet.birthPoint</c>（<c>Init</c> @0024 / <c>Skip</c> @0046），
        /// 其模长是 <c>realRadius + 0.2 + 1.45</c>（<c>GenBirthPoints</c> @00FD–010E）。
        ///
        /// <para><b>这一对前后置只量不改</b>：整平前后各取一次地形高度，连同海面高度一起打出来。
        /// 选点本身已经量过是好的（在海面之上 0.448），所以水洼只能是这一步挖出来的——
        /// 而「挖了多深、有没有掉到海面以下」是只有前后对比才答得出来的。</para>
        ///
        /// <para>挂在这里而不是 <c>FlattenTerrain</c> 上：那个方法被建造、蓝图、地基、
        /// 敌人基地等十几处调用，挂上去会淹掉日志；引导那一处才是出生点唯一的那次。</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GuideMissionStandardMode),
            nameof(GuideMissionStandardMode.OnPlanetFactoryLoad))]
        private static void GuideFlattenBefore(GuideMissionStandardMode __instance)
        {
            if (!Probing) return;

            _flattenBefore = SampleHeight(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GuideMissionStandardMode),
            nameof(GuideMissionStandardMode.OnPlanetFactoryLoad))]
        private static void GuideFlattenAfter(GuideMissionStandardMode __instance)
        {
            if (!Probing) return;

            PlanetData p = __instance.localPlanet;

            if (p == null || p.data == null) return;

            float after = SampleHeight(__instance);
            float real = p.realRadius;
            float ocean = real + p.waterHeight;
            float target = __instance.targetPos.magnitude;

            ProjectEdenPlugin.Log.LogWarning(
                $"行星放大·出生点整平诊断「{p.displayName}」（这才是真正的出生星）：\n"
                + $"  半径 {p.radius}（realRadius {real:0.00}）、精度 {p.precision}、分块 {p.segment}\n"
                + $"  整平中心 targetPos 模长 = {target:0.000}"
                + $"（= realRadius + {target - real:0.000}，原版是 +1.65）\n"
                + $"  整平**前**地形高度 = {_flattenBefore:0.000}\n"
                + $"  整平**后**地形高度 = {after:0.000}（挖深 {_flattenBefore - after:+0.000;-0.000;0.000}）\n"
                + $"  真实海面 = realRadius + waterHeight({p.waterHeight:0.000}) = {ocean:0.000}\n"
                + $"  → 整平后 {(after > ocean ? $"**在海面之上** {after - ocean:0.000}，那这块方地不该有水" : $"**在海面之下** {ocean - after:0.000}，**方形水洼就是这么来的**")}");
        }

        /// <summary>
        /// <b>读真正被画出来、也被用来碰撞的那份几何，和 <c>heightData</c> 对质。</b>
        ///
        /// <para>到这一步为止所有说「这里是干的」的测量读的都是 <c>heightData</c>
        /// （<c>QueryHeight</c>、环带扫描、暴力最近顶点，实测三者完全一致）。
        /// 但玩家<b>走过去会掉下去而且出不去</b>——那不是水，那是脚下没有面。
        /// 所以剩下唯一没被量过的环节就是 <c>PlanetData.meshes</c> 本身：
        /// 地形改造之后 <c>UpdateDirtyMesh</c> 会按 <c>dirtyFlags</c> 重建对应的块，
        /// 重建坏了就会出现「数据是干的、几何是个洞」。</para>
        ///
        /// <para>先按 <c>mesh.bounds.center</c> 的方向挑出离玩家最近的 4 块
        /// （只读 bounds，不复制顶点数组），再只对这几块取 <c>vertices</c>——
        /// <c>Mesh.vertices</c> 每次调用都会复制一份，400 块全取要几 MB 垃圾。</para>
        /// </summary>
        private static void AppendMeshProbe(System.Text.StringBuilder sb, PlanetData p,
            UnityEngine.Vector3 up, float ocean)
        {
            UnityEngine.Mesh[] meshes = p.meshes;

            if (meshes == null)
            {
                sb.Append("  ── 网格对质跳过：planet.meshes 为 null ──\n");

                return;
            }

            int expect = MeshCount(p.segment);
            int nonNull = 0;

            for (int i = 0; i < meshes.Length; i++)
                if (meshes[i] != null) nonNull++;

            sb.Append("  ── 数据 vs 几何（直接读 planet.meshes 的顶点）──\n");
            sb.Append($"    网格数组长度 {meshes.Length}、非空 {nonNull} 块，"
                      + $"应有 4×{p.segment}² = {expect} 块"
                      + $"{(nonNull == expect ? "" : "  ← **块数不对，这本身就是洞**")}\n");

            // 挑最近的 4 块
            var best = new int[4];
            var bestDot = new float[4];

            for (int k = 0; k < 4; k++) { best[k] = -1; bestDot[k] = -2f; }

            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i] == null) continue;

                UnityEngine.Vector3 c = meshes[i].bounds.center;

                if (c.sqrMagnitude < 1e-6f) continue;

                float d = UnityEngine.Vector3.Dot(c.normalized, up);

                for (int k = 0; k < 4; k++)
                {
                    if (d <= bestDot[k]) continue;

                    for (int j = 3; j > k; j--) { best[j] = best[j - 1]; bestDot[j] = bestDot[j - 1]; }

                    best[k] = i;
                    bestDot[k] = d;

                    break;
                }
            }

            float cosLimit = UnityEngine.Mathf.Cos(15f / p.realRadius);
            int near = 0;
            int belowSea = 0;
            float lo = float.MaxValue;
            float hi = float.MinValue;

            for (int k = 0; k < 4; k++)
            {
                if (best[k] < 0) continue;

                UnityEngine.Vector3[] mv = meshes[best[k]].vertices;

                sb.Append($"    最近第 {k + 1} 块：#{best[k]}，顶点 {mv.Length} 个"
                          + $"（每块应为 41² = {(p.precision / p.segment + 1) * (p.precision / p.segment + 1)}）\n");

                foreach (UnityEngine.Vector3 v in mv)
                {
                    float mag = v.magnitude;

                    if (mag < 1e-3f) continue;

                    if (UnityEngine.Vector3.Dot(v / mag, up) < cosLimit) continue;

                    near++;

                    if (mag < lo) lo = mag;
                    if (mag > hi) hi = mag;
                    if (mag < ocean) belowSea++;
                }
            }

            if (near == 0)
            {
                sb.Append("    → **这四块里没有任何顶点落在玩家 15 格以内**"
                          + "——脚下这块地的几何压根不在，洞就是这么来的\n");

                return;
            }

            sb.Append($"    15 格内网格顶点 {near} 个：最低 {lo:0.000}、最高 {hi:0.000}，"
                      + $"低于海面的有 {belowSea} 个\n");

            // **判据是「几何 vs 数据」，不是「有没有水」。**
            // 上一版只看 belowSea，于是两侧一致地报出一个 0.2 深的小水洼时，
            // 它照样打「几何沉下去了、heightData 是干的」——自相矛盾。
            // 地基基准面那个 bug 的特征是几何比数据**低一大截**（实测 200.2 vs 400.2），
            // 所以拿两个最低值相减才是真正的判据。
            float gap = float.IsNaN(_dataMinNear) ? float.NaN : lo - _dataMinNear;

            if (float.IsNaN(gap))
            {
                sb.Append("    → 拿不到 heightData 的对照值，无法判定\n");
            }
            else if (gap < -1f)
            {
                sb.Append($"    → **几何比地形数据低了 {-gap:0.000} 格**"
                          + $"（几何 {lo:0.000} vs 数据 {_dataMinNear:0.000}）"
                          + "——地基基准面那一类的 bug，去查 PlanetModPlanePatches 有没有生效\n");
            }
            else
            {
                sb.Append($"    → **几何和地形数据一致**（相差 {gap:+0.000;-0.000;0.000}）。"
                          + $"{(belowSea > 0 ? "那几个低于海面的顶点是真实的小水洼，属正常地形" : "附近也没有水")}\n");
            }
        }

        /// <summary>
        /// 取引导整平中心处的地形高度。用的是原版自己的 <c>QueryHeight</c>，
        /// 不重新实现插值——它要一个单位向量（方法开头就 <c>Normalize()</c>）。
        /// </summary>
        private static float SampleHeight(GuideMissionStandardMode g)
        {
            PlanetData p = g.localPlanet;

            if (p == null || p.data == null) return float.NaN;

            return p.data.QueryHeight(g.targetPos.normalized);
        }

        /// <summary>
        /// 从 <c>modData</c> 的长度反推它当初是按哪个精度分配的：
        /// <c>len = (p+1)²×2</c> → <c>p = sqrt(len/2) - 1</c>。
        /// 报「旧的那份对应精度 200」比报「长度 80802」有用得多——前者直接说出
        /// 那是不是一份原版尺寸的存档。
        /// </summary>
        private static int ImpliedPrecision(int modDataLength)
        {
            if (modDataLength <= 0) return -1;

            return (int)Math.Round(Math.Sqrt(modDataLength / 2.0)) - 1;
        }

        /// <summary>
        /// 星系生成完了报一次总数。<b>这是「那个场景到底发生过没有」那条问题的答案</b>——
        /// 开机状态行只能报意图（那时星系还没生成），首颗行的一次性日志只能报一颗，
        /// 两者都答不了「这一局到底改了几颗」。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UniverseGen), nameof(UniverseGen.CreateGalaxy))]
        private static void ReportGalaxy()
        {
            if (!Enabled) return;

            if (Interlocked.Exchange(ref _galaxyLogged, 1) != 0) return;

            long resized = Interlocked.Read(ref _resized);
            long skipped = Interlocked.Read(ref _skippedNonStock);

            ProjectEdenPlugin.Log.LogInfo(
                $"行星放大：本次星系生成共放大 {resized} 颗，跳过 {skipped} 颗"
                + "（气态巨星和已被别的 mod 动过的那些）。"
                + "**放大 0 颗**就说明守卫没过——那三个数不是原版的 200/200/5");
        }

        /// <summary>
        /// 开机状态行。每一种状态都打，包括无聊的那些：没有这一行，
        /// 「开关关着」和「这段代码压根没进 DLL」在日志里长得一模一样。
        /// </summary>
        internal static void Report()
        {
            string where = Utils.JsonHelper.OverridePath("planet");

            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogWarning("行星放大：读不到 planet.json，未启用");

                return;
            }

            if (!Config.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "行星放大：**已手动关闭**（默认是开的）。行星保持原版半径 200，"
                    + "老存档可以照常打开。想要 4 倍可建造面积就把 enabled 改回 true，"
                    + $"但那要配一局新档——建筑坐标的模长约等于半径。开关在 {where}");

                return;
            }

            if (Radius <= 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"行星放大：开关开着，但 radiusMultiplier = {Config.radiusMultiplier} 解析不出合法半径，未启用。"
                    + $"请填一个正数（1.0 = 原版），见 {where}");

                return;
            }

            if (_blockedByGs2)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "行星放大：开关开着，但检测到**银河尺度（GalacticScale 2）**，所以**不生效**。"
                    + "GS2 自己就在决定每颗星的半径，我们再乘一道会把它的尺寸逻辑和 scale 一起打乱。"
                    + "想用本开关请先卸掉 GS2（同样需要新档）");

                return;
            }

            float actual = Radius / StockRadius;

            if (Math.Abs(actual - Config.radiusMultiplier) > 0.001f)
                ProjectEdenPlugin.Log.LogWarning(
                    $"行星放大：配置写的是 {Config.radiusMultiplier:0.000}×，"
                    + $"已吸附到 {actual:0.000}×（半径 {Radius}）。"
                    + $"半径必须是 {TileVerts} 的倍数且落在 [{MinRadius}, {MaxRadius}] 内——"
                    + "否则地形分块铺不满球面。**按吸附后的数跑**");

            ProjectEdenPlugin.Log.LogWarning(
                $"行星放大：**已开启**，半径 {StockRadius:0} → {Radius}（{actual:0.00}×），"
                + $"精度 {StockPrecision} → {Precision}、分块 {StockSegment} → {Segment}，"
                + $"地形网格 {StockSegmentMeshCount} → {MeshCount(Segment)} 块（数组会一并撑长），"
                + $"可建造格位约 {actual * actual:0.0} 倍。**这是默认值，也是不可逆的。**\n"
                + "⚠ **在 1.12.0 或更早版本下存的档，用这一版打开就废了**"
                + "——建筑坐标的模长约等于半径，星球从 200 变成 400 之后每一座建筑都在地下 200 格。"
                + $"要继续用老存档，请把 {where} 的 enabled 改成 false 并重开游戏。\n"
                + "同理，改倍率等于重开档。"
                + $"内存每颗星约 {5.0 * actual * actual:0.0} MB（原版约 5 MB），"
                + "且已加载的和被扫描过的都要算");
        }
    }
}
