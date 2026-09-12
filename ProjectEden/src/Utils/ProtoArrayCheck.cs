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
    }
}
