// dotnet run --project tools/LabLogisticsTests
// 执行实际补丁源码；桩只提供游戏的数据容器和外部入口，不复制物流算法。
using System;
using System.Reflection;
using ProjectEden.Patches;

static class Program
{
    static readonly MethodInfo Tick = typeof(LabLogisticSupplyPatches).GetMethod(
        "PlanetTransport_GameTick", BindingFlags.Static | BindingFlags.NonPublic);

    static void Equal(long expected, long actual, string message)
    {
        if (expected != actual) throw new Exception($"{message}: expected {expected}, actual {actual}");
    }

    static LabComponent Producer(int item, int count) => new LabComponent {
        recipeExecuteData = new RecipeExecuteData { products = new[] { item } }, produced = new[] { count }
    };

    static LabComponent Consumer(params int[] items) => new LabComponent {
        recipeExecuteData = new RecipeExecuteData { requires = items, requireCounts = Array.ConvertAll(items, _ => 1) },
        served = new int[items.Length]
    };

    static LabComponent Research(int stock = 0) => new LabComponent { researchMode = true, matrixServed = new[] { stock } };

    static StationStore Slot(int item, int count, ELogisticStorage logic) => new StationStore {
        itemId = item, count = count, max = 100, localLogic = logic, inc = count * 4, qua = count * 2
    };

    static PlanetTransport World(LabComponent[] labs, params StationStore[] slots)
    {
        ProjectEdenPlugin.LabConfig = new LabConfig {
            logisticSupply = true, logisticOutput = true, supplyIntervalTicks = 10,
            supplyAssembleBatches = 10, supplyMatrixItems = 10
        };
        LabComponent.matrixIds = new[] { 6001 };
        LabComponent.matrixPoints = new[] { 1 };
        var pool = new LabComponent[labs.Length + 1];
        for (int i = 0; i < labs.Length; i++) { pool[i + 1] = labs[i]; pool[i + 1].id = i + 1; }
        return new PlanetTransport {
            factory = new PlanetFactory { factorySystem = new FactorySystem { labPool = pool, labCursor = pool.Length } },
            stationPool = new[] { null, new StationComponent { id = 1, storage = slots } }, stationCursor = 2
        };
    }

    static void Run(PlanetTransport world, long time = 10) => Tick.Invoke(null, new object[] { world, time });
    static LabComponent[] Labs(PlanetTransport world) => world.factory.factorySystem.labPool;

    static void Main()
    {
        // 宇宙矩阵：五种矩阵由研究站产出，反物质与生物矩阵由物流站供应。
        var world = World(new[] { Consumer(6001, 6002, 6003, 6004, 6005, 1122, 6007),
            Producer(6001, 12), Producer(6002, 12), Producer(6003, 12), Producer(6004, 12), Producer(6005, 12) },
            Slot(1122, 20, ELogisticStorage.Supply), Slot(6007, 20, ELogisticStorage.Supply),
            Slot(6001, 0, ELogisticStorage.Demand));
        Run(world);
        foreach (int amount in Labs(world)[1].served) Equal(10, amount, "universe input");
        Equal(2, world.stationPool[1].storage[2].count, "only surplus exported");
        Equal(0, Labs(world)[2].produced[0], "source debited once");
        Equal(10, world.stationPool[1].storage[0].count, "antimatter debit");
        Equal(10, world.stationPool[1].storage[1].count, "bio matrix debit");
        Run(world);
        Equal(2, world.stationPool[1].storage[2].count, "repeat tick does not duplicate");
        Console.WriteLine("PASS universe inputs, station fallback, surplus export, repeated tick");

        world = World(new[] { Consumer(6001), Producer(6001, 7) });
        world.stationPool = null;
        ProjectEdenPlugin.LabConfig.outputReserveItems = 2;
        Run(world);
        Equal(5, Labs(world)[1].served[0], "direct supply without stations");
        Equal(2, Labs(world)[2].produced[0], "reserve retained");
        Console.WriteLine("PASS no stations and output reserve");

        // 同一种矩阵既用于配方又用于科研；科研站排在前面也不抢生产原料。
        world = World(new[] { Research(1), Consumer(6001) }, Slot(6001, 15, ELogisticStorage.Supply));
        Run(world);
        Equal(10, Labs(world)[2].served[0], "production priority / plain count");
        Equal(18001, Labs(world)[1].matrixServed[0], "research scale with fractional stock");
        Equal(0, world.stationPool[1].storage[0].count, "conserved mixed-mode transfer");
        Console.WriteLine("PASS shared matrix demand, units and production priority");

        world = World(new[] { Research(3599) }, Slot(6001, 20, ELogisticStorage.Supply));
        Run(world);
        Equal(35999, Labs(world)[1].matrixServed[0], "fractional target rounds down");
        Equal(11, world.stationPool[1].storage[0].count, "exact whole-item debit");
        Equal(44, world.stationPool[1].storage[0].inc, "remaining spray points");
        Equal(22, world.stationPool[1].storage[0].qua, "remaining quality");
        Run(world);
        Equal(11, world.stationPool[1].storage[0].count, "sub-item gap consumes nothing");
        Console.WriteLine("PASS fractional research stock and station metadata");

        world = World(new[] { Consumer(6001), Producer(6001, 20) }, Slot(6001, 30, ELogisticStorage.Supply));
        ProjectEdenPlugin.LabConfig.logisticOutput = false;
        Run(world);
        Equal(20, Labs(world)[2].produced[0], "output disabled preserves lab output");
        Equal(20, world.stationPool[1].storage[0].count, "station-only exact matrix count");
        Console.WriteLine("PASS output switch and matrix station transfer");

        world = World(new[] { Consumer(6001), Producer(6001, 20) }, Slot(6001, 0, ELogisticStorage.Demand));
        ProjectEdenPlugin.LabConfig.logisticSupply = false;
        Run(world);
        Equal(0, Labs(world)[1].served[0], "supply disabled");
        Equal(20, world.stationPool[1].storage[0].count, "shipping remains enabled");
        Console.WriteLine("PASS supply switch preserves outbound shipping");

        world = World(new[] { Consumer(6001), Producer(6002, 20) }, Slot(6001, 30, ELogisticStorage.Demand));
        Run(world);
        Equal(0, Labs(world)[1].served[0], "demand slot not stolen");
        Equal(20, Labs(world)[2].produced[0], "unrequested product retained");
        Console.WriteLine("PASS slot semantics and recipe matching");

        world = World(new[] { Consumer(6001), Producer(6001, 20) });
        Run(world, 1);
        Equal(0, Labs(world)[1].served[0], "interval respected");
        Labs(world)[2].id = 0;
        Run(world);
        Equal(0, Labs(world)[1].served[0], "deleted source ignored");
        Console.WriteLine("PASS interval and deleted labs");

        world = World(new[] { Consumer(6001), Consumer(6001), Producer(6001, 13) });
        Labs(world)[2].recipeExecuteData.requireCounts[0] = 2;
        Run(world);
        Equal(10, Labs(world)[1].served[0], "first consumer target");
        Equal(3, Labs(world)[2].served[0], "shortage distributed without duplication");
        Equal(0, Labs(world)[3].produced[0], "total donor debit");
        Console.WriteLine("PASS multiple consumers and shortage");
    }
}

public class RecipeExecuteData { public int[] requires, requireCounts, products; }
public struct LabComponent {
    public int id;
    public bool researchMode;
    public RecipeExecuteData recipeExecuteData;
    public int[] served, produced, incServed, matrixServed;
    public static int[] matrixIds, matrixPoints;
}
public class FactorySystem { public LabComponent[] labPool; public int labCursor; }
public class PlanetFactory { public FactorySystem factorySystem; }
public class PlanetTransport { public PlanetFactory factory; public StationComponent[] stationPool; public int stationCursor; public void GameTick(long time) { } }
public class StationComponent { public int id; public StationStore[] storage; }
public struct StationStore { public int itemId, count, max, inc, qua; public ELogisticStorage localLogic; }
public enum ELogisticStorage { None, Supply, Demand }
namespace HarmonyLib {
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class HarmonyPatch : Attribute { public HarmonyPatch() { } public HarmonyPatch(Type type, string method) { } }
    public class HarmonyPostfix : Attribute { }
}
namespace ProjectEden.Patches {
    public class LabConfig {
        public bool logisticSupply, logisticOutput;
        public int supplyIntervalTicks, supplyAssembleBatches, supplyMatrixItems, outputReserveItems;
    }
    public static class ProjectEdenPlugin { public static LabConfig LabConfig; public static Logger Log = new Logger(); }
    public class Logger { public void LogInfo(string text) { } }
    public static class MegaVirtualLogisticsPatches { public static int Rotation(PlanetTransport transport) => 0; }
    public static class QualityAccess {
        public static void TakeStationQua(ref StationStore slot, int take) { slot.qua -= (int)((long)slot.qua * take / slot.count); }
    }
}
namespace ProjectEden.Diagnostics { public static class TransportSplitProbe { public static void Phase(string name) { } } }
