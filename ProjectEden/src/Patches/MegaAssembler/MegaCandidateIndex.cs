using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using ProjectEden.Patches.Diagnostics;

namespace ProjectEden.Patches
{
    // 只索引布局，不缓存库存。每轮校验字段，兼容绕过通知入口的其他mod。
    internal static class MegaCandidateIndex
    {
        internal sealed class Snapshot
        {
            internal StationComponent Station;
            internal StationStore[] Storage;
            internal bool Mega;
            internal int[] Items, Supply, Demand;
            internal ELogisticStorage[] Modes;
        }
        internal sealed class Entry { internal Snapshot Value, Pending; internal bool Changed; }
        internal sealed class State
        {
            internal readonly object Gate = new object();
            internal bool InUse;
            internal Entry[] Entries = new Entry[0];
            internal readonly Dictionary<int, HashSet<int>> Supply = new Dictionary<int, HashSet<int>>();
            internal readonly Dictionary<int, HashSet<int>> Demand = new Dictionary<int, HashSet<int>>();
            internal readonly HashSet<int> MegaSupply = new HashSet<int>(), MegaDemand = new HashSet<int>();
            internal readonly List<int> Selected = new List<int>();
            internal int[] Marks = new int[0];
            internal int Generation;
            internal StationComponent[] Pool;
            internal PlanetFactory Factory;
            internal int End;
            internal void Release() { Pool = null; Factory = null; InUse = false; Monitor.Exit(Gate); }
            internal void Scan(int begin, int end)
            {
                for (int id = begin; id < end; id++)
                {
                    var entry = Entries[id]; var old = entry.Value;
                    var station = id < End ? Pool[id] : null;
                    var storage = station != null && station.id == id ? station.storage : null;
                    entry.Changed = false; entry.Pending = null;
                    if (storage == null) { entry.Changed = old != null; continue; }
                    bool mega = false; int entity = station.entityId;
                    if (entity > 0 && entity < Factory.entityPool.Length)
                    {
                        int assembler = Factory.entityPool[entity].assemblerId;
                        var pool = Factory.factorySystem.assemblerPool;
                        mega = assembler > 0 && assembler < pool.Length && pool[assembler].id == assembler
                            && pool[assembler].speed >= MegaBuildingRegistry.MegaSpeedThreshold;
                    }
                    lock (storage)
                    {
                        bool equal = old != null && ReferenceEquals(old.Station, station) && ReferenceEquals(old.Storage, storage) && old.Mega == mega;
                        if (equal)
                            for (int slot = 0; slot < storage.Length; slot++)
                                if (storage[slot].itemId != old.Items[slot] || storage[slot].localLogic != old.Modes[slot]) { equal = false; break; }
                        if (equal) continue;
                        var snapshot = new Snapshot { Station = station, Storage = storage, Mega = mega,
                            Items = new int[storage.Length], Modes = new ELogisticStorage[storage.Length] };
                        var supply = new List<int>(); var demand = new List<int>();
                        for (int slot = 0; slot < storage.Length; slot++)
                        {
                            int item = snapshot.Items[slot] = storage[slot].itemId;
                            var mode = snapshot.Modes[slot] = storage[slot].localLogic;
                            if (item <= 0) continue;
                            if (mode == ELogisticStorage.Supply) supply.Add(slot);
                            if (mode == ELogisticStorage.Demand) demand.Add(slot);
                        }
                        snapshot.Supply = supply.ToArray(); snapshot.Demand = demand.ToArray();
                        entry.Pending = snapshot; entry.Changed = true;
                    }
                }
            }
            internal void Commit()
            {
                long changed = 0;
                for (int id = 1; id < Entries.Length; id++)
                {
                    var e = Entries[id]; if (!e.Changed) continue;
                    UpdateMap(id, e.Value, false); UpdateMap(id, e.Pending, true);
                    e.Value = e.Pending; e.Pending = null; e.Changed = false; changed++;
                }
                Interlocked.Add(ref _changed, changed);
            }
            private void UpdateMap(int id, Snapshot s, bool add)
            {
                if (s == null) return;
                UpdateMap(Supply, id, s, s.Supply, add); UpdateMap(Demand, id, s, s.Demand, add);
                if (s.Mega)
                {
                    if (add) { if (s.Supply.Length > 0) MegaSupply.Add(id); if (s.Demand.Length > 0) MegaDemand.Add(id); }
                    else { MegaSupply.Remove(id); MegaDemand.Remove(id); }
                }
            }
            private static void UpdateMap(Dictionary<int, HashSet<int>> map, int id, Snapshot s, int[] slots, bool add)
            {
                foreach (int slot in slots)
                {
                    int item = s.Items[slot];
                    if (add) { if (!map.TryGetValue(item, out var set)) map[item] = set = new HashSet<int>(); set.Add(id); }
                    else if (map.TryGetValue(item, out var set)) { set.Remove(id); if (set.Count == 0) map.Remove(item); }
                }
            }
        }
        private static readonly ConditionalWeakTable<PlanetTransport, State> States = new ConditionalWeakTable<PlanetTransport, State>();
        private static Runner _runner;
        private static int _busy, _failed;
        private static long _rounds, _parallel, _changed, _visited, _ticks, _selected;
        internal static State Prepare(PlanetTransport transport, PlanetFactory factory)
        {
            if (CpuCostProbe.Config?.indexedMegaLogistics != true || Volatile.Read(ref _failed) != 0) return null;
            var state = States.GetValue(transport, _ => new State());
            if (!Monitor.TryEnter(state.Gate)) return null;
            if (state.InUse) { Monitor.Exit(state.Gate); return null; }
            state.InUse = true;
            long start = Stopwatch.GetTimestamp();
            try
            {
                int end = Math.Min(transport.stationCursor, transport.stationPool.Length);
                if (state.Entries.Length < end)
                {
                    int old = state.Entries.Length; Array.Resize(ref state.Entries, end); Array.Resize(ref state.Marks, end);
                    for (int i = old; i < end; i++) state.Entries[i] = new Entry();
                }
                state.End = end; state.Pool = transport.stationPool; state.Factory = factory;
                if (CpuCostProbe.Config.parallelMegaIndex && end >= 2049 && Environment.ProcessorCount >= 4 && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
                {
                    try { (_runner ?? (_runner = new Runner())).Run(state); Interlocked.Increment(ref _parallel); }
                    finally { Volatile.Write(ref _busy, 0); }
                }
                else state.Scan(1, state.Entries.Length);
                state.Commit();
                Interlocked.Increment(ref _rounds); Interlocked.Add(ref _visited, Math.Max(0, state.Entries.Length - 1));
                Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - start);
                return state; // 锁由调用者在搬运结束后释放，避免同星球重入破坏查询缓冲。
            }
            catch
            {
                Volatile.Write(ref _failed, 1); States.Remove(transport); state.Release();
                ProjectEdenPlugin.Log.LogWarning("巨型物流候选索引：构建异常，辅助线程已结束；本局后续回退原扫描，本次异常原样上抛。");
                throw;
            }
        }
        private sealed class Worker
        {
            internal readonly AutoResetEvent Wake = new AutoResetEvent(false);
            internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(true);
            internal Worker(Runner runner, int part)
            {
                new Thread(() => { while (true) { Wake.WaitOne(); try { runner.Scan(part); } finally { Done.Set(); } } })
                    { IsBackground = true, Name = "ProjectEden.MegaIndex." + part }.Start();
            }
        }
        private sealed class Runner
        {
            private readonly Worker[] _workers;
            private State _state;
            private Exception _error;
            internal Runner() { _workers = new[] { new Worker(this, 1), new Worker(this, 2) }; }
            internal void Run(State state)
            {
                _state = state; _error = null; int started = 0;
                try
                {
                    foreach (var w in _workers) { w.Done.Reset(); w.Wake.Set(); started++; }
                    Scan(0); foreach (var w in _workers) w.Done.Wait();
                    if (_error != null) ExceptionDispatchInfo.Capture(_error).Throw();
                }
                finally { for (int i = 0; i < started; i++) _workers[i].Done.Wait(); _state = null; _error = null; }
            }
            internal void Scan(int part)
            {
                try { int n = _state.Entries.Length - 1; _state.Scan(1 + (int)((long)n * part / 3), 1 + (int)((long)n * (part + 1) / 3)); }
                catch (Exception e) { Interlocked.CompareExchange(ref _error, e, null); }
            }
        }
        // group：0任意站，1巨型站，2普通站。候选位置仍按原站序排序和轮转。
        internal static StationWalk Select(State state, PlanetTransport transport, ELogisticStorage mode, int group, Dictionary<int, long> items, int start)
        {
            if (state == null) return new StationWalk(null, transport.stationCursor - 1, start);
            var list = state.Selected; list.Clear();
            if (state.Generation == int.MaxValue) { Array.Clear(state.Marks, 0, state.Marks.Length); state.Generation = 0; }
            int generation = ++state.Generation;
            if (items == null)
            {
                foreach (int id in mode == ELogisticStorage.Supply ? state.MegaSupply : state.MegaDemand) list.Add(id);
            }
            else
            {
                var map = mode == ELogisticStorage.Supply ? state.Supply : state.Demand;
                foreach (var item in items)
                {
                    if (item.Value <= 0 || !map.TryGetValue(item.Key, out var ids)) continue;
                    foreach (int id in ids)
                    {
                        bool mega = state.Entries[id].Value.Mega;
                        if ((group == 1 && !mega) || (group == 2 && mega) || state.Marks[id] == generation) continue;
                        state.Marks[id] = generation; list.Add(id);
                    }
                }
            }
            list.Sort(); int first = list.BinarySearch(start + 1); if (first < 0) first = ~first;
            if (first == list.Count) first = 0;
            Interlocked.Add(ref _selected, list.Count);
            return new StationWalk(list, list.Count, first);
        }
        internal struct StationWalk
        {
            private readonly List<int> _list; private readonly int _count, _start; private int _offset;
            internal StationWalk(List<int> list, int count, int start) { _list = list; _count = count; _start = start; _offset = -1; }
            public StationWalk GetEnumerator() => this;
            public int Current => _list == null ? 1 + (_start + _offset) % _count : _list[(_start + _offset) % _count];
            public bool MoveNext() => ++_offset < _count;
        }
        internal static SlotWalk Slots(State state, int id, StationComponent station, ELogisticStorage mode)
        {
            var s = state == null ? null : state.Entries[id].Value;
            if (s != null && ReferenceEquals(s.Storage, station.storage) && ReferenceEquals(s.Station, station))
                return new SlotWalk(mode == ELogisticStorage.Supply ? s.Supply : s.Demand);
            return new SlotWalk(StationConfiguredSlots.Traverse(station.storage, station.id));
        }
        internal struct SlotWalk
        {
            private readonly int[] _slots; private int _index; private StationConfiguredSlots.SlotEnumerator _fallback;
            internal SlotWalk(int[] slots) { _slots = slots; _index = -1; _fallback = default(StationConfiguredSlots.SlotEnumerator); }
            internal SlotWalk(StationConfiguredSlots.SlotEnumerator fallback) { _slots = null; _index = -1; _fallback = fallback; }
            public SlotWalk GetEnumerator() => this;
            public int Current => _slots != null ? _slots[_index] : _fallback.Current;
            public bool MoveNext() => _slots != null ? ++_index < _slots.Length : _fallback.MoveNext();
        }
        internal static void Report()
        {
            long rounds = Interlocked.Exchange(ref _rounds, 0), parallel = Interlocked.Exchange(ref _parallel, 0), changed = Interlocked.Exchange(ref _changed, 0);
            long visited = Interlocked.Exchange(ref _visited, 0), ticks = Interlocked.Exchange(ref _ticks, 0), selected = Interlocked.Exchange(ref _selected, 0);
            ProjectEdenPlugin.Log.LogInfo($"[巨型物流候选索引] 开关={CpuCostProbe.Config?.indexedMegaLogistics == true} 并行={CpuCostProbe.Config?.parallelMegaIndex == true} 故障回退={Volatile.Read(ref _failed) != 0} 轮次={rounds} 并行轮次={parallel} 校验站位={visited} 更新站位={changed} 六阶段候选站累计={selected} 平均准备ns={(rounds > 0 ? ticks * (1000000000.0 / Stopwatch.Frequency) / rounds : 0):0}；逐轮校验布局，只更新变化索引；库存实时读，准备含等待/合并，不含搬运和候选查询。");
        }
    }
}
