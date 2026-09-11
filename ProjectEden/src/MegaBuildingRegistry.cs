// 本文件移植自 ProjectGenesis（创世之书），属于其衍生作品。
// Portions of this file are derived from ProjectGenesis (GenesisBook).
//
//     Copyright (C) 2022-2026 Awbugl and ProjectGenesis contributors
//     https://github.com/Awbugl/ProjectGenesis
//
// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Collections.Generic;
using CommonAPI.Systems;
using ProjectEden.Utils;
using UnityEngine;
using xiaoye97;

namespace ProjectEden
{
    /// <summary>
    /// 注册全部巨型建筑：克隆物流运输站的模型（带 slotPoses，传送带才能直连），
    /// 把 assemblerSpeed 提到阈值以上，各自挂一份无前置科技的配方，
    /// 并统一放进 CommonAPI 注册的"巨型建筑"分页。数值全部来自 data/megabuildings.json。
    /// </summary>
    internal static class MegaBuildingRegistry
    {
        internal static MegaBuildingsConfig Config { get; private set; }

        /// <summary>CommonAPI 分配的分页索引。它同时就是 GridIndex 的页号。</summary>
        internal static int TabIndex => _tabIndex;

        /// <summary>组装机速度达到该值即被补丁视为"巨型"，可直连传送带。</summary>
        internal static int MegaSpeedThreshold =>
            Config == null ? int.MaxValue :
            Config.megaSpeedThreshold > 0 ? Config.megaSpeedThreshold : Config.assemblerSpeed;

        /// <summary>本 mod 注册的全部配方 ID，供强制解锁使用。</summary>
        internal static readonly HashSet<int> RecipeIds = new HashSet<int>();

        /// <summary>期望模型 ID → 实际分配到的模型 ID。</summary>
        private static readonly Dictionary<int, int> AssignedModelIds = new Dictionary<int, int>();

        private static int _tabIndex = -1;

        internal static void Load() => Config = JsonHelper.Load<MegaBuildingsConfig>("megabuildings");

        /// <summary>
        /// 注册建造栏分页。必须在 Awake 里做——TabSystem 要在游戏建立建造栏之前拿到分页，
        /// 而 LDBTool 的 PreAddDataAction 已经太晚。
        /// </summary>
        internal static void RegisterTab()
        {
            _tabIndex = TabSystem.RegisterTab(Config.tabId, new TabData(Config.tabName, Config.tabIconPath));

            ProjectEdenPlugin.Log.LogInfo($"已注册建造栏分页「{Config.tabName}」，索引 {_tabIndex}");
        }

        /// <summary>LDBTool.PreAddDataAction：注册新 proto。</summary>
        internal static void OnPreAddData()
        {
            // 集装科技的解锁值要在这个阶段改：物流站面板的滑条上限按 UnlockValues 现算
            Patches.PilerLevelPatches.ModifyPilerTeches();

            // 保证 prefabDesc 等派生数据已就绪，再去读原版建筑
            LDB.items.OnAfterDeserialize();

            ItemProto source = LDB.items.Select(Config.copyFromItemId);

            if (source == null)
            {
                ProjectEdenPlugin.Log.LogError($"找不到物品模板 {Config.copyFromItemId}，巨型建筑注册中止");
                return;
            }

            if (Config.buildings == null || Config.buildings.Length == 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    "megabuildings.json 里没有解析出任何建筑（buildings 为空）。" +
                    "顶层字段能读到而 buildings 读不到，通常是 JSON 解析器的问题，不是文件内容的问题。");

                return;
            }

            // 本轮从头开始登记：本模块最先跑，顺手把登记簿清干净
            Utils.ProtoSlots.ClearReservations();

            foreach (MegaBuildingEntry entry in Config.buildings)
            {
                int modelId = ResolveModelId(entry.modelId);

                if (modelId <= 0)
                {
                    ProjectEdenPlugin.Log.LogError($"{entry.displayName}：找不到可用的模型 ID，跳过");
                    continue;
                }

                AssignedModelIds[entry.modelId] = modelId;

                ReportProceduralModels();

                CopyModelProto(Config.copyFromModelId, modelId, entry,
                               new Color(entry.tintR, entry.tintG, entry.tintB));

                AddItemProto(source, entry, modelId);
                AddRecipeProto(entry);

                RecipeIds.Add(entry.recipeId);

                // 登记给后面的注册器看——这时它们还都不在 LDB 里
                Utils.ProtoSlots.ReserveItemId(entry.itemId);
                Utils.ProtoSlots.ReserveRecipeId(entry.recipeId);
                Utils.ProtoSlots.ReserveModelId(modelId);
                Utils.ProtoSlots.ReserveGrid(GridIndexOf(entry), Utils.ProtoSlots.GridKind.Item);
                Utils.ProtoSlots.ReserveGrid(GridIndexOf(entry), Utils.ProtoSlots.GridKind.Recipe);
                Utils.ProtoSlots.ReserveBuildIndex(Config.buildCategory * 100 + entry.slot);
            }

            // 额外配方要在同一阶段注册，且共用 RecipeIds 这个免科技解锁集合
            ExtraRecipeRegistry.OnPreAddData();
        }

        /// <summary>
        /// LDBTool.PostAddDataAction：新增 proto 会让游戏启动时建立的静态缓存失效，这里按需重跑。
        /// 清单参考 ProjectGenesis 的 PostAddDataAction，只保留新建筑需要的那部分。
        /// </summary>
        internal static void OnPostAddData()
        {
            LDB.items.OnAfterDeserialize();
            LDB.recipes.OnAfterDeserialize();
            LDB.models.OnAfterDeserialize();

            // 新增 proto 之后必须重跑 Preload，否则新物品是"半成品"：
            //   · _iconSprite 为空                    → 建造栏和合成器里显示成白块
            //   · prefabDesc 没有从 ModelProto 取过来 → 制造速度/功率显示为 "-" 和 0W
            //   · recipes 没重新查找                  → 合成器里没有它的配方，"制造于"为空
            ProtoPreload();

            ItemProto.InitConstructableItems();
            ItemProto.InitItemIds();
            ItemProto.InitItemIndices();
            ItemProto.InitProductionMask();

            ModelProto.InitMaxModelIndex();
            ModelProto.InitModelIndices();
            ModelProto.InitModelOrders();

            StorageComponent.staticLoaded = false;
            StorageComponent.LoadStatic();

            UIBuildMenu.staticLoaded = false;
            UIBuildMenu.StaticLoad();

            ReportBuildMenuSlots();

            PlanetFactory.PrefabDescByModelIndex = null;
            PlanetFactory.InitPrefabDescArray();

            if (GameMain.instance != null)
            {
                GameMain.instance.CreateGPUInstancing();
                GameMain.instance.CreateBPGPUInstancing();
            }

            // 这两项都要等 proto 加载完才能查物品是否存在
            Patches.AdvancedMinerPatches.BuildProductMap();
            Patches.StationCapacityPatches.ApplyPrefabCapacity();
            Patches.GasCollectorPatches.ApplyPrefabSpeed();
            Patches.MatrixLabPatches.ApplyPrefabSpeed();
            Patches.PowerCoveragePatches.ApplyPrefabCoverage();

            foreach (MegaBuildingEntry entry in Config.buildings)
            {
                if (!AssignedModelIds.TryGetValue(entry.modelId, out int modelId)) continue;

                ProjectEdenPlugin.Log.LogInfo(
                    $"  已注册 {entry.displayName}：物品 {entry.itemId} / 模型 {modelId} / 配方 {entry.recipeId}" +
                    $"（{(ERecipeType)entry.recipeType}，{Config.assemblerSpeed / 10000.0:0.#} 倍速）");

            ReportSpeedCeiling();
            }
        }

        /// <summary>
        /// 核对巨型建筑有没有真的落进建造栏的格位表。
        /// UIBuildMenu.StaticLoad 按 BuildIndex/100 取分类、%100 取格位填 protos[16,13]，
        /// 这里把结果逐个打出来，避免"点得开但没有子项"时只能靠猜。
        /// </summary>
        private static void ReportBuildMenuSlots()
        {
            if (Config?.buildings == null) return;

            int category = Config.buildCategory;

            foreach (MegaBuildingEntry entry in Config.buildings)
            {
                ItemProto item = LDB.items.Select(entry.itemId);

                if (item == null)
                {
                    ProjectEdenPlugin.Log.LogWarning($"建造栏核对：物品 {entry.itemId} 不存在");
                    continue;
                }

                int cat = item.BuildIndex / 100;
                int slot = item.BuildIndex % 100;

                ItemProto inSlot = cat >= 0 && cat < 16 && slot >= 0 && slot < 13
                    ? UIBuildMenu.protos[cat, slot]
                    : null;

                string state = inSlot == null ? "格位为空"
                    : inSlot.ID == entry.itemId ? "已就位"
                    : $"被 {inSlot.name}({inSlot.ID}) 占用";

                ProjectEdenPlugin.Log.LogInfo(
                    $"  建造栏核对 {item.name}：BuildIndex {item.BuildIndex} → 第 {cat} 类第 {slot} 格，{state}" +
                    $"（期望分类 {category}，IsEntity={item.IsEntity}，UnlockKey={item.UnlockKey}）");
            }
        }

        /// <summary>
        /// 把「配方需要多快才能跑满」算出来打进日志，用于验证 assemblerSpeed 是否够用。
        /// 吞吐上限是每 tick 一个周期，speed 达到最长配方的 timeSpend 之后就不再有收益。
        /// </summary>
        private static void ReportSpeedCeiling()
        {
            var max = 0;
            RecipeProto longest = null;

            foreach (RecipeProto recipe in LDB.recipes.dataArray)
            {
                if (recipe == null) continue;

                int need = recipe.TimeSpend * 10000; // RecipeExecuteData.timeSpend 的换算

                if (need <= max) continue;

                max = need;
                longest = recipe;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"跑满全部配方所需的最低 speed：{max}（最长配方「{longest?.name}」），" +
                $"当前设定 {Config.assemblerSpeed}，吞吐上限为每 tick 一个周期（60 周期/秒）");

            if (Config.assemblerSpeed < max)
                ProjectEdenPlugin.Log.LogWarning(
                    $"当前 assemblerSpeed 低于 {max}，最长的那些配方达不到每 tick 一个周期。");
        }

        /// <summary>
        /// 选一个合法的模型 ID。
        ///
        /// ModelProtoSet.OnAfterDeserialize 会新建一个长度为 (模型总数 + 64) 的数组，
        /// 却用模型 ID 当下标写入。所以模型 ID 必须小于这个上界，否则那里直接 IndexOutOfRange，
        /// 且异常发生在 LDBTool 的回调里，报错位置离真正的原因很远。
        /// </summary>
        private static int ResolveModelId(int desired)
        {
            ModelProto[] models = LDB.models.dataArray;

            // 用当前长度 + 64 作上界是保守的：本 mod 自己新增的模型只会把真实上界推得更高
            int bound = models.Length + 64;

            if (desired > 0 && desired < bound && LDB.models.Select(desired) == null && !IsTaken(desired)) return desired;

            for (int id = bound - 1; id > 0; id--)
            {
                if (LDB.models.Select(id) != null || IsTaken(id)) continue;

                ProjectEdenPlugin.Log.LogWarning(
                    $"期望的模型 ID {desired} 不可用（当前模型数 {models.Length}，可用上界 {bound}），改用 {id}。" +
                    $"建议把 data/megabuildings.json 里对应的 modelId 固定为 {id}——" +
                    "模型 ID 会随建筑写进存档，日后浮动会让已建成的建筑显示异常。");

                return id;
            }

            return -1;
        }

        /// <summary>本次注册中已经分配出去的模型 ID（LDB 里还查不到，得自己记）。</summary>
        private static bool IsTaken(int id) => AssignedModelIds.ContainsValue(id);

        /// <summary>
        /// 重跑物品与配方的 Preload。移植自 ProjectGenesis 的 ProtoPreload，
        /// 只保留本 mod 需要的 items / recipes 两轮（没新增科技、矿种、里程碑）。
        /// </summary>
        private static void ProtoPreload()
        {
            for (var i = 0; i < LDB.items.dataArray.Length; ++i)
            {
                ItemProto item = LDB.items.dataArray[i];

                item.recipes = null;
                item.rawMats = null;
                item.Preload(i);
            }

            for (var i = 0; i < LDB.recipes.dataArray.Length; ++i) LDB.recipes.dataArray[i].Preload(i);
        }

        /// <summary>
        /// 合成器面板的位置。页号必须等于分页索引，否则东西会落到"建筑"页去
        /// ——分页归属看的是 GridIndex 的页号，不是 BuildIndex。
        /// </summary>
        private static int GridIndexOf(MegaBuildingEntry entry) => GridIndex(entry.gridRow, entry.gridCol);

        /// <summary>合成面板的格位编号：分页号 × 1000 + 行 × 100 + 列。</summary>
        internal static int GridIndex(int row, int col) => _tabIndex * 1000 + row * 100 + col;

        private static void AddItemProto(ItemProto source, MegaBuildingEntry entry, int modelId)
        {
            var item = new ItemProto
            {
                ID = entry.itemId,
                Name = entry.displayName,
                Description = entry.description,
                Type = source.Type,
                StackSize = Config.stackSize,
                // 合成器里的位置 = 页 * 1000 + 行 * 100 + 列，页号就是分页索引（运行时分配）
                GridIndex = GridIndexOf(entry),
                // 建造栏位置 = 分类号 * 100 + 格位，与分页索引无关
                BuildIndex = Config.buildCategory * 100 + entry.slot,
                ModelIndex = modelId,
                ModelCount = 1,
                HpMax = Config.hpMax,
                IsEntity = true,
                CanBuild = true,
                BuildMode = source.BuildMode,
                // 自制图标：走 TextureResourcesPatches 的前缀，读嵌入资源里的 PNG
                IconPath = "Assets/projecteden/" + entry.iconName,
                // 无前置科技。UnlockKey = -1 让 GameHistoryData.ItemUnlocked 直接返回 true：
                // 它是按 recipeUnlocked 集合判断的，而新开局时 SetForNewGame 会清空该集合，
                // 只靠配方解锁的话建造栏分类在新档里会是灰的。
                PreTechOverride = 0,
                UnlockKey = -1,
                Grade = 0,
                Upgrades = new int[0],
                DescFields = source.DescFields,
                prefabDesc = PrefabDesc.none,
            };

            item.name = entry.displayName;

            LDBTool.PreAddProto(item);
        }

        private static void AddRecipeProto(MegaBuildingEntry entry)
        {
            var recipe = new RecipeProto
            {
                ID = entry.recipeId,
                Name = entry.displayName,
                Description = entry.description,
                Type = ERecipeType.Assemble,
                Handcraft = true,
                Explicit = true,
                TimeSpend = Config.recipeTimeSpend,
                Items = Config.recipeItems,
                ItemCounts = Config.recipeItemCounts,
                Results = new[] { entry.itemId },
                ResultCounts = new[] { 1 },
                GridIndex = GridIndexOf(entry),
                IconPath = "Assets/projecteden/" + entry.iconName,
                // 无前置科技：preTech 留空，另由 RecipeUnlockPatches 强制标记为已解锁
                preTech = null,
            };

            recipe.name = entry.displayName;

            LDBTool.PreAddProto(recipe);
        }

        /// <summary>
        /// 克隆原版 ModelProto 并染色，同时改写 prefabDesc 使其成为一台巨型组装机。
        /// 移植自 ProjectGenesis 的 CopyModelUtils.CopyModelProto。
        /// </summary>
        private static bool _modelModeReported;

        /// <summary>
        /// 程序化模型是开是关，<b>两种状态都要报一行</b>。
        ///
        /// 关着的时候什么都不打，就分不出「开关关了」和「这段代码根本没进 DLL」——
        /// 这个仓库已经为同一个形状付过四次往返（AlloyRatioPatches.ReapplyAll、ReportCheats、
        /// CargoShaderIncProbe、多产物界面）。
        /// </summary>
        private static void ReportProceduralModels()
        {
            if (_modelModeReported) return;

            _modelModeReported = true;

            if (Config.proceduralModels)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "巨型建筑：启用程序化模型，五座各用各的几何（只换网格，占地与传送带接口仍来自原版 prefab）");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑：程序化模型已关闭，五座共用原版模型 {Config.copyFromModelId} 并只靠染色区分。" +
                "要打开就改 megabuildings.json 的 proceduralModels");
        }

        /// <summary>
        /// 巨型建筑的储物格数不能超过悬停信息框撑得住的格数。
        ///
        /// <c>UIEntityBriefInfo._OnUpdate</c> 按储物格种类数遍历 prefab 里那个<b>定长</b>的
        /// icons 数组，超出即 <c>IndexOutOfRangeException</c>。<c>StationExpandPatches</c> 会按
        /// <c>stations.json</c> 的 <c>stationMaxItemKinds</c> 把它扩容——所以
        /// <b>megabuildings.json 的格数不能大过 stations.json 的</b>，否则悬停到巨型建筑上就崩。
        ///
        /// 两个配置分处两个文件、谁也不知道谁，很容易在改其中一个时踩到。这里夹一次并说明白，
        /// 免得表现成「鼠标一碰巨型建筑就崩」这种和配置八竿子打不着的症状。
        /// </summary>
        private static int SafeStorageKinds(int wanted)
        {
            int cap = Patches.StationExpandPatches.ExpandedIconKinds;

            if (cap <= 0 || wanted <= cap) return wanted;

            ProjectEdenPlugin.Log.LogWarning(
                $"巨型建筑的储物格数 {wanted} 超过了悬停信息框能撑住的 {cap} 格，已夹到 {cap}。" +
                "那个上限来自 stations.json 的 stationMaxItemKinds（icons 数组按它扩容），" +
                "要更多格就把两个文件一起调大");

            return cap;
        }

        private static void CopyModelProto(int oriId, int id, MegaBuildingEntry entry, Color color)
        {
            ModelProto oriModel = LDB.models.Select(oriId);

            if (oriModel == null)
            {
                ProjectEdenPlugin.Log.LogError($"找不到源模型 {oriId}，{entry.displayName} 将没有外观");
                return;
            }

            var model = new ModelProto
            {
                ObjectType = oriModel.ObjectType,
                RuinType = oriModel.RuinType,
                RendererType = oriModel.RendererType,
                HpMax = oriModel.HpMax,
                PrefabPath = oriModel.PrefabPath,
                Order = oriModel.Order,
                ID = id,
                Name = id.ToString(),
                SID = "",
            };

            model.sid = "";

            PrefabDesc desc = oriModel.prefabDesc;
            GameObject prefab = desc.prefab ? desc.prefab : Resources.Load<GameObject>(oriModel.PrefabPath);
            GameObject colliderPrefab = desc.colliderPrefab ? desc.colliderPrefab : Resources.Load<GameObject>(oriModel._colliderPath);

            ref PrefabDesc modelPrefabDesc = ref model.prefabDesc;

            modelPrefabDesc = prefab == null
                ? PrefabDesc.none
                : colliderPrefab == null
                    ? new PrefabDesc(id, prefab)
                    : new PrefabDesc(id, prefab, colliderPrefab);

            // 复制材质后再染色，避免改到原版建筑的材质
            foreach (Material[] lodMaterial in modelPrefabDesc.lodMaterials)
            {
                if (lodMaterial == null) continue;

                for (var j = 0; j < lodMaterial.Length; j++)
                {
                    ref Material material = ref lodMaterial[j];

                    if (material == null) continue;

                    material = new Material(material);
                    material.SetColor("_Color", color);
                }
            }

            // 换掉被画出来的几何。放在染色之后：材质已经是我们自己的副本了，
            // 而占地 / 碰撞体 / 传送带接口下面还会从原版那份 desc 抄过来，不受影响。
            if (Config.proceduralModels && modelPrefabDesc.lodMeshes != null)
                Model.MegaBuildingMeshes.Apply(ref modelPrefabDesc, entry.itemId, entry.displayName);

            modelPrefabDesc.modelIndex = id;
            modelPrefabDesc.hasBuildCollider = desc.hasBuildCollider;
            modelPrefabDesc.colliders = desc.colliders;
            modelPrefabDesc.buildCollider = desc.buildCollider;
            modelPrefabDesc.buildColliders = desc.buildColliders;
            modelPrefabDesc.colliderPrefab = desc.colliderPrefab;
            modelPrefabDesc.dragBuild = desc.dragBuild;
            modelPrefabDesc.dragBuildDist = desc.dragBuildDist;
            modelPrefabDesc.blueprintBoxSize = desc.blueprintBoxSize;
            modelPrefabDesc.roughHeight = desc.roughHeight;
            modelPrefabDesc.roughWidth = desc.roughWidth;
            modelPrefabDesc.roughRadius = desc.roughRadius;
            modelPrefabDesc.barHeight = desc.barHeight;
            modelPrefabDesc.barWidth = desc.barWidth;

            // 这三项决定它是一台什么样的组装机，以及有多快
            modelPrefabDesc.isAssembler = true;
            modelPrefabDesc.assemblerRecipeType = (ERecipeType)entry.recipeType;
            modelPrefabDesc.assemblerSpeed = Config.assemblerSpeed;

            // 克隆的是物流运输站的 prefab，保留站点身份即可复用整套运输机调度：
            // 储物格、停机坪锚点、供需配对都是现成的。采集类站点身份要关掉，
            // 那是轨道采集器/矿脉采集站的行为，和组装机无关。
            modelPrefabDesc.isStation = Config.stationEnabled;
            modelPrefabDesc.isCollectStation = false;
            modelPrefabDesc.isVeinCollector = false;

            if (Config.stationEnabled)
            {
                modelPrefabDesc.stationMaxItemCount = Config.stationMaxItemCount;
                modelPrefabDesc.stationMaxItemKinds = SafeStorageKinds(Config.stationMaxItemKinds);
                modelPrefabDesc.stationMaxDroneCount = Config.stationMaxDroneCount;
                modelPrefabDesc.stationMaxEnergyAcc = Config.stationMaxEnergyAcc;

                // 只做行星内物流，不出星际
                modelPrefabDesc.stationMaxShipCount = 0;
            }

            modelPrefabDesc.idleEnergyPerTick = entry.idleEnergyPerTick;
            modelPrefabDesc.workEnergyPerTick = entry.workEnergyPerTick;

            LDBTool.PreAddProto(model);
        }
    }
}
