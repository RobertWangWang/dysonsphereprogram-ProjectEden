using System;
using System.Collections.Generic;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>按柜体档位与目标模式，为奇点储能厂补充输入，并把产物送入同星球本地需求槽。</summary>
    [HarmonyPatch]
    internal static class MegaExchangerLogisticsPatches
    {
        private const int Target = 20;

        private struct SlotRef
        {
            internal StationStore[] Storage;
            internal int Index;
        }

        private sealed class Slots
        {
            internal readonly List<SlotRef> Items = new List<SlotRef>();
            internal int Head;
        }

        // PlanetTransport 在多个工作线程处理不同星球，不能共享可变索引。
        // 容器复用，但每轮清除槽位引用，不把上一颗星球或上个存档带入下一轮。
        private sealed class Index
        {
            internal readonly List<int> Exchangers = new List<int>();
            internal readonly HashSet<int> Wanted = new HashSet<int>();
            internal readonly Dictionary<int, Slots> Supply = new Dictionary<int, Slots>();
            internal readonly Dictionary<int, Slots> Demand = new Dictionary<int, Slots>();
            internal void Clear()
            {
                Exchangers.Clear();
                Wanted.Clear();
                foreach (var slots in Supply.Values) { slots.Items.Clear(); slots.Head = 0; }
                foreach (var slots in Demand.Values) { slots.Items.Clear(); slots.Head = 0; }
            }
        }
        [ThreadStatic] private static Index _index;
        [ThreadStatic] private static List<ParallelExchangerIndex.Candidate> _candidates;

        private static void Add(Dictionary<int, Slots> index, int item, StationStore[] storage, int slot)
        {
            if (!index.TryGetValue(item, out var slots)) index[item] = slots = new Slots();
            slots.Items.Add(new SlotRef { Storage = storage, Index = slot });
        }

        private static void BuildIndex(PlanetTransport transport, Index index, long time)
        {
            var candidates = _candidates ?? (_candidates = new List<ParallelExchangerIndex.Candidate>());
            try
            {
                if (ParallelExchangerIndex.TryBuild(transport, index.Wanted, time, candidates))
                {
                    foreach (var candidate in candidates)
                        Add(candidate.Logic == ELogisticStorage.Supply ? index.Supply : index.Demand,
                            candidate.Item, candidate.Storage, candidate.Slot);
                    return;
                }
            }
            finally { candidates.Clear(); }
            int stations = Math.Min(transport.stationCursor, transport.stationPool.Length) - 1;
            if (stations <= 0) return;
            int start = (int)(time % stations);
            for (int offset = 0; offset < stations; offset++)
            {
                int id = 1 + (start + offset) % stations;
                var station = transport.stationPool[id];
                if (station == null || station.id != id || station.storage == null) continue;
                var storage = station.storage;
                lock (storage)
                    for (int slot = 0; slot < storage.Length; slot++)
                    {
                        ref var store = ref storage[slot];
                        if (!index.Wanted.Contains(store.itemId)) continue;
                        if (store.localLogic == ELogisticStorage.Supply)
                            Add(index.Supply, store.itemId, storage, slot);
                        else if (store.localLogic == ELogisticStorage.Demand)
                            Add(index.Demand, store.itemId, storage, slot);
                    }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void Supply(PlanetTransport __instance, long time)
        {
            ProjectEden.Patches.Diagnostics.TransportSplitProbe.Phase("储能柜虚空物流");
            var config = MegaBuildingRegistry.Config;
            if (config?.virtualLogistics != true) return;
            int interval = Math.Max(1, config.virtualIntervalTicks);
            if (time % interval != 0) return;
            var factory = __instance.factory;
            var power = factory?.powerSystem;
            if (power?.excPool == null || factory.entityPool == null || __instance.stationPool == null) return;
            int count = Math.Min(power.excCursor, power.excPool.Length) - 1;
            if (count <= 0) return;
            var index = _index ?? (_index = new Index());
            index.Clear();
            try
            {
                // 轮换收货方，供给不足时不固定优先第一座。
                int start = (int)((time / interval) % count);
                for (int offset = 0; offset < count; offset++)
                {
                    int i = 1 + (start + offset) % count;
                    ref var exc = ref power.excPool[i];
                    if (exc.id != i || exc.entityId <= 0 || exc.entityId >= factory.entityPool.Length) continue;
                    bool ours = false;
                    if (config.buildings != null)
                        foreach (var entry in config.buildings)
                            if (entry != null && entry.itemId == factory.entityPool[exc.entityId].protoId
                                && entry.exchanger != null && entry.displayName == "奇点储能厂") { ours = true; break; }
                    if (!ours) continue;
                    index.Exchangers.Add(i);
                    if (exc.emptyId > 0) index.Wanted.Add(exc.emptyId);
                    if (exc.fullId > 0) index.Wanted.Add(exc.fullId);
                }
                if (index.Exchangers.Count == 0) return;
                // 一轮只遍历一次全星球物流槽；后续只查询对应物品的供应/需求列表。
                BuildIndex(__instance, index, time);
                foreach (int i in index.Exchangers)
                {
                    ref var exc = ref power.excPool[i];
                    // 先出后进，腾出原版只有 20 个柜子的产物缓冲。
                    // 待机清退两类库存，便于停机换档；运行时只输出该模式的产物。
                    if (exc.targetState >= 0f)
                        Ship(index, exc.fullId, ref exc.fullCount, ref exc.fullInc, time);
                    if (exc.targetState <= 0f)
                        Ship(index, exc.emptyId, ref exc.emptyCount, ref exc.emptyInc, time);
                    if (exc.targetState == 0f) continue;
                    bool charging = exc.targetState > 0f;
                    int itemId = charging ? exc.emptyId : exc.fullId;
                    if (itemId <= 0) continue;
                    if (charging) Fill(index, itemId, ref exc.emptyCount, ref exc.emptyInc, time);
                    else Fill(index, itemId, ref exc.fullCount, ref exc.fullInc, time);
                }
            }
            finally { index.Clear(); }
        }

        private static void Ship(Index index, int itemId, ref short count, ref short inc, long time)
        {
            if (itemId <= 0 || count <= 0 || !index.Demand.TryGetValue(itemId, out var slots)) return;
            for (int i = slots.Head; i < slots.Items.Count && count > 0; i++)
            {
                var slot = slots.Items[i];
                lock (slot.Storage)
                {
                    ref var store = ref slot.Storage[slot.Index];
                    if (store.itemId != itemId || store.localLogic != ELogisticStorage.Demand)
                    { if (i == slots.Head) slots.Head++; continue; }
                    long room = (long)store.max - store.count
                        - Math.Max(0, store.localOrder) - Math.Max(0, store.remoteOrder);
                    int moved = (int)Math.Min(count, Math.Max(0L, room));
                    if (moved <= 0) { if (i == slots.Head) slots.Head++; continue; }
                    int points = (int)((long)inc * moved / count);
                    if (points < 0 || (long)store.inc + points > int.MaxValue) continue;
                    store.count += moved;
                    store.inc += points;
                    count -= (short)moved;
                    inc -= (short)points;
                    if (moved == room && i == slots.Head) slots.Head++;
                }
            }
        }

        private static void Fill(Index index, int itemId, ref short count, ref short inc, long time)
        {
            if (count >= Target || !index.Supply.TryGetValue(itemId, out var slots)) return;
            for (int i = slots.Head; i < slots.Items.Count && count < Target; i++)
            {
                var slot = slots.Items[i];
                lock (slot.Storage)
                {
                    ref var store = ref slot.Storage[slot.Index];
                    if (store.itemId != itemId || store.localLogic != ELogisticStorage.Supply)
                    { if (i == slots.Head) slots.Head++; continue; }
                    long available = (long)store.count + Math.Min(0, store.localOrder) + Math.Min(0, store.remoteOrder);
                    int take = (int)Math.Min(Target - count, Math.Max(0, available));
                    if (take <= 0) { if (i == slots.Head) slots.Head++; continue; }
                    int points = (int)((long)store.inc * take / store.count);
                    if (points < 0 || (long)points + inc > short.MaxValue) continue;
                    if (QualityAccess.Ready)
                    {
                        int quality = QualityAccess.GetStationQua(ref store);
                        QualityAccess.SetStationQua(ref store, quality - (int)((long)quality * take / store.count));
                    }
                    count += (short)take;
                    inc += (short)points;
                    store.count -= take;
                    store.inc -= points;
                    if (take == available && i == slots.Head) slots.Head++;
                }
            }
        }
    }
}
