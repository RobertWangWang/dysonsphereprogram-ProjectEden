#pragma warning disable 649 // 字段由 JSON 反序列化赋值

using System;

namespace ProjectEden.Patches
{
    /// <summary>data/proliferator.json 的映射类型。</summary>
    [Serializable]
    internal class ProliferatorConfig
    {
        public bool enabled;

        /// <summary>配方 ID。配方本体写在 ores.json 里，这里只用来认它。</summary>
        public int recipeId;

        /// <summary>两个选料位共用的候选名单（ores.json items 段的 key）。</summary>
        public string[] candidates;

        /// <summary>性格分界：硬度/韧性 ≥ 它出浓缩型。</summary>
        public float characterThreshold;

        /// <summary>档次分界：耐蚀+导电 ≥ 它出 Mk.V。</summary>
        public float tierThreshold;

        public ProliferatorOutcomeEntry[] outcomes;
    }

    [Serializable]
    internal class ProliferatorOutcomeEntry
    {
        /// <summary>产物物品的引用名（ores.json items 段的 key）。</summary>
        public string @ref;

        /// <summary>中文名，用来和注册出来的 proto 交叉核对——写错了要响，不能静默。</summary>
        public string name;

        /// <summary>档次高的那一半（Mk.V）。</summary>
        public bool highTier;

        /// <summary>浓缩型（高等级、少喷数）。</summary>
        public bool dense;

        /// <summary>
        /// 等级与喷数，<b>只用于交叉核对</b>。
        ///
        /// 引擎真正读的是 <c>ItemProto.Ability</c> / <c>HpMax</c>，那两个值写在
        /// ores.json 的物品上。两处对不上会报错——否则就是两份配置各说各话，
        /// 而面板显示的和喷涂机实际喷的会悄悄分叉。
        /// </summary>
        public int ability;

        public int hpMax;

        /// <summary>这一格产几个。</summary>
        public int yield;
    }
}
