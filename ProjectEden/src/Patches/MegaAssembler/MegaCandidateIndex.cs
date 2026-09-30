using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using ProjectEden.Patches.Diagnostics;

namespace ProjectEden.Patches
{
    // 只索引布局，不缓存库存。身份每轮检查，布局按版本失效并错峰复核。
    internal static class MegaCandidateIndex
    {
        internal sealed class Snapshot
        {
            internal StationComponent Station;
            internal StationStore[] Storage;
            internal bool Mega;
            internal StationConfiguredSlots.Layout Layout;
            internal long Version, CheckedAt, NextCheck;
            internal int[] Items, Supply, Demand;
            internal ELogisticStorage[] Modes;
        }
        // 高频身份扫描顺序访问连续条目，避免每站一个独立Entry对象。
        internal struct Entry { internal Snapshot Value, Pending; }
        // 只在成员变化后排序；数量变化不会使有序视图失效。
        internal sealed class Candidates
        {
            private readonly HashSet<int> _members = new HashSet<int>();
            private int[] _sorted;
            internal int Count => _members.Count;
            internal bool Contains(int id) => _members.Contains(id);
            internal void Clear() { _members.Clear(); _sorted = null; }
            internal HashSet<int>.Enumerator Members() => _members.GetEnumerator();
            internal void Add(int id) { if (_members.Add(id)) _sorted = null; }
            internal void Remove(int id) { if (_members.Remove(id)) _sorted = null; }
            internal int[] Sorted()
            {
                if (_sorted == null)
                {
                    _sorted = new int[_members.Count]; _members.CopyTo(_sorted); Array.Sort(_sorted);
                    Interlocked.Increment(ref _sorts);
                }
                return _sorted;
            }
        }
        // 六个巨型阶段和研究站两方向共用有界查询缓存。键只含正缺口物品，库存仍实时读。
        internal sealed class QueryView
        {
            internal int Group, KeyCount;
            internal ELogisticStorage Mode;
            internal bool AllMega, Valid;
            internal int[] Keys = new int[0];
            internal readonly List<int> Result = new List<int>();
            internal bool Matches(ELogisticStorage mode, int group, Dictionary<int, long> items, int count)
            {
                if (!Valid || Mode != mode || Group != group || AllMega != (items == null) || KeyCount != count) return false;
                for (int i = 0; i < KeyCount; i++)
                    if (!items.TryGetValue(Keys[i], out long value) || value <= 0) return false;
                return true;
            }
            internal void Save(ELogisticStorage mode, int group, Dictionary<int, long> items, int count, List<int> ids)
            {
                Mode = mode; Group = group; AllMega = items == null; KeyCount = count;
                if (Keys.Length < count) Array.Resize(ref Keys, count);
                int at = 0;
                if (items != null) foreach (var pair in items) if (pair.Value > 0) Keys[at++] = pair.Key;
                Result.Clear(); Result.AddRange(ids); Valid = true;
            }
        }
        internal struct MergeCursor { internal int[] Ids; internal int Offset; internal int Id => Ids[Offset]; }
        internal sealed class State
        {
            internal readonly object Gate = new object();
            internal bool InUse;
            internal Entry[] Entries = new Entry[0];
            internal readonly Dictionary<int, Candidates> Supply = new Dictionary<int, Candidates>();
            internal readonly Dictionary<int, Candidates> Demand = new Dictionary<int, Candidates>();
            internal readonly Candidates MegaSupply = new Candidates(), MegaDemand = new Candidates();
            internal readonly ConcurrentQueue<int> Changes = new ConcurrentQueue<int>();
            internal uint[] UnionWords = new uint[0];
            internal readonly List<MergeCursor> Heap = new List<MergeCursor>();
            internal long Tick, LastAudit;
            internal bool Audited, FullCheck;
            internal readonly List<int> Selected = new List<int>();
            internal readonly QueryView[] Queries = new QueryView[8];
            internal int NextQuery;
            internal void InvalidateQueries() { foreach (var q in Queries) if (q != null) q.Valid = false; }
            internal StationComponent[] Pool;
            internal PlanetFactory Factory;
            internal int End;
            internal void Release() { Pool = null; Factory = null; InUse = false; Monitor.Exit(Gate); }
            internal void Scan(int begin, int end)
            {
                long validated = 0, slotsChecked = 0;
                for (int id = begin; id < end; id++)
                {
                    ref var entry = ref Entries[id]; var old = entry.Value;
                    var station = id < End ? Pool[id] : null;
                    var storage = station != null && station.id == id ? station.storage : null;
                    if (storage == null) { if (old != null) { entry.Pending = null; Changes.Enqueue(id); } continue; }
                    bool mega = false; int entity = station.entityId;
                    if (entity > 0 && entity < Factory.entityPool.Length)
                    {
                        int assembler = Factory.entityPool[entity].assemblerId;
                        var pool = Factory.factorySystem.assemblerPool;
                        mega = assembler > 0 && assembler < pool.Length && pool[assembler].id == assembler
                            && pool[assembler].speed >= MegaBuildingRegistry.MegaSpeedThreshold;
                    }
                    bool identity = old != null && ReferenceEquals(old.Station, station) && ReferenceEquals(old.Storage, storage) && old.Mega == mega;
                    if (!FullCheck && identity && old.Version == Volatile.Read(ref old.Layout.Version) && Tick >= old.CheckedAt && Tick < old.NextCheck)
                        continue;
                    validated++;
                    lock (storage)
                    {
                        bool equal = identity;
                        long phase = (Tick % 60 + 60 + (uint)id % 60) % 60;
                        long next = Tick > long.MaxValue - 60 ? long.MaxValue : Tick + 60 - phase;
                        if (equal)
                            for (int slot = 0; slot < storage.Length; slot++)
                                {
                                    slotsChecked++;
                                    if (storage[slot].itemId != old.Items[slot] || storage[slot].localLogic != old.Modes[slot]) { equal = false; break; }
                                }
                        if (equal) { old.Version = Volatile.Read(ref old.Layout.Version); old.CheckedAt = Tick; old.NextCheck = next; continue; }
                        var snapshot = new Snapshot { Station = station, Storage = storage, Mega = mega,
                            Items = new int[storage.Length], Modes = new ELogisticStorage[storage.Length] };
                        var supply = new List<int>(); var demand = new List<int>();
                        for (int slot = 0; slot < storage.Length; slot++)
                        {
                            slotsChecked++;
                            int item = snapshot.Items[slot] = storage[slot].itemId;
                            var mode = snapshot.Modes[slot] = storage[slot].localLogic;
                            if (item <= 0) continue;
                            if (mode == ELogisticStorage.Supply) supply.Add(slot);
                            if (mode == ELogisticStorage.Demand) demand.Add(slot);
                        }
                        snapshot.Layout = StationConfiguredSlots.Track(storage);
                        snapshot.Version = Volatile.Read(ref snapshot.Layout.Version); snapshot.CheckedAt = Tick; snapshot.NextCheck = next;
                        snapshot.Supply = supply.ToArray(); snapshot.Demand = demand.ToArray();
                        entry.Pending = snapshot; Changes.Enqueue(id);
                    }
                }
                Interlocked.Add(ref _validated, validated); Interlocked.Add(ref _slotsChecked, slotsChecked);
            }
            internal void Commit()
            {
                long changed = 0;
                // 大规模蓝图变更时避免逐项删除再插入；等快照统一就绪后顺序重建。
                bool rebuild = Changes.Count >= 256 && (long)Changes.Count * 4 >= Entries.Length;
                while (Changes.TryDequeue(out int id))
                {
                    ref var e = ref Entries[id];
                    if (!rebuild) UpdateDelta(id, e.Value, e.Pending);
                    e.Value = e.Pending; e.Pending = null; changed++;
                }
                if (rebuild) Rebuild();
                Interlocked.Add(ref _changed, changed);
            }
            private static bool Has(Snapshot s, ELogisticStorage mode, int item)
            {
                if (s == null) return false;
                foreach (int slot in mode == ELogisticStorage.Supply ? s.Supply : s.Demand)
                    if (s.Items[slot] == item) return true;
                return false;
            }
            private static bool Eligible(Snapshot s, int group) => s != null
                && (group == 0 || (group == 1 ? s.Mega : !s.Mega));
            private static bool MegaMember(Snapshot s, ELogisticStorage mode) => s != null && s.Mega
                && (mode == ELogisticStorage.Supply ? s.Supply.Length : s.Demand.Length) > 0;
            private void UpdateDelta(int id, Snapshot old, Snapshot next)
            {
                // 查询只依赖站点成员关系，不依赖格位下标；移槽/替换对象仍会更新快照。
                foreach (var q in Queries)
                {
                    if (q == null || !q.Valid) continue;
                    bool changed = q.AllMega
                        ? MegaMember(old, q.Mode) != MegaMember(next, q.Mode)
                        : QueryMember(q, old) != QueryMember(q, next);
                    if (changed) { q.Valid = false; Interlocked.Increment(ref _queryInvalidations); }
                }
                UpdateDirection(Supply, id, old, next, ELogisticStorage.Supply);
                UpdateDirection(Demand, id, old, next, ELogisticStorage.Demand);
                UpdateMega(MegaSupply, id, old, next, ELogisticStorage.Supply);
                UpdateMega(MegaDemand, id, old, next, ELogisticStorage.Demand);
            }
            private static bool QueryMember(QueryView q, Snapshot s)
            {
                if (!Eligible(s, q.Group)) return false;
                for (int i = 0; i < q.KeyCount; i++) if (Has(s, q.Mode, q.Keys[i])) return true;
                return false;
            }
            private static void UpdateMega(Candidates set, int id, Snapshot old, Snapshot next, ELogisticStorage mode)
            {
                bool before = MegaMember(old, mode), after = MegaMember(next, mode);
                if (before == after) return;
                if (after) set.Add(id); else set.Remove(id);
            }
            private static void UpdateDirection(Dictionary<int, Candidates> map, int id, Snapshot old, Snapshot next, ELogisticStorage mode)
            {
                if (old != null)
                    foreach (int slot in mode == ELogisticStorage.Supply ? old.Supply : old.Demand)
                    {
                        int item = old.Items[slot];
                        if (Has(next, mode, item) || !map.TryGetValue(item, out var set)) continue;
                        set.Remove(id); if (set.Count == 0) map.Remove(item);
                    }
                if (next != null)
                    foreach (int slot in mode == ELogisticStorage.Supply ? next.Supply : next.Demand)
                    {
                        int item = next.Items[slot];
                        if (Has(old, mode, item)) continue;
                        if (!map.TryGetValue(item, out var set)) map[item] = set = new Candidates();
                        set.Add(id);
                    }
            }
            internal void Rebuild()
            {
                InvalidateQueries();
                Supply.Clear(); Demand.Clear(); MegaSupply.Clear(); MegaDemand.Clear();
                for (int id = 1; id < Entries.Length; id++) UpdateMap(id, Entries[id].Value, true);
                Interlocked.Increment(ref _rebuilds);
            }
            // 在同一轮布局复核和提交之后检查双向关系；不比较不同tick的库存。
            internal bool MapsMatch()
            {
                for (int id = 1; id < Entries.Length; id++)
                {
                    var s = Entries[id].Value;
                    if (MegaSupply.Contains(id) != (s != null && s.Mega && s.Supply.Length > 0)
                        || MegaDemand.Contains(id) != (s != null && s.Mega && s.Demand.Length > 0)) return false;
                    if (s == null) continue;
                    foreach (int slot in s.Supply)
                        if (!Supply.TryGetValue(s.Items[slot], out var ids) || !ids.Contains(id)) return false;
                    foreach (int slot in s.Demand)
                        if (!Demand.TryGetValue(s.Items[slot], out var ids) || !ids.Contains(id)) return false;
                }
                return ReverseMatches(Supply, true) && ReverseMatches(Demand, false)
                    && ValidIds(MegaSupply) && ValidIds(MegaDemand);
            }
            private bool ValidIds(Candidates ids)
            {
                var it = ids.Members();
                while (it.MoveNext()) if (it.Current <= 0 || it.Current >= Entries.Length) return false;
                return true;
            }
            private bool ReverseMatches(Dictionary<int, Candidates> map, bool supply)
            {
                foreach (var pair in map)
                {
                    var it = pair.Value.Members();
                    while (it.MoveNext())
                    {
                        int id = it.Current;
                        if (id <= 0 || id >= Entries.Length) return false;
                        var s = Entries[id].Value;
                        if (s == null) return false;
                        bool found = false;
                        foreach (int slot in supply ? s.Supply : s.Demand)
                            if (s.Items[slot] == pair.Key) { found = true; break; }
                        if (!found) return false;
                    }
                }
                return true;
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
            private static void UpdateMap(Dictionary<int, Candidates> map, int id, Snapshot s, int[] slots, bool add)
            {
                foreach (int slot in slots)
                {
                    int item = s.Items[slot];
                    if (add) { if (!map.TryGetValue(item, out var set)) map[item] = set = new Candidates(); set.Add(id); }
                    else if (map.TryGetValue(item, out var set)) { set.Remove(id); if (set.Count == 0) map.Remove(item); }
                }
            }
        }
        private static readonly ConditionalWeakTable<PlanetTransport, State> States = new ConditionalWeakTable<PlanetTransport, State>();
        private static Runner _runner;
        private static int _busy, _failed;
        private static long _rounds, _parallel, _changed, _visited, _ticks, _selected, _validated, _sorts, _queryTicks, _slotsChecked, _commitTicks, _audits, _auditRepairs, _rebuilds, _queryHits, _queryInvalidations, _bitmapQueries, _heapQueries, _queries, _scanBatches;
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
                    Array.Resize(ref state.Entries, end);
                }
                state.Tick = LogisticsTickContext.Tick;
                state.FullCheck = !state.Audited || state.Tick < state.LastAudit || state.Tick - state.LastAudit >= 600;
                state.End = end; state.Pool = transport.stationPool; state.Factory = factory;
                if (CpuCostProbe.Config.parallelMegaIndex && end >= 2049 && Environment.ProcessorCount >= 4 && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
                {
                    try { (_runner ?? (_runner = new Runner())).Run(state); Interlocked.Increment(ref _parallel); }
                    finally { Volatile.Write(ref _busy, 0); }
                }
                else state.Scan(1, state.Entries.Length);
                long commitStart = Stopwatch.GetTimestamp();
                state.Commit();
                if (state.FullCheck)
                {
                    Interlocked.Increment(ref _audits);
                    if (!state.MapsMatch())
                    {
                        state.Rebuild(); Interlocked.Increment(ref _auditRepairs);
                        ProjectEdenPlugin.Log.LogWarning("共享物流候选索引：同边界对账发现不一致，已由本轮完整布局重建映射。");
                    }
                    state.Audited = true; state.LastAudit = state.Tick;
                }
                Interlocked.Add(ref _commitTicks, Stopwatch.GetTimestamp() - commitStart);
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
            private int _cursor, _end;
            private const int BatchSize = 512;
            internal Runner() { _workers = new[] { new Worker(this, 1), new Worker(this, 2) }; }
            internal void Run(State state)
            {
                _state = state; _error = null; _cursor = 1; _end = state.Entries.Length; int started = 0;
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
                try
                {
                    // 各批次拥有互斥站号范围；布局密集区不再固定压在某一个线程上。
                    long batches = 0;
                    while (Volatile.Read(ref _error) == null)
                    {
                        int begin = Interlocked.Add(ref _cursor, BatchSize) - BatchSize;
                        if (begin >= _end) break;
                        _state.Scan(begin, Math.Min(_end, begin + BatchSize));
                        batches++;
                    }
                    Interlocked.Add(ref _scanBatches, batches);
                }
                catch (Exception e) { Interlocked.CompareExchange(ref _error, e, null); }
            }
        }
        // group：0任意站，1巨型站，2普通站。候选位置仍按原站序排序和轮转。
        internal static StationWalk Select(State state, PlanetTransport transport, ELogisticStorage mode, int group, Dictionary<int, long> items, int start)
        {
            if (state == null) return new StationWalk(null, transport.stationCursor - 1, start);
            Interlocked.Increment(ref _queries);
            long queryStart = Stopwatch.GetTimestamp();
            int keyCount = 0;
            if (items != null) foreach (var pair in items) if (pair.Value > 0) keyCount++;
            foreach (var cached in state.Queries)
                if (cached != null && cached.Matches(mode, group, items, keyCount))
                {
                    Interlocked.Increment(ref _queryHits);
                    return Walk(cached.Result, start, queryStart);
                }
            var list = state.Selected; list.Clear();
            if (items == null)
                list.AddRange((mode == ELogisticStorage.Supply ? state.MegaSupply : state.MegaDemand).Sorted());
            else
            {
                var heap = state.Heap; heap.Clear();
                long members = 0;
                var map = mode == ELogisticStorage.Supply ? state.Supply : state.Demand;
                foreach (var item in items)
                {
                    if (item.Value <= 0 || !map.TryGetValue(item.Key, out var ids)) continue;
                    var cursor = new MergeCursor { Ids = ids.Sorted() };
                    if (cursor.Ids.Length == 0) continue;
                    members += cursor.Ids.Length;
                    int at = heap.Count; heap.Add(cursor);
                    while (at > 0)
                    {
                        int parent = (at - 1) / 2;
                        if (heap[parent].Id <= cursor.Id) break;
                        heap[at] = heap[parent]; at = parent;
                    }
                    heap[at] = cursor;
                }
                int words = (state.Entries.Length + 31) / 32;
                // 稠密多物品并集不必逐元素维护堆。清空位图的成本按站位/32计，
                // 稀疏候选仍走堆，避免为了几个站遍历整个位图。
                if (heap.Count > 1 && members >= (long)words * 2)
                {
                    Interlocked.Increment(ref _bitmapQueries);
                    if (state.UnionWords.Length < words) Array.Resize(ref state.UnionWords, words);
                    Array.Clear(state.UnionWords, 0, words);
                    foreach (var cursor in heap)
                        foreach (int id in cursor.Ids) state.UnionWords[id >> 5] |= 1u << (id & 31);
                    for (int word = 0; word < words; word++)
                        foreach (int bit in new StationConfiguredSlots.SlotEnumerator(state.UnionWords[word], -1))
                        {
                            int id = word * 32 + bit;
                            bool mega = state.Entries[id].Value.Mega;
                            if (!((group == 1 && !mega) || (group == 2 && mega))) list.Add(id);
                        }
                    heap.Clear();
                }
                else
                {
                    Interlocked.Increment(ref _heapQueries);
                    int previous = -1;
                    while (heap.Count > 0)
                    {
                        var cursor = heap[0]; int id = cursor.Id;
                        if (id != previous)
                        {
                            bool mega = state.Entries[id].Value.Mega;
                            if (!((group == 1 && !mega) || (group == 2 && mega))) list.Add(id);
                            previous = id;
                        }
                        cursor.Offset++;
                        if (cursor.Offset == cursor.Ids.Length)
                        {
                            cursor = heap[heap.Count - 1]; heap.RemoveAt(heap.Count - 1);
                            if (heap.Count == 0) break;
                        }
                        int at = 0;
                        while (at * 2 + 1 < heap.Count)
                        {
                            int child = at * 2 + 1;
                            if (child + 1 < heap.Count && heap[child + 1].Id < heap[child].Id) child++;
                            if (cursor.Id <= heap[child].Id) break;
                            heap[at] = heap[child]; at = child;
                        }
                        heap[at] = cursor;
                    }
                }
            }
            int cacheSlot = state.NextQuery++ % state.Queries.Length;
            if (state.NextQuery == int.MaxValue) state.NextQuery = 0;
            var saved = state.Queries[cacheSlot] ?? (state.Queries[cacheSlot] = new QueryView());
            saved.Save(mode, group, items, keyCount, list);
            return Walk(saved.Result, start, queryStart);
        }
        private static StationWalk Walk(List<int> list, int start, long queryStart)
        {
            int first = list.BinarySearch(start + 1); if (first < 0) first = ~first;
            Interlocked.Add(ref _queryTicks, Stopwatch.GetTimestamp() - queryStart);
            if (first == list.Count) first = 0;
            Interlocked.Add(ref _selected, list.Count);
            return new StationWalk(list, list.Count, first);
        }
        internal struct StationWalk
        {
            private readonly List<int> _list;
            private readonly int _count;
            private int _position, _remaining;
            internal StationWalk(List<int> list, int count, int start)
            {
                _list = list; _count = Math.Max(0, count); _remaining = _count;
                // 调用方通常已归一化；保留旧实现对超过站数起点的轮转含义。
                int first = _count > 0 ? start % _count : 0;
                _position = first - 1;
            }
            public StationWalk GetEnumerator() => this;
            public int Current => _list == null ? _position + 1 : _list[_position];
            public bool MoveNext()
            {
                if (_remaining == 0) return false;
                _remaining--;
                if (++_position == _count) _position = 0;
                return true;
            }
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
            long validated = Interlocked.Exchange(ref _validated, 0), sorts = Interlocked.Exchange(ref _sorts, 0), queryTicks = Interlocked.Exchange(ref _queryTicks, 0);
            long slotsChecked = Interlocked.Exchange(ref _slotsChecked, 0), commitTicks = Interlocked.Exchange(ref _commitTicks, 0);
            long audits = Interlocked.Exchange(ref _audits, 0), repairs = Interlocked.Exchange(ref _auditRepairs, 0), rebuilds = Interlocked.Exchange(ref _rebuilds, 0);
            long hits = Interlocked.Exchange(ref _queryHits, 0), invalidations = Interlocked.Exchange(ref _queryInvalidations, 0);
            long bitmap = Interlocked.Exchange(ref _bitmapQueries, 0), heaps = Interlocked.Exchange(ref _heapQueries, 0), queries = Interlocked.Exchange(ref _queries, 0);
            long batches = Interlocked.Exchange(ref _scanBatches, 0);
            ProjectEdenPlugin.Log.LogInfo($"[巨型物流候选索引] 开关={CpuCostProbe.Config?.indexedMegaLogistics == true} 并行={CpuCostProbe.Config?.parallelMegaIndex == true} 故障回退={Volatile.Read(ref _failed) != 0} 轮次={rounds} 并行轮次={parallel} 动态扫描批次={batches} 批大小=512 校验站位={visited} 更新站位={changed} 巨型/研究站候选累计={selected} 完整审计={audits} 修复={repairs} 重建={rebuilds} 平均准备ns={(rounds > 0 ? ticks * (1000000000.0 / Stopwatch.Frequency) / rounds : 0):0} 实际布局复核={validated} 比较/构建格位={slotsChecked} 提交总ns={commitTicks * (1000000000.0 / Stopwatch.Frequency):0} 有序视图重建={sorts} 查询数={queries} 位图合并={bitmap} 堆合并={heaps} 查询缓存命中={hits} 局部失效={invalidations} 查询总ns={queryTicks * (1000000000.0 / Stopwatch.Frequency):0}；逐轮检查身份，版本失效/60tick错峰复核布局，队列提交变化，有序归并；库存实时读，准备含等待/合并，不含搬运和候选查询。");
        }
    }
}
