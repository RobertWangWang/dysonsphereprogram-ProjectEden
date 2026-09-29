using System;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches.Fusion
{
    /// <summary>微型聚变电站从同星球的本地供应槽取燃料，沿用虚空物流开关。</summary>
    [HarmonyPatch]
    internal static class FusionFuelLogisticsPatches
    {
        internal const int FuelTarget = 5000;
        internal const long PowerPerTick = 50000000; // 3 GW / 60 tick/s

        internal static void ApplyPower()
        {
            foreach (var item in LDB.items.dataArray)
            {
                if (item?.Name != "微型聚变发电站" || item.prefabDesc == null) continue;
                item.prefabDesc.genEnergyPerTick = PowerPerTick;
                item.prefabDesc.useFuelPerTick = PowerPerTick;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void Supply(PlanetTransport __instance, long time)
        {
            bool supply = MegaBuildingRegistry.Config?.virtualLogistics == true;
            var factory = __instance.factory;
            var power = factory?.powerSystem;
            if (power?.genPool == null || __instance.stationPool == null) return;

            // 每台每秒补一次，按组件编号错峰；不保存跨存档的组件引用。
            for (int i = 1; i < power.genCursor; i++)
            {
                if (time % 60 != i % 60) continue;
                ref var gen = ref power.genPool[i];
                if (gen.id != i || gen.entityId <= 0) continue;
                var building = LDB.items.Select(factory.entityPool[gen.entityId].protoId);
                if (building?.Name != "微型聚变发电站") continue;
                // 原版字段进存档；已有电站同样对齐，耗料与发电保持原版 100% 热效率。
                gen.genEnergyPerTick = PowerPerTick;
                gen.useFuelPerTick = PowerPerTick;
                if (supply && gen.fuelCount < FuelTarget) Fill(__instance, ref gen, time);
            }
        }

        private static void Fill(PlanetTransport transport, ref PowerGeneratorComponent gen, long time)
        {
            int count = Math.Min(transport.stationCursor, transport.stationPool.Length) - 1;
            if (count <= 0) return;
            int start = (int)((time / 60 + gen.id) % count);
            for (int offset = 0; offset < count && gen.fuelCount < FuelTarget; offset++)
            {
                int id = 1 + (start + offset) % count;
                var station = transport.stationPool[id];
                if (station == null || station.id != id || station.storage == null) continue;
                lock (station.storage)
                    for (int s = 0; s < station.storage.Length && gen.fuelCount < FuelTarget; s++)
                    {
                        ref var store = ref station.storage[s];
                        if (store.localLogic != ELogisticStorage.Supply || store.count <= 0) continue;
                        if (gen.fuelCount > 0 && gen.fuelId != store.itemId) continue;
                        var fuel = LDB.items.Select(store.itemId);
                        if (fuel == null || fuel.HeatValue <= 0 || (fuel.FuelType & gen.fuelMask) == 0) continue;
                        // 给在途物流预留已承诺出库的货，不动订单计数。
                        int available = (int)Math.Max(0L, (long)store.count
                            + Math.Min(0, store.localOrder) + Math.Min(0, store.remoteOrder));
                        int take = Math.Min(FuelTarget - gen.fuelCount, available);
                        if (take <= 0) continue;
                        // 原版燃料总增产点数为 Int16；缩小本批量，不能溢出或丢掉增产点。
                        if (store.inc > 0)
                            take = (int)Math.Min(take, (long)(short.MaxValue - gen.fuelInc) * store.count / store.inc);
                        if (take <= 0) continue;
                        int inc = (int)((long)store.inc * take / store.count);
                        if (inc < 0 || inc + (long)gen.fuelInc > short.MaxValue) continue;
                        // 燃料没有品质槽；扣除随货带走的品质，不能留给源站余货。
                        if (QualityAccess.Ready)
                        {
                            int quality = QualityAccess.GetStationQua(ref store);
                            QualityAccess.SetStationQua(ref store,
                                quality - (int)((long)quality * take / store.count));
                        }
                        if (gen.fuelCount <= 0) gen.SetNewFuel(store.itemId, (short)take, (short)inc);
                        else
                        {
                            gen.fuelCount += (short)take;
                            gen.fuelInc += (short)inc;
                        }
                        store.count -= take;
                        store.inc -= inc;
                    }
            }
        }
    }
}
