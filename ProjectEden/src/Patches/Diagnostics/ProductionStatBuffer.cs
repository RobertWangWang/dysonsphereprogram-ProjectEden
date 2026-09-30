using System;
using System.Collections.Generic;

namespace ProjectEden.Patches.Diagnostics
{
    // 每个工作线程独占一份。只缓存本次调用的统计增量，不读取或修改建筑状态。
    internal sealed class ProductionStatBuffer
    {
        private sealed class Entry
        {
            internal int[] Target, Delta;
        }
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<int> _indices = new List<int>();
        private int _used;
        private bool _active;

        internal bool Begin()
        {
            // 嵌套调用归外层统一合并，避免覆盖仍在使用的数组。
            if (_active) return false;
            _active = true;
            return true;
        }

        internal int[] Redirect(int[] target)
        {
            if (!_active || target == null) return target;
            for (int i = 0; i < _used; i++)
                if (ReferenceEquals(_entries[i].Target, target)) return _entries[i].Delta;
            if (_used == _entries.Count) _entries.Add(new Entry());
            Entry entry = _entries[_used++];
            if (entry.Delta == null || entry.Delta.Length != target.Length)
                entry.Delta = new int[target.Length];
            entry.Target = target;
            return entry.Delta;
        }

        internal void Flush(out int mergedArrays, out int mergedItems)
        {
            mergedArrays = mergedItems = 0;
            try
            {
                for (int i = 0; i < _used; i++)
                {
                    Entry entry = _entries[i];
                    _indices.Clear();
                    // 扫描在线程私有数组上完成，持有共享锁时只做非零项加法。
                    for (int j = 0; j < entry.Delta.Length; j++)
                        if (entry.Delta[j] != 0) _indices.Add(j);
                    if (_indices.Count != 0)
                    {
                        lock (entry.Target)
                            foreach (int j in _indices)
                                entry.Target[j] = unchecked(entry.Target[j] + entry.Delta[j]);
                        mergedArrays++;
                        mergedItems += _indices.Count;
                        foreach (int j in _indices) entry.Delta[j] = 0;
                    }
                    // 不让线程池长期持有已退出存档的统计数组。
                    entry.Target = null;
                }
            }
            finally
            {
                _used = 0;
                _active = false;
                _indices.Clear();
                // 仅复用有限数量的缓冲，避免旅行遍历很多星球后无限增长。
                if (_entries.Count > 32) _entries.RemoveRange(32, _entries.Count - 32);
            }
        }
    }
}
