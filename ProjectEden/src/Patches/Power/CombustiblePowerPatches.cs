using System;
using System.Collections.Generic;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 可燃液体发电：<b>能量利用率跟着燃料走</b>。完整推导在仓库根目录的 可燃液体发电V1.md。
    ///
    /// <b>原版给一种燃料的只有一个标量。</b>
    /// <c>PowerGeneratorComponent.EnergyCap_Fuel</c> 的输出功率就是 <c>genEnergyPerTick</c>，
    /// 和烧什么无关；<c>GenEnergyByFuel</c> 扣掉的燃料能量是
    /// <c>energy × useFuelPerTick / genEnergyPerTick</c>，所以
    /// <b><c>genEnergyPerTick / useFuelPerTick</c> 就是能量利用率</b>，而它是建筑常量。
    /// 一份燃料的能量池则由 <c>SetNewFuel</c> 写成 <c>ItemProto.HeatValue</c>。
    /// 于是原版能表达的只有「一份烧多久」，表达不了「效率随燃料变」。
    ///
    /// <b>入口是 <c>useFuelPerTick</c> 逐台可写。</b> 它在 <c>PowerGeneratorComponent</c> 上
    /// （struct，存在 <c>PowerSystem.genPool[]</c> 里），<c>EnergyCap_Fuel</c> 与
    /// <c>GenEnergyByFuel</c> 每 tick 都重新读它。所以不需要 transpiler，
    /// 在这两个方法前面把它对齐到 <c>genEnergyPerTick / η(当前燃料)</c> 就够了。
    ///
    /// <b>它进存档，但不需要读档重贴。</b> <c>Export</c>/<c>Import</c> 确实带着这个字段，
    /// 可是前缀每 tick 都会重算一遍，存档里那个值撑不过一帧——
    /// 这和 <see cref="AlloyRatioPatches"/> 需要 <c>ReapplyAll</c> 不同，
    /// 那边改的是 <c>recipeExecuteData</c> 引用，读档时会被 <c>Import</c> 换回全局对象。
    ///
    /// <b>必须按 <c>fuelMask</c> 过滤，不能只看燃料。</b> 甲醇、乙烯、氨这些液体的
    /// <c>FuelType</c> 是按位或上去的（1 | 16），火力发电厂照样烧得了它们——
    /// 不按掩码过滤的话，原版那座的效率也会被改。
    ///
    /// <b>锚点方向是 ABN_PowerGenerator 定的。</b> <c>CheckPowerGen</c> 判异常的条件是
    /// 运行时 <c>useFuelPerTick</c> 低于 prefab 的 0.7 倍，所以 prefab 必须锚在
    /// <b>效率最高</b>的那一档（machines.json 的 <c>generator.efficiency</c>），
    /// 这里只会把它往上乘。反过来做，玩家一打开电厂窗口就会被判异常。
    /// </summary>
    [HarmonyPatch]
    internal static class CombustiblePowerPatches
    {
        internal static CombustiblesConfig Config;

        /// <summary>物品 ID → 能量利用率。<b>下标就是物品 ID</b>：tick 路径上要 O(1) 且不能分配。</summary>
        private static float[] _eta = new float[0];

        /// <summary>物品 ID → 工作温度（摄氏度）。提示栏那一行用，0 表示没有这一行。</summary>
        private static int[] _celsius = new int[0];

        private static int _fieldId = -1;

        internal static bool Ready { get; private set; }

        internal static void Load() => Config = JsonHelper.Load<CombustiblesConfig>("combustibles");

        /// <summary>η(T) = secondLaw × (1 − T_冷 / T_热)。卡诺，冷端和二级效率都在配置里。</summary>
        internal static float Efficiency(int celsius)
        {
            float hot = celsius + 273.15f;

            if (hot <= Config.coldSideK) return 0f;

            return Config.secondLaw * (1f - Config.coldSideK / hot);
        }

        internal static void OnPostAddData()
        {
            Ready = false;
            _eta = new float[0];
            _celsius = new int[0];
            _fieldId = -1;

            // 报无聊的那一面：配置缺失、关掉、和「跑了但一条都没配上」，日志里要分得清
            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogInfo("可燃液体发电：没有 combustibles.json，跳过");
                return;
            }

            if (!Config.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo("可燃液体发电：combustibles.json 里 enabled 为 false，跳过");
                return;
            }

            if (Config.fuels == null || Config.fuels.Length == 0)
            {
                ProjectEdenPlugin.Log.LogWarning("可燃液体发电：fuels 是空的，一种液体都不会有工作温度");
                return;
            }

            if (!CheckFieldRange()) return;

            var rows = new List<string>();
            var maxId = 0;
            var resolved = new List<KeyValuePair<ItemProto, int>>();

            foreach (CombustibleFuelEntry fuel in Config.fuels)
            {
                if (fuel == null || fuel.celsius <= 0) continue;

                int itemId = string.IsNullOrEmpty(fuel.@ref)
                    ? fuel.itemId
                    : OreRegistry.FindItemIdByRef(fuel.@ref);

                ItemProto item = itemId > 0 ? LDB.items.Select(itemId) : null;

                if (item == null)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"可燃液体发电：{fuel.name ?? fuel.@ref ?? fuel.itemId.ToString()} 解析不出物品，跳过");

                    continue;
                }

                // 比 Name 不比 name：后者是译文，切英文之后永远对不上
                if (!string.IsNullOrEmpty(fuel.name) && fuel.name != item.Name)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"可燃液体发电：配置里写的是「{fuel.name}」，但 " +
                        $"{(string.IsNullOrEmpty(fuel.@ref) ? "itemId " + fuel.itemId : "ref " + fuel.@ref)} " +
                        $"解析出来的是「{item.Name}」({item.ID})——ID 或 ref 可能写错了");

                if (item.HeatValue <= 0L)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"可燃液体发电：「{item.Name}」没有热值，进了电厂也烧不出电——" +
                        "热值写在 ores.json 的 heatValue（原版物品则自带）");

                resolved.Add(new KeyValuePair<ItemProto, int>(item, fuel.celsius));

                if (item.ID > maxId) maxId = item.ID;
            }

            if (resolved.Count == 0)
            {
                ProjectEdenPlugin.Log.LogWarning("可燃液体发电：一种液体都没配上，电厂会无料可烧");
                return;
            }

            _eta = new float[maxId + 1];
            _celsius = new int[maxId + 1];

            foreach (KeyValuePair<ItemProto, int> pair in resolved)
            {
                ItemProto item = pair.Key;
                int celsius = pair.Value;
                float eta = Efficiency(celsius);

                _eta[item.ID] = eta;
                _celsius[item.ID] = celsius;

                // 按位或，不是替换：不从现有内容上拿走能力。甲醇原来是 FuelType 1，
                // 改成 1|16 之后火力发电厂照样烧得了它
                int was = item.FuelType;
                item.FuelType |= Config.fuelType;

                AppendField(item);

                rows.Add(
                    $"{item.Name}({item.ID}) {celsius}°C η={eta:0.###}"
                    + $" 热值 {item.HeatValue / 1000000.0:0.##} MJ"
                    + $" 单件出电 {item.HeatValue * eta / 1000000.0:0.##} MJ"
                    + $" FuelType {was}→{item.FuelType}");
            }

            Ready = true;

            ProjectEdenPlugin.Log.LogInfo(
                $"可燃液体发电已就绪：{resolved.Count} 种液体，燃料位 {Config.fuelType}，"
                + $"冷端 {Config.coldSideK:0.##} K，二级效率 {Config.secondLaw:0.##}，"
                + $"属性行「{Config.propertyName.Translate()}」字段号 {_fieldId}");

            foreach (string row in rows) ProjectEdenPlugin.Log.LogInfo("  " + row);

            VerifyPlant();
        }

        /// <summary>
        /// 字段号不得和 metals.json 那几行重叠 —— 重叠的话提示栏会串行，
        /// 而且是静默串行：两边都认为那个字段号是自己的。
        /// </summary>
        private static bool CheckFieldRange()
        {
            int mineStart = Config.fieldIdBase;
            int metalEnd = MetalPropertyPatches.FieldsEnd;

            if (mineStart < metalEnd)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"可燃液体发电：fieldIdBase 配成了 {mineStart}，和金属属性的区间"
                    + $"（到 {metalEnd - 1} 为止）重叠，提示栏会串行。改成 {metalEnd} 或更大");

                return false;
            }

            _fieldId = mineStart;

            return true;
        }

        /// <summary>电厂的 fuelMask 必须和这里的 fuelType 一致，否则液体进不去。</summary>
        private static void VerifyPlant()
        {
            MachineGeneratorEntry gen = MachineRegistry.FindGenerator(Config.plantKey);

            if (gen == null)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"可燃液体发电：machines.json 里找不到 key 为「{Config.plantKey}」的发电厂，"
                    + "这些液体将没有任何机器烧得了");

                return;
            }

            if (gen.fuelMask != Config.fuelType)
                ProjectEdenPlugin.Log.LogError(
                    $"可燃液体发电：电厂的 fuelMask 是 {gen.fuelMask}，而液体的燃料位是 "
                    + $"{Config.fuelType}——两者必须一致，否则电厂一滴也吸不进去");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"可燃液体发电：电厂「{Config.plantKey}」的 fuelMask {gen.fuelMask} 与燃料位对上了");
        }

        /// <summary>把工作温度那一行追加到物品原有的 <c>DescFields</c> 后面，不覆盖。</summary>
        private static void AppendField(ItemProto item)
        {
            int[] have = item.DescFields ?? new int[0];

            if (Array.IndexOf(have, _fieldId) >= 0) return;

            Array.Resize(ref have, have.Length + 1);
            have[have.Length - 1] = _fieldId;
            item.DescFields = have;
        }

        // ────────────────────────── tick 路径 ──────────────────────────

        /// <summary>
        /// 把 <c>useFuelPerTick</c> 对齐到当前燃料的效率。
        ///
        /// <b>不分配、不装箱、不查字典</b>——这是 tick 路径，而且
        /// <c>PowerSystem.GameTick</c> 有跨行星的并行变体。<c>_eta</c> 是只读查表，
        /// 写的是本行星自己的 <c>genPool</c> 元素，两边都安全。
        /// </summary>
        private static void Align(ref PowerGeneratorComponent gen)
        {
            // 只管我们那座电厂。液体的 FuelType 是 1|16，火力发电厂也烧得了它们，
            // 不按掩码过滤的话原版那座的效率也会被改
            if (!Ready || gen.fuelMask != Config.fuelType) return;

            int id = gen.curFuelId != 0 ? gen.curFuelId : gen.fuelId;

            if (id <= 0 || id >= _eta.Length) return;

            float eta = _eta[id];

            if (eta <= 0f) return;

            var want = (long)(gen.genEnergyPerTick / eta);

            if (want > 0L) gen.useFuelPerTick = want;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PowerGeneratorComponent), nameof(PowerGeneratorComponent.EnergyCap_Fuel))]
        private static void EnergyCap_Fuel_Prefix(ref PowerGeneratorComponent __instance) => Align(ref __instance);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PowerGeneratorComponent), nameof(PowerGeneratorComponent.GenEnergyByFuel))]
        private static void GenEnergyByFuel_Prefix(ref PowerGeneratorComponent __instance) => Align(ref __instance);

        // ────────────────────────── 提示栏那一行 ──────────────────────────

        private static bool Mine(ItemProto item, int index)
        {
            int[] fields = item?.DescFields;

            return _fieldId >= 0 && fields != null && index >= 0 && index < fields.Length
                   && fields[index] == _fieldId;
        }

        /// <summary>
        /// 属性行的名字。<b>要过 Translate</b>：原版这一行自己也是这么干的
        /// （<c>GetPropName</c> 里 78 个 ldstr 对 76 个 Translate 调用），
        /// 漏掉的话英文客户端的提示栏里就这一行是中文。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemProto), nameof(ItemProto.GetPropName))]
        private static void GetPropName(ItemProto __instance, int index, ref string __result)
        {
            if (Mine(__instance, index)) __result = Config.propertyName.Translate();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemProto), nameof(ItemProto.GetPropValue))]
        private static void GetPropValue(ItemProto __instance, int index, ref string __result)
        {
            if (!Mine(__instance, index)) return;

            int id = __instance?.ID ?? 0;

            __result = id > 0 && id < _celsius.Length && _celsius[id] > 0
                ? _celsius[id] + " °C"
                : "-";
        }
    }
}
