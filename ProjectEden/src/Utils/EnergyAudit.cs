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
