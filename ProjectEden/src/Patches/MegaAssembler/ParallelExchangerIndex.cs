using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using ProjectEden.Patches.Diagnostics;

namespace ProjectEden.Patches
{
    // 只读取候选格位；按原轮转offset切连续三段，合并顺序与串行完全一致。
    internal static class ParallelExchangerIndex
    {
        internal struct Candidate
        {
            internal StationStore[] Storage;
            internal int Slot, Item;
            internal ELogisticStorage Logic;
        }
        private static Runner _runner;
        private static int _busy, _failed;
        private static long _rounds, _wall, _merge, _fallbacks, _candidates;
        internal static bool TryBuild(PlanetTransport transport, HashSet<int> wanted, long time, List<Candidate> result)
        {
            if (CpuCostProbe.Config?.parallelExchangerIndex != true || Volatile.Read(ref _failed) != 0 || Environment.ProcessorCount < 4) return false;
            int count = Math.Min(transport.stationCursor, transport.stationPool.Length) - 1;
            if (count < 2048 || time < 0) return false;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { Interlocked.Increment(ref _fallbacks); return false; }
            long begin = Stopwatch.GetTimestamp();
            try
            {
                var runner = _runner ?? (_runner = new Runner());
                runner.Run(transport.stationPool, wanted, count, (int)(time % count), result);
                Interlocked.Increment(ref _rounds);
                Interlocked.Add(ref _wall, Stopwatch.GetTimestamp() - begin);
                Interlocked.Add(ref _candidates, result.Count);
                return true;
            }
            catch
            {
                Volatile.Write(ref _failed, 1);
                ProjectEdenPlugin.Log.LogWarning("储能柜索引并行：异常后已等待辅助线程结束，本局后续回退串行；本次异常原样上抛，尚未执行柜体搬运。");
                throw;
            }
            finally { Volatile.Write(ref _busy, 0); }
        }
        private sealed class Worker
        {
            internal readonly AutoResetEvent Wake = new AutoResetEvent(false);
            internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(true);
            internal Worker(Runner runner, int part)
            {
                new Thread(() => { while (true) { Wake.WaitOne(); try { runner.Scan(part); } finally { Done.Set(); } } })
                    { IsBackground = true, Name = "ProjectEden.ExchangerIndex." + part }.Start();
            }
        }
        private sealed class Runner
        {
            private readonly List<Candidate>[] _parts = { new List<Candidate>(), new List<Candidate>(), new List<Candidate>() };
            private readonly Worker[] _workers;
            private StationComponent[] _pool;
            private HashSet<int> _wanted;
            private int _count, _start;
            private Exception _error;
            internal Runner() { _workers = new[] { new Worker(this, 1), new Worker(this, 2) }; }
            internal void Run(StationComponent[] pool, HashSet<int> wanted, int count, int start, List<Candidate> result)
            {
                _pool = pool; _wanted = wanted; _count = count; _start = start; _error = null;
                int started = 0;
                try
                {
                    foreach (var worker in _workers) { worker.Done.Reset(); worker.Wake.Set(); started++; }
                    Scan(0); // 调用线程扫描第一段，并非只等待。
                    foreach (var worker in _workers) worker.Done.Wait();
                    if (_error != null) ExceptionDispatchInfo.Capture(_error).Throw();
                    long merge = Stopwatch.GetTimestamp();
                    result.Clear();
                    foreach (var part in _parts) result.AddRange(part);
                    Interlocked.Add(ref _merge, Stopwatch.GetTimestamp() - merge);
                }
                finally
                {
                    // 即使调用方失败，也不让读档/退出与未完成的后台读取重叠。
                    for (int i = 0; i < started; i++) _workers[i].Done.Wait();
                    foreach (var part in _parts) part.Clear();
                    _pool = null; _wanted = null; _error = null;
                }
            }
            internal void Scan(int part)
            {
                try
                {
                    int begin = (int)((long)_count * part / 3), end = (int)((long)_count * (part + 1) / 3);
                    var output = _parts[part];
                    for (int offset = begin; offset < end; offset++)
                    {
                        int id = 1 + (_start + offset) % _count;
                        var station = _pool[id];
                        if (station == null || station.id != id || station.storage == null) continue;
                        var storage = station.storage;
                        lock (storage)
                            for (int slot = 0; slot < storage.Length; slot++)
                            {
                                ref var store = ref storage[slot];
                                if (!_wanted.Contains(store.itemId)) continue;
                                if (store.localLogic != ELogisticStorage.Supply && store.localLogic != ELogisticStorage.Demand) continue;
                                output.Add(new Candidate { Storage = storage, Slot = slot, Item = store.itemId, Logic = store.localLogic });
                            }
                    }
                }
                catch (Exception error) { Interlocked.CompareExchange(ref _error, error, null); }
            }
        }
        internal static void Report()
        {
            long rounds = Interlocked.Exchange(ref _rounds, 0), wall = Interlocked.Exchange(ref _wall, 0);
            long merge = Interlocked.Exchange(ref _merge, 0), fallback = Interlocked.Exchange(ref _fallbacks, 0), candidates = Interlocked.Exchange(ref _candidates, 0);
            double ns = 1000000000.0 / Stopwatch.Frequency;
            ProjectEdenPlugin.Log.LogInfo($"[储能柜索引并行] 开关={CpuCostProbe.Config?.parallelExchangerIndex == true} 故障回退={Volatile.Read(ref _failed) != 0} 辅助线程=2 门槛=2048 完成轮次={rounds} 候选格位={candidates} 争用串行回退={fallback} 平均墙钟ns={(rounds > 0 ? wall * ns / rounds : 0):0} 列表合并累计ns={merge * ns:0}；墙钟含扫描/派发/等待/列表合并；最终字典填充计入供需索引分段，实际搬运仍串行。");
        }
    }
}
