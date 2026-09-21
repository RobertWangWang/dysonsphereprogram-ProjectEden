#pragma warning disable 649 // BeltsConfig 的字段由 JSON 反序列化赋值

using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 传送带速度。配置在 <c>belts.json</c>。
    ///
    /// <b>这是这个 mod 里最干净的一类改动</b>：<c>PrefabDesc.beltSpeed</c>（Int32）直接赋给
    /// <c>BeltComponent.speed</c>，全程没有夹取，不碰结构体、不碰存档格式、不需要 preloader。
    ///
    /// <b>速度怎么换算。</b> 每堆货在传送带缓冲区里占 <c>CargoPath.kCargoLength = 10</c> 格，
    /// 60 tick/秒，所以 <b>物品/秒 = speed × 6</b>。拿原版验证：Mk.I speed 1 → 6/秒、
    /// Mk.II speed 2 → 12/秒、Mk.III speed 5 → 30/秒，三个全中。
    ///
    /// <b>上限：实测出来的，比看上去低得多。</b> 原版有个常量
    /// <c>CargoPath.kMaxCargoFlowSpeedPerSecond = 120</c>（对应 speed 20），
    /// <b>没有任何代码读它——它压根没被实现</b>。曾经照着它把极速带设成 20，实测
    /// <c>CargoPath.Update</c> 抛 <c>IndexOutOfRangeException</c>（IL_03B8）：
    /// 那里有个「从当前位置往回扫 <c>速度</c> 格找空位」的循环，
    /// <b>对 buffer 下标没有下界保护</b>，速度一大就在短路径的起点扫穿数组头。
    /// 原版 speed 5 只往回扫 5 格，所以永远不触发。
    ///
    /// 目前给到 8。<c>kCargoLength = 10</c> 是最可能的真实边界，但<b>没有验证过</b>——
    /// 要往上只能一档一档试。
    ///
    /// <b>一个虚惊。</b> <c>PilerComponent</c> 拿速度当数组下标
    /// （<c>cacheCdTickArray</c> 只有 3 个元素），看着像会越界；但取值之前有
    /// <c>if (speed > 2) speed = 3</c> 的夹取——原版本来就预料到速度会超过 3。
    /// 喷涂器读速度只做乘法，也不查表。所以提速不会打崩这两个建筑。
    /// </summary>
    [HarmonyPatch]
    internal static class BeltSpeedPatches
    {
        private static BeltsConfig Config => ProjectEdenPlugin.BeltsConfig;

        /// <summary>每 tick 走 1 格 × 60 tick ÷ 每堆货占 10 格 = 6 物品/秒</summary>
        private const int ItemsPerSecondPerSpeed = 6;

        /// <summary>物品 ID → 目标速度。给读档时的老带子修正用。</summary>
        private static readonly Dictionary<int, int> Wanted = new Dictionary<int, int>();

        /// <summary>
        /// 改 prefabDesc，只对<b>之后新建</b>的传送带生效。
        /// 已经建好的走 <see cref="GameData_Import"/> 那条。
        /// </summary>
        internal static void OnPostAddData()
        {
            Wanted.Clear();

            if (Config == null || !Config.enabled || Config.belts == null) return;

            foreach (BeltEntry entry in Config.belts)
            {
                if (entry == null || entry.speed <= 0) continue;

                ItemProto item = LDB.items.Select(entry.itemId);
                ModelProto model = item == null ? null : LDB.models.Select(item.ModelIndex);

                if (model?.prefabDesc == null)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"传送带提速：找不到物品 {entry.itemId}（{entry.name}）的模型，跳过");

                    continue;
                }

                int before = model.prefabDesc.beltSpeed;

                if (before <= 0)
                {
                    ProjectEdenPlugin.Log.LogWarning(
                        $"{item.name} 的 prefabDesc.beltSpeed 是 {before}，不像传送带，跳过");

                    continue;
                }

                model.prefabDesc.beltSpeed = entry.speed;
                Wanted[entry.itemId] = entry.speed;

                ProjectEdenPlugin.Log.LogInfo(
                    $"{item.name}：速度 {before} → {entry.speed}" +
                    $"（{before * ItemsPerSecondPerSpeed} → {entry.speed * ItemsPerSecondPerSpeed} 物品/秒）");
            }
        }

        /// <summary>
        /// 读档时把<b>已经建好</b>的传送带改过来。
        ///
        /// <b>速度是烤进存档的</b>：<c>BeltComponent.speed</c> 和 <c>CargoPath.chunks</c>
        /// 都持久化，所以光改 prefabDesc 对老带子无效（CLAUDE.md 陷阱 1）。
        /// 而且<b>真正决定货物移动的是 chunks 里那个值，不是 <c>BeltComponent.speed</c></b>
        /// ——只改后者的表现是「面板显示变快了但货没变快」。
        ///
        /// <b>不能直接调 <c>CargoTraffic.UpgradeBeltComponent</c>——会空引用崩溃。</b>
        /// 那个方法在同一个函数里<b>前面判了 <c>planet.physics</c> 的空、五条指令之后又直接解引用</b>
        /// （<c>planet.physics.isPlanetPhysicsColliderDirty = true</c>，IL_0045）。原版永远不炸，
        /// 是因为它只在「玩家用升级工具点某条带子」时调用，那颗星球必然加载着；
        /// 而读档时要遍历<b>存档里所有星球</b>，没加载的星球 <c>physics</c> 就是 null。
        ///
        /// 所以这里只做<b>数据部分</b>——把 UpgradeBeltComponent 里与渲染/碰撞无关的两件事
        /// 抄出来。反正我们没有换带子的等级（protoId 没变），渲染器和碰撞体本来就不用动；
        /// 带面滚动的视觉速度会在下次进入该星球重建渲染批次时自然跟上。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
        private static void GameData_Import(GameData __instance)
        {
            if (Wanted.Count == 0 || __instance?.factories == null) return;

            var watch = Stopwatch.StartNew();
            var changed = 0;
            var planets = 0;

            foreach (PlanetFactory factory in __instance.factories)
            {
                CargoTraffic traffic = factory?.cargoTraffic;

                if (traffic?.beltPool == null || factory.entityPool == null) continue;

                var here = 0;

                for (var i = 1; i < traffic.beltCursor; i++)
                {
                    ref BeltComponent belt = ref traffic.beltPool[i];

                    if (belt.id != i) continue;

                    int entityId = belt.entityId;

                    if (entityId <= 0 || entityId >= factory.entityPool.Length) continue;

                    int protoId = factory.entityPool[entityId].protoId;

                    if (!Wanted.TryGetValue(protoId, out int speed) || belt.speed == speed) continue;

                    if (Retune(traffic, i, speed)) here++;
                }

                if (here > 0)
                {
                    planets++;
                    changed += here;
                }
            }

            watch.Stop();

            if (changed > 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"传送带提速：已改写 {planets} 颗星球上的 {changed} 段旧传送带，耗时 {watch.ElapsedMilliseconds} ms");
        }

        /// <summary>
        /// 把一段已建成传送带的速度改掉，<b>只动数据，不碰渲染和碰撞</b>。
        ///
        /// 这是从原版 <c>CargoTraffic.UpgradeBeltComponent</c> 里抄出来的必要部分：
        /// <list type="number">
        /// <item><c>BeltComponent.speed</c>——面板显示和分拣器/集装机读的是它</item>
        /// <item><c>CargoPath.InsertChunk</c>——<b>货物真正按这个走</b>。路径按分段存速度，
        /// 漏了这一步就是「数字变了货没变快」</item>
        /// <item>环形路径的接缝要 <c>SyncBuckleSpeed</c></item>
        /// </list>
        /// 分段长度的取法也照抄原版：<b>路径里最后一段</b>要一直铺到路径末尾
        /// （<c>pathLength - segIndex</c>），否则末尾会留一截没改到的旧速度。
        /// </summary>
        private static bool Retune(CargoTraffic traffic, int beltId, int speed)
        {
            ref BeltComponent belt = ref traffic.beltPool[beltId];

            belt.speed = speed;

            CargoPath path = traffic.GetCargoPath(belt.segPathId);

            if (path?.belts == null) return true;

            bool isLast = path.belts.Count > 0 && path.belts[path.belts.Count - 1] == beltId;

            int length = isLast ? path.pathLength - belt.segIndex : belt.segLength;

            if (length > 0) path.InsertChunk(belt.segIndex, length, speed);

            if (path.closed && belt.segIndex == 0) path.SyncBuckleSpeed();

            return true;
        }
    }

    /// <summary>belts.json 的结构。</summary>
    [Serializable]
    internal class BeltsConfig
    {
        public bool enabled;

        public BeltEntry[] belts;

        /// <summary>
        /// 传送带流量探针，默认关。见 <see cref="BeltThroughputProbe"/>：
        /// 它每隔几秒报一次脚下这颗星球最忙的几条带子，把「带子没提速」「带子没喂满」
        /// 「集装没生效」「喂料口本来就喂不满」这四个长得一样的成因分开。
        /// </summary>
        public bool throughputProbe;

        /// <summary>探针的报告间隔（秒）。小于 2 视为 10。</summary>
        public int probeSeconds;
    }

    /// <summary>一档传送带。</summary>
    [Serializable]
    internal class BeltEntry
    {
        /// <summary>原版物品 ID。2001 低速 / 2002 高速 / 2003 极速</summary>
        public int itemId;

        /// <summary>只是给日志和配置可读性用的，不参与匹配</summary>
        public string name;

        /// <summary>
        /// <c>PrefabDesc.beltSpeed</c>。<b>物品/秒 = speed × 6</b>。
        /// 原版 1 / 2 / 5；20 对应 120 物品/秒，也就是原版常量
        /// <c>kMaxCargoFlowSpeedPerSecond</c> 写下的那个数。
        /// </summary>
        public int speed;
    }
}
