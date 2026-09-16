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
                    Utils.ProtoSlots.GridKind.Recipe, entry.name);

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

        // ── 就地改原版配方 ──────────────────────────────────────────

        /// <summary>
        /// 在 <c>LDBTool.PostAddDataAction</c> 阶段调用：给<b>原版</b>配方追加原料。
        ///
        /// <b>为什么必须是 Post 而不是 Pre。</b> <c>RecipeProto.InitRecipeItems</c> 把整张
        /// <c>recipeExecuteData</c> 表重建一遍（IL 0000 <c>newobj</c> + 0005 <c>stsfld</c>），
        /// 而 LDBTool 在 <c>PostAddDataAction</c> <b>之后</b>才调它——所以这里改完
        /// <c>Items</c> / <c>ItemCounts</c> 就会被它自动吸收成新的 <c>RecipeExecuteData</c>，
        /// 我们一行刷新代码都不用写。宇宙矩阵加第七样原料靠的就是这个时序。
        ///
        /// <b>改原料条数对存档是安全的，这是读 IL 确认的，不是推测。</b>
        /// <c>AssemblerComponent.Export</c> 每个数组都<b>先写自己的长度再写内容</b>
        /// （IL 00E2 写 <c>requires</c> 长度、0109 写 <c>served</c> 长度，都是一个字节），
        /// 于是字节流自带长度、能原样读回；<c>Import</c> 读完之后在 IL 040B–0446 按
        /// <b>当前</b>配方 <c>Array.Resize</c>：
        /// <code>
        /// int n = recipeExecuteData.requires.Length;
        /// if (served.Length != n) { Array.Resize(ref served, n); Array.Resize(ref incServed, n); }
        /// </code>
        /// 老存档里 2 长的 <c>served</c> 会被补成 3，旧值保留、新槽为 0。
        /// <c>LabComponent.Import</c> 是同一套自愈。
        ///
        /// <b>这只对「全局改配方」成立。</b> 逐台建筑的 <c>recipeExecuteData</c> 克隆
        /// （<c>AlloyRatioPatches</c> 那条路）改长度仍然会和存档打架，因为 <c>Import</c>
        /// 是从 <c>RecipeProto</c> 的<b>静态字典</b>重新取的对象（IL 03F5 / 06AB），
        /// 克隆的形状根本活不过一次读档。两者别混。
        ///
        /// <b>投料槽数不是问题。</b> 制造台这一侧全是 <c>ldlen</c> 循环——
        /// <c>UpdateNeeds</c>、<c>InternalUpdate</c>、<c>UIAssemblerWindow.SyncServingStorage</c>
        /// 都按数组长度走，本仓库的生物温室已经在跑四原料配方了。
        /// 被写死成 6 槽的是<b>实验室</b>的产出模式，那条记在
        /// <see cref="Patches.UniverseMatrixPatches"/> 里，和这里无关。
        /// </summary>
        internal static void OnPostAddData()
        {
            VanillaRecipeEditEntry[] edits = Config?.vanillaEdits;

            if (edits == null || edits.Length == 0) return;

            foreach (VanillaRecipeEditEntry entry in edits) ApplyEdit(entry);
        }

        private static void ApplyEdit(VanillaRecipeEditEntry entry)
        {
            if (entry == null) return;

            var wantsAdd = entry.add != null && entry.add.Length > 0;
            var wantsSet = entry.setCount != null && entry.setCount.Length > 0;

            if (!wantsAdd && !wantsSet) return;

            int resultId = ResolveItem(entry.result, null);

            if (resultId <= 0)
            {
                ProjectEdenPlugin.Log.LogError($"改原版配方：产物「{entry.result}」解析不出物品 ID，这条跳过");

                return;
            }

            RecipeProto recipe = FindByResult(resultId, entry.type);

            if (recipe == null) return; // FindByResult 自己报了原因

            if (recipe.Items == null || recipe.ItemCounts == null
                || recipe.Items.Length != recipe.ItemCounts.Length)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"改原版配方：配方 {recipe.ID}「{recipe.Name}」的 Items/ItemCounts 形状不对，这条跳过");

                return;
            }

            string before = Describe(recipe);
            var added = 0;
            var retuned = 0;

            // 改份数排在加料前面：点名的是**已有**原料，先改完再追加，
            // 两个动作就互不干扰（追加进来的那样也不会被这一轮误认成「已有」）
            if (wantsSet)
                foreach (RecipeItemEntry item in entry.setCount)
                {
                    if (item == null) continue;

                    int id = ResolveItem(item.@ref, item);

                    if (id <= 0)
                    {
                        ProjectEdenPlugin.Log.LogError(
                            $"改原版配方：配方 {recipe.ID}「{recipe.Name}」要改份数的原料"
                            + $"「{item.@ref}」解析不出 ID，这一项跳过");

                        continue;
                    }

                    int at = Array.IndexOf(recipe.Items, id);

                    if (at < 0)
                    {
                        // 静默跳过的话，「名字写错了」和「改了但看不出效果」在日志里长得一样
                        ProjectEdenPlugin.Log.LogError(
                            $"改原版配方：配方 {recipe.ID}「{recipe.Name}」里根本没有 "
                            + $"{LDB.items.Select(id)?.name ?? id.ToString()} 这样原料，改不了份数。"
                            + $"当前原料表：{Describe(recipe)}");

                        continue;
                    }

                    if (item.count <= 0)
                    {
                        ProjectEdenPlugin.Log.LogError(
                            $"改原版配方：配方 {recipe.ID}「{recipe.Name}」的 setCount 写了 "
                            + $"count={item.count}，份数必须是正数（删料没做），这一项跳过");

                        continue;
                    }

                    if (recipe.ItemCounts[at] == item.count) continue; // 已经是这个数（热重载重跑）

                    recipe.ItemCounts[at] = item.count;
                    retuned++;
                }

            if (!wantsAdd) { ReportEdit(recipe, before, added, retuned); return; }

            foreach (RecipeItemEntry item in entry.add)
            {
                if (item == null) continue;

                int id = ResolveItem(item.@ref, item);

                if (id <= 0)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"改原版配方：配方 {recipe.ID}「{recipe.Name}」要加的原料「{item.@ref}」解析不出 ID，这一项跳过");

                    continue;
                }

                // **幂等**：PostAddDataAction 热重载时会重跑，已经有了就不要再加一遍
                if (Array.IndexOf(recipe.Items, id) >= 0)
                {
                    ProjectEdenPlugin.Log.LogInfo(
                        $"改原版配方：配方 {recipe.ID}「{recipe.Name}」里已经有 "
                        + $"{LDB.items.Select(id)?.name ?? id.ToString()} 了，跳过");

                    continue;
                }

                int count = item.count > 0 ? item.count : 1;

                recipe.Items = Grow(recipe.Items, id);
                recipe.ItemCounts = Grow(recipe.ItemCounts, count);
                added++;
            }

            ReportEdit(recipe, before, added, retuned);
        }

        /// <summary>
        /// 报这条改动，并**顺带把可燃能量的进出账算出来**。
        ///
        /// <b>为什么这一行非有不可：<c>EnergyAudit</c> 看不见原版配方。</b> 它走的是
        /// <c>ores.json</c> 自己的配方表，所以凡是这里改过的原版配方，都在审计的射程之外。
        /// 而这一段能做的事恰恰是「让一条原版配方开始或停止凭空造能量」——
        /// 那就得在改的地方当场把账打出来，否则谁也不会去查。
        /// </summary>
        private static void ReportEdit(RecipeProto recipe, string before, int added, int retuned)
        {
            if (added == 0 && retuned == 0) return;

            var what = new StringBuilder();

            if (added > 0) what.Append("加了 ").Append(added).Append(" 样原料");

            if (retuned > 0)
            {
                if (what.Length > 0) what.Append("、");

                what.Append("改了 ").Append(retuned).Append(" 样原料的份数");
            }

            long inHeat = Burnable(recipe.Items, recipe.ItemCounts);
            long outHeat = Burnable(recipe.Results, recipe.ResultCounts);
            long delta = outHeat - inHeat;

            ProjectEdenPlugin.Log.LogInfo(
                $"改原版配方：{recipe.ID}「{recipe.Name}」{what}。"
                + $"改前 {before}；改后 {Describe(recipe)}。"
                + $"可燃能量 {inHeat / 1000000.0:0.##} MJ 进 / {outHeat / 1000000.0:0.##} MJ 出，"
                + $"差额 {delta / 1000000.0:+0.##;-0.##;0} MJ"
                + (added > 0 ? "（原料条数变了，老存档由原版自己的 Array.Resize 兜住，新槽从 0 开始）" : ""));

            // 差额为正 = 这条配方在造可燃能量。万倍速建筑跑得了原版配方，所以这不是小事
            if (delta > 1000000L)
                ProjectEdenPlugin.Log.LogWarning(
                    $"改原版配方：{recipe.ID}「{recipe.Name}」改完之后**产物的可燃能量比原料多 "
                    + $"{delta / 1000000.0:0.##} MJ**。EnergyAudit 不覆盖原版配方，所以没有别处会拦它；"
                    + "而本 mod 的万倍速建筑跑得了这条配方。确认这是有意为之。");
        }

        /// <summary>一侧的可燃能量合计。非燃料（HeatValue 为 0）自然按 0 记。</summary>
        private static long Burnable(int[] ids, int[] counts)
        {
            if (ids == null || counts == null) return 0L;

            var sum = 0L;

            for (var i = 0; i < ids.Length && i < counts.Length; i++)
            {
                ItemProto item = LDB.items.Select(ids[i]);

                if (item != null) sum += item.HeatValue * counts[i];
            }

            return sum;
        }

        /// <summary>
        /// 按产物找配方。<b>不写死配方号</b>——原版配方号在 <c>resources.assets</c> 里，
        /// 离线枚举不到，写错一个数字就是悄悄改了别的配方。
        ///
        /// 匹配到多条而 <c>type</c> 又没点名时，<b>一条都不改</b>并把候选全列出来。
        /// 随便挑第一条改，在装了别的内容 mod（它们会给同一产物加替代配方）时就是
        /// 「改中了哪条全看运气」，而且不会报错。
        /// </summary>
        private static RecipeProto FindByResult(int resultId, int type)
        {
            RecipeProto[] all = LDB.recipes?.dataArray;

            if (all == null) return null;

            RecipeProto found = null;
            var hits = 0;
            var names = new StringBuilder();

            foreach (RecipeProto recipe in all)
            {
                if (recipe?.Results == null || Array.IndexOf(recipe.Results, resultId) < 0) continue;
                if (type > 0 && recipe.Type != (ERecipeType)type) continue;

                hits++;

                if (found == null) found = recipe;

                if (names.Length > 0) names.Append("、");

                names.Append($"{recipe.ID}「{recipe.Name}」({recipe.Type})");
            }

            string what = LDB.items.Select(resultId)?.name ?? resultId.ToString();

            if (hits == 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"改原版配方：没找到产出「{what}」" + (type > 0 ? $"且类型为 {(ERecipeType)type}" : "") + " 的配方，这条跳过");

                return null;
            }

            if (hits > 1)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"改原版配方：产出「{what}」的配方有 {hits} 条（{names}），"
                    + "分不清改哪条，**一条都没改**。在 recipes.json 的这条 vanillaEdits 上加 type 点名。");

                return null;
            }

            return found;
        }

        /// <summary>
        /// 解析一个物品引用。<c>id</c> 是原版号，<c>ref</c> 走
        /// <see cref="OreRegistry.FindItemIdByRef"/>（items 段的 key、
        /// 「矿种key.ore」/「.ingot」，或者 <c>vanilla:中文名</c>）。
        /// </summary>
        private static int ResolveItem(string @ref, RecipeItemEntry entry)
        {
            if (entry != null && entry.id > 0) return entry.id;

            return string.IsNullOrEmpty(@ref) ? 0 : OreRegistry.FindItemIdByRef(@ref);
        }

        private static int[] Grow(int[] array, int value)
        {
            var next = new int[array.Length + 1];
            Array.Copy(array, next, array.Length);
            next[array.Length] = value;

            return next;
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

        /// <summary>就地改原版配方（只加料）。见 <see cref="VanillaRecipeEditEntry"/>。</summary>
        public VanillaRecipeEditEntry[] vanillaEdits;
    }

    /// <summary>
    /// 就地改一条<b>原版</b>配方的原料表。和 <see cref="ExtraRecipeEntry"/> 是两件事：
    /// 那个是「加一条新配方」，这个是「原版那条本身要改」。
    ///
    /// 能加料（<see cref="add"/>）和改份数（<see cref="setCount"/>）。**删料仍然没做**
    /// ——删料要动数组长度，而且没人需要。
    /// </summary>
    [Serializable]
    internal class VanillaRecipeEditEntry
    {
        /// <summary>
        /// 认哪条配方：按<b>产物</b>找，不写配方号。语法同 ores.json 的 <c>ref</c>，
        /// 常用的是 <c>vanilla:中文名</c>。
        /// </summary>
        public string result;

        /// <summary>
        /// 同一产物有多条配方时用它点名（ERecipeType）。留 0 表示不限；
        /// <b>不限而又匹配到多条时一条都不改</b>，并把候选列进日志。
        /// </summary>
        public int type;

        /// <summary>要追加的原料。已经在配方里的会被跳过（热重载幂等）。</summary>
        public RecipeItemEntry[] add;

        /// <summary>
        /// 改一样<b>已有</b>原料的份数。<c>count</c> 是**绝对值不是增量**——
        /// 这条动作因此天然幂等，`PostAddDataAction` 热重载重跑一遍结果一样。
        ///
        /// <b>只动值不动长度，所以对存档安全</b>：`AssemblerComponent.Export` 依赖的是
        /// `requires` / `products` 的**长度**，份数是纯粹的值（这一点和 CLAUDE.md 里
        /// 「逐台克隆不许改长度」是同一条规则的两面）。
        ///
        /// 点名的原料不在配方里时**报 ERROR 并跳过**，不静默——那多半是名字写错了，
        /// 而「改了个不存在的原料」和「改了但没效果」在日志里必须分得开。
        /// </summary>
        public RecipeItemEntry[] setCount;
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
