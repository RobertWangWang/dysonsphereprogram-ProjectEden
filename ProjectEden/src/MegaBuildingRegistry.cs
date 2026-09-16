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
using System.Linq;
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
        /// <summary>按物品 ID 找回配置项。程序化建模那边要读单座的体量旋钮。</summary>
        internal static MegaBuildingEntry EntryOf(int itemId)
        {
            MegaBuildingEntry[] all = Config?.buildings;

            if (all == null) return null;

            for (var i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].itemId == itemId)
                    return all[i];

            return null;
        }

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
                Utils.ProtoSlots.ReserveGrid(GridIndexOf(entry), Utils.ProtoSlots.GridKind.Item,
                    entry.displayName);
                Utils.ProtoSlots.ReserveGrid(GridIndexOf(entry), Utils.ProtoSlots.GridKind.Recipe,
                    entry.displayName);
                Utils.ProtoSlots.ReserveBuildIndex(
                    Config.buildCategory * 100 + entry.slot, entry.displayName);
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

            // 哪几座建筑看天吃饭。只读 megabuildings.json，但放在这里是为了让状态行
            // 和上面那批注册结果打在一起，一眼能对上
            Patches.MegaLightPatches.Collect();

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
                DescFields = MergeDescFields(source, entry),
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
                TimeSpend = entry.recipeTimeSpend > 0 ? entry.recipeTimeSpend : Config.recipeTimeSpend,
                // 单座覆盖优先。**拿的是配置对象自己的数组**，不是全局那份的副本——
                // RecipeProto.Items 按引用挂上去，共用一份的话就地改一座会改掉全部
                Items = entry.recipeItems != null && entry.recipeItems.Length > 0
                    ? entry.recipeItems
                    : Config.recipeItems,
                ItemCounts = entry.recipeItemCounts != null && entry.recipeItemCounts.Length > 0
                    ? entry.recipeItemCounts
                    : Config.recipeItemCounts,
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

            // **配了枢纽段的建筑既不是组装机也不是物流站。**
            //
            // 它不生产任何东西，也不收发货——皮带进空柜、出满柜，就这一件事。
            // 强行留着那两个组件会同时引出两个真问题，实测都踩过：
            //
            // (1) 点开它会弹**制造面板**，而它没有配方（recipeType 0），面板是空的；
            // (2) 更糟的是**窗口之争**：UIGame.OnPlayerInspecteeChange 里 23 个组件 id
            //     依次判断、后匹配的赢，powerExcId(IL 0196) 排在 assemblerId(00A7) 之后，
            //     于是它打开 UIPowerExchangerWindow 并当场 NullReferenceException。
            //
            // 上一版的修法是把 powerExcId 也压掉，结果**模式按钮跟着没了**——充电／放电／
            // 待机三个按钮就在那个窗口上，于是蓄能柜永远充不满，「满」变体注册了却拿不到。
            // 那是拿一个坏掉的修法去补另一个坏掉的设计。
            //
            // 把组件减到只剩枢纽，这两件事**结构上**就不存在了：没有装配机就没有制造面板，
            // 没人抢窗口，原版枢纽窗口自然打开，模式按钮回来。
            var exchangerOnly = entry.exchanger != null && entry.exchanger.energyPerTick > 0L;

            modelPrefabDesc.isAssembler = !exchangerOnly;
            modelPrefabDesc.assemblerRecipeType = (ERecipeType)entry.recipeType;
            modelPrefabDesc.assemblerSpeed = Config.assemblerSpeed;

            // 克隆的是物流运输站的 prefab，保留站点身份即可复用整套运输机调度：
            // 储物格、停机坪锚点、供需配对都是现成的。采集类站点身份要关掉，
            // 那是轨道采集器/矿脉采集站的行为，和组装机无关。
            modelPrefabDesc.isStation = Config.stationEnabled && !exchangerOnly;
            modelPrefabDesc.isCollectStation = false;
            modelPrefabDesc.isVeinCollector = false;

            if (Config.stationEnabled && !exchangerOnly)
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

            ApplyGenerator(ref modelPrefabDesc, entry);

            // 枢纽也要挂节点，理由和发电机完全一样（见 ApplyGridHookup 的注释）。
            // 发电机那一半由 ApplyGenerator 内部调用，所以这里只补「只有枢纽」的情形，
            // 免得两边都挂一次
            // **模板按能力反查，不写死物品号。** 原版 proto 在 resources.assets 里离线
            // 枚举不出来，硬写一个号万一指错，抄过来的就是别人家的连接距离和覆盖半径，
            // 而且一声不吭。要抄的对象很好认：原版自己那台能量枢纽——它干的正是这件事
            if (exchangerOnly && entry.generator == null)
                ApplyGridHookup(ref modelPrefabDesc, VanillaExchangerItemId(entry), entry);

            LDBTool.PreAddProto(model);
        }

        /// <summary>
        /// 让这座巨型建筑<b>同时</b>成为一台发电机。
        ///
        /// <b>组件模型允许一台实体挂多个组件，这一点是读 IL 确认的、不是猜的。</b>
        /// <c>PlanetFactory.CreateEntityLogicComponents</c> 里 <c>isPowerGen</c>（IL 059E）
        /// 和 <c>isAssembler</c>（IL 1122）是两个完全独立的顺序 if，中间隔着十几个别的
        /// 组件判断；<c>EntityData</c> 也为它们各留了 <c>powerGenId</c> / <c>assemblerId</c>
        /// 两个字段。所以组装机 + 物流站 + 发电机 + 耗电体可以是同一台建筑。
        ///
        /// <b>那四个「必须逐个抄」的字段一个都不能漏。</b>
        /// <c>PowerSystem.NewGeneratorComponent</c> 是把 <c>photovoltaic</c> /
        /// <c>windForcedPower</c> / <c>gammaRayReceiver</c> / <c>geothermal</c> /
        /// <c>genEnergyPerTick</c> / <c>useFuelPerTick</c> / <c>fuelMask</c> /
        /// <c>powerCatalystId</c> 逐个字段抄进组件的（IL 0090~0195）。这里的 prefab 克隆自
        /// 物流运输站，那四个发电方式的布尔本来就是 false，但<b>显式写成 false</b>：
        /// 漏一个的后果是一台一度电不发的电厂，而且哪里都不报错。
        /// </summary>
        private static void ApplyGenerator(ref PrefabDesc desc, MegaBuildingEntry entry)
        {
            MegaGeneratorEntry gen = entry.generator;

            if (gen == null || gen.genEnergyPerTick <= 0L) return;

            if (gen.useFuelPerTick <= gen.genEnergyPerTick)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"「{entry.displayName}」的 useFuelPerTick({gen.useFuelPerTick}) 不大于 " +
                    $"genEnergyPerTick({gen.genEnergyPerTick})——那是能量利用率 >= 100% 的永动机，" +
                    "发电段已忽略");

                return;
            }

            desc.isPowerGen = true;

            // 四种发电方式全部显式关掉：这台烧燃料，不靠光、风、射线或地热
            desc.photovoltaic = false;
            desc.windForcedPower = false;
            desc.gammaRayReceiver = false;
            desc.geothermal = false;

            desc.genEnergyPerTick = gen.genEnergyPerTick;
            desc.useFuelPerTick = gen.useFuelPerTick;
            desc.fuelMask = gen.fuelMask > 0 ? gen.fuelMask : 32;
            desc.powerCatalystId = 0;

            ApplyGridHookup(ref desc, gen, entry);

            double eta = gen.genEnergyPerTick / (double)gen.useFuelPerTick;

            ProjectEdenPlugin.Log.LogInfo(
                $"「{entry.displayName}」同时是发电机：发电 {gen.genEnergyPerTick * 60 / 1e9:0.##} GW，" +
                $"耗燃料 {gen.useFuelPerTick * 60 / 1e9:0.##} GW，能量利用率 {eta:0.###}，" +
                $"燃料掩码 {desc.fuelMask}");
        }

        /// <summary>
        /// 把能量枢纽段落到 prefab 上：它服务哪一对空/满蓄电器，以及充放功率。
        ///
        /// <b>这一段必须在 PostAddDataAction 跑，不能跟其余部分一起在 PreAddDataAction。</b>
        /// 它要的 <c>emptyId</c> / <c>fullId</c> 是 <c>MachineRegistry</c> 注册的蓄电器物品号，
        /// 而巨型建筑注册在机器<b>之前</b>——那一刻那两个号还不存在。
        /// 早一步写进去的会是 0，而 0 的后果是「枢纽建好了、皮带接上了、一个柜子也不收」，
        /// 一声不吭。所以按 key 反查，并且在这里就把查不到吼出来。
        ///
        /// 晚写不要紧：<c>prefabDesc</c> 只在**建造实体时**被读走一次，而这座建筑还没有
        /// 任何存量实体（trap 1 说的「值烘焙进存档」对新建筑不成立）。
        /// </summary>
        internal static void ApplyExchangers()
        {
            if (Config?.buildings == null) return;

            foreach (MegaBuildingEntry entry in Config.buildings)
            {
                MegaExchangerEntry exc = entry?.exchanger;

                if (exc == null || exc.energyPerTick <= 0L) continue;

                int emptyId = MachineRegistry.MachineItemIdByKey(exc.vaultMachineKey);
                int fullId = MachineRegistry.MachineFullItemIdByKey(exc.vaultMachineKey);

                if (emptyId <= 0 || fullId <= 0)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"「{entry.displayName}」的能量枢纽段：按 key「{exc.vaultMachineKey}」"
                        + $"反查不到蓄电器（空 {emptyId} / 满 {fullId}）。"
                        + "那台得是 machines.json 里 kind 为 accumulator、且配了 fullVariant 的机器。"
                        + "**枢纽段未生效**——建好之后它一个柜子也不会收");

                    continue;
                }

                ItemProto item = LDB.items.Select(entry.itemId);
                PrefabDesc desc = item?.prefabDesc;

                if (desc == null || desc == PrefabDesc.none)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"「{entry.displayName}」拿不到自己的 prefabDesc，枢纽段未生效");

                    continue;
                }

                desc.isPowerExchanger = true;
                desc.emptyId = emptyId;
                desc.fullId = fullId;
                desc.exchangeEnergyPerTick = exc.energyPerTick;

                // 默认那一对排在最前，其余的登记给切档用
                Patches.MegaExchangerDefaultPatches.RegisterPair(
                    exc.energyPerTick, emptyId, fullId, exc.vaultMachineKey);

                if (exc.alsoServes != null)
                    foreach (string key in exc.alsoServes)
                    {
                        int e2 = MachineRegistry.MachineItemIdByKey(key);
                        int f2 = MachineRegistry.MachineFullItemIdByKey(key);

                        if (e2 > 0 && f2 > 0)
                        {
                            Patches.MegaExchangerDefaultPatches.RegisterPair(
                                exc.energyPerTick, e2, f2, key);

                            continue;
                        }

                        ProjectEdenPlugin.Log.LogError(
                            $"「{entry.displayName}」的 alsoServes 里「{key}」反查不到蓄电器"
                            + $"（空 {e2} / 满 {f2}），这一档切不过去");
                    }

                ProjectEdenPlugin.Log.LogInfo(
                    $"「{entry.displayName}」同时是能量枢纽：服务「{exc.vaultMachineKey}」"
                    + $"（空 {emptyId} / 满 {fullId}），充放功率 {exc.energyPerTick * 60 / 1e9:0.##} GW");
            }
        }

        /// <summary>
        /// 物品提示栏要显示哪几行，由 <c>ItemProto.DescFields</c> 这张<b>字段号清单</b>决定，
        /// 而不是由 prefabDesc 上有没有那个值决定。
        ///
        /// <b>这两件事很容易混为一谈，而且混错了完全不报错。</b>
        /// <c>ItemProto.GetPropValue</c> 里确实有一支现成的分支会算
        /// <c>prefabDesc.genEnergyPerTick × 60</c>（switch 的 case 5，IL 02F0~0309），
        /// 但那一支<b>只有 DescFields 里点名了它才会被调到</b>。我们的物品模板抄自制造台，
        /// 清单里当然没有发电那一项——于是这座 30 GW 的电厂在提示栏里一行发电功率都没有，
        /// 而工作功率、待机功率照常显示，看着像是发电被漏掉了。
        ///
        /// <b>字段号从真机身上取并集，不写死。</b> 写 <c>new[] { 5 }</c> 也能work，
        /// 但那是把一个从 IL 里读出来的魔数钉进代码；直接跟源电厂取并集则连
        /// 「燃料消耗」这类同族的行一起带上，而且游戏更新挪动了编号也不会错。
        /// 顺序保持「模板在前、电厂新增的在后」，提示栏的既有排版不动。
        /// </summary>
        private static int[] MergeDescFields(ItemProto source, MegaBuildingEntry entry)
        {
            int[] baseFields = source.DescFields ?? new int[0];

            MegaGeneratorEntry gen = entry.generator;

            if (gen == null || gen.genEnergyPerTick <= 0L) return baseFields;

            int sourceId = gen.connectFromItemId > 0 ? gen.connectFromItemId : 2204;
            ItemProto plant = LDB.items.Select(sourceId);

            if (plant?.DescFields == null || plant.DescFields.Length == 0)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"「{entry.displayName}」：量不到原版电厂 {sourceId} 的 DescFields，" +
                    "提示栏里不会有发电功率那一行");

                return baseFields;
            }

            var merged = new List<int>(baseFields);

            foreach (int f in plant.DescFields)
                if (!merged.Contains(f))
                    merged.Add(f);

            if (merged.Count == baseFields.Length) return baseFields;

            ProjectEdenPlugin.Log.LogInfo(
                $"「{entry.displayName}」提示栏字段：{baseFields.Length} 项 → {merged.Count} 项" +
                $"（并入「{plant.Name}」的 {string.Join("/", plant.DescFields.Select(x => x.ToString()).ToArray())}）");

            return merged.ToArray();
        }

        /// <summary>
        /// 把「怎么接电网」从一座真实的原版电厂身上量过来。
        ///
        /// <b>这一步不是锦上添花，缺了它这座电厂一度电都发不出来</b>，而且完全不报错：
        /// 组装机照跑、耗电照扣、燃料照烧，就是没有电流进电网。原因在 IL 里是闭合的——
        /// 全汇编往 <c>PowerNetwork.generators</c> 里加元素的<b>只有</b>
        /// <c>PowerSystem.OnNodeAdded</c>（IL 04DD 处 <c>list_sorted_add(net.generators, node.genId)</c>），
        /// 而 <c>OnNodeAdded</c> 只被 <c>NewNodeComponent</c> 调用。也就是说：
        /// <b>发电机是顺着「节点」进电网的，而节点身份来自 <c>isPowerNode</c></b>。
        /// <c>NewGeneratorComponent</c> 自己什么都不调——对照 <c>NewConsumerComponent</c>
        /// 会调 <c>OnConsumerAdded</c>，这个不对称就是整件事的答案。
        ///
        /// 我们的 prefab 克隆自物流运输站，那是个纯耗电体，所以节点身份得自己补上。
        /// <b>补的是身份和连接距离，不是供电范围</b>：覆盖半径照抄源电厂（电厂本来就不给别人供电），
        /// 这座建筑不该变成一座变电站。
        /// </summary>
        private static void ApplyGridHookup(ref PrefabDesc desc, MegaGeneratorEntry gen,
                                            MegaBuildingEntry entry)
        {
            ApplyGridHookup(ref desc, gen != null && gen.connectFromItemId > 0
                ? gen.connectFromItemId : 2204, entry);
        }

        /// <summary>
        /// 把「挂上电网」那组参数从一座真的发电建筑身上抄过来。
        ///
        /// <b>发电机和能量枢纽都需要它，而且理由是同一条实测。</b>
        /// <c>PowerGeneratorComponent.networkId</c> 和
        /// <c>PowerExchangerComponent.networkId</c> 的写入点，除各自的 Import 之外，
        /// <b>只有 <c>PowerSystem.OnNodeAdded / OnNodeRemoving</c></b>，而 OnNodeAdded
        /// 的唯一调用者是 <c>NewNodeComponent</c>。所以：
        /// <c>isPowerGen</c> / <c>isPowerExchanger</c> 只表示「它能干那件事」，
        /// <b><c>isPowerNode</c> 才表示「它挂在电网上」</b>。
        ///
        /// 本 mod 的巨型建筑克隆自物流运输站——**纯耗电体，没有节点**。
        /// 漏了这一步的症状是最难查的那种：**每一步都成功，功能整个不在**
        /// ——面板打开、模式能选、额定功率写着 60 GW，而「电网 #0、供电率 OFF」，
        /// 一焦耳都进不去。氧化还原燃烧厂为这条付过一次账，枢纽这次又付了一次。
        /// </summary>
        /// <summary>
        /// 找原版那台能量枢纽的物品号，**按能力认，不按号认**。
        ///
        /// 原版 proto 在 <c>resources.assets</c> 里，离线枚举不出来；写死一个号万一指错，
        /// 抄过来的就是别人家的连接距离和覆盖半径，而且一声不吭。
        /// 判据用 <c>isPowerExchanger</c>——那就是「它是一台能量枢纽」这件事本身。
        /// 找不到就退回 0，让 <see cref="ApplyGridHookup"/> 去吼。
        /// </summary>
        private static int VanillaExchangerItemId(MegaBuildingEntry entry)
        {
            ItemProto[] items = LDB.items?.dataArray;

            if (items == null) return 0;

            foreach (ItemProto item in items)
            {
                // 只认原版的：本 mod 自己的枢纽还没建好，抄自己等于什么都没抄
                if (item == null || item.ID >= 6000) continue;

                PrefabDesc d = item.prefabDesc;

                if (d != null && d.isPowerExchanger) return item.ID;
            }

            ProjectEdenPlugin.Log.LogError(
                $"「{entry.displayName}」：LDB 里找不到任何原版能量枢纽，接电网的参数抄不到。"
                + "它会有面板、能选模式、额定功率也对，但**电网是 #0**，一焦耳都进不去");

            return 0;
        }

        private static void ApplyGridHookup(ref PrefabDesc desc, int sourceId, MegaBuildingEntry entry)
        {
            if (sourceId <= 0) return;


            ItemProto source = LDB.items.Select(sourceId);
            PrefabDesc src = source?.prefabDesc;

            if (src == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"「{entry.displayName}」：量不到原版电厂 {sourceId} 的 prefab，" +
                    "接电网的参数没抄过来——它会照常生产、照常耗电，但一度电都发不进电网");

                return;
            }

            if (!src.isPowerNode)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"「{entry.displayName}」：物品 {sourceId}（{source.Name}）的 isPowerNode 是 false，" +
                    "它自己都没挂在电网上，抄它是抄不来连接能力的。参数仍会照抄，但很可能接不上" +
                    "——发电机那一路把 generator.connectFromItemId 指到一座真的电厂；" +
                    "枢纽那一路是按 isPowerExchanger 反查的，查到这个说明原版枢纽本身就没有节点，" +
                    "那得重新想模板");
            }

            desc.isPowerNode = src.isPowerNode;
            desc.powerConnectDistance = src.powerConnectDistance;
            desc.powerCoverRadius = src.powerCoverRadius;

            ProjectEdenPlugin.Log.LogInfo(
                $"「{entry.displayName}」接电网的参数量自「{source.Name}」({sourceId})：" +
                $"isPowerNode={desc.isPowerNode}，连接距离 {desc.powerConnectDistance:0.##}，" +
                $"覆盖半径 {desc.powerCoverRadius:0.##}");
        }
    }
}
