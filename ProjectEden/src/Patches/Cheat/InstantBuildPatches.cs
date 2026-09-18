using System;
using System.Collections.Generic;
using System.Diagnostics;
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

            _batchBegan = Stopwatch.GetTimestamp();

            // 和 FastBuild 一样倒着走：新放下的排在后面，先建新的手感才对
            for (int i = factory.prebuildCursor - 1; i > 0 && built < budget; i--)
            {
                if (pool[i].id != i || pool[i].isDestroyed) continue;

                // **「这一座要不要我付账」是本轮唯一还没定的事实，所以数出来。**
                // itemRequired 已经是 0 的话，料在更早的地方就被扣掉了，Pay 一次都不会跑
                // ——而那正好解释「秒完成生效了、品质诊断却一行没有」。
                if (pool[i].itemRequired > 0) needPay++;
                else freeBuilt++;

                if (pool[i].itemRequired > 0 && !Pay(player, pool, i))
                {
                    _unpayable++;

                    continue;
                }

                if (!batching)
                {
                    batching = true;

                    PlanetFactory.batchBuild = true;

                    factory.BeginFlattenTerrain();

                    // 已经飞出去的机器人得先召回，否则它们会追着马上就要消失的预建物跑
                    long recycleBegan = Stopwatch.GetTimestamp();

                    system.RecycleDronesForConstructInstantly();

                    _recycleTicks += Stopwatch.GetTimestamp() - recycleBegan;
                }

                long buildBegan = Stopwatch.GetTimestamp();

                factory.BuildFinally(player, i, false, true);

                _buildTicks += Stopwatch.GetTimestamp() - buildBegan;

                built++;
            }

            // <b>收尾整个包在 if 里，而报告放在它后面、无条件跑。</b>
            // 上一版是 `if (!batching) return;` 之后才报，结果玩家那一局「使劲造」了几千次、
            // 日志里一行自测都没有——因为料不够，一座都没建成，方法在这里就返回了。
            // 而<b>那恰恰是最该被报出来的状态</b>：预建物只进不出地堆积，
            // 原版每 tick 还要把那一堆重扫一遍派机器人。
            //
            // 报告放在最后而不是最前，是为了让计时把收尾那一段也算进去。
            if (batching)
            {
                long finishBegan = Stopwatch.GetTimestamp();

                Finish(system, factory);

                _finishTicks += Stopwatch.GetTimestamp() - finishBegan;

                if (Interlocked.Exchange(ref _logged, 1) == 0)
                    ProjectEdenPlugin.Log.LogInfo(
                        $"作弊：建造秒完成已生效，本次结算建好 {built} 个（每次上限 {budget}）——"
                        + $"其中 {needPay} 个由这里付账、{freeBuilt} 个**放下时就已经付清了**。"
                        + "**后一个数不是 0，就说明扣料不在秒完成这条路上**，品质得去扣料的真正那一处接。");
            }

            NoteBatchCost(built, budget, factory.prebuildCount);
        }

        /// <summary>批量建造的收尾：发布地形（只在真的改了地形时）+ 照抄 FastBuild 的四步。</summary>
        private static void Finish(ConstructionSystem system, PlanetFactory factory)
        {

            // <b>这一批没改地形，就别发布地形——这是本轮 100 毫秒/座的全部成因。</b>
            //
            // EndFlattenTerrain 的尾巴是四个重活，而且是<b>无条件</b>跑的：
            //   0100 PlanetData::UpdateDirtyMeshes
            //   0115 PlanetFactory::RenderLocalPlanetHeightmap
            //   0123 PlanetAlgorithm::CalcLandPercent          （整颗星球重算陆地比例）
            //   0128 GPUInstancingManager::SyncAllGPUBuffer    （遍历每一个 ObjectRenderer
            //                                                   逐个 SyncInstBuffer，几百个模型类型全量上传）
            //
            // 原版 FastBuild 把这笔固定开销摊在一批最多 100 座上；而玩家是一座一座点的，
            // 我们每次结算只建到 1 座就把整笔付掉——实测<b>每座 100+ 毫秒</b>，
            // 直接加在逻辑帧上（性能面板：建设系统 21 ms、伊卡洛斯 29 ms）。
            //
            // <b>判据是 tmp_levelChanges 为空，这是查过的事实不是猜的</b>：全程序集里写这个字典的
            // 只有 FlattenTerrain / FlattenTerrainOffline / ComputeFlattenTerrainReform /
            // FlattenTerrainReform 四个，全是地形改动，而 BeginFlattenTerrain 只负责清空它。
            // 空 ⇒ 这一批一格地形都没动 ⇒ 那四个重活发布不出任何新东西。
            //
            // <b>SyncAllGPUBuffer 不是「新建了模型要同步」。</b> 它长在地形收尾里，是因为
            // 地形一变所有实例的贴地高度都得重算；原版让机器人正常建造那条路根本不调它，
            // 单个模型的同步走的是 AddModel(setBuffer: true) 自己那条。
            bool terrainDirty = factory.tmp_levelChanges != null && factory.tmp_levelChanges.Count > 0;

            if (terrainDirty)
            {
                long flattenBegan = Stopwatch.GetTimestamp();

                factory.EndFlattenTerrain();

                _flattenTicks += Stopwatch.GetTimestamp() - flattenBegan;
                _flattenRuns++;
            }
            else
            {
                _flattenSkips++;
            }

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
        }

        private static long _flattenTicks;
        private static int _flattenRuns;
        private static int _flattenSkips;
        private static int _unpayable;
        private static int _worstPending;
        private static int _backlogWarned;

        /// <summary>
        /// 分段计时：这一次结算的 100 毫秒到底花在哪一段。
        ///
        /// <b>拿到「每次结算 103 毫秒、只建 1 座、地形收尾全跳过」之后，方法里只剩四段可疑，
        /// 而这四段的代价差着量级，再猜一次不如把它们分开量。</b>
        /// 这条路每秒只走几次（实测 10.2 秒 31 次），所以多几个时间戳是免费的——
        /// 和 1.10.0 那次不同，那一次分段计时挂在 tick 路径上，探针自己就成了瓶颈。
        /// </summary>
        private static long _recycleTicks;

        private static long _buildTicks;
        private static long _finishTicks;

        /// <summary>
        /// <see cref="PlanetTransport.RefreshStationTraffic"/> 的耗时和次数，由
        /// <see cref="StationTrafficProbe"/> 填。
        ///
        /// <b>它是 BuildFinally 里最像那 100 毫秒的一段，而且形状是确定的 O(n²)</b>：
        /// 66 条指令里是两个走满 <c>stationCursor</c> 的循环，第二个把<b>整个 stationPool</b>
        /// 传进 <c>RematchLocalPairs</c>——每个站点和其它每个站点重配一遍。
        /// 这颗星球 2078 个站点（1175 个是巨型建筑，本 mod 让它们同时是物流站），
        /// 就是 430 万次配对；而调用点之一是
        /// <c>BuildingParameters::ApplyPrebuildParametersToEntity @1024</c>——
        /// <b>放下一座带参数的建筑就整颗星球重配一次</b>。
        /// </summary>
        internal static long TrafficTicks;

        internal static int TrafficCalls;
        private static long _batchBegan;
        private static long _worstTicks;
        private static int _worstBuilt;
        private static long _batchTicks;
        private static long _batchCount;
        private static long _batchBuilt;
        private static DateTime _windowUtc;
        private static long _windowStamp;
        private static int _reports;

        /// <summary>
        /// 秒建这一批花了多久，报最坏的那一次。
        ///
        /// <b>这条不是装饰，是这个功能唯一会被玩家感觉到的代价。</b>
        /// 实测调用链是
        /// <c>GameLogic.FactoryBeforeGameTick @0016 → PlanetFactory.ConstructionBeforeGameTick @000E</c>，
        /// 也就是<b>秒建整个跑在逻辑帧里面</b>——一个 tick 造 100 座完整建筑
        /// （每座要建组件、物流站、电力消费者、模型、小地图块），
        /// 玩家看到的就是「一建造，逻辑帧从 6 ms 飙到 20 ms」。
        ///
        /// 有了这一行，「上限调多少合适」就不用猜：拿最坏那一次的毫秒数除以座数，
        /// 就是这颗星球上每座的真实代价，再拿你能接受的抖动去除它。
        ///
        /// 频率拿墙钟实测，不信 <c>Stopwatch.Frequency</c>；墙钟用 <see cref="DateTime.UtcNow"/>
        /// 而不是 Unity 的 API——这条路虽然在主线程上，但同一个错误这个仓库已经付过两次了。
        /// </summary>
        private static void NoteBatchCost(int built, int budget, int pending)
        {
            if (_batchBegan == 0L) return;

            if (pending > _worstPending) _worstPending = pending;

            // <b>积压这件事必须当场报一次，不能等窗口。</b> 上一局自测一行都没有，
            // 就是因为窗口要攒满 10 秒、而那一阵建造只持续了几秒——
            // <b>一个只在「攒够时间」才说话的探针，碰上短促的爆发就等于哑的</b>。
            // 所以再加一条按事件触发的：积压一旦过线就立刻说，整局一次。
            if (pending >= 200 && Interlocked.Exchange(ref _backlogWarned, 1) == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    $"作弊·建造秒完成：**预建物积压到了 {pending} 个**。秒建每次结算最多处理 " +
                    $"{budget} 个，而付不起料的会原样留着交回给建设机器人——" +
                    "**原版每 tick 都要把这一堆重扫一遍派机器人**，所以性能面板上「建设系统」和" +
                    "「伊卡洛斯」两栏的开销跟这个数成正比（那两栏不是本 mod 的代码）。" +
                    "堆积不消退就是料不够：先补料，或者别一直点。");

            long ticks = Stopwatch.GetTimestamp() - _batchBegan;

            _batchBegan = 0L;
            _batchTicks += ticks;
            _batchCount++;
            _batchBuilt += built;

            if (ticks > _worstTicks)
            {
                _worstTicks = ticks;
                _worstBuilt = built;
            }

            DateTime utcNow = DateTime.UtcNow;
            long stampNow = Stopwatch.GetTimestamp();

            if (_windowStamp == 0L)
            {
                _windowStamp = stampNow;
                _windowUtc = utcNow;

                return;
            }

            double wall = (utcNow - _windowUtc).TotalSeconds;

            if (wall < 10.0) return;

            if (_reports < 8)
            {
                _reports++;

                double measured = (stampNow - _windowStamp) / wall;
                double freq = measured > 0.0 ? measured : Stopwatch.Frequency;

                double worstMs = _worstTicks / freq * 1000.0;
                double avgMs = _batchCount > 0 ? _batchTicks / freq * 1000.0 / _batchCount : 0.0;
                double each = _worstBuilt > 0 ? worstMs / _worstBuilt : 0.0;

                double flattenMs = _flattenTicks / freq * 1000.0;

                ProjectEdenPlugin.Log.LogInfo(
                    $"作弊·建造秒完成·自测：过去 {wall:0.#} 秒里结算了 {_batchCount} 次、共建 {_batchBuilt} 座，" +
                    $"**最坏的一次 {worstMs:0.##} 毫秒建 {_worstBuilt} 座**（每座约 {each:0.###} 毫秒），" +
                    $"平均每次 {avgMs:0.##} 毫秒，当前上限 {budget} 座/次。" +
                    $"预建物积压最多 {_worstPending} 个、料不够而没建成 {_unpayable} 次。" +
                    $"**分段：召回机器人 {_recycleTicks / freq * 1000.0:0.##} 毫秒、" +
                    $"BuildFinally {_buildTicks / freq * 1000.0:0.##} 毫秒、" +
                    $"收尾四步 {_finishTicks / freq * 1000.0:0.##} 毫秒、" +
                    $"地形发布 {flattenMs:0.##} 毫秒（跑 {_flattenRuns} 次／跳过 {_flattenSkips} 次），" +
                    $"其余（扫描预建物池）{(_batchTicks - _recycleTicks - _buildTicks - _finishTicks) / freq * 1000.0:0.##} 毫秒。**" +
                    $"其中 RefreshStationTraffic 被调用 {TrafficCalls} 次、" +
                    $"合计 {TrafficTicks / freq * 1000.0:0.##} 毫秒" +
                    "（O(站点数²)：两个走满 stationCursor 的循环，第二个把整个 stationPool 传进 " +
                    $"RematchLocalPairs。本 mod 自己那些已改为限频合并——{StationTrafficCoalescer.Stats()}；" +
                    "剩下的是原版按自己时机调的，没碰）。" +
                    "四段里最大的那个就是这颗星球上秒建的真正代价。");
            }

            _worstTicks = 0;
            _worstBuilt = 0;
            _batchTicks = 0;
            _batchCount = 0;
            _batchBuilt = 0;
            _flattenTicks = 0;
            _flattenRuns = 0;
            _flattenSkips = 0;
            _unpayable = 0;
            _worstPending = 0;
            _recycleTicks = 0;
            _buildTicks = 0;
            _finishTicks = 0;
            TrafficTicks = 0;
            TrafficCalls = 0;
            _windowStamp = stampNow;
            _windowUtc = utcNow;
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

    /// <summary>
    /// 给 <see cref="PlanetTransport.RefreshStationTraffic"/> 计时。
    ///
    /// <b>它是「每放一座建筑 100 毫秒」这条线索上最后一个没被量过的嫌疑人，
    /// 而它的形状已经从 IL 上确定是 O(站点数²)</b>：66 条指令里两个走满
    /// <c>stationCursor</c> 的循环，第二个把整个 <c>stationPool</c> 传进
    /// <c>RematchLocalPairs</c>。它的调用点之一是
    /// <c>BuildingParameters::ApplyPrebuildParametersToEntity @1024</c>，
    /// 也就是放下一座带参数的建筑就整颗星球重配一次。
    ///
    /// 这个探针<b>不改任何行为</b>，只记时间和次数；它挂的不是 tick 路径
    /// （这个方法只在建造/拆除/改设置时被调），所以两个时间戳是免费的。
    /// </summary>
    [HarmonyPatch]
    internal static class StationTrafficProbe
    {
        [ThreadStatic] private static long _began;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.RefreshStationTraffic))]
        private static void Before() => _began = Stopwatch.GetTimestamp();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.RefreshStationTraffic))]
        private static void After()
        {
            if (_began == 0L) return;

            InstantBuildPatches.TrafficTicks += Stopwatch.GetTimestamp() - _began;
            InstantBuildPatches.TrafficCalls++;
            _began = 0L;
        }
    }
}
