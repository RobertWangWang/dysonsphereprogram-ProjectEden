using System;
using System.Threading;

namespace ProjectEden.Patches.Diagnostics
{
    // 仅诊断数字；工作线程逐站计数时不写共享缓存行，阶段结束后再合并。
    internal static class StationDiagnosticCounters
    {
        internal const int LocalSkipped = 0, LocalRan = 1, OutputSkipped = 2, OutputRan = 3;
        private static readonly long[] Totals = new long[4];
        [ThreadStatic] private static long[] _pending;
        internal static void Add(int category)
        {
            if (_pending == null) _pending = new long[4];
            _pending[category]++;
        }
        internal static void Flush()
        {
            if (_pending == null) return;
            for (int i = 0; i < _pending.Length; i++)
            {
                long delta = _pending[i];
                if (delta == 0) continue;
                _pending[i] = 0;
                Interlocked.Add(ref Totals[i], delta);
            }
        }
        internal static long Take(int category) => Interlocked.Exchange(ref Totals[category], 0);
        internal static long Read(int category) => Interlocked.Read(ref Totals[category]);
    }
}
