using System;
using System.Collections.Concurrent;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches.Station
{
    /// <summary>
    /// 读档时把已建好的物流站的**运输船泊位数组**补到 prefab 现在写的那个数——
    /// 原版缺的那个 <c>PatchShipArray</c>。
    ///
    /// <b>这是陷阱 1 的标准形状，而且是原版自己的不对称。</b>
    /// <c>StationComponent.Init</c> 按 <c>PrefabDesc.stationMaxShipCount</c> 开六个数组
    /// （@016D / @017F / @0191 / @01A3 / @022F / @0241），而读档那一路**不重算**：
    /// <c>StationComponent.Import</c> @021B 先 <c>ReadInt32()</c> 再按那个数 <c>newarr</c>，
    /// 长度完全来自存档。
    ///
    /// 无人机那一侧原版是补了的——<c>PlanetTransport.Import</c> @00C3 现读 prefab 的
    /// <c>stationMaxDroneCount</c>、@00D4 调 <c>StationComponent.PatchDroneArray</c>——
    /// 而**全程序集只有这一个 <c>Patch*Array</c>**，运输船没有对应的。所以不补的话，
    /// 把上限从 64 调到 256 对**已经建好的站一点效果都没有**，只有新建的才拿得到，
    /// 而这正是玩家会报「我改了配置，船还是那么多」的地方。
    ///
    /// <b>只涨不缩，理由不是习惯。</b> 本仓库的判据是「枚举写入者」：泊位数组长度没有任何
    /// UI 写得到（<c>UIStationWindow.OnShipIconClick</c> 写的是 <c>idleShipCount</c>，
    /// 不是数组长度），按 <c>energyMax</c> 那条本该双向对齐。但缩短会把正停在高位泊位上的
    /// 在飞船连同它的 <c>shipIndex</c> 一起截掉——**那是丢船，不是改设置**。所以缩的那一侧
    /// 只报一行，不动手。
    /// </summary>
    [HarmonyPatch]
    internal static class StationShipExpandPatches
    {
        /// <summary>每颗星球只报一次，避免每次读档刷屏；按星球分桶是「打印一次指每种一次」那条。</summary>
        private static readonly ConcurrentDictionary<int, int> Reported = new ConcurrentDictionary<int, int>();

        /// <summary>原版停泊环的半径，抄自 <c>StationComponent.Import</c> @04CC 的字面量。</summary>
        private const float DiskRadius = 11.5f;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.Import))]
        private static void PlanetTransport_Import(PlanetTransport __instance)
        {
            StationComponent[] pool = __instance?.stationPool;

            if (pool == null) return;

            var grown = 0;
            var slotsAdded = 0;
            var shrunk = 0;

            int cursor = __instance.stationCursor < pool.Length ? __instance.stationCursor : pool.Length;

            for (var i = 1; i < cursor; i++)
            {
                StationComponent station = pool[i];

                if (station == null || station.id != i) continue;

                // 判据是「有没有泊位数组」，不是物品号——行星内物流站这个数组长度天生是 0。
                if (station.workShipDatas == null || station.workShipDatas.Length == 0) continue;

                int protoId = __instance.factory.entityPool[station.entityId].protoId;
                int want = LDB.items.Select(protoId)?.prefabDesc?.stationMaxShipCount ?? 0;
                int have = station.workShipDatas.Length;

                if (want <= 0 || want == have) continue;

                if (want < have)
                {
                    shrunk++;

                    continue;
                }

                Grow(station, want);

                grown++;
                slotsAdded += want - have;
            }

            if (grown > 0) StationShipBank.CountGrownSlots(slotsAdded);

            int planetId = __instance.planet?.id ?? 0;

            // **「一行都没打」不能等于「这条路没跑过」。** 上一版只在 grown/shrunk > 0 时
            // 才出声，于是日志里没有这一行时分不开两件事：这颗星球上没有站需要补，
            // 还是这条补丁压根没被调到。本仓库为这条付过账（StackedRenderPatches 的
            // 「离开星球再回来」那次）——**针对一个你自己触发不了的场景做修复时，
            // 要记录那个场景本身，而不只是记录修复**。
            if (grown == 0 && shrunk == 0)
            {
                if (Reported.TryAdd(planetId, 1))
                    ProjectEdenPlugin.Log.LogInfo(
                        $"运输船泊位补齐（星球 {planetId}）：扫过了，没有站需要补"
                        + "（泊位数组已经和 prefab 一致，或者这颗星球上没有星际物流站）。");

                return;
            }

            if (Reported.TryAdd(planetId, 1))
                ProjectEdenPlugin.Log.LogInfo(
                    $"运输船泊位补齐（星球 {planetId}）：{grown} 座站的泊位数组按 prefab 补了上去，"
                    + $"共加 {slotsAdded} 个泊位"
                    + (shrunk > 0
                        ? $"；另有 {shrunk} 座站的 prefab 比存档里还小，**没有缩**——"
                          + "缩短会把停在高位泊位上的在飞船一起截掉，那是丢船不是改设置"
                        : "")
                    + "。原版只给无人机补了 PatchDroneArray，运输船没有对应的，所以这一步是本 mod 加的。");
        }

        /// <summary>
        /// 六个数组一起长。<b>漏一个不会报错</b>——<c>ShipRenderersOnTick</c> 会按
        /// <c>workShipDatas</c> 的长度去索引 <c>shipDiskPos</c>，长度对不上就是一次越界，
        /// 而它在渲染路径上。
        /// </summary>
        private static void Grow(StationComponent station, int slots)
        {
            station.workShipDatas = Resize(station.workShipDatas, slots);
            station.workShipOrders = Resize(station.workShipOrders, slots);
            station.shipRenderers = Resize(station.shipRenderers, slots);
            station.shipUIRenderers = Resize(station.shipUIRenderers, slots);

            RebuildDisk(station, slots);

            // 泊位数变了，旁挂位图得按新长度重开并重新派生
            StationShipBank.Invalidate(station);
        }

        private static T[] Resize<T>(T[] old, int slots)
        {
            var next = new T[slots];

            if (old != null) Array.Copy(old, next, old.Length < slots ? old.Length : slots);

            return next;
        }

        /// <summary>
        /// 停泊环，逐字抄 <c>StationComponent.Import</c> @0475–@0541：先按泊位数均分一圈，
        /// 再左乘停泊点的朝向、加上停泊点的位置。
        ///
        /// <b>抄而不是自己编，是因为这两段决定船停在哪。</b> 编错了不会报错，
        /// 只会让船停在建筑外面——而那正是「看起来像渲染 bug、其实是数据」的那一类。
        ///
        /// 注意环的半径是固定的 11.5：<b>泊位越多，船在环上贴得越紧</b>。
        /// 64 艘时已经不宽裕，256 艘会明显重叠。这是外观代价，不是故障。
        /// </summary>
        private static void RebuildDisk(StationComponent station, int slots)
        {
            var pos = new Vector3[slots];
            var rot = new Quaternion[slots];

            if (station.isStellar)
            {
                for (var i = 0; i < slots; i++)
                {
                    rot[i] = Quaternion.Euler(0f, 360f / slots * i, 0f);
                    pos[i] = rot[i] * new Vector3(0f, 0f, DiskRadius);
                }

                for (var i = 0; i < slots; i++)
                {
                    rot[i] = station.shipDockRot * rot[i];
                    pos[i] = station.shipDockPos + station.shipDockRot * pos[i];
                }
            }

            station.shipDiskPos = pos;
            station.shipDiskRot = rot;
        }
    }
}
