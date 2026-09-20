#pragma warning disable 649 // PowerConfig 的字段由 JSON 反序列化赋值

using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 电力节点的供电范围（卫星配电站的「全球覆盖」）。
    ///
    /// 链路和别处一样是「建造时定死、进存档」，但这条比容量类的多绕一层缓存：
    ///     PrefabDesc.powerCoverRadius
    ///       → PlanetFactory.CreateEntityLogicComponents 传给 PowerSystem.NewNodeComponent
    ///       → 写进 PowerNodeComponent.coverRadius（<b>进存档</b>）
    ///       → PowerSystem.OnNodeAdded 再把它<b>平方后缓存</b>进 PowerNetworkStructures.Node.coverRadius2，
    ///         并在那里一次性把范围内的用电建筑挂到电网上
    /// 所以运行时光改 coverRadius 没用：缓存的 coverRadius2 不会跟着变，
    /// 已建成的用电建筑也不会自动重连（PowerConsumerComponent.networkId 是存好的）。
    ///
    /// PowerSystem.GameTick 里那处 coverRadius 只在 isCharger 分支，是机甲充电范围，
    /// 不重算用电覆盖，指望它自愈是不行的。
    ///
    /// 做法：对目标节点走一遍原版的「拆—建」——OnNodeRemoving 之后改值、再 OnNodeAdded。
    /// 这正是玩家拆掉重建一座配电站时游戏自己走的路，缓存和消费者连接都由它重算，
    /// 比我们手动去掏 netPool 里的缓存结构安全得多。
    ///
    /// <b>时机</b>：挂在 GameData.Import 之后，主线程、每次读档一次。
    /// 不能挂 PowerSystem.GameTick——它有 FactoryPowerSystemGameTick_Parallel 这个多线程变体，
    /// 在工作线程里改电网结构是自找崩溃。
    ///
    /// <b>尺度</b>：OnNodeAdded / OnConsumerAdded 会先把坐标投影到半径 realRadius + 0.2 的球面
    /// （p × (realRadius + 0.2) / alt），再比三维距离的平方。所以 coverRadius 的单位就是米，
    /// 覆盖整颗星球需要 ≥ 2 × 星球半径。普通行星半径 200，配 2000 足够覆盖任何可建造的星球。
    /// </summary>
    [HarmonyPatch]
    internal static class PowerCoveragePatches
    {
        private static PowerConfig Config => ProjectEdenPlugin.PowerConfig;

        /// <summary>
        /// 每个目标 protoId 要用的值。<b>逐项而不是一组</b>——
        /// 卫星配电站要「全球覆盖」，电力感应塔要「局域但比原版大」，一个数套不住两者。
        /// </summary>
        private static readonly Dictionary<int, PowerNodeEntry> Targets = new Dictionary<int, PowerNodeEntry>();

        /// <summary>proto 就绪后调用：改 prefabDesc，新建的节点直接带上这个范围。</summary>
        internal static void ApplyPrefabCoverage()
        {
            Targets.Clear();

            if (Config == null) return;

            foreach (PowerNodeEntry entry in Config.Entries())
            {
                if (entry.coverRadius <= 0f && entry.connectDistance <= 0f) continue;

                ItemProto item = LDB.items.Select(entry.itemId);
                ModelProto model = item != null ? LDB.models.Select(item.ModelIndex) : null;

                if (model?.prefabDesc == null || !model.prefabDesc.isPowerNode)
                {
                    ProjectEdenPlugin.Log.LogWarning($"物品 {entry.itemId} 不是电力节点或没有 prefabDesc，供电范围未改");
                    continue;
                }

                float beforeCover = model.prefabDesc.powerCoverRadius;
                float beforeConnect = model.prefabDesc.powerConnectDistance;

                if (entry.coverRadius > 0f) model.prefabDesc.powerCoverRadius = entry.coverRadius;

                // 连接距离默认不动：把它放开会让节点之间两两相连，
                // line_arragement_for_add_node 的连线量按节点密度的平方增长。
                if (entry.connectDistance > 0f)
                    model.prefabDesc.powerConnectDistance = entry.connectDistance;

                Targets[entry.itemId] = entry;

                ProjectEdenPlugin.Log.LogInfo(
                    $"{item.name} 供电范围：{beforeCover:0.#} → {model.prefabDesc.powerCoverRadius:0.#} 米" +
                    $"（连接距离 {beforeConnect:0.#} → {model.prefabDesc.powerConnectDistance:0.#} 米）");
            }
        }

        /// <summary>读档之后把存档里的老节点补齐。主线程，一次性。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
        private static void GameData_Import(GameData __instance) => RebuildExistingNodes(__instance);

        private static void RebuildExistingNodes(GameData data)
        {
            if (Targets.Count == 0 || data?.factories == null) return;

            var rebuilt = 0;

            for (var f = 0; f < data.factoryCount; f++)
            {
                PlanetFactory factory = data.factories[f];
                PowerSystem power = factory?.powerSystem;

                if (power?.nodePool == null || factory.entityPool == null) continue;

                EntityData[] entityPool = factory.entityPool;

                for (var i = 1; i < power.nodeCursor; i++)
                {
                    if (power.nodePool[i].id != i) continue;

                    int entityId = power.nodePool[i].entityId;

                    if (entityId <= 0 || entityId >= entityPool.Length) continue;
                    if (!Targets.TryGetValue(entityPool[entityId].protoId, out PowerNodeEntry entry)) continue;

                    float wantCover = entry.coverRadius > 0f ? entry.coverRadius : power.nodePool[i].coverRadius;
                    float wantConnect = entry.connectDistance > 0f
                        ? entry.connectDistance
                        : power.nodePool[i].connectDistance;

                    // **判据是「不等于」而不是「小于」。** 早先写的是 `coverRadius >= cover 就跳过`，
                    // 那在「只放大」的年代是对的；一旦某一项配成比原版**小**的值（电力感应塔
                    // 就可能这样），那个判据会把它整批跳过，而且一声不吭。
                    if (Near(power.nodePool[i].coverRadius, wantCover)
                        && Near(power.nodePool[i].connectDistance, wantConnect))
                        continue;

                    power.OnNodeRemoving(i);

                    power.nodePool[i].coverRadius = wantCover;
                    power.nodePool[i].connectDistance = wantConnect;

                    power.OnNodeAdded(i);

                    rebuilt++;
                }
            }

            if (rebuilt > 0)
                ProjectEdenPlugin.Log.LogInfo($"已重建 {rebuilt} 座已建成电力节点的供电范围（拆建流程，消费者已重连）");
        }

        /// <summary>浮点相等的容差比较——这两个值都是从存档里读回来的，不能指望位相同。</summary>
        private static bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;
    }

    /// <summary>
    /// 一个电力节点的逐项设置。<b>不同的塔需要不同的数</b>——卫星配电站要的是「全球覆盖」，
    /// 而电力感应塔要的是「比原版大一些但仍然是局域」，一组值套不住两者。
    /// </summary>
    [Serializable]
    internal class PowerNodeEntry
    {
        /// <summary>电力节点的物品 ID。</summary>
        public int itemId;

        /// <summary>供电范围（米）。0 或负数 = 这一项不改。</summary>
        public float coverRadius;

        /// <summary>
        /// 节点之间的连接距离（米）。0 = 保持原版。
        /// <b>放大它是有代价的</b>：<c>line_arragement_for_add_node</c> 的连线量按节点密度平方增长。
        /// </summary>
        public float connectDistance;

        public string comment;
    }

    [Serializable]
    internal class PowerConfig
    {
        /// <summary>
        /// 逐项设置。<b>有它就以它为准</b>，下面那三个旧字段只在它为空时才用——
        /// 老的磁盘覆盖（只有 itemIds/coverRadius/connectDistance）因此仍然照常工作。
        /// </summary>
        public PowerNodeEntry[] nodes;

        /// <summary>旧 schema：要改的电力节点物品 ID（所有项共用下面两个值）。</summary>
        public int[] itemIds;

        /// <summary>旧 schema：供电范围（米）。0 保持原版。</summary>
        public float coverRadius;

        /// <summary>旧 schema：节点之间的连接距离（米）。0 保持原版。</summary>
        public float connectDistance;

        /// <summary>
        /// 把两种 schema 归一成一张表。新的优先；都没有就返回空表。
        /// <b>归一化放在配置类里而不是应用处</b>，这样「读哪一份」这件事只有一个答案。
        /// </summary>
        internal IEnumerable<PowerNodeEntry> Entries()
        {
            if (nodes != null && nodes.Length > 0)
            {
                foreach (PowerNodeEntry e in nodes)
                    if (e != null && e.itemId > 0)
                        yield return e;

                yield break;
            }

            if (itemIds == null || coverRadius <= 0f) yield break;

            foreach (int id in itemIds)
                yield return new PowerNodeEntry
                {
                    itemId = id,
                    coverRadius = coverRadius,
                    connectDistance = connectDistance,
                    comment = "（旧 schema：itemIds + coverRadius）",
                };
        }
    }
}
