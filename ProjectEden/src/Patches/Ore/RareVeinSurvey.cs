using System;
using System.Text;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 稀有矿脉在<b>这一局星区里</b>到底能刷出几颗——星区生成之后数一遍，把数字打进日志。
    ///
    /// <b>为什么要有这个：「极稀有」和「统计上不存在」在配置里长得一模一样。</b>
    /// <c>chance</c> 是「每颗符合主题的行星的出现概率」，但符合主题的行星有几颗，
    /// 取决于主题表和这一局的星球分布——那两样都在 <c>resources.assets</c> 和随机种子里，
    /// 离线一个都读不到。于是概率写下去的时候，没人知道它对应的是
    /// 「一个星区里有三颗」还是「平均半颗」。<b>每一步都成功、合起来没效果</b>，
    /// 而且日志里一条警告都没有——这是这个仓库最怕的那种失败。
    ///
    /// <b>读的是最终状态，不是我们自己记的账。</b> 哪些主题挂了这条矿脉、各自什么概率，
    /// 是回头扫 <c>ThemeProto.RareVeins</c> / <c>RareSettings</c> 得到的，
    /// 不是 <c>ExtendRareSlots</c> 写入时顺手记下来的——那样的话写错了也数得对，
    /// 正好把 bug 藏住。这一条不是空话：那个写入处真的把两个概率槽写反了，
    /// 而按自己的账去数是发现不了的。
    ///
    /// <b>挂在 <c>UniverseGen.CreateGalaxy</c> 上，不是 <c>GameMain.Begin</c>。</b>
    /// 头一版挂的是后者，实测它在 <b>LDBTool 建表之前</b>就跑过一次
    /// （日志里本报告出现在「LDBTool Pre Loading...」前面），那时候
    /// <see cref="OreRegistry.Ores"/> 还是空的；而一次性闩又让它再也没跑第二次。
    /// 于是它报了「没有 mode 为 rare 的矿脉」——**一句既像结论又像故障的话**。
    /// 现在按星区种子记闩，没准备好就不闩、下次再来。
    ///
    /// 期望值是<b>近似</b>：原版还会按行星自身的稀有度指数做
    /// <c>1 - pow(1 - chance, 指数)</c> 修正，指数随星球浮动。量级是准的。
    /// </summary>
    [HarmonyPatch]
    internal static class RareVeinSurvey
    {
        /// <summary>已经报过的星区种子。0 表示还没报过。</summary>
        private static int _reportedSeed;

        /// <summary>想让一种矿脉「找得到」，一个星区里至少该有这么多颗。</summary>
        private const float WantPlanets = 2f;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UniverseGen), nameof(UniverseGen.CreateGalaxy))]
        private static void UniverseGen_CreateGalaxy(GalaxyData __result)
        {
            if (__result?.stars == null) return;
            if (__result.seed == _reportedSeed) return;

            // <b>没准备好就别闩。</b> 闩了就再也不会重来，
            // 而「还没准备好」和「查完了没东西」得报成两句话
            if (LDB.themes?.dataArray == null || OreRegistry.Ores.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"稀有矿脉普查：矿脉表还没建好（已注册 {OreRegistry.Ores.Count} 种，"
                    + $"主题表 {(LDB.themes?.dataArray == null ? "未就绪" : "就绪")}），这次跳过，下次再数");

                return;
            }

            _reportedSeed = __result.seed;

            var sb = new StringBuilder($"── 稀有矿脉在本星区的分布预估（种子 {__result.seed}）──");
            var rare = 0;

            foreach (OreRegistry.Ore ore in OreRegistry.Ores)
            {
                PlacementEntry place = ore.Entry?.placement;

                if (place == null || place.mode != "rare") continue;

                rare++;
                Report(sb, __result, ore);
            }

            if (rare == 0)
                sb.Append($"\n  已注册 {OreRegistry.Ores.Count} 种矿脉，但没有一种 placement.mode 是 rare");

            sb.Append("\n  （期望值是近似：原版还会按行星稀有度指数做幂次修正，量级准、小数不准）");

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());

            // 期望值只说「大概几颗」，探矿器接着说「具体哪几颗」
            RareVeinProspector.Start(__result);
        }

        private static void Report(StringBuilder sb, GalaxyData galaxy, OreRegistry.Ore ore)
        {
            var themeCount = 0;
            float chanceBirth = 0f;
            float chanceOutside = 0f;

            foreach (ThemeProto theme in LDB.themes.dataArray)
            {
                if (theme?.RareVeins == null) continue;

                int slot = Array.IndexOf(theme.RareVeins, ore.VeinId);

                if (slot < 0) continue;

                themeCount++;

                float[] s = theme.RareSettings;

                if (s == null || slot * 4 + 1 >= s.Length) continue;

                // GenerateVeins IL 03F6：index == 0 取 [+0]，否则取 [+1]
                chanceBirth = s[slot * 4 + 0];
                chanceOutside = s[slot * 4 + 1];
            }

            if (themeCount == 0)
            {
                sb.Append($"\n  {ore.Entry.veinName}：**一个主题都没挂上** —— 它在这一局里根本不会出现");

                return;
            }

            var outside = 0;
            var birth = 0;

            foreach (StarData star in galaxy.stars)
            {
                if (star?.planets == null) continue;

                foreach (PlanetData planet in star.planets)
                {
                    if (planet == null || !ThemeCarries(planet.theme, ore.VeinId)) continue;

                    if (star.index == 0) birth++;
                    else outside++;
                }
            }

            float expect = outside * chanceOutside + birth * chanceBirth;

            sb.Append($"\n  {ore.Entry.veinName}：符合主题的行星 母星系外 {outside} 颗 / 母星系内 {birth} 颗")
              .Append($"，概率 母星系外 {chanceOutside:0.###} / 母星系内 {chanceBirth:0.###}")
              .Append($" → **期望 {expect:0.0} 颗**");

            if (expect >= WantPlanets) return;

            float need = outside > 0 ? WantPlanets / outside : 0f;

            sb.Append($"\n    ⚠ 少于 {WantPlanets:0} 颗，玩家很可能整局都找不到它。")
              .Append(need > 0f && need < 1f
                  ? $"想稳定有 {WantPlanets:0} 颗，ores.json 里这条的 placement.chance 要填到 {need:0.###} 左右"
                  : "候选行星太少，光调 chance 不够，得放宽 themes");
        }

        /// <summary>这个主题挂着这条矿脉吗。</summary>
        private static bool ThemeCarries(int themeId, int veinId)
        {
            ThemeProto theme = themeId > 0 ? LDB.themes.Select(themeId) : null;

            return theme?.RareVeins != null && Array.IndexOf(theme.RareVeins, veinId) >= 0;
        }
    }
}
