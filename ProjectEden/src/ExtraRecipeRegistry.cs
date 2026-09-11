#pragma warning disable 649 // 配置字段由 JSON 反序列化赋值

using System;
using System.Text;
using ProjectEden.Utils;
using xiaoye97;

namespace ProjectEden
{
    /// <summary>
    /// 额外配方：把原版某条配方原样克隆一份、只改名字和配方类型，
    /// 让别的机器也能做同一件事。
    ///
    /// 为什么是克隆而不是从头写：DSP 的配方选择器按<b>单一</b> ERecipeType 过滤
    /// （UIRecipePicker.RefreshIcons 比 recipe.Type，UIAssemblerWindow 传的是
    /// prefabDesc.assemblerRecipeType），一台机器只认一种类型。想让化工类机器做精炼类
    /// 的活，要么改选择器和 AssemblerComponent.SetRecipe 支持多类型，要么就是这里
    /// 这个办法——加一条化工类型、内容一模一样的配方。后者简单得多，也不动原版判定。
    ///
    /// 源配方不写死 ID，而是按「类型 + 产物」现找，找不到就 loud-fail 并打印原因。
    /// 图标、描述、耗时、增产属性全部沿用源配方，所以不需要额外美术资源。
    /// </summary>
    internal static class ExtraRecipeRegistry
    {
        internal static ExtraRecipesConfig Config;

        internal static void Load() => Config = JsonHelper.Load<ExtraRecipesConfig>("recipes");

        /// <summary>在 LDBTool.PreAddDataAction 阶段调用，紧跟巨型建筑之后。</summary>
        internal static void OnPreAddData()
        {
            if (Config?.recipes == null || Config.recipes.Length == 0) return;

            foreach (ExtraRecipeEntry entry in Config.recipes)
            {
                if (entry == null || entry.id <= 0) continue;

                if (LDB.recipes.Select(entry.id) != null)
                {
                    ProjectEdenPlugin.Log.LogError($"配方 ID {entry.id} 已被占用，「{entry.name}」未注册");
                    continue;
                }

                RecipeProto source = FindSource(entry);

                if (source == null)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"找不到「{(ERecipeType)entry.cloneFromType}」类型中产出物品 {entry.cloneFromResult} 的原版配方，" +
                        $"「{entry.name}」未注册");

                    continue;
                }

                var recipe = new RecipeProto
                {
                    ID = entry.id,
                    Name = entry.name,
                    Description = string.IsNullOrEmpty(entry.description) ? source.Description : entry.description,
                    Type = (ERecipeType)entry.type,

                    // 机器配方，不进手搓
                    Handcraft = false,
                    Explicit = true,
                    NonProductive = source.NonProductive,

                    TimeSpend = entry.timeSpend > 0 ? entry.timeSpend : source.TimeSpend,

                    // 必须复制数组：直接引用会让改动串到原版配方上
                    Items = (int[])source.Items.Clone(),
                    ItemCounts = (int[])source.ItemCounts.Clone(),
                    Results = (int[])source.Results.Clone(),
                    ResultCounts = (int[])source.ResultCounts.Clone(),

                    // 合成面板的格位。与原版配方共用一格会把对方顶掉，所以单独占一格，
                    // 沿用巨型建筑分页的编号规则（分页号 × 1000 + 行 × 100 + 列）。
                    GridIndex = MegaBuildingRegistry.GridIndex(entry.gridRow, entry.gridCol),

                    IconPath = source.IconPath,

                    // 无前置科技：另由 RecipeUnlockPatches 强制标记为已解锁
                    preTech = null,
                };

                recipe.name = entry.name;

                LDBTool.PreAddProto(recipe);

                // 和巨型建筑的配方共用同一个解锁集合
                MegaBuildingRegistry.RecipeIds.Add(entry.id);

                Utils.ProtoSlots.ReserveRecipeId(entry.id);
                Utils.ProtoSlots.ReserveGrid(MegaBuildingRegistry.GridIndex(entry.gridRow, entry.gridCol),
                    Utils.ProtoSlots.GridKind.Recipe);

                ProjectEdenPlugin.Log.LogInfo(
                    $"已注册配方「{entry.name}」（{(ERecipeType)entry.type}，格位 {recipe.GridIndex}）：" +
                    $"克隆自「{source.Name}」，{Describe(recipe)}，耗时 {recipe.TimeSpend / 60.0:0.##} 秒");
            }
        }

        /// <summary>按「配方类型 + 产物」找源配方，避免把原版配方 ID 写死。</summary>
        private static RecipeProto FindSource(ExtraRecipeEntry entry)
        {
            var wanted = (ERecipeType)entry.cloneFromType;

            foreach (RecipeProto recipe in LDB.recipes.dataArray)
            {
                if (recipe == null || recipe.Type != wanted) continue;
                if (recipe.Results == null || recipe.Items == null) continue;

                foreach (int result in recipe.Results)
                    if (result == entry.cloneFromResult)
                        return recipe;
            }

            return null;
        }

        /// <summary>把配方的进出料拼成人话，方便对着日志核对确实克隆对了。</summary>
        private static string Describe(RecipeProto recipe)
        {
            var text = new StringBuilder();

            Append(text, recipe.Items, recipe.ItemCounts);
            text.Append(" → ");
            Append(text, recipe.Results, recipe.ResultCounts);

            return text.ToString();
        }

        private static void Append(StringBuilder text, int[] ids, int[] counts)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                if (i > 0) text.Append(" + ");

                ItemProto item = LDB.items.Select(ids[i]);

                text.Append(item != null ? item.name : ids[i].ToString());
                text.Append(" ×");
                text.Append(i < counts.Length ? counts[i] : 0);
            }
        }
    }

    [Serializable]
    internal class ExtraRecipesConfig
    {
        public ExtraRecipeEntry[] recipes;
    }

    [Serializable]
    internal class ExtraRecipeEntry
    {
        /// <summary>新配方 ID，不能和原版或本 mod 已有的冲突</summary>
        public int id;

        public string name;
        public string description;

        /// <summary>新配方的 ERecipeType：1 熔炉 / 2 化工 / 3 精炼 / 4 组装 / 5 粒子</summary>
        public int type;

        /// <summary>去哪个类型里找源配方</summary>
        public int cloneFromType;

        /// <summary>源配方必须产出的物品 ID，用它把源配方认出来</summary>
        public int cloneFromResult;

        /// <summary>制作耗时（tick，60 = 1 秒）。0 表示沿用源配方</summary>
        public int timeSpend;

        /// <summary>合成面板里的格位，沿用巨型建筑分页</summary>
        public int gridRow, gridCol;
    }
}
