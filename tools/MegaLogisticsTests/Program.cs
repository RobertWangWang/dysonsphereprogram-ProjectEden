using System;
using System.Reflection;
using System.Threading.Tasks;
using ProjectEden.Patches;

class Program
{
    static readonly MethodInfo Current = typeof(MegaVirtualLogisticsPatches).GetMethod("PlanetTransport_GameTick", BindingFlags.NonPublic | BindingFlags.Static);
    static readonly MethodInfo Previous = typeof(MegaVirtualReference).GetMethod("PlanetTransport_GameTick", BindingFlags.NonPublic | BindingFlags.Static);
    internal static PlanetTransport Create(int seed, int count = 30)
    {
        var random = new Random(seed);
        var world = new PlanetTransport { stationCursor = count + 1, stationPool = new StationComponent[count + 1], factory = new PlanetFactory { entityPool = new Entity[count + 1], factorySystem = new FactorySystem { assemblerPool = new AssemblerComponent[count + 1] } } };
        for (int id = 1; id <= count; id++)
        {
            world.factory.entityPool[id].assemblerId = id;
            world.factory.factorySystem.assemblerPool[id] = new AssemblerComponent { id = id, speed = id % 3 == 0 ? 100000000 : 0 };
            var storage = new StationStore[id % 7 == 0 ? 40 : 30];
            for (int j = 0; j < storage.Length; j++)
            {
                if (random.Next(10) > seed % 10) continue;
                int stock = random.Next(1000);
                storage[j] = new StationStore { itemId = random.Next(1, 8), count = stock, max = 1000, inc = stock * random.Next(5), qua = stock * random.Next(8), localLogic = (ELogisticStorage)random.Next(3) };
            }
            world.stationPool[id] = new StationComponent { id = random.Next(20) == 0 ? 0 : id, entityId = id, storage = storage };
        }
        return world;
    }
    static void Compare(int seed, int count = 30)
    {
        var a = Create(seed, count); var b = Create(seed, count);
        for (int tick = 0; tick < 3; tick++)
        {
            // 两份世界做同样布局变化，缓存写入通知只属于优化版。
            if (tick != 0)
            {
                a.stationPool[1].storage[29] = b.stationPool[1].storage[29] = new StationStore { itemId = 2 + tick, count = 500, max = 1000, inc = 1500, qua = 1000, localLogic = ELogisticStorage.Supply };
                StationConfiguredSlots.Invalidate(a.stationPool[1].storage);
            }
            Previous.Invoke(null, new object[] { b, 10L });
            LogisticsTickContext.Begin(out var state);
            try { Current.Invoke(null, new object[] { a, 10L }); }
            finally { LogisticsTickContext.Finish(state); }
            for (int i = 1; i < a.stationCursor; i++)
                for (int j = 0; j < a.stationPool[i].storage.Length; j++)
                    if (!a.stationPool[i].storage[j].Equals(b.stationPool[i].storage[j])) throw new Exception($"巨型物流对照失败 seed={seed} tick={tick} station={i} slot={j}");
        }
    }
    static void Main()
    {
        GameMain.gameTick = 100;
        Parallel.For(0, 2000, seed => Compare(seed));
        for (int phase = 0; phase < 30; phase++) { GameMain.gameTick = phase; Compare(phase); }
        for (int phase = 0; phase < 24; phase++) { GameMain.gameTick = phase; Compare(phase, 2053); }
        CandidateIndexTests.Run();
        Console.WriteLine("PASS: 巨型物流2000组并发世界×3轮与原实现逐字段相同，含库存/增产/品质、稀疏/稠密/40格、同站供需、无效站、布局变更和轮转。");
    }
}
public enum ELogisticStorage { None, Supply, Demand }
public struct StationStore { public int itemId, count, max, inc, qua; public ELogisticStorage localLogic; }
public class StationComponent { public int id, entityId; public StationStore[] storage; }
public struct Entity { public int assemblerId; }
public struct AssemblerComponent { public int id, speed; }
public class FactorySystem { public AssemblerComponent[] assemblerPool; }
public class PlanetFactory { public FactorySystem factorySystem; public Entity[] entityPool; }
public class PlanetTransport { public PlanetFactory factory; public int stationCursor; public StationComponent[] stationPool; public void GameTick() {} }
public static class GameMain { public static long gameTick; public static object data = new object(); }
public static class ProjectEdenPlugin { public static Logger Log = new Logger(); }
public class Logger { public void LogInfo(string text) {} public void LogWarning(string text) {} }
namespace HarmonyLib { public class HarmonyPatch : Attribute { public HarmonyPatch() {} public HarmonyPatch(Type t, string name) {} } public class HarmonyPostfix : Attribute {} public class HarmonyPrefix : Attribute {} public class HarmonyFinalizer : Attribute {} }
namespace ProjectEden { public class MegaBuildingsConfig { public bool virtualLogistics = true; public int virtualIntervalTicks = 10, virtualStockPerSlot = 100; } public static class MegaBuildingRegistry { public static MegaBuildingsConfig Config = new MegaBuildingsConfig(); public const int MegaSpeedThreshold = 1000000; } }
namespace ProjectEden.Utils { public static class QualityAccess { public static bool Ready = true; public static int GetStationQua(ref StationStore s) => s.qua; public static void SetStationQua(ref StationStore s, int q) => s.qua = q; } }
namespace ProjectEden.Patches.Diagnostics { public static class TransportSplitProbe { public static bool Armed = true; public static void Phase(string name) {} } }

namespace ProjectEden.Patches.Diagnostics {public static class CpuCostProbe {public static IndexConfig Config=new IndexConfig();}public class IndexConfig {public bool indexedMegaLogistics=true,parallelMegaIndex=true;}}
