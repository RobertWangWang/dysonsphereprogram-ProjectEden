using System;
using System.Collections.Generic;
using ProjectEden.Utils;

namespace ProjectEden.Patches.UI
{
    /// <summary>直接查询原版星球特征标志，不依赖矿脉扫描或主题名称。</summary>
    internal static class PlanetPropertySearch
    {
        private static readonly EPlanetSingularity[] Flags =
        {
            EPlanetSingularity.TidalLocked, EPlanetSingularity.TidalLocked2,
            EPlanetSingularity.TidalLocked4, EPlanetSingularity.LaySide,
            EPlanetSingularity.ClockwiseRotate, EPlanetSingularity.MultipleSatellites
        };

        private static readonly string[] Names =
        {
            "潮汐锁定（永昼永夜）", "潮汐锁定1:2", "潮汐锁定1:4",
            "横躺自转", "反向自转", "多卫星"
        };

        private static readonly string[] English =
        {
            "Tidal locking (permanent day and night);Tidally locked",
            "Tidal locking 1:2;Tidally locked 1:2",
            "Tidal locking 1:4;Tidally locked 1:4",
            "Horizontal rotation;Lay side;Obliquity",
            "Reverse rotation;Clockwise rotation;Retrograde rotation", "Multiple satellites"
        };

        // 查询先解析成标志掩码；即使当前星系没有这类星球，也能正确显示零结果。
        internal static EPlanetSingularity Parse(string query)
        {
            var mask = (EPlanetSingularity)0;
            if (string.IsNullOrWhiteSpace(query)) return mask;
            query = query.Trim().Replace('：', ':');
            for (int i = 0; i < Flags.Length; i++)
                if (Contains(Names[i], query) || Contains(I18N.Tr(Names[i]), query)
                    || Contains(English[i], query))
                    mask |= Flags[i];
            return mask;
        }

        internal static bool Matches(PlanetData planet, EPlanetSingularity mask) =>
            planet != null && (planet.singularity & mask) != 0;

        internal static string Describe(PlanetData planet)
        {
            var labels = new List<string>();
            for (int i = 0; i < Flags.Length; i++)
                if ((planet.singularity & Flags[i]) != 0) labels.Add(I18N.Tr(Names[i]));
            return string.Join(" / ", labels);
        }

        private static bool Contains(string text, string query) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
