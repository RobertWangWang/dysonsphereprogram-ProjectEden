using System.Threading;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 限制一条矿脉上「正在开采」发光环的数量。
    ///
    /// <b>玩家报的是「多台小型速采机叠在一起，材质集中反光，糊成特别亮的一片」，
    /// 而发光的根本不是建筑。</b> 这一条查错了三轮，值得把判据记下来：前三轮一直在压
    /// 建筑材质（<c>_Color</c> → <c>_AlbedoMultiplier</c> → <c>_MetallicMultiplier</c>
    /// / <c>_SpecularColor</c> / <c>_SmoothMultiplier</c>），每一轮都确实压到了、每一轮
    /// 都没用。**一张截图结束了它**：光团的颜色跟着<b>矿种</b>走而不是跟着建筑的橙色
    /// 染色走——铜矿脉旁边是橙黄的、铁矿脉旁边是白青的，而煤矿脉那一片<b>完全不亮</b>
    /// （煤是黑的）。建筑染成什么色都解释不了这个，矿脉能。
    ///
    /// <b>机制。</b><c>VeinData</c> 逐矿脉存着 <c>minerBaseModelId</c> 和
    /// <c>minerCircleModelId0..3</c>：一个底座，外加一台采矿机一圈发光环。
    /// <c>VeinData.AddMiner</c> 在 <c>minerId3</c> 之后直接 <c>ret</c>，所以封顶四圈。
    /// 原版几乎撞不到这个上限——它的建造间距规则本来就不让采矿机叠在一起。而本 mod
    /// 两头都放开了：<c>MinerBuildRulePatches</c> 允许重叠建造，小型速采机又是
    /// 「产量钉死、靠叠数量出力」的设计。于是一组十几条矿脉上同时点亮四五十圈共位的
    /// 发光环，泛光在屏幕空间把它们加起来，就是截图里那团白。
    ///
    /// <b>做法是后置摘除，不是转译。</b><c>RefreshVeinMiningDisplay</c> 的结构是
    /// 「先把一个底座和四圈全部 <c>RemoveModel</c>，再按当前 <c>minerCount</c> 加回来」
    /// （IL 009C / 00BA / 00D8 / 00F6 / 0114 五处移除，随后五处添加）。所以在它做完
    /// 之后把超额的那几圈摘掉最省事：用的是原版自己的 <c>RemoveModel</c>，不动控制流，
    /// 而且每次刷新都会再跑一遍，幂等。
    ///
    /// <b>清零那几个字段是安全的，有原版先例。</b> 摘掉之后
    /// <c>minerCircleModelId1..3</c> 归 0，下一次刷新时原版会对着 0 再 <c>RemoveModel</c>
    /// 一次——而这正是原版对「不满四台采矿机的矿脉」每天在做的事（那几个字段本来就是 0，
    /// IL 00D8/00F6/0114 三处移除前<b>没有任何判空</b>）。所以这不是我们发明的形状。
    ///
    /// <b>只关视觉。</b><c>minerCount</c> 与 <c>minerId0..3</c> 一个字都不动，
    /// 采矿逻辑、产量、矿脉归属全部照旧。
    /// </summary>
    [HarmonyPatch]
    internal static class VeinMiningGlowPatches
    {
        private static AdvancedMinerConfig Config => ProjectEdenPlugin.MinerConfig;

        private static int _reported;

        /// <summary>本局一共摘掉了多少圈——开机状态行之外的事件计数</summary>
        private static int _removed;

        /// <summary>
        /// 后置：把超出限额的发光环摘掉。
        ///
        /// 参数名必须和原版声明的一致（<c>veinId</c> / <c>addMinerEntityId</c> /
        /// <c>removeMinerEntityId</c>，已按 IL 核对）——Harmony 按<b>名字</b>注入，
        /// 名字对不上会从 <c>PatchAll</c> 抛出去，而且它的杀伤范围是「它之后的补丁类
        /// 全部被跳过」，症状会表现成某个毫不相干的功能悄悄消失。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.RefreshVeinMiningDisplay))]
        internal static void AfterRefreshVeinMiningDisplay(PlanetFactory __instance, int veinId)
        {
            int? limit = Config?.veinMiningCircles;
            bool? drawBase = Config?.veinMiningBase;

            if (!limit.HasValue && !drawBase.HasValue) return;

            if (veinId <= 0 || __instance?.veinPool == null || veinId >= __instance.veinPool.Length) return;

            ref VeinData vein = ref __instance.veinPool[veinId];

            if (vein.id != veinId) return;

            VeinProto proto = LDB.veins.Select((int)vein.type);

            if (proto == null) return;

            GPUInstancingManager gpui = GameMain.gpuiManager;

            if (gpui == null) return;

            int keep = limit ?? 4;
            var cut = 0;

            // 环是四个独立字段而不是数组，所以只能摊开写。顺序从后往前不重要——
            // 每一个都是独立的实例句柄
            if (keep < 4) cut += Drop(gpui, proto.MinerCircleModelIndex, ref vein.minerCircleModelId3);
            if (keep < 3) cut += Drop(gpui, proto.MinerCircleModelIndex, ref vein.minerCircleModelId2);
            if (keep < 2) cut += Drop(gpui, proto.MinerCircleModelIndex, ref vein.minerCircleModelId1);
            if (keep < 1) cut += Drop(gpui, proto.MinerCircleModelIndex, ref vein.minerCircleModelId0);

            if (drawBase.HasValue && !drawBase.Value)
                cut += Drop(gpui, proto.MinerBaseModelIndex, ref vein.minerBaseModelId);

            if (cut == 0) return;

            _removed += cut;

            // 一次性事件行：报清楚「确实摘到了东西」以及当时那条矿脉上挂着几台。
            // 只有开机状态行而没有事件行的话，「限额生效了」和「玩家这局没建采矿机」
            // 分不开——本仓库为这条规矩付过不止一次账
            if (Interlocked.Exchange(ref _reported, 1) == 0)
            {
                // 顺便把两个显示模型的编号和名字报出来——「那团光到底是哪个模型」
                // 离线读不到（模型在 resources.assets 里），只能让它自己说
                ModelProto circle = LDB.models.Select(proto.MinerCircleModelIndex);
                ModelProto baseModel = LDB.models.Select(proto.MinerBaseModelIndex);

                // 这两个模型的材质从头到尾没人碰过，而「从矿脉向上飞的那片光」只可能
                // 出自它们——建筑自己那四份材质已经全部归零了。材质在 resources.assets
                // 里离线读不到，所以让它自己报
                if (Config.veinMiningReport)
                {
                    MaterialProbe.DumpModel($"矿脉开采底座（模型 {proto.MinerBaseModelIndex}）", baseModel);
                    MaterialProbe.DumpModel($"矿脉开采圆环（模型 {proto.MinerCircleModelIndex}）", circle);
                }

                ProjectEdenPlugin.Log.LogInfo(
                    $"矿脉开采光环：限额 {keep} 圈" +
                    (drawBase.HasValue && !drawBase.Value ? "、底座不画" : "") +
                    $"已生效。第一次触发是矿种 {(int)vein.type}（{proto.name}）的 {veinId} 号矿脉，" +
                    $"上面挂着 {vein.minerCount} 台采矿机，这一次摘掉 {cut} 个显示模型。" +
                    $"圆环模型 {proto.MinerCircleModelIndex}（{circle?.Name ?? "查不到"}），" +
                    $"底座模型 {proto.MinerBaseModelIndex}（{baseModel?.Name ?? "查不到"}），" +
                    $"圆环半径 {proto.CircleRadius:0.##}。" +
                    "逻辑不受影响，minerCount 与 minerId 一个字没动。");
            }
        }

        /// <summary>摘掉一个显示模型并把句柄清零。返回摘掉的数量，便于计数。</summary>
        private static int Drop(GPUInstancingManager gpui, int modelIndex, ref int modelId)
        {
            if (modelId == 0) return 0;

            gpui.RemoveModel(modelIndex, modelId, true);
            modelId = 0;

            return 1;
        }

        /// <summary>
        /// 开机状态行，<b>无条件</b>打印。
        ///
        /// 状态行回答「接上了没有」，事件行回答「它决定了什么」，两者不能互相顶替。
        /// 只在限额生效时才打印的话，「没配这个字段」和「这段代码压根没进 DLL」
        /// 会长得一模一样。
        /// </summary>
        internal static void Report()
        {
            int? limit = Config?.veinMiningCircles;
            bool? drawBase = Config?.veinMiningBase;

            if (!limit.HasValue && !drawBase.HasValue)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "矿脉开采光环：未配置（advancedminer.json 的 veinMiningCircles / veinMiningBase 都留空），" +
                    "按原版走——一条矿脉一个底座、最多四圈。");
                return;
            }

            string circles = !limit.HasValue
                ? "圆环按原版（最多 4 圈）"
                : limit.Value <= 0
                    ? "一圈都不画"
                    : $"最多 {limit.Value} 圈（原版 4 圈）";

            string base_ = !drawBase.HasValue
                ? "底座按原版"
                : drawBase.Value
                    ? "底座照画"
                    : "底座不画";

            bool allOff = limit.HasValue && limit.Value <= 0 && drawBase.HasValue && !drawBase.Value;

            ProjectEdenPlugin.Log.LogInfo(
                $"矿脉开采光环：{circles}，{base_}。这只关视觉——采矿逻辑、产量和矿脉归属全部照旧。" +
                (allOff
                    ? "【整套开采显示已全关——这是一次判定实验：若那团光还在，说明成因不在矿脉显示上】"
                    : ""));
        }
    }
}
