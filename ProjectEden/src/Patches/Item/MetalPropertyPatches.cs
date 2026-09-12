#pragma warning disable 649 // MetalsConfig 的字段由 JSON 反序列化赋值

using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 给金属加「硬度 / 韧性 / 耐蚀 / 导电」四维属性，显示在物品提示栏里。配置在 <c>metals.json</c>。
    ///
    /// <b>怎么挂上去的。</b> 原版提示栏的属性行由 <c>ItemProto.DescFields</c>（一串字段号）驱动，
    /// 每个号在 <c>ItemProto.GetPropName</c> / <c>GetPropValue</c> 的 switch 里对应一行。
    /// 两个 switch <b>都有兜底分支</b>（分别返回 <c>??</c> 和 <c>-</c>），所以自定义字段号只要
    /// postfix 接管就行，不用 transpiler——和 <c>RecipeTypeNamePatches</c> 处理「制造于」是同一个路子。
    ///
    /// <b>字段号 0~73 被原版占了</b>（两个 switch 的分支数都是 74），74 起是空的。
    /// 这些号<b>不进存档</b>，只在运行时拼提示栏文本，所以撞了最多是显示串行，不会污染档。
    ///
    /// <b>行数没有上限</b>：属性行拼进的是 <c>UIItemTip</c> 的<b>单个</b> <c>propsText</c> 控件，
    /// 不是定长数组——这一点和悬停信息框的 <c>icons</c> 正相反，那个才是定长的。
    ///
    /// <b>目前只是展示，不驱动任何玩法。</b> 它是合金配方的数据底座：
    /// 有了每种金属的四维值，合金的属性才谈得上「按成分算」而不是拍脑袋。
    /// </summary>
    [HarmonyPatch]
    internal static class MetalPropertyPatches
    {
        private static MetalsConfig Config => ProjectEdenPlugin.MetalsConfig;

        /// <summary>原版两个 switch 的分支数都是 74，所以 74 以下不能碰。</summary>
        private const int VanillaFieldCount = 74;

        /// <summary>第 i 个属性的字段号。</summary>
        private static int _base;

        /// <summary>属性<b>显示名</b>，下标即「字段号 − _base」。会被本地化换掉</summary>
        private static string[] _names = new string[0];

        /// <summary>
        /// 属性的<b>键</b>（hardness / toughness / …），下标和 <see cref="_names"/> 对齐。
        ///
        /// 和显示名分开存，是因为显示名会被本地化换掉——按显示名查轴，切到英文就全查不到了。
        /// </summary>
        private static string[] _keys = new string[0];

        /// <summary>物品 ID → 各属性的值，顺序和 <see cref="_names"/> 一致。</summary>
        private static readonly Dictionary<int, int[]> Values = new Dictionary<int, int[]>();

        /// <summary>
        /// 某个物品某一轴的值，查不到返回 0。给合金弹药按组合算侵彻分用。
        ///
        /// 轴名用的是 metals.json 里 <c>values</c> 的键（hardness / toughness /
        /// corrosion / conductivity），不是显示名——显示名会被本地化换掉。
        /// </summary>
        internal static int Axis(int itemId, string axis)
        {
            if (!Values.TryGetValue(itemId, out int[] row)) return 0;

            for (var i = 0; i < _keys.Length && i < row.Length; i++)
                if (_keys[i] == axis)
                    return row[i];

            return 0;
        }

        /// <summary>这个物品有没有四维属性行。</summary>
        internal static bool Has(int itemId) => Values.ContainsKey(itemId);

        /// <summary>
        /// 本表占用的字段号区间的<b>下一个</b>号。别的属性表要从这里或更大处起，
        /// 否则两边会认领同一个字段号，提示栏静默串行。
        ///
        /// <b>读的是注册后的实际值，不是配置值</b>——配置里的 fieldIdBase 会被
        /// VanillaFieldCount 抬高，按配置算会算矮。所以查它的人必须排在
        /// <see cref="OnPostAddData"/> 之后。
        /// </summary>
        internal static int FieldsEnd => _names.Length > 0 ? _base + _names.Length : VanillaFieldCount;

        internal static void OnPostAddData()
        {
            Values.Clear();
            _names = new string[0];
            _keys = new string[0];

            if (Config == null || !Config.enabled || Config.properties == null || Config.metals == null) return;

            _base = Config.fieldIdBase >= VanillaFieldCount ? Config.fieldIdBase : VanillaFieldCount;

            if (Config.fieldIdBase > 0 && Config.fieldIdBase < VanillaFieldCount)
                ProjectEdenPlugin.Log.LogWarning(
                    $"金属属性：fieldIdBase 配成了 {Config.fieldIdBase}，会和原版的属性行撞号" +
                    $"（原版占 0~{VanillaFieldCount - 1}），已改用 {_base}");

            var names = new List<string>();
            var keys = new List<string>();

            foreach (MetalPropertyEntry prop in Config.properties)
                if (prop != null && !string.IsNullOrEmpty(prop.key))
                {
                    names.Add(string.IsNullOrEmpty(prop.name) ? prop.key : prop.name);
                    keys.Add(prop.key);
                }

            _names = names.ToArray();
            _keys = keys.ToArray();

            if (_names.Length == 0) return;

            var fields = new int[_names.Length];

            for (var i = 0; i < fields.Length; i++) fields[i] = _base + i;

            var done = 0;

            foreach (MetalEntry metal in Config.metals)
            {
                if (metal == null) continue;

                // 本 mod 的物品走 ref，原版的写 itemId——ID 撞车时会自动顺延，
                // 对自家物品写死数字会一声不吭地指到别人家去
                int itemId = string.IsNullOrEmpty(metal.@ref)
                    ? metal.itemId
                    : OreRegistry.FindItemIdByRef(metal.@ref);

                ItemProto item = itemId > 0 ? LDB.items.Select(itemId) : null;

                if (item == null)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"金属属性：{metal.name ?? metal.@ref ?? metal.itemId.ToString()} 解析不出物品，跳过");

                    continue;
                }

                // 原版物品是按 itemId 写死的（钢材 1103、钛合金 1107）。ID 写错的话
                // 属性会一声不响挂到别人身上——所以拿配置里的 name 和真实 proto 名核一次。
                // 这个 name 本来只是给日志看的，这里让它顺便当一道校验。
                // 比 Name 不比 name：后者是 Name.Translate() 的结果，切英文之后
                // 和配置里的中文永远对不上，会把一道有用的校验变成满屏误报
                if (!string.IsNullOrEmpty(metal.name) && metal.name != item.Name)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"金属属性：配置里写的是「{metal.name}」，但 " +
                        $"{(string.IsNullOrEmpty(metal.@ref) ? "itemId " + metal.itemId : "ref " + metal.@ref)} " +
                        $"解析出来的是「{item.Name}」({item.ID})——ID 或 ref 可能写错了");

                var row = new int[_names.Length];

                for (var i = 0; i < Config.properties.Length && i < row.Length; i++)
                {
                    MetalPropertyEntry prop = Config.properties[i];

                    if (prop?.key != null && metal.values != null && metal.values.TryGetValue(prop.key, out int v))
                        row[i] = v;
                }

                Values[item.ID] = row;

                AppendFields(item, fields);

                done++;
            }

            if (done == 0)
            {
                ProjectEdenPlugin.Log.LogWarning("金属属性：一个金属都没配上，提示栏不会有这几行");
                return;
            }

            // 报<b>显示出来的那一份</b>，不是配置里的原串。这两者一样才说明
            // GetPropName 真的走了 Translate —— 上一版漏了那一步，而译文一直都在
            // i18n.json 里，所以日志看着完全正常、英文客户端的提示栏里却是四行中文。
            var shown = new string[_names.Length];

            for (var i = 0; i < _names.Length; i++) shown[i] = _names[i].Translate();

            ProjectEdenPlugin.Log.LogInfo(
                $"金属属性已挂上 {done} 种金属：{string.Join(" / ", shown)}" +
                $"（字段号 {_base}~{_base + _names.Length - 1}）");
        }

        /// <summary>
        /// 把属性行追加到物品原有的 <c>DescFields</c> 后面。
        ///
        /// <b>是追加不是覆盖</b>：原版金属块本来就有自己的属性行（比如「能量」「类型」），
        /// 直接换掉会把它们抹了。重复调用也不会加两遍。
        /// </summary>
        private static void AppendFields(ItemProto item, int[] fields)
        {
            int[] have = item.DescFields ?? new int[0];
            var add = new List<int>();

            foreach (int f in fields)
                if (Array.IndexOf(have, f) < 0)
                    add.Add(f);

            if (add.Count == 0) return;

            int at = have.Length;

            Array.Resize(ref have, have.Length + add.Count);

            for (var i = 0; i < add.Count; i++) have[at + i] = add[i];

            item.DescFields = have;
        }

        /// <summary>
        /// 这一行是不是我们加的。
        ///
        /// <b>参数 <c>index</c> 是 <c>DescFields</c> 里的下标，不是字段号</b>——
        /// 原版是 <c>switch (DescFields[index])</c>，先按下标取出字段号再 switch。
        /// 头一版照着签名想当然，拿 index 直接和字段号比，结果永远不成立：
        /// 提示栏里四行都在，但显示的是原版兜底的 ?? 和 −。
        /// <b>方法体要读，签名不能只看名字。</b>
        /// </summary>
        private static bool Mine(ItemProto item, int index, out int slot)
        {
            slot = -1;

            int[] fields = item?.DescFields;

            if (fields == null || index < 0 || index >= fields.Length) return false;

            slot = fields[index] - _base;

            return _names.Length > 0 && slot >= 0 && slot < _names.Length;
        }

        /// <summary>
        /// 属性行的名字。原版对认不出的字段号返回 <c>??</c>，这里把我们那几个换掉。
        ///
        /// <b><c>_names</c> 里是 metals.json 写的中文原串，要过一道 Translate。</b>
        /// 原版这一行自己也是这么干的（<c>ItemProto.GetPropName</c> 里全是
        /// <c>"风能".Translate()</c>），漏掉的话 i18n.json 里那四条译文没有任何人去查，
        /// 英文客户端的提示栏里就只有这四行是中文。<c>Translate</c> 对没登记的键原样返回，
        /// 所以 metals.json 里新加一条属性没有译文也不会炸。
        ///
        /// 和 <see cref="AlloyRatioPatches.AxisName"/> 一样<b>在调用时翻译</b>，
        /// 不在 <c>Collect</c> 里预先翻好——语言可以中途切换。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemProto), nameof(ItemProto.GetPropName))]
        private static void GetPropName(ItemProto __instance, int index, ref string __result)
        {
            if (Mine(__instance, index, out int slot)) __result = _names[slot].Translate();
        }

        /// <summary>
        /// 属性行的值。原版对认不出的字段号返回 <c>-</c>。
        ///
        /// 这是<b>实例方法</b>，所以 <c>__instance</c> 就是被查询的那个物品——
        /// 不用另想办法判断「现在问的是谁」。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemProto), nameof(ItemProto.GetPropValue))]
        private static void GetPropValue(ItemProto __instance, int index, ref string __result)
        {
            if (!Mine(__instance, index, out int slot)) return;

            __result = __instance != null && Values.TryGetValue(__instance.ID, out int[] row)
                ? row[slot].ToString()
                : "-";
        }
    }

    /// <summary>metals.json 的结构。</summary>
    [Serializable]
    internal class MetalsConfig
    {
        public bool enabled;

        /// <summary>自定义属性行的起始字段号。原版占 0~73，所以不能低于 74</summary>
        public int fieldIdBase;

        /// <summary>属性定义，顺序即显示顺序</summary>
        public MetalPropertyEntry[] properties;

        public MetalEntry[] metals;
    }

    /// <summary>一维属性的定义。</summary>
    [Serializable]
    internal class MetalPropertyEntry
    {
        /// <summary>在 metals 的 values 里用来取值的键</summary>
        public string key;

        /// <summary>提示栏里显示的名字</summary>
        public string name;
    }

    /// <summary>一种金属的属性值。</summary>
    [Serializable]
    internal class MetalEntry
    {
        /// <summary>原版物品 ID</summary>
        public int itemId;

        /// <summary>本 mod 的物品：矿种 key 加 .ingot / .ore。<b>别对自家物品写死 itemId</b></summary>
        public string @ref;

        /// <summary>日志和配置可读性用，不参与匹配</summary>
        public string name;

        /// <summary>属性键 → 值</summary>
        public Dictionary<string, int> values;
    }
}
