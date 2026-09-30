using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using HarmonyLib;
using ProjectEden.Patches.Diagnostics;

namespace ProjectEden.Patches
{
    // 只拆运输阶段的第二次需求刷新；原版入库阶段已有站点级并行，保持不动。
    [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick_UpdateNeeds))]
    internal static class ParallelStationNeedsPatches
    {
        private const int MinimumStations = 2048, BatchSize = 256;
        private static int _busy, _failed;
        private static Runner _runner;
        private static long _rounds, _stations, _batches, _wall, _busyFallbacks, _workerTicks;

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Prefix(PlanetTransport __instance) => !TryRun(__instance);

        internal static bool TryRun(PlanetTransport transport)
        {
            if (CpuCostProbe.Config?.parallelStationNeeds != true || !LogisticsTickContext.Active ||
                Volatile.Read(ref _failed) != 0 || Environment.ProcessorCount < 4) return false;
            var pool = transport.stationPool;
            var needs = transport.factory?.entityNeeds;
            if (pool == null || needs == null || transport.stationCursor - 1 < MinimumStations) return false;
            // 全进程只有一组辅助线程。其他星球继续串行工作，不排队等待，也不无限增线程。
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                Interlocked.Increment(ref _busyFallbacks);
                return false;
            }
            try
            {
                var runner = _runner ?? (_runner = new Runner());
                runner.Run(pool, needs, transport.stationCursor, LogisticsTickContext.Tick, LogisticsTickContext.World);
                return true;
            }
            catch
            {
                Volatile.Write(ref _failed, 1);
                ProjectEdenPlugin.Log.LogWarning("需求并行：任务异常，已等待全部辅助任务结束；本局后续回退串行，本次异常原样上抛，不重复执行已完成的站点。");
                throw;
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        private sealed class Runner
        {
            private sealed class Worker
            {
                internal readonly AutoResetEvent Wake = new AutoResetEvent(false);
                internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(true);
                internal Worker(Runner owner, int index)
                {
                    var thread = new Thread(() =>
                    {
                        while (true)
                        {
                            Wake.WaitOne();
                            try { owner.Drain(); }
                            finally { Done.Set(); }
                        }
                    }) { IsBackground = true, Name = "ProjectEden.Needs." + index };
                    thread.Start();
                }
            }
            private readonly Worker[] _workers = new Worker[3];
            private StationComponent[] _pool;
            private int[][] _entityNeeds;
            private object _world;
            private long _tick;
            private int _cursor, _end;
            private Exception _error;
            internal Runner() { for (int i = 0; i < _workers.Length; i++) _workers[i] = new Worker(this, i); }

            internal void Run(StationComponent[] pool, int[][] needs, int end, long tick, object world)
            {
                _pool = pool; _entityNeeds = needs; _end = end; _cursor = 1;
                _tick = tick; _world = world; _error = null;
                long start = Stopwatch.GetTimestamp();
                try
                {
                    foreach (var worker in _workers) { worker.Done.Reset(); worker.Wake.Set(); }
                    Drain(); // 调用线程参与抢批次，而不是只负责等待。
                    foreach (var worker in _workers) worker.Done.Wait();
                    if (_error != null) ExceptionDispatchInfo.Capture(_error).Throw();
                    Interlocked.Increment(ref _rounds);
                    Interlocked.Add(ref _wall, Stopwatch.GetTimestamp() - start);
                }
                finally
                {
                    // 不把任何存档/站点数组留在长期存活的线程任务上。
                    _pool = null; _entityNeeds = null; _world = null; _error = null;
                }
            }

            private void Drain()
            {
                long start = Stopwatch.GetTimestamp(), stations = 0, batches = 0;
                var previous = LogisticsTickContext.Enter(_tick, _world);
                try
                {
                    while (Volatile.Read(ref _error) == null)
                    {
                        int begin = Interlocked.Add(ref _cursor, BatchSize) - BatchSize;
                        if (begin >= _end) break;
                        int end = Math.Min(begin + BatchSize, _end);
                        NeedsRefreshProbe.Begin(out bool sample);
                        Exception error = null;
                        try
                        {
                            for (int id = begin; id < end; id++)
                            {
                                var station = _pool[id];
                                if (station == null || station.id != id) continue;
                                NeedsRefreshProbe.UpdateStation(station);
                                _entityNeeds[station.entityId] = station.needs;
                                stations++;
                            }
                            batches++;
                        }
                        catch (Exception e) { error = e; throw; }
                        finally { NeedsRefreshProbe.Finish(error, sample); }
                    }
                }
                catch (Exception e) { Interlocked.CompareExchange(ref _error, e, null); }
                finally
                {
                    LogisticsTickContext.Finish(previous);
                    Interlocked.Add(ref _stations, stations);
                    Interlocked.Add(ref _batches, batches);
                    Interlocked.Add(ref _workerTicks, Stopwatch.GetTimestamp() - start);
                }
            }
        }

        internal static void Report()
        {
            long rounds = Interlocked.Exchange(ref _rounds, 0), stations = Interlocked.Exchange(ref _stations, 0);
            long batches = Interlocked.Exchange(ref _batches, 0), wall = Interlocked.Exchange(ref _wall, 0);
            long fallback = Interlocked.Exchange(ref _busyFallbacks, 0), work = Interlocked.Exchange(ref _workerTicks, 0);
            double ns = 1000000000.0 / Stopwatch.Frequency;
            ProjectEdenPlugin.Log.LogInfo($"[需求并行] 开关={CpuCostProbe.Config?.parallelStationNeeds == true} 故障回退={Volatile.Read(ref _failed) != 0} 辅助线程上限=3 批大小={BatchSize} 门槛={MinimumStations} 并行轮次={rounds} 已处理站={stations} 已完成批次={batches} 争用串行回退={fallback} 平均墙钟ns={(rounds > 0 ? wall * ns / rounds : 0):0} 工作线程累计ns={work * ns:0}；墙钟含派发/等待，线程累计不能当作帧时。");
        }
    }
}
