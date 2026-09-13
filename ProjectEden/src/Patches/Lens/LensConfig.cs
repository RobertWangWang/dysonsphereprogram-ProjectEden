#pragma warning disable 649

using System;

namespace ProjectEden.Patches
{
    /// <summary>
    /// <c>data/lens.json</c>：活性透镜的几个旋钮。
    ///
    /// <b>单独一个文件是有意的</b>——<c>JsonHelper</c> 的磁盘覆盖是整文件生效的，
    /// 把这几个数塞进 <c>ores.json</c> 就等于为了调一个倍率去影子掉整张矿脉表。
    /// 同 <c>cargoprobe.json</c> 的先例。
    /// </summary>
    [Serializable]
    internal class LensConfig
    {
        public bool enabled;

        /// <summary><c>ores.json</c> 的 <c>items</c> 段里那条的 <c>key</c>。<b>不写裸 ID</b></summary>
        public string lensItemKey;

        /// <summary>
        /// 原版催化剂的 <c>ItemProto.Name</c>（引力透镜）。按名字反查，不写裸号——
        /// 原版 proto 在 <c>resources.assets</c> 里，离线枚举不出来。
        /// </summary>
        public string defaultCatalystName;

        /// <summary>发电倍率，相对引力透镜。1 = 和引力透镜一样</summary>
        public float powerMultiplier;

        /// <summary>临界光子倍率，相对引力透镜。1 = 和引力透镜一样</summary>
        public float photonMultiplier;

        /// <summary>自愈速率，<b>必须严格小于 1</b>，否则透镜成了永动机</summary>
        public float healRate;
    }
}
