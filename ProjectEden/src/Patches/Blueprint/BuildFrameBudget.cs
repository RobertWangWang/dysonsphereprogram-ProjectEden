using System;
namespace ProjectEden.Patches
{
    // 同一渲染帧可能运行多个逻辑tick，共享已用时间，避免补帧时重复花预算。
    internal sealed class BuildFrameBudget
    {
        private int _frame = int.MinValue;
        private long _spent, _started, _limit;
        internal bool Begin(int frame, long now, double milliseconds, long frequency)
        {
            if (_frame != frame) { _frame = frame; _spent = 0; }
            _limit = milliseconds > 0 && !double.IsNaN(milliseconds) && !double.IsInfinity(milliseconds)
                ? (long)(Math.Min(milliseconds, 1000) * frequency / 1000.0) : 0;
            if (_limit > 0 && _spent >= _limit) return false;
            _started = now;
            return true;
        }
        internal bool Expired(long now) => _limit > 0 && _spent + now - _started >= _limit;
        internal void Finish(long now) { _spent += Math.Max(0, now - _started); }
    }
}
