#pragma warning disable 649 // 字段由 JSON 反序列化赋值

using System;

namespace ProjectEden.Patches
{
    /// <summary>
    /// data/abnormality.json 的映射类型：一个开关，<b>默认开启</b>。
    ///
    /// <b>单独一个文件，不并进 cheats.json，两条理由。</b>
    /// 一是 <c>JsonHelper</c> 的磁盘覆盖<b>整文件生效</b>，为了翻这一个布尔值去影子掉
    /// 整份作弊配置，以后对内嵌 cheats.json 的每一次修改都会被静默忽略——
    /// CLAUDE.md 里记着的 LDBTool <c>CustomID.cfg</c> 同款陷阱，
    /// <c>cargoprobe.json</c> 已经为同一条理由单独成过一次文件。
    /// 二是性质不同：cheats.json 那六条是<b>主动绕过游戏规则</b>，这一条是修一个
    /// <b>装了内容就必然触发的误伤</b>——把六个作弊开关全关掉，它照样会响。
    ///
    /// 为什么必然触发，见 <see cref="AbnormalityPatches"/> 的类注释。
    /// </summary>
    [Serializable]
    internal class AbnormalityConfig
    {
        /// <summary>
        /// 屏蔽「数据异常」判定。<b>默认 true。</b>
        ///
        /// 关掉之后本 mod 对原版的异常判定不做任何事，也就回到
        /// 「读档和存档各记三笔 ABN_ProtoData，成就和元数据全程关着」的状态。
        /// </summary>
        public bool enabled;
    }
}
