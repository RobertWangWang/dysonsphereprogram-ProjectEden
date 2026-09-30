using System;
using System.Collections.Generic;
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

        private struct FuelSlot
        {
            internal int Station, Slot, Item, Mask;
        }
        [ThreadStatic] private static List<FuelSlot> _slots;
        [ThreadStatic] private static Dictionary<int, int> _fuelMasks;

        // 每轮最多建一次，只收录有库存的燃料供应槽；不缓存跨 tick 的站点引用。
        private static void BuildIndex(PlanetTransport transport, List<FuelSlot> slots)
        {
            var masks = _fuelMasks ?? (_fuelMasks = new Dictionary<int, int>());
            masks.Clear();
            int end = Math.Min(transport.stationCursor, transport.stationPool.Length);
            for (int id = 1; id < end; id++)
            {
                var station = transport.stationPool[id];
                if (station == null || station.id != id || station.storage == null) continue;
                lock (station.storage)
                    foreach (int s in StationConfiguredSlots.Traverse(station.storage, station.id))
                    {
                        ref var store = ref station.storage[s];
                        if (store.localLogic != ELogisticStorage.Supply || store.count <= 0) continue;
                        // 同轮同种物品仅查一次原型，非燃料的负结果也缓存；下一轮清空。
                        if (!masks.TryGetValue(store.itemId, out int mask))
                        {
                            var fuel = LDB.items.Select(store.itemId);
                            mask = fuel != null && fuel.HeatValue > 0 ? fuel.FuelType : 0;
                            masks.Add(store.itemId, mask);
                        }
                        if (mask == 0) continue;
                        slots.Add(new FuelSlot { Station = id, Slot = s, Item = store.itemId, Mask = mask });
                    }
            }
        }

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
            ProjectEden.Patches.Diagnostics.TransportSplitProbe.Phase("聚变燃料虚空物流");
            bool supply = MegaBuildingRegistry.Config?.virtualLogistics == true;
            var factory = __instance.factory;
            var power = factory?.powerSystem;
            if (power?.genPool == null || __instance.stationPool == null) return;

            if (_slots == null) _slots = new List<FuelSlot>();
            var slots = _slots;
            slots.Clear();
            bool indexed = false;
            // 直接访问本轮余数对应的组件，保持每台每秒一次的原有错峰时机。
            int first = (int)(time % 60);
            if (first < 0) return;
            if (first == 0) first = 60;
            for (int i = first; i < Math.Min(power.genCursor, power.genPool.Length); i += 60)
            {
                ref var gen = ref power.genPool[i];
                if (gen.id != i || gen.entityId <= 0) continue;
                var building = LDB.items.Select(factory.entityPool[gen.entityId].protoId);
                if (building?.Name != "微型聚变发电站") continue;
                // 原版字段进存档；已有电站同样对齐，耗料与发电保持原版 100% 热效率。
                gen.genEnergyPerTick = PowerPerTick;
                gen.useFuelPerTick = PowerPerTick;
                if (supply && gen.fuelCount < FuelTarget)
                {
                    // 没有待补给电站时不扫描物流站。
                    if (!indexed) { BuildIndex(__instance, slots); indexed = true; }
                    Fill(__instance, ref gen, time, slots);
                }
            }
            slots.Clear();
        }

        private static void Fill(PlanetTransport transport, ref PowerGeneratorComponent gen, long time, List<FuelSlot> slots)
        {
            int count = Math.Min(transport.stationCursor, transport.stationPool.Length) - 1;
            if (count <= 0) return;
            int start = (int)((time / 60 + gen.id) % count);
            // 二分定位原算法的起始站，保留环形站序和同站格位次序。
            int lo = 0, hi = slots.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (slots[mid].Station < start + 1) lo = mid + 1; else hi = mid;
            }
            for (int offset = 0; offset < slots.Count && gen.fuelCount < FuelTarget; offset++)
            {
                var candidate = slots[(lo + offset) % slots.Count];
                if ((candidate.Mask & gen.fuelMask) == 0 || (gen.fuelCount > 0 && gen.fuelId != candidate.Item)) continue;
                int id = candidate.Station;
                var station = transport.stationPool[id];
                if (station == null || station.id != id || station.storage == null) continue;
                lock (station.storage)
                    {
                        int s = candidate.Slot;
                        if (s >= station.storage.Length) continue;
                        ref var store = ref station.storage[s];
                        if (store.itemId != candidate.Item) continue;
                        if (store.localLogic != ELogisticStorage.Supply || store.count <= 0) continue;
                        if (gen.fuelCount > 0 && gen.fuelId != store.itemId) continue;
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
