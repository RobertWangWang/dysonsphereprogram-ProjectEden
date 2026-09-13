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
    /// <b>不加新建筑是所有者的决定，代价记在这里。</b> 引擎<b>不从透镜物品上读任何东西</b>：
    /// <c>catalystId</c> 只是一次相等比较，<c>cata = 2 × (1 + inc)</c> 是硬写在三个方法里的
    /// 字面量。所以「只加一个新透镜物品、让它进原版接收站」除了换个图标，行为和引力透镜
    /// <b>一字不差</b>——倍率只能落在我们自己的 transpiler 上，也就是设计稿
    /// <c>活性透镜.md</c> 第六节明确否掉过的那条「同一个乘积写在三个方法里」。
    /// 现在那三处一起改，并断言必须正好命中 3 处。
    ///
    /// <b>这条约束不是风格问题。</b> 只改 <c>EnergyCap_Gamma_Req</c> 的话，接收站实发的电
    /// 和它报给电网的最大出力（<c>MaxOutputCurrent_Gamma</c>）、向电网索要的量
    /// （<c>RequiresCurrent_Gamma</c>）就对不上，电网按错误的数调度——症状是功率曲线抖
    /// <b>而不是报错</b>。形状和自动集装机那四个 <c>4</c> 完全一样：同一个常数既是产能、
    /// 又是申报、又是索取。
    ///
    /// <b>光子和电力是两个独立旋钮</b>，这是读 IL 读出来的，不是设计出来的：
    /// <c>GameTick_Gamma</c> @00D2 是 <c>productCount += capacityCurrentTick / productHeat</c>，
    /// 分母和分子完全解耦。所以电力倍率改分子（那三处 <c>cata</c>），光子倍率改分母
    /// （一处 <c>productHeat</c>），互不牵扯。
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

            ReportBalance();
            Report();
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
        /// 替掉那三处 <c>ldc.r4 2</c>。<b>只有仓里有货时才会走到这里</b>——
        /// 原版把它放在 <c>catalystPoint &gt; 0</c> 的分支里。
        /// </summary>
        internal static float CataFactor(ref PowerGeneratorComponent gen)
        {
            return Active && gen.catalystId == LensId ? 2f * _powerMul : 2f;
        }

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
        /// 替掉 <c>PlanetFactory.EntityFastFillIn</c> @1223 那次 <c>ldfld catalystId</c>。
        ///
        /// <b>这条路是第一版漏掉的，症状正是「活性透镜放不进射线接收站」。</b>
        /// 从背包 shift 点击建筑走的是这个方法，而它<b>不看你手上拿的是什么</b>——
        /// 它读 <c>catalystId</c>，然后 <c>TakeItemFromPlayer(ref 那个 ID, …)</c>
        /// <b>照着这个号去背包里拿</b>。所以不改这里的话，shift 点永远只会塞引力透镜，
        /// 背包里的活性透镜一件都进不去，而且<b>什么提示都没有</b>。
        ///
        /// 和窗口那条（<see cref="SwitchCatalystByHand" />）的区别：那边看你手上拿的是哪种，
        /// 这边看背包里有没有。两种透镜都带着的时候，shift 点优先活性透镜——
        /// 要精确指定就用窗口的催化剂槽。
        /// </summary>
        internal static int FastFillCatalyst(ref PowerGeneratorComponent gen)
        {
            int real = gen.catalystId;

            if (!Active || real == LensId) return real;

            // 仓里还有货就不换：换了的话剩下的点数会被算成新料，等于凭空换物品
            if (gen.catalystPoint > 0) return real;

            Player player = GameMain.mainPlayer;

            if (player?.package == null) return real;

            // 背包里没有活性透镜就维持原样，否则会把这台接收站锁死在一种拿不出来的料上
            if (player.package.GetItemCount(LensId) <= 0) return real;

            gen.catalystId = LensId;
            gen.catalystIncPoint = 0;

            ReportInsert($"快速填入：实体 {gen.entityId} 空仓，背包里有活性透镜，已换料");

            return LensId;
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
        //  A. 倍率：三个方法各一处 ldc.r4 2
        // ════════════════════════════════════════════════════════════════════

        private static readonly string[] CataMethods =
        {
            nameof(PowerGeneratorComponent.EnergyCap_Gamma_Req),
            nameof(PowerGeneratorComponent.MaxOutputCurrent_Gamma),
            nameof(PowerGeneratorComponent.RequiresCurrent_Gamma),
        };

        /// <summary>
        /// 每个方法<b>最近一次</b>改写了几处。键是方法，不是累加器——
        /// 同一个方法会被 transpile 不止一次（别的补丁挂到同一个方法上时 Harmony 会重跑），
        /// 用累加器总数就会翻倍，而每一次其实都是对的。这是
        /// <c>RecipeTypeCompatPatches</c> 上交过学费的那条。
        /// </summary>
        private static readonly Dictionary<string, int> Hits = new Dictionary<string, int>();

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(PowerGeneratorComponent), nameof(PowerGeneratorComponent.EnergyCap_Gamma_Req))]
        [HarmonyPatch(typeof(PowerGeneratorComponent), nameof(PowerGeneratorComponent.MaxOutputCurrent_Gamma))]
        [HarmonyPatch(typeof(PowerGeneratorComponent), nameof(PowerGeneratorComponent.RequiresCurrent_Gamma))]
        private static IEnumerable<CodeInstruction> CataTranspiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);

            // 先解析再进循环：**绝不能把 null 当操作数发出去**，那会一路活到
            // ILManipulator.WriteTo 才炸，栈顶指向 Harmony 的写入器而不是这里
            MethodInfo helper = AccessTools.Method(typeof(LensPatches), nameof(CataFactor));

            if (helper == null)
            {
                ProjectEdenPlugin.Log.LogError("活性透镜：解析不到 CataFactor，倍率补丁本方法不改");

                return code;
            }

            var hits = 0;

            // 原版形状（三处一字不差）：
            //   ldfld catalystPoint ; ldc.i4.0 ; bgt ; ldc.r4 1 ; br ;
            //   [ldc.r4 2] ; ldc.r4 1 ; ldloc(inc) ; add ; mul
            // 认的是「2 后面紧跟 1、ldloc、add、mul」——只认一个 ldc.r4 2 会误伤别处
            for (int i = 0; i + 4 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldc_R4) continue;
                if (!(code[i].operand is float two) || Math.Abs(two - 2f) > 1e-6f) continue;

                if (code[i + 1].opcode != OpCodes.Ldc_R4) continue;
                if (!(code[i + 1].operand is float one) || Math.Abs(one - 1f) > 1e-6f) continue;

                if (!code[i + 2].IsLdloc()) continue;
                if (code[i + 3].opcode != OpCodes.Add) continue;
                if (code[i + 4].opcode != OpCodes.Mul) continue;

                // 原地改操作码，不换对象——`bgt` 正指着这一条，换对象会丢掉标签
                code[i].opcode = OpCodes.Ldarg_0;
                code[i].operand = null;

                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, helper));

                hits++;
                i += 5;
            }

            Hits[original.Name] = hits;

            if (hits != 1)
                ProjectEdenPlugin.Log.LogError(
                    $"活性透镜：{original.Name} 里应当正好有 1 处 cata 倍率，实际 {hits} 处。"
                    + "**三处必须一起改**，漏一处的症状是功率曲线抖而不是报错——游戏更新过的话要重新读 IL");

            return code;
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
                if (!ReferenceEquals(code[i].operand, heatField)) continue;

                code[i].opcode = OpCodes.Call;
                code[i].operand = photon;

                photonHits++;
            }

            // ── D. 两个传送带取货口 ──
            // 原版形状（两处一字不差）：
            //   ldarg.0 ; ldfld catalystId   ← filter
            //   ldnull                        ← needs
            //   ldloca ; ldloca ; callvirt PickFrom
            //   ldarg.0 ; ldfld catalystId ; bne.un
            // 用 ldnull + 两个 ldloca + callvirt 定位，比单认 ldfld 稳
            for (int i = code.Count - 1; i >= 1; i--)
            {
                if (code[i].opcode != OpCodes.Ldnull) continue;
                if (i + 3 >= code.Count) continue;
                if (!IsLdloca(code[i + 1]) || !IsLdloca(code[i + 2])) continue;
                if (code[i + 3].opcode != OpCodes.Callvirt) continue;
                if (!(code[i + 3].operand is MethodInfo pick) || pick.Name != "PickFrom") continue;

                // filter：ldnull 前一条就是 `ldfld catalystId`
                if (code[i - 1].opcode != OpCodes.Ldfld || !ReferenceEquals(code[i - 1].operand, cataField))
                    continue;

                int cmp = i + 4; // 取货之后：ldarg.0 ; ldfld catalystId ; bne.un

                if (cmp + 1 >= code.Count) continue;
                if (code[cmp].opcode != OpCodes.Ldarg_0) continue;
                if (code[cmp + 1].opcode != OpCodes.Ldfld) continue;
                if (!ReferenceEquals(code[cmp + 1].operand, cataField)) continue;

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
                    + "少改的那个口喂不进活性透镜，玩家会看到「有的接收站吃、有的不吃」");

            return code;
        }

        // ════════════════════════════════════════════════════════════════════
        //  F. 快速填入（背包 shift 点击建筑）
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>PlanetFactory.EntityFastFillIn</c> 里唯一一处 <c>ldfld catalystId</c>（实测 @1223）。
        /// 前面的 <c>ldelema PowerGeneratorComponent</c> 已经把元素地址压上来了，
        /// 所以把 <c>ldfld</c> 原地换成 <c>call</c> 就是等价的，栈形状一模一样。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.EntityFastFillIn))]
        private static IEnumerable<CodeInstruction> FastFillTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            MethodInfo helper = AccessTools.Method(typeof(LensPatches), nameof(FastFillCatalyst));

            FieldInfo cataField = AccessTools.Field(typeof(PowerGeneratorComponent),
                nameof(PowerGeneratorComponent.catalystId));

            if (helper == null || cataField == null)
            {
                ProjectEdenPlugin.Log.LogError("活性透镜：解析不到 FastFillCatalyst 或 catalystId，快速填入不改");

                return code;
            }

            var hits = 0;

            for (var i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldfld) continue;
                if (!ReferenceEquals(code[i].operand, cataField)) continue;

                code[i].opcode = OpCodes.Call;
                code[i].operand = helper;

                hits++;
            }

            Hits["EntityFastFillIn"] = hits;

            if (hits != 1)
                ProjectEdenPlugin.Log.LogError(
                    $"活性透镜：EntityFastFillIn 里应当正好有 1 处 catalystId，实际 {hits} 处。"
                    + "**这条不生效的症状就是「活性透镜放不进射线接收站」**——shift 点击会照着"
                    + "旧的 catalystId 去背包里拿引力透镜，而且什么提示都没有");

            return code;
        }

        /// <summary>把各处改写数汇总成一行，开局日志里一眼能核。</summary>
        private static void Report()
        {
            var want = 0;
            var got = 0;

            var where = new List<string>();

            foreach (KeyValuePair<string, int> kv in Hits)
            {
                got += kv.Value;
                where.Add($"{kv.Key} {kv.Value}");
            }

            where.Sort();

            // 三处 cata + 一处 productHeat + 两个取货口 + 一处快速填入
            want = CataMethods.Length + 1 + 2 + 1;

            string detail = string.Join("、", where.ToArray());

            if (got == want && Hits.Count == CataMethods.Length + 3)
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
