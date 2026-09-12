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
    public class ProjectEdenPlugin : BaseUnityPlugin, IModCanSave
    {
        public const string GUID    = "com.wangyu.projecteden";
        public const string NAME    = "Project Eden";
        public const string VERSION = "1.2.0";

        /// <summary>存档格式版本。改动 Export/Import 的字节布局时必须递增。</summary>
        private const int SaveVersion = 3;

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

        /// <summary>作弊类开关（cheats.json）。默认全关，开启时每条都会在日志里留一行</summary>
        internal static Patches.CheatsConfig CheatsConfig;

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
            CargoProbeConfig = JsonHelper.Load<Patches.CargoProbeConfig>("cargoprobe");
            AmmoRegistry.Load();
            CompositeRegistry.Load();
            CombustiblePowerPatches.Load();
            ProliferatorPatches.Load();
            AlienVeinPatches.Load();

            // 英文本地化：表要在任何 proto 注册之前载好，注册本身挂在
            // Localization.LoadSettings 上（那时 namesIndexer 才有内容，防撞检查才做得了）
            I18N.Load();

            ReportCheats();
            ReportCargoProbe();
            Patches.CargoWidening.Report();

            // 分页要在游戏建立建造栏之前注册，LDBTool 的回调里已经太晚
            MegaBuildingRegistry.RegisterTab();

            _harmony = new Harmony(GUID);
            _harmony.PatchAll(typeof(ProjectEdenPlugin).Assembly);
            CompatibilityRegistry.ApplyPatches(_harmony);

            LDBTool.PreAddDataAction += MegaBuildingRegistry.OnPreAddData;
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
            // 复合材只解析不注册（物品和配方都在 ores.json 里），所以只挂 PostAdd
            LDBTool.PostAddDataAction += CompositeRegistry.OnPostAddData;
            // 活性增产剂：排在复合材之后——投料就是那四级，
            // 而两个分数读的是 metals.json 的四维，两边都得先就绪
            LDBTool.PostAddDataAction += ProliferatorPatches.OnPostAddData;
            // 外星矿脉：排在金属属性之后——钻头谓词读的就是四维
            LDBTool.PostAddDataAction += AlienVeinPatches.OnPostAddData;

            // 可燃液体发电：排在金属属性之后——它要读 MetalPropertyPatches.FieldsEnd
            // 来避开已被占用的属性行字段号，而那个值只有注册跑完才是准的。
            // 也要排在矿种与机器注册之后，才解析得出液体和电厂
            LDBTool.PostAddDataAction += CombustiblePowerPatches.OnPostAddData;

            // 排在最后：要等所有注册器都把物品塞进 LDB 之后，才重建流体白名单
            LDBTool.PostAddDataAction += RefreshFluidList;

            // 炮塔弹药白名单是同一族的另一张预加载期静态表，LDBTool 同样没有替我们重跑
            LDBTool.PostAddDataAction += RefreshTurretNeeds;

            // 燃料普查：纯诊断，不改任何东西。
            // FuelType 位和发电建筑的 prefab 参数都在 resources.assets 里，离线读不到
            LDBTool.PostAddDataAction += FuelSurvey.OnPostAddData;

            // 增产剂普查：同样是纯诊断。Ability / HpMax / incItemId 都在
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

            LDBTool.PostAddDataAction += I18N.VerifyCoverage;
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
            LDBTool.PostAddDataAction -= BeltSpeedPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= MetalPropertyPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= AlloyRatioPatches.OnPostAddData;
            LDBTool.PreAddDataAction -= AmmoRegistry.OnPreAddData;
            LDBTool.PostAddDataAction -= AmmoRegistry.OnPostAddData;
            LDBTool.PostAddDataAction -= CompositeRegistry.OnPostAddData;
            LDBTool.PostAddDataAction -= ProliferatorPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= AlienVeinPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= CombustiblePowerPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= RefreshFluidList;
            LDBTool.PostAddDataAction -= RefreshTurretNeeds;
            LDBTool.PostAddDataAction -= FuelSurvey.OnPostAddData;
            LDBTool.PostAddDataAction -= ProliferatorSurvey.OnPostAddData;
            LDBTool.PostAddDataAction -= MinerStationSurvey.OnPostAddData;
            LDBTool.PostAddDataAction -= BioMatrixPatches.OnPostAddData;
            LDBTool.PostAddDataAction -= UniverseMatrixPatches.OnPostAddData;
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
                Log.LogInfo($"作弊开关：总开关 enabled=false，五项全部无效。改这里：{where}");

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
                Log.LogInfo($"作弊开关：五项全部关闭。要开哪一项改这里：{where}（不存在就新建，改完重开游戏，不用重新编译）");

                return;
            }

            Log.LogWarning($"以下作弊项已开启：{string.Join("、", on.ToArray())}。改这里：{where}");
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

            // **必须在这里再贴一次。** AlloyRatioPatches 挂在 GameData.Import 上的那个后置
            // 跑在本方法之前，那时这个 store 还是空的——只靠它的话，存档里的配比
            // 会在读档时被静默丢掉。到了这里配方数据已经恢复完，正是补贴的时机。
            AlloyRatioPatches.ReapplyAll("存档块读回后");
            AmmoPairPatches.ReapplyAll();
            CompositePatches.ReapplyAll();
            CompositeOutputPatches.ReapplyAll();
            ProliferatorPatches.ReapplyAll();
        }

        public void IntoOtherSave()
        {
            SlotDataStore.Clear();
            AlloyRatioStore.Clear();
        }
    }
}
