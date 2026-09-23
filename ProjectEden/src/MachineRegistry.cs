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

        /// <summary>
        /// 配成垃圾箱的那些物流站的物品 ID。<see cref="Patches.Station.DustbinPatches"/> 用。
        ///
        /// 走注册表而不是在补丁里写死号码：物品 ID 由 <see cref="ProtoSlots.ResolveItemId"/>
        /// 解析，撞号时会顺延，写死的号会安静地指到别人身上。
        /// </summary>
        internal static IEnumerable<int> DustbinItemIds
        {
            get
            {
                foreach (Machine machine in Machines)
                    if (machine.IsStation && machine.Entry?.station?.voidItems == true)
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

        /// <summary>
        /// 按 <c>machines.json</c> 的 key 反查这台机器的物品号。
        ///
        /// <b>只看已经注册过的</b>（<c>Machines</c> 是按配置顺序追加的），所以引用的那台
        /// 必须排在前面——和能量枢纽的 <c>pairMachineKey</c> 是同一条约束，理由也一样：
        /// 注册期 LDB 里还没有本 mod 的任何东西，只能靠这份内部账本。
        /// </summary>
        internal static int MachineItemIdByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;

            for (var i = 0; i < Machines.Count; i++)
                if (Machines[i].Entry != null && Machines[i].Entry.key == key)
                    return Machines[i].ItemId;

            return 0;
        }

        /// <summary>
        /// 按 key 反查这台蓄电器的<b>满变体</b>物品号。枢纽要的 <c>fullId</c> 就是它。
        /// 不是蓄电器（<c>accumulator.fullVariant</c> 没配）就返回 0。
        /// </summary>
        internal static int MachineFullItemIdByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;

            for (var i = 0; i < Machines.Count; i++)
                if (Machines[i].Entry != null && Machines[i].Entry.key == key)
                    return Machines[i].FullItemId;

            return 0;
        }

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

        /// <summary>
        /// 把 machines.json 里**手工钉死**的合成面板格位提前登记掉。
        ///
        /// <b>为什么要单开这一趟：钉的时机比钉的值更重要。</b> 注册顺序是
        /// 巨型建筑 → ores.json → 钻头 → 本文件，而 ores.json 里有一批物品的
        /// <c>gridIndex</c> 写的是 0（「让解析器挑」），解析器**从可见区从头扫空格**。
        /// 于是本文件钉的格位还没登记，就已经被前面那批自动分配吃掉了——
        /// <see cref="OnPreAddData"/> 里那句 <c>ReserveGrid</c> 是在**解析之后**才调的，
        /// 登记的是解析结果，救不了被抢的那一格。
        ///
        /// 实测：小型速采机钉在第 2 行第 7 列，被「钒渣油」（自动分配）和
        /// 「钴块 · 甲醇还原」抢走，日志只说「已被占用，改用 3807」——
        /// 读起来像一次成功的回退，实际是这个号从此跟着 ores.json 的物品数量漂。
        ///
        /// <b>只登记显式钉死的，不登记回落值。</b> <c>WantedGrid</c> 在没配 megaTab、
        /// 也没配 gridIndex 时会回落到源建筑的格位——那是原版占着的格子，本来就要挪，
        /// 提前登记它只会平白多一条重复警告。
        ///
        /// 登记本身不挪任何东西（见 <see cref="ProtoSlots.ReserveGrid"/>）：两处手工钉的
        /// 格位真撞了，它会吼，然后由人去改配置。
        /// </summary>
        internal static void PreReserveGrids()
        {
            if (Config == null || !Config.enabled || Config.machines == null) return;

            var pinned = 0;

            foreach (MachineEntry entry in Config.machines)
            {
                if (entry == null || !entry.enabled) continue;

                int grid = PinnedGrid(entry);

                if (grid > 0)
                {
                    ProtoSlots.ReserveGrid(grid, ProtoSlots.GridKind.Item, entry.displayName);
                    pinned++;
                }

                // 建造配方没单配 recipeGridIndex 时跟着物品格位走，和 BuildRecipe 里的回落一致
                int recipeGrid = entry.recipeGridIndex > 0 ? entry.recipeGridIndex : grid;

                if (recipeGrid > 0)
                    ProtoSlots.ReserveGrid(recipeGrid, ProtoSlots.GridKind.Recipe,
                        entry.displayName + "（配方）");
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"machines.json 手工钉死的合成面板格位已提前登记 {pinned} 个"
                + "——注册顺序排在 ores.json 的自动分配之前，否则钉的号会被抢走");
        }

        /// <summary>
        /// 这条配置**显式**要求的物品格位；没有显式要求则 0。
        /// 和 <see cref="WantedGrid"/> 的区别只有一个：不走「回落到源建筑格位」那一支。
        /// </summary>
        private static int PinnedGrid(MachineEntry entry)
        {
            if (entry.megaTab && MegaBuildingRegistry.TabIndex > 0)
                return MegaBuildingRegistry.GridIndex(
                    entry.gridRow > 0 ? entry.gridRow : 1,
                    entry.gridCol > 0 ? entry.gridCol : 1);

            return entry.gridIndex > 0 ? entry.gridIndex : 0;
        }

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

                var machine = new Machine
                {
                    Entry = entry,
                    // 自画图标那条路直接把路径钉在这里，下游三处（物品 / 满变体 / 建造配方）
                    // 全都读它，就不用每处各判一次
                    SourceIconPath = string.IsNullOrEmpty(entry.iconName)
                        ? source.IconPath
                        : "Assets/projecteden/" + entry.iconName,
                };

                machine.ItemId = ProtoSlots.ResolveItemId(entry.itemId, entry.displayName);
                machine.ModelId = ProtoSlots.ResolveModelId(entry.modelId, entry.displayName);

                // mine：PreReserveGrids 替这条提前占下的那一格。不豁免的话它会被自己
                // 写进登记簿的那条挡住，然后挪走——那一格就此谁也用不上（实测挤掉了
                // 双元推进剂和金属浆料燃料的可见格位）。
                machine.Grid = ProtoSlots.ResolveGridIndex(
                    WantedGrid(entry, source), entry.displayName,
                    ProtoSlots.GridKind.Item, g => Pending(g, ProtoSlots.GridKind.Item),
                    0, PinnedGrid(entry));

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
                // 带上名字：PreReserveGrids 用的是同一个名字，同主人再登记一次不算撞车
                ProtoSlots.ReserveGrid(machine.Grid, ProtoSlots.GridKind.Item, entry.displayName);
                ProtoSlots.ReserveBuildIndex(machine.BuildIndex, machine.Entry?.displayName);

                CloneModel(machine, source);
                AddItem(machine, source);
                AddFullVariant(machine);

                Machines.Add(machine);

                AddBuildRecipe(machine);

                string what = machine.IsStation
                    ? (entry.station?.voidItems == true ? "垃圾箱（物流站，槽位每 tick 清空）：" : "物流站：") +
                      $"运输机 {entry.station?.maxDroneCount ?? 0} / 运输船 {entry.station?.maxShipCount ?? 0}" +
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
        /// <summary>
        /// 把占地缩到 <c>footprintCells</c> 格：碰撞体、地基点、粗略尺寸、选中框、蓝图框、
        /// 拖拽间距一起按同一个比例缩。
        ///
        /// <para><b>传送带接口故意不缩。</b> 接口缩了会落到「一格 × k」的倍数上，
        /// 而带子是按<b>整格</b>吸附的——对不上就再也接不上带子，而这件事离线验证不了。
        /// 不缩的代价只是接口留在原来的位置、看着飘在建筑外面。</para>
        ///
        /// <para><b>数组一律先克隆。</b> <c>colliders</c> / <c>buildColliders</c> 是
        /// <see cref="CloneModel"/> 从源建筑<b>按引用</b>接过来的，就地改等于把原版
        /// 物流运输站也一起缩了。<c>ColliderData</c> 是值类型，所以 <c>Clone()</c>
        /// 就是真正的深拷贝。</para>
        /// </summary>
        private static void ScaleFootprint(PrefabDesc desc, MachineEntry e)
        {
            if (desc == null || e.footprintCells <= 0) return;

            float width = desc.buildCollider.ext.x * 2f;

            if (width <= 0.01f)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"{e.displayName} 配了 footprintCells={e.footprintCells}，但源建筑的建造碰撞体是 0，"
                    + "推不出当前几格宽，占地不动");

                return;
            }

            float cur = width / Model.MegaBuildingMeshes.MetresPerCell;
            float k = e.footprintCells / cur;

            // 只缩不放大：这个旋钮是为「太大了」加的，放大只会让它挡住别的建筑
            if (k >= 0.999f)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"{e.displayName} 占地已经是 {cur:0.#} 格，不大于目标 {e.footprintCells} 格，不缩");

                return;
            }

            desc.buildCollider = Shrink(desc.buildCollider, k);

            if (desc.buildColliders != null)
            {
                var copy = (ColliderData[])desc.buildColliders.Clone();

                for (var i = 0; i < copy.Length; i++) copy[i] = Shrink(copy[i], k);

                desc.buildColliders = copy;
            }

            if (desc.colliders != null)
            {
                var copy = (ColliderData[])desc.colliders.Clone();

                for (var i = 0; i < copy.Length; i++) copy[i] = Shrink(copy[i], k);

                desc.colliders = copy;
            }

            if (desc.landPoints != null)
            {
                var copy = (UnityEngine.Vector3[])desc.landPoints.Clone();

                // 地基点也只缩水平：y 是它相对地面的高度偏移
                for (var i = 0; i < copy.Length; i++)
                    copy[i] = new UnityEngine.Vector3(copy[i].x * k, copy[i].y, copy[i].z * k);

                desc.landPoints = copy;
            }

            desc.roughRadius *= k;
            desc.roughWidth *= k;
            desc.blueprintBoxSize *= k;
            desc.dragBuildDist *= k;

            // 选中框同样只缩水平：它的高度由 FitColliderHeight 按模型压
            desc.selectSize = new UnityEngine.Vector3(
                desc.selectSize.x * k, desc.selectSize.y, desc.selectSize.z * k);

            ProjectEdenPlugin.Log.LogInfo(
                $"{e.displayName} 占地已缩：{cur:0.#} 格 → {e.footprintCells} 格（比例 {k:0.###}）。"
                + "碰撞体 / 地基点 / 选中框 / 蓝图框 / 拖拽间距都按这个比例缩了，"
                + "**传送带接口没缩**——接口缩了会落到非整格的位置上，带子就再也接不上了；"
                + "代价是接口留在原来的位置，看着飘在建筑外面");
        }

        /// <summary>
        /// 把一个碰撞体在**水平方向**按 <paramref name="k"/> 缩小，<b>高度不动</b>。
        ///
        /// <para>高度归 <c>MegaBuildingMeshes.FitColliderHeight</c> 管——它按模型的实际
        /// 包围盒去压。两处都改 y 的话就是乘两遍，碰撞体会比模型还矮，
        /// 表现是「从桶顶飞得进去」。<b>占地是横向的事，高度是纵向的事，分开。</b></para>
        ///
        /// <para><c>ColliderData</c> 是值类型，所以按值进出。</para>
        /// </summary>
        private static ColliderData Shrink(ColliderData c, float k)
        {
            c.pos = new UnityEngine.Vector3(c.pos.x * k, c.pos.y, c.pos.z * k);
            c.ext = new UnityEngine.Vector3(c.ext.x * k, c.ext.y, c.ext.z * k);
            c.radius *= k;

            return c;
        }

        /// <summary>
        /// 把这座建筑的**占地参数**打一行出来。自绘模型的建筑各报一次。
        ///
        /// <para><b>为什么要有这一行：「模型小了」和「站的地方小了」是两件事，
        /// 而玩家说「太大」时说的往往是后者。</b> 模型能换，占地换不了——
        /// 底盘、碰撞体、传送带接口全部来自被克隆的原版建筑，
        /// 而这些值住在 <c>resources.assets</c> 里，<b>离线一个都读不到</b>。
        /// 真要缩占地，只能先有这一行把实际数字量出来，再决定按什么比例缩
        /// （而且缩了之后传送带接口 <c>portPoses</c> 必须跟着一起缩，
        /// 否则接口会飘在建筑外面）。</para>
        ///
        /// <para>没有这一行的话，下一轮还是只能靠猜——而这个仓库已经为
        /// 「离线读不到就别猜」付过好几次账了。</para>
        /// </summary>
        private static void ReportFootprintOnce(PrefabDesc desc, string name)
        {
            if (desc == null) return;

            int ports = desc.portPoses?.Length ?? 0;
            int cols = desc.colliders?.Length ?? 0;

            string buildExt = desc.hasBuildCollider
                ? $"{desc.buildCollider.ext}"
                : desc.buildColliders != null && desc.buildColliders.Length > 0
                    ? $"{desc.buildColliders[0].ext}（共 {desc.buildColliders.Length} 个）"
                    : "无";

            // 建造碰撞体的半长 → 整宽 → 格。用的是 MegaBuildingMeshes 里那个推出来的
            // 常数（一格 2π/5 米），所以「模型几格」和「占地几格」是同一把尺
            float cells = desc.buildCollider.ext.x * 2f / Model.MegaBuildingMeshes.MetresPerCell;
            IntVector2 grid = desc.dragBuildGridDistOverride;

            ProjectEdenPlugin.Log.LogInfo(
                $"{name} 占地参数：**建造碰撞体约 {cells:0.#} 格宽**"
                + $"（{desc.buildCollider.ext.x * 2f:0.##} 米 ÷ {Model.MegaBuildingMeshes.MetresPerCell:0.###} 米/格）；"
                + $"dragBuildGridDistOverride {grid.x}×{grid.y}（是 0 就说明源建筑没填这个 override，"
                + "别拿它当格数）；"
                + $"粗略 半径 {desc.roughRadius:0.##} / 宽 {desc.roughWidth:0.##}"
                + $" / 高 {desc.roughHeight:0.##}；建造碰撞体 {buildExt}；物理碰撞体 {cols} 个；"
                + $"传送带接口 {ports} 个；选中框 {desc.selectSize}；蓝图框 {desc.blueprintBoxSize}。"
                + "这一行是**缩完之后**的末态：横向由 footprintCells 缩、纵向由 "
                + "MegaBuildingMeshes.FitColliderHeight 按模型高度压，"
                + "**只有 portPoses 故意没动**（缩了会落到非整格的位置上，带子就接不上了）");
        }

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

            TintMaterials(modelDesc, machine.Entry);

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

            // 自绘模型：只换外观，占地一概不动（理由见 MachineEntry.modelShape）
            if (!string.IsNullOrEmpty(machine.Entry.modelShape) && modelDesc.lodMeshes != null)
            {
                Model.MegaBuildingMeshes.Apply(ref modelDesc, machine.ItemId, machine.Entry.displayName,
                    machine.Entry.modelShape, machine.Entry.modelScale,
                    machine.Entry.modelHeightScale, machine.Entry.modelCells);

                ScaleFootprint(modelDesc, machine.Entry);

                ReportFootprintOnce(modelDesc, machine.Entry.displayName);
            }

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

            // **−1 = 明确「它不是燃料」**，和 0（按容量倍率推）是两件事。
            //
            // 需要这一档是因为倍率一旦大起来，「顺带也是机甲燃料」就变成白送的数值膨胀：
            // 原版满蓄电器 540 MJ 是机甲燃料，容量放大 333 倍之后那就是机甲无限续航。
            // 而容量本身要放大——这一对东西的用途正是「把电打包运走」。
            // 两条轴必须能分开配，否则只能在「电池不够大」和「机甲开挂」之间二选一。
            var notFuel = full.fuelType < 0 || full.heatValue < 0L;

            long heat = notFuel ? 0L
                : full.heatValue > 0L ? full.heatValue
                : (long)(source.HeatValue * cap);

            var item = new ItemProto
            {
                ID = machine.FullItemId,
                Name = full.displayName,
                Description = full.description,
                Type = source.Type,
                GridIndex = grid,
                StackSize = source.StackSize,
                // 「满」变体和本体共用同一张图（machine.SourceIconPath 两条路都已选好）
                IconPath = machine.SourceIconPath,
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
                FuelType = notFuel ? 0 : full.fuelType > 0 ? full.fuelType : source.FuelType,
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
                (notFuel
                    ? "**不作燃料**（配置里显式关掉了：容量倍率一大，机甲燃料那一轴就是白送的数值膨胀）"
                    : $"燃料类型 {item.FuelType}，热值 {Energy(heat)}（源 {Energy(source.HeatValue)} ×{cap:0.##}），"
                      + $"机甲功率 ×{item.ReactorInc + 1f:0.##}（源 ×{source.ReactorInc + 1f:0.##}）"));
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
                // **64 那道类型上限已经不在了**（1.12.14）：idleShipIndices / workShipIndices
                // 那两个 UInt64 位图被 StationShipBank 整体换成了旁挂位图，八个翻位方法前缀
                // 替换 + ShipRenderersOnTick 里两处内联取位转译。位图是派生量，不进存档。
                //
                // 这里还留一个夹子，但它夹的是**内存**不是类型：每艘船一条 ShipData
                // （约 130 字节）加上四个并行数组，一座站按泊位数全额分配，而泊位环的
                // 半径是原版写死的 11.5——泊位越多船贴得越紧。4096 是个说得出理由的头，
                // 不是又一条位宽。
                const int roomCeiling = 4096;

                int ships = station.maxShipCount > roomCeiling ? roomCeiling : station.maxShipCount;

                if (ships != station.maxShipCount)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"运输船上限 {station.maxShipCount} 超过 {roomCeiling}，已夹到 {ships}——" +
                        "这不是位宽限制（那道已经解除），是内存和停泊环的现实：" +
                        "每个泊位要一条 ShipData 加四个并行数组项，而环的半径原版写死 11.5");

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
            // 自带一个中转台，再由 HubCourierPatches 在配送器 tick 前后把它摆满、清空——
            // 它在 tick 之间必然是空的，存储空间仍然只有那 30 个槽位。
            desc.isStorage = true;
            desc.storageCol = station.bufferCols > 0 ? station.bufferCols : 6;
            desc.storageRow = station.bufferRows > 0 ? station.bufferRows : 5;
        }

        private const string ColorProp = "_Color";

        // 下面四个名字是 materialReport 从 VF Shaders/Forward/PBR Standard Mining Drill Mk2
        // 身上量出来的。**这里曾经写的是 _EmissionColor，那个属性根本不存在**——它没有
        // 静默失败只是因为写入有 HasProperty 守卫。别凭印象写属性名。
        private const string AlbedoMulProp = "_AlbedoMultiplier";
        private const string MetallicMulProp = "_MetallicMultiplier";
        private const string EmissionMulProp = "_EmissionMultiplier";
        private const string SpecularProp = "_SpecularColor";
        private const string SmoothMulProp = "_SmoothMultiplier";

        /// <summary>
        /// 加法混合层的强度。**这个才是「叠放会炸」的成因**，而它和上面四个不在同一条
        /// 渲染路径上：那四个属于 PBR 着色器，而这一层是
        /// <c>VF Shaders/Forward/Unlit Additive …</c>——Unlit，不吃光照，
        /// 所以压反照率/金属度/光滑度/镜面色对它完全无效。
        /// </summary>
        private const string AdditiveMulProp = "_Multiplier";

        /// <summary>染过色的机器台数，以及其中被夹取过的台数——开机核对那一行用</summary>
        private static int _tintedCount;

        private static int _tintClampedCount;

        private static int _tintMissingColorProp;

        /// <summary>
        /// 材质要先复制再染，否则改的是原版建筑自己的材质。
        ///
        /// <b>染色通道夹在 1.0 以内，这一条是踩出来的。</b> 小型速采机原本写着
        /// <c>[1.15, 0.62, 0.38]</c>，是全仓库唯一一台超过 1 的；<c>_Color</c> 是反照率
        /// 乘子，大于 1 的表面反射的能量比它接收的还多，单台就顶在泛光阈值上，而玩家
        /// 把几十台速采机叠在同一条矿脉上时（<c>MinerBuildRulePatches</c> 允许重叠建造，
        /// 而这台机器产量固定、本来就是靠叠数量出力的），一小片屏幕里挤满过亮表面，
        /// 泛光把它们糊成白花花的一团。玩家报的「集中反光」就是这个。
        ///
        /// <b>等比压，不逐通道夹。</b> 逐通道 <c>Clamp01</c> 会把 1.15/0.62/0.38 压成
        /// 1.00/0.62/0.38——最大通道降了 13% 而另外两个没动，色相跟着偏，橙色会发粉。
        /// 等比缩放只改明度不改色相。
        ///
        /// <b>而且夹取要点名，不能静默。</b> 配置里写着 1.15、实际生效 1.0，不说出来
        /// 就是下一个人重新调一遍的理由——和「解析器说『我换了个号』就是在报告一个
        /// 不稳定的 ID」是同一条规矩。
        /// </summary>
        private static void TintMaterials(PrefabDesc desc, MachineEntry entry)
        {
            float[] tint = entry?.tint;

            if (tint == null || tint.Length < 3 || desc.lodMaterials == null) return;

            float r = tint[0], g = tint[1], b = tint[2];
            float peak = Mathf.Max(r, Mathf.Max(g, b));

            if (peak > 1f)
            {
                float k = 1f / peak;
                r *= k;
                g *= k;
                b *= k;
                _tintClampedCount++;

                ProjectEdenPlugin.Log.LogWarning(
                    $"材质染色：「{entry.displayName}」的 tint 最大通道是 {peak:0.###}，超过 1。" +
                    "_Color 是反照率乘子——大于 1 的表面反射的能量比它接收的还多，单台就顶在" +
                    "泛光阈值上，叠放时会被泛光糊成一片白。已按等比压到 " +
                    $"[{r:0.###}, {g:0.###}, {b:0.###}]（色相不变）。" +
                    "请把 machines.json 里那一行直接改成这三个值，别留着每局被夹一次。");
            }

            var color = new Color(r, g, b);
            var reported = false;
            // 逐属性统计「几份材质有、几份没有」。
            //
            // **上一版这里是两个 List<string>，报出来的结论是错的。** 一台建筑有多份
            // 材质、可能挂着不同的着色器（实测小型速采机就有：_AlbedoMultiplier 在一份
            // 里基准 1.5、另一份里基准 1，还有几份两个属性都没有）。逐材质记 missing、
            // 却按整台机器下结论，于是日志一边说「已压 _AlbedoMultiplier 1.5→1.001」、
            // 一边说「配了 _AlbedoMultiplier 但没有这个属性，一点没变」——两句都指向
            // 同一台机器，后一句是假的。**报之前先问清楚统计的单位是什么。**
            var applied = new Dictionary<string, string>();
            var hit = new Dictionary<string, int>();
            var miss = new Dictionary<string, int>();

            foreach (Material[] lod in desc.lodMaterials)
            {
                if (lod == null) continue;

                for (var i = 0; i < lod.Length; i++)
                {
                    ref Material material = ref lod[i];

                    if (material == null) continue;

                    material = new Material(material);

                    // LOD 之间未必共用着色器，而 Unity 对不存在的属性是静默忽略的：
                    // 不守卫的话「写了但没看到变化」和「根本没有这个属性」分不开
                    if (material.HasProperty(ColorProp)) material.SetColor(ColorProp, color);
                    else _tintMissingColorProp++;

                    ScaleFloat(material, AlbedoMulProp, entry.albedoScale, applied, hit, miss);
                    ScaleFloat(material, MetallicMulProp, entry.metallicScale, applied, hit, miss);
                    ScaleFloat(material, EmissionMulProp, entry.emissionScale, applied, hit, miss);
                    ScaleFloat(material, SmoothMulProp, entry.smoothScale, applied, hit, miss);
                    ScaleFloat(material, AdditiveMulProp, entry.additiveScale, applied, hit, miss);

                    if (entry.materialScales != null)
                        foreach (KeyValuePair<string, float> pair in entry.materialScales)
                            ScaleAny(material, pair.Key, pair.Value, applied, hit, miss);
                    ScaleColorRgb(material, SpecularProp, entry.specularScale, applied, hit, miss);

                    // **一次只打一份材质是不够的。** 这台建筑挂着不止一个着色器，而属性表
                    // 是逐着色器的——只打第一份，就正好看不见那些「没有这个属性」的材质
                    // 到底是什么。按着色器去重：once 应当是「每一种一次」，不是「每局一次」
                    if (entry.materialReport) reported |= MaterialProbe.DumpOncePerShader(entry.displayName, material);
                }
            }

            ReportBrightness(entry, applied, hit, miss);

            _tintedCount++;
        }

        /// <summary>
        /// 逐属性报告：几份材质压上了、几份没有这个属性。
        ///
        /// <b>只有「一份都没压上」才是警告。</b> 部分材质没有某个属性是正常的——
        /// 一台建筑的不同部件本来就可能挂不同的着色器。
        /// </summary>
        private static void ReportBrightness(MachineEntry entry,
                                             Dictionary<string, string> applied,
                                             Dictionary<string, int> hit,
                                             Dictionary<string, int> miss)
        {
            foreach (KeyValuePair<string, int> pair in miss)
            {
                if (hit.ContainsKey(pair.Key)) continue;

                ProjectEdenPlugin.Log.LogWarning(
                    $"材质亮度：「{entry.displayName}」配了 {pair.Key}，但它的 {pair.Value} 份材质" +
                    "没有一份声明这个属性，那一项一点没变。" +
                    "把这台的 materialReport 打开跑一局，日志会列出每个着色器真正声明的属性名。");
            }

            if (hit.Count == 0) return;

            var sb = new System.Text.StringBuilder();

            sb.Append($"材质亮度：「{entry.displayName}」");

            var first = true;

            foreach (KeyValuePair<string, int> pair in hit)
            {
                if (!first) sb.Append('；');

                first = false;

                int absent = miss.ContainsKey(pair.Key) ? miss[pair.Key] : 0;

                sb.Append($"{pair.Key} 压了 {pair.Value} 份材质（{applied[pair.Key]}）");

                if (absent > 0) sb.Append($"，另有 {absent} 份没有这个属性");
            }

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>
        /// <b>不写</b>才表示「不动」，所以哨兵是 <c>null</c>，不是 0。
        ///
        /// 上一版写的是「0 或 1 表示不动」，于是玩家想把反光关掉、照直在 JSON 里写了 0，
        /// 那一项纹丝不动——<b>0 正是他想要的值，不能同时又兼任「没配置」的标记</b>。
        /// 和「同一个常量既当上限又当哨兵」是同一族的错。
        /// </summary>
        private static bool Wanted(float? scale) => scale.HasValue && !Mathf.Approximately(scale.Value, 1f);

        /// <summary>
        /// 按属性在着色器里声明的<b>类型</b>决定怎么乘：浮点按值，颜色四通道一起乘。
        ///
        /// 颜色连 alpha 一起乘是有理由的：<c>_RimColor</c> 这类效果色的强度常常就写在
        /// alpha 里（实测采矿机玻璃那份是 <c>(1, 1, 1, 0.855)</c>），只乘 RGB 会漏掉一半。
        ///
        /// 类型是现查着色器的属性表，不是猜的——<c>GetColor</c> 打在一个浮点属性上
        /// 返回的是垃圾值，而且不报错。
        /// </summary>
        private static void ScaleAny(Material material, string prop, float scale,
                                     Dictionary<string, string> applied,
                                     Dictionary<string, int> hit, Dictionary<string, int> miss)
        {
            if (!Wanted(scale)) return;

            Shader shader = material.shader;

            if (shader == null || !material.HasProperty(prop))
            {
                Bump(miss, prop);
                return;
            }

            int count = shader.GetPropertyCount();
            var index = -1;

            for (var i = 0; i < count; i++)
            {
                if (shader.GetPropertyName(i) != prop) continue;

                index = i;
                break;
            }

            if (index < 0)
            {
                Bump(miss, prop);
                return;
            }

            switch (shader.GetPropertyType(index))
            {
                case UnityEngine.Rendering.ShaderPropertyType.Color:
                {
                    Color before = material.GetColor(prop);
                    var after = new Color(before.r * scale, before.g * scale,
                                          before.b * scale, before.a * scale);

                    material.SetColor(prop, after);
                    Bump(hit, prop);
                    Note(applied, prop, $"{before.r:0.##}/{before.a:0.##}→{after.r:0.##}/{after.a:0.##}");
                    break;
                }

                case UnityEngine.Rendering.ShaderPropertyType.Float:
                case UnityEngine.Rendering.ShaderPropertyType.Range:
                {
                    float before = material.GetFloat(prop);
                    float after = before * scale;

                    material.SetFloat(prop, after);
                    Bump(hit, prop);
                    Note(applied, prop, $"{before:0.###}→{after:0.###}");
                    break;
                }

                default:
                    // 贴图和向量不在这张表的职责范围内——说出来，别假装成功
                    Bump(miss, prop);
                    break;
            }
        }

        /// <summary>
        /// 同一个属性在不同材质上基准可能不同（实测 <c>_AlbedoMultiplier</c> 有 1.5 也有 1），
        /// 所以记下来的是「见过哪几种」，不是最后一个。
        /// </summary>
        private static void Note(Dictionary<string, string> applied, string prop, string note)
        {
            applied[prop] = applied.ContainsKey(prop) && applied[prop] != note
                ? applied[prop] + " / " + note
                : note;
        }

        private static void Bump(Dictionary<string, int> tally, string key)
        {
            tally[key] = tally.ContainsKey(key) ? tally[key] + 1 : 1;
        }

        private static void ScaleFloat(Material material, string prop, float? scale,
                                       Dictionary<string, string> applied,
                                       Dictionary<string, int> hit, Dictionary<string, int> miss)
        {
            if (!Wanted(scale)) return;

            if (!material.HasProperty(prop))
            {
                Bump(miss, prop);
                return;
            }

            float before = material.GetFloat(prop);
            float after = before * scale.Value;

            material.SetFloat(prop, after);
            Bump(hit, prop);

            Note(applied, prop, $"{before:0.###}→{after:0.###}");
        }

        /// <summary>只缩放 RGB，保留 alpha——alpha 在这些属性上多半另有含义。</summary>
        private static void ScaleColorRgb(Material material, string prop, float? scale,
                                          Dictionary<string, string> applied,
                                          Dictionary<string, int> hit, Dictionary<string, int> miss)
        {
            if (!Wanted(scale)) return;

            if (!material.HasProperty(prop))
            {
                Bump(miss, prop);
                return;
            }

            float k = scale.Value;
            Color before = material.GetColor(prop);
            var after = new Color(before.r * k, before.g * k, before.b * k, before.a);

            material.SetColor(prop, after);
            Bump(hit, prop);

            Note(applied, prop, $"{before.r:0.##}→{after.r:0.##}");
        }

        /// <summary>
        /// 把一台建筑的材质原样打进日志。存在的理由是材质在 <c>resources.assets</c> 里，
        /// 离线一个字都读不到——和 <c>BuildingTexture.MeasuredMetalSmooth</c> 去读原版
        /// 金属度贴图的均值是同一个路子：<b>不知道的东西就让它自己报，别猜。</b>
        /// </summary>
        // 材质探针已经抽到 Utils/MaterialProbe——矿脉那一侧的显示模型也要用同一件工具。
        // 「每种着色器打一次」那条去重也在它那里，所以两边共用同一张已打过的表，
        // 不会为同一个着色器打两遍。

        /// <summary>
        /// 开机核对：染色这一步跑过几台、夹了几台。
        ///
        /// 无条件打印，包括「一台都没夹」那一行——只在出事时才打印的状态行，
        /// 会让「没超标」和「这段代码压根没跑」长得一模一样。本文件为这条规矩
        /// 付过不止一次账。
        /// </summary>
        private static void ReportTintOnce()
        {
            if (_tintedCount == 0)
            {
                ProjectEdenPlugin.Log.LogInfo("材质染色：没有一台机器配了 tint，外观全部沿用源建筑");
                return;
            }

            string clamped = _tintClampedCount == 0
                ? "没有超标的通道"
                : $"其中 {_tintClampedCount} 台的 tint 超过 1 被等比压回（上面有点名）";

            string missing = _tintMissingColorProp == 0
                ? ""
                : $"；另有 {_tintMissingColorProp} 份材质没有 {ColorProp} 属性，那几份没染上色";

            ProjectEdenPlugin.Log.LogInfo($"材质染色：{_tintedCount} 台已染色，{clamped}{missing}");
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
                // ↓ 下面那个 IconPath 读的是 machine.SourceIconPath：
                //   配了 iconName 就是自画那张，否则是源建筑的
                GridIndex = machine.Grid,
                StackSize = source.StackSize,
                // 配了 iconName 就是自画那张，否则先用源建筑的原图、
                // PostAddData 里再换成改色版本（machine.SourceIconPath 已经替两条路选好了）
                IconPath = machine.SourceIconPath,
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

                // **本文件自己的 key 也能引**，比如「以电浆蓄能柜为基础件再加凝核稳定剂」。
                // 只认**先于本条注册**的机器（Machines 是按顺序追加的），和能量枢纽那条
                // pairMachineKey 的约束一样：引用一台还没注册的机器，拿到的会是 0。
                // 没有这一档就只能在建造配方里写死 ID，而本 mod 的物品 ID 撞车时会顺延，
                // 写死的数字会一声不吭地指到别人家去
                if (id <= 0 && !string.IsNullOrEmpty(item.@ref)) id = MachineItemIdByKey(item.@ref);

                if (!string.IsNullOrEmpty(item.@ref) && id <= 0)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"{e.displayName} 的建造配方里，引用名「{item.@ref}」解析不出物品——" +
                        "它得是 ores.json 里 items 段某条的 key、矿种的 key 加 .ore / .ingot 后缀、" +
                        "`vanilla:中文名`，或者 machines.json 里**排在本条之前**的某台机器的 key。配方未注册");

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
            int pinnedRecipe = e.recipeGridIndex > 0 ? e.recipeGridIndex : PinnedGrid(e);

            machine.RecipeGrid = ProtoSlots.ResolveGridIndex(
                e.recipeGridIndex > 0 ? e.recipeGridIndex : machine.Grid, e.displayName + "（配方）",
                ProtoSlots.GridKind.Recipe, g => Pending(g, ProtoSlots.GridKind.Recipe),
                0, pinnedRecipe);

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
            ProtoSlots.ReserveGrid(machine.RecipeGrid, ProtoSlots.GridKind.Recipe,
                e.displayName + "（配方）");
        }

        /// <summary>
        /// 装自画的图标。物品、建造配方、以及「满」变体都要装上——
        /// 少装一处的表现是「面板里有图、合成面板里是白的」，看着像加载失败。
        /// </summary>
        private static void ApplyDrawnIcon(Machine machine, MachineEntry e)
        {
            string path = "Assets/projecteden/" + e.iconName;
            var icon = Resources.Load<Sprite>(path);

            if (icon == null)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"{e.displayName} 配了 iconName「{e.iconName}」，但 {path} 取不到图——"
                    + "检查 assets/icons/ 下有没有这个 .png，以及它有没有被当成嵌入资源编进 DLL"
                    + "（改完 assets 必须重新编译，运行中的游戏读的是 DLL 里那一份）");

                return;
            }

            ItemProto item = LDB.items.Select(machine.ItemId);
            RecipeProto recipe = machine.RecipeId > 0 ? LDB.recipes.Select(machine.RecipeId) : null;

            if (item != null) item._iconSprite = icon;
            if (recipe != null) recipe._iconSprite = icon;

            if (machine.FullItemId > 0)
            {
                ItemProto full = LDB.items.Select(machine.FullItemId);

                if (full != null) full._iconSprite = icon;
            }

            ProjectEdenPlugin.Log.LogInfo($"{e.displayName} 用的是自画图标 {e.iconName}.png");
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

            ReportTintOnce();
        }

        /// <summary>
        /// 图标：配了 <c>iconName</c> 就用自画的那张，否则拿源建筑的图标改色。
        ///
        /// <b>两条路只能走一条，而且自画那条要自己把 sprite 装上。</b>
        /// 改色那条是直接拿源物品**已经加载好的** <c>_iconSprite</c> 去改，所以不需要
        /// 任何预加载；自画那条只有一个路径字符串，得自己 <c>Resources.Load</c>
        /// （<c>TextureResourcesPatches</c> 拦的就是这个前缀）。少了这一步的表现是
        /// <b>图标一片空白</b>——本仓库为「漏了 Preload 就是白图标」记过一次。
        /// </summary>
        private static void TintIcon(Machine machine)
        {
            MachineEntry e = machine.Entry;

            if (!string.IsNullOrEmpty(e.iconName))
            {
                ApplyDrawnIcon(machine, e);

                return;
            }

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
