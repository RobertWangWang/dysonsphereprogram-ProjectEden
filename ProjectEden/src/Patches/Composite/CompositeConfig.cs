#pragma warning disable 649 // 字段由 JSON 反序列化赋值

using System;

namespace ProjectEden.Patches
{
    /// <summary>data/composite.json 的映射类型。</summary>
    [Serializable]
    internal class CompositeConfig
    {
        public bool enabled;

        /// <summary>配方 ID。配方本体写在 ores.json 里，这里只用来认它。</summary>
        public int recipeId;

        /// <summary>基体 + 合金的总份数。滑条的分辨率就是 1/totalParts。</summary>
        public int totalParts;

        /// <summary>基体物品的引用名（ores.json items 段的 key）。</summary>
        public string matrixRef;

        /// <summary>
        /// 基体自己的四维。<b>它不进 metals.json</b> —— 那张表是给金属的，基体是有机物。
        /// 硬度与导电接近零，这正是「复合材的硬度和导电不可能超过填料」的原因。
        /// </summary>
        public CompositeAxes matrixAxes;

        /// <summary>可选填料的引用名。</summary>
        public string[] candidates;

        public CompositeGradeEntry[] grades;

        public int baseYield;
        public float toughnessAnchor;
        public float toughnessExponent;
        public float yieldMin;
        public float yieldMax;

        /// <summary>渗流阈值：低于它，金属是孤岛，复合材基本不导电。</summary>
        public float percolationStart;

        /// <summary>渗流之后到基本饱和之间的跨度。</summary>
        public float percolationSpan;

        /// <summary>刚化惩罚强度：配比越高，韧性掉得越快。</summary>
        public float rigidizePenalty;
    }

    [Serializable]
    internal class CompositeAxes
    {
        public float hardness;
        public float toughness;
        public float corrosion;
        public float conductivity;
    }

    [Serializable]
    internal class CompositeGradeEntry
    {
        /// <summary>产物物品的引用名（ores.json items 段的 key）。</summary>
        public string @ref;

        /// <summary>中文名，用来和注册出来的 proto 交叉核对——写错了要响，不能静默。</summary>
        public string name;

        /// <summary>进入该等级所需的最小合金份数。滑条落在哪一段就出哪一级。</summary>
        public int minParts;

        /// <summary>该等级的耗时（tick）。等级越高连通度越高，长得越慢。</summary>
        public int timeSpend;
    }
}
