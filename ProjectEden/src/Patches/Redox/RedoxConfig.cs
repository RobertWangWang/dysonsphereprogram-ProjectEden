#pragma warning disable 649 // 字段由 JSON 反序列化赋值

using System;

namespace ProjectEden.Utils
{
    /// <summary>
    /// <c>data/redox.json</c>：氧化还原燃烧厂。
    ///
    /// <b>为什么单独一个文件而不是塞进 megabuildings.json。</b>
    /// <see cref="JsonHelper"/> 的磁盘覆盖是<b>整份文件</b>的，把它塞进
    /// megabuildings.json 就意味着想调一个配氧比得连带遮蔽十座建筑的全部配置，
    /// 此后对内嵌 megabuildings.json 的任何改动都会静默失效。这是 CustomID.cfg
    /// 那个坑放大版，cargoprobe.json 已经为同一条理由单独成文件过一次。
    /// </summary>
    [Serializable]
    internal class RedoxConfig
    {
        public bool enabled;

        /// <summary>燃烧厂本体的物品 ID，用来和 megabuildings.json 那一条对上。</summary>
        public int plantItemId;

        public int recipeType;
        public int recipeId;
        public int recipeGridIndex;
        public int timeSpend;

        /// <summary>一次配方吃几件还原剂。氧化剂的份数由配平方程和配氧比算出来。</summary>
        public int reducerParts;

        public int ratioMin;
        public int ratioMax;
        public int ratioDefault;

        /// <summary>富氧对药柱密度的指数。0.5 即密度乘 √φ。</summary>
        public float densityExponent;

        public RedoxAgentEntry[] reducers;
        public RedoxAgentEntry[] oxidizers;
        public RedoxTierEntry[] tiers;
    }

    /// <summary>
    /// 一种还原剂或氧化剂。<c>ref</c> 指本 mod 的物品，<c>id</c> 指原版物品。
    /// </summary>
    [Serializable]
    internal class RedoxAgentEntry
    {
        /// <summary>本 mod 物品的引用名（items 段的 key，或 <c>矿种.ore</c> / <c>矿种.ingot</c>）。</summary>
        public string @ref;

        /// <summary>原版物品 ID。和 <c>ref</c> 二选一。</summary>
        public int id;

        /// <summary>中文名。<b>会和真实 proto 的 Name 交叉核对</b>，对不上就拒绝并报 ERROR。</summary>
        public string name;

        /// <summary>
        /// 每件要消耗（还原剂）或能放出（氧化剂）几个氧原子，由配平方程给出。
        /// </summary>
        public float oxygen;
    }

    /// <summary>一档药柱。</summary>
    [Serializable]
    internal class RedoxTierEntry
    {
        public string key;
        public int itemId;
        public string name;
        public string description;

        /// <summary>一根药柱的热值（焦耳）。= 4 × 该档代表能量密度。</summary>
        public long heatValue;

        /// <summary>进这一档所需的最低能量密度（MJ / 每件投料）。</summary>
        public float minDensity;
    }
}
