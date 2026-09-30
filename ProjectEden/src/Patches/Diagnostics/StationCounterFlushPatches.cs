using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ProjectEden.Patches.Diagnostics
{
    [HarmonyPatch]
    internal static class StationCounterFlushPatches
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(PlanetTransport), nameof(PlanetTransport.GameTick));
            yield return AccessTools.Method(typeof(PlanetTransport), nameof(PlanetTransport.GameTick_OutputToBelt));
            // 并行出带绕过上面的星球入口，直接按站点分块执行。
            yield return AccessTools.Method(typeof(GameLogic), "_station_output_parallel");
        }
        [HarmonyFinalizer]
        private static void Finish() => StationDiagnosticCounters.Flush();
    }
}
