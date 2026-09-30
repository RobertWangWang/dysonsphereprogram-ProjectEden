using System;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    // GameMain.gameTick/data 的 getter 包含 Unity 对象检查；同一物流轮次只读一次。
    // Finalizer 包住原版及所有后置，异常和嵌套调用都恢复线程上下文。
    [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
    internal static class LogisticsTickContext
    {
        internal struct Scope
        {
            internal bool Active, Measure, Entered;
            internal long Tick, Calls, Visited, Full;
            internal object World;
        }
        [ThreadStatic] private static Scope _current;
        private static long _calls, _visited, _full;
        internal static bool Active => _current.Active;
        internal static long Tick => _current.Active ? _current.Tick : GameMain.gameTick;
        internal static object World => _current.Active ? _current.World : GameMain.data;

        [HarmonyPrefix]
        internal static void Begin(out Scope __state) => __state = Enter(GameMain.gameTick, GameMain.data);
        internal static Scope Enter(long tick, object world)
        {
            var previous = _current;
            previous.Entered = true;
            _current = new Scope { Active = true, Tick = tick, World = world,
                Measure = Diagnostics.TransportSplitProbe.Armed };
            return previous;
        }
        [HarmonyFinalizer]
        internal static void Finish(Scope __state)
        {
            if (!__state.Entered) return;
            if (_current.Measure)
            {
                Interlocked.Add(ref _calls, _current.Calls);
                Interlocked.Add(ref _visited, _current.Visited);
                Interlocked.Add(ref _full, _current.Full);
            }
            _current = __state;
        }
        internal static void NoteTraversal(int visited, int full)
        {
            if (!_current.Measure) return;
            _current.Calls++; _current.Visited += visited; _current.Full += full;
        }
        internal static void Report()
        {
            long calls = Interlocked.Exchange(ref _calls, 0), visited = Interlocked.Exchange(ref _visited, 0), full = Interlocked.Exchange(ref _full, 0);
            ProjectEdenPlugin.Log.LogInfo($"[补给格位遍历] 调用={calls} 候选格位={visited} 原全扫格位={full}；仅统计巨型建筑/研究站/聚变补给的格位循环，不含布局复查成本，不代表耗时降幅。");
        }
    }
}
