using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 小型速采机：一台<b>产量被钉死</b>的采矿机。
    ///
    /// 它和大型采矿机的关系不是「小一号」，而是<b>另一种曲线</b>：
    /// 大型采矿机跟着「矿物利用」系列科技一路涨，早期弱、后期强；
    /// 这一台<b>从头到尾都是每分钟固定那么多</b>——开局就能用，而且永远不会更快。
    ///
    /// <b>「钉死」是它全部的实现难度。</b> 原版每 tick 的累加是
    /// <code>
    /// time += (int)(power × speedDamper × speed × miningSpeed × veinCount)
    /// </code>
    /// 一个 <c>period</c> 出一颗矿。其中 <c>miningSpeed</c> 是被科技放大过的、
    /// <c>veinCount</c> 是脚下压着几条矿脉——<b>两个都不是常数</b>。
    /// 想让产量和它们都无关，唯一的办法是每 tick 反解 <c>speed</c> 把它们除掉：
    /// <code>
    /// speed = 目标每 tick 产量 × period / (miningSpeed × veinCount)
    /// </code>
    /// 于是乘回去之后 <c>miningSpeed</c> 和 <c>veinCount</c> 正好约掉。
    ///
    /// <b>为什么不能只写 prefabDesc 里的 speed。</b> 那个数在建造时被抄进存档，
    /// 之后每 tick 还要乘 <c>miningSpeed</c>——科技一升产量就跟着涨，
    /// 而面板上的数看着还是对的。这正是本仓库第 3 号坑（界面和逻辑读的不是同一个源）。
    ///
    /// <b>它必须排在 <see cref="AdvancedMinerPatches"/> 之后。</b>
    /// 那一个也前置在同一个方法上，会按自己的规则改 <c>speed</c> / <c>miningRate</c> /
    /// <c>speedDamper</c>。两个前置的相对顺序不写就是不确定的，而症状是
    /// 「产量偶尔对、偶尔跟着科技涨」——所以这里显式取 <c>Priority.Last</c>，
    /// 最后写的那个说了算。
    /// </summary>
    [HarmonyPatch]
    internal static class MiniMinerPatches
    {
        /// <summary>一分钟 3600 tick。</summary>
        private const float TicksPerMinute = 3600f;

        /// <summary>
        /// 写进 <c>MinerComponent.speed</c> 的定值。这个字段的单位是百分之一，
        /// 所以 10000 在采矿面板上读作<b>开采速度 100%</b>——一台定速机器本来就该这么显示。
        /// 真正承担产量比例的是 <c>miningSpeed</c>，见下面为什么。
        /// </summary>
        private const int PanelSpeed = 10000;

        private static readonly Dictionary<int, MachineMinerEntry> ByProto =
            new Dictionary<int, MachineMinerEntry>();

        internal static bool Ready => ByProto.Count > 0;

        /// <summary>
        /// 建表。<b>三种状态都打</b>——「没配这种机器」和「这段代码没进 DLL」
        /// 在日志上本来长得一模一样。
        /// </summary>
        internal static void OnPostAddData()
        {
            ByProto.Clear();

            foreach (MachineRegistry.Machine m in MachineRegistry.Machines)
            {
                if (!m.IsMiner) continue;

                MachineMinerEntry cfg = m.Entry.miner;

                if (cfg == null || cfg.oresPerMinute <= 0)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"{m.Entry.displayName} 是 miner 型但没配 miner.oresPerMinute，" +
                        "产量不会被钉死——它会退化成一台跟着科技涨的普通采矿机。");

                    continue;
                }

                ByProto[m.ItemId] = cfg;
            }

            if (ByProto.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo("小型速采机：machines.json 里没有 miner 型的条目。");

                return;
            }

            foreach (KeyValuePair<int, MachineMinerEntry> pair in ByProto)
                ProjectEdenPlugin.Log.LogInfo(
                    $"小型速采机已就绪：物品 {pair.Key}，固定 {pair.Value.oresPerMinute} 矿/分钟" +
                    $"（≈ {pair.Value.oresPerMinute / TicksPerMinute:0.###} 矿/tick），" +
                    $"不吃采矿速度科技，矿脉{(pair.Value.consumeVeins ? "会" : "不会")}消耗。");
        }

        private static MachineMinerEntry Find(ref MinerComponent miner, PlanetFactory factory)
        {
            if (ByProto.Count == 0 || factory?.entityPool == null) return null;

            int entityId = miner.entityId;

            if (entityId <= 0 || entityId >= factory.entityPool.Length) return null;

            return ByProto.TryGetValue(factory.entityPool[entityId].protoId, out MachineMinerEntry cfg)
                ? cfg
                : null;
        }

        /// <summary>
        /// 把这一台的产量钉死。<b>Priority.Last</b>：AdvancedMinerPatches 也前置在这里，
        /// 它按自己的规则写过的 speed / miningRate / speedDamper 在这里被覆盖掉。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(MinerComponent), nameof(MinerComponent.InternalUpdate))]
        private static void MinerComponent_InternalUpdate_Prefix(ref MinerComponent __instance,
            PlanetFactory factory, float power, ref float miningRate, ref float miningSpeed)
        {
            MachineMinerEntry cfg = Find(ref __instance, factory);

            if (cfg == null) return;

            // 矿脉不消耗。0 是「这一 tick 一点储量都不扣」，不是「扣得少」。
            if (!cfg.consumeVeins) miningRate = 0f;

            // **节流关掉。** 原版按缓存/仓位占比把速度降到 2%，那是给「产量会溢出」
            // 设计的背压；这一台的产量是个常数，让它随仓位起伏就不叫「钉死」了。
            // 仓位满了自然会停——StationComponent.UpdateVeinCollection 第一句就是
            // 「本地供应量 ≥ max 就直接返回」，那一道闸在这之外，不受影响。
            __instance.speedDamper = 1f;

            int veins = __instance.veinCount > 0 ? __instance.veinCount : 1;

            // 每 tick 要累加多少 time 才刚好是目标产量
            float wantPerTick = cfg.oresPerMinute / TicksPerMinute * __instance.period;

            // **反解的是 miningSpeed（float），不是 speed（int）。**
            //
            // 两个都能把科技和矿脉数除掉，但 speed 是整数：把整个比例塞进它，
            // 矿脉压得越多、科技越高，除出来的数越小，截断误差就越大——
            // 实测量级是「10 条矿脉 + 满级科技」时低 10%。
            // 一台卖点是「产量钉死」的机器，产量随矿脉数浮动 10% 就不叫钉死了。
            //
            // 所以 speed 固定成 10000（这个字段的单位是百分之一，10000 = 100%，
            // 面板上的「开采速度」因此读作 100%，对一台定速机器来说这正是该显示的），
            // 由浮点的 miningSpeed 去承担那个比例。
            __instance.speed = PanelSpeed;

            float divisor = (float)PanelSpeed * veins;

            if (divisor <= 0f) return;

            // 乘回去正好抵消：
            //   time += power × 1 × PanelSpeed × miningSpeed × veins
            //         = power × 每 tick 目标 time
            // ——科技放大过的那个 miningSpeed 在这一行被整个替换掉了，
            // 这就是第 8 条要的「不享受采矿利用率的科技加成」。
            float techSpeed = miningSpeed;

            miningSpeed = wantPerTick / divisor;

            ReportOnce(ref __instance, cfg, techSpeed, veins, miningSpeed);
        }

        /// <summary>
        /// 功率钉死。
        ///
        /// <b>原版的采矿机功率是速度的平方</b>（<c>SetPCState</c> 里
        /// <c>speedDamper × speed² / 1e8</c>），而上面为了钉死产量把 speed 反解成了
        /// 一个随科技变化的数——照原版公式算，功率会跟着科技上下跳，
        /// 而且科技越高 speed 越小、功率反而越低。那讲不通，也不是配置里写的那个数。
        ///
        /// 所以这里直接写 <c>requiredEnergy</c>，绕开那条平方公式。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(MinerComponent), nameof(MinerComponent.SetPCState))]
        private static void MinerComponent_SetPCState_Postfix(ref MinerComponent __instance,
            PowerConsumerComponent[] pcPool)
        {
            if (ByProto.Count == 0 || pcPool == null) return;

            // SetPCState 拿不到 factory，所以按「这台机器的 speed 是我们写的那种」认不出来。
            // 改用 pcId 反查：功耗组件上记着 workEnergyPerTick，那是我们在 prefabDesc 里
            // 设好的值，直接用它当工作功率即可——省掉一次实体查表，也不会认错别的采矿机。
            int pcId = __instance.pcId;

            if (pcId <= 0 || pcId >= pcPool.Length) return;

            if (!PcOfMini.ContainsKey(pcId)) return;

            // **只换量纲，不替原版做「在不在干活」的判断。** 原版刚刚算完的
            // requiredEnergy 是 0 还是正数，已经表达了它这一 tick 有没有在采矿；
            // 照抄那个结论、只把数值换成配置里的功率，比自己再判一遍矿脉、仓位、
            // 电量要稳得多——那几条判据将来一变，自己判的那份就会悄悄和原版分叉。
            pcPool[pcId].requiredEnergy = pcPool[pcId].requiredEnergy > 0
                ? (int)pcPool[pcId].workEnergyPerTick
                : (int)pcPool[pcId].idleEnergyPerTick;
        }

        /// <summary>
        /// 哪些功耗组件属于小型速采机。
        ///
        /// <b>必须是并发容器。</b> 写它的是建造（主线程），读它的是
        /// <c>SetPCState</c>——那条路跑在 <c>_miner_parallel</c> 上，约 31 个工作线程。
        /// 普通 <c>HashSet</c> 在这种读写并发下会在几分钟内损坏（本仓库第 4 号坑，
        /// <c>MegaVirtualLogisticsPatches</c> 在线上炸过一次）。
        /// </summary>
        private static readonly ConcurrentDictionary<int, byte> PcOfMini =
            new ConcurrentDictionary<int, byte>();

        /// <summary>
        /// 建造时把这台机器的 pcId 记下来，<c>SetPCState</c> 才认得出它。
        ///
        /// <b>为什么不在 SetPCState 里现查。</b> 那个方法只拿得到 <c>MinerComponent</c>
        /// 和功耗池，没有 <c>PlanetFactory</c>，查不到 protoId；而它每 tick 每台都跑，
        /// 不适合再兜一圈。建造时记一次是最省的。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.CreateEntityLogicComponents))]
        private static void PlanetFactory_CreateEntityLogicComponents(PlanetFactory __instance, int entityId)
        {
            if (ByProto.Count == 0 || __instance?.entityPool == null) return;
            if (entityId <= 0 || entityId >= __instance.entityPool.Length) return;

            ref EntityData entity = ref __instance.entityPool[entityId];

            if (entity.minerId <= 0 || entity.powerConId <= 0) return;
            if (!ByProto.ContainsKey(entity.protoId)) return;

            PcOfMini.TryAdd(entity.powerConId, 0);
        }

        private static int _reported;

        private static void ReportOnce(ref MinerComponent miner, MachineMinerEntry cfg,
            float techSpeed, int veins, float solved)
        {
            if (Interlocked.Exchange(ref _reported, 1) != 0) return;

            // 一次性日志的抢占要在拼字符串**之前**——这个方法跑在 _miner_parallel 上，
            // 不抢的话每个线程都会拼一遍那串字（本仓库记过的 tick 路径禁分配）。
            ProjectEdenPlugin.Log.LogInfo(
                $"小型速采机第一次结算：period={miner.period}，脚下矿脉 {veins} 条，" +
                $"科技给的采矿速率 {techSpeed:0.###}（**已被整个替换掉**），" +
                $"speed 固定 {miner.speed}（面板 100%），反解出 miningSpeed={solved:0.#####}，" +
                $"折合 {cfg.oresPerMinute} 矿/分钟。科技再升、矿脉再多，这个数都不变——这是它的设计。");
        }
    }
}
