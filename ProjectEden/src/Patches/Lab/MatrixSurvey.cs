using System.Collections.Generic;
using System.Text;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把每一种研究矩阵的**生产时间**打进启动日志，并拿它去核对 <c>lab.json</c> 的
    /// <c>assembleSpeed</c> 够不够填满最慢的那一条。
    ///
    /// <b>为什么必须是运行时 dump。</b> 原版配方的 <c>TimeSpend</c> 躺在
    /// <c>resources.assets</c> 里，反编译读不到——和主题表、燃料热值是同一堵墙。
    /// 在这行日志存在之前，本仓库对这几个数字的全部依据是两句注释：
    /// <c>lab.json</c> 的「最慢的引力矩阵 24 秒 = 1440 tick」和「超过 9 秒的配方
    /// （信息 / 引力 / 宇宙矩阵）硬编码只囤 6 个」。<b>那是记下来的，不是量出来的</b>，
    /// 而本文件为「凭记忆写档位」已经付过一次账：<see cref="FuelSurvey.DumpFuelLadder"/>
    /// 那次一跑就发现原版五档燃料倍率里错了四档。
    ///
    /// <b>判据是 <c>LabComponent.matrixIds</c>，不是物品号区间，更不是名字。</b>
    /// 那个数组<b>就是引擎自己对「什么算研究矩阵」的回答</b>——
    /// <c>PlanetFactory.InsertInto</c> 按 <c>itemId − matrixIds[0]</c> 算槽位，
    /// <c>GameTickLabResearchMode</c> 按它折动画下标。而且
    /// <see cref="BioMatrixPatches"/> 已经把生物矩阵接在它后面，所以第七种矩阵
    /// 自动进这张表，不用在这里再写一遍它的 ID。
    ///
    /// <b>单位链（全部是 IL 量的，不是换算出来的）：</b>
    /// <list type="bullet">
    /// <item><c>UIRecipeEntry.SetRecipe</c> @0112 和 <c>UIReplicatorWindow.OnSelectedRecipeChange</c>
    /// @0261 都是 <c>TimeSpend / 60f</c> —— <b>60 帧 = 1 秒是原版自己的换算</b>，
    /// 面板上那个「X 秒」就是这么来的。</item>
    /// <item><c>RecipeProto.InitRecipeItems</c> @003C：<c>timeSpend = TimeSpend × 10000</c>。</item>
    /// <item>同一方法 @004A：<c>extraTimeSpend = TimeSpend × 100000</c>。</item>
    /// <item><c>LabComponent.UpdateNeedsAssemble</c> @0014：那道
    /// <c>timeSpend &gt; 5400000</c> 的闸 —— 换算过来正好是 540 帧 = 9 秒。</item>
    /// </list>
    /// </summary>
    internal static class MatrixSurvey
    {
        /// <summary>原版每帧一次结算，<c>UIRecipeEntry.SetRecipe</c> 里就是拿 60 去除的。</summary>
        private const double TicksPerSecond = 60.0;

        /// <summary><c>RecipeProto.InitRecipeItems</c> @003C 的乘数：主轨刻度。</summary>
        private const int MainScale = 10000;

        /// <summary>同上 @004A 的乘数：增产额外轨刻度。</summary>
        private const int ExtraScale = 100000;

        /// <summary>
        /// <c>LabComponent.UpdateNeedsAssemble</c> @0014 那道硬编码闸的阈值。
        /// 超过它，原版无论速度多高都只囤 6 份原料。
        /// </summary>
        private const int SlowRecipeGate = 5400000;

        private static bool _done;

        internal static void OnPostAddData()
        {
            // 一次就够：这张表在一次运行里不会再变
            if (_done) return;

            _done = true;

            List<MatrixRow> rows = Collect();

            if (rows == null) return;

            Dump(rows);
            CheckSpeed(rows);
        }

        // ── 采集 ──────────────────────────────────────────────

        private struct MatrixRow
        {
            internal ItemProto Item;
            internal RecipeProto Recipe;
            internal int Ticks;
        }

        /// <summary>
        /// 按 <c>LabComponent.matrixIds</c> 逐个反查产出配方。
        ///
        /// 反查 <c>Results</c> 而不是读 <c>ItemProto.maincraft</c>：后者可能为 null，
        /// 而且一件东西可以有多条产出路径（本 mod 的铝就有碳热和电解两条），
        /// 只打一条会把「还有别的路子」看成「只有这一条」。
        /// </summary>
        private static List<MatrixRow> Collect()
        {
            int[] ids = LabComponent.matrixIds;

            if (ids == null || ids.Length == 0)
            {
                // 报无聊的那一面：没有矩阵和这段代码没跑起来，看日志得能分清
                ProjectEdenPlugin.Log.LogWarning(
                    "矩阵普查：LabComponent.matrixIds 是空的 —— 这不正常，原版至少有六种");

                return null;
            }

            RecipeProto[] recipes = LDB.recipes?.dataArray;

            if (recipes == null)
            {
                ProjectEdenPlugin.Log.LogWarning("矩阵普查：LDB.recipes 还没建好，跳过");

                return null;
            }

            var rows = new List<MatrixRow>();

            foreach (int id in ids)
            {
                ItemProto item = LDB.items?.Select(id);

                if (item == null)
                {
                    // matrixIds 里有号、LDB 里没有 proto，是槽位表和物品表脱节，
                    // 而那会让实验室按一个不存在的东西算槽位——必须响
                    ProjectEdenPlugin.Log.LogWarning(
                        $"矩阵普查：matrixIds 里有 {id}，但 LDB.items 里没有这个 proto");

                    continue;
                }

                var found = 0;

                foreach (RecipeProto recipe in recipes)
                {
                    if (recipe?.Results == null) continue;

                    var makes = false;

                    for (var i = 0; i < recipe.Results.Length; i++)
                        if (recipe.Results[i] == id)
                            makes = true;

                    if (!makes) continue;

                    found++;

                    rows.Add(new MatrixRow
                    {
                        Item = item,
                        Recipe = recipe,
                        Ticks = recipe.TimeSpend
                    });
                }

                if (found == 0)
                    // 一种没有任何配方产出的矩阵，是「注册了但没接上产线」的样子，
                    // 而它在日志里和「我没扫到」长得一样，所以显式说出来
                    rows.Add(new MatrixRow { Item = item, Recipe = null, Ticks = 0 });
            }

            return rows;
        }

        // ── 输出 ──────────────────────────────────────────────

        private static void Dump(List<MatrixRow> rows)
        {
            ProjectEdenPlugin.Log.LogInfo(
                "── 矩阵生产时间（TimeSpend 单位是帧，60 帧 = 1 秒，取自 UIRecipeEntry.SetRecipe 的 TimeSpend/60f）──");

            var slowest = 0;
            string slowestName = null;

            foreach (MatrixRow row in rows)
            {
                if (row.Recipe == null)
                {
                    ProjectEdenPlugin.Log.LogWarning(
                        $"  {row.Item.Name}({row.Item.ID})  **没有任何配方产出它** —— "
                        + "它进得了实验室的矩阵槽，却造不出来");

                    continue;
                }

                var sb = new StringBuilder("  ")
                         .Append(row.Item.Name).Append('(').Append(row.Item.ID).Append(')')
                         .Append("  ").Append(row.Ticks).Append(" 帧 = ")
                         .Append((row.Ticks / TicksPerSecond).ToString("0.##")).Append(" 秒")
                         .Append("  ← 配方「").Append(row.Recipe.Name).Append("」")
                         .Append(row.Recipe.Type);

                // 原版类型直接用枚举名（Smelt/Research/…），自定义类型（9 起）没有名字，
                // ToString 会打数字，再补上本 mod 注册的机器名
                string machine = MachineRegistry.RecipeTypeMachineName((int)row.Recipe.Type);

                if (!string.IsNullOrEmpty(machine)) sb.Append('/').Append(machine);

                sb.Append("  ");
                AppendSide(sb, row.Recipe.Items, row.Recipe.ItemCounts);
                sb.Append(" → ");
                AppendSide(sb, row.Recipe.Results, row.Recipe.ResultCounts);

                ProjectEdenPlugin.Log.LogInfo(sb.ToString());

                long main = (long)row.Ticks * MainScale;
                long extra = (long)row.Ticks * ExtraScale;

                ProjectEdenPlugin.Log.LogInfo(
                    $"      主轨 timeSpend {main}（帧 × {MainScale}）　"
                    + $"增产轨 extraTimeSpend {extra}（帧 × {ExtraScale}）"
                    + (main > SlowRecipeGate
                           ? $"　⚠ 超过原版 {SlowRecipeGate} 那道闸：原版只囤 6 份原料，本 mod 由 assembleStorage 接管"
                           : string.Empty));

                if (row.Ticks <= slowest) continue;

                slowest = row.Ticks;
                slowestName = row.Item.Name;
            }

            // 数的是**矩阵种数**，不是行数 —— 一种矩阵可以有多条产出路径，
            // 按行数报会把「铝那样的两条路」说成「多了一种矩阵」
            var kinds = new HashSet<int>();

            foreach (MatrixRow row in rows) kinds.Add(row.Item.ID);

            // 全都一样长的时候不能说「最慢的是 X」——那是在陈述一个并不存在的区别
            // （循环里 `>` 只是取了第一个）。日志声称一个假分辨，是本仓库反复付账的形状
            var tied = 0;

            foreach (MatrixRow row in rows)
                if (row.Recipe != null && row.Ticks == slowest)
                    tied++;

            string which = slowestName == null ? "（无）"
                : tied > 1 ? $"{tied} 条并列"
                : slowestName;

            ProjectEdenPlugin.Log.LogInfo(
                $"  共 {kinds.Count} 种矩阵（{rows.Count} 条配方），最长的是 {which} "
                + $"{slowest} 帧 = {(slowest / TicksPerSecond):0.##} 秒");
        }

        private static void AppendSide(StringBuilder sb, int[] ids, int[] counts)
        {
            if (ids == null || ids.Length == 0)
            {
                // 零投入配方是真实存在的（生物温室的光合育林），不是解析失败
                sb.Append("（无投入）");

                return;
            }

            for (var i = 0; i < ids.Length; i++)
            {
                if (i > 0) sb.Append(" + ");

                ItemProto proto = LDB.items?.Select(ids[i]);

                sb.Append(proto != null ? proto.Name : ids[i].ToString())
                  .Append('×')
                  .Append(counts != null && i < counts.Length ? counts[i] : 0);
            }
        }

        // ── 核对 assembleSpeed ────────────────────────────────

        /// <summary>
        /// 拿实测的最慢配方去核对配置的速度够不够「一帧填满」。
        ///
        /// <b>这才是这个普查真正要干的事。</b> <c>lab.json</c> 的注释里写着
        /// 「最慢的引力矩阵 24 秒 = 1440 tick，主轨需要 1440 万、增产额外轨需要 1.44 亿」——
        /// 那是**一次性手算**，而它依赖的两个输入（哪个配方最慢、喷涂表给多少倍）
        /// 都可能变：装个内容 mod 加一种更慢的矩阵，或者原版更新调了
        /// <c>Cargo.incTableMilli</c>，那句注释就悄悄变成假的，而症状是
        /// 「研究站莫名其妙慢了」——没有任何报错。
        ///
        /// 两条轨是 <c>InternalUpdateAssemble</c> 里**互斥**的两支（IL 03A0–0422）：
        /// <list type="bullet">
        /// <item>增产模式：<c>extraSpeed = (int)(speed × incTableMilli[级] × 10 + 0.1)</c>，
        /// 而 <c>speedOverride = speed</c>（@03CD）—— 所以<b>主轨在这一支上没有加成</b>，
        /// 核对主轨要按这个最坏情况算。</item>
        /// <item>加速模式：<c>extraSpeed = 0</c>，
        /// <c>speedOverride = (int)(speed × (1 + accTableMilli[级]) + 0.1)</c>。</item>
        /// </list>
        /// 每帧的累加是 <c>time += (int)(power × speedOverride)</c>（@044A）和
        /// <c>extraTime += (int)(power × extraSpeed)</c>（@0461），所以
        /// <b>「一帧填满」就是 speedOverride ≥ timeSpend、extraSpeed ≥ extraTimeSpend</b>。
        ///
        /// <b>喷涂倍率是从 <c>Cargo.incTableMilli</c> 读最大值，不是写死 0.25。</b>
        /// 那张表和配方时间一样在 <c>resources.assets</c> 里——写死就是又一个
        /// 「凭记忆的档位」，正是这个文件存在的理由。
        /// </summary>
        private static void CheckSpeed(List<MatrixRow> rows)
        {
            LabConfig cfg = ProjectEdenPlugin.LabConfig;
            int speed = cfg?.assembleSpeed ?? 0;

            if (speed <= 0)
            {
                // 报无聊的那一面：保持原版和「这段核对没跑」不能长得一样
                ProjectEdenPlugin.Log.LogInfo(
                    "  矩阵速度核对：lab.json 的 assembleSpeed 是 0（保持原版 1 倍速），不核对");

                return;
            }

            var slowest = 0;
            string slowestName = null;

            foreach (MatrixRow row in rows)
            {
                if (row.Recipe == null || row.Ticks <= slowest) continue;

                slowest = row.Ticks;
                slowestName = row.Item.Name;
            }

            if (slowest <= 0)
            {
                ProjectEdenPlugin.Log.LogWarning("  矩阵速度核对：一条产出矩阵的配方都没扫到，无法核对");

                return;
            }

            double maxInc = MaxIncMilli();

            long needMain = (long)slowest * MainScale;
            long needExtra = (long)slowest * ExtraScale;

            // 主轨最坏情况：增产模式那一支里 speedOverride 就等于 speed，没有加成
            long haveMain = speed;
            long haveExtra = (long)(speed * maxInc * 10 + 0.1);

            ProjectEdenPlugin.Log.LogInfo(
                $"── 矩阵速度核对（assembleSpeed = {speed}，{speed / (double)MainScale:0.##} 倍；"
                + $"最慢配方 {slowestName} {slowest} 帧）──");

            Report("主轨", needMain, haveMain,
                   "speedOverride 在增产模式下就等于 speed（IL @03CD），按这个最坏情况算");

            Report("增产轨", needExtra, haveExtra,
                   $"extraSpeed = speed × max(incTableMilli)={maxInc:0.###} × 10（IL @03A2–03C6）");

            // Int32 余量：speedOverride 和 extraSpeed 都是 Int32，撑爆了会变负数，
            // 而负的每帧累加意味着永远填不满 timeSpend —— 停产，且不报错
            long peakOverride = (long)(speed * (1 + MaxAccMilli()) + 0.1);
            long peak = peakOverride > haveExtra ? peakOverride : haveExtra;

            if (peak > int.MaxValue)
                ProjectEdenPlugin.Log.LogError(
                    $"  ⚠ speedOverride/extraSpeed 的峰值 {peak} 超出 Int32（{int.MaxValue}）—— "
                    + "会溢出成负数，每帧累加变负、配方永远填不满，而且一句报错都没有。请调小 assembleSpeed");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"  Int32 余量：峰值 {peak}，上限 {int.MaxValue}（还差 {int.MaxValue / (double)peak:0.#} 倍）");
        }

        private static void Report(string track, long need, long have, string how)
        {
            if (have >= need)
                ProjectEdenPlugin.Log.LogInfo(
                    $"  {track}：需要 {need}，实际 {have} → 够（余量 {have / (double)need:0.##} 倍）。{how}");
            else
                ProjectEdenPlugin.Log.LogWarning(
                    $"  {track}：需要 {need}，实际只有 {have} → **不够**，最慢的矩阵填不满一帧，"
                    + $"产能会低于每秒 60 个。{how}");
        }

        /// <summary>
        /// <c>Cargo.incTableMilli</c> 的最大值 = 满级喷涂给增产轨的倍率。
        /// 读不到就退回 0（核对会因此报「不够」而不是悄悄放过）。
        /// </summary>
        private static double MaxIncMilli() => MaxOf(Cargo.incTableMilli);

        /// <summary><c>Cargo.accTableMilli</c> 的最大值 = 满级喷涂给加速模式的倍率。</summary>
        private static double MaxAccMilli() => MaxOf(Cargo.accTableMilli);

        private static double MaxOf(double[] table)
        {
            if (table == null || table.Length == 0)
            {
                ProjectEdenPlugin.Log.LogWarning("矩阵速度核对：喷涂倍率表读不到，按 0 处理（核对会偏保守）");

                return 0.0;
            }

            double max = table[0];

            for (var i = 1; i < table.Length; i++)
                if (table[i] > max)
                    max = table[i];

            return max;
        }
    }
}
