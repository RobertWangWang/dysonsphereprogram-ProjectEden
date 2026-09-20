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
    }
}
