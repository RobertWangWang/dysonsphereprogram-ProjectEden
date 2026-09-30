using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ProjectEden.Patches
{
    // 只缓存布局，不缓存库存/容量/需求结果。调用 Get 时持有 storage 锁。
    internal static class StationConfiguredSlots
    {
        internal sealed class Layout
        {
            internal uint Mask;
            internal int Count;
            internal bool Valid;
            internal long CheckedAt, NextCheck;
        }
        private static readonly ConditionalWeakTable<StationStore[], Layout> Layouts = new ConditionalWeakTable<StationStore[], Layout>();
        // 热路径不争抢 ConditionalWeakTable 的共享内部锁；各线程只缓存同一份布局对象的引用。
        // 切换存档时清空，且设置硬上限；WeakReference 不额外保活整个旧 GameData。
        [ThreadStatic] private static Dictionary<StationStore[], Layout> _local;
        [ThreadStatic] private static WeakReference _world;
        [ThreadStatic] private static long _worldTick;

        internal static Layout Get(StationStore[] storage, int stationId, out bool rebuilt)
        {
            long tick = LogisticsTickContext.Tick;
            if (_local == null || _worldTick != tick)
            {
                object world = LogisticsTickContext.World;
                if (_local == null || _world == null || !ReferenceEquals(_world.Target, world))
                {
                    _local = new Dictionary<StationStore[], Layout>();
                    _world = new WeakReference(world);
                }
                _worldTick = tick;
            }
            if (!_local.TryGetValue(storage, out Layout layout))
            {
                if (_local.Count >= 65536) _local.Clear();
                layout = Layouts.GetValue(storage, Create);
                _local.Add(storage, layout);
            }
            rebuilt = !layout.Valid || tick < layout.CheckedAt || tick >= layout.NextCheck;
            if (rebuilt)
            {
                uint mask = 0, bit = 1;
                int count = 0;
                for (int i = 0; i < storage.Length; i++, bit <<= 1)
                    if (storage[i].itemId != 0) { mask |= bit; count++; }
                layout.Mask = mask; layout.Count = count;
                layout.CheckedAt = tick;
                // 按站号错峰，每60逻辑tick复核一次，兜底第三方绕过设置入口的直接写入。
                long phase = (tick % 60 + 60 + (uint)stationId % 60) % 60;
                layout.NextCheck = tick > long.MaxValue - 60 ? long.MaxValue : tick + 60 - phase;
                layout.Valid = true;
            }
            return layout;
        }

        // 共享配置格位索引。稀疏布局跳空格，稠密或超过32格时保持顺序线性遍历。
        // 必须在库存锁内枚举；调用方继续实时校验物品、供需方向、库存和容量。
        internal static SlotEnumerator Traverse(StationStore[] storage, int stationId)
        {
            if (storage.Length <= 32)
            {
                var layout = Get(storage, stationId, out _);
                if (layout.Count <= storage.Length / 2)
                {
                    LogisticsTickContext.NoteTraversal(layout.Count, storage.Length);
                    return new SlotEnumerator(layout.Mask, -1);
                }
            }
            LogisticsTickContext.NoteTraversal(storage.Length, storage.Length);
            return new SlotEnumerator(0, storage.Length);
        }
        internal struct SlotEnumerator
        {
            private uint _mask;
            private int _index;
            private readonly int _length;
            internal SlotEnumerator(uint mask, int length) { _mask = mask; _length = length; _index = -1; }
            public SlotEnumerator GetEnumerator() => this;
            public int Current => _index;
            public bool MoveNext()
            {
                if (_length >= 0) return ++_index < _length;
                if (_mask == 0) return false;
                uint lowest = _mask & unchecked(0u - _mask);
                _index = BitIndices[unchecked(lowest * 0x077CB531u) >> 27];
                _mask &= _mask - 1;
                return true;
            }
        }
        private static readonly int[] BitIndices = { 0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8, 31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9 };

        private static Layout Create(StationStore[] storage) => new Layout();

        internal static void Invalidate(StationStore[] storage)
        {
            if (storage == null || storage.Length > 32) return;
            lock (storage)
                if (Layouts.TryGetValue(storage, out Layout layout)) layout.Valid = false;
        }
    }

    [HarmonyPatch(typeof(PlanetTransport), "SetStationStorage")]
    internal static class StationConfiguredSlotsPatches
    {
        // 异常时也失效，防止设置过程中已经改了物品却没走到正常返回。
        [HarmonyFinalizer]
        private static void Changed(PlanetTransport __instance, int stationId)
        {
            var pool = __instance.stationPool;
            if (pool != null && stationId > 0 && stationId < pool.Length)
                StationConfiguredSlots.Invalidate(pool[stationId]?.storage);
        }
    }
}
