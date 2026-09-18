using System.Collections.Generic;
using ProjectEden.Utils;

namespace ProjectEden
{
    /// <summary>
    /// 启动自检：配方图里有没有「凭空多出可燃能量」的地方。
    ///
    /// <b>为什么需要它。</b> 本 mod 的热值全部锚在煤上（393.5 kJ/mol ↔ 2.7 MJ），
    /// 所以一条配方的「产出可燃热值 − 投入可燃热值」本该约等于它真实的反应焓——几个 MJ 量级。
    /// 而一次实测扫出 <b>14 条为正、合计 +528.6 MJ</b>，最严重的一条 4 秒配方凭空多出 114.8 MJ。
    ///
    /// <b>那不是「数值偏大」，那是永动机。</b> 巨型建筑把一次结算的耗电摊薄到万分之一，
    /// 所以任何「产出燃料比投入燃料值钱」的配方都是一台发电机：1 倍速机器约 30 倍回报，
    /// 万倍速约 800 倍。而这三个错误——记账单位在配置里自相矛盾、馏分热值按错误的直觉排、
    /// 原版氢比锚点高 4.1 倍——<b>每一个单独看都不像 bug</b>，只有把整张图一起算才露出来。
    ///
    /// 所以它跟 <see cref="I18N.VerifyCoverage"/> 和 <c>ProtoArrayCheck</c> 一样挂在
    /// <c>PostAddDataAction</c> 的最后：<b>加一条新配方就会被它盯着</b>，
    /// 而不是等下一次有人想起来手算。
    ///
    /// <b>豁免必须声明，不能内置清单。</b> 三类正当情况（真实吸热 / 阳光 / 刻意不给热值的中间体）
    /// 由配方自己的 <c>energyNote</c> 写明理由；写不出理由的就是洞。
    /// 内置一张硬编码的白名单则会随着配方变动慢慢腐烂，而且没人看得见它为什么在那儿。
    /// </summary>
    internal static class EnergyAudit
    {
        /// <summary>低于这个差额不报。真实反应焓大多在 1 MJ 以内，再低就全是舍入噪声。</summary>
        private const double ThresholdJ = 1e6;

        internal static void Run()
        {
            OreConfig cfg = OreRegistry.Config;

            if (cfg == null)
            {
                ProjectEdenPlugin.Log.LogError("能量审计：ores.json 没读出来，本次不检查");

                return;
            }

            var leaks = new List<string>();
            var exempt = 0;
            var checkedCount = 0;

            foreach (OreRecipeEntry e in AllRecipes(cfg))
            {
                if (e == null || !e.enabled) continue;

                checkedCount++;

                double delta = Side(e.results) - Side(e.items);

                if (delta <= ThresholdJ) continue;

                if (!string.IsNullOrEmpty(e.energyNote))
                {
                    exempt++;

                    continue;
                }

                leaks.Add($"「{e.name}」+{delta / 1e6:0.#} MJ");
            }

            // 没问题也要打一行——沉默的诊断分不出「没事」和「没跑」，
            // 这条规矩这个仓库已经付过三次学费
            if (leaks.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"能量审计通过：{checkedCount} 条配方，没有凭空多出可燃能量的（{exempt} 条已声明豁免）");

                return;
            }

            ProjectEdenPlugin.Log.LogWarning(
                $"能量审计：{leaks.Count} 条配方的产出可燃热值高于投入——{string.Join("、", leaks.ToArray())}。" +
                "巨型建筑把耗电摊薄到万分之一，所以这样的配方实际上是发电机。" +
                "若属于真实吸热 / 阳光 / 刻意不给热值的中间体，就在那条配方上写 energyNote 说明理由；" +
                "写不出理由的话它就是个洞");
        }

        /// <summary>报几条：输出必须有界，否则一屏警告等于没有警告。</summary>
        private const int TopN = 5;

        /// <summary>
        /// 第二遍：**原版配方里，哪些能被巨型建筑跑**。
        ///
        /// <b>为什么必须有这一遍。</b> 上面那一遍只走 <c>ores.json</c> 自己的配方表，
        /// 所以每一条原版配方都在它的视野之外——而巨型建筑会非常乐意用 10000 倍速跑一条原版配方。
        /// 「能量审计通过」那行的真实含义一直是<b>「没有 mod 配方凭空造能量」，
        /// 不是「没有东西凭空造能量」</b>。这件事 CLAUDE.md 记着，但一直没人补上检查。
        ///
        /// 补它的直接原因是综合化学厂加收了精炼（3）：那把三条原版精炼配方连同
        /// <c>石脑油 · 常减压蒸馏</c> 一起送进了万倍速。**改动点要自带检查**，
        /// 否则下一个人也只能靠手算和注释里的数——而注释里的数是主张，不是测量。
        ///
        /// 判据是「哪些类型有巨型建筑能跑」：每座巨型建筑自己的 <c>recipeType</c>，
        /// 加上它 <c>acceptsRecipeTypes</c> 里的那些。**不是只看综合化学厂**——
        /// 天工装配厂（4）、冶铸熔炉（1）、燔石化工厂（2）早就在跑原版配方了，
        /// 只报新加的那一类会给出一个漂亮而片面的答案。
        ///
        /// 它<b>只报告，不拦截</b>：原版的平衡不是本 mod 的责任，而且这里真正的杠杆
        /// （「巨型建筑允许跑哪些配方」）是所有者的设计决定，不是自检该替他做的。
        /// </summary>
        internal static void AuditMegaVanilla()
        {
            MegaBuildingsConfig mega = MegaBuildingRegistry.Config;

            if (mega?.buildings == null || LDB.recipes?.dataArray == null)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "能量审计·原版侧：巨型建筑表或 LDB.recipes 读不到，本次不检查。");

                return;
            }

            var runnable = new HashSet<int>();

            foreach (MegaBuildingEntry b in mega.buildings)
            {
                if (b == null) continue;

                if (b.recipeType > 0) runnable.Add(b.recipeType);

                if (b.acceptsRecipeTypes == null) continue;

                foreach (int t in b.acceptsRecipeTypes)
                    if (t > 0) runnable.Add(t);
            }

            if (runnable.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo("能量审计·原版侧：没有任何巨型建筑声明了配方类型，跳过。");

                return;
            }

            var own = new HashSet<int>(ProtoSlots.OwnRecipeIds);

            var hits = new List<KeyValuePair<double, string>>();
            var scanned = 0;

            foreach (RecipeProto r in LDB.recipes.dataArray)
            {
                if (r == null) continue;

                // 本 mod 自己的配方由上面那一遍负责，这里只看原版的，免得同一条报两次。
                if (own.Contains(r.ID)) continue;

                if (!runnable.Contains((int)r.Type)) continue;

                scanned++;

                double delta = Burnable(r.Results, r.ResultCounts) - Burnable(r.Items, r.ItemCounts);

                if (delta <= ThresholdJ) continue;

                hits.Add(new KeyValuePair<double, string>(
                    delta, $"「{r.name}」+{delta / 1e6:0.#} MJ（类型 {(int)r.Type}）"));
            }

            string where = string.Join("、", System.Array.ConvertAll(
                new List<int>(runnable).ToArray(), x => x.ToString()));

            if (hits.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"能量审计·原版侧通过：巨型建筑能跑的配方类型是 {where}，"
                    + $"其中 {scanned} 条原版配方没有一条产出可燃热值高于投入。");

                return;
            }

            hits.Sort((a, b) => b.Key.CompareTo(a.Key));

            var top = new List<string>();

            for (var i = 0; i < hits.Count && i < TopN; i++) top.Add(hits[i].Value);

            ProjectEdenPlugin.Log.LogWarning(
                $"能量审计·原版侧：巨型建筑能跑的 {scanned} 条**原版**配方里，有 {hits.Count} 条"
                + $"产出可燃热值高于投入，最大的 {top.Count} 条是 {string.Join("、", top.ToArray())}"
                + $"（巨型建筑覆盖的配方类型：{where}）。"
                + "巨型建筑把耗电摊薄到万分之一，所以这些配方在万倍速下实际上是发电机。"
                + "**这不一定是本次改动造成的**——原版配方一直在巨型建筑里跑，只是过去没人检查；"
                + "真正的杠杆是巨型建筑允许跑哪些类型（megabuildings.json 的 recipeType / acceptsRecipeTypes），"
                + "以及那些产物给不给热值。这一条只报告，不拦截。");
        }

        /// <summary>
        /// 原版配方一侧的可燃热值合计。
        ///
        /// 和 <see cref="Side"/> 同样读 <b>LDB 里的最终值</b>，所以 <c>vanillaHeat</c> 的改写
        /// 会被算进来——氢从 9.0 改成 1.96 之后，凡是产氢的原版配方账面都会变，
        /// 而那正是这一遍要看的东西。
        /// </summary>
        private static double Burnable(int[] ids, int[] counts)
        {
            if (ids == null || counts == null) return 0.0;

            var total = 0.0;
            int n = ids.Length < counts.Length ? ids.Length : counts.Length;

            for (var i = 0; i < n; i++)
            {
                ItemProto proto = LDB.items.Select(ids[i]);

                if (proto != null) total += (double)proto.HeatValue * counts[i];
            }

            return total;
        }

        private static IEnumerable<OreRecipeEntry> AllRecipes(OreConfig cfg)
        {
            if (cfg.recipes != null)
                foreach (OreRecipeEntry e in cfg.recipes)
                    yield return e;

            if (cfg.ores == null) yield break;

            foreach (OreEntry o in cfg.ores)
            {
                if (o?.recipes == null) continue;

                foreach (OreRecipeEntry e in o.recipes)
                    yield return e;
            }
        }

        /// <summary>
        /// 一侧的可燃热值合计。
        ///
        /// 读的是 <b>LDB 里的最终值</b>，不是配置里写的——原版物品的热值可能被
        /// <c>vanillaHeat</c> 改过，而那一步就在这之前跑。从最终状态去核，
        /// 而不是从「我以为我写了什么」去核。
        /// </summary>
        private static double Side(RecipeItemEntry[] side)
        {
            if (side == null) return 0.0;

            var total = 0.0;

            foreach (RecipeItemEntry e in side)
            {
                if (e == null) continue;

                int id = e.id > 0 ? e.id : OreRegistry.FindItemIdByRef(e.@ref);

                if (id <= 0) continue;

                ItemProto proto = LDB.items.Select(id);

                if (proto != null) total += (double)proto.HeatValue * e.count;
            }

            return total;
        }
    }
}
