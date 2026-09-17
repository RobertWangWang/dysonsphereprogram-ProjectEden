using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using CommonAPI;
using CommonAPI.Systems;
using crecheng.DSPModSave;
using HarmonyLib;
using ProjectEden.Compatibility;
using ProjectEden.Patches;
using ProjectEden.Utils;
using xiaoye97;

namespace ProjectEden
{
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInProcess("DSPGAME.exe")]
    [BepInDependency(LDBToolPlugin.MODGUID)]
    [BepInDependency(DSPModSavePlugin.MODGUID)]
    [BepInDependency(CommonAPIPlugin.GUID)]
    [CommonAPISubmoduleDependency(nameof(TabSystem))]
    [BepInDependency(GenesisBookCompat.MODGUID, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(GalacticScaleCompat.MODGUID, BepInDependency.DependencyFlags.SoftDependency)]
    // **软依赖在这里不是「可选的适配」，是加载顺序。** 没有它，BepInEx 可能先加载本 mod，
    // 那时 Chainloader.PluginInfos 里还没有 UXAssist，兼容层会认定「没装」而整个跳过——
    // 实测就是这样：日志里「UXAssist 没装」那一行紧接着就是「Loading [UXAssist]」。
    [BepInDependency(UXAssistCompat.Guid, BepInDependency.DependencyFlags.SoftDependency)]
    public class ProjectEdenPlugin : BaseUnityPlugin, IModCanSave
    {
        public const string GUID    = "com.wangyu.projecteden";
        public const string NAME    = "Project Eden";
        public const string VERSION = "1.9.6";

        /// <summary>存档格式版本。改动 Export/Import 的字节布局时必须递增。</summary>
        private const int SaveVersion = 5;

        internal static ManualLogSource Log;

        /// <summary>大型采矿机改造的配置，见 data/advancedminer.json</summary>
        internal static AdvancedMinerConfig MinerConfig;

        /// <summary>物流站储物格容量的配置，见 data/stations.json</summary>
        internal static Patches.StationsConfig StationsConfig;

        /// <summary>着色器 inc 探针的开关，见 data/cargoprobe.json（单独一个文件的理由写在那里面）</summary>
        internal static Patches.CargoProbeConfig CargoProbeConfig;

        /// <summary>矩阵研究站的配置，见 data/lab.json</summary>
        internal static Patches.LabConfig LabConfig;

        /// <summary>电力节点供电范围的配置，见 data/power.json</summary>
        internal static Patches.PowerConfig PowerConfig;

        /// <summary>传送带速度（belts.json）</summary>
        internal static Patches.BeltsConfig BeltsConfig;

        /// <summary>金属的四维属性（metals.json）</summary>
        internal static Patches.MetalsConfig MetalsConfig;

        /// <summary>合金的逐建筑配比（alloys.json）</summary>
        internal static Patches.AlloysConfig AlloysConfig;

        /// <summary>作弊类开关（cheats.json）。**默认全开**，开着的每条都会在日志里留一行</summary>
        internal static Patches.CheatsConfig CheatsConfig;

        /// <summary>
        /// 屏蔽「数据异常」判定（abnormality.json）。**默认开**。
        /// 单独一个文件的理由写在那份 JSON 和 <see cref="Patches.AbnormalityConfig"/> 里。
        /// </summary>
        internal static Patches.AbnormalityConfig AbnormalityConfig;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            CompatibilityRegistry.Init();

            MegaBuildingRegistry.Load();
            ExtraRecipeRegistry.Load();
            OreRegistry.Load();
            MachineRegistry.Load();
            MinerConfig = JsonHelper.Load<AdvancedMinerConfig>("advancedminer");
            StationsConfig = JsonHelper.Load<Patches.StationsConfig>("stations");
            LabConfig = JsonHelper.Load<Patches.LabConfig>("lab");
            PowerConfig = JsonHelper.Load<Patches.PowerConfig>("power");
            BeltsConfig = JsonHelper.Load<Patches.BeltsConfig>("belts");
            MetalsConfig = JsonHelper.Load<Patches.MetalsConfig>("metals");
            AlloysConfig = JsonHelper.Load<Patches.AlloysConfig>("alloys");
            CheatsConfig = JsonHelper.Load<Patches.CheatsConfig>("cheats");
            AbnormalityConfig = JsonHelper.Load<Patches.AbnormalityConfig>("abnormality");
            CargoProbeConfig = JsonHelper.Load<Patches.CargoProbeConfig>("cargoprobe");
            Patches.CatalystBedPatches.Config = JsonHelper.Load<Patches.CatalystConfig>("catalyst");
            Patches.LensPatches.Config = JsonHelper.Load<Patches.LensConfig>("lens");
            AmmoRegistry.Load();
            RedoxRegistry.Load();
            CompositeRegistry.Load();
            CombustiblePowerPatches.Load();
            ProliferatorPatches.Load();
            AlienVeinPatches.Load();

            // 英文本地化：表要在任何 proto 注册之前载好，注册本身挂在
            // Localization.LoadSettings 上（那时 namesIndexer 才有内容，防撞检查才做得了）
            I18N.Load();

            ReportCheats();
            ReportAbnormality();
            ReportCargoProbe();
            Patches.CargoWidening.Report();
            Patches.QualityWidening.Report();
            Patches.QualitySourcePatches.Report();
            // 放在 PatchAll 之后才知道改写了几处，所以这一行挪到下面去打

            // 分页要在游戏建立建造栏之前注册，LDBTool 的回调里已经太晚
            MegaBuildingRegistry.RegisterTab();

            _harmony = new Harmony(GUID);
            _harmony.PatchAll(typeof(ProjectEdenPlugin).Assembly);
            CompatibilityRegistry.ApplyPatches(_harmony);

            // 要等转译器跑完才数得出改写了几处
            Patches.RecipeTypeCompatPatches.Report();
            // 读的是 Harmony 自己的补丁表，所以必须在 PatchAll 之后
            Patches.QualityCraftPatches.Report();
            Patches.QualityRepairPatches.Report();
            Patches.QualityCraftFlowPatches.Report();
            Patches.QualityCraftOut.Report();
            Patches.QualityFeedPatches.Report();
            Patches.QualityHandUsePatches.Report();
            Patches.QualityBuildPatches.Report();
            Patches.QualityBuildRefundPatches.Report();
            Patches.QualitySaveCensusPatches.Report();

            LDBTool.PreAddDataAction += MegaBuildingRegistry.OnPreAddData;
            // **燃料白名单的撑长不在这条链上，而在使用点，这是量出来的结论。**
            // 曾经在这里排过一次（链首），实测无效：那一刻两座裂变电厂的 prefabDesc
            // 还没连上，扫出来的最大掩码是 32，于是照旧按 64 格放过。
            // 连接发生在 MegaBuildingRegistry.ProtoPreload() 里的 ItemProto.Preload，
            // 而 InitProductionMask（拿 fuelMask 当下标的那个）就是同一个方法紧接着调的
            // ——**「prefabDesc 都连好了」和「开始查表」之间没有任何时刻可以插进去。**
            // 所以 FuelNeedsCapacityPatches 那个使用点前置不是保险，是唯一正确的位置。
            LDBTool.PostAddDataAction += MegaBuildingRegistry.OnPostAddData;

            // 自定义矿脉排在巨型建筑之后：那边的 PostAddData 会重跑 ProtoPreload，
            // 图标改色必须在 Preload 之后做，否则会被 Preload 用原图覆盖回去
            LDBTool.PreAddDataAction += OreRegistry.OnPreAddData;
            LDBTool.PostAddDataAction += OreRegistry.OnPostAddData;

            // 钻头：排在 OreRegistry 之后——它要按 metals.json 的四维展开配方，
            // 而那里的 ref 得等矿石和锭拿到 ID 才解析得出来
            LDBTool.PreAddDataAction += DrillBitRegistry.OnPreAddData;

            // 新生产设备同理：图标也是改色出来的，得排在 ProtoPreload 之后
            LDBTool.PreAddDataAction += MachineRegistry.OnPreAddData;
            LDBTool.PostAddDataAction += MachineRegistry.OnPostAddData;

            // 巨型建筑的能量枢纽段：**必须排在 MachineRegistry 之后**。
            // 它要的空/满蓄电器物品号是那边注册的，而巨型建筑本身注册在机器之前——
            // 早一步写进去的是 0，后果是「枢纽建好了、皮带接上了、一个柜子也不收」，
            // 而且一声不吭
            LDBTool.PostAddDataAction += MegaBuildingRegistry.ApplyExchangers;

            // 小型速采机的表要在 MachineRegistry 之后建：它读的是那一遍解析出来的 ItemId
            LDBTool.PostAddDataAction += Patches.MiniMinerPatches.OnPostAddData;

            // 传送带提速：要等 LDB 建好才拿得到带子的 ModelProto
            LDBTool.PostAddDataAction += BeltSpeedPatches.OnPostAddData;

            // 金属属性：排在矿种注册之后，才解析得出自家金属的 ref
            LDBTool.PostAddDataAction += MetalPropertyPatches.OnPostAddData;

            // 合金配比：要等矿种注册完才解析得出各味料和牌号的 ref
            LDBTool.PostAddDataAction += AlloyRatioPatches.OnPostAddData;

            // 合金弹药：PreAdd 里注册物品与配方，PostAdd 里解析可用合金
            // （要等合金本身进了 LDB 才拿得到它们的物品 ID）
            LDBTool.PreAddDataAction += AmmoRegistry.OnPreAddData;
            LDBTool.PostAddDataAction += AmmoRegistry.OnPostAddData;
            LDBTool.PreAddDataAction += RedoxRegistry.OnPreAddData;
            LDBTool.PostAddDataAction += RedoxRegistry.OnPostAddData;

            // 提纯注入表要在 LDBTool 敲定配方 ID 之后建——ores.json 里写的那个 ID
            // 可能被 CustomID.cfg 顶掉，按配置里的数建表会查不到任何东西。
            LDBTool.PostAddDataAction += Patches.QualityRefineryPatches.Build;
            // 复合材只解析不注册（物品和配方都在 ores.json 里），所以只挂 PostAdd
            LDBTool.PostAddDataAction += CompositeRegistry.OnPostAddData;
            // 活性增产剂：排在复合材之后——投料就是那四级，
            // 而两个分数读的是 metals.json 的四维，两边都得先就绪
            LDBTool.PostAddDataAction += ProliferatorPatches.OnPostAddData;
            // 外星矿脉：排在金属属性之后——钻头谓词读的就是四维
            LDBTool.PostAddDataAction += AlienVeinPatches.OnPostAddData;

            // 抽岩浆：排在矿种注册之后——要按 key 解析出岩浆的物品号，
            // 还要给抽水类设备的 prefabDesc.waterTypes 添上熔岩
            LDBTool.PostAddDataAction += LavaPumpPatches.OnPostAddData;

            // 催化剂床：要在 OreRegistry 之后，它按 ref 名解析催化剂与待生催化剂的物品号
            LDBTool.PostAddDataAction += CatalystBedPatches.OnPostAddData;

            // 可燃液体发电：排在金属属性之后——它要读 MetalPropertyPatches.FieldsEnd
            // 来避开已被占用的属性行字段号，而那个值只有注册跑完才是准的。
            // 也要排在矿种与机器注册之后，才解析得出液体和电厂
            LDBTool.PostAddDataAction += CombustiblePowerPatches.OnPostAddData;

            // 活性透镜：排在矿种注册之后——它要按 key 反查透镜的物品 ID，
            // 而那条物品是 OreRegistry 的 items 段注册的
            LDBTool.PostAddDataAction += LensPatches.OnPostAddData;

            // 排在最后：要等所有注册器都把物品塞进 LDB 之后，才重建流体白名单
            LDBTool.PostAddDataAction += RefreshFluidList;

            // 炮塔弹药白名单是同一族的另一张预加载期静态表，LDBTool 同样没有替我们重跑
            LDBTool.PostAddDataAction += RefreshTurretNeeds;

            // 燃料白名单的**核对**：撑长和重建已经在链首做过了（见那里的注释），
            // 这里再跑一遍是为了核对末态——那时候所有注册器都已经把物品塞进 LDB。
            // 理由同 RefreshFluidList：验的是结果，不是自己那一步
            LDBTool.PostAddDataAction += RefreshFuelNeeds;

            // 增产剂普查：纯诊断，不改任何东西。Ability / HpMax / incItemId 都在
            // resources.assets 里，离线读不到；等级上限还要按集装层数现算
            LDBTool.PostAddDataAction += ProliferatorSurvey.OnPostAddData;

            // 采矿机物流站普查：外星矿脉要给它加一个钻头槽，
            // 而 stationMaxItemKinds 在 prefab 里，离线读不到
            LDBTool.PostAddDataAction += MinerStationSurvey.OnPostAddData;

            // 漏译核对也排在最后：要等所有 proto 都进了 LDB 才数得清
            // 第七种矩阵：排在矿种注册之后（要按 key 解析物品号），
            // 也要排在 LDB.techs 建好之后才追加得了科技
            LDBTool.PostAddDataAction += BioMatrixPatches.OnPostAddData;
            // 必须排在生物矩阵之后：它要读 BioMatrixPatches.MatrixId
            LDBTool.PostAddDataAction += UniverseMatrixPatches.OnPostAddData;

            // recipes.json 的 vanillaEdits：就地给原版配方加原料。
            // 排在这里有两个理由——要等本 mod 的物品都进了 LDB（ref 才解析得出来），
            // 又要赶在 EnergyAudit 之前（审计读的得是改完的配方）。
            // 再往后 LDBTool 自己会调 InitRecipeItems 把改动吸收成 RecipeExecuteData。
            LDBTool.PostAddDataAction += ExtraRecipeRegistry.OnPostAddData;

            // 燃料普查：纯诊断，不改任何东西。FuelType 位、发电建筑的 prefab 参数、
            // 原版的热值与 ReactorInc 都在 resources.assets 里，离线读不到。
            //
            // **它必须排在 vanillaEdits 后面**，而它原本排在前面，实测打出了一行陈旧的账：
            // 燃料阶梯报「氢燃料棒 ← 钛块×1 + 氢×10」，而当局真正生效的是 ×56。
            // 那张表存在的全部意义就是「这条燃料到底要花多少料」，报中间态等于没报——
            // 和 VeinProtoArrayPatches 那条是同一条规矩：**验末态，不验自己那一步**。
            //
            // 只挪了它一个：另外两个普查读的是 proto 字段和 prefab，
            // 后面没有任何一步会改，跟着一起挪就成了照搬。
            LDBTool.PostAddDataAction += FuelSurvey.OnPostAddData;

            // 矩阵配方时间：**必须排在 BioMatrixPatches 之后**（它按 LabComponent.matrixIds 扫，
            // 那张表是 BioMatrixPatches 接长的），而且**必须在 PostAddDataAction 里**——
            // LDBTool 在这个动作之后才调 RecipeProto.InitRecipeItems，
            // timeSpend/extraTimeSpend 正是在那里从 TimeSpend 算出来的，所以改是白捡的。
            LDBTool.PostAddDataAction += MatrixLabPatches.ApplyMatrixTime;

            // 矩阵生产时间：同样是「原版数值只能在运行时读」，而且**必须排在这里**——
            // 它按 LabComponent.matrixIds 遍历，那张表是 BioMatrixPatches 接长的；
            // 而它要打的宇宙矩阵配方，第七种原料是 UniverseMatrixPatches 加的。
            // 排在两者之前就会报一张「六种矩阵、宇宙矩阵六种原料」的真事实、假末态。
            LDBTool.PostAddDataAction += MatrixSurvey.OnPostAddData;

            // 模型号余量：一种会悄悄用完的资源，用完的症状是「建筑没有模型」而不是报错
            LDBTool.PostAddDataAction += ProtoSlots.ReportModelBudget;

            // 星体矿脉的状态行**必须排在这里，不能跟着 PatchAll 走**。
            // 它要同时报两件事：转译改写了几份（PatchAll 时就定了）、配了几种矿
            // （PreAddDataAction 才填）。第一版放在 Awake 里，于是每局都打
            // 「一条都没配置」——**在它要测量的东西存在之前就测量了**，
            // 真事实、假结论，而且把那条「5 份是否全部命中」的断言一起吞掉了
            LDBTool.PostAddDataAction += Patches.StarVeinPatches.Report;
            LDBTool.PostAddDataAction += Patches.VeinMiningGlowPatches.Report;
            LDBTool.PostAddDataAction += Patches.ModelRenderCensus.Report;
            LDBTool.PostAddDataAction += Patches.StackedRenderPatches.Report;

            LDBTool.PostAddDataAction += I18N.VerifyCoverage;
            // 能量审计排在最后：它要读 LDB 里的最终热值，
            // 而原版热值改写、物品注册都得先完成
            LDBTool.PostAddDataAction += EnergyAudit.Run;
            LDBTool.PostAddDataAction += ProtoArrayCheck.Verify;

            Logger.LogInfo($"{NAME} v{VERSION} 已加载");
        }

        // 支持 ScriptEngine 热重载
        private void OnDestroy()
        {
            LDBTool.PreAddDataAction -= MegaBuildingRegistry.OnPreAddData;
            LDBTool.PostAddDataAction -= MegaBuildingRegistry.OnPostAddData;
            LDBTool.PreAddDataAction -= OreRegistry.OnPreAddData;
            LDBTool.PostAddDataAction -= OreRegistry.OnPostAddData;
            LDBTool.PreAddDataAction -= MachineRegistry.OnPreAddData;
            LDBTool.PostAddDataAction -= MachineRegistry.OnPostAddData;
            LDBTool.PostAddDataAction -= Patches.MiniMinerPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= BeltSpeedPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= MetalPropertyPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= AlloyRatioPatches.OnPostAddData;
            LDBTool.PreAddDataAction -= AmmoRegistry.OnPreAddData;
            LDBTool.PostAddDataAction -= AmmoRegistry.OnPostAddData;
            LDBTool.PreAddDataAction -= RedoxRegistry.OnPreAddData;
            LDBTool.PostAddDataAction -= RedoxRegistry.OnPostAddData;
            LDBTool.PostAddDataAction -= CompositeRegistry.OnPostAddData;
            LDBTool.PostAddDataAction -= ProliferatorPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= AlienVeinPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= LavaPumpPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= CatalystBedPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= CombustiblePowerPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= RefreshFluidList;
            LDBTool.PostAddDataAction -= RefreshTurretNeeds;
            LDBTool.PostAddDataAction -= FuelSurvey.OnPostAddData;
            LDBTool.PostAddDataAction -= MatrixLabPatches.ApplyMatrixTime;
            LDBTool.PostAddDataAction -= MatrixSurvey.OnPostAddData;
            LDBTool.PostAddDataAction -= ProliferatorSurvey.OnPostAddData;
            LDBTool.PostAddDataAction -= MinerStationSurvey.OnPostAddData;
            LDBTool.PostAddDataAction -= BioMatrixPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= UniverseMatrixPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= ExtraRecipeRegistry.OnPostAddData;
            LDBTool.PostAddDataAction -= I18N.VerifyCoverage;
            LDBTool.PostAddDataAction -= ProtoArrayCheck.Verify;

            _harmony?.UnpatchSelf();
            Logger.LogInfo($"{NAME} 已卸载");
        }

        /// <summary>
        /// 把作弊开关的状态报一遍。<b>无论开没开都要报。</b>
        ///
        /// 本仓库的排查方法就是读 LogOutput.log，而作弊项改的是建造判定、碰撞、材料结算
        /// 这些底层行为——半年后再看一份日志时，「无碰撞开着」和「建筑能重叠是个 bug」
        /// 长得一模一样。
        ///
        /// <b>第一版只在「有开关打开」时才打，这是错的，而且立刻就还了债。</b>
        /// 玩家报「作弊功能失败了」，日志里一行作弊相关的记录都没有——
        /// 那到底是「开关全关」还是「这段代码压根没进 DLL」？一条静默的诊断分不出这两件事，
        /// 白白多花一轮。这正是 CLAUDE.md 里记着的那条：
        /// <b>计数为零时就沉默的诊断，区分不了「没事可做」和「根本没跑」。</b>
        /// </summary>
        private static void ReportCheats()
        {
            Patches.CheatsConfig cheats = CheatsConfig;

            string where = Utils.JsonHelper.OverridePath("cheats");

            if (cheats == null)
            {
                Log.LogError("作弊开关：cheats.json 没读出来");

                return;
            }

            if (!cheats.enabled)
            {
                Log.LogInfo($"作弊开关：总开关 enabled=false，六项全部无效。改这里：{where}");

                return;
            }

            var on = new List<string>();

            if (cheats.instantBuild) on.Add($"建造秒完成（每次上限 {(cheats.instantBuildPerTick > 0 ? cheats.instantBuildPerTick : 100)}，材料照扣）");
            if (cheats.noConditionBuild) on.Add("无条件建造（采矿机除外）");
            if (cheats.noCollision) on.Add("无碰撞建造（建筑可重叠；不影响传送带连接和机甲碰撞）");
            if (cheats.noCollisionPhysics)
                on.Add("关闭碰撞体对象池（机甲穿墙；**会导致传送带接不到已有传送带上**，重叠建造并不需要它）");
            if (cheats.powerNoSpacing) on.Add("发电建筑无间距限制");
            if (cheats.waterPumpAnywhere) on.Add("平地抽水");

            if (on.Count == 0)
            {
                Log.LogInfo($"作弊开关：六项全部关闭。本 mod 的默认是**全开**，所以走到这一行说明它们是被显式关掉的"
                                + $"（多半是 {where} 这份覆盖文件）。改完重开游戏，不用重新编译。");

                return;
            }

            Log.LogWarning($"以下作弊项已开启：{string.Join("、", on.ToArray())}。改这里：{where}");
        }

        /// <summary>
        /// 报告「数据异常屏蔽」的状态。<b>关着也要打一行</b>，同 <see cref="ReportCheats"/>。
        ///
        /// 这一条比别的更需要这行日志：它开着的时候<b>玩家再也看不到游戏自己的那条警告</b>，
        /// 关着的时候成就和元数据全程是灰的、也没有任何东西说明是谁干的。
        /// 两种状态都得能在日志里一眼认出来。
        /// </summary>
        private static void ReportAbnormality()
        {
            string where = Utils.JsonHelper.OverridePath("abnormality");

            if (AbnormalityConfig == null)
            {
                Log.LogError("数据异常屏蔽：abnormality.json 没读出来");

                return;
            }

            if (!AbnormalityConfig.enabled)
            {
                Log.LogInfo(
                    $"数据异常屏蔽：关闭。本 mod 的默认是**开**，所以走到这一行说明它是被显式关掉的（多半是 {where}）。" +
                    "关着的后果：装了本 mod 的存档每次读档和存档都会被 ABN_ProtoData 记三笔"
                        + "（物品/配方/矿脉三张表的签名都变了），**成就和元数据会一直是关着的**。");

                return;
            }

            Log.LogWarning(
                $"数据异常屏蔽：已开启（默认值）。改这里：{where}。" +
                "拦掉 TriggerAbnormality 并让 NothingAbnormal 恒为真——成就、元数据和那几行警告都会恢复正常，" +
                "**存档里已有的异常记录一个字节都不动**，关掉开关就全都回来。" +
                "注意开着它成就会真的解锁并进 Steam；只想本地解锁不上传的话得另外拦 SteamAchievementManager，这里没做。");
        }

        /// <summary>
        /// 报告着色器 inc 探针的状态。<b>关着也要打一行。</b>
        ///
        /// 这条规矩这个仓库已经付过两次学费（<c>AlloyRatioPatches.ReapplyAll</c>、<c>ReportCheats</c>），
        /// 而这个探针第一版又犯了一次：开关默认关、关着就完全沉默，于是日志里一行探针记录都没有——
        /// 「开关没打开」和「这段代码压根没进 DLL」长得一模一样，白白多花一轮。
        /// <b>计数为零时就沉默的诊断，区分不了「没事可做」和「根本没跑」。</b>
        /// </summary>
        private static void ReportCargoProbe()
        {
            string where = Utils.JsonHelper.OverridePath("cargoprobe");

            if (CargoProbeConfig == null)
            {
                Log.LogError("着色器 inc 探针：cargoprobe.json 没读出来");

                return;
            }

            if (!CargoProbeConfig.enabled)
            {
                Log.LogInfo(
                    $"着色器 inc 探针：关闭。要打开改这里：{where}" +
                    "（不存在就新建，把 enabled 改成 true，重开游戏，不用重新编译）");

                return;
            }

            Log.LogWarning(
                "着色器 inc 探针：已开启。**什么键都不用按**——走到一条有货的传送带前、让货物占住一部分屏幕即可，" +
                "它每 5 秒自己试一次，成功一次就不再跑。别用暂停：暂停时游戏根本不画传送带货物。" +
                $"用完把 {where} 删掉即可关闭。");
        }

        /// <summary>
        /// 重建原版的流体白名单 <c>ItemProto.fluids</c>，让本 mod 的气体/液体进得了储液罐。
        ///
        /// <b>为什么光设 IsFluid = true 不够：时序。</b> 原版在 <c>VFPreload.PreloadThread</c>
        /// 里调 <c>ItemProto.InitFluids()</c>（IL 偏移 0x08B0），扫 <c>LDB.items.dataArray</c>
        /// 把所有 <c>IsFluid</c> 的 ID 收成一张静态表；而 LDBTool 挂的是
        /// <c>VFPreload.InvokeOnLoadWorkEnded</c>（同一个方法的 0x0E11，最后一步）才把
        /// mod 的 proto 塞进 LDB。<b>白名单比 mod 物品先建好</b>，新流体一个都不在里面。
        ///
        /// 卡死在哪：储液罐空罐时，四条皮带的取货口都是
        /// <c>TryPickItemAtRear(beltN, 0, ItemProto.fluids, …)</c>——那张表是<b>直接当过滤数组</b>
        /// 传进去的。不在表里就捡不起来，捡不起来就永远拿不到 <c>fluidId</c>，于是空罐一直是空罐。
        /// 手动放入走 <c>PlanetFactory.EntityFastFillIn</c> → <c>ItemProto.isFluid(id)</c>，
        /// 读的是同一张表，一样被拦。这两处是仅有的两道关卡（<c>ItemProto</c> 上没有流体颜色
        /// 之类的字段，罐体外观是通用的）。
        ///
        /// 修法：LDB 建好之后把原版这个方法原样再跑一遍。它从 dataArray 整表重建，
        /// <b>幂等且不丢原版流体</b>，不用 transpiler。所有读取方都是每次现取
        /// <c>ldsfld ItemProto::fluids</c>，所以换掉静态字段就能被立刻看到。
        /// </summary>
        /// <summary>
        /// 重建原版的炮塔弹药白名单 <c>ItemProto.turretNeeds</c>，让本 mod 的弹药进得了炮塔。
        ///
        /// <b>和流体白名单是同一个坑，连位置都挨着。</b> <c>ItemProto.InitTurretNeeds</c> 的
        /// 唯一调用点是 <c>VFPreload.PreloadThread</c> 的 <b>IL 0x08AB</b>，而
        /// <c>InitFluids</c> 在 0x08B0——两张表在预加载线程里前后脚建起来，
        /// 都早于 LDBTool 在 0x0E11 把 mod 物品塞进物品表。
        ///
        /// <b>LDBTool 重跑了 InitFuelNeeds，唯独没重跑这两个</b>（它的
        /// <c>VFPreloadPostPatch</c> 里只有 InitFuelNeeds / InitConstructableItems /
        /// InitItemIds / InitItemIndices / InitRecipeItems / InitSignalKeyIdPairs / IconSet.Create）。
        ///
        /// 不补的后果是<b>静默的</b>：弹药物品注册得好好的、AmmoType 也对，但
        /// <c>TurretComponent.BeltUpdate</c> 是把整张 <c>turretNeeds[类型]</c>
        /// <b>当过滤数组</b>传进 <c>TryPickItem</c> 的——不在表里就永远捡不起来，
        /// 手动塞进炮塔也会被同一张表拦住，而且不报任何错。
        ///
        /// <c>InitTurretNeeds</c> 自己是从 <c>dataArray</c> 整表重建的，幂等、不丢原版弹药，
        /// 而且 <c>turretNeeds</c> 是 <c>new int[16][]</c>、弹药类型最大才 6，不需要扩容。
        /// </summary>
        /// <summary>
        /// 把 <c>ItemProto.fuelNeeds</c> 撑到够用的长度，再让原版自己重建它。
        ///
        /// <b>这解锁了 bit 6 以上的燃料位。</b> 本文件长期记着「合法位只有 bit 0~5」，
        /// 而那个上限的**唯一**来源是 <c>ItemProto..cctor</c> IL 0017 的
        /// <c>ldc.i4.s 64 ; newarr</c> —— 一个写死的初始长度，不是任何类型宽度。
        /// 读 IL 量出来的三件事：
        ///
        /// <list type="number">
        /// <item><c>InitFuelNeeds</c> 的外层循环边界是 <c>fuelNeeds.Length</c>
        /// （IL 005F–0066 的 <c>ldsfld ; ldlen ; blt</c>），**动态读的**。所以数组变长，
        /// 它就多填几格，填法还是 <c>mask &amp; proto.FuelType</c>，语义不变。</item>
        /// <item>全部 8 个读者无一例外是 <c>fuelNeeds[fuelMask] ; ldelem.ref</c>，
        /// 没有一处做边界算术、没有一处遍历整张表。</item>
        /// <item>整个程序集里，燃料掩码附近**没有任何** 63/64 的比较——不存在上限判定。
        /// （<c>InitProductionMask</c> IL 029A 那个 64 是 <c>consumptionMask</c> 的位，
        /// 和燃料位无关，是「同一个常数两种含义」的典型，别被它骗了。）</item>
        /// </list>
        ///
        /// <b>真正的上限是 <c>PowerGeneratorComponent.fuelMask</c> 的宽度：Int16</b>
        /// （<c>PrefabDesc.fuelMask</c> 是 Int32，抄过去会收窄）。所以可用到 bit 14，
        /// 再往上碰符号位。
        ///
        /// 做法照抄 <see cref="RefreshFluidList"/>：<c>InitFuelNeeds</c> 整表重建、幂等，
        /// 所以换一个更长的数组再跑一次就行，没有转译器。
        /// </summary>
        private static void RefreshFuelNeeds()
        {
            // **这一层 try 不是装饰。** 实测：本方法第一版在这里静默失败，
            // 于是 PostAddDataAction 里排在它后面的**每一个**处理器都没跑
            // ——增产剂普查、宇宙矩阵、vanillaEdits、燃料阶梯、英文核对、能量审计，
            // 全部消失，而 BepInEx 日志和 Player.log 里一条异常都没有。
            // 症状是「日志莫名短了一截」，指不到任何地方。
            // 和「PatchAll 抛异常 = Awake 后面全死」是同一族，只是更隐蔽：那边至少有栈。
            try
            {
                // 起止各一行：**抛异常**（起行有、止行无、错误行有）、**卡住**（起行有、别的都没有）、
                // **这段代码没进 DLL**（起行都没有）——三种在日志里必须分得开。
                // 上一局三者长得一模一样，白费了一次启动
                Log.LogInfo("燃料白名单：开始重建");
                RefreshFuelNeedsCore();
                Log.LogInfo("燃料白名单：重建完成");
            }
            catch (Exception e)
            {
                Log.LogError(
                    $"燃料白名单重建失败：{e}。**本条已被隔离**，后面的注册步骤照常跑——"
                    + "但本 mod 的燃料可能喂不进对应的发电厂");
            }
        }

        /// <summary>
        /// 把 <c>fuelNeeds</c> 撑到装得下最宽的那座电厂的掩码，并让原版重新填满它。
        ///
        /// <b>它必须能在任意时刻被调用，因为它的使用点比它早。</b> 实测崩溃：
        /// <c>ItemProto.InitProductionMask</c> IL 0265–0271 是
        /// <c>fuelNeeds[desc.fuelMask] ; ldelem.ref</c> —— 拿掩码当下标。
        /// 而它由 <c>MegaBuildingRegistry.OnPostAddData</c> 调用，那一条**排在整条
        /// PostAddDataAction 的第一个**，比这里原本的注册位置早得多。
        /// 新电厂的掩码是 64 / 128，数组却还是 64 格，于是
        /// <c>IndexOutOfRangeException</c>，而报错里只出现 LDBTool 和本 mod 的名字。
        ///
        /// 这正是本仓库为矿种数组写过的那条：**排在使用点之前的准备步骤可以被跳过、
        /// 重排或抢先，使用点不会。** 所以这段既在链首跑一次，也由
        /// <c>FuelNeedsCapacityPatches</c> 在使用点再兜一次。
        ///
        /// <b>长度不够和格子是 null 都会炸，所以撑长之后必须立刻填。</b>
        /// <c>InitProductionMask</c> 拿到 null 之后下一步就是 <c>ldlen</c>。
        /// </summary>
        internal static bool EnsureFuelNeedsCapacity()
        {
            const int VanillaLength = 64;
            const int MaskCeiling = short.MaxValue; // PowerGeneratorComponent.fuelMask 是 Int16

            ItemProto[] items = LDB.items?.dataArray;

            if (items == null) return false;

            // 需要多长，由**发电机的掩码**决定——读者拿它当下标
            var maxMask = 0;
            var widest = "";

            foreach (ItemProto item in items)
            {
                PrefabDesc desc = item?.prefabDesc;

                if (desc == null || !desc.isPowerGen || desc.fuelMask <= maxMask) continue;

                maxMask = desc.fuelMask;
                widest = $"{item.Name}({item.ID})";
            }

            if (maxMask > MaskCeiling)
            {
                Log.LogError(
                    $"燃料位：{widest} 的 fuelMask = {maxMask} 超过 Int16 上限 {MaskCeiling}。"
                    + "PowerGeneratorComponent.fuelMask 是 Int16，抄过去会收窄成别的值，"
                    + "那座电厂会去查一张错的白名单。把它降到 bit 14 以内");

                return false;
            }

            int want = maxMask + 1;

            if (want < VanillaLength) want = VanillaLength;

            int[][] needs = ItemProto.fuelNeeds;
            int had = needs?.Length ?? 0;

            if (had >= want) return false;

            // 整表重建，所以不用搬旧内容——但**必须立刻重建**，空着的格子是 null
            ItemProto.fuelNeeds = new int[want][];
            ItemProto.InitFuelNeeds();

            Log.LogInfo(
                $"燃料位：把燃料白名单从 {had} 格撑到 {want} 格（最宽的电厂是 {widest}，掩码 {maxMask}）。"
                + "原版那个 64 只是 ItemProto..cctor 里写死的初始长度，"
                + "InitFuelNeeds 的循环边界读的是数组自身长度");

            return true;
        }

        private static void RefreshFuelNeedsCore()
        {
            ItemProto[] items = LDB.items?.dataArray;

            // 报无聊的那一面：静默返回和「这段代码没进 DLL」在日志里长得一模一样
            if (items == null)
            {
                Log.LogWarning("燃料白名单：LDB.items 还没建好，跳过");

                return;
            }

            // 撑长（需要的话）并重建。撑长那一步可能早在使用点的兜底里就做过了，
            // 这里再跑一次 InitFuelNeeds 是为了把本 mod 的物品也填进去——
            // 兜底那次可能发生在 LDB 还没齐的时候
            EnsureFuelNeedsCapacity();
            ItemProto.InitFuelNeeds();

            int[][] needs = ItemProto.fuelNeeds;

            if (needs == null)
            {
                Log.LogWarning("燃料白名单为空，本 mod 的燃料可能进不了发电厂");

                return;
            }

            // **核对末态，不核对自己那一步**：凡是某座电厂的掩码认得的燃料，
            // 都必须真的出现在那座电厂查的那张表里。只数「我加了几个」的话，
            // 别人先把同一件事做掉时会报假警——流体白名单那条已经栽过一次
            var broken = new List<string>();
            var reachable = 0;

            foreach (ItemProto item in items)
            {
                if (item == null || item.FuelType == 0) continue;

                foreach (ItemProto plant in items)
                {
                    PrefabDesc desc = plant?.prefabDesc;

                    if (desc == null || !desc.isPowerGen || desc.fuelMask == 0) continue;
                    if ((desc.fuelMask & item.FuelType) == 0) continue;

                    int[] list = desc.fuelMask < needs.Length ? needs[desc.fuelMask] : null;

                    if (list != null && Array.IndexOf(list, item.ID) >= 0)
                    {
                        reachable++;

                        continue;
                    }

                    broken.Add($"{item.Name}({item.ID}) 进不了 {plant.Name}(掩码 {desc.fuelMask})");
                }
            }

            if (broken.Count > 0)
                Log.LogError(
                    $"燃料白名单核对失败 {broken.Count} 项：{string.Join("、", broken.ToArray())}。"
                    + "这些燃料喂不进对应的发电厂，而且不会报错——只会一直烧不起来");
            else
                Log.LogInfo(
                    $"燃料白名单核对通过：{needs.Length} 格，{reachable} 组（燃料 × 电厂）配对可达");
        }

        private static void RefreshTurretNeeds()
        {
            ItemProto.InitTurretNeeds();

            int[][] needs = ItemProto.turretNeeds;

            if (needs == null)
            {
                Log.LogWarning("炮塔弹药白名单为空，本 mod 的弹药可能进不了炮塔");

                return;
            }

            // 同样核对**结果**而不是增量：只要有 AmmoType 的物品不在自己那一类的表里，就是坏的
            var missing = new List<string>();
            var mine = 0;

            foreach (ItemProto item in LDB.items.dataArray)
            {
                if (item == null || item.AmmoType == EAmmoType.None) continue;

                var type = (int)item.AmmoType;

                int[] list = type >= 0 && type < needs.Length ? needs[type] : null;

                if (list != null && Array.IndexOf(list, item.ID) >= 0)
                {
                    if (item.ID >= ModItemIdBase) mine++;

                    continue;
                }

                missing.Add($"{item.name}({item.ID}, {item.AmmoType})");
            }

            if (missing.Count > 0)
            {
                Log.LogWarning(
                    $"炮塔弹药白名单里缺了 {missing.Count} 种：{string.Join("、", missing.ToArray())}。" +
                    "它们进不了炮塔——检查 InitTurretNeeds 的调用时机，或该物品的 AmmoType 是否真的设上了");

                return;
            }

            var counts = new List<string>();

            for (var i = 1; i < needs.Length; i++)
                if (needs[i] != null && needs[i].Length > 0)
                    counts.Add($"{(EAmmoType)i} {needs[i].Length}");

            Log.LogInfo($"炮塔弹药白名单已核对：{string.Join("、", counts.ToArray())}，其中本 mod {mine} 种");
        }

        private static void RefreshFluidList()
        {
            ItemProto.InitFluids();

            int[] fluids = ItemProto.fluids ?? new int[0];

            // 核对的是**结果**，不是"这次新增了几个"。
            // CommonAPI 的 ProtoRegistry.OnPostAdd 同样会调 InitFluids，而且排在我们前面，
            // 所以"新增 0 个"是完全正常的——按增量判断会一直误报"功能坏了"。
            var missing = new List<string>();
            var mine = 0;

            foreach (ItemProto item in LDB.items.dataArray)
            {
                if (item == null || !item.IsFluid) continue;

                if (Array.IndexOf(fluids, item.ID) >= 0)
                {
                    if (item.ID >= ModItemIdBase) mine++;

                    continue;
                }

                missing.Add($"{item.name}({item.ID})");
            }

            if (missing.Count > 0)
            {
                Log.LogWarning(
                    $"流体白名单里缺了 {missing.Count} 种：{string.Join("、", missing.ToArray())}。" +
                    "它们进不了储液罐——检查 InitFluids 的调用时机，或该物品的 IsFluid 是否真的设上了");

                return;
            }

            Log.LogInfo($"流体白名单已核对：共 {fluids.Length} 种，其中本 mod {mine} 种，全部在表内（可进储液罐）");
        }

        /// <summary>本 mod 的物品 ID 都在 6500 以上，用来把自家流体和原版流体分开计数。</summary>
        private const int ModItemIdBase = 6500;

        // ── 存档 ──────────────────────────────────────────────
        // 字节流是位置相关的：新增持久化功能时，Export / Import / IntoOtherSave
        // 三处都要按同样顺序加，否则读档会错位。

        public void Export(BinaryWriter w)
        {
            w.Write(SaveVersion);
            SlotDataStore.Export(w);
            AlloyRatioStore.Export(w);
            CatalystBedStore.Export(w);
            QualityBuildStore.Export(w);
        }

        public void Import(BinaryReader r)
        {
            int version = r.ReadInt32();

            if (version > SaveVersion)
            {
                Log.LogWarning($"存档里的 ProjectEden 数据版本为 {version}，高于当前支持的 {SaveVersion}，已跳过读取");
                SlotDataStore.Clear();

                return;
            }

            SlotDataStore.Import(r);

            // 版本 2 才有配比这一块；读版本 1 的老档时流到这里就结束了，不能再读。
            // 版本 2 和 3 的布局不同（2 是每台一个 int，3 是每台一组份数），由 Store 自己迁移
            if (version >= 2) AlloyRatioStore.Import(r, version);
            else AlloyRatioStore.Clear();

            // 催化剂床是版本 4 才追加的一块。读更老的档时流到这里就结束了，**不能再读**——
            // 这个字节流是位置相关的，多读一个 int 就会把后面全部错位。
            if (version >= 4) CatalystBedStore.Import(r);

            // 版本 5 起：每座建筑是用什么品质的材料造的（效果层的唯一输入）
            if (version >= 5) QualityBuildStore.Import(r);
            else CatalystBedStore.Clear();

            // **必须在这里再贴一次。** AlloyRatioPatches 挂在 GameData.Import 上的那个后置
            // 跑在本方法之前，那时这个 store 还是空的——只靠它的话，存档里的配比
            // 会在读档时被静默丢掉。到了这里配方数据已经恢复完，正是补贴的时机。
            AlloyRatioPatches.ReapplyAll("存档块读回后");
            AmmoPairPatches.ReapplyAll();
            CompositePatches.ReapplyAll();
            CompositeOutputPatches.ReapplyAll();
            ProliferatorPatches.ReapplyAll();
            RedoxBurnerPatches.ReapplyAll();
            QualityRefinerySelectPatches.ReapplyAll();
        }

        public void IntoOtherSave()
        {
            SlotDataStore.Clear();
            AlloyRatioStore.Clear();
            CatalystBedStore.Clear();
            QualityBuildStore.Clear();
            // 站点号在新存档里会重复使用，不清的话那些站点会被当成「已经引导过容量」
            Patches.StationCapacityPatches.ClearBootstrapped();
            // 同理：实体号也会重复使用，共位登记表留着会把新存档的建筑错认成旧的
            Patches.StackedRenderPatches.Reset();
        }
    }
}
