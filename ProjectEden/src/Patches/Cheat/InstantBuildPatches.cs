using System.Collections.Generic;
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

            // 品质要按 (星球, 桩号) 记，而 Pay 只拿得到桩号——在这里把星球号放好。
            // 上面那三道判定已经保证了「只处理玩家当前所在、且已加载的那颗星球」，
            // 所以这一局里它不会中途换值。
            _planetId = factory.planet.id;

            int budget = Config.instantBuildPerTick > 0 ? Config.instantBuildPerTick : DefaultPerTick;

            PrebuildData[] pool = factory.prebuildPool;

            var built = 0;
            var batching = false;
            var needPay = 0;
            var freeBuilt = 0;

            // 和 FastBuild 一样倒着走：新放下的排在后面，先建新的手感才对
            for (int i = factory.prebuildCursor - 1; i > 0 && built < budget; i--)
            {
                if (pool[i].id != i || pool[i].isDestroyed) continue;

                // **「这一座要不要我付账」是本轮唯一还没定的事实，所以数出来。**
                // itemRequired 已经是 0 的话，料在更早的地方就被扣掉了，Pay 一次都不会跑
                // ——而那正好解释「秒完成生效了、品质诊断却一行没有」。
                if (pool[i].itemRequired > 0) needPay++;
                else freeBuilt++;

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
                    $"作弊：建造秒完成已生效，本次结算建好 {built} 个（每次上限 {budget}）——"
                    + $"其中 {needPay} 个由这里付账、{freeBuilt} 个**放下时就已经付清了**。"
                    + "**后一个数不是 0，就说明扣料不在秒完成这条路上**，品质得去扣料的真正那一处接。");
        }

        /// <summary>
        /// 从机甲背包扣料。返回<b>这一个预建物是不是已经付清</b>。
        ///
        /// <c>TakeTailItems</c> 会把 <paramref name="pool"/>[<paramref name="index"/>] 要的数量
        /// 改写成<b>实际拿到的数量</b>，所以背包里只有一半时也能先付一半，剩下的下一轮再付，
        /// 和建设机器人的行为一致。拿不出来就原样留着，不会把预建物卡死。
        ///
        /// 增产点数（<c>inc</c>）直接丢掉：建筑不吃增产，原版机器人也不把它带进建筑里。
        ///
        /// <b>品质则相反，必须接住——这一处曾经是把它清零的，而那让「建筑按品质省电」
        /// 整条轴在秒完成开着时完全失效。</b>
        ///
        /// 原来那句注释写着「建好的建筑不保留品质（建筑没有品质槽位）」，它在写的时候是对的，
        /// 效果层出现之后就过时了：建筑确实没有品质槽位，但<b>材料的品质会在建造那一刻
        /// 折进这座建筑的耗电</b>（<see cref="QualityBuildPatches"/>）。
        ///
        /// 而秒完成是<b>本 mod 自己重写的一条建造路径</b>：它从
        /// <c>ConstructionBeforeGameTick</c> 的后置里扣料，**在 <c>QualityBuildPatches</c>
        /// 的扣料作用域之外**（那个作用域是五把建造工具的 <c>CreatePrebuilds</c> 等七个方法）。
        /// 于是玩家放下一座用 50 分材料做的建筑，日志里一行都没有、电费一分不省。
        /// **这是「本仓库为某个功能加的规则，悄悄限制了后来加的另一个功能」那一类**，
        /// 而这次两边都是我们自己的。
        /// </summary>
        private static bool Pay(Player player, PrebuildData[] pool, int index)
        {
            int itemId = pool[index].protoId;
            int count = pool[index].itemRequired;

            // **调用前清、调用后读、读完再清。** preloader 把 TakeTailItems 改写成了
            // 「读寄存器（入参方向）+ 在 ret 前写寄存器（出参方向）」，而那条协议
            // 只在游戏自己的调用点上接好了。不清前面，进去时它消费上一个人留下的值；
            // 不清后面，它留下的值会被下一个读它的人当成自己的——两头都让品质凭空长出来。
            //
            // **但「清」和「读」不是一回事**，而上一版把两者混为一谈：出参方向的正确做法是
            // 读回来再清，直接清掉等于把被调方刚算好的答案扔了。
            // **扣了多少分是量出来的，不是从侧信道接的。**
            //
            // 接侧信道本来更直接，但它依赖「被调方在返回前发布、而且没有别人把它擦掉」
            // 这条协议在这一处成立——而本仓库已经栽过两次：`TakeItemFromPlayer` 和
            // `UseHandItems` 都是算对了再自己擦掉。**量背包差值不依赖任何协议**，
            // 而且是精确的：背包少掉的那些分，正是这次建造吃掉的。
            //
            // 侧信道的值仍然读一下，只进诊断——两个数不一致时那一行就是线索。
            MeasureBag(player, itemId, out int bagCount, out int bagQua);

            if (QualityAccess.ChannelClearable) QualityAccess.ClearChannel();

            player.package.TakeTailItems(ref itemId, ref count, out int _, false);

            int viaChannel = QualityAccess.ChannelReady ? QualityAccess.GetChannel0() : 0;

            if (QualityAccess.ChannelClearable) QualityAccess.ClearChannel();

            MeasureBag(player, itemId, out int _, out int bagAfter);

            int qua = bagQua - bagAfter;

            if (qua < 0) qua = 0;

            // **每一个闸都打出来，不是只打我相信的那一个。** 「材料本来就没品质」和
            // 「品质读丢了」在结果上一模一样（都是 0 分），而它们要做的事完全相反。
            if (Interlocked.Exchange(ref _payLogged, 1) == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    $"作弊·建造秒完成·诊断：桩 {pool[index].id} 要「{pool[index].protoId}」，扣到 {count} 件。"
                    + $"扣之前背包里这种货 {bagCount} 件 / {bagQua} 分，扣之后 {bagAfter} 分，"
                    + $"**按差值算这次吃掉 {qua} 分**；侧信道同时读回 {viaChannel} 分。"
                    + "**背包那两个数都是 0 → 材料本来就没品质（上游的事）；"
                    + "差值不是 0 而侧信道是 0 → 侧信道在这一处被谁擦了（差值已经绕开它）。** 整局只报一次。");

            if (count <= 0) return false;

            pool[index].itemRequired -= count;

            NotePaid(pool[index].id, count, qua);

            if (pool[index].itemRequired > 0) return false;

            SettleQuality(index);

            return true;
        }

        // ── 品质：一座桩可能分几个 tick 才付清，所以要按桩号累计 ──────────

        /// <summary>
        /// <c>prebuildId → (件数, 总分)</c>。背包里只有一半时这一座会先付一半、下一轮再付，
        /// 所以不能付一次算一次——<b>平均分要拿整座建筑的料算</b>。
        ///
        /// 只在本机当前星球上跑（<see cref="BuildPaid"/> 开头就把别的星球挡掉了），
        /// 而且每 tick 最多 <c>budget</c> 座，所以一个普通字典足够，不必上并发容器。
        /// </summary>
        private static readonly Dictionary<int, (int Items, long Points)> Paying =
            new Dictionary<int, (int, long)>();

        private static int _planetId;

        private static void NotePaid(int prebuildId, int items, int qua)
        {
            if (prebuildId <= 0 || items <= 0) return;

            // **付到一半的桩被玩家取消掉，这条就没人来收了。** 没有现成的钩子能知道
            // 哪一座被取消，所以这里只做个上界：正常情况下这张表几乎总是空的
            // （绝大多数建筑一次付清），涨到三位数就说明积了垃圾，整张丢掉。
            // 代价是当时正在分期付款的那几座退回 0 分——有界、可解释，比无限涨好。
            if (Paying.Count > 256) Paying.Clear();

            Paying.TryGetValue(prebuildId, out (int Items, long Points) acc);

            Paying[prebuildId] = (acc.Items + items, acc.Points + qua);
        }

        /// <summary>
        /// 付清了：把「每件平均分」挂到这座桩上，等 <c>BuildFinally</c> 把它变成实体时，
        /// <c>QualityBuildPatches</c> 的 <c>AddEntityDataWithComponents</c> 后置会把它接走
        /// 并写进耗电。**这里不碰耗电**——那个字段只该在实体刚建好、还没人改过时写一次。
        /// </summary>
        private static void SettleQuality(int prebuildId)
        {
            if (!Paying.TryGetValue(prebuildId, out (int Items, long Points) acc)) return;

            Paying.Remove(prebuildId);

            if (_planetId <= 0 || acc.Items <= 0 || acc.Points <= 0) return;

            var perItem = (int)(acc.Points / acc.Items);

            if (perItem <= 0) return;

            QualityBuildStore.SetPending(_planetId, prebuildId, perItem);

            // **这条路自己的一次性日志。** 秒完成开着时，扣料不走 QualityBuildPatches
            // 的作用域，所以那边的「建造扣料」永远不会打——少了这一行，
            // 「秒完成把品质接上了」和「压根没接」在日志里又分不开了。
            if (Interlocked.Exchange(ref _quaLogged, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"作弊·建造秒完成：第一次把材料品质接给建筑——桩 {prebuildId}，"
                + $"{acc.Items} 件料合计 {acc.Points} 分，每件 {perItem} 分。"
                + "**秒完成是本 mod 自己的建造路径，不走「建造扣料」那条作用域**，"
                + "所以看这一行、不要找那一行。接下来该出现的是「效果层：第一座用带品质的材料造出来的建筑」。");
        }

        private static int _quaLogged;

        private static int _payLogged;

        /// <summary>背包里这种货现在共几件、共多少分。只给上面那条一次性诊断用。</summary>
        private static void MeasureBag(Player player, int itemId, out int count, out int qua)
        {
            count = 0;
            qua = 0;

            StorageComponent.GRID[] grids = player.package?.grids;

            if (grids == null || !QualityAccess.GridReady) return;

            for (var g = 0; g < grids.Length; g++)
            {
                if (grids[g].count <= 0 || grids[g].itemId != itemId) continue;

                count += grids[g].count;
                qua += QualityAccess.GetGridQua(ref grids[g]);
            }
        }
    }
}
