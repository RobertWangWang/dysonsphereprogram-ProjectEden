using System.Collections.Generic;
using System.Text;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把「燃料类型位」和「发电建筑的 prefab 参数」打进启动日志。
    ///
    /// <b>为什么必须是运行时 dump。</b> 这两张表都在 <c>resources.assets</c> 里，
    /// 反编译读不到 —— 和星球主题表是同一种情况，做法照抄
    /// <see cref="OreRegistry"/> 的 <c>DumpThemes</c>：唯一的事实来源就是跑起来的 <c>LDB</c>。
    ///
    /// 它回答两个问题，都是「可燃液体发电厂」落地前必须先知道的：
    ///
    /// <list type="number">
    /// <item><b>哪个 <c>FuelType</c> 位是空的。</b> <c>ItemProto.InitFuelNeeds</c> 按<b>掩码值</b>
    /// 索引 <c>fuelNeeds</c>（<c>(mask &amp; proto.FuelType) != 0</c>），而
    /// <c>ItemProto..cctor</c> 里是 <c>new int[64][]</c> —— 所以合法位只有 bit 0~5，
    /// 一共六个，原版已经占掉低位几个。给新燃料开一位之前得先知道还剩哪几位。</item>
    /// <item><b>原版烧燃料的发电厂效率是多少。</b>
    /// <c>PowerGeneratorComponent.GenEnergyByFuel</c> 扣的燃料能量是
    /// <c>energy × useFuelPerTick / genEnergyPerTick</c>，所以
    /// <c>genEnergyPerTick / useFuelPerTick</c> <b>就是能量利用率</b>。
    /// 火力发电厂看着是 1:1（煤 2.7 MJ ÷ 2.16 MW = 1.25 s），但那是从面板数字倒推的，
    /// 不是读出来的。</item>
    /// </list>
    ///
    /// <b>它同时是 ABN_PowerGenerator 那条红线的参照物。</b>
    /// <c>CheckPowerGen</c> 判异常的条件是运行时的 <c>genEnergyPerTick</c> 高于 prefab 的
    /// 1.5 倍、或 <c>useFuelPerTick</c> 低于 prefab 的 0.7 倍。任何逐台改这两个字段的功能，
    /// 都要拿这里打出来的 prefab 值当基准。
    /// </summary>
    internal static class FuelSurvey
    {
        /// <summary><c>fuelNeeds</c> 的长度是 64，所以掩码只到 63，合法位就是 0~5。</summary>
        private const int FuelBits = 6;

        private static bool _done;

        internal static void OnPostAddData()
        {
            // 一次就够：这张表在一次运行里不会变
            if (_done) return;

            _done = true;

            DumpFuelTypes();
            DumpGenerators();
        }

        /// <summary>
        /// 扫 <c>LDB.items</c>，按 <c>FuelType</c> 分组，最后报还剩哪几位没人用。
        /// </summary>
        private static void DumpFuelTypes()
        {
            ItemProto[] items = LDB.items?.dataArray;

            if (items == null)
            {
                ProjectEdenPlugin.Log.LogWarning("燃料普查：LDB.items 还没建好，跳过");
                return;
            }

            // FuelType 值 → 该值下的物品，按值分组而不是按位分组：
            // 一个物品的 FuelType 理论上可以是多位的组合，按位分组会把它数两次
            var byType = new Dictionary<int, List<ItemProto>>();
            var usedBits = 0;

            foreach (ItemProto proto in items)
            {
                if (proto == null || proto.FuelType == 0) continue;

                usedBits |= proto.FuelType;

                if (!byType.TryGetValue(proto.FuelType, out List<ItemProto> list))
                {
                    list = new List<ItemProto>();
                    byType[proto.FuelType] = list;
                }

                list.Add(proto);
            }

            ProjectEdenPlugin.Log.LogInfo("── 燃料类型位（新燃料要开新位时照这里挑）──");

            if (byType.Count == 0)
            {
                // 报无聊的那一面：没有燃料和这段代码没跑起来，看日志得能分清
                ProjectEdenPlugin.Log.LogWarning("  一件带 FuelType 的物品都没有 —— 这不正常，原版至少有煤");
                return;
            }

            var types = new List<int>(byType.Keys);
            types.Sort();

            foreach (int type in types)
            {
                List<ItemProto> list = byType[type];
                var names = new StringBuilder();

                // 只列前四个，够认出这一位是干什么的就行
                for (var i = 0; i < list.Count && i < 4; i++)
                {
                    if (i > 0) names.Append('、');
                    names.Append(list[i].Name).Append('(').Append(list[i].ID).Append(')');
                }

                if (list.Count > 4) names.Append(" 等 ").Append(list.Count).Append(" 种");

                ProjectEdenPlugin.Log.LogInfo(
                    $"  FuelType {type,-3}（二进制 {System.Convert.ToString(type, 2).PadLeft(FuelBits, '0')}）"
                    + $"：{names}");
            }

            // 空位：bit 0~5 里没被任何物品用到的
            var free = new List<int>();

            for (var bit = 0; bit < FuelBits; bit++)
            {
                int value = 1 << bit;

                if ((usedBits & value) == 0) free.Add(value);
            }

            if (free.Count == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    $"  已用位掩码 {usedBits}，bit 0~{FuelBits - 1} 全被占满 —— "
                    + "没有空位可以给新燃料了，只能和现有类型共用一位（那样原版机器也会烧它）");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"  已用位掩码 {usedBits}，空位还有 {free.Count} 个：{string.Join("、", free.ConvertAll(v => v.ToString()).ToArray())}"
                    + $"（fuelNeeds 长度 64，所以最大只能到 {1 << (FuelBits - 1)}）");
        }

        /// <summary>
        /// 扫所有 <c>isPowerGen</c> 的 prefab，报它们的功率、耗料速率和两者之比。
        /// </summary>
        private static void DumpGenerators()
        {
            ItemProto[] items = LDB.items?.dataArray;

            if (items == null) return;

            ProjectEdenPlugin.Log.LogInfo("── 发电建筑 prefab（efficiency = genEnergyPerTick / useFuelPerTick）──");

            var count = 0;

            foreach (ItemProto proto in items)
            {
                PrefabDesc desc = proto?.prefabDesc;

                if (desc == null || !desc.isPowerGen) continue;

                count++;

                // 非燃料发电（光伏/风力/伽马/地热）的 useFuelPerTick 是 0，
                // 算比值会除零，所以分开报
                var kind = desc.photovoltaic ? "光伏"
                    : desc.windForcedPower ? "风力"
                    : desc.gammaRayReceiver ? "伽马"
                    : desc.geothermal ? "地热"
                    : "燃料";

                var line =
                    $"  {proto.Name}({proto.ID})  {kind}"
                    + $"  发电 {desc.genEnergyPerTick * 60L / 1000000.0:0.###} MW";

                if (desc.useFuelPerTick > 0L)
                    line += $"  耗料 {desc.useFuelPerTick * 60L / 1000000.0:0.###} MW"
                            + $"  efficiency {(double)desc.genEnergyPerTick / desc.useFuelPerTick:0.####}"
                            + $"  fuelMask {desc.fuelMask}"
                            + $"  ABN 下限 useFuelPerTick ≥ {desc.useFuelPerTick * 0.7:0}";
                else
                    line += "  不烧燃料";

                if (desc.powerCatalystId != 0) line += $"  催化剂 {desc.powerCatalystId}";

                ProjectEdenPlugin.Log.LogInfo(line);
            }

            if (count == 0)
                ProjectEdenPlugin.Log.LogWarning("  一座 isPowerGen 的建筑都没找到 —— 这不正常");
        }
    }
}
