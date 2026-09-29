using System;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>奇点储能厂的满柜出带沿用物流塔集装科技；保留原版模式与出带成功才扣货的语义。</summary>
    [HarmonyPatch]
    internal static class MegaExchangerStackPatches
    {
        private static bool IsVault(CargoTraffic traffic, int entityId)
        {
            var entities = traffic?.factory?.entityPool;
            if (entities == null || entityId <= 0 || entityId >= entities.Length) return false;
            var entries = MegaBuildingRegistry.Config?.buildings;
            if (entries == null) return false;
            foreach (var entry in entries)
                if (entry != null && entry.itemId == entities[entityId].protoId
                    && entry.exchanger != null && entry.displayName == "奇点储能厂") return true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PowerExchangerComponent), nameof(PowerExchangerComponent.InsertItemToBelt),
            new[] { typeof(CargoTraffic), typeof(int), typeof(bool) })]
        private static bool InsertSelected(ref PowerExchangerComponent __instance,
            CargoTraffic cargoTraffic, int beltId, bool isEmptyAcc, ref bool __result)
        {
            if (isEmptyAcc || !IsVault(cargoTraffic, __instance.entityId)) return true;
            __result = InsertFull(ref __instance, cargoTraffic, beltId);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PowerExchangerComponent), nameof(PowerExchangerComponent.InsertItemToBelt),
            new[] { typeof(CargoTraffic), typeof(int) })]
        private static bool InsertIdle(ref PowerExchangerComponent __instance,
            CargoTraffic cargoTraffic, int beltId, ref bool __result)
        {
            if (!IsVault(cargoTraffic, __instance.entityId)) return true;
            // 待机时仍先出空柜，插入失败才试满柜，与原版次序一致。
            __result = (__instance.emptyCount > 0 && __instance.InsertItemToBelt(cargoTraffic, beltId, true))
                || InsertFull(ref __instance, cargoTraffic, beltId);
            return false;
        }

        internal static int StackCount(int count, int inc, int level, int stackMax, int incMax)
        {
            int take = Math.Min(count, Math.Min(Math.Max(1, level), stackMax));
            // 未装加宽 preloader 时也不允许货物或增产点被截断。
            while (take > 0 && SplitPoints(count, inc, take) > incMax) take--;
            return take;
        }

        // 与原版 split_inc 相同：先分每件基础点数，再将余数留给剩余库存。
        internal static int SplitPoints(int count, int inc, int take) => count <= 0 ? 0
            : inc / count * take + Math.Max(0, inc % count - (count - take));

        private static bool InsertFull(ref PowerExchangerComponent exc, CargoTraffic traffic, int beltId)
        {
            if (exc.fullCount <= 0 || beltId <= 0 || traffic.beltPool == null
                || beltId >= traffic.beltPool.Length) return false;
            var path = traffic.GetCargoPath(traffic.beltPool[beltId].segPathId);
            if (path == null) return false;
            int take = StackCount(exc.fullCount, exc.fullInc, GameMain.history?.stationPilerLevel ?? 1,
                CargoWidening.StackIsWide ? short.MaxValue : byte.MaxValue, CargoWidening.IncMax);
            if (take <= 0) return false;
            int inc = SplitPoints(exc.fullCount, exc.fullInc, take);
            if (!CargoWidening.InsertAtHead(path, exc.fullId, take, inc)) return false;
            exc.fullCount -= (short)take;
            exc.fullInc -= (short)inc;
            return true;
        }
    }
}
