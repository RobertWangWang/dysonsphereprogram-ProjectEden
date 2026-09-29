using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches.Diagnostics
{
    /// <summary>仅每个画面帧记一次时间，定期记录卡顿、GC 与进程资源，和任务耗时报告对照。</summary>
    [HarmonyPatch]
    internal static class SystemHealthProbe
    {
        private static long _start, _last, _tick;
        private static int _frames, _slow33, _slow100;
        private static double _maxMs, _cpu;
        private static readonly int[] Gc = new int[3];
        private static bool _announced;
        private static readonly int[] FrameGc = new int[3];
        private static long _frameTick;
        private static GameData _data;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), nameof(UIGame._OnUpdate))]
        private static void Update()
        {
            if (CpuCostProbe.Config?.systemTiming != true && !NanosecondProbe.Enabled) return;
            NanosecondProbe.Flush(Stopwatch.GetTimestamp());
            if (!_announced)
            {
                _announced = true;
                ProjectEdenPlugin.Log.LogInfo("[系统探针] 已启用：画面帧间隔、逻辑 UPS、GC 和进程资源；CPU 分项及物流拆分见相邻日志。"
                    + $"处理器 {SystemInfo.processorType}，逻辑核心 {Environment.ProcessorCount}，显卡 {SystemInfo.graphicsDeviceName}。"
                    + $"计时器频率={Stopwatch.Frequency}Hz，单计数间隔ns={1000000000.0/Stopwatch.Frequency:0.###}，高精度={Stopwatch.IsHighResolution}。"
                    + "纳秒是输出单位，不保证1ns测量精度；帧间隔包含渲染/等待，不代表 GPU 耗时；探针自身也有开销。");
            }
            if (!GameMain.isRunning || GameMain.isPaused || _data != GameMain.data)
            {
                _start = 0;
                _data = GameMain.data;
                return;
            }
            long now = Stopwatch.GetTimestamp();
            if (_start == 0 || GameMain.gameTick < _tick)
            {
                Reset(now);
                return;
            }
            double ms = (now - _last) * 1000.0 / Stopwatch.Frequency;
            if (NanosecondProbe.Enabled && ms >= 1000)
                NanosecondProbe.Event($"秒级长帧 星球={GameMain.localPlanet?.id ?? 0} 逻辑tick={_frameTick}->{GameMain.gameTick} GC增量={GC.CollectionCount(0)-FrameGc[0]}/{GC.CollectionCount(1)-FrameGc[1]}/{GC.CollectionCount(2)-FrameGc[2]}（相关事件需按时间区间对照，不代表因果）", _last, now);
            _frameTick = GameMain.gameTick;
            for (int i = 0; i < 3; i++) FrameGc[i] = GC.CollectionCount(i);
            _last = now;
            _frames++;
            if (ms > 33.333) _slow33++;
            if (ms > 100) _slow100++;
            _maxMs = Math.Max(_maxMs, ms);
            double seconds = (now - _start) / (double)Stopwatch.Frequency;
            if (seconds < Math.Max(5, CpuCostProbe.Config.perfProbeSeconds)) return;
            using (var process = Process.GetCurrentProcess())
            {
                double cpu = process.TotalProcessorTime.TotalSeconds;
                ProjectEdenPlugin.Log.LogInfo($"[系统探针] 窗口 {seconds:0.0}s，星球 {GameMain.localPlanet?.id ?? 0}，"
                    + $"FPS {_frames / seconds:0.0}，UPS {(GameMain.gameTick - _tick) / seconds:0.0}，"
                    + $"平均帧间隔ns={(long)(seconds * 1000000000 / Math.Max(1, _frames))}，最长ns={(long)(_maxMs * 1000000)}，"
                    + $">33ms {_slow33}/{_frames}，>100ms {_slow100}/{_frames}；"
                    + $"GC次数 Δ0/1/2={GC.CollectionCount(0)-Gc[0]}/{GC.CollectionCount(1)-Gc[1]}/{GC.CollectionCount(2)-Gc[2]}，"
                    + $"托管堆 {GC.GetTotalMemory(false)/1048576.0:0.0}MiB，工作集 {(process.WorkingSet64 > 0 ? (process.WorkingSet64/1048576.0).ToString("0.0") + "MiB" : "不可用")}，"
                    + $"进程CPU {(cpu-_cpu)/seconds*100/Environment.ProcessorCount:0.0}%（全逻辑核心归一化）。");
            }
            Reset(now);
        }

        private static void Reset(long now)
        {
            _start = _last = now;
            _tick = GameMain.gameTick;
            _frames = _slow33 = _slow100 = 0;
            _maxMs = 0;
            for (int i = 0; i < 3; i++) Gc[i] = FrameGc[i] = GC.CollectionCount(i);
            _frameTick = GameMain.gameTick;
            using (var process = Process.GetCurrentProcess()) _cpu = process.TotalProcessorTime.TotalSeconds;
        }
    }
}
