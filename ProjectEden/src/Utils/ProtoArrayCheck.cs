using System.Collections.Generic;
using System.Text;

namespace ProjectEden.Utils
{
    /// <summary>
    /// 检查本 mod 注册的 proto 里那些<b>数组字段有没有留成 null</b>。
    ///
    /// <b>为什么要专门查这个。</b> 原版的 proto 是从 <c>resources.assets</c> 反序列化出来的，
    /// 数组字段一律是真数组（可能长度为 0，但不是 null）。而本仓库的注册器有两种写法：
    /// 大多数是<b>从某个原版 proto 抄一份</b>（<c>DescFields = source.DescFields</c>），
    /// 少数是<b>从零 new 一个</b>——后者只要漏写一个数组字段就是 null，
    /// 而<b>注册、图标、配方、本地化全都正常</b>，只有在某条原版代码对它做
    /// <c>ldlen</c> / 遍历时才炸。
    ///
    /// 钻头就是这么炸的：<c>DrillBitRegistry</c> 手工 new 了 <c>ItemProto</c> 但没给
    /// <c>DescFields</c>，于是鼠标一悬停到钻头上，
    /// <c>UIItemTip.SetTip</c> 在 <c>ldfld DescFields ; ldlen</c> 处
    /// <c>NullReferenceException</c>。而报错栈里点名的是 LDBTool 和 UXAssist
    /// （它们各自在 <c>SetTip</c> / <c>UIButton.LateUpdate</c> 上有补丁），
    /// <b>本 mod 一个字都没出现</b>——因为空的是数据，不是代码。
    ///
    /// 所以这条检查的价值不在于"又加了一个断言"，而在于
    /// <b>它把一个「报错指向别人」的故障变成了启动时的一行警告</b>。
    /// </summary>
    internal static class ProtoArrayCheck
    {
        internal static void Verify()
        {
            var bad = new List<string>();
            var items = 0;
            var recipes = 0;

            foreach (int id in ProtoSlots.OwnItemIds)
            {
                ItemProto item = LDB.items?.Select(id);

                if (item == null) continue;

                items++;

                if (item.DescFields == null) bad.Add($"{item.Name}({id}).DescFields");
                if (item.Upgrades == null) bad.Add($"{item.Name}({id}).Upgrades");
            }

            foreach (int id in ProtoSlots.OwnRecipeIds)
            {
                RecipeProto recipe = LDB.recipes?.Select(id);

                if (recipe == null) continue;

                recipes++;

                if (recipe.Items == null) bad.Add($"{recipe.Name}({id}).Items");
                if (recipe.ItemCounts == null) bad.Add($"{recipe.Name}({id}).ItemCounts");
                if (recipe.Results == null) bad.Add($"{recipe.Name}({id}).Results");
                if (recipe.ResultCounts == null) bad.Add($"{recipe.Name}({id}).ResultCounts");
            }

            ReportPages();

            // 没问题也报一行：否则「查过了没事」和「这段检查没跑」分不开
            if (bad.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"proto 数组字段核对通过：{items} 个物品、{recipes} 条配方，没有 null 数组");

                return;
            }

            var sb = new StringBuilder(
                $"以下 proto 的数组字段是 null，原版代码对它做 ldlen 或遍历时会直接 NRE，"
                + $"而报错栈多半指向别的 mod（空的是数据不是代码）：");

            foreach (string s in bad) sb.Append("\n  ").Append(s);

            sb.Append("\n  修法：手工 new proto 时把这些字段补成空数组，或从某个原版 proto 抄一份");

            ProjectEdenPlugin.Log.LogError(sb.ToString());
        }

        /// <summary>
        /// 本 mod 的物品与配方各落在哪一页、有没有越过可见列。
        ///
        /// <b>这条是「不要挤占第一页」那次改动的验收线。</b>
        /// 格位分页是纯数据，改错了不会报错——只会让某件物品在
        /// 掉落过滤和信号选取窗口里<b>静悄悄地不存在</b>（那两个窗口硬裁 14 列，
        /// 而且没有横向翻页兜底）。所以改完必须有一处能一眼看出结果的地方。
        ///
        /// 报的是**最终状态**：从 LDB 里回读 GridIndex，不是回读我们请求的值——
        /// LDBTool 的 CustomGridIndex.cfg 有能力在注册之后把格位改回去
        /// （本 profile 实测 164 条全是 0 即无覆盖，但那是实测出来的，不是假定的）。
        /// </summary>
        private static void ReportPages()
        {
            Tally(ProtoSlots.OwnItemIds, true);
            Tally(ProtoSlots.OwnRecipeIds, false);
        }

        private static void Tally(IEnumerable<int> ids, bool item)
        {
            var pages = new Dictionary<int, int>();
            var overflow = new List<string>();
            var total = 0;

            foreach (int id in ids)
            {
                int grid;
                string name;

                if (item)
                {
                    ItemProto p = LDB.items?.Select(id);

                    if (p == null) continue;

                    grid = p.GridIndex;
                    name = p.Name;
                }
                else
                {
                    RecipeProto p = LDB.recipes?.Select(id);

                    if (p == null) continue;

                    grid = p.GridIndex;
                    name = p.Name;
                }

                total++;

                int page = grid / 1000;
                pages[page] = pages.TryGetValue(page, out int n) ? n + 1 : 1;

                if (grid % 100 > ProtoSlots.VisibleCols) overflow.Add($"{name}({grid})");
            }

            var sb = new StringBuilder(item ? "本 mod 物品格位分布：" : "本 mod 配方格位分布：");

            foreach (KeyValuePair<int, int> kv in pages)
                sb.Append($"第 {kv.Key} 页 {kv.Value} 个　");

            sb.Append($"（共 {total}）");

            if (pages.TryGetValue(1, out int onFirst) && onFirst > 0)
                sb.Append($"\n  注意：还有 {onFirst} 个留在第 1 页——那一页原版已占 111/112 格");

            if (overflow.Count > 0)
            {
                sb.Append($"\n  **{overflow.Count} 个越过了第 {ProtoSlots.VisibleCols} 列**"
                          + (item ? "，在掉落过滤与信号选取窗口里画不出来：" : "："));

                for (var i = 0; i < overflow.Count && i < 12; i++) sb.Append("\n    ").Append(overflow[i]);

                if (overflow.Count > 12) sb.Append($"\n    …另有 {overflow.Count - 12} 个");

                ProjectEdenPlugin.Log.LogWarning(sb.ToString());

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }
    }
}
