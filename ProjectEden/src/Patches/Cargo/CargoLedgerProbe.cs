using System;
using System.Collections.Concurrent;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 「货物账本」骨架的最小验证探针。<b>不改任何游戏逻辑，只观察。</b>
    ///
    /// <b>要验证的是什么。</b> 传送带上每堆货的增产点数存在 <c>Cargo.inc</c>（一个 byte，
    /// 存的是<b>整堆</b>的总点数），增产剂 Mk.III 每件 4 点，于是 <c>stack × 4 ≤ 255</c>
    /// 把集装层数钉死在 <b>63</b>（详见 CLAUDE.md 的 cargo stacking ceiling 一节）。
    ///
    /// 想突破又不碰渲染，办法是<b>另开一本账</b>：<c>Cargo</c> 结构体一个字节都不动
    /// （它每帧按 32 字节 stride 裸传 GPU，动了传送带就花屏），真实总点数放进一个
    /// 与 <c>cargoPool</c> 平行、按 cargoId 索引的数组。
    ///
    /// <b>这个探针只回答一个问题：那本账跟得住吗。</b> 三个风险点：
    /// <list type="number">
    /// <item><b>扩容</b>——<c>cargoPool</c> 会 <c>Expand2x</c>，账本得跟着长；读档时容量整个换掉。</item>
    /// <item><b>ID 回收</b>——cargoId 是复用的。删除时不销账，下一个占用该槽位的货就会
    /// 继承前任的点数，等于凭空造增产剂。</item>
    /// <item><b>并行</b>——容器是每星球一份（<c>PlanetFactory.cargoContainer</c>），而星球是多线程并行 tick 的。</item>
    /// </list>
    ///
    /// 验证手法是<b>让账本自证</b>：入账时记下这堆货的物品 ID，之后定期全量比对
    /// 「账本记的」和「货物实际的」。对不上就说明骨架有洞——不用等到真接管点数才发现。
    ///
    /// 目前<b>默认打开</b>（`stations.json` 的 <c>cargoLedgerProbe</c>）——配置是嵌进 DLL 的
    /// 嵌入资源，关掉就得重新编译，所以在验证期间保持开着。验证完就该关。
    /// </summary>
    [HarmonyPatch]
    internal static class CargoLedgerProbe
    {
        /// <summary>一个容器（一颗星球）的账本。</summary>
        private sealed class Ledger
        {
            /// <summary>按 cargoId 索引，与 cargoPool 平行。0 = 该槽位没有货。</summary>
            internal int[] Tags;

            internal Ledger(int capacity) => Tags = new int[capacity > 0 ? capacity : 1];

            internal void EnsureSize(int capacity)
            {
                if (Tags.Length >= capacity) return;

                // 按目标容量重建，不在这里自作主张 ×2——扩容节奏由游戏定，跟着它走
                Array.Resize(ref Tags, capacity);
            }
        }

        /// <summary>
        /// 每个 CargoContainer 一本账。<b>必须是并发字典</b>：星球是并行 tick 的，
        /// 普通 Dictionary 被多个 worker 线程写几分钟就会炸
        /// （CLAUDE.md 陷阱 4，MegaVirtualLogisticsPatches 真踩过）。
        /// </summary>
        private static readonly ConcurrentDictionary<CargoContainer, Ledger> Ledgers =
            new ConcurrentDictionary<CargoContainer, Ledger>();

        /// <summary>
        /// 上一次用过的 (容器, 账本)。AddCargo / RemoveCargo 是热路径，
        /// 而一个 worker 线程在一段时间内只处理同一颗星球，缓存能省掉绝大多数字典查找。
        /// <b>ThreadStatic 的初始化器只在第一个线程上跑</b>，所以不能给初值。
        /// </summary>
        [ThreadStatic] private static CargoContainer _lastContainer;

        [ThreadStatic] private static Ledger _lastLedger;

        /// <summary>
        /// 加宽生效时连补丁都不要打。
        ///
        /// <c>Enabled</c> 只拦住了方法<b>体</b>，Harmony 的 <c>PatchAll</c> 照样会去
        /// 改写 <c>CargoContainer.Import</c>——而读一个被 preloader 改过的方法体
        /// 本身就是风险（第一版就是在这里把一个坏分支炒成了启动崩溃）。
        /// 探针反正已经功成身退，直接不挂。
        /// </summary>
        private static bool Prepare() => !CargoWidening.IsActive;

        /// <summary>
        /// 加宽生效后永远关闭：本文件直接按 byte 读 <c>Cargo.inc</c>，
        /// 而它现在是 Int16；再说这个探针本来就是为了评估「另开一本账」那条路，
        /// 而最终选的是直接加宽，它的使命已经完成。
        /// </summary>
        private static bool Enabled => !CargoWidening.IsActive &&
                                       ProjectEdenPlugin.StationsConfig?.cargoLedgerProbe == true;

        private static Ledger Of(CargoContainer container)
        {
            if (ReferenceEquals(container, _lastContainer)) return _lastLedger;

            Ledger ledger = Ledgers.GetOrAdd(container, c => new Ledger(c.poolCapacity));

            _lastContainer = container;
            _lastLedger = ledger;

            return ledger;
        }

        // ── 生命周期钩子 ────────────────────────────────────

        /// <summary>新货入账。两个重载都要挂，漏一个就会出现「有货没账」。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CargoContainer), nameof(CargoContainer.AddCargo),
            typeof(short), typeof(byte), typeof(byte))]
        private static void AddCargo_Short(CargoContainer __instance, int __result) => Tag(__instance, __result);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CargoContainer), nameof(CargoContainer.AddCargo),
            typeof(short), typeof(byte), typeof(byte), typeof(Vector3), typeof(Quaternion))]
        private static void AddCargo_Full(CargoContainer __instance, int __result) => Tag(__instance, __result);

        private static void Tag(CargoContainer container, int cargoId)
        {
            if (!Enabled || container?.cargoPool == null || cargoId < 0) return;

            Ledger ledger = Of(container);

            ledger.EnsureSize(container.poolCapacity);

            if (cargoId < ledger.Tags.Length && cargoId < container.cargoPool.Length)
                ledger.Tags[cargoId] = container.cargoPool[cargoId].item;
        }

        /// <summary>
        /// 销账。<b>这一处是整个骨架里最要命的</b>：cargoId 会被回收复用，
        /// 不销账的话下一个占用这个槽位的货会继承前任的数据。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CargoContainer), nameof(CargoContainer.RemoveCargo))]
        private static void RemoveCargo(CargoContainer __instance, int index)
        {
            // 参数名必须叫 index —— 原版签名就是 RemoveCargo(int index)，
            // Harmony 按**参数名**注入，写成 cargoId 会挂不上而且不报错
            if (!Enabled || index < 0) return;

            Ledger ledger = Of(__instance);

            if (index < ledger.Tags.Length) ledger.Tags[index] = 0;
        }

        /// <summary>货池翻倍，账本跟着长。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CargoContainer), nameof(CargoContainer.Expand2x))]
        private static void Expand2x(CargoContainer __instance)
        {
            if (!Enabled) return;

            Of(__instance).EnsureSize(__instance.poolCapacity);
        }

        /// <summary>
        /// 读档：容量整个换掉，而且货是直接填进池子的（没走 AddCargo），
        /// 所以要按池子的实际内容重建整本账。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CargoContainer), nameof(CargoContainer.Import))]
        private static void Import(CargoContainer __instance)
        {
            if (!Enabled) return;

            var ledger = new Ledger(__instance.poolCapacity);

            Rebuild(__instance, ledger);

            Ledgers[__instance] = ledger;

            _lastContainer = null;
            _lastLedger = null;

            ProjectEdenPlugin.Log.LogInfo(
                $"[货物账本] 读档重建：容量 {__instance.poolCapacity}，游标 {__instance.cursor}，" +
                $"在途货堆 {CountLive(__instance)} 堆");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CargoContainer), nameof(CargoContainer.Free))]
        private static void Free(CargoContainer __instance)
        {
            Ledgers.TryRemove(__instance, out Ledger _);

            _lastContainer = null;
            _lastLedger = null;
        }

        private static void Rebuild(CargoContainer container, Ledger ledger)
        {
            Cargo[] pool = container.cargoPool;

            if (pool == null) return;

            int end = Bound(container, ledger.Tags.Length);

            for (var i = 0; i < end; i++)
                ledger.Tags[i] = pool[i].stack > 0 ? pool[i].item : 0;
        }

        /// <summary>有效槽位范围：游标、池长、账本长三者取最小，越界一次就是一堆脏数据。</summary>
        private static int Bound(CargoContainer container, int tagsLength)
        {
            int end = container.cursor;

            if (container.cargoPool != null && container.cargoPool.Length < end) end = container.cargoPool.Length;
            if (tagsLength < end) end = tagsLength;

            return end < 0 ? 0 : end;
        }

        private static int CountLive(CargoContainer container)
        {
            Cargo[] pool = container.cargoPool;

            if (pool == null) return 0;

            int end = Bound(container, int.MaxValue);
            var n = 0;

            for (var i = 0; i < end; i++)
                if (pool[i].stack > 0)
                    n++;

            return n;
        }

        // ── 定期自检 ────────────────────────────────────────

        /// <summary>
        /// 下次报告的时刻，用<b>实时时钟</b>而不是 gameTick。
        ///
        /// 踩过：原来用 <c>GameMain.gameTick</c> 记账，换一个存档之后 gameTick 跳回小值，
        /// 「当前 &lt; 下次」永远成立，报告就此哑掉——表现是「玩了半天日志里只有一行」，
        /// 很容易误判成「一切正常」。实时时钟单调递增，不受读档、暂停、倍速影响。
        /// </summary>
        private static float _nextReportAt;

        /// <summary>
        /// 整个会话的峰值，<b>只涨不降</b>。
        ///
        /// 每次报告是对「此刻」的快照，而集装满、喷过增产剂的货在带子上是流动的，
        /// 快照很容易正好错过。留一份会话峰值，只要出现过一次就记下来了。
        /// </summary>
        private static int _peakStack, _peakInc, _peakPerItem;

        /// <summary>
        /// 主线程每帧调一次，按配置的间隔做一次全量比对。
        ///
        /// 放在 <c>GameMain.Update</c> 而不是 <c>PlanetTransport.GameTick</c>：
        /// 后者是并行的，从那儿打日志会几十个线程一起打。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameMain), "Update")]
        private static void GameMain_Update()
        {
            if (!Enabled || !GameMain.isRunning || GameMain.isPaused) return;

            int seconds = ProjectEdenPlugin.StationsConfig?.cargoLedgerLogSeconds ?? 0;

            if (seconds <= 0) seconds = 10;

            float now = Time.realtimeSinceStartup;

            if (now < _nextReportAt) return;

            _nextReportAt = now + seconds;

            Report();
        }

        private static void Report()
        {
            var containers = 0;
            var live = 0;
            var missing = 0;    // 有货，账上没有或对不上
            var stale = 0;      // 没货，账上还留着（ID 回收没清干净）
            var undersized = 0; // 账本比货池短
            var maxStack = 0;
            var maxInc = 0;
            var maxPerItem = 0; // 单件点数，正常 ≤ 4

            foreach (System.Collections.Generic.KeyValuePair<CargoContainer, Ledger> pair in Ledgers)
            {
                CargoContainer container = pair.Key;
                Cargo[] pool = container?.cargoPool;

                if (pool == null) continue;

                containers++;

                int[] tags = pair.Value.Tags;

                if (tags.Length < container.cursor && tags.Length < pool.Length) undersized++;

                int end = Bound(container, tags.Length);

                for (var i = 0; i < end; i++)
                {
                    int stack = pool[i].stack;

                    if (stack > 0)
                    {
                        live++;

                        if (tags[i] != pool[i].item) missing++;

                        if (stack > maxStack) maxStack = stack;
                        if (pool[i].inc > maxInc) maxInc = pool[i].inc;

                        int perItem = pool[i].inc / stack;

                        if (perItem > maxPerItem) maxPerItem = perItem;
                    }
                    else if (tags[i] != 0)
                    {
                        stale++;
                    }
                }
            }

            if (maxStack > _peakStack) _peakStack = maxStack;
            if (maxInc > _peakInc) _peakInc = maxInc;
            if (maxPerItem > _peakPerItem) _peakPerItem = maxPerItem;

            bool ok = missing == 0 && stale == 0 && undersized == 0;

            // 没见过集装、也没见过增产剂，就说明这条产线根本没触发要验的那两条路径。
            // 明说出来，免得把「测了但没测到」当成「测过了没问题」
            string quality = _peakStack <= 1 && _peakInc == 0
                ? "　⚠ 样本无效：全程没出现集装货堆，也没出现增产剂，风险路径未被触发"
                : "";

            string line =
                $"[货物账本] {(ok ? "一致" : "有偏差")}　星球 {containers} 个／在途货堆 {live} 堆　" +
                $"对不上 {missing}／残留 {stale}／账本过短 {undersized}　" +
                $"此刻峰值 层数 {maxStack}／整堆点数 {maxInc}　" +
                $"会话峰值 层数 {_peakStack}／整堆点数 {_peakInc}／单件点数 {_peakPerItem}{quality}";

            if (ok) ProjectEdenPlugin.Log.LogInfo(line);
            else ProjectEdenPlugin.Log.LogWarning(line);
        }
    }
}
