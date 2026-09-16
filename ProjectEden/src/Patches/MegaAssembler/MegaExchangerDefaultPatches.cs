using System.Collections.Generic;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 新建的巨型能量枢纽**默认停在充电档**，而不是原版的待机档。
    ///
    /// <b>这不是便利功能，是修一个「每一步都成功、功能却用不了」的坑。</b>
    /// <c>PlanetFactory.EntityFastFillIn</c> 的枢纽分支（IL 12E0~1403）是**按模式分支**的：
    /// 先读 <c>state</c>，充电档才认 <c>emptyId</c>、放电档才认 <c>fullId</c>，
    /// <b>待机档两样都不收</b>。而枢纽新建出来就是待机。
    ///
    /// 于是玩家的体验是：建筑造好了、皮带接上了、柜子在手里，
    /// <b>怎么塞都塞不进去，而且没有任何提示</b>——实测报上来的就是这一句
    /// 「无法放入电浆储能柜」。原版的能量枢纽也是这个默认，只是玩家早就熟悉它。
    ///
    /// 默认改成充电，是因为这条线的第一步一定是「把空柜充满」：
    /// 满柜只能从这里来，而空柜是造出来的。放电是第二步，那时玩家已经打开过面板了。
    ///
    /// <b>只动本 mod 自己的枢纽。</b> 判据是 prefab 的 <c>exchangeEnergyPerTick</c>
    /// 是否等于配置里那个值——不是按物品号，因为号会随撞车顺延。
    /// </summary>
    [HarmonyPatch]
    internal static class MegaExchangerDefaultPatches
    {
        /// <summary>充电档。原版三档是 −1 放电 / 0 待机 / 1 充电，和面板三个按钮一一对应。</summary>
        private const float Charging = 1f;

        private static int _done;
        private static int _switched;

        /// <summary>一台巨型枢纽能服务的一对空/满蓄电器。</summary>
        internal class Pair
        {
            internal long EnergyPerTick;
            internal int EmptyId;
            internal int FullId;
            internal string Key;
        }

        internal static readonly List<Pair> Pairs = new List<Pair>();

        internal static void RegisterPair(long energyPerTick, int emptyId, int fullId, string key)
        {
            foreach (Pair p in Pairs)
                if (p.EnergyPerTick == energyPerTick && p.EmptyId == emptyId)
                    return;

            Pairs.Add(new Pair
            {
                EnergyPerTick = energyPerTick, EmptyId = emptyId, FullId = fullId, Key = key,
            });
        }

        /// <summary>
        /// 拿着另一档的柜子去点柜位图标 → 这台枢纽就改服务那一档。
        ///
        /// <b>为什么挂在这里，而不是新做一个界面。</b> 原版这个点击处理本来就读
        /// <c>player.inhandItemId</c>（IL 003F~0045），也就是说「手上拿什么」已经是它的输入了。
        /// 于是玩家做最自然的那个动作——拿着过载柜去点柜位——正好可以当切档，
        /// 而且图标当场就变，反馈是天然的，不用再教。
        ///
        /// <b>只在两边都空着时才切。</b> 柜子还在里头就改 <c>emptyId</c>，
        /// 那些柜子会变成再也取不出来的幽灵——计数还在，而 id 已经指向别的物品了。
        ///
        /// 改动**不需要自己存档**：<c>emptyId</c>/<c>fullId</c> 是逐组件字段，
        /// 原版的 <c>Export</c>/<c>Import</c> 本来就写它们。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIPowerExchangerWindow),
            nameof(UIPowerExchangerWindow.OnEmptyOrFullUIButtonClick))]
        private static void OnEmptyOrFullUIButtonClick_Prefix(UIPowerExchangerWindow __instance)
        {
            int id = __instance?.exchangerId ?? 0;
            PowerExchangerComponent[] pool = __instance?.powerSystem?.excPool;

            if (id <= 0 || pool == null || id >= pool.Length) return;

            int held = __instance.player?.inhandItemId ?? 0;

            if (held <= 0) return;

            long rate = pool[id].energyPerTick;

            // 已经是当前这一对了，交给原版处理
            if (pool[id].emptyId == held || pool[id].fullId == held) return;

            // 里头还有货就不能切：计数留着而 id 变了，那些柜子就再也取不出来
            if (pool[id].emptyCount != 0 || pool[id].fullCount != 0) return;

            foreach (Pair p in Pairs)
            {
                if (p.EnergyPerTick != rate) continue;
                if (p.EmptyId != held && p.FullId != held) continue;

                pool[id].emptyId = p.EmptyId;
                pool[id].fullId = p.FullId;

                if (_switched++ < 4)
                    ProjectEdenPlugin.Log.LogInfo(
                        $"巨型能量枢纽 #{id}：按手上那件货改服务「{p.Key}」"
                        + $"（空 {p.EmptyId} / 满 {p.FullId}）。"
                        + "emptyId/fullId 是逐组件字段且进存档，所以这个选择由原版自己存下来");

                return;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PowerSystem), nameof(PowerSystem.NewExchangerComponent))]
        private static void NewExchangerComponent_Postfix(
            PowerSystem __instance, PrefabDesc desc, int __result)
        {
            if (__result <= 0 || desc == null || !IsOurs(desc)) return;

            PowerExchangerComponent[] pool = __instance?.excPool;

            if (pool == null || __result >= pool.Length) return;

            pool[__result].targetState = Charging;
            pool[__result].state = Charging;

            // 一次就够，但要有——「默认档改了」和「这补丁没生效」在玩家那里长得一样，
            // 而后者的症状正是塞不进柜子
            if (_done++ > 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                "巨型能量枢纽：新建的默认停在**充电档**（原版默认待机）。"
                + "待机档 EntityFastFillIn 两样都不收，玩家会以为柜子塞不进去");
        }

        /// <summary>
        /// 是不是本 mod 的巨型枢纽。按 <c>exchangeEnergyPerTick</c> 认，不按物品号——
        /// 物品号撞车时会顺延，而这个功率是配置里写死的一个很特别的数。
        /// </summary>
        private static bool IsOurs(PrefabDesc desc)
        {
            MegaBuildingEntry[] all = MegaBuildingRegistry.Config?.buildings;

            if (all == null || !desc.isPowerExchanger) return false;

            foreach (MegaBuildingEntry entry in all)
            {
                MegaExchangerEntry exc = entry?.exchanger;

                if (exc != null && exc.energyPerTick > 0L && desc.exchangeEnergyPerTick == exc.energyPerTick)
                    return true;
            }

            return false;
        }
    }
}
