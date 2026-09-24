using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches.Fusion
{
    /// <summary>
    /// 聚变线的运行期部分，两件事：
    ///
    /// <list type="number">
    /// <item>「氚 · 氘氘聚变」跑起来时，**对撞机的功耗变成 100 倍**。</item>
    /// <item>巨型聚变发电站把自己压出来的燃料棒直接烧掉发电（和氧化还原燃烧厂同一个形状）。</item>
    /// </list>
    ///
    /// <b>两个号都按名字解析，不信 JSON 里钉的那个数。</b> ores.json 写的是 6700/6701，
    /// 但 <c>ProtoSlots.ResolveRecipeId</c> 撞号时会顺延并只打一条 WARNING——
    /// 之后按写死的号去比对，表现是「功耗没变」「一度电不发」，而且一个字都不报。
    /// 这是本仓库记过的「解析器说它改用了别的号，那是不稳定的号，不是成功的回退」。
    /// </summary>
    [HarmonyPatch]
    internal static class FusionRegistry
    {
        internal const string DDRecipeName = "氚 · 氘氘聚变";
        internal const string RodRecipeName = "氘氚燃料棒 · 芯块装管";

        /// <summary>对撞机跑这条配方时的功耗倍数。所有者指定的数。</summary>
        internal const int PowerMultiplier = 100;

        internal static int DDRecipeId { get; private set; }
        internal static int RodRecipeId { get; private set; }
        internal static int RodItemId { get; private set; }

        internal static bool Ready => DDRecipeId > 0 && RodRecipeId > 0 && RodItemId > 0;

        /// <summary>
        /// 注册在 <c>PostAddDataAction</c> 上，此时 LDB 已经完整。
        /// <b>无论成没成都打一行</b>——「没解析到」和「这段代码没跑」在日志里必须分得开，
        /// 本仓库为这条规矩付过七次学费。
        /// </summary>
        internal static void Resolve()
        {
            RecipeProto[] recipes = LDB.recipes?.dataArray;

            if (recipes == null)
            {
                ProjectEdenPlugin.Log.LogError("聚变线：LDB.recipes 为空，整条线不生效");

                return;
            }

            for (var i = 0; i < recipes.Length; i++)
            {
                RecipeProto r = recipes[i];

                if (r == null) continue;

                // 比 Proto.Name（原始键），不是 proto.name（翻译过的）——
                // 后者在英文客户端上永远匹配不上，本仓库已经踩过
                if (r.Name == DDRecipeName) DDRecipeId = r.ID;
                else if (r.Name == RodRecipeName)
                {
                    RodRecipeId = r.ID;

                    if (r.Results != null && r.Results.Length > 0) RodItemId = r.Results[0];
                }
            }

            if (!Ready)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"聚变线没接上：氘氘聚变配方 {DDRecipeId}、燃料棒配方 {RodRecipeId}、" +
                    $"燃料棒物品 {RodItemId}（0 表示没找到）。" +
                    "对撞机的 100 倍功耗和发电站的自烧都不会生效。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"聚变线已接上：氘氘聚变配方 {DDRecipeId}（跑它时对撞机功耗 ×{PowerMultiplier}）、" +
                $"燃料棒配方 {RodRecipeId} → 物品 {RodItemId}");
        }
    }

    /// <summary>
    /// **跑氘氘聚变时，对撞机功耗 ×100。**
    ///
    /// <para>挂点是 <c>AssemblerComponent.SetPCState</c>，它整个方法只有 8 条指令：
    /// <c>pcPool[pcId].SetRequiredEnergy(replicating, 1000 + extraPowerRatio)</c>。
    /// 所以正确的做法是**用原版自己的公式再算一遍**、把千分比乘上去，
    /// 而不是去改 <c>requiredEnergy</c> 的结果——后者要自己重现「待机走
    /// idleEnergyPerTick、工作走 workEnergyPerTick」那个分支，重现就会走样。</para>
    ///
    /// <para><b>三件量过的事，缺一条这个补丁就是错的：</b>
    /// ① <c>SetRequiredEnergy</c> 有 <b>三个重载</b>（<c>(bool)</c>、<c>(double)</c>、
    /// <c>(bool,int)</c>），所以这里显式取 <c>(bool, int)</c> 那个；按名字解析会
    /// <c>AmbiguousMatchException</c>，而那会把整个 mod 带下去。
    /// ② 三个重载**全是赋值**（<c>stfld requiredEnergy</c>）不是累加，所以再调一次是覆盖，
    /// 不会叠加。
    /// ③ <c>requiredEnergy</c> / <c>workEnergyPerTick</c> 都是 <b>Int64</b>，
    /// 750,000 × 100,000 远在范围内，不会翻负。</para>
    ///
    /// <para><b>待机那一支故意不碰。</b> 原版的 <c>(bool,int)</c> 重载里，
    /// <c>working == false</c> 直接取 <c>idleEnergyPerTick</c>、千分比根本不参与计算。
    /// 所以「只有真的在跑这条配方时才 ×100」不需要额外的判断，是原版分支自带的。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class ColliderFusionPowerPatches
    {
        private static int _logged;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AssemblerComponent), nameof(AssemblerComponent.SetPCState))]
        private static void AssemblerComponent_SetPCState(ref AssemblerComponent __instance,
                                                          PowerConsumerComponent[] pcPool)
        {
            if (!FusionRegistry.Ready) return;
            if (__instance.recipeId != FusionRegistry.DDRecipeId) return;

            // 没在生产就不加价：原版那一支走 idleEnergyPerTick，本来就该是待机功耗
            if (!__instance.replicating) return;

            int pcId = __instance.pcId;

            if (pcId <= 0 || pcPool == null || pcId >= pcPool.Length) return;

            // 原版的式子：1000 + extraPowerRatio（增产剂那一档），这里整体乘 100
            int permillage = (1000 + __instance.extraPowerRatio) * FusionRegistry.PowerMultiplier;

            pcPool[pcId].SetRequiredEnergy(true, permillage);

            // tick 路径上不许分配：抢到之后再拼串
            if (Interlocked.Exchange(ref _logged, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"氘氘聚变已开始，对撞机功耗 ×{FusionRegistry.PowerMultiplier}：" +
                $"千分比 {1000 + __instance.extraPowerRatio} → {permillage}，" +
                $"本台实耗 {pcPool[pcId].requiredEnergy * 60L / 1_000_000L} MW");
        }
    }

    /// <summary>
    /// **巨型聚变发电站：自己压出来的燃料棒直接进燃料舱，不上传送带。**
    ///
    /// <para>整套机制和氧化还原燃烧厂一模一样，那一座的注释里已经写全了推导：
    /// <c>PowerGeneratorComponent.SetNewFuel(itemId, count, inc)</c> 是公开方法、
    /// 自己从 LDB 取 <c>HeatValue</c> 填 <c>fuelHeat</c>；<c>EnergyCap_Fuel</c>
    /// 只看 <c>fuelCount &gt; 0</c>，<b>烧的时候不查 fuelMask</b>。所以这里零 transpiler。</para>
    ///
    /// <para>比那一座简单的地方：这台**只有一种燃料**，所以没有「换档要等烧完」那一段——
    /// 那段存在是因为 <c>SetNewFuel</c> 会整体替换 <c>fuelHeat</c>，而换一半会让
    /// <c>fuelEnergy</c> 里剩的旧档对不上账。一种燃料就不存在这个问题。</para>
    /// </summary>
    internal static class FusionBurnerPatches
    {
        /// <summary>
        /// 燃料舱目标存量。<c>fuelCount</c> 是 <b>Int16</b>，所以这个数必须远低于 32767——
        /// 不是留余量，是超了会翻负。和氧化还原燃烧厂取同一个数。
        /// </summary>
        private const int FuelTarget = 3000;

        private static int _stateLogged;
        private static int _noGenLogged;
        private static int _burnLogged;

        /// <summary>
        /// 一次性状态行，把发电链上每一环都打出来。
        ///
        /// <para><b>它存在的理由和氧化还原燃烧厂那条一样，而且那次是真返工：</b>
        /// 「能产燃料棒但不发电」可能出在配方号对不上、发电组件没建起来、没并上电网、
        /// 燃料没搬进去、电网不取电——五个环节，每一环失败都什么都不报。
        /// <c>networkId == 0</c> 是最值得盯的一个：<c>NewGeneratorComponent</c> 自己不并网，
        /// 发电机是顺着**节点**进电网的，所以这台必须也是 isPowerNode；
        /// 而 prefabDesc 只对**新建**实体生效（坑 1），改动之前建的要拆了重建。</para>
        /// </summary>
        private static void ReportOnce(PlanetFactory factory, ref AssemblerComponent component,
                                       int genId, int produced)
        {
            if (Interlocked.Exchange(ref _stateLogged, 1) != 0) return;

            string head =
                $"巨型聚变发电站自检：配方 {component.recipeId}（期望 {FusionRegistry.RodRecipeId}）" +
                $"　产物槽 {produced}　powerGenId {genId}";

            if (genId <= 0 || factory.powerSystem?.genPool == null
                || genId >= factory.powerSystem.genPool.Length)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    head + "　→ **没有发电机组件**。megabuildings.json 的 generator 段没生效，" +
                    "或者这台是改动之前建的——prefabDesc 只对新建实体生效，拆了重建即可");

                return;
            }

            ref PowerGeneratorComponent g = ref factory.powerSystem.genPool[genId];

            ProjectEdenPlugin.Log.LogInfo(
                head +
                $"　电网 {g.networkId}" +
                $"　燃料 {g.fuelId}×{g.fuelCount}（单根 {g.fuelHeat / 1e6:0.#} MJ）" +
                $"　实发 {g.generateCurrentTick * 60L / 1_000_000_000L} GW" +
                $"／上限 {g.capacityCurrentTick * 60L / 1_000_000_000L} GW");
        }

        /// <summary>
        /// 挂在 <c>MegaAssemblerPatches.MegaTick</c> 上，在周期结算之后跑。
        ///
        /// <para><b>顺序决定了「先喂自己、再出货」，而这不需要额外的标志位</b>：
        /// MegaTick 先跑 UpdateSlots（产物进储物格），再结算周期，最后才轮到这里——
        /// 所以每 tick 新产的棒先进燃料舱，装满之后多出来的才在下一 tick 到储物格。
        /// 一座电厂当然先喂自己。</para>
        ///
        /// <para><b>tick 路径上不许分配</b>：没有 new、没有闭包、没有字符串插值；
        /// 一次性日志用 <c>Interlocked</c> 抢，串留到抢到之后再拼——
        /// 组装机的 tick 是跨星球并行的，普通 bool 守卫挡不住每个线程各打一遍。</para>
        /// </summary>
        internal static void Burn(PlanetFactory factory, ref AssemblerComponent component)
        {
            if (!FusionRegistry.Ready) return;
            if (component.recipeId != FusionRegistry.RodRecipeId) return;

            int entityId = component.entityId;

            if (entityId <= 0 || factory.entityPool == null || entityId >= factory.entityPool.Length) return;

            int genId = factory.entityPool[entityId].powerGenId;

            // 状态行放在所有早退之前：「产物槽是空的」和「这段代码根本没跑」
            // 在日志里必须分得开
            ReportOnce(factory, ref component, genId,
                       component.produced != null && component.produced.Length > 0 ? component.produced[0] : -1);

            if (component.produced == null || component.produced.Length < 1) return;
            if (component.produced[0] <= 0) return;

            if (genId <= 0 || factory.powerSystem?.genPool == null || genId >= factory.powerSystem.genPool.Length)
            {
                if (Interlocked.Exchange(ref _noGenLogged, 1) == 0)
                    ProjectEdenPlugin.Log.LogWarning(
                        "巨型聚变发电站没有发电机组件（powerGenId 为 0）——" +
                        "它会照常压燃料棒，但一度电也不发。检查 megabuildings.json 里这一条的 generator 段。");

                return;
            }

            ref PowerGeneratorComponent gen = ref factory.powerSystem.genPool[genId];

            // 产物物品读 recipeExecuteData 而不是 RecipeProto——那是这台机器自己那份，
            // 和配方原型可能不是同一个对象
            RecipeExecuteData data = component.recipeExecuteData;

            if (data?.products == null || data.products.Length < 1) return;

            int rodId = data.products[0];

            if (rodId <= 0) return;

            // 舱里是别的东西而且还没烧完——等它烧完。这台只有一种燃料，
            // 所以这一条实际上只会在存档里留着旧燃料时命中
            if (gen.fuelCount > 0 && gen.fuelId != rodId) return;

            int room = FuelTarget - gen.fuelCount;

            if (room <= 0) return;

            int move = component.produced[0];

            if (move > room) move = room;

            component.produced[0] -= move;

            if (gen.fuelCount <= 0) gen.SetNewFuel(rodId, (short)move, 0);
            else gen.fuelCount = (short)(gen.fuelCount + move);

            if (Interlocked.Exchange(ref _burnLogged, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    "巨型聚变发电站已开始自烧：燃料棒从组装机产物槽直接进燃料舱，不经过传送带。");
        }
    }
}
