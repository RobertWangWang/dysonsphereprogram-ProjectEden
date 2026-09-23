using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 活性透镜：一枚<b>活的</b>引力透镜。进的是<b>原版射线接收站</b>，不新增建筑。
    ///
    /// <b>0.10.35 把这个功能的一半变成了数据，那一半的转译器已经删了——而这段历史值得留着。</b>
    ///
    /// 旧版（≤ 0.10.34）引擎<b>不从透镜物品上读任何东西</b>：<c>catalystId</c> 只是一次相等
    /// 比较，<c>cata = 2 × (1 + inc)</c> 是<b>硬写在三个方法里的字面量</b>
    /// （<c>EnergyCap_Gamma_Req</c> / <c>MaxOutputCurrent_Gamma</c> / <c>RequiresCurrent_Gamma</c>），
    /// 所以倍率只能落在我们自己的 transpiler 上，而且<b>三处必须一起改</b>——只改第一处的话，
    /// 接收站实发的电和它报给电网的最大出力、向电网索要的量就对不上，电网按错误的数调度，
    /// 症状是<b>功率曲线抖而不是报错</b>。形状和自动集装机那四个 <c>4</c> 完全一样：
    /// 同一个常数既是产能、又是申报、又是索取。
    ///
    /// 0.10.35 把催化剂重做成了<b>和燃料同构的通用系统</b>：<c>ItemProto.CatalystType</c>
    /// （位掩码，对应 <c>PrefabDesc.powerCatalystMask</c>）、<c>ItemProto.catalystNeeds</c>
    /// （白名单数组，和 <c>fuelNeeds</c> / <c>turretNeeds</c> 同族）、
    /// <c>ItemProto.catalystAbilityById[id] = Ability × 0.01f</c>（倍率）。
    /// 于是那个字面量 2 没有了，三处一起改的约束也随之消失——<b>倍率只有一份数据</b>。
    /// 四个转译器（三处 cata + 一处 <c>EntityFastFillIn</c>）因此全部删除，
    /// 换成 <see cref="ApplyCatalystData"/> 写两个字段再重跑原版自己的两个构建器。
    ///
    /// <b>这次更新是靠「改写计数不对就大声失败」抓到的</b>：日志里那行
    /// 「应当改写 7 处，实际 5 处」。没有那个计数的话，症状会是「透镜装上去不涨功率、
    /// 而且放不进接收站」，而每一步都不报错。
    ///
    /// <b>光子和电力是两个独立旋钮</b>，这是读 IL 读出来的，不是设计出来的：
    /// <c>GameTick_Gamma</c> @00D2 是 <c>productCount += capacityCurrentTick / productHeat</c>，
    /// 分母和分子完全解耦。电力倍率改分子（现在是 <c>Ability</c> 那份数据），
    /// 光子倍率改分母（一处 <c>productHeat</c>，仍然是转译器），互不牵扯。
    ///
    /// <b>换料靠写 <c>catalystId</c> 本身，不靠放宽比较。</b> 插入侧三条路
    /// （传送带 <c>PickFrom</c>、<c>EntityFastFillIn</c>、<c>OnCataButtonClick</c>）
    /// 都不是「比对白名单」而是<b>照着 <c>catalystId</c> 点名要货</b>。所以把那个字段
    /// 换成活性透镜，统计（<c>ProductionStatistics</c> / <c>SingleProducerStatPlan</c> /
    /// <c>UIReferenceSpeedTip</c>）、拆建筑返还（<c>TakeBackItemsInEntity</c>）、
    /// 消耗记账（<c>consumeRegister[catalystId]</c>）就<b>全部自动对上</b>；
    /// 反过来只放宽比较的话，喂活性透镜、拆下来还你引力透镜——那是凭空换物品。
    ///
    /// <b>存档一个字段都不用加。</b> <c>catalystPoint</c> / <c>catalystIncPoint</c> /
    /// <c>catalystId</c> 本来就在 <c>Export</c>（@0106/@0112/@011E）和
    /// <c>Import</c>（@0174/@0180/@0190）里。
    /// </summary>
    [HarmonyPatch]
    internal static class LensPatches
    {
        internal static LensConfig Config;

        /// <summary>活性透镜的物品 ID，没注册成功就是 0</summary>
        internal static int LensId;

        /// <summary>原版催化剂（引力透镜）的物品 ID，用来让玩家换回去</summary>
        internal static int DefaultCatalystId;

        private static float _powerMul = 1f;
        private static float _photonMul = 1f;
        private static float _healRate;

        /// <summary>
        /// 功能是否真的生效。<b>每个助手都要查它</b>——transpiler 在 <c>PatchAll</c> 时就装上了，
        /// 那时候 LDBTool 还没建表，物品 ID 一个都解析不出来。
        /// </summary>
        private static bool Active => LensId > 0 && Config != null && Config.enabled;

        /// <summary>
        /// 配置里关掉就整个不打补丁。<c>Config</c> 在 <c>Awake</c> 里、<c>PatchAll</c> 之前加载，
        /// 所以这里读得到。
        /// </summary>
        private static bool Prepare()
        {
            return Config == null || Config.enabled;
        }

        // ════════════════════════════════════════════════════════════════════
        //  注册后：解析 ID、钳配置、把平衡对照打进日志
        // ════════════════════════════════════════════════════════════════════

        internal static void OnPostAddData()
        {
            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogWarning("活性透镜：读不到 lens.json，功能未启用");

                return;
            }

            if (!Config.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo("活性透镜：lens.json 里 enabled = false，功能已关闭");

                return;
            }

            LensId = OreRegistry.FindItemIdByRef(Config.lensItemKey);

            if (LensId <= 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"活性透镜：ores.json 里找不到 key「{Config.lensItemKey}」，整个功能失效。"
                    + "多半是那条物品的 enabled 为 false，或者它的 proto 模板没解析出来（看上面的 ERROR）");

                return;
            }

            DefaultCatalystId = OreRegistry.VanillaItemIdByName(Config.defaultCatalystName);

            if (DefaultCatalystId <= 0)
                ProjectEdenPlugin.Log.LogWarning(
                    $"活性透镜：按名字找不到原版催化剂「{Config.defaultCatalystName}」。"
                    + "换成活性透镜之后就<b>换不回去了</b>（除非把仓清空再用传送带喂）。"
                    + "比的是 ItemProto.Name（原始中文键）不是翻译后的名字");

            _powerMul = Config.powerMultiplier > 0f ? Config.powerMultiplier : 1f;
            _photonMul = Config.photonMultiplier > 0f ? Config.photonMultiplier : 1f;

            // healRate 必须严格小于 1，否则净消耗不为正，透镜成了永动机。
            // 钳住而不是照用，并且说清楚为什么——这是个物理约束不是口味
            _healRate = Config.healRate;

            if (_healRate >= 1f)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"活性透镜：healRate = {Config.healRate} ≥ 1，已钳到 0.95。"
                    + "自愈速率必须严格小于 1，否则净消耗不为正，一枚透镜能永远烧下去");

                _healRate = 0.95f;
            }

            if (_healRate < 0f) _healRate = 0f;

            ProjectEdenPlugin.Log.LogInfo(
                $"活性透镜：物品 {LensId}，原版催化剂「{Config.defaultCatalystName}」= {DefaultCatalystId}，"
                + $"发电 ×{_powerMul:0.##}，光子 ×{_photonMul:0.##}，自愈 {_healRate:0.##}"
                + $"（满照寿命 ×{1f / (1f - _healRate):0.##}）");

            ApplyCatalystData();
            ReportBalance();
            Report();
        }

        /// <summary>
        /// 把倍率写成**数据**，而不是转译出来。
        ///
        /// <b>0.10.35 把催化剂做成了和燃料同构的通用系统，本 mod 的三个转译器因此全部作废。</b>
        /// 旧版三处一字不差的 <c>cata = 2 × (1 + inc)</c> 里那个字面量 2 没有了，现在是：
        /// <code>
        /// V_0  = (float)Cargo.accTableMilli[catalystIncLevel]            // 喷涂等级
        /// V_1  = ItemProto.catalystAbilityById[curCatalystId]            // ← 按物品 id 的倍率
        /// cata = (catalystPoint > 0 || catalystCount > 0) ? V_1 * (1 + V_0) : 1
        /// </code>
        /// 而那张表由 <c>ItemProto.InitCatalystAbilityById</c> 建：
        /// <c>catalystAbilityById[proto.ID] = proto.Ability × 0.01f</c>，只收
        /// <c>CatalystType > 0</c> 的。**所以「×5」现在就是 <c>Ability = 500</c>**，
        /// 三处一起改的问题不存在了——数据只有一份。
        ///
        /// 插入那一头同理：<c>EntityFastFillIn</c> 现在查
        /// <c>ItemProto.catalystNeeds[catalystMask]</c>（和 <c>fuelNeeds</c> / <c>turretNeeds</c>
        /// 同一族），所以只要 <c>CatalystType</c> 的位和接收站的 <c>powerCatalystMask</c> 对上，
        /// 白名单自己就收了我们——那个转译器也一并删了。
        ///
        /// <b><c>CatalystType</c> 抄原版透镜的，不写死。</b> 它是个位掩码，要和
        /// <c>PrefabDesc.powerCatalystMask</c> 对位，而两者都在 <c>resources.assets</c> 里，
        /// 离线读不到——写死就是猜，猜错了表现是「放不进接收站」，一个字都不报。
        ///
        /// <b>两张表都是 preload 期建的静态缓存，必须重跑</b>（陷阱 4b）：
        /// <c>InitCatalystNeeds</c> 在 <c>VFPreload.PreloadThread</c> @08AB、
        /// <c>InitCatalystAbilityById</c> 在 @08B0，而 LDBTool 的 post-patch 列表里
        /// 两个都没有。不重跑的话本 mod 的透镜根本不在表里，倍率读出来是 0
        /// （<b>比 1 还糟：接收站直接不发电</b>），而且不会报错。两个构建器都是从
        /// <c>dataArray</c> 整体重建的，所以重跑幂等。
        /// </summary>
        private static void ApplyCatalystData()
        {
            ItemProto lens = LDB.items.Select(LensId);

            if (lens == null)
            {
                ProjectEdenPlugin.Log.LogError($"活性透镜：LDB 里取不到物品 {LensId}，倍率写不进去");

                return;
            }

            ItemProto vanilla = DefaultCatalystId > 0 ? LDB.items.Select(DefaultCatalystId) : null;

            if (vanilla == null || vanilla.CatalystType <= 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"活性透镜：原版催化剂「{Config.defaultCatalystName}」拿不到 CatalystType"
                    + $"（proto {(vanilla == null ? "为空" : "的 CatalystType = " + vanilla.CatalystType)}）。"
                    + "**透镜放不进射线接收站**——那个位掩码要和 PrefabDesc.powerCatalystMask 对位，"
                    + "它在 resources.assets 里，离线读不到也猜不得");

                return;
            }

            lens.CatalystType = vanilla.CatalystType;

            // **倍率是相对原版透镜的，不是绝对值——差别是整整一倍。**
            // 旧转译器把硬写的那个 2 乘上 powerMultiplier，所以 ×5 的配置意味着
            // 最终 cata = 10（引力透镜是 2）。照搬成 `powerMul × 100` 会得到 cata = 5，
            // 也就是**悄悄砍掉一半**，而且没有任何报错——两份文档里那张
            // 「发电 ×2 / ×10」的对照表是唯一会露馅的地方。
            //
            // 所以基数取原版透镜自己的 Ability 而不是写死 100：它在
            // resources.assets 里，写死就是猜，而且万一原版哪天把引力透镜从 ×2 调走，
            // 「相对它 5 倍」这个承诺会自己跟上。
            var ability = (int)System.Math.Round(vanilla.Ability * _powerMul);

            if (ability < 1) ability = 1;

            lens.Ability = ability;

            ItemProto.InitCatalystNeeds();
            ItemProto.InitCatalystAbilityById();

            float[] table = ItemProto.catalystAbilityById;
            float mine = table != null && LensId < table.Length ? table[LensId] : 0f;
            float theirs = table != null && DefaultCatalystId < table.Length ? table[DefaultCatalystId] : 0f;

            if (mine <= 0f)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"活性透镜：重建之后 catalystAbilityById[{LensId}] 仍然是 {mine}，"
                    + "**接收站装上它会直接不发电**。检查 CatalystType 有没有写进去、以及重建有没有真的跑");

                return;
            }

            // **把「相对倍率」也打出来。** 绝对值自己看不出对不对——
            // 要核的是它是不是配置里那个 powerMultiplier，而那要两个数相除才知道。
            float relative = theirs > 0f ? mine / theirs : 0f;

            ProjectEdenPlugin.Log.LogInfo(
                $"活性透镜·催化剂表：CatalystType {lens.CatalystType}（抄自「{Config.defaultCatalystName}」）、"
                + $"Ability {lens.Ability} → 倍率 ×{mine:0.##}；"
                + $"原版「{Config.defaultCatalystName}」Ability {vanilla.Ability} → ×{theirs:0.##}；"
                + $"**相对原版 ×{relative:0.##}**（配置要的是 ×{_powerMul:0.##}）。"
                + "两张表（catalystNeeds / catalystAbilityById）已按原版自己的构建器重建"
                + "——它们是 preload 期建的，LDBTool 两个都不会重跑。");

            if (theirs > 0f && System.Math.Abs(relative - _powerMul) > 0.05f)
                ProjectEdenPlugin.Log.LogWarning(
                    $"活性透镜：相对倍率算出来是 ×{relative:0.###}，配置要的是 ×{_powerMul:0.###}，对不上。"
                    + "Ability 是整数，原版基数小的时候四舍五入会有偏差；差得多就说明基数读错了");
        }

        /// <summary>
        /// 把原版射线接收站的真值和换算结果打进日志。
        ///
        /// <b>为什么必须在游戏里打而不是写在配置里：</b><c>genEnergyPerTick</c> 和
        /// <c>powerProductHeat</c> 都在 <c>resources.assets</c> 的 prefab 里，
        /// 离线读不到也反编译不出来——写死一个数就是猜。同
        /// <c>CompositeRegistry.ReportVanillaRoutes</c> 的先例。
        ///
        /// <b>重点是光子那一行。</b> 一台接收站每 tick 最多吐一个光子
        /// （<c>GameTick_Gamma</c> 六个传送带口，每个成功后都是 <c>br</c> 跳过其余），
        /// 也就是 <b>60 个/秒</b>封顶；而铸造侧没有这个限制，并且
        /// <b>统计是按截断前记的</b>（@0106 先记账、@0129 才把 productCount 截到 20）。
        /// 铸快了会让产量面板显示传送带永远看不到的光子。所以把两个数并排打出来。
        /// </summary>
        private static void ReportBalance()
        {
            ItemProto receiver = null;

            foreach (ItemProto proto in LDB.items.dataArray)
            {
                if (proto?.prefabDesc == null || !proto.prefabDesc.gammaRayReceiver) continue;

                receiver = proto;

                break;
            }

            if (receiver == null)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "活性透镜：找不到任何 gammaRayReceiver 建筑，平衡对照这一行打不出来");

                return;
            }

            PrefabDesc desc = receiver.prefabDesc;

            // 满照、满预热、不喷涂、光子模式：capacity = (1 + 1×1.5) × cata × 8 × genEnergyPerTick
            const double Warm = 1.0 + 1.0 * 1.5;
            const double PhotonMode = 8.0;

            double vanillaCap = Warm * 2.0 * PhotonMode * desc.genEnergyPerTick;
            double livingCap = Warm * (2.0 * _powerMul) * PhotonMode * desc.genEnergyPerTick;

            ProjectEdenPlugin.Log.LogInfo(
                $"活性透镜 · 平衡对照：「{receiver.name}」发电 {Power(desc.genEnergyPerTick)}，"
                + $"光子热值 {desc.powerProductHeat}");

            if (desc.powerProductHeat <= 0L)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "活性透镜 · 平衡对照：powerProductHeat 是 0，光子倍率这一档没法核对");

                return;
            }

            double vanillaPhotons = vanillaCap / desc.powerProductHeat * 60.0;
            double livingHeat = desc.powerProductHeat * (double)_powerMul / _photonMul;
            double livingPhotons = livingCap / livingHeat * 60.0;

            var line =
                $"活性透镜 · 平衡对照：满照满预热光子模式下，引力透镜 {vanillaPhotons:0.###} 个/秒 → "
                + $"活性透镜 {livingPhotons:0.###} 个/秒（×{livingPhotons / vanillaPhotons:0.##}），"
                + "出口天花板 60 个/秒";

            // 天花板是硬的：超了的部分进产量统计但永远上不了传送带，
            // 是「面板好看、反物质线饿着」那种最难查的形状，所以这里要 WARNING 而不是 INFO
            if (livingPhotons > 60.0)
                ProjectEdenPlugin.Log.LogWarning(
                    line + "。**已经超出天花板**：多出来的光子会进产量面板但永远吐不到传送带上。"
                    + "把 lens.json 的 photonMultiplier 调小");
            else
                ProjectEdenPlugin.Log.LogInfo(line);
        }

        private static string Power(long perTick)
        {
            double w = perTick * 60.0;

            if (w >= 1e9) return $"{w / 1e9:0.##} GW";
            if (w >= 1e6) return $"{w / 1e6:0.##} MW";

            return $"{w / 1e3:0.##} kW";
        }

        // ════════════════════════════════════════════════════════════════════
        //  运行时助手
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 替掉 <c>GameTick_Gamma</c> 里那一处 <c>ldfld productHeat</c>。
        ///
        /// 光子是 <c>capacityCurrentTick / productHeat</c>。分子已经被
        /// <see cref="CataFactor"/> 乘上了 <c>powerMultiplier</c>，所以这里把分母
        /// 也乘上 <c>powerMultiplier / photonMultiplier</c>，光子净倍率就是
        /// <c>photonMultiplier</c>。两个旋钮因此互不牵扯。
        /// </summary>
        internal static long PhotonHeat(ref PowerGeneratorComponent gen)
        {
            long heat = gen.productHeat;

            if (heat <= 0L || !Active || gen.catalystId != LensId) return heat;

            var scaled = (long)(heat * (double)_powerMul / _photonMul);

            return scaled > 0L ? scaled : 1L;
        }

        /// <summary>
        /// 传送带取货的 <c>filter</c> 参数。仓里有货就维持原版行为（只收同一种），
        /// 空仓才放开交给 <see cref="CatalystNeeds"/> 的白名单。
        ///
        /// 这个「空仓才放开」是<b>防丢件</b>的：<c>PickFrom</c> 取到不匹配的货之后，
        /// 原版是直接 <c>bne.un</c> 跳过——<b>货已经从传送带上拿下来了，就这么没了</b>。
        /// 两道闸一起卡住，混料的传送带最多是取不上货，不会吃件。
        /// </summary>
        internal static int CatalystFilter(ref PowerGeneratorComponent gen)
        {
            return !Active || gen.catalystPoint > 0 ? gen.catalystId : 0;
        }

        /// <summary>
        /// 传送带取货的 <c>needs</c> 参数，替掉原版的 <c>ldnull</c>。
        ///
        /// <b>必须正好 6 格。</b><c>CargoPath.TryPickItem</c> @010C~013A 把它
        /// <b>展开成六次 <c>ldelem.i4</c></b>（<c>needs[0]</c>~<c>needs[5]</c>），
        /// 无条件索引——短一格就是 <c>IndexOutOfRangeException</c>。补 0 是安全的：
        /// 真实物品 ID 不可能是 0。和 <c>filter</c> 是<b>与</b>的关系
        /// （@0104：<c>filter != 0 &amp;&amp; item != filter</c> 就退出）。
        ///
        /// <b><c>[ThreadStatic]</c> 而不是共享数组</b>：电力 tick 跑在
        /// <c>FactoryPowerSystemGameTick_Parallel</c> 下，每行星一个线程。
        /// 初始化器只在第一个线程上跑，所以要惰性 new。tick 路径上不许分配，
        /// 所以 new 完就一直复用。
        /// </summary>
        [ThreadStatic] private static int[] _needs;

        internal static int[] CatalystNeeds(ref PowerGeneratorComponent gen)
        {
            int[] a = _needs ?? (_needs = new int[6]);

            a[0] = gen.catalystId;
            a[1] = 0;
            a[2] = 0;
            a[3] = 0;
            a[4] = 0;
            a[5] = 0;

            if (!Active || gen.catalystPoint > 0) return a;

            // 空仓：两种透镜都收，谁先到算谁的
            a[0] = DefaultCatalystId > 0 ? DefaultCatalystId : gen.catalystId;
            a[1] = LensId;

            return a;
        }

        /// <summary>
        /// 替掉取货之后那次 <c>ldfld catalystId</c>（比较的右操作数）。
        /// 返回 <paramref name="picked" /> 表示接受，返回别的值让原版的 <c>bne.un</c> 跳过。
        ///
        /// 空仓收到另一种透镜时<b>就地改写 <c>catalystId</c></b>——这才是换料的真正动作，
        /// 统计 / 拆返还 / 消耗记账全跟着它走。
        /// </summary>
        internal static int AcceptPicked(int picked, ref PowerGeneratorComponent gen)
        {
            if (picked <= 0) return -1;                  // 没取到货
            if (picked == gen.catalystId) return picked; // 原版路径，一字不差

            // 下面这些分支在 CatalystFilter/CatalystNeeds 卡住之后其实到不了，
            // 留着是因为「到不了」依赖另外两个助手都对，而丢件是不可逆的
            if (!Active) return -1;
            if (gen.catalystPoint > 0) return -1;
            if (picked != LensId && picked != DefaultCatalystId) return -1;

            gen.catalystId = picked;
            gen.catalystIncPoint = 0; // 空仓换料，残留的喷涂点数不属于新料

            return picked;
        }


        // ════════════════════════════════════════════════════════════════════
        //  B + D：GameTick_Gamma 里的光子分母和两个取货口
        // ════════════════════════════════════════════════════════════════════

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(PowerGeneratorComponent), nameof(PowerGeneratorComponent.GameTick_Gamma))]
        private static IEnumerable<CodeInstruction> GammaTickTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            MethodInfo photon = AccessTools.Method(typeof(LensPatches), nameof(PhotonHeat));
            MethodInfo filter = AccessTools.Method(typeof(LensPatches), nameof(CatalystFilter));
            MethodInfo needs = AccessTools.Method(typeof(LensPatches), nameof(CatalystNeeds));
            MethodInfo accept = AccessTools.Method(typeof(LensPatches), nameof(AcceptPicked));

            FieldInfo heatField = AccessTools.Field(typeof(PowerGeneratorComponent),
                nameof(PowerGeneratorComponent.productHeat));

            FieldInfo cataField = AccessTools.Field(typeof(PowerGeneratorComponent),
                nameof(PowerGeneratorComponent.catalystId));

            if (photon == null || filter == null || needs == null || accept == null
                || heatField == null || cataField == null)
            {
                ProjectEdenPlugin.Log.LogError("活性透镜：解析不到 GameTick_Gamma 的助手或字段，本方法不改");

                return code;
            }

            var photonHits = 0;
            var beltHits = 0;

            // ── B. 光子分母。方法里只有这一处读 productHeat（实测计数 1）──
            // `ldarg.0` 已经把 ref this 压上来了，所以把 ldfld 原地换成 call 就是等价的
            for (var i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldfld) continue;
                if (!SameField(code[i].operand, heatField)) continue;

                code[i].opcode = OpCodes.Call;
                code[i].operand = photon;

                photonHits++;
            }

            // ── D. 两个传送带取货口 ──
            //
            // 原版形状（两处一字不差，加宽后也一样，已对着 patched 副本核过）：
            //   ldarg.0 ; ldfld catalystId   ← filter
            //   ldnull                        ← needs
            //   ldloca ; ldloca ; callvirt PickFrom
            //   ldarg.0 ; ldfld catalystId ; bne.un
            //
            // <b>锚点从 ldnull 换成了「名字叫 PickFrom 的 callvirt」，这是一次返工。</b>
            // 上一版拿 ldnull 起手、再向后看两个 ldloca 和一个 callvirt——形状写得对，
            // 实测却<b>一处都匹配不上</b>（日志：取货口 0，而同一个转译器里的光子那处匹配成功，
            // 所以转译器本身是跑了的）。用通用操作码当锚点的坏处就在这儿：
            // 否掉它的可能有六七条谓词，而<b>失败时一条信息都没有</b>，只能靠猜。
            //
            // 改成锚在语义唯一的那个东西上（方法名），并且<b>每一条谓词不匹配时都记一笔</b>，
            // 所以下次再坏，日志直接说是哪一条，不用再来一轮。
            var pickAnchors = 0;
            var rejectShape = 0;
            var rejectFilter = 0;
            var rejectCompare = 0;

            for (int k = code.Count - 1; k >= 4; k--)
            {
                if (code[k].opcode != OpCodes.Callvirt) continue;

                // 取不到 MethodInfo 也要算一笔：preloader 改过签名的方法，Harmony 有可能
                // 解析不出来而把 operand 交成 null（CLAUDE.md 记过这个形状）
                var pick = code[k].operand as MethodInfo;

                if (pick == null || pick.Name != "PickFrom") continue;

                pickAnchors++;

                if (k + 3 >= code.Count) { rejectShape++; continue; }

                // 往前：ldfld catalystId ; ldnull ; ldloca ; ldloca ; [callvirt]
                if (!IsLdloca(code[k - 1]) || !IsLdloca(code[k - 2])
                    || code[k - 3].opcode != OpCodes.Ldnull)
                {
                    rejectShape++;
                    continue;
                }

                if (code[k - 4].opcode != OpCodes.Ldfld || !SameField(code[k - 4].operand, cataField))
                {
                    rejectFilter++;
                    continue;
                }

                int i = k - 3;   // ldnull（needs）

                // 取货之后：<载入 this> ; ldfld catalystId ; bne.un
                //
                // <b>这里不再写死 `ldarg.0`，这是第二次返工。</b> 上一版要求 cmp 处正好是
                // <c>OpCodes.Ldarg_0</c>，结果两处<b>都</b>倒在这一条上——而 Cecil 读原版
                // （以及 preloader 加宽后的副本）那里明明白白就是 `ldarg.0`。
                // 也就是说 Harmony 交到转译器手里的编码和文件里的不是同一个形式：
                // 「载入第 0 号参数」有 <c>ldarg.0</c> / <c>ldarg.s 0</c> / <c>ldarg 0</c> 三种写法，
                // 认死一种就会在另一种上静默失配。
                //
                // 真正的锚点是<b>那次 `ldfld catalystId`</b>（语义上唯一），
                // 载入 this 的那条只要求它「是在载入第 0 号参数」，不管用哪种编码。
                // **第三次返工，而这次的对手是本 mod 自己。** 1c 的品质改写在
                // <c>PickFrom</c> 之后插了 <c>ldsfld Q0 ; stloc</c>（把出参的品质接回来），
                // 于是「取货之后紧跟着 ldfld catalystId」不再成立，两处取货口<b>同时</b>失配
                // ——日志里只表现为「应当 7 处、实际 5 处」，而那 5 处都是对的。
                // 先把我们自己插的指令跳掉，再从那里开始找锚点。
                int scan = QualityAccess.SkipChannelNoise(code, k + 1);

                int cmp = -1;

                for (int p = scan + 1; p <= scan + 3 && p < code.Count; p++)
                {
                    if (code[p].opcode != OpCodes.Ldfld) continue;
                    if (!SameField(code[p].operand, cataField)) continue;
                    if (!IsLdargZero(code[p - 1])) break;   // 前一条不是载入 this，不敢动

                    cmp = p - 1;
                    break;
                }

                if (cmp < 0)
                {
                    rejectCompare++;

                    if (rejectCompare <= 2)
                        ProjectEdenPlugin.Log.LogWarning(
                            $"活性透镜·诊断：PickFrom 之后没认出「载入 this + ldfld catalystId」"
                            + $"（跳过品质指令后从第 {scan - k} 条起算）。"
                            + $"实际是 [{code[scan].opcode} {code[scan].operand}]"
                            + $" [{Peek(code, scan + 1)}] [{Peek(code, scan + 2)}]");

                    continue;
                }

                // (3) 比较：先 dup 一份取货结果，再把 ldfld 换成 AcceptPicked(picked, ref gen)。
                //     插入点上的标签要搬到 dup 上，否则分支会落到 dup 之后、栈就错了
                var dup = new CodeInstruction(OpCodes.Dup);

                dup.labels.AddRange(code[cmp].labels);
                code[cmp].labels.Clear();

                code[cmp + 1].opcode = OpCodes.Call;
                code[cmp + 1].operand = accept;

                code.Insert(cmp, dup);

                // (2) needs：ldnull → ldarg.0 + call CatalystNeeds
                code[i].opcode = OpCodes.Ldarg_0;
                code[i].operand = null;

                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, needs));

                // (1) filter：ldfld catalystId → call CatalystFilter（ldarg.0 已在前面）
                code[i - 1].opcode = OpCodes.Call;
                code[i - 1].operand = filter;

                beltHits++;
            }

            Hits["GameTick_Gamma.光子"] = photonHits;
            Hits["GameTick_Gamma.取货口"] = beltHits;

            if (photonHits != 1)
                ProjectEdenPlugin.Log.LogError(
                    $"活性透镜：GameTick_Gamma 里应当正好有 1 处 productHeat，实际 {photonHits} 处，"
                    + "光子倍率不生效");

            if (beltHits != 2)
                ProjectEdenPlugin.Log.LogError(
                    $"活性透镜：GameTick_Gamma 里应当正好有 2 个传送带取货口，实际 {beltHits} 个。"
                    + "少改的那个口喂不进活性透镜，玩家会看到「有的接收站吃、有的不吃」。"
                    + $"（找到 {pickAnchors} 个 PickFrom 调用；被否掉的：形状 {rejectShape}、"
                    + $"filter 字段 {rejectFilter}、比较字段 {rejectCompare}）"
                    + "——**四个数就是诊断本身**：PickFrom 为 0 说明连方法都没认出来"
                    + "（多半是 operand 解析不出，preloader 动过那个签名）；"
                    + "形状不为 0 说明原版把参数摆法改了；字段不为 0 说明 catalystId 认错了。");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"活性透镜：GameTick_Gamma 的 {beltHits} 个传送带取货口已接管"
                    + $"（共扫到 {pickAnchors} 个 PickFrom 调用）");

            return code;
        }


        /// <summary>
        /// 每个方法<b>最近一次</b>改写了几处。键是方法，不是累加器——
        /// 同一个方法会被 transpile 不止一次（别的补丁挂到同一个方法上时 Harmony 会重跑），
        /// 用累加器总数就会翻倍，而每一次其实都是对的。这是
        /// <c>RecipeTypeCompatPatches</c> 上交过学费的那条。
        /// </summary>
        private static readonly Dictionary<string, int> Hits = new Dictionary<string, int>();

        /// <summary>
        /// 把各处改写数汇总成一行，开局日志里一眼能核。
        ///
        /// <b>0.10.35 之后只剩 3 处，原先是 7。</b> 少掉的四处不是没修，是**不需要修了**：
        /// 三处 <c>cata</c> 倍率和一处快速填入都被原版自己的数据表接管了
        /// （<c>ItemProto.Ability</c> / <c>CatalystType</c>，见 <see cref="ApplyCatalystData"/>），
        /// 转译器已删。剩下的三处是光子分母一处 + 传送带取货口两处。
        /// </summary>
        private static void Report()
        {
            var got = 0;

            var where = new List<string>();

            foreach (KeyValuePair<string, int> kv in Hits)
            {
                got += kv.Value;
                where.Add($"{kv.Key} {kv.Value}");
            }

            where.Sort();

            // 一处 productHeat（光子分母）+ 两个传送带取货口
            const int want = 1 + 2;

            string detail = string.Join("、", where.ToArray());

            if (got == want && Hits.Count == 2)
                ProjectEdenPlugin.Log.LogInfo($"活性透镜：共改写 {got} 处，全部命中（{detail}）");
            else
                ProjectEdenPlugin.Log.LogError(
                    $"活性透镜：应当改写 {want} 处，实际 {got} 处（{detail}）——"
                    + "差的那些地方功能不生效，游戏更新过的话要重新读 IL");
        }

        // ════════════════════════════════════════════════════════════════════
        //  C. 手动换料
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 空仓时，把手上拿着的那种透镜设成这台接收站认的料，然后让原版照常跑。
        ///
        /// <b>做成前缀而不是 transpiler，是因为原版只有一道门。</b>
        /// <c>OnCataButtonClick</c> @00C6 是 <c>if (inhandItemId != catalystId) { 弹「只能放入 X」; return; }</c>
        /// ——先把 <c>catalystId</c> 改好，那道门自己就放行了，连错误提示里的名字都跟着对。
        /// 放入量、堆叠上限、取出时的零头，全部照原版口径走，一处 3600 都没碰。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIPowerGeneratorWindow), "OnCataButtonClick")]
        private static void SwitchCatalystByHand(UIPowerGeneratorWindow __instance)
        {
            if (!Active)
            {
                ReportInsert("手动放入：功能未生效（lens.json 关了，或者透镜物品没注册成）");

                return;
            }

            PowerSystem ps = __instance.powerSystem;

            if (ps?.genPool == null || __instance.player == null)
            {
                ReportInsert("手动放入：powerSystem / player 是 null，窗口状态不对");

                return;
            }

            int id = __instance.generatorId;

            if (id <= 0 || id >= ps.genPool.Length)
            {
                ReportInsert($"手动放入：generatorId = {id} 越界（genPool 长 {ps.genPool.Length}）");

                return;
            }

            ref PowerGeneratorComponent gen = ref ps.genPool[id];

            if (gen.id != id || !gen.gamma)
            {
                ReportInsert($"手动放入：这台不是射线接收站（id {gen.id} vs {id}，gamma={gen.gamma}）");

                return;
            }

            int hand = __instance.player.inhandItemId;

            // 仓里还有货就不换：换了的话剩下的点数会被算成新料，等于凭空换物品。
            // 这是最常见的一条，所以提示里直接把该怎么办写出来
            if (gen.catalystPoint > 0)
            {
                ReportInsert(
                    $"手动放入：仓里还有 {gen.catalystPoint / 3600} 枚「{NameOf(gen.catalystId)}」，不换料。"
                    + "要换成另一种透镜，得先把仓清空（点一下槽位取出来）");

                return;
            }

            if (hand <= 0)
            {
                ReportInsert("手动放入：手上没拿东西（这是取出，不是放入）");

                return;
            }

            if (hand == gen.catalystId) return; // 本来就是这种，原版自己会处理

            if (hand != LensId && hand != DefaultCatalystId)
            {
                ReportInsert(
                    $"手动放入：手上是「{NameOf(hand)}」({hand})，既不是活性透镜({LensId})"
                    + $"也不是引力透镜({DefaultCatalystId})，不换料");

                return;
            }

            gen.catalystId = hand;
            gen.catalystIncPoint = 0;

            ReportInsert($"手动放入：实体 {gen.entityId} 空仓，已换成「{NameOf(hand)}」");
        }

        private static string NameOf(int itemId)
        {
            return itemId <= 0 ? "空" : LDB.items.Select(itemId)?.name ?? itemId.ToString();
        }

        /// <summary>
        /// 放入路径的诊断行，全局限量。
        ///
        /// <b>加这个是因为上一版没加，而代价当场就付了：</b>玩家报「活性透镜放不进射线接收站」，
        /// 而日志里注册、图标、六处 transpiler 全是绿的——<b>分不出「补丁没跑」和
        /// 「补丁跑了但某个闸挡住了」</b>。这正是本仓库记了三次的那条：
        /// 有「什么都不做」分支的功能，那条分支要在同一次提交里带上日志行。
        /// </summary>
        private static int _insertLogs;

        private static void ReportInsert(string line)
        {
            if (System.Threading.Interlocked.Increment(ref _insertLogs) > 24) return;

            ProjectEdenPlugin.Log.LogInfo($"活性透镜 · {line}");
        }

        // ════════════════════════════════════════════════════════════════════
        //  E. 自愈
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>它在射线里愈合，在黑暗里老去。</b>
        ///
        /// 原版透镜烧得毫无条件——<c>GameTick_Gamma</c> @0019 那个 <c>if</c> 里只有
        /// <c>tick % 10 == 0</c>，<b>不看 <c>currentStrength</c>、不看预热、不看电网要不要电</b>。
        /// 活性透镜反过来：按这一 tick 真的收到多少射线加回去一点。
        ///
        /// <b>三件必须一起做对的事：</b>
        /// <list type="number">
        /// <item><c>ref __instance</c>。它是 struct 的实例方法，不取引用就是改副本，
        /// 一点效果没有而且不报错。已核实调用点
        /// （<c>PowerSystem.GameTick</c> 的 V_107 类型就是 <c>PowerGeneratorComponent&amp;</c>，
        /// 从 <c>ldelema</c> 拿的元素地址），所以写回落在 <c>genPool[i]</c> 上。</item>
        /// <item><b>必须同时补 <c>catalystIncPoint</c>。</b> 原版消耗块是两个字段一起走的
        /// （@002A <c>point -= 1</c> 配 @0038 <c>incPoint -= incLevel</c>），比值
        /// <c>incPoint / point</c>——也就是槽里透镜的喷涂等级——才不变。只往 point 里加
        /// 的话比值单调下滑：healRate 0.6 时 incPoint 在第 3600 步就归零而 point 要到第 9000 步，
        /// <b>延长出来的那 15 分钟透镜是完全没喷涂的</b>，×5 退回 ×2，全程不报错。</item>
        /// <item><b>确定性抖动，不要 <c>Random</c>。</b> 这里跑在
        /// <c>FactoryPowerSystemGameTick_Parallel</c> 下，每行星一个线程，共享 RNG 会被撕坏
        /// （<c>MegaVirtualLogisticsPatches</c> 已经为这条付过代价）。</item>
        /// </list>
        ///
        /// <b>不会凭空造透镜。</b> <c>healRate &lt; 1</c> 且 <c>currentStrength ≤ 1</c>，
        /// 所以每个消耗 tick 最多加回 1 点而必定扣掉 1 点，<c>catalystPoint</c> 单调不增——
        /// 拆建筑时 <c>TakeBackItemsInEntity</c> 按 3600 还回去的数永远不会超过放进去的。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PowerGeneratorComponent), nameof(PowerGeneratorComponent.GameTick_Gamma))]
        private static void Heal(ref PowerGeneratorComponent __instance, bool useCata)
        {
            // useCata 就是原版消耗的那道闸（调用点传的是 tick % 10 == 0）。
            // 名字有误导性，值不是。愈合和消耗必须同频，否则比例对不上
            if (!useCata || !Active) return;
            if (__instance.catalystId != LensId) return;

            int point = __instance.catalystPoint;

            if (point <= 0) return;

            float strength = __instance.currentStrength;

            if (strength <= 0f) return;

            float regen = _healRate * strength;

            if (regen <= 0f) return;

            // regen 是小数而 catalystPoint 是 Int32。攒余数要加存档字段（否决），
            // 所以按概率取整，种子取 entityId + 第几个消耗 tick
            uint roll = Hash((uint)__instance.entityId, (uint)(GameMain.gameTick / 10L)) & 0xFFFFu;

            if (roll >= (uint)(regen * 65536f)) return;

            // 和原版 get_catalystIncLevel 同一个算法（整除），比例才锁得住
            int level = __instance.catalystIncPoint / point;

            __instance.catalystPoint = point + 1;

            if (level > 0) __instance.catalystIncPoint += level;
        }

        /// <summary>
        /// 本版 HarmonyLib 没有 <c>IsLdloca</c> 扩展（只有 <c>IsLdloc</c>），自己认一下。
        /// </summary>
        private static bool IsLdloca(CodeInstruction ins)
        {
            return ins.opcode == OpCodes.Ldloca || ins.opcode == OpCodes.Ldloca_S;
        }

        /// <summary>
        /// 「载入第 0 号参数」的<b>三种编码</b>都认：<c>ldarg.0</c> / <c>ldarg.s 0</c> / <c>ldarg 0</c>。
        ///
        /// 只认 <c>ldarg.0</c> 吃过一次亏：Cecil 读文件看到的是 <c>ldarg.0</c>，
        /// Harmony 交给转译器的却未必是同一个形式，而失配时<b>没有任何报错</b>。
        /// 短/长形式这一类差别，凡是按操作码匹配的地方都要一次认全。
        /// </summary>
        /// <summary>诊断用：越界也要能打印，否则诊断自己会抛。</summary>
        private static string Peek(List<CodeInstruction> code, int at) =>
            at >= 0 && at < code.Count ? code[at].opcode + " " + code[at].operand : "（越界）";

        private static bool IsLdargZero(CodeInstruction ins)
        {
            if (ins.opcode == OpCodes.Ldarg_0) return true;
            if (ins.opcode != OpCodes.Ldarg && ins.opcode != OpCodes.Ldarg_S) return false;

            return ins.operand == null || System.Convert.ToInt32(ins.operand) == 0;
        }

        /// <summary>
        /// 比较转译器里的字段操作数。<b>不要只用 <c>ReferenceEquals</c>。</b>
        ///
        /// 反射对象的引用相等在<b>大多数</b>情况下成立（运行时会缓存 <c>RuntimeFieldInfo</c>），
        /// 所以用它写出来的匹配器能跑很久——直到某一次不成立，而那时的表现是
        /// <b>匹配数为 0、没有任何报错</b>，和「原版改了形状」长得一模一样，分不开。
        ///
        /// 按「声明类型 + 名字」兜底，代价是一次字符串比较（只在编译补丁时跑，不在 tick 上）。
        /// </summary>
        private static bool SameField(object operand, FieldInfo want)
        {
            if (want == null) return false;
            if (ReferenceEquals(operand, want)) return true;

            return operand is FieldInfo got
                   && got.Name == want.Name
                   && got.DeclaringType == want.DeclaringType;
        }

        /// <summary>tick 路径上的确定性散列，不分配、不共享状态。</summary>
        private static uint Hash(uint a, uint b)
        {
            uint h = a * 2654435761u ^ b * 2246822519u;

            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;

            return h;
        }
    }
}
