using System;
using ProjectEden.Patches.UI;

// 原版 EPlanetSingularity 的数值；替身仅隔离 Unity 和本地化运行时。
[Flags]
public enum EPlanetSingularity { None = 0, TidalLocked = 1, TidalLocked2 = 2, TidalLocked4 = 4, LaySide = 8, ClockwiseRotate = 16, MultipleSatellites = 32 }
public class PlanetData { public EPlanetSingularity singularity; }
namespace ProjectEden.Utils { internal static class I18N { internal static string Tr(string key) => key; } }
internal static class Program
{
    private static void Main()
    {
        Check("潮汐锁定", 7);
        Check(" TIDAL LOCKING ", 7);
        Check("tidally locked", 7);
        Check("永昼永夜", 1);
        Check("潮汐锁定1：2", 2);
        Check("tidal locking 1:4", 4);
        Check("横躺", 8);
        Check("retrograde", 16);
        Check("多卫星", 32);
        Check("铁", 0);
        Check("iron", 0);
        Check("1001", 0);
        Check("", 0);
        Check(null, 0);
        // 穷举所有原版标志组合，防止属性共存时漏掉结果。
        for (int flags = 0; flags < 64; flags++)
        {
            var planet = new PlanetData { singularity = (EPlanetSingularity)flags };
            if (PlanetPropertySearch.Matches(planet, PlanetPropertySearch.Parse("潮汐锁定")) != ((flags & 7) != 0))
                throw new Exception("潮汐锁定组合匹配失败");
            if (PlanetPropertySearch.Matches(planet, PlanetPropertySearch.Parse("永昼永夜")) != ((flags & 1) != 0))
                throw new Exception("永昼永夜误匹配其他共振");
        }
        if (PlanetPropertySearch.Matches(null, (EPlanetSingularity)7)) throw new Exception("空星球");
        Console.WriteLine("通过：14 项查询、64 种星球标志组合和空星球检查。");
    }
    private static void Check(string query, int expected)
    {
        if ((int)PlanetPropertySearch.Parse(query) != expected) throw new Exception("查询失败：" + query);
    }
}
