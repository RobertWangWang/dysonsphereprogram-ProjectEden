using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;

namespace ProjectEden.Patches.Diagnostics
{
    // 只在系统探针开启时，每20秒扫描一次本地工厂；同时看机器与物流槽，区分缺料和结算慢。
    [HarmonyPatch]
    internal static class AntimatterLineProbe
    {
        private struct Sample { internal int Cycles, Recipe; internal long Tick, Clock; }
        private static readonly Dictionary<int, Sample> Samples = new Dictionary<int, Sample>();
        private static PlanetFactory _factory;
        private static long _next;
        [HarmonyPostfix, HarmonyPatch(typeof(UIGame), nameof(UIGame._OnUpdate))]
        private static void Update()
        {
            if (CpuCostProbe.Config?.systemTiming != true || !GameMain.isRunning || GameMain.isPaused) return;
            var factory = GameMain.localPlanet?.factory;
            if (factory?.factorySystem == null) return;
            long clock = Stopwatch.GetTimestamp();
            if (_factory != factory) { _factory = factory; Samples.Clear(); _next = 0; }
            if (clock < _next) return;
            _next = clock + 20 * Stopwatch.Frequency;
            var pool = factory.factorySystem.assemblerPool;
            var shown = new int[4]; var counts = new int[4]; var shortInput = new int[4]; var blocked = new int[4];
            var sb = new StringBuilder($"[反物质产线探针] 星球={factory.planetId} tick={GameMain.gameTick}；每类最多显示2台，缺料/堵料为当前快照。\n");
            for (int i = 1; i < factory.factorySystem.assemblerCursor; i++)
            {
                ref var c = ref pool[i];
                int type = (int)c.recipeType - 19;
                if (c.id != i || type < 0 || type >= 4 || c.recipeExecuteData == null) continue;
                counts[type]++;
                var d = c.recipeExecuteData;
                bool lacking = false, full = false;
                for (int j = 0; j < d.requireCounts.Length; j++) if (c.served[j] < d.requireCounts[j]) lacking = true;
                for (int j = 0; j < d.productCounts.Length; j++)
                    if ((long)c.produced[j] > (long)d.productCounts[j] * MegaOutputGatePatches.Scale(19, ref c)) full = true;
                if (lacking) shortInput[type]++;
                if (full) blocked[type]++;
                if (shown[type]++ >= 2) continue;
                int entity = c.entityId;
                var e = factory.entityPool[entity];
                float power = factory.powerSystem.networkServes[factory.powerSystem.consumerPool[c.pcId].networkId];
                string rate = "首次采样";
                if (Samples.TryGetValue(entity, out var old) && old.Recipe == c.recipeId && GameMain.gameTick > old.Tick && c.cycleCount >= old.Cycles)
                    rate = $"实秒周期/s={(c.cycleCount-old.Cycles)* (double)Stopwatch.Frequency/(clock-old.Clock):0.##} 逻辑秒周期/s={(c.cycleCount-old.Cycles)*60.0/(GameMain.gameTick-old.Tick):0.##}";
                Samples[entity] = new Sample {Cycles=c.cycleCount,Recipe=c.recipeId,Tick=GameMain.gameTick,Clock=clock};
                sb.Append($"  {LDB.items.Select(e.protoId)?.name} 实体={entity} 配方={c.recipeId} speed={c.speed} speedOverride={c.speedOverride} 供电={power:P1} {rate} 周期预算={MegaThrottle.CyclesFor(factory,entity,MegaBuildingRegistry.Config?.cyclesPerTick??1)} 分频={MegaThrottle.DividerFor(factory,entity)} time={c.time}/{d.timeSpend}\n");
                StationComponent station = e.stationId > 0 ? factory.transport.stationPool[e.stationId] : null;
                for (int j = 0; j < d.requires.Length; j++)
                    sb.Append($"    原料 {LDB.items.Select(d.requires[j])?.name ?? d.requires[j].ToString()}：机器={c.served[j]} 单周期需求={d.requireCounts[j]} 本机物流槽={Stock(station,d.requires[j])}\n");
                for (int j = 0; j < d.products.Length; j++)
                    sb.Append($"    产物 {LDB.items.Select(d.products[j])?.name ?? d.products[j].ToString()}：机器={c.produced[j]} 本机物流槽={Stock(station,d.products[j])}\n");
            }
            for (int i = 0; i < 4; i++) sb.Append($"  类型{i+19}：总台数={counts[i]} 当前原料不足={shortInput[i]} 当前产物堵塞={blocked[i]}\n");
            if (counts[0]+counts[1]+counts[2]+counts[3]>0) ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }
        private static string Stock(StationComponent station, int item)
        {
            if (station?.storage == null) return "无物流站";
            lock(station.storage)
            {
                for(int i=0;i<station.storage.Length;i++)
                    if(station.storage[i].itemId==item)
                        return $"{station.storage[i].count}/{station.storage[i].max}({station.storage[i].localLogic})";
            }
            return "未配置";
        }
    }
}
