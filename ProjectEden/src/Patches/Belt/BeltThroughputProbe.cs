// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 传送带实际跑多快——**量出来，而不是从配置反推**。
    ///
    /// <para><b>「不达标」有四个完全不同的成因，它们在游戏里长得一模一样。</b></para>
    /// 玩家看到的永远只有「货流看起来不够快」，而下面这四条任意一条都会长成那样：
    ///
    /// <list type="number">
    /// <item><b>带子本身没提速。</b> 速度烤进存档两处——<c>BeltComponent.speed</c> 和
    /// <c>CargoPath.chunks</c>（步长 3，速度在第 +2 格），而**货物是按 chunks 里那个值走的**。
    /// 只改前者就是「数字变了、货没变快」，这是本仓库为 <c>BeltSpeedPatches</c> 记过的坑。
    /// 所以这里两个都读，并逐条比对。</item>
    /// <item><b>带子没喂满。</b> 一条空了一半的带子，流量就是满速的一半——而这时带子本身
    /// 一点问题都没有，瓶颈在上游。占用率把这一条和上一条彻底分开。</item>
    /// <item><b>集装没生效。</b> 带子满速满载，但每堆只有 1 层——那 120 格/秒就只有
    /// 120 件/秒，而不是 60 万。平均层数把这一条单拎出来。</item>
    /// <item><b>喂料口本身就喂不到 120 格/秒。</b> 原版每个投放口**一个 tick 最多放一堆**
    /// （<c>TryInsertItemAtHeadAndFillBlank</c> 先 <c>TestBlankAtHead</c> 找空位，一次一堆），
    /// 也就是 <b>60 格/秒</b>——**正好是 120 格/秒带子的一半**。所以单一货源喂的极速带
    /// 天生只能跑到一半，这不是故障，是要多个投放点或者上游本来就该更宽。</item>
    /// </list>
    ///
    /// <para><b>为什么是快照而不是计数器。</b> 一条匀速前进的带子，流量 =
    /// 速度 × 6 × 占用率（格/秒），这是恒等式不是估计：每格货占 <c>kCargoLength = 10</c> 个
    /// 缓冲单位，一 tick 前进 <c>speed</c> 个单位，60 tick/秒。所以**一次快照就够**，
    /// 不必在 tick 路径上挂计数器——那条路径这个仓库的规矩是一个分配都不许有。</para>
    ///
    /// 默认关。开关在 <c>belts.json</c> 的 <c>throughputProbe</c>，**开关在哪个状态都会打一行**。
    /// </summary>
    [HarmonyPatch]
    internal static class BeltThroughputProbe
    {
        /// <summary>每格货占多少缓冲单位。原版常量 <c>CargoPath.kCargoLength</c>。</summary>
        private const int CargoLength = 10;

        /// <summary>一秒多少 tick。</summary>
        private const int TicksPerSecond = 60;

        /// <summary>报告里最多列几条带子（按货物数排序）。</summary>
        private const int TopPaths = 3;

        /// <summary>每条带子最多抽查多少堆货来算平均层数。够用且不会在长带子上变慢。</summary>
        private const int StackSamples = 64;

        private static float _next;

        private static readonly List<Sample> Scratch = new List<Sample>();

        private struct Sample
        {
            internal int PathId;
            internal int Cargo;         // 这条路径上有几堆货
            internal int Capacity;      // 最多能放几堆
            internal int ChunkSpeed;    // CargoPath 自己的速度（货物真正按它走）
            internal int BeltSpeed;     // BeltComponent.speed（存档里的另一份）
            internal int ItemId;
            internal long StackSum;
            internal int StackSamples;
            internal int MaxStack;
        }

        /// <summary>
        /// 开机状态行：**开着 / 关着 / 配置压根没读到**，三种都打。
        ///
        /// 只在开着的时候打的话，「关着」和「这段代码没进 DLL」在日志里长得一样——
        /// 本仓库为这条付过三次账（<c>ReportCheats</c>、<c>CargoShaderIncProbe</c>、这条）。
        /// </summary>
        internal static void Report()
        {
            BeltsConfig cfg = ProjectEdenPlugin.BeltsConfig;

            if (cfg == null)
            {
                ProjectEdenPlugin.Log.LogInfo("传送带流量探针：没读到 belts.json，不启用。");

                return;
            }

            if (!cfg.throughputProbe)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "传送带流量探针：**关着**（默认）。想量一量实际跑多快，把 belts.json 的 "
                    + "throughputProbe 改成 true —— 它会每隔几秒报一次脚下这颗星球最忙的几条带子："
                    + "速度（两份分别读）、占用率、平均集装层数、实测格/秒与件/秒，并指出是哪一段卡着。");

                return;
            }

            ProjectEdenPlugin.Log.LogWarning(
                $"传送带流量探针：**已开启**，每 {Seconds(cfg)} 秒报一次本星球最忙的 {TopPaths} 条带子。"
                + "量的是「速度 × 6 × 占用率」这个恒等式，不在 tick 路径上挂计数器。");
        }

        private static int Seconds(BeltsConfig cfg) => cfg.probeSeconds < 2 ? 10 : cfg.probeSeconds;

        /// <summary>
        /// 挂在 UI 帧上，不在 tick 路径上。
        ///
        /// 计时用 <c>Time.realtimeSinceStartup</c> 而不是 <c>GameMain.gameTick</c>：后者换存档时
        /// 会倒退，于是「下次报告时刻」永远等不到，报告悄无声息地死掉——那看起来和「一切正常」
        /// 一模一样。这条是 <c>CargoLedgerProbe</c> 栽过的。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), nameof(UIGame._OnUpdate))]
        private static void UIGame_OnUpdate_Postfix()
        {
            BeltsConfig cfg = ProjectEdenPlugin.BeltsConfig;

            if (cfg == null || !cfg.throughputProbe) return;

            float now = Time.realtimeSinceStartup;

            if (now < _next) return;

            _next = now + Seconds(cfg);

            Sweep();
        }

        private static void Sweep()
        {
            PlanetFactory factory = GameMain.localPlanet?.factory;
            CargoTraffic traffic = factory?.cargoTraffic;

            if (traffic?.pathPool == null)
            {
                ProjectEdenPlugin.Log.LogInfo("传送带流量探针：脚下没有已加载的工厂，这一轮没得量。");

                return;
            }

            Scratch.Clear();

            for (var i = 1; i < traffic.pathCursor && i < traffic.pathPool.Length; i++)
            {
                CargoPath path = traffic.pathPool[i];

                if (path?.buffer == null || path.bufferLength <= 0) continue;

                Sample s = Measure(path, i);

                if (s.Cargo > 0) Scratch.Add(s);
            }

            if (Scratch.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "传送带流量探针：本星球所有带子上**一件货都没有**，没什么可量的。"
                    + "这一行本身正常——站到有货在跑的带子那颗星球上再看。");

                return;
            }

            Scratch.Sort((a, b) => b.Cargo.CompareTo(a.Cargo));

            AttachBeltSpeeds(traffic);

            int n = Scratch.Count < TopPaths ? Scratch.Count : TopPaths;

            ProjectEdenPlugin.Log.LogInfo(
                $"── 传送带流量（{GameMain.localPlanet?.displayName ?? "本星球"}，"
                + $"{Scratch.Count} 条带子上有货，列最忙的 {n} 条）──");

            for (var i = 0; i < n; i++) ReportPath(Scratch[i]);
        }

        private static Sample Measure(CargoPath path, int pathId)
        {
            var s = new Sample
            {
                PathId = pathId,
                Capacity = path.bufferLength / CargoLength,
                ChunkSpeed = path.chunkCount > 0 ? path.rearSpeed : 0,
                BeltSpeed = -1,
            };

            byte[] buf = path.buffer;
            int len = path.bufferLength < buf.Length ? path.bufferLength : buf.Length;
            int step = 1;

            // 只数「货头」那一格（标记 246 = kCargoHead），一堆货只会被数一次
            for (var i = 0; i < len; i += step)
            {
                if (buf[i] != 246) continue;

                s.Cargo++;

                // 层数抽查：整条带子每堆都查一遍在 5000 格的长带上不划算，
                // 而平均层数只要样本够就稳
                if (s.StackSamples < StackSamples)
                {
                    int itemId = CargoWidening.QueryAt(path, i, out int stack, out int _);

                    if (itemId > 0)
                    {
                        s.ItemId = itemId;
                        s.StackSum += stack;
                        s.StackSamples++;

                        if (stack > s.MaxStack) s.MaxStack = stack;
                    }
                }

                // 下一堆最早也在 kCargoLength 之后
                i += CargoLength - 1;
            }

            return s;
        }

        /// <summary>
        /// 把每条路径配一个 <c>BeltComponent.speed</c>。
        ///
        /// 两份速度分开读是**这个探针的重点之一**：货物按 <c>CargoPath.chunks</c> 走，
        /// 而面板和升级逻辑看的是 <c>BeltComponent.speed</c>，两者都进存档。
        /// 它们不一致时的症状正是「数字变了、货没变快」。
        /// </summary>
        private static void AttachBeltSpeeds(CargoTraffic traffic)
        {
            BeltComponent[] belts = traffic.beltPool;

            if (belts == null) return;

            for (var b = 1; b < traffic.beltCursor && b < belts.Length; b++)
            {
                if (belts[b].id != b) continue;

                int seg = belts[b].segPathId;

                for (var i = 0; i < Scratch.Count; i++)
                {
                    if (Scratch[i].PathId != seg || Scratch[i].BeltSpeed >= 0) continue;

                    Sample s = Scratch[i];

                    s.BeltSpeed = belts[b].speed;
                    Scratch[i] = s;

                    break;
                }
            }
        }

        private static void ReportPath(Sample s)
        {
            float occupancy = s.Capacity > 0 ? (float)s.Cargo / s.Capacity : 0f;
            float avgStack = s.StackSamples > 0 ? (float)s.StackSum / s.StackSamples : 0f;

            // 恒等式：一 tick 前进 speed 个缓冲单位，一堆货占 kCargoLength 个单位
            float ceilingPerSec = s.ChunkSpeed * (float)TicksPerSecond / CargoLength;
            float actualPerSec = ceilingPerSec * occupancy;

            string item = s.ItemId > 0 ? LDB.items.Select(s.ItemId)?.name ?? s.ItemId.ToString() : "—";

            ProjectEdenPlugin.Log.LogInfo(
                $"  路径 {s.PathId}：{item}　货 {s.Cargo}/{s.Capacity} 堆（占用 {occupancy:P0}）"
                + $"　速度 chunks {s.ChunkSpeed} / BeltComponent {(s.BeltSpeed < 0 ? "?" : s.BeltSpeed.ToString())}"
                + $"　平均 {avgStack:0.#} 层（最高 {s.MaxStack}）"
                + $"　→ **实测 {actualPerSec:0.#} 格/秒、{actualPerSec * avgStack:0} 件/秒**"
                + $"（满载上限 {ceilingPerSec:0.#} 格/秒）");

            // ── 逐条指认是哪一段卡着，而不是只报一个数 ──
            if (s.BeltSpeed >= 0 && s.ChunkSpeed != s.BeltSpeed)
                ProjectEdenPlugin.Log.LogWarning(
                    $"    ⚠ 两份速度不一致（chunks {s.ChunkSpeed} ≠ BeltComponent {s.BeltSpeed}）。"
                    + "**货物按 chunks 走**，所以这条带子实际跑的是 chunks 那个数。"
                    + "已建成的带子要靠读档时的逐条改写才会同步，这一行说明那一步没盖到它。");

            if (occupancy < 0.9f)
                ProjectEdenPlugin.Log.LogInfo(
                    $"    · 只装了 {occupancy:P0}，**瓶颈在上游而不是带子**。注意原版每个投放口"
                    + $"一个 tick 最多放一堆货，也就是 {TicksPerSecond} 格/秒——"
                    + $"单一货源喂不满 {ceilingPerSec:0.#} 格/秒的带子是正常的，要多个投放点。");

            if (avgStack > 0f && avgStack < 2f)
                ProjectEdenPlugin.Log.LogInfo(
                    "    · 平均层数不到 2，**集装没堆起来**。集装是自动集装机堆的，"
                    + "科技等级只是上限；货源直接吐到带子上时本来就是 1 层。");
        }
    }
}
