using System;
using System.Collections.Generic;
using HarmonyLib;
using ProjectEden.Utils;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 共位重叠的同种建筑<b>只画配置的那么几台</b>，其余的照常运转但不进渲染器。
    ///
    /// <b>动机是量出来的。</b> 模型普查在玩家那颗星球上报出 <c>模型 699 × 398</c>
    /// ——398 台小型速采机叠在同一处，而 36 种正在绘制的模型里没有第二个跟着台数涨的。
    /// 这台机器<b>产量钉死、本来就是靠叠数量出力</b>的设计，加上
    /// <c>MinerBuildRulePatches</c> 允许重叠建造，所以「几百台共位」是这个 mod 自己
    /// 造出来的常态，而原版的建造间距规则从来不让它发生。
    ///
    /// 共位副本在画面上是纯粹的浪费：**画 398 份和画 1 份看起来本该一样**，
    /// 而实际上不一样——半透明层会逐层混合、加法层会逐份累加，于是叠得越多越白。
    /// 逐个属性去压每一份的贡献治不了本：压到多小，台数一多都会重新累回来。
    ///
    /// <b>做法：让原版照常跑完，再把多余那些的模型摘掉。</b> 不走「前缀返回 false
    /// 跳过整个方法」那条路——<c>CreateEntityDisplayComponents</c> 除了加模型还建
    /// 小地图块、还给分拣器算姿态，整个跳过会连带弄丢那些。后置摘除只动模型这一件事。
    ///
    /// <b>安全性是量过的，而且原版自己就依赖这个形状。</b>
    /// <c>RemoveEntityWithComponents</c> IL 07DB 处是
    /// <c>ldfld EntityData::modelId ; brfalse</c>——<b>modelId 为 0 时整段
    /// RemoveModel 被跳过</b>；而 <c>CreateEntityDisplayComponents</c> 开头本来就有
    /// 两处「模型原型或 prefabDesc 为空就直接 ret」，那两条路留下的正是 modelId = 0
    /// 的实体。所以「这个实体不进渲染器」是原版原生支持的状态，不是我们硬造的。
    ///
    /// <b>逻辑一个字不动</b>：采矿、耗电、物流、点击、碰撞、小地图全部照旧，
    /// 变的只是 GPU 那一侧画几份。
    /// </summary>
    [HarmonyPatch]
    internal static class StackedRenderPatches
    {
        private static AdvancedMinerConfig Config => ProjectEdenPlugin.MinerConfig;

        /// <summary>
        /// 一格里的登记：画出来的那几台，和被藏起来的那些。
        ///
        /// <b><see cref="Hidden"/> 是 HashSet 而不是 List，这是性能而不是风格。</b>
        /// 这一摞就是玩家叠在同一点的全部建筑，而 <c>AfterCreateDisplay</c> 每建一台、
        /// 以及<b>落星球时每一个实体</b>都要问一次「这台在不在藏着的里面」——
        /// <c>List.Contains</c> 是线性扫描，叠 N 台就是 O(N²)。
        /// <see cref="Drawn"/> 留 List：它的长度被 <c>stackedRenderLimit</c> 卡着（默认 1），
        /// 而 <c>Promote</c> 要的是「顶一台上来」，顺序在那里有意义。
        /// </summary>
        private sealed class Cell
        {
            internal readonly List<int> Drawn = new List<int>();
            internal readonly HashSet<int> Hidden = new HashSet<int>();

            /// <summary>上一次全表清理时 <see cref="Hidden"/> 有多大——摊销清理用，见 <c>Prune</c>。</summary>
            internal int PrunedAt;
        }

        private readonly struct CellKey : IEquatable<CellKey>
        {
            private readonly int _proto;
            private readonly int _x;
            private readonly int _y;
            private readonly int _z;

            internal CellKey(int proto, int x, int y, int z)
            {
                _proto = proto;
                _x = x;
                _y = y;
                _z = z;
            }

            public bool Equals(CellKey other) =>
                _proto == other._proto && _x == other._x && _y == other._y && _z == other._z;

            public override bool Equals(object obj) => obj is CellKey other && Equals(other);

            public override int GetHashCode()
            {
                // 结构体键，不装箱、不碰撞——用字符串拼 key 会在读档时给几百个实体各分配一次
                int h = _proto;

                h = h * 397 ^ _x;
                h = h * 397 ^ _y;
                h = h * 397 ^ _z;

                return h;
            }
        }

        /// <summary>逐星球的登记表。读档走的是加载线程，所以所有访问都上锁。</summary>
        private static readonly Dictionary<int, Dictionary<CellKey, Cell>> _byPlanet =
            new Dictionary<int, Dictionary<CellKey, Cell>>();

        private static readonly object _gate = new object();

        private static int _hiddenTotal;

        private static int _promoted;

        /// <summary>重返星球后重新藏起来的台数——和首次藏起来分开记，因为它们是两条路</summary>
        private static int _rehidden;

        private static bool _rehideReported;

        private static bool _reported;

        private static bool Enabled => Config?.stackedRenderLimit != null && Config.stackedRenderLimit.Value > 0;

        private static float Radius => Config != null && Config.stackedRenderRadius > 0f
            ? Config.stackedRenderRadius
            : 2f;

        private static CellKey KeyOf(ref EntityData entity)
        {
            float r = Radius;
            Vector3 p = entity.pos;

            return new CellKey(entity.protoId,
                               Mathf.RoundToInt(p.x / r),
                               Mathf.RoundToInt(p.y / r),
                               Mathf.RoundToInt(p.z / r));
        }

        private static Dictionary<CellKey, Cell> PlanetTable(PlanetFactory factory)
        {
            int id = factory?.planetId ?? 0;

            if (!_byPlanet.TryGetValue(id, out Dictionary<CellKey, Cell> table))
            {
                table = new Dictionary<CellKey, Cell>();
                _byPlanet[id] = table;
            }

            return table;
        }

        /// <summary>
        /// 后置：这一台如果是本格里多余的那些，把它的模型摘掉。
        ///
        /// 参数名 <c>entityId</c> 已按 IL 核对——Harmony 按名字注入，名字不对会从
        /// <c>PatchAll</c> 抛出去，而且它之后的补丁类会被整批跳过，症状表现成某个
        /// 毫不相干的功能悄悄消失。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.CreateEntityDisplayComponents))]
        internal static void AfterCreateDisplay(PlanetFactory __instance, int entityId)
        {
            long began = Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            try
            {
                AfterCreateDisplayCore(__instance, entityId);
            }
            finally
            {
                if (began != 0L) Measure(System.Diagnostics.Stopwatch.GetTimestamp() - began);
            }
        }

        // ── 自测：这个挂点到底花了多少时间 ────────────────────

        private static long _probeTicks;
        private static long _probeCalls;
        private static long _probeMaxTicks;
        private static long _probeWindowStamp;
        private static DateTime _probeWindowUtc;
        private static int _probeReports;

        /// <summary>
        /// 累计这个挂点的耗时，每 10 秒报一行。
        ///
        /// <b>为什么值得常驻：玩家报的「叠建之后很卡」，日志里一个数都没有。</b>
        /// 「是我们这个后置在 O(N) 地扫登记表」和「是原版画这么多东西本来就慢」
        /// 表现完全一样，而处置相反。有了这一行，下一局就能直接把我们这一侧
        /// 证明或者洗清，不用再猜一轮。
        ///
        /// <b>频率要用墙钟校准，不能信 <c>Stopwatch.Frequency</c>。</b> 1.10.0 那一轮
        /// 第一版分段计时直接除以它，报出 240 ms/帧（真实逻辑帧才 16 ms），差了 12 倍。
        /// 自报的数在核对之前只是主张。
        ///
        /// 这里挂的不是 tick 路径——只有建造和落星球才会创建实体，所以每次两个时间戳、
        /// 十秒一个字符串是可以接受的。
        ///
        /// <b>墙钟用 <see cref="DateTime.UtcNow"/>，不用 <c>Time.realtimeSinceStartup</c>。</b>
        /// 这个类自己的登记表注释就写着「读档走的是加载线程」——而 Unity 的 API
        /// 在非主线程上调用会直接抛，那就变成落星球必崩。
        /// </summary>
        private static void Measure(long ticks)
        {
            string line = null;

            lock (_gate)
            {
                _probeTicks += ticks;
                _probeCalls++;

                if (ticks > _probeMaxTicks) _probeMaxTicks = ticks;

                DateTime utcNow = DateTime.UtcNow;
                long stampNow = System.Diagnostics.Stopwatch.GetTimestamp();

                if (_probeWindowStamp == 0L)
                {
                    _probeWindowStamp = stampNow;
                    _probeWindowUtc = utcNow;

                    return;
                }

                double wall = (utcNow - _probeWindowUtc).TotalSeconds;

                if (wall < 10.0) return;

                // 每局只报有限几次：它是排查用的，不该长期占日志
                if (_probeReports < 6)
                {
                    _probeReports++;

                    // **实测频率，不信 Stopwatch.Frequency。** 1.10.0 那一轮第一版直接除以
                    // 自报值，报出 240 ms/帧（真实逻辑帧才 16 ms），差了 12 倍
                    double measured = (stampNow - _probeWindowStamp) / wall;
                    double declared = System.Diagnostics.Stopwatch.Frequency;
                    double freq = measured > 0.0 ? measured : declared;

                    double totalMs = _probeTicks / freq * 1000.0;
                    double maxMs = _probeMaxTicks / freq * 1000.0;
                    double share = totalMs / (wall * 1000.0) * 100.0;

                    var biggest = 0;

                    foreach (KeyValuePair<int, Dictionary<CellKey, Cell>> planet in _byPlanet)
                    foreach (KeyValuePair<CellKey, Cell> pair in planet.Value)
                    {
                        int size = pair.Value.Drawn.Count + pair.Value.Hidden.Count;

                        if (size > biggest) biggest = size;
                    }

                    line =
                        $"共位只画一台·自测：过去 {wall:0.#} 秒里这个挂点被调用 {_probeCalls} 次，" +
                        $"合计 {totalMs:0.###} 毫秒（单次最久 {maxMs:0.###} 毫秒，占这段墙钟的 {share:0.##}%），" +
                        $"最大的一格叠了 {biggest} 台。" +
                        "**这个总数就是本 mod 在「共位只画一台」上花掉的全部时间**——" +
                        "它如果只有几毫秒而你仍然觉得卡，那卡的不是这里，" +
                        "该去看统计面板的性能测试（逻辑帧）或者显卡那一侧。" +
                        (Math.Abs(measured - declared) > declared * 0.05
                            ? $" ⚠ Stopwatch 自报频率 {declared:0} 和实测 {measured:0} 差得多，已按实测算。"
                            : "");
                }

                _probeTicks = 0;
                _probeCalls = 0;
                _probeMaxTicks = 0;
                _probeWindowStamp = stampNow;
                _probeWindowUtc = utcNow;
            }

            // 出锁再打日志：日志实现不在我们手里，别把它关进这把锁
            if (line != null) ProjectEdenPlugin.Log.LogInfo(line);
        }

        private static void AfterCreateDisplayCore(PlanetFactory __instance, int entityId)
        {
            if (!Enabled || __instance?.entityPool == null) return;
            if (entityId <= 0 || entityId >= __instance.entityPool.Length) return;

            ref EntityData entity = ref __instance.entityPool[entityId];

            if (entity.id != entityId || entity.protoId <= 0) return;

            // 原版那两条「模型为空就 ret」的路会留下 modelId = 0，那种本来就没画，不用管
            if (entity.modelId == 0) return;

            int limit = Config.stackedRenderLimit.Value;
            var sayRehide = false;
            var rehide = false;

            lock (_gate)
            {
                Dictionary<CellKey, Cell> table = PlanetTable(__instance);
                CellKey key = KeyOf(ref entity);

                if (!table.TryGetValue(key, out Cell cell))
                {
                    cell = new Cell();
                    table[key] = cell;
                }

                Prune(__instance, cell);

                // 已经登记为「画出来的那台」——这一次照旧画，什么都不用做
                if (cell.Drawn.Contains(entityId)) return;

                // **已经登记为「藏起来的」，但模型又回来了。**
                //
                // 离开星球再飞回来会走这条路：PlanetFactory.UnloadDisplay 把每个实体的
                // modelId / mmblockId / colliderId 逐个清零并整批拆掉渲染（IL 0037/0049/005B），
                // 回来时 PlanetModelingManager.LoadingPlanetFactoryMain @074D 又对每个实体
                // 重新调一次 CreateEntityDisplayComponents。所以这个后置会被再跑一遍，
                // 而**上一版在这里和 Drawn 合并成一个提前返回**，于是刚被原版重新创建的
                // 那个模型没人摘 —— 玩家报的「飞走再飞回来，反光又回来了」就是这个。
                //
                // 这是本文件反复记的那一类：**登记表记的是「我决定过什么」，
                // 不是「现在画着什么」。** 判断该不该摘，得看实体此刻的 modelId，
                // 不能看我自己的账本。
                rehide = cell.Hidden.Contains(entityId);

                if (!rehide)
                {
                    if (cell.Drawn.Count < limit)
                    {
                        cell.Drawn.Add(entityId);
                        return;
                    }

                    cell.Hidden.Add(entityId);
                    _hiddenTotal++;
                }
                else
                {
                    _rehidden++;
                }

                GameMain.gpuiManager?.RemoveModel(entity.modelIndex, entity.modelId, true);
                entity.modelId = 0;

                if (rehide && !_rehideReported)
                {
                    _rehideReported = true;
                    sayRehide = true;
                }
            }

            // 两条日志各走各的：首次藏起来是「功能生效了」，重返后重新藏起是
            // 「往返这条路也覆盖到了」。合成一条会让第二种情形没有任何痕迹，
            // 而那正是这次 bug 之所以只能靠玩家肉眼发现的原因
            if (sayRehide)
                ProjectEdenPlugin.Log.LogInfo(
                    $"共位只画一台：重返星球后重新藏起（实体 {entityId}，本局累计 {_rehidden} 台）。" +
                    "离开星球时原版会整批拆掉渲染并把每个实体的 modelId 清零" +
                    "（UnloadDisplay IL 0037），回来时 LoadingPlanetFactoryMain 逐个重建，" +
                    "所以这一步每次往返都要重跑一遍。");

            if (rehide) return;

            if (_reported) return;

            _reported = true;

            ProjectEdenPlugin.Log.LogInfo(
                $"共位只画一台：已生效（每格 {limit} 台，格边长 {Radius:0.##} 米）。" +
                $"第一次藏起来的是实体 {entityId}（物品原型 {__instance.entityPool[entityId].protoId}）。" +
                "逻辑一个字不动——采矿、耗电、点击、碰撞、小地图全照旧，只是不进渲染器。");
        }

        /// <summary>
        /// 前置：这一台被拆掉时，如果它正是画出来的那台，从藏着的里面顶一台上来。
        ///
        /// 不顶的话，玩家拆掉看得见的那台会让整摞消失——那看起来像「我一下拆光了」，
        /// 是比原问题更糟的症状。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.RemoveEntityWithComponents))]
        internal static void BeforeRemoveEntity(PlanetFactory __instance, int id)
        {
            if (!Enabled || __instance?.entityPool == null) return;
            if (id <= 0 || id >= __instance.entityPool.Length) return;

            ref EntityData entity = ref __instance.entityPool[id];

            if (entity.id != id || entity.protoId <= 0) return;

            lock (_gate)
            {
                Dictionary<CellKey, Cell> table = PlanetTable(__instance);
                CellKey key = KeyOf(ref entity);

                if (!table.TryGetValue(key, out Cell cell)) return;

                cell.Hidden.Remove(id);

                if (!cell.Drawn.Remove(id)) return;

                Promote(__instance, cell);
            }
        }

        /// <summary>
        /// 后置：星球显示被整批拆掉时报一行。
        ///
        /// <b>这一行的存在理由是「两种情形在日志里必须分得开」。</b>
        /// 上一版修好「重返后重新藏起」之后，下一局日志里那行没出现——而这
        /// <b>分不开</b>「玩家这局没起飞」和「修法根本没生效」，只能靠推测。
        /// 有了这一行就是确定的：
        /// <list type="bullet">
        /// <item>没有这一行 → 这局没离开过星球，那个路径压根没被测到</item>
        /// <item>有这一行、却没有「重返星球后重新藏起」→ 修法真的没生效</item>
        /// </list>
        ///
        /// <c>UnloadDisplay</c> 没有参数，不存在参数名对不上的风险。
        /// 它的唯一调用者是 <c>PlanetData.UnloadFactory</c>，而那个的第一个调用者是
        /// <c>GameData.LeavePlanet</c>——**起飞离开星球就会走到这里**。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.UnloadDisplay))]
        internal static void AfterUnloadDisplay(PlanetFactory __instance)
        {
            if (!Enabled) return;

            int drawn = 0, hidden = 0;

            lock (_gate)
            {
                if (!_byPlanet.TryGetValue(__instance?.planetId ?? 0, out Dictionary<CellKey, Cell> table)) return;

                foreach (KeyValuePair<CellKey, Cell> pair in table)
                {
                    drawn += pair.Value.Drawn.Count;
                    hidden += pair.Value.Hidden.Count;
                }
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"共位只画一台：{__instance?.planetId} 号星球的显示已被原版整批拆除" +
                $"（登记表里画着 {drawn} 台、藏着 {hidden} 台）。" +
                "飞回来时原版会逐个重建，那时藏着的那些要重新摘一遍——" +
                "下面应当出现「重返星球后重新藏起」。没有就是修法没生效。");
        }

        /// <summary>
        /// 从藏着的里面挑一台画出来。
        ///
        /// 挑哪一台无所谓——它们在同一个点上，玩家看到的是同一个位置有没有东西。
        /// 所以用枚举器取头一个即可，不需要 Hidden 保持顺序（这也是它能改成 HashSet 的前提）。
        /// </summary>
        private static void Promote(PlanetFactory factory, Cell cell)
        {
            while (cell.Hidden.Count > 0)
            {
                var next = 0;

                foreach (int e in cell.Hidden)
                {
                    next = e;

                    break;
                }

                cell.Hidden.Remove(next);

                if (cell.PrunedAt > cell.Hidden.Count) cell.PrunedAt = cell.Hidden.Count;

                if (next <= 0 || next >= factory.entityPool.Length) continue;

                ref EntityData other = ref factory.entityPool[next];

                if (other.id != next) continue;

                // 直接补一次 AddModel，而不是回头调 CreateEntityDisplayComponents——
                // 那个方法还会再建一次小地图块，等于给同一台建筑挂两块
                other.modelId = GameMain.gpuiManager.AddModel(other.modelIndex, next, other.pos, other.rot, true);

                cell.Drawn.Add(next);
                _promoted++;

                return;
            }
        }

        /// <summary>
        /// 把登记表里已经不存在的实体清掉。
        ///
        /// <b><see cref="Cell.Hidden"/> 那一半是摊销的，不是每次都扫。</b>
        /// 这个方法原先每次创建显示都把两张表整个走一遍，而 Hidden 的长度就是玩家叠在
        /// 这一点的建筑数——叠 N 台就是 O(N²)，落星球时更是一次性全付。
        ///
        /// 它其实很少有事可做：拆除走 <c>BeforeRemoveEntity</c>，那里已经把编号摘掉了；
        /// <c>Promote</c> 挑人时也会顺手跳过失效的。所以 Hidden 只在**比上次清理时涨了
        /// 一大截**才重扫一遍，摊到每次插入是常数。<see cref="Cell.Drawn"/> 照旧每次扫——
        /// 它的长度被 <c>stackedRenderLimit</c> 卡着（默认 1）。
        /// </summary>
        private static void Prune(PlanetFactory factory, Cell cell)
        {
            for (int i = cell.Drawn.Count - 1; i >= 0; i--)
            {
                int e = cell.Drawn[i];

                if (e > 0 && e < factory.entityPool.Length && factory.entityPool[e].id == e) continue;

                cell.Drawn.RemoveAt(i);
            }

            if (cell.Hidden.Count < cell.PrunedAt + PruneStride) return;

            cell.Hidden.RemoveWhere(e =>
                e <= 0 || e >= factory.entityPool.Length || factory.entityPool[e].id != e);

            cell.PrunedAt = cell.Hidden.Count;
        }

        /// <summary>Hidden 每涨这么多才全表清理一次。摊销之后每次插入是常数。</summary>
        private const int PruneStride = 1024;

        /// <summary>换存档时必须清空：实体编号会被复用，留着旧表会把新存档的建筑错认成旧的。</summary>
        internal static void Reset()
        {
            lock (_gate)
            {
                _byPlanet.Clear();
                _hiddenTotal = 0;
                _promoted = 0;
                _rehidden = 0;
                _rehideReported = false;
                _reported = false;
            }
        }

        /// <summary>开机状态行，<b>无条件</b>打印。</summary>
        internal static void Report()
        {
            if (!Enabled)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "共位只画一台：未开启（advancedminer.json 的 stackedRenderLimit 留空）。" +
                    "重叠建造的同种建筑会逐台绘制——几百台共位时半透明层和加法层会逐份累加，画面会发白。");
                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"共位只画一台：已开启，每格最多画 {Config.stackedRenderLimit.Value} 台，" +
                $"格边长 {Radius:0.##} 米。逻辑一个字不动，只影响 GPU 那一侧画几份。");
        }
    }
}
