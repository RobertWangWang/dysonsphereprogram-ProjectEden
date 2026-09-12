#pragma warning disable 649 // 字段由 JSON 反序列化赋值

using System;

namespace ProjectEden.Patches
{
    /// <summary>data/combustibles.json 的映射类型。</summary>
    [Serializable]
    internal class CombustiblesConfig
    {
        public bool enabled;

        /// <summary>machines.json 里那台发电厂的 key，只用来核对它确实注册上了。</summary>
        public string plantKey;

        /// <summary>
        /// 可燃液体专用的 <c>FuelType</c> 位。
        ///
        /// <b>同时是电厂的 <c>fuelMask</c>，两边必须一致</b>——注册时会核对。
        /// 它也是 tick 路径上唯一的判据：只有 <c>fuelMask</c> 等于它的发电机才逐台改效率，
        /// 否则火力发电厂烧甲醇时也会被改。
        /// </summary>
        public int fuelType;

        /// <summary>卡诺公式的冷端温度，开尔文。</summary>
        public float coldSideK;

        /// <summary>二级效率：实际效率与卡诺效率之比。真实大型热机 0.6~0.75。</summary>
        public float secondLaw;

        /// <summary>属性行的起始字段号。不得和 metals.json 的区间重叠。</summary>
        public int fieldIdBase;

        /// <summary>属性行的名字。显示时会过一道 <c>Translate</c>。</summary>
        public string propertyName;

        public CombustibleFuelEntry[] fuels;
    }

    [Serializable]
    internal class CombustibleFuelEntry
    {
        /// <summary>本 mod 物品的引用名（ores.json items 段的 key）。</summary>
        public string @ref;

        /// <summary>原版物品的 ID。和 <see cref="@ref"/> 二选一。</summary>
        public int itemId;

        /// <summary>
        /// 中文名，用来和解析出来的 proto 交叉核对。
        ///
        /// <b>写死原版 ID 是有风险的</b>——数字打错会一声不响地把温度挂到别的物品上。
        /// 比的是 <c>proto.Name</c>（原始键）不是 <c>proto.name</c>（译文），
        /// 否则切英文之后这道校验会变成满屏误报。
        /// </summary>
        public string name;

        /// <summary>这种燃料允许的热通道工作温度，摄氏度。</summary>
        public int celsius;
    }
}
