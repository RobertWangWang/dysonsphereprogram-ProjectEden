using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 建造秒完成：预建物一放下就建好，不等建设机器人飞过去。<b>材料照扣。</b>
    ///
    /// <b>原版自己就有这条路，只是锁在沙盒模式里。</b> 从 IL 读出来的调用链是
    /// <code>
    ///   PlanetFactory.ConstructionBeforeGameTick
    ///     → ConstructionSystem.BeforeGameTick
    ///       → ConstructionSystem.ExecuteFastBuild
    ///           if (!GameMain.sandboxToolsEnabled) return;
    ///           if (GamePrefsData.fastBuildBatchSize &lt;= 0) return;
    ///           RecycleDronesForConstructInstantly();
    ///           FastBuild(fastBuildBatchSize);
    /// </code>
    /// 而 <c>FastBuild</c> 的躯体就是本文件抄的那套：
    /// <c>batchBuild = true</c> → <c>BeginFlattenTerrain()</c> → 从 <c>prebuildCursor</c>
    /// 倒着走、逐个 <c>BuildFinally(mainPlayer, id, false, true)</c> → <c>EndFlattenTerrain()</c>
    /// → <c>batchBuild = false</c> → 通知射线逻辑与音频、触发两个批量建造事件。
    ///
    /// <b>所以不需要 CheatEnabler 那一层传送带渲染抑制。</b> 它额外前置了
    /// <c>CargoTraffic.AlterBeltRenderer</c> 等五个方法把刷新攒到最后，而原版自己的
    /// <c>FastBuild</c> 并不这么做——<c>PlanetFactory.batchBuild</c> 这一个静态标志
    /// （<c>BuildFinally</c> IL 01CC 处读它，用来跳过 <c>OnSinglyBuildEntity</c>）
    /// 加上 FlattenTerrain 的成对括号就是原版认为足够的全部。照原版走，少五个补丁。
    ///
    /// <b>唯一和原版不同的地方：付钱。</b> <c>FastBuild</c> 完全无视 <c>itemRequired</c>
    /// ——沙盒模式里本来就免费。这里改成先从机甲背包扣料，扣得起才建，扣不起原样留着
    /// 交回给原版的机器人。这样「秒完成」只省时间，不顺手把「免费建造」也一起给了。
    ///
    /// 挂的是<b>后置</b>不是前置：沙盒模式下原版已经把预建物清空了，我们的循环自然空转，
    /// 不会把沙盒的免费建造降级成收费的。
    /// </summary>
    [HarmonyPatch]
    internal static class InstantBuildPatches
    {
        private const int DefaultPerTick = 100;

        private static CheatsConfig Config => ProjectEdenPlugin.CheatsConfig;

        private static bool On => Config != null && Config.enabled && Config.instantBuild;

        private static int _logged;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ConstructionSystem), nameof(ConstructionSystem.ExecuteFastBuild))]
        private static void ConstructionSystem_ExecuteFastBuild(ConstructionSystem __instance) => BuildPaid(__instance);

        /// <summary>
        /// 每 tick 都会跑，所以先用最便宜的判断挡住：没开、不是本地星球、没有预建物，直接走人。
        /// </summary>
        private static void BuildPaid(ConstructionSystem system)
        {
            if (!On || system == null || system.planet == null) return;

            PlanetFactory factory = system.factory;

            if (factory == null) return;

            // FastBuild 的第一条判定：只处理玩家当前所在、且已加载的那颗星球。
            // 别的星球没有加载模型和碰撞体，就地建出来是没法收场的。
            if (factory.gameData == null || factory.gameData.localLoadedPlanetFactory != factory) return;

            if (factory.prebuildCount <= 0) return;

            Player player = GameMain.mainPlayer;

            if (player == null || player.package == null) return;

            int budget = Config.instantBuildPerTick > 0 ? Config.instantBuildPerTick : DefaultPerTick;

            PrebuildData[] pool = factory.prebuildPool;

            var built = 0;
            var batching = false;

            // 和 FastBuild 一样倒着走：新放下的排在后面，先建新的手感才对
            for (int i = factory.prebuildCursor - 1; i > 0 && built < budget; i--)
            {
                if (pool[i].id != i || pool[i].isDestroyed) continue;

                if (pool[i].itemRequired > 0 && !Pay(player, pool, i)) continue;

                if (!batching)
                {
                    batching = true;

                    PlanetFactory.batchBuild = true;

                    factory.BeginFlattenTerrain();

                    // 已经飞出去的机器人得先召回，否则它们会追着马上就要消失的预建物跑
                    system.RecycleDronesForConstructInstantly();
                }

                factory.BuildFinally(player, i, false, true);

                built++;
            }

            if (!batching) return;

            factory.EndFlattenTerrain();

            PlanetFactory.batchBuild = false;

            // 收尾这四步照抄 FastBuild：少了它们，拆除后的射线数据、行星音效
            // 和别的 mod 挂的批量建造回调都会停在旧状态上
            PlanetData planet = factory.planet;

            if (planet != null)
            {
                if (planet.physics != null && planet.physics.raycastLogic != null)
                    planet.physics.raycastLogic.NotifyBatchObjectRemove();

                if (planet.audio != null) planet.audio.SetPlanetAudioDirty();
            }

            system.onBatchBuild?.Invoke();
            ConstructionSystem.onFactoryBatchBuild?.Invoke(factory);

            if (Interlocked.Exchange(ref _logged, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"作弊：建造秒完成已生效，本次结算建好 {built} 个（每次上限 {budget}，材料从机甲背包扣）");
        }

        /// <summary>
        /// 从机甲背包扣料。返回<b>这一个预建物是不是已经付清</b>。
        ///
        /// <c>TakeTailItems</c> 会把 <paramref name="pool"/>[<paramref name="index"/>] 要的数量
        /// 改写成<b>实际拿到的数量</b>，所以背包里只有一半时也能先付一半，剩下的下一轮再付，
        /// 和建设机器人的行为一致。拿不出来就原样留着，不会把预建物卡死。
        ///
        /// 增产点数（<c>inc</c>）直接丢掉：建筑不吃增产，原版机器人也不把它带进建筑里。
        /// </summary>
        private static bool Pay(Player player, PrebuildData[] pool, int index)
        {
            int itemId = pool[index].protoId;
            int count = pool[index].itemRequired;

            // **调用前后各清一次品质侧信道。** preloader 把 TakeTailItems 改写成了
            // 「读寄存器（入参方向）+ 在 ret 前写寄存器（出参方向）」，而那条协议
            // 只在游戏自己的调用点上接好了。我们不清的话：进去时它消费上一个人留下的值，
            // 出来时它留下的值又会被下一个读它的人当成自己的——两头都会让品质凭空长出来。
            //
            // 这一处是 tools/verify_quality.ps1 新加的那道检查第一次跑就抓出来的，
            // 而肉眼扫「谁调了搬运方法」时它被漏掉了：建造扣料看着和物品搬运不是一回事。
            //
            // 建好的建筑不保留品质（建筑没有品质槽位），所以这里是清零而不是赋值。
            if (QualityAccess.ChannelClearable) QualityAccess.ClearChannel();

            player.package.TakeTailItems(ref itemId, ref count, out int _, false);

            if (QualityAccess.ChannelClearable) QualityAccess.ClearChannel();

            if (count <= 0) return false;

            pool[index].itemRequired -= count;

            return pool[index].itemRequired <= 0;
        }
    }
}
