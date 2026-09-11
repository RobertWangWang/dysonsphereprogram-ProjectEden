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

        /// <summary>要放大供电范围的建筑 protoId。</summary>
        private static readonly HashSet<int> Targets = new HashSet<int>();

        /// <summary>proto 就绪后调用：改 prefabDesc，新建的节点直接带上这个范围。</summary>
        internal static void ApplyPrefabCoverage()
        {
            Targets.Clear();

            if (Config?.itemIds == null || Config.coverRadius <= 0f) return;

            foreach (int itemId in Config.itemIds)
            {
                ItemProto item = LDB.items.Select(itemId);
                ModelProto model = item != null ? LDB.models.Select(item.ModelIndex) : null;

                if (model?.prefabDesc == null || !model.prefabDesc.isPowerNode)
                {
                    ProjectEdenPlugin.Log.LogWarning($"物品 {itemId} 不是电力节点或没有 prefabDesc，供电范围未改");
                    continue;
                }

                float beforeCover = model.prefabDesc.powerCoverRadius;
                float beforeConnect = model.prefabDesc.powerConnectDistance;

                model.prefabDesc.powerCoverRadius = Config.coverRadius;

                // 连接距离默认不动：把它一起放开会让所有节点两两相连，
                // line_arragement_for_add_node 的连线量按平方增长，节点一多就是性能问题。
                if (Config.connectDistance > 0f)
                    model.prefabDesc.powerConnectDistance = Config.connectDistance;

                Targets.Add(itemId);

                ProjectEdenPlugin.Log.LogInfo(
                    $"{item.name} 供电范围：{beforeCover:0.#} → {model.prefabDesc.powerCoverRadius:0.#} 米" +
                    $"（连接距离 {beforeConnect:0.#} → {model.prefabDesc.powerConnectDistance:0.#}）");
            }
        }

        /// <summary>读档之后把存档里的老节点补齐。主线程，一次性。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
        private static void GameData_Import(GameData __instance) => RebuildExistingNodes(__instance);

        private static void RebuildExistingNodes(GameData data)
        {
            if (Targets.Count == 0 || data?.factories == null) return;

            float cover = Config.coverRadius;
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
                    if (!Targets.Contains(entityPool[entityId].protoId)) continue;

                    // 已经是目标范围就跳过——新建的节点从 prefabDesc 就拿到了正确值
                    if (power.nodePool[i].coverRadius >= cover) continue;

                    power.OnNodeRemoving(i);

                    power.nodePool[i].coverRadius = cover;

                    if (Config.connectDistance > 0f) power.nodePool[i].connectDistance = Config.connectDistance;

                    power.OnNodeAdded(i);

                    rebuilt++;
                }
            }

            if (rebuilt > 0)
                ProjectEdenPlugin.Log.LogInfo($"已重建 {rebuilt} 座已建成配电站的供电范围（拆建流程，消费者已重连）");
        }
    }

    [Serializable]
    internal class PowerConfig
    {
        /// <summary>要改的电力节点物品 ID</summary>
        public int[] itemIds;

        /// <summary>供电范围（米）。0 保持原版。</summary>
        public float coverRadius;

        /// <summary>节点之间的连接距离（米）。0 保持原版——不建议动，见类注释。</summary>
        public float connectDistance;
    }
}
