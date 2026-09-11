using System;
using System.Collections.Generic;
using ProjectEden.Utils;
using UnityEngine;
using xiaoye97;

namespace ProjectEden
{
    /// <summary>
    /// 自定义矿脉的注册器：矿脉 → 矿石（→ 可选的冶炼配方）。
    ///
    /// 这里的每一条都是钴矿那一轮踩出来的，加新矿种前先读一遍：
    ///
    /// <b>1. 不需要 preloader。</b> 加新矿种的常规做法是用 BepInEx patcher 改 EVeinType 枚举
    /// （ProjectGenesis 就是这么干的）。这里不用：EVeinType 是 byte 枚举，(EVeinType)15
    /// 不需要有名字也合法；PrepareWorks 按 LDB.veins 最后一个原型的 ID + 1 分配数组，
    /// GenerateVeins 又按 veinProtos.Length 分配自己那三个，都会自动适配。
    ///
    /// <b>2. 但必须抬循环上界。</b> 原版遍历矿种的上界写死成 15（EVeinType.Max），
    /// 共 7 处，见 OreVeinRangePatches。不抬的话矿脉一颗都不生成。
    ///
    /// <b>3. 矿种编号必须连续。</b> 见 OreEntry 的注释——空号会把星球详情面板打崩。
    ///
    /// <b>4. 外观靠克隆模型 + 染材质。</b> 原版矿脉颜色由着色器按矿种编号取色，
    /// 新矿种它不认识；把颜色打包进 AnimData.state 则要自带 shader。所以给每个新矿种
    /// 克隆一份铁矿脉的 ModelProto（网格贴图照搬），只把复制出来的材质染色。
    /// <b>克隆时必须把碰撞体一起带过来</b>，否则矿脉射线打不中，右键点不到也采不了。
    ///
    /// <b>5. ID 会被 LDBTool 反向覆盖。</b> 它按<b>名字</b>把 ID 记进
    /// BepInEx/config/LDBTool/LDBTool.CustomID.cfg，之后每次开局用文件里的值覆盖代码设的值。
    /// 所以这里注册完还要回头核对一次（VerifyIds），对不上就点名那个文件。
    /// </summary>
    internal static class OreRegistry
    {
        // 原版 ID，写死方便阅读
        private const int IronOreItemId   = 1001; // 铁矿
        private const int IronIngotItemId = 1101; // 铁块
        private const int IronVeinId      = 1;    // 铁矿脉 = EVeinType.Iron

        /// <summary>
        /// 自制图标的虚拟资源前缀。TextureResourcesPatches 前置了 Resources.Load，
        /// 把这个前缀转到嵌入的 assets/icons/&lt;名字&gt;.png，所以只要把 IconPath 写成这个样子，
        /// ItemProto.Preload 就会自己把 _iconSprite 读出来——不用手动塞 Sprite，
        /// 也不怕后面谁又跑一次 ProtoPreload 把它冲掉。
        /// </summary>
        private const string IconPrefix = "Assets/projecteden/";

        private static string IconPathOf(string icon) =>
            string.IsNullOrEmpty(icon) ? null : IconPrefix + icon;

        /// <summary>一个矿种的运行时状态：配置 + 现场解析出来的各种 ID。</summary>
        internal class Ore
        {
            internal OreEntry Entry;

            internal int OreItemId;
            internal int IngotItemId;
            internal int VeinModelId;
            internal int OreGrid;
            internal int IngotGrid;

            /// <summary>矿石最终用的图标路径</summary>
            internal string OreIconPath;

            /// <summary>锭最终用的图标路径：自制图标或铁块的原路径。没有锭时为 null</summary>
            internal string IngotIconPath;

            /// <summary>这个矿种下挂的所有配方，可以是 0 条</summary>
            internal readonly List<Recipe> Recipes = new List<Recipe>();

            internal int VeinId => Entry.veinId;
            internal bool HasIngot => Entry.hasIngot;
            internal string Key => Entry.key ?? Entry.oreName;
        }

        /// <summary>一条配方的运行时状态。</summary>
        internal class Recipe
        {
            internal OreRecipeEntry Entry;

            internal int RecipeId;
            internal int Grid;
        }

        /// <summary>一个额外物品（副产物之类）的运行时状态。</summary>
        internal class ExtraItem
        {
            internal ExtraItemEntry Entry;

            internal int ItemId;
            internal int Grid;

            internal string Key => Entry.key ?? Entry.name;
        }

        internal static OreConfig Config { get; private set; }

        /// <summary>已经注册的矿种，按配置顺序。</summary>
        internal static readonly List<Ore> Ores = new List<Ore>();

        /// <summary>已经注册的额外物品，按配置顺序。</summary>
        internal static readonly List<ExtraItem> ExtraItems = new List<ExtraItem>();

        /// <summary>不属于任何矿种的配方（ores.json 顶层的 recipes 段）。</summary>
        internal static readonly List<Recipe> StandaloneRecipes = new List<Recipe>();

        internal static bool Enabled => Config != null && Config.enabled && Ores.Count > 0;

        /// <summary>这个矿种编号是不是本 mod 加的。</summary>
        internal static bool IsCustomVein(int veinType)
        {
            for (var i = 0; i < Ores.Count; i++)
                if (Ores[i].VeinId == veinType)
                    return true;

            return false;
        }

        internal static Ore Find(int veinType)
        {
            for (var i = 0; i < Ores.Count; i++)
                if (Ores[i].VeinId == veinType)
                    return Ores[i];

            return null;
        }

        /// <summary>最大的矿种编号，给需要按编号开数组的地方用。</summary>
        internal static int MaxVeinId
        {
            get
            {
                var max = 0;

                for (var i = 0; i < Ores.Count; i++)
                    if (Ores[i].VeinId > max)
                        max = Ores[i].VeinId;

                return max;
            }
        }

        internal static void Load()
        {
            Config = JsonHelper.Load<OreConfig>("ores");

            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogWarning("读不到 ores.json，自定义矿脉未启用");

                return;
            }

            if (!Config.enabled) ProjectEdenPlugin.Log.LogInfo("自定义矿脉已在配置里关闭");
        }

        // ── 注册：物品与配方 ──────────────────────────────────

        internal static void OnPreAddData()
        {
            Ores.Clear();
            ExtraItems.Clear();
            StandaloneRecipes.Clear();

            if (Config == null || !Config.enabled) return;

            ItemProto ironOre = LDB.items.Select(IronOreItemId);
            ItemProto ironIngot = LDB.items.Select(IronIngotItemId);

            if (ironOre == null || ironIngot == null)
            {
                ProjectEdenPlugin.Log.LogError($"找不到铁矿（{IronOreItemId}）或铁块（{IronIngotItemId}），自定义矿脉未注册");

                return;
            }

            // 额外物品排在前面：配方按 ref 引用它们，得先有 ID 才解析得出来
            AddExtraItems();

            if (Config.ores == null) return;

            foreach (OreEntry entry in Config.ores)
            {
                if (entry == null || !entry.enabled) continue;

                if (entry.veinId <= 0)
                {
                    ProjectEdenPlugin.Log.LogError($"矿种「{entry.oreName}」没有配 veinId，跳过");

                    continue;
                }

                var ore = new Ore { Entry = entry };

                ore.OreItemId = ResolveItemId(entry.oreItemId, entry.oreName);
                ore.OreGrid = ResolveGridIndex(entry.oreGridIndex, entry.oreName, ProtoSlots.GridKind.Item);

                ore.OreIconPath = IconPathOf(entry.oreIcon) ?? ironOre.IconPath;

                AddItem(ore.OreItemId, entry.oreName, entry.oreDescription, EItemType.Resource,
                    ore.OreGrid, ironOre, entry.stackSize, entry.veinName, "", false, ore.OreIconPath);

                ProtoSlots.ReserveItemId(ore.OreItemId);
                ProtoSlots.ReserveGrid(ore.OreGrid, ProtoSlots.GridKind.Item);

                if (entry.hasIngot)
                {
                    ore.IngotItemId = ResolveItemId(entry.ingotItemId, entry.ingotName);
                    ore.IngotGrid = ResolveGridIndex(entry.ingotGridIndex, entry.ingotName, ProtoSlots.GridKind.Item, ore.OreGrid);

                    ore.IngotIconPath = IconPathOf(entry.ingotIcon) ?? ironIngot.IconPath;

                    AddItem(ore.IngotItemId, entry.ingotName, entry.ingotDescription, EItemType.Material,
                        ore.IngotGrid, ironIngot, entry.stackSize, "", ProduceFrom(entry), false,
                        ore.IngotIconPath);

                    ProtoSlots.ReserveItemId(ore.IngotItemId);
                    ProtoSlots.ReserveGrid(ore.IngotGrid, ProtoSlots.GridKind.Item);
                }

                CloneVeinModel(ore);

                Ores.Add(ore);
            }

            // 配方要等所有矿石和锭都拿到 ID 之后才能解析 ref——
            // 跨矿种引用（比如 A 的锭当 B 的原料）也就跟着能用了
            foreach (Ore ore in Ores) AddRecipes(ore.Entry.recipes, ore);

            // 顶层那批不属于任何矿种的配方，比如电解水
            AddRecipes(Config.recipes, null);

            if (Ores.Count > 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"自定义矿脉已注册 {Ores.Count} 种：{string.Join("、", Ores.ConvertAll(o => $"{o.Entry.veinName}(矿种 {o.VeinId})").ToArray())}");

            VerifyContiguous();
        }

        /// <summary>「制造于」那栏的文字：配了就用配的，没配就按第一条配方的类型推。</summary>
        private static string ProduceFrom(OreEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.ingotProduceFrom)) return entry.ingotProduceFrom;

            if (entry.recipes == null) return "";

            var text = "";

            foreach (OreRecipeEntry recipe in entry.recipes)
            {
                if (recipe == null || !recipe.enabled) continue;

                string machine = MachineName(recipe.type);

                if (machine.Length == 0 || text.Contains(machine)) continue;

                text += text.Length == 0 ? machine : " / " + machine;
            }

            return text;
        }

        private static string MachineName(int recipeType)
        {
            switch ((ERecipeType)recipeType)
            {
                case ERecipeType.Smelt:    return "冶炼厂";
                case ERecipeType.Chemical: return "化工厂";
                case ERecipeType.Refine:   return "精炼厂";
                case ERecipeType.Assemble: return "制造台";
                case ERecipeType.Particle: return "粒子对撞机";

                // 自定义类型（9 电化学 / 10 氧化还原 / 11 生化培养）的机器名在
                // machines.json 和 megabuildings.json 里，不在这个 switch 里
                default: return MachineRegistry.RecipeTypeMachineName(recipeType) ?? "";
            }
        }

        /// <summary>注册 ores.json 的 items 段：不属于任何矿脉的新物品。</summary>
        private static void AddExtraItems()
        {
            if (Config.items == null) return;

            foreach (ExtraItemEntry entry in Config.items)
            {
                if (entry == null || !entry.enabled) continue;

                ItemProto source = ProtoSlots.ItemIdTaken(entry.iconFrom) ? LDB.items.Select(entry.iconFrom) : null;

                if (source == null)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"物品「{entry.name}」的图标来源 {entry.iconFrom} 在原版里不存在，跳过");

                    continue;
                }

                var item = new ExtraItem { Entry = entry };

                item.ItemId = ResolveItemId(entry.itemId, entry.name);
                item.Grid = ResolveGridIndex(entry.gridIndex, entry.name, ProtoSlots.GridKind.Item);

                ItemProto proto = AddItem(item.ItemId, entry.name, entry.description, EItemType.Material,
                    item.Grid, source, entry.stackSize, "", entry.produceFrom, entry.isFluid,
                    IconPathOf(entry.icon) ?? source.IconPath);

                ApplyFuel(proto, entry);

                ProtoSlots.ReserveItemId(item.ItemId);
                ProtoSlots.ReserveGrid(item.Grid, ProtoSlots.GridKind.Item);

                ExtraItems.Add(item);
            }

            if (ExtraItems.Count > 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"额外物品已注册 {ExtraItems.Count} 个：{string.Join("、", ExtraItems.ConvertAll(x => $"{x.Entry.name}({x.ItemId})").ToArray())}");
        }

        /// <summary>
        /// 矿种编号必须从 15 起连续。留洞会让星球详情面板在遍历到空号时空引用崩溃，
        /// 这条实测踩过，所以在注册阶段就吼出来，而不是等玩家打开面板才崩。
        /// </summary>
        private static void VerifyContiguous()
        {
            if (Ores.Count == 0) return;

            var ids = new List<int>();

            foreach (Ore ore in Ores) ids.Add(ore.VeinId);

            ids.Sort();

            int expected = (int)EVeinType.Max; // 15

            foreach (int id in ids)
            {
                if (id == expected)
                {
                    expected++;

                    continue;
                }

                ProjectEdenPlugin.Log.LogError(
                    $"矿种编号不连续：期望 {expected}，配置里是 {id}。" +
                    "原版星球详情面板遍历矿种时会先解引用再判空，空号会让面板一打开就崩。" +
                    "请把 ores.json 里的 veinId 从 15 开始连续排。");

                return;
            }
        }

        // ── 矿脉模型：克隆铁矿脉再染色 ────────────────────────

        private static void CloneVeinModel(Ore ore)
        {
            ore.VeinModelId = 0;

            VeinProto iron = LDB.veins.Select(IronVeinId);
            ModelProto source = iron == null ? null : LDB.models.Select(iron.ModelIndex);

            if (source == null)
            {
                ProjectEdenPlugin.Log.LogWarning($"找不到铁矿脉的模型，{ore.Entry.veinName}将沿用铁矿脉的外观");

                return;
            }

            int id = ProtoSlots.ResolveModelId(ore.Entry.veinModelId, ore.Entry.veinName);

            ProtoSlots.ReserveModelId(id);

            if (id <= 0)
            {
                ProjectEdenPlugin.Log.LogWarning($"没有可用的模型 ID，{ore.Entry.veinName}将沿用铁矿脉的外观");

                return;
            }

            var model = new ModelProto
            {
                ObjectType = source.ObjectType,
                RuinType = source.RuinType,
                RendererType = source.RendererType,
                HpMax = source.HpMax,
                PrefabPath = source.PrefabPath,
                Order = source.Order,
                ID = id,
                Name = id.ToString(),
                SID = "",
            };

            model.sid = "";

            PrefabDesc desc = source.prefabDesc;
            GameObject prefab = desc.prefab ? desc.prefab : Resources.Load<GameObject>(source.PrefabPath);
            GameObject colliderPrefab = desc.colliderPrefab ? desc.colliderPrefab : Resources.Load<GameObject>(source._colliderPath);

            if (prefab == null)
            {
                ProjectEdenPlugin.Log.LogWarning($"铁矿脉模型 {iron.ModelIndex} 没有 prefab，{ore.Entry.veinName}将沿用铁矿脉的外观");

                return;
            }

            ref PrefabDesc target = ref model.prefabDesc;

            // 碰撞体必须一起带过来，否则矿脉射线打不中：右键点不到、也没法手动开采
            target = colliderPrefab == null ? new PrefabDesc(id, prefab) : new PrefabDesc(id, prefab, colliderPrefab);

            target.modelIndex = id;
            target.colliders = desc.colliders;
            target.buildCollider = desc.buildCollider;
            target.buildColliders = desc.buildColliders;
            target.colliderPrefab = desc.colliderPrefab;
            target.hasBuildCollider = desc.hasBuildCollider;
            target.roughHeight = desc.roughHeight;
            target.roughWidth = desc.roughWidth;
            target.roughRadius = desc.roughRadius;

            Color tint = TintColor(ore.Entry);
            var painted = 0;
            var report = new System.Text.StringBuilder();

            foreach (Material[] lodMaterial in target.lodMaterials)
            {
                if (lodMaterial == null) continue;

                for (var j = 0; j < lodMaterial.Length; j++)
                {
                    ref Material material = ref lodMaterial[j];

                    if (material == null) continue;

                    // 先复制：直接改会连原版铁矿脉一起染了
                    material = new Material(material);
                    painted += TintMaterial(material, tint, report);
                }
            }

            LDBTool.PreAddProto(model);

            ore.VeinModelId = id;

            ProjectEdenPlugin.Log.LogInfo(
                $"{ore.Entry.veinName}模型已克隆自铁矿脉模型 {iron.ModelIndex} → {id}，染色 {painted} 个颜色属性" +
                $"（R×{tint.r:0.##} G×{tint.g:0.##} B×{tint.b:0.##}）{report}");

            if (painted == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    "矿脉材质里一个颜色属性都没有，颜色改不掉——上面那行列出了它实际有哪些属性");
        }

        /// <summary>
        /// 把材质里<b>所有颜色属性</b>乘上染色系数。
        ///
        /// 不猜属性名：第一次写死 "_Color" 的结果是「染色 0 份材质」——矿脉材质根本没有这个属性。
        /// 改成问着色器要属性表，有几个颜色就染几个；一个都没有时把完整属性表打进日志。
        /// </summary>
        private static int TintMaterial(Material material, Color tint, System.Text.StringBuilder report)
        {
            Shader shader = material.shader;

            if (shader == null) return 0;

            var painted = 0;
            int count = shader.GetPropertyCount();
            var others = new System.Text.StringBuilder();

            for (var i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                UnityEngine.Rendering.ShaderPropertyType kind = shader.GetPropertyType(i);

                if (kind == UnityEngine.Rendering.ShaderPropertyType.Color)
                {
                    material.SetColor(name, material.GetColor(name) * tint);
                    painted++;

                    continue;
                }

                others.Append(name).Append('(').Append(kind).Append(") ");
            }

            if (painted == 0 && report.Length < 600)
                report.Append(" | 着色器 ").Append(shader.name).Append(" 的属性：").Append(others);

            return painted;
        }

        private static Color TintColor(OreEntry entry)
        {
            float[] t = entry.veinTint;

            return t != null && t.Length >= 3 ? new Color(t[0], t[1], t[2]) : Color.white;
        }

        // ── 物品与配方 ────────────────────────────────────────

        private static ItemProto AddItem(int id, string displayName, string description, EItemType type, int gridIndex,
            ItemProto source, int stackSize, string miningFrom, string produceFrom, bool isFluid,
            string iconPath)
        {
            var item = new ItemProto
            {
                ID = id,
                Name = displayName,
                Description = description,
                Type = type,
                GridIndex = gridIndex,
                StackSize = stackSize > 0 ? stackSize : source.StackSize,
                MiningFrom = miningFrom,
                ProduceFrom = produceFrom,
                // 自制图标就直接指过去（Preload 会经 TextureResourcesPatches 读出来）；
                // 没有自制图标时先指向来源物品的图，保证任何时候都有一张能用的图，
                // 之后在 PostAddData 里换成改色版本
                IconPath = iconPath,
                IsFluid = isFluid,
                CanBuild = false,
                BuildIndex = 0,
                FuelType = 0,
                HeatValue = 0L,
                Grade = 0,
                Upgrades = new int[0],
                // 坑 5：新开局 SetForNewGame 会清空 recipeUnlocked，
                // 只靠配方解锁的话物品在新档里是不可见的。-1 让 ItemUnlocked 直接返回 true
                UnlockKey = -1,
                PreTechOverride = 0,
                DescFields = source.DescFields,
                prefabDesc = PrefabDesc.none,
            };

            item.name = displayName;

            LDBTool.PreAddProto(item);

            return item;
        }

        /// <summary>
        /// 给物品配上可燃属性。
        ///
        /// <b>这里改 proto 是安全的：<c>LDBTool.PreAddProto</c> 只是排队</b>，
        /// 真正写进 LDB 要等到 <c>VFPreload.InvokeOnLoadWorkEnded</c>（IL 0x0E11）那一刻，
        /// 所以排队之后接着改字段完全来得及。
        ///
        /// <b>不用像流体那样自己重建静态表。</b> 原版在预加载期建的
        /// <c>ItemProto.fuelNeeds</c>（<c>InitFuelNeeds</c>，IL 0x08A6）同样早于 mod 物品入表，
        /// 而它正是发电建筑从皮带取燃料时用的过滤数组——但 <b>LDBTool 在自己的
        /// PostAddData 后处理里已经重跑了 <c>InitFuelNeeds</c></b>（连带 InitItemIds /
        /// InitItemIndices / InitRecipeItems / IconSet.Create）。它唯独漏了 InitFluids，
        /// 那个得我们自己补，见 <c>ProjectEdenPlugin.RefreshFluidList</c>。
        /// </summary>
        private static void ApplyFuel(ItemProto item, ExtraItemEntry entry)
        {
            if (item == null || entry.fuelType == 0 && entry.heatValue == 0L) return;

            if (entry.fuelType == 0 || entry.heatValue <= 0L)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"物品「{entry.name}」的 fuelType={entry.fuelType}、heatValue={entry.heatValue} 只配了一半，" +
                    "两个都要有才烧得起来，已忽略");

                return;
            }

            item.FuelType = entry.fuelType;
            item.HeatValue = entry.heatValue;

            ProjectEdenPlugin.Log.LogInfo(
                $"「{entry.name}」可作燃料：类型 {entry.fuelType}，热值 {entry.heatValue / 1000000.0:0.##} MJ");
        }

        /// <summary>
        /// 注册一个矿种下挂的所有配方。
        ///
        /// 原料和产物都走 <see cref="ResolveRef"/>：写 <c>id</c> 就是原版物品，
        /// 写 <c>ref</c> 就是本 mod 的新物品。用引用名而不是写死 ID，是因为新物品的
        /// ID 撞车时会自动顺延，写死的话配方就指向别人家的东西了。
        /// </summary>
        private static void AddRecipes(OreRecipeEntry[] entries, Ore owner)
        {
            if (entries == null) return;

            foreach (OreRecipeEntry entry in entries)
            {
                if (entry == null || !entry.enabled) continue;

                // 原料侧允许为空（零原料配方，比如光合育林）；产物侧不允许
                if (!BuildSide(entry.items, owner, entry.name, "原料", out int[] items, out int[] itemCounts, true)) continue;
                if (!BuildSide(entry.results, owner, entry.name, "产物", out int[] results, out int[] resultCounts)) continue;

                var reg = new Recipe { Entry = entry };

                reg.RecipeId = ResolveRecipeId(entry.recipeId, entry.name);
                reg.Grid = ResolveGridIndex(entry.gridIndex, entry.name, ProtoSlots.GridKind.Recipe);

                var recipe = new RecipeProto
                {
                    ID = reg.RecipeId,
                    Name = entry.name,
                    Description = entry.description,
                    Type = (ERecipeType)entry.type,
                    // 不给手搓：这是产线物品，手搓会让前期直接跳过采矿
                    Handcraft = false,
                    Explicit = true,
                    TimeSpend = entry.timeSpend,
                    Items = items,
                    ItemCounts = itemCounts,
                    Results = results,
                    ResultCounts = resultCounts,
                    GridIndex = reg.Grid,
                    // 没有锭的矿种（配方产物全是原版物品）最后回落到矿石的图标，
                    // 不能落到 null——IconPath 为 null 时 Preload 读不出图，配方格子会是空白
                    IconPath = IconPathOf(entry.icon)
                               ?? (entry.iconFrom > 0 ? LDB.items.Select(entry.iconFrom)?.IconPath : null)
                               ?? owner?.IngotIconPath
                               ?? owner?.OreIconPath,
                    // 无前置科技：preTech 留空，解锁交给 RecipeUnlockPatches
                    preTech = null,
                };

                recipe.name = entry.name;

                LDBTool.PreAddProto(recipe);

                // 和巨型建筑共用那个「免科技解锁」集合
                MegaBuildingRegistry.RecipeIds.Add(reg.RecipeId);

                ProtoSlots.ReserveRecipeId(reg.RecipeId);
                ProtoSlots.ReserveGrid(reg.Grid, ProtoSlots.GridKind.Recipe);

                if (owner != null) owner.Recipes.Add(reg);
                else StandaloneRecipes.Add(reg);

                if (owner == null && string.IsNullOrEmpty(entry.icon) && entry.iconFrom <= 0)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"配方「{entry.name}」不属于任何矿种，又没配 icon / iconFrom，合成面板上那一格会是空白");

                ProjectEdenPlugin.Log.LogInfo(
                    $"配方「{entry.name}」已注册：ID {reg.RecipeId}，{MachineName(entry.type)}，" +
                    $"{Describe(items, itemCounts)} → {Describe(results, resultCounts)}，{entry.timeSpend / 60f:0.##} 秒");
            }
        }

        private static string Describe(int[] ids, int[] counts)
        {
            var text = "";

            for (var i = 0; i < ids.Length; i++)
            {
                if (i > 0) text += " + ";

                text += $"{NameOf(ids[i])}×{counts[i]}";
            }

            return text;
        }

        /// <summary>
        /// 物品名。<b>本 mod 的新物品这时候还没进 LDB</b>（要等 LDBTool 建表），
        /// 所以先查自己这几张表，查不到再去 LDB 里找原版的。
        /// </summary>
        private static string NameOf(int id)
        {
            foreach (ExtraItem extra in ExtraItems)
                if (extra.ItemId == id)
                    return extra.Entry.name;

            foreach (Ore ore in Ores)
            {
                if (ore.OreItemId == id) return ore.Entry.oreName;
                if (ore.HasIngot && ore.IngotItemId == id) return ore.Entry.ingotName;
            }

            return LDB.items.Select(id)?.name ?? id.ToString();
        }

        /// <summary>把配方的一侧（原料或产物）解析成 LDB 要的两个平行数组。</summary>
        private static bool BuildSide(RecipeItemEntry[] side, Ore owner, string recipeName, string label,
            out int[] ids, out int[] counts, bool allowEmpty = false)
        {
            ids = null;
            counts = null;

            if (side == null || side.Length == 0)
            {
                if (!allowEmpty)
                {
                    ProjectEdenPlugin.Log.LogError($"配方「{recipeName}」没有配{label}，跳过");

                    return false;
                }

                // 零原料配方是合法的，但它和「漏写了原料」长得一模一样，所以在这里报一行，
                // 免得一条打字错误变成一台凭空造物的机器。
                // 引擎侧已核对过 IL：AssemblerComponent.InternalUpdate 的缺料检查是
                // for (i = 0; i < requireCounts.Length; i++)，长度为 0 时循环体一次都不进，
                // 「原料不足」那条 return 走不到。
                ProjectEdenPlugin.Log.LogInfo($"配方「{recipeName}」是零原料配方——{label}一格都没有，这是配置里写明的");

                ids = new int[0];
                counts = new int[0];

                return true;
            }

            ids = new int[side.Length];
            counts = new int[side.Length];

            for (var i = 0; i < side.Length; i++)
            {
                int id = ResolveRef(side[i], owner, recipeName, label);

                if (id <= 0) return false;

                ids[i] = id;
                counts[i] = side[i].count > 0 ? side[i].count : 1;
            }

            return true;
        }

        /// <summary>
        /// 一格原料/产物 → 物品 ID。<c>ref</c> 取值：<c>ore</c> 本矿种的矿石、
        /// <c>ingot</c> 本矿种的锭、其余按 items 段和别的矿种的 key 查。
        /// </summary>
        /// <summary>
        /// 按引用名找本 mod 的物品 ID，给别的注册器用（machines.json 的建造配方要引 ores.json 里的物品）。
        /// 语法和配方里的 <c>ref</c> 一致：items 段的 key，或者「矿种key.ore」/「矿种key.ingot」。
        ///
        /// <b>存在的意义是别写死 ID。</b> 新物品的 ID 撞车时会自动顺延，
        /// 写死的那个数字就指到别人家去了，而且不会报错——只会做出一条原料不对的配方。
        /// </summary>
        internal static int FindItemIdByRef(string @ref)
        {
            if (string.IsNullOrEmpty(@ref)) return 0;

            foreach (ExtraItem extra in ExtraItems)
                if (extra.Key == @ref)
                    return extra.ItemId;

            foreach (Ore other in Ores)
            {
                if (@ref == other.Key + ".ore") return other.OreItemId;
                if (@ref == other.Key + ".ingot" && other.HasIngot) return other.IngotItemId;
            }

            return 0;
        }

        private static int ResolveRef(RecipeItemEntry item, Ore owner, string recipeName, string label)
        {
            if (string.IsNullOrEmpty(item.@ref))
            {
                // 存在性判定扫 dataArray，不用 ProtoSet.Select——它对不存在的 ID
                // 并不可靠地返回 null，写错的 ID 会被它放过去
                if (item.id > 0 && ProtoSlots.ItemIdTaken(item.id)) return item.id;

                ProjectEdenPlugin.Log.LogError(
                    $"配方「{recipeName}」的{label}里，物品 ID {item.id} 在原版里不存在，整条配方跳过");

                return 0;
            }

            if (item.@ref == "ore" || item.@ref == "ingot")
            {
                if (owner == null)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"配方「{recipeName}」在顶层 recipes 段里，没有「本矿种」可言，" +
                        $"{label}不能引用 {item.@ref}——请改写成 items 段的 key 或「矿种key.ore」这样的全名。整条配方跳过");

                    return 0;
                }

                if (item.@ref == "ore") return owner.OreItemId;

                if (owner.HasIngot) return owner.IngotItemId;

                ProjectEdenPlugin.Log.LogError(
                    $"配方「{recipeName}」引用了 ingot，但矿种「{owner.Key}」的 hasIngot 是 false，整条配方跳过");

                return 0;
            }

            foreach (ExtraItem extra in ExtraItems)
                if (extra.Key == item.@ref)
                    return extra.ItemId;

            // 跨矿种引用：ref 写别的矿种的 key，加 ".ore" / ".ingot" 后缀
            foreach (Ore other in Ores)
            {
                if (item.@ref == other.Key + ".ore") return other.OreItemId;
                if (item.@ref == other.Key + ".ingot" && other.HasIngot) return other.IngotItemId;
            }

            ProjectEdenPlugin.Log.LogError(
                $"配方「{recipeName}」的{label}里，引用名「{item.@ref}」解析不出物品——" +
                "它得是 ore / ingot、ores.json 里 items 段某条的 key，或者别的矿种的 key 加 .ore / .ingot 后缀。整条配方跳过");

            return 0;
        }

        // ── LDB 建表之后：矿脉、主题、图标 ────────────────────

        internal static void OnPostAddData()
        {
            foreach (ExtraItem extra in ExtraItems)
            {
                VerifyId(extra.ItemId, extra.Entry.name);
                TintExtraIcon(extra);
            }

            ExtendGasThemes();

            if (Ores.Count == 0) return;

            DumpThemes();

            foreach (Ore ore in Ores)
            {
                VerifyIds(ore);
                AddVeinProto(ore);
                ExtendThemes(ore);
                TintIcons(ore);
            }
        }

        /// <summary>
        /// 把配置里的气体投进气态巨星的主题。
        ///
        /// <b>和矿脉是同一个套路，都写 ThemeProto。</b> 生成端只有
        /// <c>PlanetGen.SetPlanetTheme</c> 一处（由 <c>PlanetGen.CreatePlanet</c> 调用），
        /// 它先按 <c>planet.type == EPlanetType.Gas</c> 过滤，然后：
        /// <code>
        /// items[i]  = theme.GasItems[i];                       // 种类原样照抄，无随机
        /// speeds[i] = theme.GasSpeeds[i]
        ///           * (rand.NextDouble() * 0.19090915f + 0.9090909f)   // 约 ±10%
        ///           * PlanetGen.gasCoef
        ///           * Mathf.Pow(planet.star.resourceCoef, 0.3f);
        /// heats[i]  = LDB.items.Select(items[i]).HeatValue;     // ← 直接取物品热值
        /// gasTotalHeat += heats[i] * speeds[i];
        /// </code>
        ///
        /// 三个要点：
        /// <list type="number">
        /// <item><b>两个数组必须等长。</b> 循环跑 <c>GasSpeeds.Length</c> 却索引 <c>items[i]</c>，
        /// GasSpeeds 比 GasItems 长会当场越界。这里统一对齐到同一长度。</item>
        /// <item><b>没有热值的气体对 gasTotalHeat 贡献 0。</b> 那个值是采集器能耗折扣的分母
        /// （见 GasCollectorPatches），所以投放惰性气体不会让采集变快，但也不会出问题
        /// ——原版对分母为 0 有兜底。</item>
        /// <item><b>只对还没生成过的星球生效</b>，已生成的行星把 gasItems 烤进了存档。</item>
        /// </list>
        ///
        /// 投放之后必须调 <see cref="EnsureCollectorSlots"/>，否则新气体可能拿不到储物格。
        /// </summary>
        private static void ExtendGasThemes()
        {
            if (Config?.gases == null) return;

            var any = false;

            foreach (GasEntry entry in Config.gases)
            {
                if (entry == null || !entry.enabled) continue;

                int itemId = FindItemIdByRef(entry.@ref);

                if (itemId <= 0)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"气体配置里的引用名「{entry.@ref}」解析不出物品，未投放。" +
                        "它得是 ores.json 里 items 段某条的 key，或矿种 key 加 .ore / .ingot 后缀");

                    continue;
                }

                ItemProto proto = LDB.items.Select(itemId);
                var themes = 0;
                var speedSum = 0f;

                foreach (ThemeProto theme in LDB.themes.dataArray)
                {
                    // 本来就有气体的主题才是气态巨星；别的主题硬塞也不会被读到
                    if (theme?.GasItems == null || theme.GasItems.Length == 0) continue;

                    if (Array.IndexOf(theme.GasItems, itemId) >= 0) continue;

                    float baseSpeed = 0f;

                    if (theme.GasSpeeds != null)
                        foreach (float speed in theme.GasSpeeds)
                            if (speed > baseSpeed)
                                baseSpeed = speed;

                    if (baseSpeed <= 0f) continue;

                    int index = theme.GasItems.Length;

                    Array.Resize(ref theme.GasItems, index + 1);

                    // 两个数组必须等长：SetPlanetTheme 跑 GasSpeeds.Length 却索引 items[i]
                    float[] speeds = theme.GasSpeeds ?? new float[0];

                    Array.Resize(ref speeds, index + 1);

                    theme.GasItems[index] = itemId;
                    speeds[index] = baseSpeed * (entry.speedRatio > 0f ? entry.speedRatio : 1f);
                    theme.GasSpeeds = speeds;

                    speedSum += speeds[index];
                    themes++;
                }

                if (themes == 0)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"没有任何气态巨星主题被写入「{proto?.name ?? entry.@ref}」，采集器不会产出它" +
                        "——检查 ThemeProto.GasItems 的结构是否变了");

                    continue;
                }

                any = true;

                ProjectEdenPlugin.Log.LogInfo(
                    $"「{proto?.name ?? entry.@ref}」已投放 {themes} 个气态巨星主题" +
                    $"（速率为该主题最高气体的 {entry.speedRatio:0.##} 倍，平均 {speedSum / themes:0.###}/s）；" +
                    (proto != null && proto.HeatValue <= 0L
                        ? "它没有热值，不参与采集器的能耗折扣计算（惰性气体本就如此）；"
                        : "") +
                    "只对还没生成过的星球生效");
            }

            if (any) EnsureCollectorSlots();
        }

        /// <summary>
        /// 保证轨道采集器的储物格够放下最多气体的那个主题。
        ///
        /// <b>这是「加了气体却采不到」的唯一原因。</b> <c>StationComponent.Init</c> 的采集器分支：
        /// <code>
        /// for (int i = 0; i &lt; collectionIds.Length; i++) {
        ///     if (i &gt; _desc.stationMaxItemKinds - 1) break;   // ← 超出上限的气体没有槽位
        ///     storage[i].itemId = collectionIds[i];
        /// }
        /// </code>
        /// 所以格数必须 ≥ 主题里的气体种类数。<b>但也只能提到刚好够</b>：
        /// StationCapacityPatches 特意跳过采集器不给它套 30 格，
        /// 因为格位是按 collectionIds 铺的，多出来的全是空格，还会把 30 格的物流站界面拖到采集器上。
        ///
        /// 只影响<b>之后新建的</b>采集器——storage 数组在建造时就烤进存档了。
        /// </summary>
        private static void EnsureCollectorSlots()
        {
            var maxKinds = 0;

            foreach (ThemeProto theme in LDB.themes.dataArray)
                if (theme?.GasItems != null && theme.GasItems.Length > maxKinds)
                    maxKinds = theme.GasItems.Length;

            if (maxKinds <= 0) return;

            foreach (ItemProto item in LDB.items.dataArray)
            {
                if (item == null) continue;

                ModelProto model = LDB.models.Select(item.ModelIndex);
                PrefabDesc desc = model?.prefabDesc;

                if (desc == null || !desc.isCollectStation) continue;
                if (desc.stationMaxItemKinds >= maxKinds) continue;

                int before = desc.stationMaxItemKinds;

                desc.stationMaxItemKinds = maxKinds;

                ProjectEdenPlugin.Log.LogInfo(
                    $"{item.name} 的储物格 {before} → {maxKinds}，刚好放下气体种类最多的那个主题；" +
                    "不这样做的话，超出原上限的气体拿不到槽位、永远采不到");
            }
        }

        /// <summary>
        /// 核对最终生效的 ID 是不是我们要的那个。
        ///
        /// <b>LDBTool 会在我们之后覆盖 ID。</b> PreAddProto → Bind → IdBind / GridIndexBind
        /// 把每个 mod proto 的 ID 和格位按<b>名字</b>记进
        /// BepInEx/config/LDBTool/LDBTool.CustomID.cfg，之后每次开局用文件里的值反向覆盖。
        /// 也就是说改 ores.json 对「已经被记过一次」的物品是无效的——钴矿石长期占着
        /// 电磁矩阵（6001）不放就是这么来的，改多少次配置都没用。
        /// 这种冲突只表现为「图标串了」，不核对几乎查不出来。
        /// </summary>
        private static void VerifyIds(Ore ore)
        {
            VerifyId(ore.OreItemId, ore.Entry.oreName);

            if (ore.HasIngot) VerifyId(ore.IngotItemId, ore.Entry.ingotName);
        }

        private static void VerifyId(int id, string expected)
        {
            ItemProto actual = LDB.items.Select(id);

            if (actual != null && actual.name == expected) return;

            ProjectEdenPlugin.Log.LogError(
                $"「{expected}」本应占用物品 ID {id}，实际那里是「{actual?.name ?? "空"}」。" +
                "几乎可以肯定是 BepInEx/config/LDBTool/LDBTool.CustomID.cfg 里存着旧的绑定——" +
                $"它按名字记 ID 并在本 mod 之后反向覆盖。把该文件里 [Item] 段的「{expected}」一行删掉" +
                "（连同 CustomGridIndex.cfg 里对应的条目）再重开游戏，LDBTool 会按新值重新写入。");
        }

        // ── ID 与格位解析：统一走 Utils/ProtoSlots ────────────
        //
        // 这几件事在 MachineRegistry 那边也要做，两份复制品意味着
        // 「别用 ProtoSet.Select 判占用」「格位不能翻页也不能扩行」这些坑
        // 只修一边就会漏。实现放在 ProtoSlots，这里只补本模块的待分配清单。

        private static int ResolveItemId(int wanted, string label) =>
            ProtoSlots.ResolveItemId(wanted, label, 6510);

        private static int ResolveRecipeId(int wanted, string label) =>
            ProtoSlots.ResolveRecipeId(wanted, label, 6510);

        private static int ResolveGridIndex(int wanted, string label, ProtoSlots.GridKind kind,
            int alsoAvoid = 0) =>
            ProtoSlots.ResolveGridIndex(wanted <= 0 ? 1601 : wanted, label, kind,
                g => TakenByPending(g, kind), alsoAvoid);

        /// <summary>
        /// 本轮已经分配出去的格位。物品要到 LDBTool 建表时才进 LDB.items，
        /// 只查 LDB 的话第二个矿种会拿到和第一个一样的格位。
        /// </summary>
        private static bool TakenByPending(int grid, ProtoSlots.GridKind kind)
        {
            // 物品格和配方格互不相干，这里也得分开判——混着判等于本轮的配方
            // 把物品能落的格子占掉，正是「物品在物品栏里看不见」那个 bug 的来源之一
            var wantItem = kind == ProtoSlots.GridKind.Item;

            if (wantItem)
                foreach (ExtraItem extra in ExtraItems)
                    if (extra.Grid == grid)
                        return true;

            if (!wantItem)
                foreach (Recipe recipe in StandaloneRecipes)
                    if (recipe.Grid == grid)
                        return true;

            foreach (Ore ore in Ores)
            {
                if (wantItem)
                {
                    if (ore.OreGrid == grid || (ore.HasIngot && ore.IngotGrid == grid)) return true;

                    continue;
                }

                foreach (Recipe recipe in ore.Recipes)
                    if (recipe.Grid == grid)
                        return true;
            }

            return false;
        }

        // ── 矿脉原型 ──────────────────────────────────────────

        /// <summary>
        /// 克隆铁矿脉的 VeinProto。采集特效、音效、采矿机底座全部原样继承，
        /// 只换 ID / 名字 / 产出物 / 模型——出问题的面最小。
        /// </summary>
        private static void AddVeinProto(Ore ore)
        {
            VeinProto iron = LDB.veins.Select(IronVeinId);

            if (iron == null)
            {
                ProjectEdenPlugin.Log.LogError($"找不到铁矿脉原型（{IronVeinId}），{ore.Entry.veinName}未注册");

                return;
            }

            if (LDB.veins.Select(ore.VeinId) != null)
            {
                ProjectEdenPlugin.Log.LogWarning($"矿脉 ID {ore.VeinId} 已被占用（可能是别的 mod），{ore.Entry.veinName}未注册");

                return;
            }

            var vein = new VeinProto
            {
                ID = ore.VeinId,
                Name = ore.Entry.veinName,
                Description = ore.Entry.oreName,
                IconPath = iron.IconPath,
                IconTag = iron.IconTag,
                // 用克隆并染过色的模型；克隆失败时退回铁矿脉的模型，至少看得见
                ModelIndex = ore.VeinModelId > 0 ? ore.VeinModelId : iron.ModelIndex,
                ModelCount = 1,
                CircleRadius = iron.CircleRadius,
                MiningItem = ore.OreItemId,
                MiningTime = iron.MiningTime,
                MiningAudio = iron.MiningAudio,
                MiningEffect = iron.MiningEffect,
                MinerBaseModelIndex = iron.MinerBaseModelIndex,
                MinerCircleModelIndex = iron.MinerCircleModelIndex,
            };

            vein.name = ore.Entry.veinName;

            VeinProtoSet veins = LDB.veins;
            int length = veins.dataArray.Length;

            Array.Resize(ref veins.dataArray, length + 1);
            veins.dataArray[length] = vein;
            veins.OnAfterDeserialize();

            vein.Preload();

            ProjectEdenPlugin.Log.LogInfo(
                $"{ore.Entry.veinName}已注册：矿种 {ore.VeinId}，产出 {ore.OreItemId}，模型 {vein.ModelIndex}");

            // modelIndex > 2 正是期望状态：那类矿脉的外观来自自己的材质，不走「按矿种编号取色」
            if (vein.ModelIndex <= 2)
                ProjectEdenPlugin.Log.LogWarning(
                    $"{ore.Entry.veinName}用的是通用矿堆模型 {vein.ModelIndex}（专属模型克隆失败），外观只能和铁矿脉一样");
        }

        /// <summary>
        /// 给每个「产铁」的主题补一格。
        ///
        /// ThemeProto 的 VeinSpot / VeinCount / VeinOpacity 三个数组下标是<b>矿种 - 1</b>：
        /// GenerateVeins 里是 Array.Copy(theme.VeinSpot, 0, spots, 1, …)，从 1 开始收。
        /// 只挑本来就产铁的主题，气态巨星那种不产矿的不会硬塞。
        /// </summary>
        /// <summary>
        /// 把矿种投进星球主题。两种模式，由 <c>placement.mode</c> 决定：
        ///
        /// <list type="bullet">
        /// <item><b>normal</b>（默认）——占<b>普通矿脉位</b>（<c>ThemeProto.VeinSpot</c>），
        /// 密度按 <c>veinRarity</c> 乘该主题的铁矿脉。适合到处都有的基础矿。</item>
        /// <item><b>rare</b>——占<b>稀有槽</b>（<c>ThemeProto.RareVeins</c>），
        /// 像金伯利矿那样按概率决定「整颗星球有没有」。适合值得跨星系去找的矿。</item>
        /// </list>
        ///
        /// 两种模式都能用 <c>placement.themes</c> 限定主题（按显示名做包含匹配）。
        /// </summary>
        private static void ExtendThemes(Ore ore)
        {
            PlacementEntry place = ore.Entry.placement;

            if (place != null && place.mode == "rare") ExtendRareSlots(ore, place);
            else ExtendNormalSpots(ore, place);
        }

        /// <summary>
        /// 普通矿脉位：给每个「产铁且主题对得上」的主题补一格。
        ///
        /// <c>VeinSpot</c> / <c>VeinCount</c> / <c>VeinOpacity</c> 三个数组的下标是<b>矿种 − 1</b>：
        /// <c>GenerateVeins</c> 里是 <c>Array.Copy(theme.VeinSpot, 0, spots, 1, …)</c>，从 1 开始收。
        /// 只挑本来就产铁的主题，气态巨星那种不产矿的不会硬塞。
        /// </summary>
        private static void ExtendNormalSpots(Ore ore, PlacementEntry place)
        {
            int index = ore.VeinId - 1;
            int ironIndex = IronVeinId - 1;
            int needed = index + 1;

            var themes = 0;
            var skippedByTheme = 0;

            foreach (ThemeProto theme in LDB.themes.dataArray)
            {
                if (theme?.VeinSpot == null || theme.VeinSpot.Length <= ironIndex) continue;

                // 本来就不产铁的主题（气态巨星、部分冰原）不铺
                if (theme.VeinSpot[ironIndex] <= 0) continue;

                if (!ThemeMatches(theme, place?.themes))
                {
                    skippedByTheme++;

                    continue;
                }

                int ironSpot = theme.VeinSpot[ironIndex];
                float ironCount = theme.VeinCount != null && theme.VeinCount.Length > ironIndex ? theme.VeinCount[ironIndex] : 1f;
                float ironOpacity = theme.VeinOpacity != null && theme.VeinOpacity.Length > ironIndex ? theme.VeinOpacity[ironIndex] : 1f;

                Grow(ref theme.VeinSpot, needed);
                Grow(ref theme.VeinCount, needed);
                Grow(ref theme.VeinOpacity, needed);

                int spot = Mathf.RoundToInt(ironSpot * ore.Entry.veinRarity);

                if (spot < 1) spot = 1;

                theme.VeinSpot[index] = spot;
                theme.VeinCount[index] = ironCount * ore.Entry.veinAmountScale;
                theme.VeinOpacity[index] = ironOpacity;

                themes++;
            }

            // 普通矿脉位没有「母星系」那一档概率，那是稀有槽独有的
            if (place != null && !place.birthSystem)
                ProjectEdenPlugin.Log.LogWarning(
                    $"{ore.Entry.veinName} 配了 birthSystem: false，但普通矿脉位没有母星系那一档概率" +
                    "（RareSettings[i*4+1] 是稀有槽独有的），这条会被忽略。要排除母星系请改用 mode: rare");

            if (themes == 0)
                ProjectEdenPlugin.Log.LogError(
                    $"没有任何主题被写入{ore.Entry.veinName}，新星球不会生成它——" +
                    (skippedByTheme > 0
                        ? $"有 {skippedByTheme} 个产铁主题因为 themes 过滤被排除了，八成是主题名写错。日志里有完整主题表，照着改"
                        : "检查 ThemeProto.VeinSpot 的结构是否变了"));
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"{ore.Entry.veinName}已写入 {themes} 个星球主题（普通矿脉位，密度为铁矿脉的 {ore.Entry.veinRarity:0.##} 倍）；只对还没生成过的星球生效");
        }

        /// <summary>
        /// 稀有槽：往 <c>ThemeProto.RareVeins</c> 追加一项，并在 <c>RareSettings</c> 里补 4 个数。
        ///
        /// <b>RareSettings 的步长是 4</b>，含义是从 <c>PlanetAlgorithm.GenerateVeins</c> 读出来的：
        /// <code>
        /// [i*4+0] 非母星系的出现概率
        /// [i*4+1] 母星系专用的出现概率（star.index == 0 时用这一档）
        /// [i*4+2] 出现之后，每再追加一个矿脉位的概率（最多连滚 11 次）
        /// [i*4+3] 储量 / 浓度系数
        /// </code>
        /// 所以「母星系不要有」是<b>原版就支持的数据位</b>，把 [i*4+1] 填 0 即可，不用打补丁。
        ///
        /// 概率还会按行星自身的稀有度指数做幂次修正（<c>1 - pow(1 - chance, 指数)</c>），
        /// 所以这里填的是基准值，实际出现率随星球浮动。
        /// </summary>
        private static void ExtendRareSlots(Ore ore, PlacementEntry place)
        {
            var themes = 0;
            var skippedByTheme = 0;

            foreach (ThemeProto theme in LDB.themes.dataArray)
            {
                if (theme == null || theme.PlanetType == EPlanetType.Gas) continue;

                if (!ThemeMatches(theme, place.themes))
                {
                    skippedByTheme++;

                    continue;
                }

                int[] veins = theme.RareVeins ?? new int[0];

                if (Array.IndexOf(veins, ore.VeinId) >= 0) continue;

                int slot = veins.Length;

                Array.Resize(ref veins, slot + 1);
                veins[slot] = ore.VeinId;
                theme.RareVeins = veins;

                float[] settings = theme.RareSettings ?? new float[0];

                // 必须补齐到 (槽位数 × 4)，少一个都会让 GenerateVeins 读越界
                Array.Resize(ref settings, (slot + 1) * 4);

                float chance = place.chance > 0f ? place.chance : 0.1f;

                settings[slot * 4 + 0] = chance;
                settings[slot * 4 + 1] = place.birthSystem ? chance : 0f;
                settings[slot * 4 + 2] = place.extraChance;
                settings[slot * 4 + 3] = place.richness > 0f ? place.richness : 0.5f;

                theme.RareSettings = settings;

                themes++;
            }

            if (themes == 0)
                ProjectEdenPlugin.Log.LogError(
                    $"没有任何主题被写入{ore.Entry.veinName}（稀有槽）——" +
                    (skippedByTheme > 0
                        ? $"有 {skippedByTheme} 个主题因为 themes 过滤被排除了，八成是主题名写错。日志里有完整主题表，照着改"
                        : "检查 ThemeProto.RareVeins 的结构是否变了"));
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"{ore.Entry.veinName}已写入 {themes} 个星球主题（稀有槽，出现概率 {place.chance:0.###}，" +
                    $"追加概率 {place.extraChance:0.###}，储量系数 {place.richness:0.##}）；" +
                    (place.birthSystem ? "母星系照常刷" : "母星系不刷") + "；只对还没生成过的星球生效");
        }

        /// <summary>
        /// 主题名匹配，<b>包含即算命中</b>（写「熔岩」能同时命中「熔岩」和「潮汐锁定熔岩」）。
        /// 原始名和翻译名都试一遍：<c>DisplayName</c> 可能是本地化键而不是人读的字。
        /// </summary>
        private static bool ThemeMatches(ThemeProto theme, string[] wanted)
        {
            if (wanted == null || wanted.Length == 0) return true;

            string raw = theme.DisplayName ?? "";
            string shown = theme.displayName ?? "";

            foreach (string want in wanted)
            {
                if (string.IsNullOrEmpty(want)) continue;

                if (raw.Contains(want) || shown.Contains(want)) return true;
            }

            return false;
        }

        private static bool _themesDumped;

        /// <summary>
        /// 把主题表打一遍。<b>这不是调试残留，是配置的唯一依据</b>——
        /// ThemeProto 存在 resources.assets 里，离线读不到，
        /// 所以 <c>placement.themes</c> 该写什么名字只能照着这份日志抄。
        /// </summary>
        private static void DumpThemes()
        {
            if (_themesDumped || LDB.themes?.dataArray == null) return;

            _themesDumped = true;

            ProjectEdenPlugin.Log.LogInfo("── 星球主题表（placement.themes 照这里的名字写）──");

            int ironIndex = IronVeinId - 1;

            foreach (ThemeProto theme in LDB.themes.dataArray)
            {
                if (theme == null) continue;

                bool iron = theme.VeinSpot != null && theme.VeinSpot.Length > ironIndex && theme.VeinSpot[ironIndex] > 0;
                int rare = theme.RareVeins?.Length ?? 0;
                string rareList = rare > 0
                    ? "（矿种 " + string.Join(",", Array.ConvertAll(theme.RareVeins, x => x.ToString())) + "）"
                    : "";

                ProjectEdenPlugin.Log.LogInfo(
                    $"  [{theme.ID,3}] {theme.displayName ?? theme.DisplayName,-14} 类型 {theme.PlanetType,-7} " +
                    $"产铁 {(iron ? "是" : "否")}　稀有槽 {rare} 个{rareList}");
            }
        }

        private static void Grow(ref int[] array, int length)
        {
            if (array == null) array = new int[length];
            else if (array.Length < length) Array.Resize(ref array, length);
        }

        private static void Grow(ref float[] array, int length)
        {
            if (array == null) array = new float[length];
            else if (array.Length < length) Array.Resize(ref array, length);
        }

        // ── 图标：由铁的对应图标改色 ──────────────────────────

        /// <summary>
        /// 源 sprite 取自<b>原版</b>物品／矿脉，它们一定已经 Preload 过，
        /// 所以不依赖本 mod 自己的 Preload 时机。
        ///
        /// 矿脉图标染的是<b>铁矿脉自己的图标</b>（480×480 的矿簇图），不是铁矿石那个
        /// 80×80 的物品图标——尺寸形状都不一样，混用会在面板里对不齐。
        ///
        /// 只染 _iconSprite，不动 _iconSprite80px：后者由 IconSet 用 Graphics.CopyTexture
        /// 拷进共享图集，对贴图格式有要求，换成自建贴图有失败风险，而它只影响地图小图标。
        /// </summary>
        private static void TintIcons(Ore ore)
        {
            OreEntry e = ore.Entry;

            Sprite oreIcon = Tint(LDB.items.Select(IronOreItemId)?._iconSprite, e);

            if (oreIcon == null)
            {
                ProjectEdenPlugin.Log.LogWarning($"铁矿石的图标改色失败，{e.oreName}用的还是铁的原图");

                return;
            }

            // 矿石：配了自制图标就跳过——IconPath 已经指到那张 PNG，
            // Preload 会自己把 _iconSprite 读出来，这里再覆盖就把它盖掉了。
            // 矿脉图标不受影响：它染的是铁矿脉那张 480×480 的矿簇图，另一条路。
            if (string.IsNullOrEmpty(e.oreIcon))
            {
                ItemProto oreItem = LDB.items.Select(ore.OreItemId);

                if (oreItem != null) oreItem._iconSprite = oreIcon;
            }

            VeinProto ironVein = LDB.veins.Select(IronVeinId);
            Sprite veinIcon = Tint(ironVein?._iconSprite, e) ?? oreIcon;
            VeinProto vein = LDB.veins.Select(ore.VeinId);

            if (vein != null) vein._iconSprite = veinIcon;

            // 锭：配了自制图标就什么都不做——IconPath 已经指到那张 PNG，
            // Preload 会自己把 _iconSprite 读出来，这里再覆盖反而把它盖掉
            if (ore.HasIngot && string.IsNullOrEmpty(e.ingotIcon))
            {
                Sprite ingotIcon = Tint(LDB.items.Select(IronIngotItemId)?._iconSprite, e) ?? oreIcon;
                ItemProto ingot = LDB.items.Select(ore.IngotItemId);

                if (ingot != null) ingot._iconSprite = ingotIcon;

                // 配方图标默认跟着锭走；配了 icon / iconFrom 的那些自己有 IconPath，
                // Preload 已经处理过，这里跳过
                foreach (Recipe reg in ore.Recipes)
                {
                    if (!string.IsNullOrEmpty(reg.Entry.icon) || reg.Entry.iconFrom > 0) continue;

                    RecipeProto proto = LDB.recipes.Select(reg.RecipeId);

                    if (proto != null) proto._iconSprite = ingotIcon;
                }
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"{e.oreName}的图标已由铁的对应图标改色生成（色相 {e.iconHue:0}°，饱和度 ×{e.iconSaturationScale:0.##}，明度 ×{e.iconValueScale:0.##}）");
        }

        /// <summary>额外物品的图标：由它自己配的那个原版物品的图标改色而来。</summary>
        private static void TintExtraIcon(ExtraItem extra)
        {
            ExtraItemEntry e = extra.Entry;

            // 自制图标走 IconPath + Preload，不需要在这里改色
            if (!string.IsNullOrEmpty(e.icon)) return;

            Sprite source = LDB.items.Select(e.iconFrom)?._iconSprite;

            if (source == null)
            {
                ProjectEdenPlugin.Log.LogWarning($"{e.name}的图标来源 {e.iconFrom} 没有 iconSprite，沿用原图");

                return;
            }

            Sprite icon = IconTinter.Tint(source, e.iconHue, e.iconSaturationScale, e.iconMinSaturation, e.iconValueScale);

            if (icon == null)
            {
                ProjectEdenPlugin.Log.LogWarning($"{e.name}的图标改色失败，沿用原图");

                return;
            }

            ItemProto item = LDB.items.Select(extra.ItemId);

            if (item != null) item._iconSprite = icon;

            ProjectEdenPlugin.Log.LogInfo(
                $"{e.name}的图标已由「{LDB.items.Select(e.iconFrom)?.name}」改色生成" +
                $"（色相 {e.iconHue:0}°，饱和度 ×{e.iconSaturationScale:0.##}，明度 ×{e.iconValueScale:0.##}）");
        }

        private static Sprite Tint(Sprite source, OreEntry e) =>
            source == null
                ? null
                : IconTinter.Tint(source, e.iconHue, e.iconSaturationScale, e.iconMinSaturation, e.iconValueScale);
    }
}
