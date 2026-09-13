#pragma warning disable 649 // 配置类的字段由 JSON 反序列化赋值

using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 合金的逐建筑配比：滑动条连续、产物离散。<b>一张表驱动</b>，
    /// 要让一种合金支持配比，在 <c>alloys.json</c> 里加一条，不改代码。
    ///
    /// <b>引擎确实允许逐建筑改配方，但不能原地改。</b>
    /// <c>RecipeExecuteData</c> 是 <b>class 不是 struct</b>，<c>AssemblerComponent.SetRecipe</c>
    /// 在 IL 0075 处从 <c>RecipeProto</c> 上的<b>静态字典</b>里取出对象、0085 处把<b>引用</b>
    /// 存进实例字段。跑同一条配方的每一台机器、每一颗星球，拿到的是同一个对象——
    /// 原地改 <c>requireCounts[0]</c> 就是改全局配方表，而且眼前那台确实变了，
    /// 看起来完全像是"只改了这一台"。这是最难查的一类 bug。
    ///
    /// 正确做法是<b>让那个引用字段指向我们自己 new 的一份</b>。已验证的两个前提：
    /// <list type="bullet">
    /// <item>那个静态字典在装配器里只有 3 个读取点（<c>SetRecipe</c> 一次、<c>Import</c> 两处），
    /// <c>InternalUpdate</c> / <c>UpdateNeeds</c> 全程只读实例字段 —— 克隆在 tick 路径上不会被冲掉，
    /// 也不需要 transpiler，热路径零开销。</item>
    /// <item><c>RecipeExecuteData</c> 有公开构造函数，参数正好是那七个字段，
    /// <c>Clone()</c> 天然保住数组长度。</item>
    /// </list>
    ///
    /// <b>一条硬约束：可以改值，绝不能改数组长度。</b>
    /// <c>AssemblerComponent.Export</c> 在 IL 00D5 / 019E 处拿
    /// <c>recipeExecuteData.requires</c> / <c>products</c> 的<b>长度</b>决定往存档里写几条
    /// <c>served</c> / <c>produced</c>。长度一变，读档时重建出来的数组和流里的条数对不上，会坏档。
    /// 所以两根滑动条只能在<b>已经存在的两个原料槽</b>之间挪份数，不能加料减料。
    ///
    /// <b>为什么产物必须量化。</b> 产物是 <c>products[0]</c>，一个 int itemId，
    /// 承载不了连续的属性向量——这就是为什么每种要配比的合金都必须自带一组牌号物品。
    /// 输入连续（受份数粒度限制）、产出 snap 到牌号，这个错配正是玩法。
    /// </summary>
    [HarmonyPatch]
    internal static class AlloyRatioPatches
    {
        /// <summary>解析好的一种合金。</summary>
        internal class Alloy
        {
            internal AlloyEntry Entry;

            /// <summary>每个配置槽在 <c>requires[]</c> 里的下标，和 Entry.slots 一一对应</summary>
            internal int[] SlotIndex;

            /// <summary>每个槽的显示名，面板上做标签用</summary>
            internal string[] SlotName;

            /// <summary>唯一的产物物品。<b>不再随配比切换</b>——配比结算到产量和耗时上</summary>
            internal int ProductItemId;

            /// <summary>配方本身声明的基准产量与基准耗时，结算时乘倍率用</summary>
            internal int BaseYield;
            internal int BaseTime;

            /// <summary>
            /// 这种合金在它自己的滑动条行程内，加权质量能取到的最小/最大值。
            ///
            /// <b>注册时穷举出来的，不是配的。</b> 各合金四维值的量纲差得很远
            /// （钒钛合金全程只动 1%，锰钢能动 23%），直接拿原始质量去换算，
            /// 有的合金滑动条形同虚设。按各自的可达区间归一之后，
            /// 每根滑动条的行程才落在同一个可读的量级上。
            /// </summary>
            internal float QMin, QMax;

            /// <summary>可调槽的个数 = slots.Length − 1（第 0 个是余量）</summary>
            internal int Adjustable => Entry.slots.Length - 1;
        }

        private static readonly Dictionary<int, Alloy> ByRecipe = new Dictionary<int, Alloy>();

        internal static int Count => ByRecipe.Count;

        internal static Alloy Find(int recipeId) =>
            ByRecipe.TryGetValue(recipeId, out Alloy alloy) ? alloy : null;

        // ── 注册 ──────────────────────────────────────────────

        internal static void OnPostAddData()
        {
            ByRecipe.Clear();

            AlloysConfig cfg = ProjectEdenPlugin.AlloysConfig;

            if (cfg == null || !cfg.enabled || cfg.alloys == null) return;

            foreach (AlloyEntry e in cfg.alloys)
            {
                if (e == null) continue;

                Alloy alloy = Resolve(e);

                if (alloy == null) continue;

                if (ByRecipe.ContainsKey(e.recipeId))
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"合金配比：配方 {e.recipeId} 被「{e.key}」重复登记，后一条已跳过");

                    continue;
                }

                ByRecipe[e.recipeId] = alloy;

                var slots = new List<string>();

                for (var i = 1; i < e.slots.Length; i++)
                    slots.Add($"{alloy.SlotName[i]} {e.slots[i].min}~{e.slots[i].max}");

                ProjectEdenPlugin.Log.LogInfo(
                    $"合金配比「{e.key}」已就绪：余量 {alloy.SlotName[0]}，可调 {string.Join(" / ", slots)}，" +
                    $"总份数 {e.totalParts}（粒度 {100f / e.totalParts:0.#}%），产物 {alloy.SlotName.Length} 味料合一，" +
                    $"{alloy.Adjustable} 个自由度" +
                    $"（{(e.model == "solution" ? "固溶体·标称锚点" : "复合材料·混合律")}）");
            }

            if (ByRecipe.Count == 0)
                ProjectEdenPlugin.Log.LogWarning("合金配比：一条都没配上，冶炼炉窗口里不会出现配比面板");
        }

        /// <summary>原料槽解析成物品 ID。<b>原版物品写 id，本 mod 的写 ref</b>。</summary>
        private static int SlotItemId(AlloySlot slot) =>
            slot == null ? 0 : slot.id > 0 ? slot.id : OreRegistry.FindItemIdByRef(slot.@ref);

        private static string Describe(AlloySlot slot) =>
            slot == null ? "(空)" : slot.id > 0 ? "id " + slot.id : slot.@ref;

        private static Alloy Resolve(AlloyEntry e)
        {
            string label = string.IsNullOrEmpty(e.key) ? e.recipeId.ToString() : e.key;

            RecipeProto recipe = LDB.recipes.Select(e.recipeId);

            if (recipe == null)
            {
                ProjectEdenPlugin.Log.LogError($"合金配比「{label}」：配方 {e.recipeId} 不存在，跳过");

                return null;
            }

            if (e.totalParts <= 0 || e.slots == null || e.slots.Length < 2)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"合金配比「{label}」：总份数或槽位没配全（槽位至少两个：一味余量 + 一味可调），跳过");

                return null;
            }

            if (e.model == "solution" && e.nominal == null)
            {
                ProjectEdenPlugin.Log.LogError($"合金配比「{label}」：固溶体模型要 nominal，没配，跳过");

                return null;
            }

            var index = new int[e.slots.Length];
            var names = new string[e.slots.Length];

            for (var i = 0; i < e.slots.Length; i++)
            {
                AlloySlot slot = e.slots[i];
                int id = SlotItemId(slot);

                index[i] = id > 0 ? Array.IndexOf(recipe.Items, id) : -1;

                if (index[i] < 0)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"合金配比「{label}」：配方「{recipe.name}」的原料里定位不到 {Describe(slot)}({id})，跳过");

                    return null;
                }

                names[i] = LDB.items.Select(id)?.name ?? Describe(slot);

                if (i == 0)
                {
                    if (e.model == "solution" || slot.props != null) continue;

                    ProjectEdenPlugin.Log.LogError(
                        $"合金配比「{label}」：复合材料的余量槽没配 props，跳过");

                    return null;
                }

                if (slot.min > slot.max || slot.nominal < slot.min || slot.nominal > slot.max)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"合金配比「{label}」：可调槽 {names[i]} 的 min/max/nominal 不自洽，跳过");

                    return null;
                }

                if (e.model == "solution" && slot.slopes == null)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"合金配比「{label}」：固溶体的可调槽 {names[i]} 没配 slopes，跳过");

                    return null;
                }

                if (e.model != "solution" && slot.props == null)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"合金配比「{label}」：复合材料的槽 {names[i]} 没配 props，跳过");

                    return null;
                }
            }

            for (var i = 0; i < index.Length; i++)
            for (var j = i + 1; j < index.Length; j++)
                if (index[i] == index[j])
                {
                    ProjectEdenPlugin.Log.LogError($"合金配比「{label}」：两个槽指向同一味料，跳过");

                    return null;
                }

            // 余量在任何合法配比下都必须还有剩，否则这条配方摆不平
            var used = 0;

            for (var i = 1; i < e.slots.Length; i++) used += e.slots[i].max;

            if (used >= e.totalParts)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"合金配比「{label}」：可调槽的上限加起来 {used} 已吃满总份数 {e.totalParts}，余量会归零，跳过");

                return null;
            }

            if (e.quality == null || e.quality.weights == null)
            {
                ProjectEdenPlugin.Log.LogError($"合金配比「{label}」：没配 quality.weights，跳过");

                return null;
            }

            RecipeProto proto = LDB.recipes.Select(e.recipeId);

            if (proto == null || proto.Results == null || proto.Results.Length == 0)
            {
                ProjectEdenPlugin.Log.LogError($"合金配比「{label}」：配方 {e.recipeId} 查不到或没有产物，跳过");

                return null;
            }

            var alloy = new Alloy
            {
                Entry = e,
                SlotIndex = index,
                SlotName = names,
                ProductItemId = proto.Results[0],
                BaseYield = e.baseYield > 0 ? e.baseYield : proto.ResultCounts[0],
                BaseTime = e.baseTimeSpend > 0 ? e.baseTimeSpend : proto.TimeSpend,
            };

            Calibrate(alloy, label);

            return alloy;
        }

        /// <summary>
        /// 穷举这种合金滑动条能走到的<b>每一种</b>配比，记下加权质量的上下界，并把结果打进日志。
        ///
        /// <b>为什么必须穷举而不是估。</b> 归一化的分母就是这个区间，估错了滑动条要么失灵、
        /// 要么全程都顶在两端。而且这段日志顺带就是这条配方的验收单——
        /// 「最优配比落在哪、能换来多少产量」是照着日志读的，不用另写文档。
        ///
        /// 可调槽最多两味、行程也就十几档，最坏几百次，都在注册期跑完，不上 tick 路径。
        /// </summary>
        private static void Calibrate(Alloy alloy, string label)
        {
            AlloyEntry e = alloy.Entry;

            var parts = new int[alloy.Adjustable];

            alloy.QMin = float.MaxValue;
            alloy.QMax = float.MinValue;

            int[] bestParts = null, worstParts = null;

            Walk(0);

            void Walk(int depth)
            {
                if (depth == parts.Length)
                {
                    if (BalanceParts(alloy, parts) <= 0) return;

                    float q = RawQuality(alloy, parts);

                    if (q < alloy.QMin) { alloy.QMin = q; worstParts = (int[])parts.Clone(); }
                    if (q > alloy.QMax) { alloy.QMax = q; bestParts = (int[])parts.Clone(); }

                    return;
                }

                AlloySlot slot = e.slots[depth + 1];

                for (int v = slot.min; v <= slot.max; v++)
                {
                    parts[depth] = v;

                    Walk(depth + 1);
                }
            }

            if (bestParts == null || alloy.QMax - alloy.QMin < 1e-6f)
            {
                // 上下界重合意味着滑动条怎么拖质量都不变，结算就成了摆设。
                // 多半是 weights 写到了几乎不随配比变化的那几轴上。
                ProjectEdenPlugin.Log.LogWarning(
                    $"合金配比「{label}」：滑动条全程的加权质量几乎不变（{alloy.QMin:0.###}~{alloy.QMax:0.###}），" +
                    "产量和耗时将没有区别——检查 quality.weights 选的是不是随配比变化的那几轴");

                alloy.QMax = alloy.QMin + 1e-6f;

                return;
            }

            Settle(alloy, bestParts, out int bestYield, out int bestTime);
            Settle(alloy, worstParts, out int worstYield, out int worstTime);

            ProjectEdenPlugin.Log.LogInfo(
                $"合金配比「{label}」：最优 {Join(worstParts.Length, bestParts)} → 产量 {bestYield}、耗时 {bestTime / 60f:0.##} 秒；" +
                $"最差 {Join(worstParts.Length, worstParts)} → 产量 {worstYield}、耗时 {worstTime / 60f:0.##} 秒" +
                $"（基准 产量 {alloy.BaseYield}、耗时 {alloy.BaseTime / 60f:0.##} 秒）");
        }

        private static string Join(int n, int[] v)
        {
            var sb = new System.Text.StringBuilder("[");

            for (var i = 0; i < n; i++)
            {
                if (i > 0) sb.Append('/');

                sb.Append(v[i]);
            }

            return sb.Append(']').ToString();
        }

        // ── 两套模型 ──────────────────────────────────────────
        //
        // <b>复合材料（composite）</b>：两相各自保持本性，走 Voigt–Reuss 混合律——
        // 硬度取幂平均（上界）、韧性取调和平均（被最弱环节拖住）。属性<b>一定落在组元之间</b>。
        //
        // <b>固溶体（solution）</b>：合金元素溶进晶格，靠固溶强化、析出强化、细化晶粒
        // <b>让整体超过任何一个组元</b>——锰钢韧性高于铁也高于锰，这正是人类炼合金的理由。
        // <b>插值模型永远算不出这种数</b>，硬套等于宣布「合金不可能比组元强」。
        // 所以固溶体以标称成分为锚点，每个可调槽离开自己的锚点多远就按 slopes 偏移多少。

        /// <summary>余量槽的份数 = 总份数 − 可调槽之和。</summary>
        internal static int BalanceParts(Alloy alloy, int[] parts)
        {
            int rest = alloy.Entry.totalParts;

            for (var i = 0; i < parts.Length; i++) rest -= parts[i];

            return rest;
        }

        internal static void Mix(Alloy alloy, int[] parts, out float h, out float t, out float c, out float e)
        {
            AlloyEntry cfg = alloy.Entry;

            if (cfg.model == "solution")
            {
                h = cfg.nominal.hardness;
                t = cfg.nominal.toughness;
                c = cfg.nominal.corrosion;
                e = cfg.nominal.conductivity;

                for (var i = 0; i < parts.Length; i++)
                {
                    AlloySlot slot = cfg.slots[i + 1];

                    float d = parts[i] - slot.nominal;

                    h += slot.slopes.hardness * d;
                    t += slot.slopes.toughness * d;
                    c += slot.slopes.corrosion * d;
                    e += slot.slopes.conductivity * d;
                }

                return;
            }

            double sumH = 0.0, invT = 0.0, sumC = 0.0, sumE = 0.0;

            for (var i = 0; i < cfg.slots.Length; i++)
            {
                int n = i == 0 ? BalanceParts(alloy, parts) : parts[i - 1];
                double w = (double)n / cfg.totalParts;
                AlloyProps p = cfg.slots[i].props;

                sumH += w * p.hardness * p.hardness;
                invT += w / (p.toughness > 0f ? p.toughness : 1f);
                sumC += w * p.corrosion;
                sumE += w * p.conductivity;
            }

            h = (float)Math.Sqrt(sumH);
            t = (float)(invT > 0.0 ? 1.0 / invT : 0.0);
            c = (float)sumC;
            e = (float)sumE;
        }

        /// <summary>
        /// 轴名，<b>给人看的那一份</b>，不是键。
        ///
        /// <b>在这里翻译，不在注册时翻译。</b> 语言可以中途切换
        /// （<c>Localization.LoadLanguage</c>），在加载时把译文缓存下来会把它冻在
        /// 当时那一种语言上——面板上的四维读数会一直是启动时的语言。
        /// </summary>
        internal static string AxisName(string axis)
        {
            switch (axis)
            {
                case "toughness": return "韧性".Translate();
                case "corrosion": return "耐蚀".Translate();
                case "conductivity": return "导电".Translate();
                default: return "硬度".Translate();
            }
        }

        private static float Pick(string axis, float h, float t, float c, float e)
        {
            switch (axis)
            {
                case "toughness": return t;
                case "corrosion": return c;
                case "conductivity": return e;
                default: return h;
            }
        }

        /// <summary>
        /// 加权质量（未归一）：四维值的<b>加权几何平均</b>，权重逐合金配。
        ///
        /// <b>为什么是几何平均不是加权和。</b> 加权和对某一轴归零毫无反应——
        /// 把所有份数堆到一轴上换来的「高分」并不是一块能用的合金。
        /// 几何平均里任何一轴趋零整体就趋零，正好对上「合金要各项都说得过去」这件事，
        /// 而且它是<b>非线性</b>的：和线性的原料成本一配，最优点才可能落在行程内部，
        /// 而不是永远顶在滑动条端点上。
        ///
        /// 权重只写在意的那几轴（锰钢看韧性、不锈钢看耐蚀、硬质合金看硬度），
        /// 没写的轴不参与——它们仍然照常显示，只是不换算成产量。
        /// </summary>
        internal static float RawQuality(Alloy alloy, int[] parts)
        {
            Mix(alloy, parts, out float h, out float t, out float c, out float e);

            AlloyProps w = alloy.Entry.quality.weights;

            var product = 1.0;

            foreach (string axis in AlloyThresholds.Axes)
            {
                float weight = w.Get(axis);

                if (weight <= 0f) continue;

                double v = Pick(axis, h, t, c, e);

                if (v < 0.001) v = 0.001;

                product *= Math.Pow(v, weight);
            }

            return (float)product;
        }

        /// <summary>
        /// 归一到 [0, 1]：0 是这根滑动条能调出的最差配比，1 是最好的。
        /// 上下界由注册时穷举得到，见 <c>Calibrate</c>。
        /// </summary>
        internal static float Quality(Alloy alloy, int[] parts)
        {
            float span = alloy.QMax - alloy.QMin;

            if (span <= 0f) return 1f;

            float q = (RawQuality(alloy, parts) - alloy.QMin) / span;

            return q < 0f ? 0f : q > 1f ? 1f : q;
        }

        /// <summary>
        /// 把配比结算成两个 int：<b>产量</b>和<b>耗时</b>。
        ///
        /// 这两个都是「改值不改数组长度」，逐建筑安全——长度一变就坏档，见类注释。
        /// 产物物品<b>不再随配比切换</b>，所以也不存在「改配比把产物缓冲区里的货一起变质」
        /// 那条套利路径，原来的清空限制一并去掉了。
        ///
        /// <b>好配比不是免费的。</b> 可调槽装的就是稀有金属（铬、钒、钴、碳化钨），
        /// 往上拖等于每一炉多吃这些料。所以「拖满」不等于「最划算」——
        /// 划算与否取决于你眼下缺的是稀有金属还是产能，这件事游戏不替你决定。
        /// </summary>
        internal static void Settle(Alloy alloy, int[] parts, out int yield, out int timeSpend)
        {
            AlloyQualityEntry q = alloy.Entry.quality;

            float k = Quality(alloy, parts);

            float yieldMul = Lerp(q.yieldMin > 0f ? q.yieldMin : 0.8f,
                                  q.yieldMax > 0f ? q.yieldMax : 1.25f, k);

            // 耗时反着来：质量越高越快
            float timeMul = Lerp(q.timeMax > 0f ? q.timeMax : 1.2f,
                                 q.timeMin > 0f ? q.timeMin : 0.85f, k);

            yield = (int)Math.Round(alloy.BaseYield * yieldMul);
            timeSpend = (int)Math.Round(alloy.BaseTime * timeMul);

            if (yield < 1) yield = 1;
            if (timeSpend < 1) timeSpend = 1;
        }

        private static float Lerp(float a, float b, float k) => a + (b - a) * k;

        internal static int ClampParts(Alloy alloy, int slot, int value)
        {
            AlloySlot s = alloy.Entry.slots[slot + 1];

            if (value < s.min) return s.min;
            if (value > s.max) return s.max;

            return value;
        }

        internal static int[] DefaultParts(Alloy alloy)
        {
            int[] stored = AlloyRatioStore.GetPlayerDefault(alloy.Entry.recipeId);
            var parts = new int[alloy.Adjustable];

            for (var i = 0; i < parts.Length; i++)
                parts[i] = stored != null && stored.Length == parts.Length
                    ? ClampParts(alloy, i, stored[i])
                    : alloy.Entry.slots[i + 1].nominal;

            return parts;
        }

        // ── 贴克隆 ────────────────────────────────────────────

        private static bool Locate(PlanetFactory factory, int entityId, out int assemblerId, out Alloy alloy)
        {
            assemblerId = 0;
            alloy = null;

            if (factory == null || entityId <= 0 || entityId >= factory.entityCursor) return false;

            assemblerId = factory.entityPool[entityId].assemblerId;

            if (assemblerId <= 0) return false;

            alloy = Find(factory.factorySystem.assemblerPool[assemblerId].recipeId);

            return alloy != null;
        }

        internal static ApplyResult Apply(PlanetFactory factory, int entityId, int[] parts)
        {
            if (!Locate(factory, entityId, out int assemblerId, out Alloy alloy)) return ApplyResult.NotAlloy;
            if (parts == null || parts.Length != alloy.Adjustable) return ApplyResult.NotAlloy;

            AlloyEntry cfg = alloy.Entry;

            ref AssemblerComponent comp = ref factory.factorySystem.assemblerPool[assemblerId];

            RecipeExecuteData src = comp.recipeExecuteData;

            if (src == null) return ApplyResult.NotAlloy;

            var use = new int[parts.Length];

            for (var i = 0; i < parts.Length; i++) use[i] = ClampParts(alloy, i, parts[i]);

            int balance = BalanceParts(alloy, use);

            if (balance <= 0) return ApplyResult.NotAlloy;

            // 产量和耗时都从「基准值 × 质量倍率」现算。
            // **基准取自配方 proto，不是 src** —— src 很可能已经是我们上一次贴上去的克隆，
            // 拿它当基准会每贴一次就再乘一遍，几轮之后产量指数级跑飞，而且一声不响。
            Settle(alloy, use, out int yield, out int timeSpend);

            // Clone() 保住长度——长度一变就坏档，见类注释
            var requires = (int[])src.requires.Clone();
            var requireCounts = (int[])src.requireCounts.Clone();
            var products = (int[])src.products.Clone();
            var productCounts = (int[])src.productCounts.Clone();

            requireCounts[alloy.SlotIndex[0]] = balance;

            for (var i = 0; i < use.Length; i++) requireCounts[alloy.SlotIndex[i + 1]] = use[i];

            // 产物物品不动：一种合金只有一个物品，配比只改产量
            productCounts[0] = yield;

            comp.recipeExecuteData = new RecipeExecuteData(
                requires, requireCounts, products, productCounts,
                timeSpend, src.extraTimeSpend, src.productive);

            AlloyRatioStore.Set(factory.planetId, entityId, use);

            return ApplyResult.Ok;
        }


        private static void ApplyStoredOrDefault(PlanetFactory factory, int entityId)
        {
            if (ByRecipe.Count == 0) return;
            if (!Locate(factory, entityId, out _, out Alloy alloy)) return;

            int[] parts = AlloyRatioStore.TryGet(factory.planetId, entityId, out int[] stored)
                          && stored.Length == alloy.Adjustable
                ? stored
                : DefaultParts(alloy);

            Apply(factory, entityId, parts);
        }

        /// <summary>
        /// 这台建筑当前的配比。<b>读的是组件上的实时值</b>，不是存档里那份——
        /// 界面要显示的是机器现在真的在按什么配比跑。
        /// </summary>
        internal static bool Current(PlanetFactory factory, int entityId, out Alloy alloy, out int[] parts)
        {
            parts = null;

            if (!Locate(factory, entityId, out int assemblerId, out alloy)) return false;

            RecipeExecuteData data = factory.factorySystem.assemblerPool[assemblerId].recipeExecuteData;

            if (data?.requireCounts == null) return false;

            parts = new int[alloy.Adjustable];

            for (var i = 0; i < parts.Length; i++)
            {
                int idx = alloy.SlotIndex[i + 1];

                parts[i] = idx >= 0 && idx < data.requireCounts.Length
                    ? data.requireCounts[idx]
                    : alloy.Entry.slots[i + 1].nominal;
            }

            return true;
        }

        // ── 钩子 ──────────────────────────────────────────────
        // 全都在"设配方"的入口上，没有一个在 tick 路径里。
        // AssemblerComponent.SetRecipe 本身不能挂：它拿不到 planetId
        // （签名是 SetRecipe(int recipeId, SignData[] signPool)），
        // 而它的每一个调用方都带着 PlanetFactory。

        /// <summary>
        /// 把存档里记着的配比全部重贴回去。
        ///
        /// <b>必须由两个地方各调一次，因为它们的先后顺序是反的。</b>
        /// <c>GameData.Import</c> 的后置跑在前面，那时 DSPModSave 还没把我们的存档块读回来，
        /// store 是空的；等 DSPModSave 的 <c>Post Load</c> 触发
        /// <c>IModCanSave.Import</c> 时，配方数据早就恢复完了、却没人再去贴。
        /// 只挂前者的结果是<b>存档里的配比在读档时被静默丢掉</b>——
        /// 日志上的表现是「迁移了 N 台」出现了，而「重贴成功」一行根本不打印
        /// （因为那时 <c>Count</c> 还是 0，连日志都被跳过）。这正是发现它的方式。
        /// </summary>
        internal static void ReapplyAll(string reason)
        {
            GameData data = GameMain.data;

            if (ByRecipe.Count == 0 || data?.factories == null || AlloyRatioStore.Count == 0) return;

            var done = 0;

            for (var i = 0; i < data.factoryCount; i++)
            {
                PlanetFactory factory = data.factories[i];

                if (factory == null) continue;

                foreach (var pair in AlloyRatioStore.All)
                {
                    if (pair.Key.PlanetId != factory.planetId) continue;

                    if (Apply(factory, pair.Key.EntityId, pair.Value) == ApplyResult.Ok) done++;
                }
            }

            if (done == AlloyRatioStore.Count)
                ProjectEdenPlugin.Log.LogInfo(
                    $"合金配比（{reason}）：存档里记了 {AlloyRatioStore.Count} 台，全部重贴成功");
            else
                ProjectEdenPlugin.Log.LogWarning(
                    $"合金配比（{reason}）：存档里记了 {AlloyRatioStore.Count} 台，只重贴上 {done} 台 —— " +
                    "对不上的那些多半是建筑已被拆除，或者它现在跑的不是当初那条配方");
        }

        /// <summary>读档：Import 会把 recipeExecuteData 写回成全局那一份。这一次通常什么都贴不上（store 还没读回来），真正生效的是 Plugin.Import 里那次。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
        private static void GameData_Import()
        {
            ReapplyAll("GameData.Import");

            // 合金弹药存的是同一张表（每台建筑一个 int[]），所以两边都得重贴一次。
            // 这里和 Plugin.Import 各调一次，理由见上面 ReapplyAll 的注释——两个入口顺序是反的
            AmmoPairPatches.ReapplyAll();
            CompositePatches.ReapplyAll();
            CompositeOutputPatches.ReapplyAll();
            ProliferatorPatches.ReapplyAll();
            RedoxBurnerPatches.ReapplyAll();
            QualityRefinerySelectPatches.ReapplyAll();
        }

        /// <summary>玩家在装配器窗口里选了配方：贴这台建筑记着的配比，没记过就贴当前默认。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIAssemblerWindow), nameof(UIAssemblerWindow.OnRecipePickerReturn))]
        private static void UIAssemblerWindow_OnRecipePickerReturn(UIAssemblerWindow __instance)
        {
            if (__instance?.factory == null) return;

            int assemblerId = __instance._assemblerId;

            if (assemblerId <= 0) return;

            ApplyStoredOrDefault(__instance.factory,
                __instance.factory.factorySystem.assemblerPool[assemblerId].entityId);
        }

        /// <summary>复制粘贴建筑参数：蓝图带不了配比，粘出来的按「玩家当前默认」走。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildingParameters), nameof(BuildingParameters.PasteToFactoryObject))]
        private static void BuildingParameters_PasteToFactoryObject(int objectId, PlanetFactory factory)
        {
            if (objectId <= 0) return;

            ApplyStoredOrDefault(factory, objectId);
        }

        /// <summary>蓝图预建转正式建筑：同上。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildingParameters), nameof(BuildingParameters.ApplyPrebuildParametersToEntity))]
        private static void BuildingParameters_ApplyPrebuildParametersToEntity(int entityId, PlanetFactory factory)
        {
            ApplyStoredOrDefault(factory, entityId);
        }
    }

    /// <summary>贴配比的结果。界面要按它给出不同的提示。</summary>
    internal enum ApplyResult
    {
        Ok,

        /// <summary>这台建筑没在跑任何一条可配比的合金配方</summary>
        NotAlloy,
    }

    /// <summary>alloys.json 的结构。</summary>
    [Serializable]
    internal class AlloysConfig
    {
        public bool enabled;

        public AlloyEntry[] alloys;
    }

    /// <summary>
    /// 一种可调配比的合金。
    ///
    /// <b>槽位是「一味余量 + N 味可调」</b>，正是真实合金牌号的写法
    /// （"18% Cr, 8% Mn, 余量 Fe"）。<c>slots[0]</c> 是余量，份数 = 总份数 − 其余之和；
    /// 后面每一味各自可调。这样<b>拖任何一根滑动条都不需要联动规则</b>——
    /// 余量那味自动吸收，三元和二元用同一套交互。
    /// </summary>
    [Serializable]
    internal class AlloyEntry
    {
        /// <summary>日志和配置可读性用，不参与匹配</summary>
        public string key;

        public int recipeId;

        /// <summary>一个周期的总份数。滑动条的真实粒度就是 1/totalParts</summary>
        public int totalParts;

        /// <summary><b>第 0 个是余量</b>，其余每个一根滑动条。配方里其余原料份数不动</summary>
        public AlloySlot[] slots;

        /// <summary>
        /// 属性怎么算：<c>composite</c> 走混合律（属性必落在组元之间，每个槽要 props），
        /// <c>solution</c> 以标称成分为锚点按斜率偏移（属性可以超过任何组元，每个可调槽要 slopes）。
        /// 留空按 composite 处理。
        /// </summary>
        public string model;

        /// <summary>solution：标称成分下的四维值。<b>就是合金表里手标的那一行</b></summary>
        public AlloyProps nominal;

        /// <summary>配比怎么换算成产量和耗时</summary>
        public AlloyQualityEntry quality;

        /// <summary>基准耗时（tick）。留 0 用配方自己声明的</summary>
        public int baseTimeSpend;

        /// <summary>基准产量。留 0 用配方自己声明的 ResultCounts[0]</summary>
        public int baseYield;
    }

    /// <summary>
    /// 配比 → 产量 / 耗时 的换算。
    ///
    /// <b>为什么要按各合金自己的可达区间归一。</b> 各合金四维值的量纲差得很远——
    /// 同样把滑动条从一端拖到另一端，锰钢的加权质量能动 23%，钒钛合金只动 1%。
    /// 直接拿原始质量换算的话，后者的滑动条等于没有。注册时穷举出各自的上下界，
    /// 归一之后每根滑动条的行程才落在同一个量级上，日志里会把标定结果打出来。
    /// </summary>
    [Serializable]
    internal class AlloyQualityEntry
    {
        /// <summary>
        /// 四维各自的权重，<b>只写这种合金在意的那几轴</b>（锰钢看韧性、不锈钢看耐蚀）。
        /// 没写的轴照常显示，只是不换算成产量。权重之和不必是 1，几何平均对总量不敏感。
        /// </summary>
        public AlloyProps weights;

        /// <summary>最差配比的产量倍率。留 0 用 0.8</summary>
        public float yieldMin;

        /// <summary>最优配比的产量倍率。留 0 用 1.25</summary>
        public float yieldMax;

        /// <summary>最优配比的耗时倍率（越小越快）。留 0 用 0.85</summary>
        public float timeMin;

        /// <summary>最差配比的耗时倍率。留 0 用 1.2</summary>
        public float timeMax;
    }

    /// <summary>一个原料槽。第 0 个是余量，不用配 min/max/nominal。</summary>
    [Serializable]
    internal class AlloySlot
    {
        /// <summary>原版物品 ID。和 ref 二选一</summary>
        public int id;

        /// <summary>本 mod 的物品：ores.json 里 items 的 key，或 &lt;矿种&gt;.ore / .ingot</summary>
        public string @ref;

        /// <summary>可调槽：滑动条的行程</summary>
        public int min;
        public int max;

        /// <summary>可调槽：标称成分下的份数，也就是锚点</summary>
        public int nominal;

        /// <summary>solution：这一味离开锚点每 1 份，各轴变化多少</summary>
        public AlloyProps slopes;

        /// <summary>composite：这一味自己的四维值</summary>
        public AlloyProps props;
    }

    /// <summary>四维值。</summary>
    [Serializable]
    internal class AlloyProps
    {
        public float hardness;
        public float toughness;
        public float corrosion;
        public float conductivity;

        internal float Get(string axis)
        {
            switch (axis)
            {
                case "toughness": return toughness;
                case "corrosion": return corrosion;
                case "conductivity": return conductivity;
                default: return hardness;
            }
        }
    }

    /// <summary>
    /// 四维的轴名表。牌号门槛那套已经去掉了，这个类现在只剩 <c>Axes</c> 还有人用——
    /// 加权质量要按轴名遍历权重。可空字段留着，给以后可能回来的「用途谓词」那套用。
    /// </summary>
    [Serializable]
    internal class AlloyThresholds
    {
        public float? hardness;
        public float? toughness;
        public float? corrosion;
        public float? conductivity;

        internal float? Get(string axis)
        {
            switch (axis)
            {
                case "toughness": return toughness;
                case "corrosion": return corrosion;
                case "conductivity": return conductivity;
                default: return hardness;
            }
        }

        internal static readonly string[] Axes =
            { "hardness", "toughness", "corrosion", "conductivity" };
    }
}
