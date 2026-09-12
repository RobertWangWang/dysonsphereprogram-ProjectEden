using System;
using System.Collections.Generic;

namespace ProjectEden.Utils
{
    /// <summary>
    /// proto 的 ID / 格位 / 建造栏槽位解析。撞了就顺延，并把结果大声记下来。
    ///
    /// <b>占用判定一律扫 dataArray，不用 ProtoSet.Select。</b> 后者对不存在的 ID
    /// 并不可靠地返回 null——实测物品集会把连续 200 个 ID 全判成已占用，
    /// 拿它当占用测试会得到完全错误的结论（见 CLAUDE.md）。
    ///
    /// 另外原版的物品与配方 proto 存在 resources.assets 里、不在程序集里，
    /// <b>反编译是列不出已用 ID 的</b>，唯一权威的表就是运行中的 LDB——
    /// 所以这些判定只能在运行时做，不能在配置里靠人肉避让。
    /// </summary>
    internal static class ProtoSlots
    {
        /// <summary>本页原有列用完时允许往右多找的列数，靠合成器横向翻页露出。</summary>
        internal const int ExtraCols = 28;

        // ── 本轮已认领、但还没进 LDB 的东西 ────────────────────
        //
        // <b>PreAddData 阶段 LDB 里没有任何 mod 的 proto。</b> LDBTool.PreAddProto 只是把
        // proto 塞进它自己的队列，要等 LDBTool 建表时才写进 LDB.items / recipes / models。
        // 所以在这个阶段光扫 dataArray，看到的只有原版——先注册的那个模块占了什么，
        // 后注册的模块<b>一无所知</b>。电化学厂就是这么和天空装配厂抢到同一个建造栏槽位的：
        // 巨型建筑先注册，但那时它还不在 LDB 里，第 12 类看起来空空如也。
        //
        // 各注册器认领到一个号就往这里登记一笔，后面的模块才看得见。

        private static readonly HashSet<int> ReservedItemIds = new HashSet<int>();
        private static readonly HashSet<int> ReservedRecipeIds = new HashSet<int>();
        private static readonly HashSet<int> ReservedModelIds = new HashSet<int>();
        private static readonly HashSet<int> ReservedItemGrids = new HashSet<int>();
        private static readonly HashSet<int> ReservedRecipeGrids = new HashSet<int>();
        private static readonly HashSet<int> ReservedBuildIndices = new HashSet<int>();

        /// <summary>每一轮 PreAddData 开始时清空。热重载会重跑一遍，不清就会越攒越多。</summary>
        internal static void ClearReservations()
        {
            ReservedItemIds.Clear();
            ReservedRecipeIds.Clear();
            ReservedModelIds.Clear();
            ReservedItemGrids.Clear();
            ReservedRecipeGrids.Clear();
            ReservedBuildIndices.Clear();
        }

        internal static void ReserveItemId(int id) { if (id > 0) ReservedItemIds.Add(id); }

        internal static void ReserveRecipeId(int id) { if (id > 0) ReservedRecipeIds.Add(id); }

        internal static void ReserveModelId(int id) { if (id > 0) ReservedModelIds.Add(id); }

        /// <summary>
        /// 格位有<b>两张</b>，各画各的：物品格由 <c>ItemProto.GridIndex</c> 排，
        /// 配方格由 <c>RecipeProto.GridIndex</c> 排。认领时必须说清是哪一张。
        /// </summary>
        internal enum GridKind
        {
            /// <summary>物品格：物品选取／信号选取／掉落过滤这几个窗口画的就是它</summary>
            Item,

            /// <summary>配方格：合成面板和配方选取窗口画的是它</summary>
            Recipe,
        }

        internal static void ReserveGrid(int grid, GridKind kind)
        {
            if (grid <= 0) return;

            (kind == GridKind.Item ? ReservedItemGrids : ReservedRecipeGrids).Add(grid);
        }

        internal static void ReserveBuildIndex(int index) { if (index > 0) ReservedBuildIndices.Add(index); }

        /// <summary>
        /// 本 mod 这一轮登记过的物品 / 配方 ID。
        ///
        /// 给 <see cref="I18N"/> 的漏译核对用：要判断「这个 proto 是不是我们自己加的」，
        /// 按 ID 区间猜是不行的——同时装了 GenesisBook 之类的 mod 时，
        /// 它们的 proto 也落在 6500 以上，会被误报成漏译。登记簿是唯一准的那份名单。
        /// </summary>
        internal static IEnumerable<int> OwnItemIds => ReservedItemIds;

        internal static IEnumerable<int> OwnRecipeIds => ReservedRecipeIds;

        // ── 占用判定 ──────────────────────────────────────────

        internal static bool ItemIdTaken(int id)
        {
            if (ReservedItemIds.Contains(id)) return true;

            foreach (ItemProto proto in LDB.items.dataArray)
                if (proto != null && proto.ID == id)
                    return true;

            return false;
        }

        internal static bool RecipeIdTaken(int id)
        {
            if (ReservedRecipeIds.Contains(id)) return true;

            foreach (RecipeProto proto in LDB.recipes.dataArray)
                if (proto != null && proto.ID == id)
                    return true;

            return false;
        }

        internal static bool ModelIdTaken(int id)
        {
            if (ReservedModelIds.Contains(id)) return true;

            foreach (ModelProto proto in LDB.models.dataArray)
                if (proto != null && proto.ID == id)
                    return true;

            return false;
        }

        /// <summary>
        /// 格位是不是已经有人了。
        ///
        /// <b>只扫自己那张表。</b> 合成面板画的是<b>配方</b>——RefreshRecipeIcons 从头到尾
        /// 只遍历 LDB.recipes，按 RecipeProto.GridIndex 摆位，没有配方的物品根本不出现；
        /// 而 ItemProto.GridIndex 管的是物品选取那类面板。两套格位互不相干，
        /// 一个物品和一个配方占同一个格号毫无问题——原版自己就是这么摆的
        /// （铁块的物品格和铁块的配方格本来就是同一个号）。
        ///
        /// <b>合起来判会出大事，这是踩过的坑。</b> 早先这里两张表一起扫，
        /// 而合成面板第 1 页的<b>配方</b>本来就几乎排满，于是每一个 mod <b>物品</b>
        /// 都被挤到第 15 列以后。四个画物品格的窗口
        /// （<c>UIItemPicker</c> / <c>UILootFilter</c> / <c>UISignalPicker</c> /
        /// <c>UISignalTagPicker</c>）在 <c>RefreshIcons</c> 里都有一句硬裁
        /// <c>col &gt;= 14 → continue</c>，而且都没有横向翻页——
        /// 结果是<b>本 mod 的物品在物品栏里一个都看不见</b>，
        /// 偏偏配方在合成面板里好端端的（那两个窗口有翻页补丁撑着）。
        /// 「配方看得见、物品看不见」就是这个 bug 的签名。
        /// </summary>
        /// <summary>画物品格的那几个窗口写死的可见列数（RefreshIcons 里的 col &gt;= 14）。</summary>
        internal const int VisibleCols = 14;

        /// <summary>
        /// 可用的行数。两张网格的渲染都是 <c>row &gt;= 8 → continue</c>，
        /// 所以第 1~8 行都画得出来。
        ///
        /// <b>不能用「量出来的最大行」当上界。</b> 原版某一页的物品可能只排到第 6 行，
        /// 量出来就是 6，于是第 7、8 行那 28 个格子一个都不会被试——
        /// 白白把东西挤到扩展列里去，而扩展列在物品格上等于看不见。
        /// </summary>
        internal const int VisibleRows = 8;

        internal static bool GridTaken(int grid, GridKind kind)
        {
            if (kind == GridKind.Item)
            {
                if (ReservedItemGrids.Contains(grid)) return true;

                foreach (ItemProto item in LDB.items.dataArray)
                    if (item != null && item.GridIndex == grid)
                        return true;

                return false;
            }

            if (ReservedRecipeGrids.Contains(grid)) return true;

            foreach (RecipeProto recipe in LDB.recipes.dataArray)
                if (recipe != null && recipe.GridIndex == grid)
                    return true;

            return false;
        }

        internal static bool BuildIndexTaken(int buildIndex)
        {
            if (ReservedBuildIndices.Contains(buildIndex)) return true;

            foreach (ItemProto item in LDB.items.dataArray)
                if (item != null && item.BuildIndex == buildIndex)
                    return true;

            return false;
        }

        // ── 解析 ──────────────────────────────────────────────

        internal static int ResolveItemId(int wanted, string label, int fallback = 6500) =>
            Resolve(wanted, label, "物品", ItemIdTaken, fallback);

        internal static int ResolveRecipeId(int wanted, string label, int fallback = 6500) =>
            Resolve(wanted, label, "配方", RecipeIdTaken, fallback);

        /// <summary>
        /// 想要的 ID 被占了就往后找。找到的值会被打进日志，<b>请把它钉回配置</b>——
        /// 物品和配方 ID 都进存档，每次开局重新分配会让老存档里的东西错位。
        /// </summary>
        private static int Resolve(int wanted, string label, string kind, Func<int, bool> taken, int fallback)
        {
            if (wanted <= 0) wanted = fallback;

            int id = wanted;

            // 别无限找：连着 200 个都被占说明配置本身选错了区间
            for (var step = 0; step < 200; step++, id++)
            {
                if (taken(id)) continue;

                if (id != wanted)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"{label}的{kind} ID {wanted} 已被占用，改用 {id}。" +
                        $"ID 会进存档，请把配置里对应的值钉成 {id} 再继续游戏");

                return id;
            }

            ProjectEdenPlugin.Log.LogError($"{label}的{kind} ID 从 {wanted} 起连续 200 个都被占用，换一个区间");

            return wanted;
        }

        /// <summary>
        /// 模型 ID。上界不是随便定的：ModelProtoSet.OnAfterDeserialize 按
        /// <c>dataArray.Length + 64</c> 开数组，却<b>用模型 ID 当下标</b>，超了就越界。
        ///
        /// 找不到就从上界往下倒着找——低位号全是原版的，从下往上找纯属浪费。
        /// </summary>
        internal static int ResolveModelId(int wanted, string label)
        {
            int bound = LDB.models.dataArray.Length + 64;

            if (wanted > 0 && wanted < bound && !ModelIdTaken(wanted)) return wanted;

            for (int id = bound - 1; id > 0; id--)
            {
                if (ModelIdTaken(id)) continue;

                ProjectEdenPlugin.Log.LogWarning(
                    $"{label}期望的模型 ID {wanted} 不可用（已占用或 ≥ 上限 {bound}），改用 {id}。" +
                    $"模型 ID 会写进存档，请把配置里对应的值固定为 {id}");

                return id;
            }

            return 0;
        }

        private static bool _ownTabLogged;

        /// <summary>
        /// 把第 1 页的请求改到本 mod 自己的分页上。理由见
        /// <see cref="MegaBuildingsConfig.ownTabForModProtos"/>。
        ///
        /// <b>三道闸都过了才搬</b>，任何一道不过就原样返回——
        /// 页号就是标签页号，搬到一个没注册成功的分页等于让东西凭空消失，
        /// 那种失败还不会报错。
        /// </summary>
        private static int PreferOwnTab(int wanted)
        {
            MegaBuildingsConfig cfg = MegaBuildingRegistry.Config;

            if (cfg == null || !cfg.ownTabForModProtos) return wanted;

            int tab = MegaBuildingRegistry.TabIndex;

            // 原版占 1（物品）和 2（建筑），所以自有分页必然 ≥ 3；
            // 拿不到就是 CommonAPI 那边没注册成功，这时候搬过去等于扔掉
            if (tab <= 2) return wanted;

            // 只搬第 1 页。建筑本来就在第 2 页，那是它们该待的地方
            if (wanted / 1000 != 1) return wanted;

            if (!_ownTabLogged)
            {
                _ownTabLogged = true;

                ProjectEdenPlugin.Log.LogInfo(
                    $"物品与配方格位：默认第 1 页的一律改到本 mod 自己的第 {tab} 页。"
                    + "原版第 1 页实测 111/112 格已占，挤进去只能落到第 14 列之外，"
                    + "而掉落过滤与信号选取窗口硬裁 14 列——那等于这件物品在那些窗口里不存在。"
                    + "（megabuildings.json 的 ownTabForModProtos 可关）");
            }

            return tab * 1000 + wanted % 1000;
        }

        /// <summary>
        /// 合成面板格位。<b>只在本页内找</b>，不能滚到下一页——分页号决定物品落在
        /// 「物品」还是「建筑」标签下，翻页等于把东西搬到另一个标签里去了。
        ///
        /// 也<b>不能往行上扩</b>：RefreshRecipeIcons 里 row &gt;= 8 直接跳过，第 9 行画都不画。
        /// 只能往列上扩，多出来的列靠合成器的横向翻页露出（见 ReplicatorExpandPatches）。
        /// </summary>
        internal static int ResolveGridIndex(int wanted, string label, GridKind kind,
            Func<int, bool> alsoTaken = null, int alsoAvoid = 0)
        {
            if (wanted <= 0) wanted = 1601;

            wanted = PreferOwnTab(wanted);

            int page = wanted / 1000;
            int maxRow = 1, maxCol = 1;

            // 量的也只是自己那张表——物品格的行列范围和配方格的没有关系
            if (kind == GridKind.Item)
                foreach (ItemProto item in LDB.items.dataArray) Measure(item?.GridIndex ?? 0);
            else
                foreach (RecipeProto recipe in LDB.recipes.dataArray) Measure(recipe?.GridIndex ?? 0);

            // 量出来的只是「现有内容排到哪」，不是「能排到哪」。
            // 行列的真实上界由渲染代码决定，往这两个数上取大。
            if (maxRow < VisibleRows) maxRow = VisibleRows;
            if (maxCol < VisibleCols) maxCol = VisibleCols;

            void Measure(int grid)
            {
                if (grid / 1000 != page) return;

                int r = grid % 1000 / 100;
                int c = grid % 100;

                if (r > maxRow) maxRow = r;
                if (c > maxCol) maxCol = c;
            }

            // <b>先把可见的 14 列填满，再溢出到扩展列。</b>
            // 原来是单层行优先：第 1 行一路扫到第 42 列，所以第 1 行的可见格子一满
            // 就直接跳到第 15 列去了，<b>后面 7 行的可见格子一个都没试过</b>。
            // 对配方还只是难看（合成面板有横向翻页兜着），对物品是致命的——
            // 画物品格的四个窗口都硬裁 col>=14，落到扩展列就等于这件物品不存在。
            for (var band = 0; band < 2; band++)
            {
                int fromCol = band == 0 ? 1 : VisibleCols + 1;
                int toCol = band == 0 ? VisibleCols : maxCol + ExtraCols;

                for (var row = 1; row <= maxRow; row++)
                for (int col = fromCol; col <= toCol; col++)
                {
                    int grid = page * 1000 + row * 100 + col;

                    if (grid == alsoAvoid || GridTaken(grid, kind) || (alsoTaken != null && alsoTaken(grid)))
                        continue;

                    if (grid != wanted)
                        ProjectEdenPlugin.Log.LogInfo(
                            $"{label}的{(kind == GridKind.Item ? "物品" : "配方")}格位 {wanted} 已被占用，改用 {grid}" +
                            $"（第 {page} 页第 {row} 行第 {col} 列）");

                    // 物品落到扩展列 = 在物品栏里不存在，必须吵出来。
                    // ItemPickerExpandPatches 会给物品选取窗口补横向翻页把它们捞回来，
                    // 但掉落过滤和两个信号窗口还没有，所以这条警告继续留着。
                    if (kind == GridKind.Item && col > VisibleCols)
                        ProjectEdenPlugin.Log.LogWarning(
                            $"{label}的物品格位是 {grid}（第 {col} 列），超过了 {VisibleCols} 列 —— " +
                            "物品选取窗口要靠横向翻页才看得到它；掉落过滤与信号选取窗口里它画不出来");

                    return grid;
                }
            }

            ProjectEdenPlugin.Log.LogWarning(
                $"{label} 在第 {page} 页（{maxRow} 行 × {maxCol}+{ExtraCols} 列）找不到空格位，沿用 {wanted}，可能与别的物品互相遮挡");

            return wanted;
        }

        /// <summary>
        /// 建造栏位置 = 分类 × 100 + 槽位。
        ///
        /// 想要的槽被占了就在<b>同一分类</b>里往后找，不换分类——换了分类等于把建筑挪到
        /// 另一个建造标签下面去了。本分类实在没空位时才退到 <paramref name="fallbackCategory"/>。
        ///
        /// 槽位上限是 <see cref="Patches.BuildMenuScrollPatches.MaxSlot"/>——一屏放不下的部分
        /// 靠子项行的横向滑动露出。<b>不要绕过那套搬运直接往原版 protos 里塞</b>：
        /// 原版 childButtons[slot] 是不判空解引用的，没有按钮的槽位会让建造栏每帧空引用。
        /// </summary>
        internal static int ResolveBuildIndex(int wanted, string label, Func<int, bool> alsoTaken = null,
            int fallbackCategory = 0)
        {
            if (wanted <= 0) return 0;

            // 槽位可以一直排到 MaxSlot：BuildMenuScrollPatches 每帧只把「当前窗口」搬进
            // 原版的 protos，超出一屏的部分永远不会落到没有按钮的格子上。
            const int limit = Patches.BuildMenuScrollPatches.MaxSlot;

            int category = wanted / 100;
            int slot = wanted % 100;

            if (slot >= 1 && slot <= limit && !Taken(wanted)) return wanted;

            int found = FindIn(category);

            if (found > 0) return found;

            if (fallbackCategory > 0 && fallbackCategory != category)
            {
                found = FindIn(fallbackCategory);

                if (found > 0)
                {
                    ProjectEdenPlugin.Log.LogWarning(
                        $"{label} 在第 {category} 类建造栏里排满了，" +
                        $"已改放到第 {fallbackCategory} 类第 {found % 100} 槽");

                    return found;
                }
            }

            ProjectEdenPlugin.Log.LogError(
                $"{label} 在第 {category} 类和第 {fallbackCategory} 类都排满了（每类上限 {limit} 个），不放进建造栏");

            return 0;

            bool Taken(int index) => BuildIndexTaken(index) || (alsoTaken != null && alsoTaken(index));

            int FindIn(int cat)
            {
                for (var s = 1; s <= limit; s++)
                {
                    int index = cat * 100 + s;

                    if (Taken(index)) continue;

                    if (index != wanted)
                        ProjectEdenPlugin.Log.LogInfo(
                            $"{label}的建造栏槽位 {wanted} 已被占用，改用 {index}（第 {cat} 类第 {s} 槽）");

                    return index;
                }

                return 0;
            }
        }
    }
}
