using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让抽水站在熔岩星球上抽出<b>岩浆</b>。
    ///
    /// <b>先弄清一件事：<c>PlanetData.waterItemId</c> 不是物品号，是个带标签的联合体。</b>
    /// 这张表是从 <c>UIPlanetDetail.OnPlanetDataSet</c>（IL 0684–06B5）里读出来的，不是猜的：
    /// <list type="bullet">
    /// <item><c>&gt; 0</c> —— 真的物品号（水 1000、硫酸 1116）</item>
    /// <item><c>0</c> —— 没有海洋</item>
    /// <item><c>-1</c> —— <b>熔岩</b></item>
    /// <item><c>-2</c> —— 冰</item>
    /// <item>更负 —— 未知</item>
    /// </list>
    /// 「-1 就是熔岩」有两处独立佐证：<c>PlanetModelingManager.ModelingPlanetMain</c>
    /// （IL 0778–07B9）用 <c>oceanSpheres[-waterItemId]</c> 挑海洋球体，负值就是渲染用的下标；
    /// 而 <c>PowerSystem.CalculateGeothermalStrength</c> 与 <c>SetGeothermalAffectStrength</c>
    /// 的<b>第一条指令</b>就是 <c>waterItemId == -1</c>——地热发电站正是建在熔岩上的。
    ///
    /// <b>所以绝对不能去改 waterItemId 本身。</b> 把熔岩星的 -1 换成岩浆的物品号会同时打坏两处：
    /// 海洋球体的下标会越界或选错，而 PowerSystem 那两处判等会让<b>地热发电站全部失效</b>。
    /// 这是「同一个常量两种含义」那条陷阱的字段版——改的是<b>读出来的值</b>，源头一个字不动。
    ///
    /// 原版挡住抽岩浆的是两道<b>互相独立</b>的闸，各自静默：
    /// <list type="number">
    /// <item><b>出料</b>：<c>MinerComponent.InternalUpdate</c> 的 Water 分支（IL 0690 起）是
    ///   <c>productId = planet.waterItemId; if (productId &gt; 0) 产出; else productId = 0;</c>
    ///   ——熔岩的 -1 走 else，产量 0，不报错不打日志。</item>
    /// <item><b>建造</b>：<c>CheckBuildConditions</c>（IL 2896–28F8）按
    ///   <c>desc.waterTypes</c> 这张白名单判，不在表里就
    ///   <c>condition = desc.geothermal ? NeedGeothermalResource(25) : NeedWater(24)</c>。</item>
    /// </list>
    /// 两道闸独立，这一点是要紧的：cheats.json 的「平地抽水」只清掉第二道，
    /// 所以在它打开的情况下抽水站<b>能摆到熔岩上却一滴都不出</b>——第一道还在。
    ///
    /// 对应地，这里也分两头处理，都不碰源头数据：
    /// <list type="number">
    /// <item>把 -1 加进抽水类设备的 <c>prefabDesc.waterTypes</c>（<see cref="OpenWaterTypes"/>）；</item>
    /// <item>在读取 <c>waterItemId</c> 的<b>若干指定方法</b>里，把读出来的 -1 换成岩浆的物品号
    ///   （<see cref="MapWaterProduct"/>）。</item>
    /// </list>
    ///
    /// <b>要改写的方法是列出来的，不是按特征全扫的。</b> 全程序共 37 处读 <c>waterItemId</c>，
    /// 其中渲染（ModelingPlanetMain）、地热（PowerSystem 两处）、建造判定（BuildTool 各处）、
    /// 地形改造、音效、成就、星球生成算法<b>都必须读到原始的 -1</b>。列白名单而不是扫特征，
    /// 是因为这两类在指令形状上完全一样，只能靠语义分。
    /// </summary>
    [HarmonyPatch]
    internal static class LavaPumpPatches
    {
        /// <summary>熔岩海洋在 <c>PlanetData.waterItemId</c> 里的编码。原版常量，见类注释。</summary>
        internal const int LavaOceanId = -1;

        private static int _lavaItemId;
        private static int _firstHitLogged;

        private static AdvancedMinerConfig Config => ProjectEdenPlugin.MinerConfig;

        /// <summary>配置开着才装补丁。物品解析不出来时补丁仍在，但 <see cref="MapWaterProduct"/> 原样返回。</summary>
        private static bool Prepare() => Config != null && Config.lavaPumping;

        // ── 注册期：解析物品并放开建造白名单 ──────────────────────

        internal static void OnPostAddData()
        {
            // 三种状态各有一行，缺一行就分不清「开关关着」和「这段代码根本没进 DLL」
            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogInfo("抽岩浆：advancedminer.json 没读到，未启用");

                return;
            }

            if (!Config.lavaPumping)
            {
                ProjectEdenPlugin.Log.LogInfo("抽岩浆：advancedminer.json 里 lavaPumping 为 false，未启用");

                return;
            }

            int itemId = OreRegistry.FindItemIdByRef(Config.lavaItemKey);
            ItemProto proto = itemId > 0 ? LDB.items.Select(itemId) : null;

            if (proto == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"抽岩浆：引用名「{Config.lavaItemKey}」解析不出物品（解析到 {itemId}），未启用。"
                    + "它得是 ores.json 里 items 段某条的 key");

                return;
            }

            if (!proto.IsFluid)
                ProjectEdenPlugin.Log.LogWarning(
                    $"抽岩浆：「{proto.Name}」的 isFluid 是 false，抽得出来但进不了储液罐"
                    + "——原版空罐从皮带取货时直接把流体白名单当过滤数组用");

            _lavaItemId = itemId;

            OpenWaterTypes(proto);
        }

        /// <summary>
        /// 把熔岩加进抽水类设备的建造白名单。
        ///
        /// <b>整条数组换新的，不在原数组上追加。</b> <c>PrefabDesc</c> 的数组字段是不是每个
        /// prefab 自己的一份，离线看不出来（<c>ReadPrefab</c> 对不同字段处理不一样）；
        /// 换一条新数组则无论共享与否都波及不到别的建筑。这和贴图那边
        /// 「数组是我们的、数组里的对象不是」是同一条边界，只是这里连数组都不敢认。
        ///
        /// 判定用 <c>PrefabDesc.minerType == EMinerType.Water</c>，和 <c>IsBoosted</c> 同一个判据
        /// ——抽水站、大抽水机都算上，不必逐个列物品号。
        /// </summary>
        private static void OpenWaterTypes(ItemProto lava)
        {
            var touched = 0;
            var pumps = 0;

            foreach (ItemProto item in LDB.items.dataArray)
            {
                PrefabDesc desc = item?.prefabDesc;

                if (desc == null || desc.minerType != EMinerType.Water) continue;

                pumps++;

                int[] types = desc.waterTypes ?? new int[0];
                var has = false;

                foreach (int t in types)
                    if (t == LavaOceanId)
                    {
                        has = true;

                        break;
                    }

                if (has) continue;

                var grown = new int[types.Length + 1];

                types.CopyTo(grown, 0);
                grown[types.Length] = LavaOceanId;

                desc.waterTypes = grown;
                touched++;
            }

            // 核对末态而不是自己改了几处：哪天别的 mod 也放开了，这里仍该算通过
            var ready = 0;

            foreach (ItemProto item in LDB.items.dataArray)
            {
                PrefabDesc desc = item?.prefabDesc;

                if (desc?.waterTypes == null || desc.minerType != EMinerType.Water) continue;

                foreach (int t in desc.waterTypes)
                    if (t == LavaOceanId)
                    {
                        ready++;

                        break;
                    }
            }

            if (ready == 0)
                ProjectEdenPlugin.Log.LogError(
                    $"抽岩浆：扫到 {pumps} 台抽水类设备，但没有一台的 waterTypes 含熔岩（{LavaOceanId}），"
                    + "抽水站在熔岩星上仍会报「需要建在水面上」");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"抽岩浆已启用：产物「{lava.Name}」({lava.ID})，"
                    + $"{ready}/{pumps} 台抽水类设备可建在熔岩上（本次改写 {touched} 台）");
        }

        // ── 运行期：把读出来的 -1 换成岩浆 ────────────────────────

        /// <summary>
        /// 供 IL 调用：把 <c>waterItemId</c> 读出来的熔岩编码换成岩浆的物品号，其余原样返回。
        /// 水（1000）、硫酸（1116）、没有海洋（0）、冰（-2）都走原路。
        /// </summary>
        internal static int MapWaterProduct(int waterItemId)
        {
            if (waterItemId != LavaOceanId || _lavaItemId <= 0) return waterItemId;

            // 先做一次普通读再抢占：这个方法挂在并行的 tick 路径上，每次都走 Interlocked 太贵，
            // 而字符串拼接必须等抢到之后才做
            if (_firstHitLogged == 0 && Interlocked.Exchange(ref _firstHitLogged, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo($"抽岩浆：熔岩海洋首次映射为物品 {_lavaItemId}");

            return _lavaItemId;
        }

        private static readonly FieldInfo WaterItemIdField =
            AccessTools.Field(typeof(PlanetData), nameof(PlanetData.waterItemId));

        private static readonly MethodInfo MapWaterProductMethod =
            AccessTools.Method(typeof(LavaPumpPatches), nameof(MapWaterProduct));

        /// <summary>
        /// 各目标方法里 <c>ldfld PlanetData::waterItemId</c> 的处数，对不上就大声失败
        /// ——游戏版本变化后这是唯一会响的地方。
        ///
        /// <b>出料那一处（MinerComponent.InternalUpdate）是整个功能的根。</b>
        /// 其余全是显示与统计：少了它们功能照样成立，只是面板上会出现「抽水站没有产物图标」
        /// 「统计里查不到岩浆」这种看着像 bug 的空缺——和当年矿石换成冶炼产物之后
        /// 采矿机从参考速率里消失是同一个坑。
        /// </summary>
        private static readonly Dictionary<string, int> Expected = new Dictionary<string, int>
        {
            { "MinerComponent.InternalUpdate", 1 },                  // 出料，功能的根
            { "PlanetFactory.CreateEntityLogicComponents", 1 },      // 建筑顶上那个产物图标
            { "UIMinerWindow._OnUpdate", 1 },                        // 抽水站面板的产物图标
            { "UIVeinCollectorPanel._OnUpdate", 1 },                 // 采集器面板
            { "UIControlPanelVeinCollectorPanel._OnUpdate", 1 },     // 控制面板里的那一版
            { "EntityBriefInfo.SetBriefInfo", 1 },                   // 悬停简报
            { "AstroResourceStatPlan.AddPlanetResources", 3 },       // 星球资源统计
            { "ProductionExtraInfoCalculator.CalculateFactory", 1 }, // 理论产能
            { "UIReferenceSpeedTip.AddEntryDataWithFactory", 1 },    // 参考速率
            { "UIPlanetDetail.OnPlanetDataSet", 1 },                 // 行星面板的「海洋类型」行
            { "UIStarDetail.OnStarDataSet", 1 },                     // 恒星面板的资源汇总
        };

        /// <summary>在每一处 <c>ldfld PlanetData::waterItemId</c> 之后补一次映射。目标方法见 <see cref="Expected"/>。</summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(MinerComponent), nameof(MinerComponent.InternalUpdate))]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.CreateEntityLogicComponents))]
        [HarmonyPatch(typeof(UIMinerWindow), "_OnUpdate")]
        [HarmonyPatch(typeof(UIVeinCollectorPanel), "_OnUpdate")]
        [HarmonyPatch(typeof(UIControlPanelVeinCollectorPanel), "_OnUpdate")]
        [HarmonyPatch(typeof(EntityBriefInfo), nameof(EntityBriefInfo.SetBriefInfo))]
        [HarmonyPatch(typeof(AstroResourceStatPlan), "AddPlanetResources")]
        [HarmonyPatch(typeof(ProductionExtraInfoCalculator), "CalculateFactory")]
        [HarmonyPatch(typeof(UIReferenceSpeedTip), "AddEntryDataWithFactory")]
        [HarmonyPatch(typeof(UIPlanetDetail), "OnPlanetDataSet")]
        [HarmonyPatch(typeof(UIStarDetail), "OnStarDataSet")]
        private static IEnumerable<CodeInstruction> WaterItemId_Transpiler(IEnumerable<CodeInstruction> instructions,
                                                                          MethodBase original)
        {
            var list = new List<CodeInstruction>(instructions);
            string name = $"{original.DeclaringType?.Name}.{original.Name}";

            if (WaterItemIdField == null || MapWaterProductMethod == null)
            {
                ProjectEdenPlugin.Log.LogError($"抽岩浆：解析不到 PlanetData.waterItemId，{name} 未改写");

                return list;
            }

            var patched = 0;

            // 倒着走，插入不会打乱还没处理到的下标
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].LoadsField(WaterItemIdField)) continue;

                // 新建指令而不是改写原指令：原指令上可能挂着跳转标签
                list.Insert(i + 1, new CodeInstruction(OpCodes.Call, MapWaterProductMethod));
                patched++;
            }

            int want = Expected.TryGetValue(name, out int n) ? n : -1;

            if (want < 0 || patched != want)
                ProjectEdenPlugin.Log.LogError(
                    $"抽岩浆：{name} 改写了 {patched} 处，预期 {want} 处。游戏版本变化后需要重新对照 IL");
            else
                ProjectEdenPlugin.Log.LogInfo($"抽岩浆：{name} 已接管 {patched} 处 waterItemId 读取");

            return list;
        }
    }
}
