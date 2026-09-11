#pragma warning disable 649 // 字段由 JSON 反序列化赋值

using System;

namespace ProjectEden.Patches
{
    /// <summary>
    /// data/ammo.json 的映射类型：合金弹药。
    ///
    /// <b>玩法是「两种合金 → 一档弹药」，而配方不公开。</b> 合成面板里只有一条
    /// 『合金弹药』，具体哪一对给哪一档得自己试——所以物品提示栏里未发现档位的
    /// 伤害和发数显示成 ???。
    ///
    /// <b>为什么伤害必须分档。</b> 伤害是 <c>ItemProto.Ability</c>，按 proto 存
    /// （<c>TurretComponent.SetNewItem</c> 里 <c>bulletDamage = proto.Ability</c>，
    /// <c>Mecha.ammoDamage</c> 也读它）。一个伤害值就得有一个物品，这一点躲不掉；
    /// 档位少才吃得消物品格位。产量走 <c>productCounts[0]</c>，连续，不占物品。
    /// </summary>
    [Serializable]
    internal class AmmoConfig
    {
        public bool enabled;

        /// <summary>1 = Bullet（机枪类）。见 EAmmoType</summary>
        public int ammoType;

        /// <summary>
        /// 伤害与发数的锚点物品。<b>留 0 则自动取「同类型、ID 最小」的那个原版弹药。</b>
        ///
        /// 配的是倍率不是绝对值：原版弹药的 <c>Ability</c> / <c>HpMax</c> 存在
        /// resources.assets 的 proto 里，离线看不到、反编译也查不到，硬写等于猜。
        /// 乘出来的真值会打进开局日志，想钉死照日志看即可。
        /// </summary>
        public int anchorItemId;

        public int recipeId;
        public int recipeType;
        public int timeSpend;

        /// <summary>一炉吃两种合金各几份。份数不变，变的是两个槽指向哪个物品</summary>
        public int partsA;
        public int partsB;

        /// <summary>能拿来做弹药的合金，写 ref。留空则取 alloys.json 里全部</summary>
        public string[] candidates;

        public AmmoTierEntry[] tiers;

        // ── 组合怎么换算 ──────────────────────────────────────

        /// <summary>侵彻分里硬度占的权重。硬度是主项——穿甲靠硬度</summary>
        public float hardnessWeight;

        /// <summary>侵彻分里耐蚀占的权重，当成「不易烧蚀」的小加成</summary>
        public float corrosionWeight;

        public int baseYield;

        /// <summary>产量公式的韧性基准值</summary>
        public float toughnessAnchor;

        /// <summary>产量随韧性变化的指数。&lt; 1 是递减回报</summary>
        public float toughnessExponent;

        public float yieldMin;
        public float yieldMax;

        /// <summary>未炼出过的档位，提示栏里把伤害和发数显示成 ???</summary>
        public bool hideUndiscovered;
    }

    /// <summary>一档弹药。伤害和发数都是相对锚点的倍率。</summary>
    [Serializable]
    internal class AmmoTierEntry
    {
        /// <summary>ores.json 风格的 key，日志和 i18n 用</summary>
        public string key;

        /// <summary>物品 ID。<b>进存档</b>，定下来别改</summary>
        public int itemId;

        public string name;
        public string description;

        /// <summary>相对锚点弹药的伤害倍率</summary>
        public float damage;

        /// <summary>相对锚点弹药的发数倍率</summary>
        public float rounds;

        /// <summary>命中这一档要求的侵彻分下限。第一档写 0</summary>
        public float minScore;
    }
}
