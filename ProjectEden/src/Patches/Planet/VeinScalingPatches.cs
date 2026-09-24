using System;
using System.Collections.Generic;
using ProjectEden.Compatibility;

namespace ProjectEden.Patches
{
    /// <summary>
    /// **把矿脉分布跟着行星面积一起放大**（<c>planet.json</c> 的 <c>veinScaling</c> 段）。
    ///
    /// <para>推导、时序和三个旋钮的理由写在 <see cref="VeinScalingConfig"/> 上，这里不重复。
    /// 一句话：<c>GenerateVeins</c> 读了六次 <c>radius</c> 但<b>一次都没进矿脉数量</b>，
    /// 所以半径翻倍等于密度掉到四分之一——这一段是把它补回来，顺带按种–面积关系补种类。</para>
    ///
    /// <para><b>没有 transpiler，没有 prefab 改动。</b> 它只在 <c>PostAddDataAction</c> 上
    /// 改写 <c>ThemeProto</c> 的五个数组，和 <c>OreRegistry.ExtendThemes</c> 走的是同一条路——
    /// 那条路本来就是这个仓库注册自定义矿脉的方式，已经验证过很多版了。</para>
    ///
    /// <para><b>时序：必须排在 <c>OreRegistry.OnPostAddData</c> 之后</b>，
    /// 否则本 mod 自己那十四种矿还没进主题表，缩放只会作用在原版那几种上——
    /// 而且<b>不会报任何错</b>，只是效果少一半。</para>
    /// </summary>
    internal static class VeinScalingPatches
    {
        /// <summary>原版半径，面积倍率的分母。</summary>
        private const float StockRadius = 200f;

        private static VeinScalingConfig Config => PlanetRadiusPatches.Config?.veinScaling;

        /// <summary>
        /// 面积倍率 = (当前半径 / 200)²。
        ///
        /// <para><b>取的是解析之后的真实半径，不是配置里写的倍率</b>——
        /// <c>ResolveRadius</c> 会把倍率吸附到 40 的倍数再夹进 [200, 480]，
        /// 所以「配置写 1.5、实际跑 1.6」是可能的，而按配置算就会和世界对不上。
        /// 这条规矩本仓库在半径那一段已经付过账了。</para>
        /// </summary>
        private static float AreaRatio
        {
            get
            {
                int r = PlanetRadiusPatches.Radius;

                if (r <= 0) return 1f;

                float k = r / StockRadius;

                return k * k;
            }
        }

        internal static void Apply()
        {
            VeinScalingConfig cfg = Config;

            // **无论走哪一支都打一行。** 「没配置」「关着」「面积是 1」「被 GS2 挡了」
            // 四种情况在日志里长得一模一样，而它们要的处置完全不同——
            // 本仓库为这条规矩付过七次学费
            if (cfg == null)
            {
                ProjectEdenPlugin.Log.LogInfo("矿脉缩放：planet.json 里没有 veinScaling 段，未启用");

                return;
            }

            if (!cfg.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo("矿脉缩放：开关关着，未启用（planet.json 的 veinScaling.enabled）");

                return;
            }

            if (GalacticScaleCompat.Installed)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "矿脉缩放：装了银河尺度（GalacticScale），**这一段整体不生效**——" +
                    "它把矿脉生成整个换掉了，ThemeProto 根本不会被读到。" +
                    "矿脉分布请在 GS2 那边调。");

                return;
            }

            float area = AreaRatio;

            if (area <= 1.0001f)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"矿脉缩放：当前半径 {PlanetRadiusPatches.Radius}，面积倍率 {area:0.###}——" +
                    "和原版一样大，本段是空操作（它的全部意义就是补回放大带来的密度损失）");

                return;
            }

            ThemeProto[] themes = LDB.themes?.dataArray;

            if (themes == null || themes.Length == 0)
            {
                ProjectEdenPlugin.Log.LogError("矿脉缩放：读不到 LDB.themes，未启用");

                return;
            }

            float z = cfg.speciesAreaExponent > 0f ? cfg.speciesAreaExponent : 0.25f;
            float typeRatio = (float)Math.Pow(area, z);
            float extraDensity = cfg.extraTypeDensity > 0f ? cfg.extraTypeDensity : 0.35f;

            ProjectEdenPlugin.Log.LogInfo(
                $"矿脉缩放：半径 {PlanetRadiusPatches.Radius}（原版 200），**面积 ×{area:0.##}**。" +
                $"数量 ×{area:0.##}{(cfg.scaleSpots ? "" : "（关）")}；" +
                $"种类按种–面积关系 S∝A^{z:0.##} → ×{typeRatio:0.###}{(cfg.extraTypes ? "" : "（关）")}；" +
                $"稀有矿概率 p→1−(1−p)^{area:0.##}{(cfg.scaleRareChance ? "" : "（关）")}");

            // 先统计每个矿种在多少个主题上出现过——补种类时要按「在别处有多普遍」排序，
            // 所以这一趟必须在任何改写之前跑完，否则会把自己刚补进去的算进普遍度
            Dictionary<int, int> spread = CountSpread(themes);

            var totals = new int[5];

            foreach (ThemeProto theme in themes)
            {
                if (theme?.VeinSpot == null || theme.VeinSpot.Length == 0) continue;

                int beforeTypes = CountTypes(theme);
                int beforeSpots = SumSpots(theme);

                if (cfg.scaleSpots) totals[0] += ScaleSpots(theme, area);
                if (cfg.extraTypes) totals[1] += AddTypes(theme, spread, typeRatio, extraDensity);
                if (cfg.scaleRareRichness) totals[2] += ScaleRareRichness(theme, area);
                if (cfg.scaleRareChance) totals[3] += ScaleRareChance(theme, area);

                totals[4]++;

                if (!cfg.report) continue;

                ProjectEdenPlugin.Log.LogInfo(
                    $"　主题「{theme.DisplayName}」：矿种 {beforeTypes} → {CountTypes(theme)}，" +
                    $"矿脉处数 {beforeSpots} → {SumSpots(theme)}");
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"矿脉缩放完成：扫过 {totals[4]} 张主题表，" +
                $"放大处数 {totals[0]} 项、补进矿种 {totals[1]} 项、" +
                $"稀有储量 {totals[2]} 项、稀有概率 {totals[3]} 项。" +
                "**只对还没生成的星球生效**——已经去过的星球保留烘焙好的矿脉数据。");
        }

        // ── 统计 ────────────────────────────────────────────────

        /// <summary>每个矿种（下标 = 矿种 − 1）在多少张主题表上有普通矿脉位。</summary>
        private static Dictionary<int, int> CountSpread(ThemeProto[] themes)
        {
            var spread = new Dictionary<int, int>();

            foreach (ThemeProto theme in themes)
            {
                if (theme?.VeinSpot == null) continue;

                for (var i = 0; i < theme.VeinSpot.Length; i++)
                {
                    if (theme.VeinSpot[i] <= 0) continue;

                    spread.TryGetValue(i, out int n);
                    spread[i] = n + 1;
                }
            }

            return spread;
        }

        private static int CountTypes(ThemeProto theme)
        {
            var n = 0;

            for (var i = 0; i < theme.VeinSpot.Length; i++)
                if (theme.VeinSpot[i] > 0) n++;

            return n;
        }

        private static int SumSpots(ThemeProto theme)
        {
            var n = 0;

            for (var i = 0; i < theme.VeinSpot.Length; i++)
                if (theme.VeinSpot[i] > 0) n += theme.VeinSpot[i];

            return n;
        }

        // ── 四个改写 ────────────────────────────────────────────

        /// <summary>
        /// 数量 × 面积倍率。**向上取整**：原版有大量 <c>VeinSpot == 1</c> 的条目，
        /// 向下取整会把 ×1.44（1.2 倍半径）这种整档吃掉，表现为「改了配置什么都没变」。
        /// </summary>
        private static int ScaleSpots(ThemeProto theme, float area)
        {
            var touched = 0;

            for (var i = 0; i < theme.VeinSpot.Length; i++)
            {
                if (theme.VeinSpot[i] <= 0) continue;

                var scaled = (int)Math.Ceiling(theme.VeinSpot[i] * area);

                if (scaled == theme.VeinSpot[i]) continue;

                theme.VeinSpot[i] = scaled;
                touched++;
            }

            return touched;
        }

        /// <summary>
        /// 种类：按种–面积关系补上该主题原本没有的矿种。
        ///
        /// <para><b>候选只从「在别的主题上出现过」里取</b>——绝不凭空造出一个
        /// 原版和本 mod 都没打算放在任何地方的矿种。排序按<b>在别处有多普遍</b>降序：
        /// 一种到处都有的矿，最可能在这里也以伴生形式出现；一种只在熔岩上有的矿，
        /// 不该因为这条规则跑到冰原上去。</para>
        ///
        /// <para>密度用该主题<b>铁矿</b>的处数乘 <c>extraTypeDensity</c>，
        /// 和 <c>OreRegistry.ExtendThemes</c> 取同一个锚——不是另起一套。</para>
        /// </summary>
        private static int AddTypes(ThemeProto theme, Dictionary<int, int> spread,
                                    float typeRatio, float density)
        {
            int have = CountTypes(theme);

            if (have <= 0) return 0;

            var want = (int)Math.Round(have * typeRatio) - have;

            if (want <= 0) return 0;

            // 铁矿（矿种 1 → 下标 0）是锚。没有铁的主题（气巨等）整个跳过
            if (theme.VeinSpot.Length < 1 || theme.VeinSpot[0] <= 0) return 0;

            int ironSpot = theme.VeinSpot[0];
            float ironCount = theme.VeinCount != null && theme.VeinCount.Length > 0 ? theme.VeinCount[0] : 1f;
            float ironOpacity = theme.VeinOpacity != null && theme.VeinOpacity.Length > 0 ? theme.VeinOpacity[0] : 1f;

            var spot = (int)Math.Round(ironSpot * density);

            if (spot < 1) spot = 1;

            // 候选：本主题没有、但别处有；按别处的普遍度降序，同名次按矿种号升序（稳定）
            var candidates = new List<int>();

            foreach (KeyValuePair<int, int> kv in spread)
            {
                if (kv.Key < theme.VeinSpot.Length && theme.VeinSpot[kv.Key] > 0) continue;

                candidates.Add(kv.Key);
            }

            candidates.Sort((a, b) =>
            {
                int bySpread = spread[b].CompareTo(spread[a]);

                return bySpread != 0 ? bySpread : a.CompareTo(b);
            });

            var added = 0;

            foreach (int index in candidates)
            {
                if (added >= want) break;

                int needed = index + 1;

                Grow(ref theme.VeinSpot, needed);
                Grow(ref theme.VeinCount, needed);
                Grow(ref theme.VeinOpacity, needed);

                theme.VeinSpot[index] = spot;
                theme.VeinCount[index] = ironCount * density;
                theme.VeinOpacity[index] = ironOpacity;

                added++;
            }

            return added;
        }

        /// <summary>稀有矿储量：<c>RareSettings[i*4+3]</c> × 面积倍率。</summary>
        private static int ScaleRareRichness(ThemeProto theme, float area)
        {
            if (theme.RareVeins == null || theme.RareSettings == null) return 0;

            var touched = 0;

            for (var i = 0; i < theme.RareVeins.Length; i++)
            {
                int slot = i * 4 + 3;

                if (slot >= theme.RareSettings.Length) break;
                if (theme.RareSettings[slot] <= 0f) continue;

                theme.RareSettings[slot] *= area;
                touched++;
            }

            return touched;
        }

        /// <summary>
        /// 稀有矿概率：<c>p = 1 − (1−p₀)^k</c>，k 是面积倍率。
        ///
        /// <para>推导见 <see cref="VeinScalingConfig"/>。两个自带的好性质省掉了全部特判：
        /// <b>结果恒 &lt; 1</b>（不需要夹），而且 <b>p₀ = 0 时结果仍是 0</b>——
        /// 那正是「母星系不刷」那一格（<c>[i*4+0]</c>），本仓库曾经因为把这两格搞反
        /// 而让四种稀有矿只长在母星系，所以这里绝不能特判它。</para>
        ///
        /// <para>三格概率一起：<c>[+0]</c> 母星系、<c>[+1]</c> 母星系之外、
        /// <c>[+2]</c> 追加矿点。<c>[+3]</c> 是储量，归上面那个方法。</para>
        /// </summary>
        private static int ScaleRareChance(ThemeProto theme, float area)
        {
            if (theme.RareVeins == null || theme.RareSettings == null) return 0;

            var touched = 0;

            for (var i = 0; i < theme.RareVeins.Length; i++)
            {
                for (var k = 0; k < 3; k++)
                {
                    int slot = i * 4 + k;

                    if (slot >= theme.RareSettings.Length) break;

                    float p = theme.RareSettings[slot];

                    // p <= 0 时公式本来就给 0，但提前跳掉省一次 Pow；
                    // p >= 1 已经是必出，再算也还是 1
                    if (p <= 0f || p >= 1f) continue;

                    theme.RareSettings[slot] = 1f - (float)Math.Pow(1f - p, area);
                    touched++;
                }
            }

            return touched;
        }

        private static void Grow<T>(ref T[] array, int needed)
        {
            if (array == null) array = new T[needed];
            else if (array.Length < needed) Array.Resize(ref array, needed);
        }
    }
}
