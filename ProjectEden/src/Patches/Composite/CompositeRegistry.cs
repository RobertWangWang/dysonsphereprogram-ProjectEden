using System.Collections.Generic;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 活性复合材的解析与自检。
    ///
    /// <b>这里不注册任何 proto。</b> 四个产物物品和那条配方都写在 <c>ores.json</c> 里，
    /// 走的是现成的注册管线——图标、描述、中英文本地化、格位解析全都不用重写一遍。
    /// 本类只做一件事：把配置里的引用名解析成运行时的物品 / 配方 ID，并核对它们真的存在。
    ///
    /// <b>核对是必须的，不是装饰。</b> 引用名写错的后果是静默的：
    /// 面板照样画出来，但 <c>Apply</c> 写进去的 <c>products[0]</c> 是 0，
    /// 机器就变成一台吃原料不产出的黑洞。所以解析不出来就整个功能停用并报 ERROR。
    /// </summary>
    internal static class CompositeRegistry
    {
        internal class Grade
        {
            internal CompositeGradeEntry Entry;
            internal int ItemId;
        }

        internal static CompositeConfig Config { get; private set; }

        internal static readonly List<Grade> Grades = new List<Grade>();

        /// <summary>可选填料的物品 ID，顺序和配置一致（选料行靠下标前后翻）。</summary>
        internal static readonly List<int> Candidates = new List<int>();

        internal static int MatrixItemId { get; private set; }

        internal static int RecipeId { get; private set; }

        internal static int TotalParts => Config != null && Config.totalParts > 1 ? Config.totalParts : 10;

        internal static bool Ready =>
            Grades.Count > 0 && Candidates.Count > 0 && MatrixItemId > 0 && RecipeId > 0;

        internal static void Load() => Config = JsonHelper.Load<CompositeConfig>("composite");

        /// <summary>
        /// 在 LDB 建好之后解析。<b>必须是 PostAdd</b>：PreAdd 阶段 LDBTool 还没把
        /// 本 mod 的 proto 放进 LDB，这时按 ref 查什么都查不到。
        /// </summary>
        internal static void OnPostAddData()
        {
            Grades.Clear();
            Candidates.Clear();
            MatrixItemId = 0;
            RecipeId = 0;

            // 无论开关是什么状态都报一行 —— 不报的话，「配置关了」和
            // 「这段代码根本没进 DLL」在日志里长得一模一样。这个坑本仓库踩过四次。
            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogWarning("活性复合材：读不到 composite.json，功能停用");

                return;
            }

            if (!Config.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo("活性复合材：已在 composite.json 里关闭");

                return;
            }

            MatrixItemId = OreRegistry.FindItemIdByRef(Config.matrixRef);

            if (MatrixItemId <= 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"活性复合材：基体 ref「{Config.matrixRef}」解析不出物品，功能停用");

                return;
            }

            // ── 产物四级 ──
            if (Config.grades != null)
                foreach (CompositeGradeEntry g in Config.grades)
                {
                    if (g == null) continue;

                    int id = OreRegistry.FindItemIdByRef(g.@ref);

                    if (id <= 0)
                    {
                        ProjectEdenPlugin.Log.LogError(
                            $"活性复合材：等级 ref「{g.@ref}」解析不出物品，功能停用");

                        Grades.Clear();

                        return;
                    }

                    // 名字交叉核对：比的是 Name（原始键）不是 name（翻译后），
                    // 否则英文环境下每一条都会误报 —— 本仓库有两处自检栽在这上面过
                    ItemProto proto = LDB.items.Select(id);

                    if (proto != null && !string.IsNullOrEmpty(g.name) && proto.Name != g.name)
                        ProjectEdenPlugin.Log.LogWarning(
                            $"活性复合材：ref「{g.@ref}」解析出的是「{proto.Name}」，" +
                            $"配置里写的是「{g.name}」—— ref 可能写错了");

                    Grades.Add(new Grade { Entry = g, ItemId = id });
                }

            if (Grades.Count == 0)
            {
                ProjectEdenPlugin.Log.LogError("活性复合材：一个等级都没解析出来，功能停用");

                return;
            }

            // ── 可选填料 ──
            var missing = new List<string>();

            if (Config.candidates != null)
                foreach (string r in Config.candidates)
                {
                    int id = OreRegistry.FindItemIdByRef(r);

                    if (id > 0) Candidates.Add(id);
                    else missing.Add(r);
                }

            if (missing.Count > 0)
                ProjectEdenPlugin.Log.LogWarning(
                    $"活性复合材：这些填料 ref 解析不出物品，已跳过：{string.Join("、", missing.ToArray())}");

            if (Candidates.Count == 0)
            {
                ProjectEdenPlugin.Log.LogError("活性复合材：一种可用填料都没有，功能停用");

                return;
            }

            // ── 配方 ──
            RecipeProto recipe = LDB.recipes.Select(Config.recipeId);

            if (recipe == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"活性复合材：找不到配方 {Config.recipeId}。配方本体在 ores.json 里，" +
                    "两边的 recipeId 必须一致");

                return;
            }

            RecipeId = Config.recipeId;

            // 配方的原料/产物长度是存档的命脉：AssemblerComponent.Export 按
            // requires / products 的**长度**决定写几个 served / produced。
            // 这里只改值不改长度，但先核一遍长度对不对，错了立刻停用。
            if (recipe.Items == null || recipe.Items.Length != 2
                || recipe.Results == null || recipe.Results.Length != 1)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"活性复合材：配方「{recipe.Name}」的原料数应为 2、产物数应为 1，" +
                    $"实际是 {recipe.Items?.Length ?? 0} / {recipe.Results?.Length ?? 0}，功能停用");

                RecipeId = 0;

                return;
            }

            // 让合成面板里那一格显示成一个像样的默认组合
            recipe.Items[0] = MatrixItemId;
            recipe.Items[1] = Candidates[0];
            recipe.Results[0] = Grades[0].ItemId;

            var names = new List<string>();

            for (var i = 0; i < Candidates.Count; i++)
                names.Add(LDB.items.Select(Candidates[i])?.name ?? Candidates[i].ToString());

            ProjectEdenPlugin.Log.LogInfo(
                $"活性复合材已就绪：{Candidates.Count} 种填料 × {Grades.Count} 个等级 = " +
                $"{Candidates.Count * Grades.Count} 种组合，共用配方 {RecipeId}「{recipe.Name}」；" +
                $"可选填料：{string.Join("、", names.ToArray())}");
        }

        /// <summary>合金份数 → 等级下标。取「份数够得着」的最高一级。</summary>
        internal static int GradeIndex(int alloyParts)
        {
            var index = 0;

            for (var i = 0; i < Grades.Count; i++)
                if (alloyParts >= Grades[i].Entry.minParts)
                    index = i;

            return index;
        }
    }
}
