#pragma warning disable 649 // 配置类的字段由 JSON 反序列化赋值

using System;
using System.Collections.Generic;
using ProjectEden.Utils;
using xiaoye97;

namespace ProjectEden.Patches
{
    /// <summary>alienvein.json 的 <c>bit</c> 段。</summary>
    [Serializable]
    internal class AlienVeinBitEntry
    {
        public int itemId;
        public string name;
        public string description;
        public string icon;
        public int gridIndex;
        public int stackSize;
        public string produceFrom;

        /// <summary>展开出来的配方从这个号往后排。</summary>
        public int recipeIdBase;

        public int recipeType;
        public int timeSpend;
    }

    /// <summary>
    /// 钻头：<b>一条配方模板 + 一个谓词，在注册时展开成一条条具体配方</b>。
    ///
    /// <b>这是 CLAUDE.md 「已知缺口」里那条「设计了但还没写」的东西第一次落地。</b>
    /// 那里写着：谓词驱动的配方<b>必须在注册时展开成一条配方一种材料，绝不能运行时求值</b>
    /// ——因为 <c>RecipeProto.Items</c> 是 <c>int[]</c>，表达不了「任何硬度 ≥ X 的材料」，
    /// 而运行时的消费钩子还得骗过分拣器插入、<c>UpdateNeeds</c> 和已经进了存档的值。
    ///
    /// 所以这里就按那条做：读 metals.json 的四维，对每种材料算一遍谓词，
    /// <b>合格的各生成一条配方</b>。代价是合成面板上多几个格子，换来的是
    /// 不用第六种面板模式、不用逐台存档状态，而且每种材料各占一格、更好发现。
    ///
    /// <b>四维的差别搬了家。</b> 钻头本身是统一的（一个能挖
    /// <see cref="AlienVeinPatches.BitCapacity"/> 矿），变的是<b>做一个要投多少料</b>：
    /// <c>ceil(capacity / 该材料的产量)</c>。于是最好的材料投 1 个——
    /// 那是算出来的，不是定出来的，因为 <c>capacity</c> 就取自最高的那个产量。
    ///
    /// <b>为什么在 PreAddData 而不是 PostAddData。</b> 物品和配方都得在 LDBTool 建表之前
    /// 排队进去。这意味着不能用 <see cref="MetalPropertyPatches"/>（它在 PostAddData 才解析），
    /// 得直接读 metals.json 的配置并自己解析 <c>ref</c>——所以本类必须排在
    /// <c>OreRegistry.OnPreAddData</c> 之后，那时候矿石和锭的 ID 才拿得到。
    /// </summary>
    internal static class DrillBitRegistry
    {
        internal class BitRecipe
        {
            internal int MaterialId;
            internal string MaterialName;
            internal float Yield;
            internal int Count;
            internal int RecipeId;
        }

        /// <summary>钻头物品的 ID，0 表示没注册成。</summary>
        internal static int BitItemId { get; private set; }

        /// <summary>展开出来的配方，日志与文档用。</summary>
        internal static readonly List<BitRecipe> Recipes = new List<BitRecipe>();

        internal static void OnPreAddData()
        {
            BitItemId = 0;
            Recipes.Clear();

            AlienVeinConfig cfg = AlienVeinPatches.Config;

            // 报无聊的那一面：三种「没生效」在日志里要分得清
            if (cfg == null || cfg.bit == null)
            {
                ProjectEdenPlugin.Log.LogInfo("钻头：alienvein.json 里没有 bit 段，跳过");
                return;
            }

            if (!cfg.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo("钻头：外星矿脉关着，跳过");
                return;
            }

            if (!Evaluate(cfg, out float capacity))
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "钻头：没有任何材料够格，一条配方都不会有——这种矿脉将完全挖不动。"
                    + "检查 alienvein.json 的 slack，或给候选材料补 metals.json 的四维行");

                return;
            }

            AlienVeinPatches.SetBitCapacity(capacity);

            AddItem(cfg.bit);
            AddRecipes(cfg.bit, capacity);
        }

        /// <summary>
        /// 对 metals.json 里每一行跑一遍谓词。
        ///
        /// <b>读的是配置不是 <c>MetalPropertyPatches</c>：</b> 那边要等 PostAddData 才解析完，
        /// 而配方必须在 PreAddData 排队。两边用的是同一份数据，所以不会分叉。
        /// </summary>
        private static bool Evaluate(AlienVeinConfig cfg, out float capacity)
        {
            capacity = 0f;

            MetalsConfig metals = ProjectEdenPlugin.MetalsConfig;

            if (metals?.metals == null) return false;

            // 矿石自己的硬度：谓词的基准
            float oreHardness = AxisOf(metals, cfg.veinRef + ".ore", "hardness");

            if (oreHardness <= 0f)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"钻头：metals.json 里找不到「{cfg.veinRef}.ore」的硬度，谓词算不出来");

                return false;
            }

            float floor = oreHardness - cfg.slack;

            foreach (MetalEntry metal in metals.metals)
            {
                if (metal?.values == null) continue;

                int itemId = string.IsNullOrEmpty(metal.@ref)
                    ? metal.itemId
                    : OreRegistry.FindItemIdByRef(metal.@ref);

                if (itemId <= 0) continue;

                // 矿石自己不能当做自己的钻头材料的前提是它够硬——这里不特判，
                // 谓词说够格就够格（碳化硅本来就是磨料，这是自然结果不是特例）
                if (!metal.values.TryGetValue("hardness", out int h)) continue;
                if (!metal.values.TryGetValue("toughness", out int t)) continue;

                float margin = h - floor;

                if (margin <= 0f || t <= 0) continue;

                var y = (float)(cfg.@base
                                * Math.Pow(margin, cfg.hardExponent)
                                * Math.Pow(t / cfg.toughnessRef, cfg.toughnessExponent));

                if (y < 1f) continue;

                Recipes.Add(new BitRecipe
                {
                    MaterialId = itemId,
                    MaterialName = metal.name,
                    Yield = y,
                });

                if (y > capacity) capacity = y;
            }

            if (Recipes.Count == 0) return false;

            // 产量高的排前面，合成面板上的顺序也就从好到差
            Recipes.Sort((a, b) => b.Yield.CompareTo(a.Yield));

            foreach (BitRecipe r in Recipes)
            {
                var n = (int)Math.Ceiling(capacity / r.Yield);
                r.Count = n < 1 ? 1 : n;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"── 钻头：矿石硬度 {oreHardness:0}，硬度下限 {floor:0}"
                + $"，一个钻头能挖 {capacity:N0} 矿（取自最好的那种材料）──");

            return true;
        }

        private static float AxisOf(MetalsConfig metals, string @ref, string axis)
        {
            foreach (MetalEntry metal in metals.metals)
                if (metal?.values != null && metal.@ref == @ref
                    && metal.values.TryGetValue(axis, out int v))
                    return v;

            return 0f;
        }

        private static void AddItem(AlienVeinBitEntry bit)
        {
            BitItemId = ProtoSlots.ResolveItemId(bit.itemId, bit.name);

            int grid = ProtoSlots.ResolveGridIndex(bit.gridIndex, bit.name, ProtoSlots.GridKind.Item);

            var item = new ItemProto
            {
                ID = BitItemId,
                Name = bit.name,
                Description = bit.description,
                Type = EItemType.Component,
                GridIndex = grid,
                StackSize = bit.stackSize > 0 ? bit.stackSize : 300,
                ProduceFrom = bit.produceFrom,
                IconPath = "Assets/projecteden/" + bit.icon,
                IsFluid = false,
                CanBuild = false,
                FuelType = 0,
                HeatValue = 0L,
                Grade = 0,
                Upgrades = new int[0],
                // 坑 5：新开局 SetForNewGame 会清空 recipeUnlocked，
                // 只靠配方解锁的话物品在新档里是不可见的。-1 让 ItemUnlocked 直接返回 true
                UnlockKey = -1,
                PreTechOverride = 0,
                prefabDesc = PrefabDesc.none,
            };

            item.name = bit.name;

            LDBTool.PreAddProto(item);

            ProtoSlots.ReserveItemId(BitItemId);
            ProtoSlots.ReserveGrid(grid, ProtoSlots.GridKind.Item);

            ProjectEdenPlugin.Log.LogInfo($"钻头已注册：物品 {BitItemId}");
        }

        private static void AddRecipes(AlienVeinBitEntry bit, float capacity)
        {
            for (var i = 0; i < Recipes.Count; i++)
            {
                BitRecipe r = Recipes[i];
                string name = bit.name + " · " + r.MaterialName;

                r.RecipeId = ProtoSlots.ResolveRecipeId(bit.recipeIdBase + i, name);

                int grid = ProtoSlots.ResolveGridIndex(bit.gridIndex, name, ProtoSlots.GridKind.Recipe);

                var recipe = new RecipeProto
                {
                    ID = r.RecipeId,
                    Name = name,
                    Description = $"用{r.MaterialName}做钻冠。越硬越耐磨的材料，做一个钻头用得越少。",
                    Type = (ERecipeType)bit.recipeType,
                    // 不给手搓：外星矿脉是后期内容，手搓钻头等于绕过整条产线
                    Handcraft = false,
                    Explicit = true,
                    TimeSpend = bit.timeSpend,
                    Items = new[] { r.MaterialId },
                    ItemCounts = new[] { r.Count },
                    Results = new[] { BitItemId },
                    ResultCounts = new[] { 1 },
                    GridIndex = grid,
                    IconPath = "Assets/projecteden/" + bit.icon,
                    // 无前置科技：解锁交给 RecipeUnlockPatches
                    preTech = null,
                };

                recipe.name = name;

                LDBTool.PreAddProto(recipe);

                // 和巨型建筑共用那个「免科技解锁」集合
                MegaBuildingRegistry.RecipeIds.Add(r.RecipeId);

                ProtoSlots.ReserveRecipeId(r.RecipeId);
                ProtoSlots.ReserveGrid(grid, ProtoSlots.GridKind.Recipe);

                ProjectEdenPlugin.Log.LogInfo(
                    $"  {r.MaterialName}({r.MaterialId}) ×{r.Count} → 钻头 ×1"
                    + $"（该材料每钻头 {r.Yield:N0} 矿）");
            }
        }
    }
}
