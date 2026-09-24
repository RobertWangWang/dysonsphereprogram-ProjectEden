#pragma warning disable 649 // 字段由 JSON 反序列化赋值

using System;

namespace ProjectEden.Patches
{
    /// <summary>
    /// data/planet.json 的映射类型：把行星做大。<b>默认关</b>。
    ///
    /// <para><b>单独一个文件</b>，理由和 cargoprobe.json / abnormality.json / perfprobe.json
    /// 一样：<c>JsonHelper</c> 的磁盘覆盖<b>整文件生效</b>，为了翻一个 bool 去影子掉
    /// 一份内容配置，之后对内嵌那份的每一次修改都会被静默忽略。</para>
    ///
    /// <para><b>这是本仓库唯一一个开了就没法回头的开关，比 preloader 还硬。</b>
    /// preloader 那条是「卸了存档就打不开」；这一条是<b>「配置里这个数一改就等于重开」</b>
    /// ——建筑坐标存的是行星局部坐标、模长约等于 <c>radius</c>。详见
    /// <see cref="PlanetRadiusPatches"/> 的类注释。</para>
    /// </summary>
    [Serializable]
    internal class PlanetRadiusConfig
    {
        /// <summary>
        /// 总开关。<b>默认 false。</b>只在全新存档上开。
        /// </summary>
        public bool enabled;

        /// <summary>
        /// 半径倍率，基准是原版的 200。
        ///
        /// <para>会被<b>吸附到最近的合法半径</b>（40 的倍数，见
        /// <see cref="PlanetRadiusPatches.ResolveRadius"/>）并夹在 [200, 480] 之内。
        /// 吸附结果和夹取都会打进日志——一个「我按 1.5 配的，它按 1.6 跑」的静默偏差
        /// 比报错还难查。</para>
        /// </summary>
        public float radiusMultiplier;

        /// <summary>
        /// 出生点诊断探针。<b>默认 false</b>。
        ///
        /// <para>打开之后会在落地 5 秒后做一次<b>全量扫描</b>：暴力遍历约 64 万个地形顶点、
        /// 再读最近四块网格的顶点数组，把「地形数据」和「真正被画出来/拿去碰撞的几何」
        /// 摆在一起对质。一次性，约几十毫秒加几 MB 垃圾——<b>够贵，所以默认关着</b>。</para>
        ///
        /// <para>它是查出「地基基准面写死在半径 200」那个根因的工具
        /// （见 <see cref="PlanetModPlanePatches"/>），留着是因为这一类问题
        /// ——<b>数据对、几何错</b>——只有这种两侧对质才看得出来。</para>
        /// </summary>
        public bool probe;

        /// <summary>
        /// 矿脉随面积缩放。见 <see cref="VeinScalingConfig"/>。
        /// </summary>
        public VeinScalingConfig veinScaling;
    }

    /// <summary>
    /// **把矿脉分布跟着行星面积一起放大。**
    ///
    /// <para><b>它存在的理由是一处实测的失衡，不是一个增强。</b>
    /// <c>PlanetAlgorithm.GenerateVeins</c> 读 <c>PlanetData.radius</c> 六次，
    /// 而<b>没有一次进入矿脉数量</b>——IL 0089 那处是 <c>2.1 / radius</c>（把每处矿脉的
    /// 角尺寸缩小，好让它的物理大小不变），其余五处全是高度/水面剔除的比较。
    /// 数量只来自 <c>ThemeProto.VeinSpot[i]</c>，是每个主题写死的整数。
    /// 所以半径翻倍之后，<b>同样多的矿脉被摊到 4 倍面积上</b>，密度悄悄掉到四分之一。</para>
    ///
    /// <para><b>三个旋钮各自有推导，都不是挑出来的数：</b></para>
    /// <list type="bullet">
    /// <item><b>数量</b>：×面积倍率。原版那些数是照半径 200 写的，不改等于被动缩水。</item>
    /// <item><b>种类</b>：<b>种–面积关系</b> <c>S = cA^z</c>——海岛生物地理学的经验律，
    /// 地质省份同理：更大的星球采样到更多成矿环境。z 取 0.25 时面积 ×4 给出
    /// <c>4^0.25 = 1.41</c>，即种类 +41%。</item>
    /// <item><b>稀有矿概率</b>：泊松。若每单位面积的成矿事件率为 λ，则
    /// <c>P(至少一处) = 1 − e^(−λA)</c>；由原版的 p₀ 反解 λA₀ 再代入 kA₀，得
    /// <c>p = 1 − (1−p₀)^k</c>。它<b>自动不会超过 1</b>，而且 <c>p₀ = 0</c>
    /// （「母星系不刷」那一格）会<b>自动保持 0</b>——不需要任何特判。</item>
    /// </list>
    ///
    /// <para><b>只影响还没生成的星球</b>（含老存档里没去过的），已经materialize 的星球
    /// 保留烘焙好的矿脉数据。<b>装了银河尺度则整段不生效</b>——它把矿脉生成整个换掉了，
    /// ThemeProto 根本不会被读。两件事启动时都会明说。</para>
    /// </summary>
    [Serializable]
    internal class VeinScalingConfig
    {
        /// <summary>总开关。面积倍率是 1 时它自己就是空操作。</summary>
        public bool enabled;

        /// <summary>数量：<c>VeinSpot × 面积倍率</c>。关掉则矿脉处数保持原版。</summary>
        public bool scaleSpots;

        /// <summary>种类：按种–面积关系给每个主题补上它原本没有的矿种。</summary>
        public bool extraTypes;

        /// <summary>
        /// 种–面积关系的指数 z。海岛生物地理学实测在 0.25~0.35，取下限 0.25 偏保守。
        /// 调大 → 每个主题补更多种类。
        /// </summary>
        public float speciesAreaExponent;

        /// <summary>
        /// 新补进来那些矿种的密度，相对该主题的铁矿。默认 0.35——
        /// <b>它们是伴生矿，不是主矿</b>：星球仍然该有专长。
        /// </summary>
        public float extraTypeDensity;

        /// <summary>稀有矿储量（<c>RareSettings[i*4+3]</c>）× 面积倍率。</summary>
        public bool scaleRareRichness;

        /// <summary>稀有矿概率按泊松公式提高（三格概率一起，见类注释）。</summary>
        public bool scaleRareChance;

        /// <summary>
        /// 每个主题打一行改写前后的对照。**默认开**——这一段改的是世界生成，
        /// 而世界生成的错误只会表现为「矿好像不太对」，不会报任何异常。
        /// </summary>
        public bool report;
    }
}
