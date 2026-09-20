// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Collections.Generic;

namespace ProjectEden.Utils
{
    /// <summary>
    /// <c>ores.json</c> 的配方引用 <c>machines.json</c> 的物品：**注册期解析不了，延后到
    /// <c>PostAddDataAction</c> 再填。**
    ///
    /// <b>为什么不能当场解析，也不能靠调换顺序解决。</b>
    /// <c>OreRegistry.FindItemIdByRef</c> 认三种写法——<c>ores.json</c> 自己 <c>items[]</c> 的
    /// key、<c>&lt;矿种key&gt;.ore</c> / <c>.ingot</c>、以及 <c>vanilla:中文名</c>。
    /// <b>它认不了 <c>machines.json</c> 注册的物品</b>（蓄能柜那一族就是），而那些物品在
    /// <c>OreRegistry.OnPreAddData</c> 跑的时候根本还不存在：
    ///
    /// <code>
    /// LDBTool.PreAddDataAction += MegaBuildingRegistry.OnPreAddData;
    /// LDBTool.PreAddDataAction += OreRegistry.OnPreAddData;      ← 这里想引用 ↓
    /// LDBTool.PreAddDataAction += MachineRegistry.OnPreAddData;  ← 这里才注册
    /// </code>
    ///
    /// **而且这个顺序不能调**：<c>machines.json</c> 的建造配方要用
    /// <c>OreRegistry.FindItemIdByRef</c> 解析自己的原料，机器必须排在矿之后——反过来就循环了。
    ///
    /// <b>为什么不干脆写死 ID。</b> 这正是 <c>vanilla:</c> 那条存在的理由，换一个命名空间而已：
    /// 写死一个号**不会报错**，只会安静地让配方吃进别的东西。而本 mod 自己的物品号还会因为
    /// 撞车自动顺延（<c>ResolveItemId</c>），写死的那一刻就可能已经错了。
    ///
    /// <b>延后改写是安全的，而且是免费的——这一条仓库里已经验过。</b>
    /// LDBTool 在 <c>PostAddDataAction</c> **之后**才调 <c>RecipeProto.InitRecipeItems</c>
    /// 重建整张 <c>recipeExecuteData</c>，所以这个阶段改 <c>Items</c> / <c>ItemCounts</c>
    /// 会被自动吸收，不用自己刷新任何缓存。宇宙矩阵加第七种原料、
    /// <c>RedoxRegistry.ApplyDefaultToProto</c> 改两个投料位，靠的都是同一件事。
    ///
    /// **只改值，绝不改长度**：长度是存档相关的（<c>AssemblerComponent.Export</c> 按
    /// <c>requires</c> / <c>products</c> 的长度决定写几个 <c>served</c> / <c>produced</c>）。
    /// 这里填的是原料表里本来就有的那一格，长度一个字节都不动。
    /// </summary>
    internal static class LateItemRef
    {
        /// <summary>配置里的前缀。<c>"machine:电浆蓄能柜（满）"</c>。</summary>
        internal const string Prefix = "machine:";

        /// <summary>
        /// 注册期先塞进去的占位物品。
        ///
        /// <b>必须是一个真实存在的原版固体</b>，不能是 0：配方注册的时候会拿它去做各种
        /// 检查，一个不存在的 ID 会让整条配方在别处炸掉，而那个炸法和「引用写错了」
        /// 长得完全不一样，更难查。铁块（1101）人人都有，而且一定在 LDB 里。
        ///
        /// 真正的号在 <see cref="ResolveAll"/> 里替换掉；替换不成功会大声报错，
        /// **不会**留着占位悄悄发出去——那才是最坏的结果（配方能跑，吃的是铁块）。
        /// </summary>
        private const int PlaceholderItemId = 1101;

        private struct Pending
        {
            internal RecipeProto Recipe;
            internal bool IsInput;
            internal int Index;
            internal string Name;      // 要找的物品名
            internal string Where;     // 报错时说清是哪条配方的哪一侧
        }

        private static readonly List<Pending> Queue = new List<Pending>();

        /// <summary>
        /// <c>ref</c> 是不是一个延后引用。是的话把要找的名字抠出来。
        /// </summary>
        internal static bool IsLate(string @ref, out string name)
        {
            name = null;

            if (string.IsNullOrEmpty(@ref)) return false;
            if (!@ref.StartsWith(Prefix, System.StringComparison.Ordinal)) return false;

            name = @ref.Substring(Prefix.Length);

            return name.Length > 0;
        }

        /// <summary>注册期调：返回占位号，同时把「待会儿要回来改哪一格」记下来。</summary>
        internal static int Reserve() => PlaceholderItemId;

        /// <summary>
        /// 把一格待解析的原料登记进队列。配方对象是引用，所以到了
        /// <see cref="ResolveAll"/> 的时候直接改它的数组就行。
        /// </summary>
        internal static void Record(RecipeProto recipe, bool isInput, int index, string name, string where)
        {
            if (recipe == null || string.IsNullOrEmpty(name)) return;

            Queue.Add(new Pending
            {
                Recipe = recipe,
                IsInput = isInput,
                Index = index,
                Name = name,
                Where = where,
            });
        }

        /// <summary>
        /// <c>PostAddDataAction</c> 处理器。**必须排在 <c>MachineRegistry.OnPostAddData</c>
        /// 之后**，否则要找的物品还没进 LDB。
        ///
        /// 比的是 <c>Proto.Name</c>（原始键）而不是 <c>proto.name</c>（翻译后的）——
        /// 后者在英文客户端下全都对不上，这是本仓库栽过一次的那条。
        /// </summary>
        internal static void ResolveAll()
        {
            if (Queue.Count == 0)
            {
                // **没有活儿也要报一行。** 「一条都没登记」和「这段代码压根没跑」
                // 在日志里长得一样，而这条规矩本文件所在的仓库已经付过六次账。
                ProjectEdenPlugin.Log.LogInfo(
                    $"延后物品引用：这一局没有任何配方用到「{Prefix}名字」，没有要解析的。");

                return;
            }

            ItemProto[] all = LDB.items?.dataArray;

            if (all == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"延后物品引用：LDB.items 还没建好，{Queue.Count} 处引用一个都没解析——"
                    + "这些配方会吃着占位的铁块跑。检查注册顺序。");

                return;
            }

            var ok = 0;
            var failed = 0;

            foreach (Pending p in Queue)
            {
                var id = 0;

                foreach (ItemProto proto in all)
                {
                    if (proto == null || proto.Name != p.Name) continue;

                    id = proto.ID;

                    break;
                }

                if (id <= 0)
                {
                    failed++;

                    ProjectEdenPlugin.Log.LogError(
                        $"延后物品引用：{p.Where} 要的「{p.Name}」在 LDB 里找不到。"
                        + $"那一格还占着铁块({PlaceholderItemId})——**配方能跑，但吃的是错的东西**。"
                        + "名字要和 machines.json / ores.json 里的 displayName 或 name 一字不差。");

                    continue;
                }

                int[] ids = p.IsInput ? p.Recipe.Items : p.Recipe.Results;

                if (ids == null || p.Index < 0 || p.Index >= ids.Length)
                {
                    failed++;

                    ProjectEdenPlugin.Log.LogError(
                        $"延后物品引用：{p.Where} 的第 {p.Index} 格超出了数组范围，跳过。"
                        + "配方注册之后原料表的长度被人改过？");

                    continue;
                }

                ids[p.Index] = id;
                ok++;

                ProjectEdenPlugin.Log.LogInfo(
                    $"延后物品引用：{p.Where} 的「{p.Name}」解析到 {id}，已替换占位。");
            }

            if (failed > 0)
                ProjectEdenPlugin.Log.LogError(
                    $"延后物品引用：{Queue.Count} 处里有 {failed} 处没解析出来（见上面几行）。"
                    + "**这些配方会安静地吃错东西**，不是跑不起来——所以这条是 ERROR 不是 WARNING。");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"延后物品引用：{ok} 处全部解析完毕。");
        }
    }
}
