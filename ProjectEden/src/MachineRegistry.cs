using System.Collections.Generic;
using ProjectEden.Utils;
using UnityEngine;
using xiaoye97;

namespace ProjectEden
{
    /// <summary>
    /// 新生产设备的注册器：整台克隆一个原版建筑，只换外观、名字和<b>配方类型</b>。
    ///
    /// <b>为什么「新机器」等价于「新配方类型」。</b> 原版按单一 ERecipeType 过滤配方选择器
    /// （UIRecipePicker.RefreshIcons：filter != 0 且 filter != recipe.Type 就跳过；
    /// filter 由 UIAssemblerWindow.OnSelectRecipeClick 从 prefabDesc.assemblerRecipeType 取），
    /// 一台机器只认一种类型。所以「只有新机器能做的一类配方」等价于「开一个新的类型号 +
    /// 一台 assemblerRecipeType 指向它的机器」。
    ///
    /// <b>不需要 preloader。</b> ERecipeType 是 int 枚举，(ERecipeType)9 不需要有名字也合法
    /// ——和 EVeinType 一个道理。原版占了 1~8 与 15，<b>9~14 空着</b>。而且
    /// AssemblerComponent.SetRecipe <b>完全不校验类型</b>（IL 里只有 recipeId &gt; 0 与查表判空），
    /// 类型只在 UI 那一层起作用，所以生产逻辑一行都不用改。
    ///
    /// 要补的只有两处文字：配方的「制造于」和物品提示栏的「类型」，见 RecipeTypeNamePatches。
    /// </summary>
    internal static class MachineRegistry
    {
        /// <summary>一台已注册机器的运行时状态。</summary>
        internal class Machine
        {
            internal MachineEntry Entry;

            internal int ItemId;
            internal int ModelId;
            internal int RecipeId;
            internal int Grid;
            internal int RecipeGrid;
            internal int BuildIndex;

            /// <summary>源建筑的图标路径，PostAddData 里拿它改色</summary>
            internal string SourceIconPath;

            internal int RecipeType => Entry.recipeType;
            internal string Key => Entry.key ?? Entry.displayName;

            /// <summary>物流站型：克隆星际站，两种无人机原样继承</summary>
            internal bool IsStation => Entry.kind == "station";

            /// <summary>蓄电器型：克隆蓄电器，只放大容量和充放电功率</summary>
            internal bool IsAccumulator => Entry.kind == "accumulator";

            /// <summary>能量枢纽型：克隆能量枢纽，只换它服务的那一对空/满</summary>
            internal bool IsExchanger => Entry.kind == "exchanger";

            /// <summary>发电设备型：克隆发电建筑，只放大发电功率</summary>
            internal bool IsGenerator => Entry.kind == "generator";

            internal bool IsMiner => Entry.kind == "miner";

            /// <summary>「满」版本的物品 ID，没有则为 0</summary>
            internal int FullItemId;
        }

        internal static MachinesConfig Config { get; private set; }

        /// <summary>按 key 找一台发电建筑的 generator 配置，找不到返回 null。</summary>
        internal static MachineGeneratorEntry FindGenerator(string key)
        {
            if (Config?.machines == null || string.IsNullOrEmpty(key)) return null;

            foreach (MachineEntry entry in Config.machines)
                if (entry != null && entry.key == key)
                    return entry.generator;

            return null;
        }

        internal static readonly List<Machine> Machines = new List<Machine>();

        /// <summary>
        /// 物流站型建筑的物品 ID。StationCapacityPatches 会把它们和原版物流站一视同仁，
        /// 所以储量 / 格数 / 充能功率不用在 machines.json 里重复配一遍。
        /// </summary>
        /// <summary>
        /// 「满」版本物品 → 配对的「空」版本物品。烧完之后要把空壳还给玩家。
        /// 不是本 mod 的满版本就返回 0。给 <c>MechaFuelShellPatches</c> 用。
        /// </summary>
        internal static int EmptyForFull(int fullItemId)
        {
            if (fullItemId <= 0) return 0;

            for (var i = 0; i < Machines.Count; i++)
                if (Machines[i].FullItemId == fullItemId)
                    return Machines[i].ItemId;

            return 0;
        }

        internal static IEnumerable<int> StationItemIds
        {
            get
            {
                foreach (Machine machine in Machines)
                    if (machine.IsStation)
                        yield return machine.ItemId;
            }
        }

        internal static void Load()
        {
            Config = JsonHelper.Load<MachinesConfig>("machines");

            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogWarning("读不到 machines.json，新生产设备未启用");

                return;
            }

            if (!Config.enabled) ProjectEdenPlugin.Log.LogInfo("新生产设备已在配置里关闭");
        }

        /// <summary>配方类型号 → 「制造于」显示的机器名。给 RecipeTypeNamePatches 用。</summary>
        /// <remarks>
        /// <b>必须先排除 0。</b> 物流站 / 蓄电器 / 能量枢纽这几类条目根本不配 recipeType，
        /// 字段默认就是 0；若不排除，任何 <c>Type == None</c> 的配方（手搓专用配方就是这样）
        /// 都会被认成「制造于 综合物流枢纽」。
        /// </remarks>
        /// <summary>
        /// 是不是本 mod 自己的配方类型。<b>原版占 1~8 和 15</b>（15 是 Research），
        /// 其余全是我们的——包括 16 以上，那一段和 14 一样能用。
        /// </summary>
        internal static bool IsCustomType(int recipeType) => recipeType >= 9 && recipeType != 15;

        internal static string RecipeTypeMachineName(int recipeType)
        {
            if (recipeType <= 0) return null;

            for (var i = 0; i < Machines.Count; i++)
                if (Machines[i].RecipeType == recipeType)
                    return Machines[i].Entry.displayName;

            return MegaBuildingRecipeTypeName(recipeType);
        }

        /// <summary>
        /// 巨型建筑也能持有自定义配方类型（生物温室的 11 号），这时「制造于」该写它的名字。
        ///
        /// <b>只认自定义类型号，不认原版的。</b> 前五座巨型建筑借的是原版类型
        /// （1 熔炉 / 2 化工 / 4 组装 / 5 粒子），若在这里一并返回，
        /// 所有原版配方的「制造于」都会从「制造台」变成「天工装配厂」——
        /// 那是把原版文案改掉，不是补一句缺失的文案。
        ///
        /// <b>判据是「不是原版类型」，不是「落在 9~14 里」。</b> 原版占 1~8 和 15，
        /// 剩下的全是本 mod 的。这里原先写死 <c>9 &lt;= t &lt;= 14</c>，
        /// 而 14 从来不是上限（<c>ERecipeType</c> 没有任何上限，推导在 CLAUDE.md）——
        /// 综合化学厂拿了 16，那个区间就把它漏在外面了。
        /// </summary>
        private static string MegaBuildingRecipeTypeName(int recipeType)
        {
            if (!IsCustomType(recipeType)) return null;

            MegaBuildingEntry[] buildings = MegaBuildingRegistry.Config?.buildings;

            if (buildings == null) return null;

            for (var i = 0; i < buildings.Length; i++)
                if (buildings[i] != null && buildings[i].recipeType == recipeType)
                    return buildings[i].displayName;

            return null;
        }

        /// <summary>物品是本 mod 的新机器时，返回它提示栏「类型」那行的文字。</summary>
        internal static string MachineTypeName(int itemId)
        {
            for (var i = 0; i < Machines.Count; i++)
                if (Machines[i].ItemId == itemId)
                    return Machines[i].Entry.machineTypeName;

            // 持有自定义配方类型的巨型建筑：原版 typeString 是
            // assemblerRecipeType - 1 的跳转表，11 号会落到 default，显示成不相干的词
            MegaBuildingEntry[] buildings = MegaBuildingRegistry.Config?.buildings;

            if (buildings == null) return null;

            for (var i = 0; i < buildings.Length; i++)
                if (buildings[i] != null && buildings[i].itemId == itemId
                                         && IsCustomType(buildings[i].recipeType))
                    return string.IsNullOrEmpty(buildings[i].machineTypeName)
                        ? buildings[i].displayName
                        : buildings[i].machineTypeName;

            return null;
        }

        // ── 注册 ──────────────────────────────────────────────

        internal static void OnPreAddData()
        {
            Machines.Clear();

            if (Config == null || !Config.enabled || Config.machines == null) return;

            foreach (MachineEntry entry in Config.machines)
            {
                if (entry == null || !entry.enabled) continue;

                ItemProto source = ProtoSlots.ItemIdTaken(entry.copyFromItemId)
                    ? LDB.items.Select(entry.copyFromItemId)
                    : null;

                if (source == null)
                {
                    ProjectEdenPlugin.Log.LogError($"找不到源建筑 {entry.copyFromItemId}，{entry.displayName} 未注册");

                    continue;
                }

                bool isStation = entry.kind == "station";

                bool isAccumulator = entry.kind == "accumulator";
                bool isExchanger = entry.kind == "exchanger";
                bool isGenerator = entry.kind == "generator";
                bool isMiner = entry.kind == "miner";

                if (!isStation && !isAccumulator && !isExchanger && !isGenerator && !isMiner
                    && entry.recipeType <= 0)
                {
                    ProjectEdenPlugin.Log.LogError($"{entry.displayName} 没有配 recipeType，跳过");

                    continue;
                }

                if (isGenerator && !SourceIsPowerGen(source))
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"{entry.displayName} 配成了 generator，但来源建筑 {entry.copyFromItemId} 的 " +
                        "prefabDesc.isPowerGen 是 false，跳过。来源要选一台发电建筑。");

                    continue;
                }

                if (isAccumulator && !SourceIsAccumulator(source))
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"{entry.displayName} 配成了 accumulator，但来源建筑 {entry.copyFromItemId} 的 " +
                        "prefabDesc.isAccumulator 是 false，跳过。来源要选蓄电器。");

                    continue;
                }

                if (isMiner && !SourceIsVeinMiner(source))
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"{entry.displayName} 配成了 miner，但来源建筑 {entry.copyFromItemId} 的 " +
                        "prefabDesc.minerType 不是 Vein，跳过。来源要选一台采矿机。");

                    continue;
                }

                if (isStation && !SourceIsStation(source))
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"{entry.displayName} 配成了 station，但来源建筑 {entry.copyFromItemId} 的 prefabDesc.isStation 是 false，跳过。" +
                        "想同时要行星内运输机和星际运输船，来源要选星际物流运输站。");

                    continue;
                }

                var machine = new Machine { Entry = entry, SourceIconPath = source.IconPath };

                machine.ItemId = ProtoSlots.ResolveItemId(entry.itemId, entry.displayName);
                machine.ModelId = ProtoSlots.ResolveModelId(entry.modelId, entry.displayName);

                machine.Grid = ProtoSlots.ResolveGridIndex(
                    WantedGrid(entry, source), entry.displayName,
                    ProtoSlots.GridKind.Item, g => Pending(g, ProtoSlots.GridKind.Item));

                // 本分类没有可画的空槽时，退到本 mod 自己那一类（巨型建筑），
                // 而不是硬塞一个没有按钮的槽位——那会让建造栏每帧空引用
                machine.BuildIndex = ProtoSlots.ResolveBuildIndex(
                    WantedBuildIndex(entry, source), entry.displayName, PendingBuild,
                    MegaBuildingRegistry.Config?.buildCategory ?? 0);

                if (machine.ModelId <= 0)
                {
                    ProjectEdenPlugin.Log.LogError($"{entry.displayName} 找不到可用的模型 ID，未注册");

                    continue;
                }

                ProtoSlots.ReserveItemId(machine.ItemId);
                ProtoSlots.ReserveModelId(machine.ModelId);
                ProtoSlots.ReserveGrid(machine.Grid, ProtoSlots.GridKind.Item);
                ProtoSlots.ReserveBuildIndex(machine.BuildIndex);

                CloneModel(machine, source);
                AddItem(machine, source);
                AddFullVariant(machine);

                Machines.Add(machine);

                AddBuildRecipe(machine);

                string what = machine.IsStation
                    ? $"物流站：运输机 {entry.station?.maxDroneCount ?? 0} / 运输船 {entry.station?.maxShipCount ?? 0}" +
                      (entry.station?.courierCount > 0 ? $" / 配送运输机 {entry.station.courierCount}" : "")
                    : machine.IsAccumulator
                        ? "蓄电器"
                        : machine.IsExchanger
                            ? "能量枢纽"
                            : machine.IsGenerator
                                ? "发电设备"
                                : machine.IsMiner
                                    ? $"采矿设备：固定 {entry.miner?.oresPerMinute ?? 0} 矿/分钟"
                                : $"配方类型 {entry.recipeType}（{entry.recipeTypeName}）";

                ProjectEdenPlugin.Log.LogInfo(
                    $"{entry.displayName}已注册：物品 {machine.ItemId}，模型 {machine.ModelId}，{what}，" +
                    $"建造栏第 {machine.BuildIndex / 100} 类第 {machine.BuildIndex % 100} 槽");
            }
        }

        /// <summary>
        /// 这台建筑想要的合成面板格位。
        ///
        /// <c>megaTab</c> 打开时页号取自 <c>MegaBuildingRegistry.TabIndex</c>——
        /// 那是 CommonAPI 在 Awake 里现分配的，<b>写不进 JSON</b>。
        /// 注册顺序上巨型建筑排在本模块之前，所以这时一定已经拿到了。
        /// </summary>
        private static int WantedGrid(MachineEntry entry, ItemProto source)
        {
            if (entry.megaTab && MegaBuildingRegistry.TabIndex > 0)
                return MegaBuildingRegistry.GridIndex(
                    entry.gridRow > 0 ? entry.gridRow : 1,
                    entry.gridCol > 0 ? entry.gridCol : 1);

            if (entry.megaTab)
                ProjectEdenPlugin.Log.LogWarning(
                    $"{entry.displayName} 配了 megaTab，但巨型建筑分页还没注册出来（索引 " +
                    $"{MegaBuildingRegistry.TabIndex}），格位退回源建筑那一页");

            return entry.gridIndex > 0 ? entry.gridIndex : source.GridIndex;
        }

        /// <summary>这台建筑想要的建造栏位置。megaTab 打开时分类号取自 megabuildings.json。</summary>
        private static int WantedBuildIndex(MachineEntry entry, ItemProto source)
        {
            int megaCategory = MegaBuildingRegistry.Config?.buildCategory ?? 0;

            if (entry.megaTab && megaCategory > 0)
                return megaCategory * 100 + (entry.buildSlot > 0 ? entry.buildSlot : 1);

            return entry.buildIndex > 0 ? entry.buildIndex : source.BuildIndex;
        }

        private static bool Pending(int grid, ProtoSlots.GridKind kind)
        {
            var wantItem = kind == ProtoSlots.GridKind.Item;

            for (var i = 0; i < Machines.Count; i++)
                if (wantItem ? Machines[i].Grid == grid : Machines[i].RecipeGrid == grid)
                    return true;

            return false;
        }

        private static bool PendingBuild(int buildIndex)
        {
            for (var i = 0; i < Machines.Count; i++)
                if (Machines[i].BuildIndex == buildIndex)
                    return true;

            return false;
        }

        /// <summary>
        /// 克隆源建筑的 ModelProto 并染色。
        ///
        /// 和 MegaBuildingRegistry.CopyModelProto 同一套路，区别是这里<b>不改写 prefabDesc 的用途</b>
        /// ——源建筑本来就是一台组装机，除了 assemblerRecipeType 之外全部原样继承：
        /// 速度、功耗、传送带槽位、碰撞体都不用自己配。
        /// </summary>
        private static void CloneModel(Machine machine, ItemProto source)
        {
            ModelProto oriModel = LDB.models.Select(source.ModelIndex);

            if (oriModel == null)
            {
                ProjectEdenPlugin.Log.LogError($"找不到源模型 {source.ModelIndex}，{machine.Entry.displayName} 将没有外观");

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
                ID = machine.ModelId,
                Name = machine.ModelId.ToString(),
                SID = "",
            };

            model.sid = "";

            PrefabDesc desc = oriModel.prefabDesc;
            GameObject prefab = desc.prefab ? desc.prefab : Resources.Load<GameObject>(oriModel.PrefabPath);
            GameObject colliderPrefab = desc.colliderPrefab
                ? desc.colliderPrefab
                : Resources.Load<GameObject>(oriModel._colliderPath);

            ref PrefabDesc modelDesc = ref model.prefabDesc;

            modelDesc = prefab == null
                ? PrefabDesc.none
                : colliderPrefab == null
                    ? new PrefabDesc(machine.ModelId, prefab)
                    : new PrefabDesc(machine.ModelId, prefab, colliderPrefab);

            TintMaterials(modelDesc, machine.Entry.tint);

            modelDesc.modelIndex = machine.ModelId;

            // 碰撞体不带过来的话建造预览打不中、拆也拆不掉，和克隆矿脉模型是同一个坑
            modelDesc.hasBuildCollider = desc.hasBuildCollider;
            modelDesc.colliders = desc.colliders;
            modelDesc.buildCollider = desc.buildCollider;
            modelDesc.buildColliders = desc.buildColliders;
            modelDesc.colliderPrefab = desc.colliderPrefab;
            modelDesc.dragBuild = desc.dragBuild;
            modelDesc.dragBuildDist = desc.dragBuildDist;
            modelDesc.blueprintBoxSize = desc.blueprintBoxSize;
            modelDesc.roughHeight = desc.roughHeight;
            modelDesc.roughWidth = desc.roughWidth;
            modelDesc.roughRadius = desc.roughRadius;
            modelDesc.barHeight = desc.barHeight;

            if (machine.IsStation) ApplyStation(modelDesc, machine.Entry.station);
            else if (machine.IsAccumulator) ApplyAccumulator(modelDesc, desc, machine);
            else if (machine.IsExchanger) ApplyExchanger(modelDesc, desc, machine);
            else if (machine.IsGenerator) ApplyGenerator(modelDesc, desc, machine);
            else if (machine.IsMiner) ApplyMiner(modelDesc, desc, machine);
            else
                // 制造设备：这一行就是整件事的目的
                modelDesc.assemblerRecipeType = (ERecipeType)machine.Entry.recipeType;

            LDBTool.PreAddProto(model);
        }

        /// <summary>
        /// 注册蓄电器的「满」版本物品。
        ///
        /// <b>它不是新建筑，是同一台建筑的另一张脸。</b> 原版 2206 / 2207 的 ModelIndex 都是 46，
        /// 所以这里也指向同一个 <c>machine.ModelId</c>——共用 PrefabDesc，
        /// 容量和充放电功率自然一致，不会出现「满版本参数对不上」。
        ///
        /// <b>BuildIndex 必须是 0。</b> 原版满蓄电器就是 0：它不占建造栏槽位，只能从物品栏放下去。
        /// 给它一个真槽位反而危险——那一格若没有对应按钮，建造栏每帧空引用（见 BuildMenuScrollPatches）。
        ///
        /// 热值留 0 就按容量倍率从源物品推：容量放大几倍，一块满电池能放出的能量就是几倍。
        /// </summary>
        private static void AddFullVariant(Machine machine)
        {
            MachineFullVariantEntry full = machine.Entry.accumulator?.fullVariant;

            if (!machine.IsAccumulator || full == null) return;

            ItemProto source = ProtoSlots.ItemIdTaken(full.copyFromItemId)
                ? LDB.items.Select(full.copyFromItemId)
                : null;

            if (source == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"{machine.Entry.displayName}的「满」版本找不到源物品 {full.copyFromItemId}，未注册");

                return;
            }

            machine.FullItemId = ProtoSlots.ResolveItemId(full.itemId, full.displayName);

            int grid = ProtoSlots.ResolveGridIndex(
                full.gridIndex > 0 ? full.gridIndex : source.GridIndex, full.displayName,
                ProtoSlots.GridKind.Item, g => Pending(g, ProtoSlots.GridKind.Item));

            float cap = machine.Entry.accumulator.capacityMultiplier > 0f
                ? machine.Entry.accumulator.capacityMultiplier
                : 1f;

            long heat = full.heatValue > 0L ? full.heatValue : (long)(source.HeatValue * cap);

            var item = new ItemProto
            {
                ID = machine.FullItemId,
                Name = full.displayName,
                Description = full.description,
                Type = source.Type,
                GridIndex = grid,
                StackSize = source.StackSize,
                IconPath = source.IconPath,
                IsFluid = false,
                IsEntity = true,
                CanBuild = true,
                BuildInGas = source.BuildInGas,
                BuildIndex = 0,
                BuildMode = source.BuildMode,
                ModelIndex = machine.ModelId,
                ModelCount = source.ModelCount,
                HpMax = source.HpMax,
                Grade = 0,
                Upgrades = new int[0],
                DescFields = source.DescFields,
                FuelType = full.fuelType > 0 ? full.fuelType : source.FuelType,
                HeatValue = heat,
                // 机甲反应堆功率加成：ratio = ReactorInc + 1，乘到 reactorPowerGen 上
                ReactorInc = full.reactorInc != 0f ? full.reactorInc : source.ReactorInc,
                UnlockKey = -1,
                PreTechOverride = 0,
                prefabDesc = PrefabDesc.none,
            };

            item.name = full.displayName;

            LDBTool.PreAddProto(item);

            ProtoSlots.ReserveItemId(machine.FullItemId);
            ProtoSlots.ReserveGrid(grid, ProtoSlots.GridKind.Item);

            ProjectEdenPlugin.Log.LogInfo(
                $"「{full.displayName}」已注册：物品 {machine.FullItemId}，" +
                $"燃料类型 {item.FuelType}，热值 {Energy(heat)}（源 {Energy(source.HeatValue)} ×{cap:0.##}），" +
                $"机甲功率 ×{item.ReactorInc + 1f:0.##}（源 ×{source.ReactorInc + 1f:0.##}）");
        }

        /// <summary>
        /// 能量枢纽型：只换它服务的那一对空/满，外加功率与能量池倍率。
        ///
        /// <b>为什么是再克隆一台，而不是给原版枢纽打补丁。</b>
        /// <c>PowerExchangerComponent.emptyId / fullId</c> 直接来自
        /// <c>PrefabDesc.emptyId / fullId</c>，整个组件（皮带进出、状态机、能量结算）
        /// 从头到尾只认这两个 ID——<b>一台枢纽天然只服务一对</b>。想支持多对就得把
        /// InternalUpdate 里每一处都接管掉，不划算；再克隆一台正是原版建模这件事的方式。
        /// </summary>
        private static void ApplyExchanger(PrefabDesc target, PrefabDesc source, Machine machine)
        {
            MachineExchangerEntry ex = machine.Entry.exchanger;

            Machine pair = null;

            for (var i = 0; i < Machines.Count; i++)
                if (Machines[i].Key == ex?.pairMachineKey)
                    pair = Machines[i];

            if (pair == null || pair.FullItemId <= 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"{machine.Entry.displayName} 的 pairMachineKey「{ex?.pairMachineKey}」" +
                    "找不到对应的蓄电器，或那台没有配 fullVariant。注册按 machines 数组顺序走，" +
                    "蓄电器那条必须排在枢纽之前。" +
                    $"本台将继续服务原版的那一对（{source.emptyId} / {source.fullId}）");

                return;
            }

            target.emptyId = pair.ItemId;
            target.fullId = pair.FullItemId;

            float energy = ex.energyMultiplier > 0f ? ex.energyMultiplier : 1f;
            float pool = ex.poolMultiplier > 0f ? ex.poolMultiplier : 1f;

            target.exchangeEnergyPerTick = (long)(source.exchangeEnergyPerTick * energy);
            target.maxExcEnergy = (long)(source.maxExcEnergy * pool);

            ProjectEdenPlugin.Log.LogInfo(
                $"{machine.Entry.displayName}：服务「{pair.Entry.displayName}」（空 {target.emptyId} / 满 {target.fullId}），" +
                $"功率 {Power(source.exchangeEnergyPerTick)} → {Power(target.exchangeEnergyPerTick)}（×{energy:0.##}），" +
                $"能量池 {Energy(source.maxExcEnergy)} → {Energy(target.maxExcEnergy)}（×{pool:0.##}）");
        }

        /// <summary>
        /// 「满」版本单独改一次色：它的源是原版满蓄电器，图标本来就画着「充满」，
        /// 用同一组色相参数染过来就自动是「本 mod 配色的满电池」，不用画新图。
        /// </summary>
        private static void TintFullVariant(Machine machine, MachineEntry e)
        {
            MachineFullVariantEntry full = e.accumulator?.fullVariant;

            if (machine.FullItemId <= 0 || full == null) return;

            ItemProto source = LDB.items.Select(full.copyFromItemId);
            ItemProto item = LDB.items.Select(machine.FullItemId);

            if (source?._iconSprite == null || item == null) return;

            Sprite icon = IconTinter.Tint(source._iconSprite, e.iconHue, e.iconSaturationScale,
                e.iconMinSaturation, e.iconValueScale);

            if (icon == null)
            {
                ProjectEdenPlugin.Log.LogWarning($"{full.displayName}的图标改色失败，用的还是源物品的原图");

                return;
            }

            item._iconSprite = icon;
        }

        private static bool SourceIsVeinMiner(ItemProto source)
        {
            ModelProto model = LDB.models.Select(source.ModelIndex);

            return model?.prefabDesc != null && model.prefabDesc.minerType == EMinerType.Vein;
        }

        /// <summary>
        /// 采矿机型：整台克隆，只改功率和自带物流站那一格的容量。
        ///
        /// <b>产量不在这里改。</b> 采矿速度是 <c>MinerComponent.speed</c>，
        /// 那是建造时从 prefabDesc 抄进存档的、而且每 tick 都会被科技和矿脉数放大——
        /// 想钉死它只能在 tick 上每次反解，见 <c>MiniMinerPatches</c>。
        /// 这里写死一个 speed 只会得到「面板上的数对、实际产量随科技涨」。
        ///
        /// <b>矿脉数那一格的容量要在这里给足。</b> <c>StationComponent.Init</c>
        /// 从 <c>stationMaxItemCount</c> 抄 <c>storage[0].max</c>，而那是进存档的；
        /// 给小了之后再改配置，已经建好的那些不会跟着变（第 1 号坑）。
        /// </summary>
        private static void ApplyMiner(PrefabDesc target, PrefabDesc source, Machine machine)
        {
            MachineMinerEntry cfg = machine.Entry.miner;

            // 采矿的那一套标志整组继承：minerType / isVeinCollector / 以及自带的物流站。
            // 漏一个就是「建得起来、一颗矿也不产」，而且不报错。
            target.minerType = source.minerType;
            target.isVeinCollector = source.isVeinCollector;
            target.isStation = source.isStation;
            target.stationMaxItemKinds = source.stationMaxItemKinds;
            target.stationMaxDroneCount = source.stationMaxDroneCount;
            target.stationMaxShipCount = source.stationMaxShipCount;
            target.stationCollectSpeed = source.stationCollectSpeed;
            target.veinMiner = source.veinMiner;
            target.oilMiner = source.oilMiner;

            if (cfg == null) return;

            if (cfg.stationCapacity > 0) target.stationMaxItemCount = cfg.stationCapacity;

            if (cfg.workEnergyWatt > 0)
            {
                target.workEnergyPerTick = cfg.workEnergyWatt / 60L;
                target.idleEnergyPerTick = target.workEnergyPerTick / 20L;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"  {machine.Entry.displayName}：固定产量 {cfg.oresPerMinute} 矿/分钟，" +
                $"工作功率 {cfg.workEnergyWatt / 1000000f:0.##} MW，" +
                $"仓位容量 {target.stationMaxItemCount}，" +
                $"矿脉{(cfg.consumeVeins ? "会" : "不会")}消耗");
        }

        private static bool SourceIsPowerGen(ItemProto source)
        {
            ModelProto model = LDB.models.Select(source.ModelIndex);

            return model != null && model.prefabDesc != null && model.prefabDesc.isPowerGen;
        }

        /// <summary>
        /// 发电设备型：只放大发电功率，发电种类原样继承。
        ///
        /// <b>发电种类的那几个布尔必须显式抄过来。</b>
        /// <c>PowerSystem.NewGeneratorComponent</c> 从 PrefabDesc 逐字段读
        /// photovoltaic / windForcedPower / gammaRayReceiver / geothermal /
        /// genEnergyPerTick / useFuelPerTick / fuelMask / powerCatalystId
        /// （IL 0090~0195）——漏掉哪一个，克隆出来的就变成「一台不发电的发电站」，
        /// 而且没有任何报错，只是电网上永远是 0。
        ///
        /// <b>没有钳位。</b> 风力那一支是
        /// <c>capacityCurrentTick = (long)(windStrength * genEnergyPerTick)</c>
        /// （<c>EnergyCap_Wind</c>，全部指令就这么几条），乘几倍就是几倍。
        /// genEnergyPerTick 是 Int64，倍率再大也不会溢出。
        /// </summary>
        private static void ApplyGenerator(PrefabDesc target, PrefabDesc source, Machine machine)
        {
            MachineGeneratorEntry gen = machine.Entry.generator;

            target.isPowerGen = true;
            target.subId = source.subId;

            target.photovoltaic = source.photovoltaic;
            target.windForcedPower = source.windForcedPower;
            target.gammaRayReceiver = source.gammaRayReceiver;
            target.geothermal = source.geothermal;

            // 燃料类型掩码：配置没写就继承源建筑的。写了就是要把这台机器和原版那台隔开
            target.fuelMask = gen != null && gen.fuelMask > 0 ? gen.fuelMask : source.fuelMask;

            target.powerCatalystId = source.powerCatalystId;
            target.powerProductId = source.powerProductId;
            target.powerProductHeat = source.powerProductHeat;

            float power = gen?.powerMultiplier > 0f ? gen.powerMultiplier : 1f;

            target.genEnergyPerTick = (long)(source.genEnergyPerTick * power);

            // efficiency 优先于 fuelMultiplier：前者写的是意图，后者写的是算好的结果
            if (gen != null && gen.efficiency > 0f)
                target.useFuelPerTick = (long)(target.genEnergyPerTick / gen.efficiency);
            else
                target.useFuelPerTick =
                    (long)(source.useFuelPerTick * (gen?.fuelMultiplier > 0f ? gen.fuelMultiplier : power));

            var line =
                $"{machine.Entry.displayName}：发电 {Power(source.genEnergyPerTick)} → " +
                $"{Power(target.genEnergyPerTick)}（×{power:0.##}）";

            if (source.useFuelPerTick > 0L && target.useFuelPerTick > 0L)
            {
                // 源效率和目标效率都报出来：这一对是 ABN_PowerGenerator 那条红线的参照物，
                // 也是「这台比原版省还是费」唯一能一眼看出来的地方
                double was = (double)source.genEnergyPerTick / source.useFuelPerTick;
                double now = (double)target.genEnergyPerTick / target.useFuelPerTick;

                line += $"，耗料 {Power(source.useFuelPerTick)} → {Power(target.useFuelPerTick)}"
                        + $"，能量利用率 {was:0.###} → {now:0.###}"
                        + $"，燃料掩码 {source.fuelMask} → {target.fuelMask}"
                        + $"，ABN 下限 useFuelPerTick ≥ {target.useFuelPerTick * 0.7:0}";
            }
            else
            {
                line += "，无燃料";
            }

            ProjectEdenPlugin.Log.LogInfo(line);
        }

        private static bool SourceIsAccumulator(ItemProto source)
        {
            ModelProto model = LDB.models.Select(source.ModelIndex);

            return model != null && model.prefabDesc != null && model.prefabDesc.isAccumulator;
        }

        /// <summary>
        /// 蓄电器型：只放大容量和充放电功率，其余原样继承。
        ///
        /// <b>基准取自源建筑运行时的 PrefabDesc，配置里给的是倍率。</b>
        /// 这几个值都在 resources.assets 的预制体里，离线看不到，写死绝对值就是猜；
        /// 乘出来的真值会打进日志。
        ///
        /// <c>isAccumulator</c> 和 <c>subId</c> 必须原样带过来：
        /// PlanetFactory.CreateEntityLogicComponents 按 isAccumulator 决定要不要建
        /// PowerAccumulatorComponent，而 PowerSystem.NewAccumulatorComponent 直接从
        /// PrefabDesc 里取 subId / inputEnergyPerTick / outputEnergyPerTick / maxAcuEnergy。
        /// 注意 curEnergy 恒从 0 起——建出来一定是空的，这是原版行为，不是漏了什么。
        /// </summary>
        private static void ApplyAccumulator(PrefabDesc target, PrefabDesc source, Machine machine)
        {
            MachineAccumulatorEntry acc = machine.Entry.accumulator;

            target.isAccumulator = true;
            target.subId = source.subId;

            float cap = acc?.capacityMultiplier > 0f ? acc.capacityMultiplier : 1f;
            float input = acc?.inputMultiplier > 0f ? acc.inputMultiplier : 1f;
            float output = acc?.outputMultiplier > 0f ? acc.outputMultiplier : 1f;

            target.maxAcuEnergy = (long)(source.maxAcuEnergy * cap);
            target.inputEnergyPerTick = (long)(source.inputEnergyPerTick * input);
            target.outputEnergyPerTick = (long)(source.outputEnergyPerTick * output);

            ProjectEdenPlugin.Log.LogInfo(
                $"{machine.Entry.displayName}：容量 {Energy(source.maxAcuEnergy)} → {Energy(target.maxAcuEnergy)}（×{cap:0.##}），" +
                $"充电 {Power(source.inputEnergyPerTick)} → {Power(target.inputEnergyPerTick)}（×{input:0.##}），" +
                $"放电 {Power(source.outputEnergyPerTick)} → {Power(target.outputEnergyPerTick)}（×{output:0.##}）");
        }

        /// <summary>焦耳转成人读得懂的单位。日志里给绝对值，好照着钉数值</summary>
        private static string Energy(long joule)
        {
            if (joule >= 1000000000L) return $"{joule / 1e9:0.##} GJ";
            if (joule >= 1000000L) return $"{joule / 1e6:0.##} MJ";

            return $"{joule / 1e3:0.##} kJ";
        }

        /// <summary>每 tick 的焦耳换算成功率（60 tick = 1 秒）</summary>
        private static string Power(long joulePerTick)
        {
            double watt = joulePerTick * 60.0;

            if (watt >= 1e9) return $"{watt / 1e9:0.##} GW";
            if (watt >= 1e6) return $"{watt / 1e6:0.##} MW";

            return $"{watt / 1e3:0.##} kW";
        }

        private static bool SourceIsStation(ItemProto source)
        {
            ModelProto model = LDB.models.Select(source.ModelIndex);

            return model != null && model.prefabDesc != null && model.prefabDesc.isStation;
        }

        /// <summary>
        /// 物流站型：只调无人机与运输船的数量。
        ///
        /// <b>isStation / isStellarStation 原样继承，不要动。</b> 两种无人机是
        /// StationComponent 的两半，克隆星际站就已经都有了；关掉 isStellarStation
        /// 反而会把运输船那半去掉。
        /// </summary>
        private static void ApplyStation(PrefabDesc desc, MachineStationEntry station)
        {
            if (station == null) return;

            if (station.maxDroneCount > 0) desc.stationMaxDroneCount = station.maxDroneCount;

            if (station.maxShipCount > 0)
            {
                // idleShipIndices 是 UInt64 位图，按 1L << (index & 63) 索引，超过 64 会绕回去
                int ships = station.maxShipCount > 64 ? 64 : station.maxShipCount;

                if (ships != station.maxShipCount)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"运输船上限 {station.maxShipCount} 超过类型上限，已夹到 {ships}——" +
                        "StationComponent.idleShipIndices 是 64 位位图，再多会互相覆盖");

                desc.stationMaxShipCount = ships;
            }

            if (station.maxItemCount > 0) desc.stationMaxItemCount = station.maxItemCount;
            if (station.maxItemKinds > 0) desc.stationMaxItemKinds = station.maxItemKinds;
            if (station.maxEnergyAcc > 0) desc.stationMaxEnergyAcc = station.maxEnergyAcc;
            if (station.workEnergyPerTick > 0) desc.workEnergyPerTick = station.workEnergyPerTick;

            if (station.courierCount <= 0) return;

            // 第三种无人机：配送运输机。它和前两种不是一个组件——
            // CreateEntityLogicComponents 里 isStation 和 isDispenser 是两个独立的 if，
            // 所以同一个实体可以两个都挂。
            desc.isDispenser = true;
            desc.dispenserMaxCourierCount = station.courierCount;
            desc.dispenserMaxEnergyAcc = station.courierEnergyAcc > 0 ? station.courierEnergyAcc : 30000000L;

            // 配送器的货源必须是一个 StorageComponent（它的 storage 字段就是这个类型），
            // 而物流站的槽位是 StationStore[]，两者类型不同、读不到对方。所以给这台建筑
            // 自带一个缓冲仓，再由 HubCourierPatches 每 tick 把它和 30 个槽位对齐。
            desc.isStorage = true;
            desc.storageCol = station.bufferCols > 0 ? station.bufferCols : 6;
            desc.storageRow = station.bufferRows > 0 ? station.bufferRows : 5;
        }

        /// <summary>材质要先复制再染，否则改的是原版建筑自己的材质。</summary>
        private static void TintMaterials(PrefabDesc desc, float[] tint)
        {
            if (tint == null || tint.Length < 3 || desc.lodMaterials == null) return;

            var color = new Color(tint[0], tint[1], tint[2]);

            foreach (Material[] lod in desc.lodMaterials)
            {
                if (lod == null) continue;

                for (var i = 0; i < lod.Length; i++)
                {
                    ref Material material = ref lod[i];

                    if (material == null) continue;

                    material = new Material(material);
                    material.SetColor("_Color", color);
                }
            }
        }

        private static void AddItem(Machine machine, ItemProto source)
        {
            MachineEntry e = machine.Entry;

            var item = new ItemProto
            {
                ID = machine.ItemId,
                Name = e.displayName,
                Description = e.description,
                Type = source.Type,
                GridIndex = machine.Grid,
                StackSize = source.StackSize,
                // 图标先用源建筑的原图，PostAddData 里再换成改色版本
                IconPath = source.IconPath,
                IsFluid = false,
                IsEntity = true,
                CanBuild = true,
                BuildInGas = source.BuildInGas,
                BuildIndex = machine.BuildIndex,
                BuildMode = source.BuildMode,
                ModelIndex = machine.ModelId,
                ModelCount = source.ModelCount,
                HpMax = source.HpMax,
                Grade = 0,
                Upgrades = new int[0],
                DescFields = source.DescFields,
                // 坑 5：新开局 SetForNewGame 会清空 recipeUnlocked，
                // 只靠配方解锁的话建筑在新档里是不可见的。-1 让 ItemUnlocked 直接返回 true
                UnlockKey = -1,
                PreTechOverride = 0,
                prefabDesc = PrefabDesc.none,
            };

            item.name = e.displayName;

            LDBTool.PreAddProto(item);
        }

        private static void AddBuildRecipe(Machine machine)
        {
            MachineEntry e = machine.Entry;

            if (e.recipeItems == null || e.recipeItems.Length == 0)
            {
                ProjectEdenPlugin.Log.LogWarning($"{e.displayName} 没有配建造配方，做不出来");

                return;
            }

            var items = new int[e.recipeItems.Length];
            var counts = new int[e.recipeItems.Length];

            for (var i = 0; i < e.recipeItems.Length; i++)
            {
                RecipeItemEntry item = e.recipeItems[i];

                // ref 引本 mod 的物品（ores.json 的 items 段 key，或「矿种key.ingot」），
                // id 引原版物品。**别对本 mod 的物品写死 ID**：撞车时会自动顺延，
                // 写死的数字就指到别人家去了，而且一声不吭。
                int id = string.IsNullOrEmpty(item.@ref)
                    ? item.id
                    : OreRegistry.FindItemIdByRef(item.@ref);

                if (!string.IsNullOrEmpty(item.@ref) && id <= 0)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"{e.displayName} 的建造配方里，引用名「{item.@ref}」解析不出物品——" +
                        "它得是 ores.json 里 items 段某条的 key，或者矿种的 key 加 .ore / .ingot 后缀。配方未注册");

                    return;
                }

                if (!ProtoSlots.ItemIdTaken(id))
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"{e.displayName} 的建造配方里，物品 ID {id} 不存在，配方未注册");

                    return;
                }

                items[i] = id;
                counts[i] = item.count > 0 ? item.count : 1;
            }

            machine.RecipeId = ProtoSlots.ResolveRecipeId(e.recipeId, e.displayName);
            machine.RecipeGrid = ProtoSlots.ResolveGridIndex(
                e.recipeGridIndex > 0 ? e.recipeGridIndex : machine.Grid, e.displayName + "（配方）",
                ProtoSlots.GridKind.Recipe, g => Pending(g, ProtoSlots.GridKind.Recipe));

            var recipe = new RecipeProto
            {
                ID = machine.RecipeId,
                Name = e.displayName,
                Description = e.description,
                // Type 为 None(0) 就是「只能手搓」：合成器只看 Handcraft，照样列得出来；
                // 而配方选择器的过滤是 filter != 0 && filter != recipe.Type 就跳过，
                // 任何机器的 assemblerRecipeType 都不会是 0，于是哪台机器都选不到它。
                Type = e.recipeHandcraftOnly ? ERecipeType.None : ERecipeType.Assemble,
                // 建筑要能手搓，否则新开局造不出第一台
                Handcraft = true,
                Explicit = true,
                TimeSpend = e.recipeTimeSpend > 0 ? e.recipeTimeSpend : 180,
                Items = items,
                ItemCounts = counts,
                Results = new[] { machine.ItemId },
                ResultCounts = new[] { 1 },
                GridIndex = machine.RecipeGrid,
                IconPath = machine.SourceIconPath,
                // 无前置科技：preTech 留空，解锁交给 RecipeUnlockPatches
                preTech = null,
            };

            recipe.name = e.displayName;

            LDBTool.PreAddProto(recipe);

            // 和巨型建筑共用那个「免科技解锁」集合
            MegaBuildingRegistry.RecipeIds.Add(machine.RecipeId);

            ProtoSlots.ReserveRecipeId(machine.RecipeId);
            ProtoSlots.ReserveGrid(machine.RecipeGrid, ProtoSlots.GridKind.Recipe);
        }

        // ── LDB 建表之后：核对 ID、图标改色 ──────────────────

        internal static void OnPostAddData()
        {
            foreach (Machine machine in Machines)
            {
                ItemProto item = LDB.items.Select(machine.ItemId);

                // 比的是 Name（原始键）不是 name（译文）：name 走 Name.Translate()，
                // 切到英文就成了 "Electrochemical Plant"，拿它和配置里的中文比会满屏误报
                if (item == null || item.Name != machine.Entry.displayName)
                    ProjectEdenPlugin.Log.LogError(
                        $"「{machine.Entry.displayName}」本应占用物品 ID {machine.ItemId}，" +
                        $"实际那里是「{item?.Name ?? "空"}」。多半是 BepInEx/config/LDBTool/LDBTool.CustomID.cfg " +
                        "里存着旧的绑定——它按名字记 ID 并在本 mod 之后反向覆盖。删掉那一行再重开游戏。");

                TintIcon(machine);
            }
        }

        /// <summary>图标由源建筑的图标改色而来，不需要美术资源。</summary>
        private static void TintIcon(Machine machine)
        {
            MachineEntry e = machine.Entry;

            ItemProto source = LDB.items.Select(e.copyFromItemId);

            Sprite icon = source?._iconSprite == null
                ? null
                : IconTinter.Tint(source._iconSprite, e.iconHue, e.iconSaturationScale,
                    e.iconMinSaturation, e.iconValueScale);

            if (icon == null)
            {
                ProjectEdenPlugin.Log.LogWarning($"{e.displayName}的图标改色失败，用的还是源建筑的原图");

                return;
            }

            ItemProto item = LDB.items.Select(machine.ItemId);
            RecipeProto recipe = machine.RecipeId > 0 ? LDB.recipes.Select(machine.RecipeId) : null;

            if (item != null) item._iconSprite = icon;
            if (recipe != null) recipe._iconSprite = icon;

            TintFullVariant(machine, e);

            ProjectEdenPlugin.Log.LogInfo(
                $"{e.displayName}的图标已由「{source.name}」改色生成" +
                $"（色相 {e.iconHue:0}°，饱和度 ×{e.iconSaturationScale:0.##}，明度 ×{e.iconValueScale:0.##}）");
        }
    }
}
