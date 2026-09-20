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
    /// <item><b>一种新燃料该插在哪一档。</b> 参照物全是原版物品，而
    /// <c>HeatValue</c> / <c>ReactorInc</c> 和原版配方一样躺在 <c>resources.assets</c> 里，
    /// 离线读不到——凭记忆写下来的档位就是猜。<see cref="DumpFuelLadder"/> 把整条阶梯
    /// 连同每种燃料的<b>来源配方</b>一起打出来，后者是「几份原料压成一根」的锚。</item>
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
            DumpFuelLadder();
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

                // 只列前四个，够认出这一位是干什么的就行。
                // **带上热值**：本文件之外唯一知道原版热值的地方是 EnergyAudit 报的差额，
                // 而差额要靠猜配方形状才能反推出绝对值——那正是本仓库反复记的「按注释复述数字」。
                for (var i = 0; i < list.Count && i < 4; i++)
                {
                    if (i > 0) names.Append('、');
                    names.Append(list[i].Name).Append('(').Append(list[i].ID).Append(')')
                         .Append(' ').Append(Mj(list[i].HeatValue));
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

            DumpHeatWithoutFuelBit(items);
        }

        /// <summary>
        /// 「有热值、但一个燃料位都没有」的物品。
        ///
        /// <b>这一族是两个工具定义不一致的地方，而不一致本身从来没人报过。</b>
        /// 上面那张表按 <c>FuelType != 0</c> 分组，所以看不见它们；而
        /// <c>EnergyAudit.Burnable</c> 算的是 <c>HeatValue × 件数</c>、**完全不看
        /// <c>FuelType</c>**。于是同一个物品「不是燃料」却照样进能量账——
        /// 设计一条新产线时按燃料表判断「这东西不带能量」，会直接把审计的结论算反。
        ///
        /// 反物质就是这一族：它不在任何 FuelType 组里，但它有没有热值，
        /// 决定了以它为产物的配方在万倍速机器上是不是发电机。
        ///
        /// 它也顺带盖住本文件另一处记过的形状——热值和燃料位是一对，只设一半的话
        /// 烧起来是零功率。那种是反过来的（有位无值），这里一并报。
        /// </summary>
        private static void DumpHeatWithoutFuelBit(ItemProto[] items)
        {
            var orphans = new List<ItemProto>();

            foreach (ItemProto proto in items)
            {
                if (proto == null) continue;

                if (proto.HeatValue > 0L && proto.FuelType == 0) orphans.Add(proto);
            }

            if (orphans.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "  有热值但没有燃料位的物品：一个都没有（能量审计和上面这张燃料表口径一致）");

                return;
            }

            var sb = new StringBuilder();

            for (var i = 0; i < orphans.Count && i < 12; i++)
            {
                if (i > 0) sb.Append('、');
                sb.Append(orphans[i].Name).Append('(').Append(orphans[i].ID).Append(')')
                  .Append(' ').Append(Mj(orphans[i].HeatValue));
            }

            if (orphans.Count > 12) sb.Append(" 等 ").Append(orphans.Count).Append(" 种");

            ProjectEdenPlugin.Log.LogInfo(
                $"  **有热值但没有燃料位**（{orphans.Count} 种）：{sb}"
                + " —— 它们烧不了，但 EnergyAudit 按 HeatValue 记账，照样算进能量差额。"
                + "给新产线定配方比例时以这一行为准，不要以上面那张燃料表为准。");
        }

        /// <summary>热值按 MJ 打，省得在日志里数零。</summary>
        private static string Mj(long heatValue)
        {
            if (heatValue <= 0L) return "热值 0";

            double mj = heatValue / 1000000.0;

            return mj >= 1000.0 ? $"热值 {mj / 1000.0:0.###} GJ" : $"热值 {mj:0.###} MJ";
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

        /// <summary>
        /// 燃料能量阶梯：每件热值、机甲功率倍率、烧得了它的建筑，外加它自己的来源配方。
        ///
        /// <b>判据是 <c>FuelType != 0</c>，不是名字。</b> 起念头时只想打「那四根燃料棒」，
        /// 而那要拿名字当判据——本仓库为「按名字挑就会按名字漏」已经付过五次账
        /// （<c>_stack</c>、<c>itemInc</c>、<c>cacheCargoInc1</c>、自动属性的后备字段……）。
        /// <c>FuelType != 0</c> 就是「这东西能不能烧」那个事实本身，顺带把整条阶梯摆出来，
        /// 新燃料该插哪一档一眼看得见，还免了「是不是漏了一种没带棒字的燃料」这个问题。
        ///
        /// <b>热值降序排。</b> 要回答的问题是「插在谁和谁之间」，那就得让相邻两档挨着打。
        /// </summary>
        private static void DumpFuelLadder()
        {
            ItemProto[] items = LDB.items?.dataArray;

            if (items == null) return;

            // 先把发电建筑的掩码收一遍，下面按位反查「谁烧得了它」——
            // 一次 O(建筑) 换掉每种燃料各扫一遍全表
            var burners = new List<KeyValuePair<string, int>>();
            var fuels = new List<ItemProto>();

            foreach (ItemProto proto in items)
            {
                if (proto == null) continue;

                if (proto.FuelType != 0) fuels.Add(proto);

                PrefabDesc desc = proto.prefabDesc;

                if (desc != null && desc.isPowerGen && desc.fuelMask != 0)
                    burners.Add(new KeyValuePair<string, int>(proto.Name, desc.fuelMask));
            }

            if (fuels.Count == 0)
            {
                // 报无聊的那一面：没有燃料和这段代码没跑起来，看日志得能分清
                ProjectEdenPlugin.Log.LogWarning("燃料阶梯：一件燃料都没有 —— 这不正常，原版至少有煤");
                return;
            }

            fuels.Sort((a, b) => b.HeatValue.CompareTo(a.HeatValue));

            ProjectEdenPlugin.Log.LogInfo(
                "── 燃料能量阶梯（热值降序；机甲功率 = ReactorInc + 1，只改放电速率不改总能量）──");

            foreach (ItemProto fuel in fuels)
            {
                var who = new StringBuilder();

                for (var i = 0; i < burners.Count; i++)
                {
                    if ((burners[i].Value & fuel.FuelType) == 0) continue;

                    if (who.Length > 0) who.Append('、');

                    who.Append(burners[i].Key);
                }

                // 这一栏只统计**发电机**，所以空着有两种截然不同的含义，必须分开说：
                // 蓄电器（满）这一族（位 8）是由「能量枢纽」放电的，
                // PowerExchangerComponent 不是 PowerGenerator，没有 fuelMask 可扫；
                // 而真正空着的那种是半对燃料——有热值、没人认、烧出来 0 电，
                // 正是 ApplyFuel 要警告的错。一句「没有建筑烧得了它」把两者混成一个假结论
                if (who.Length == 0)
                    who.Append(fuel.FuelType == 8
                        ? "没有发电机烧它（这一位由能量枢纽放电，不走发电机）"
                        : "没有发电机烧它 —— 若它也不是机甲燃料，那就是半对燃料");

                ProjectEdenPlugin.Log.LogInfo(
                    $"  {fuel.Name}({fuel.ID})  位 {fuel.FuelType}"
                    + $"  {fuel.HeatValue / 1000000.0:0.###} MJ/件"
                    + $"  机甲 ×{fuel.ReactorInc + 1f:0.##}"
                    + (fuel.IsFluid ? "  流体" : "")
                    + $"  可烧：{who}");

                DumpSourceRecipes(fuel);
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"  共 {fuels.Count} 种。**机甲反应堆收哪几位不在这张表里**——它不是 prefab，"
                + "没有 fuelMask 可读；ReactorInc 非 0 只能说明原版为它专门调过功率。");
        }

        /// <summary>
        /// 打出产出这件燃料的全部配方。**这是压缩比的锚**——新做一种燃料棒时
        /// 「几份原料压成一根」不该拍脑袋，照原版自己那条比例来。
        ///
        /// 按 <c>Results</c> 反查，而不是读 <c>ItemProto.maincraft</c>：后者可能是 null，
        /// 而且一件东西可以有好几条产出路径（本 mod 的铝就有碳热和电解两条）。
        /// 只打一条会把「还有别的路子」看成「只有这一条」。
        /// </summary>
        private static void DumpSourceRecipes(ItemProto fuel)
        {
            RecipeProto[] recipes = LDB.recipes?.dataArray;

            if (recipes == null) return;

            foreach (RecipeProto recipe in recipes)
            {
                if (recipe?.Results == null || recipe.ResultCounts == null) continue;

                var makes = false;

                for (var i = 0; i < recipe.Results.Length; i++)
                    if (recipe.Results[i] == fuel.ID)
                        makes = true;

                if (!makes) continue;

                var sb = new StringBuilder("      ← 配方「").Append(recipe.Name).Append("」");

                // 原版类型直接用枚举名（Smelt/Chemical/…），自定义类型（9 起）没有名字，
                // ToString 会打数字，再补上本 mod 注册的机器名
                sb.Append(recipe.Type);

                string machine = MachineRegistry.RecipeTypeMachineName((int)recipe.Type);

                if (!string.IsNullOrEmpty(machine)) sb.Append('/').Append(machine);

                sb.Append("  ");
                AppendSide(sb, recipe.Items, recipe.ItemCounts);
                sb.Append(" → ");
                AppendSide(sb, recipe.Results, recipe.ResultCounts);

                // TimeSpend 的单位是帧
                sb.Append("  ").Append((recipe.TimeSpend / 60.0).ToString("0.##")).Append(" 秒");

                ProjectEdenPlugin.Log.LogInfo(sb.ToString());
            }
        }

        private static void AppendSide(StringBuilder sb, int[] ids, int[] counts)
        {
            if (ids == null || ids.Length == 0)
            {
                // 零投入配方是真实存在的（生物温室的光合育林），不是解析失败
                sb.Append("（无投入）");
                return;
            }

            for (var i = 0; i < ids.Length; i++)
            {
                if (i > 0) sb.Append(" + ");

                ItemProto proto = LDB.items.Select(ids[i]);

                sb.Append(proto != null ? proto.Name : ids[i].ToString())
                  .Append('×')
                  .Append(counts != null && i < counts.Length ? counts[i] : 0);
            }
        }
    }
}
