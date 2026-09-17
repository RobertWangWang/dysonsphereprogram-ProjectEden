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

        /// <summary>一格里的登记：画出来的那几台，和被藏起来的那些。</summary>
        private sealed class Cell
        {
            internal readonly List<int> Drawn = new List<int>();
            internal readonly List<int> Hidden = new List<int>();
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
            if (!Enabled || __instance?.entityPool == null) return;
            if (entityId <= 0 || entityId >= __instance.entityPool.Length) return;

            ref EntityData entity = ref __instance.entityPool[entityId];

            if (entity.id != entityId || entity.protoId <= 0) return;

            // 原版那两条「模型为空就 ret」的路会留下 modelId = 0，那种本来就没画，不用管
            if (entity.modelId == 0) return;

            int limit = Config.stackedRenderLimit.Value;

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

                if (cell.Drawn.Contains(entityId) || cell.Hidden.Contains(entityId)) return;

                if (cell.Drawn.Count < limit)
                {
                    cell.Drawn.Add(entityId);
                    return;
                }

                cell.Hidden.Add(entityId);
                _hiddenTotal++;

                GameMain.gpuiManager?.RemoveModel(entity.modelIndex, entity.modelId, true);
                entity.modelId = 0;
            }

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

        /// <summary>从藏着的里面挑一台画出来。</summary>
        private static void Promote(PlanetFactory factory, Cell cell)
        {
            while (cell.Hidden.Count > 0)
            {
                int next = cell.Hidden[0];

                cell.Hidden.RemoveAt(0);

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

        /// <summary>把登记表里已经不存在的实体清掉。</summary>
        private static void Prune(PlanetFactory factory, Cell cell)
        {
            for (int i = cell.Drawn.Count - 1; i >= 0; i--)
            {
                int e = cell.Drawn[i];

                if (e > 0 && e < factory.entityPool.Length && factory.entityPool[e].id == e) continue;

                cell.Drawn.RemoveAt(i);
            }

            for (int i = cell.Hidden.Count - 1; i >= 0; i--)
            {
                int e = cell.Hidden[i];

                if (e > 0 && e < factory.entityPool.Length && factory.entityPool[e].id == e) continue;

                cell.Hidden.RemoveAt(i);
            }
        }

        /// <summary>换存档时必须清空：实体编号会被复用，留着旧表会把新存档的建筑错认成旧的。</summary>
        internal static void Reset()
        {
            lock (_gate)
            {
                _byPlanet.Clear();
                _hiddenTotal = 0;
                _promoted = 0;
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
