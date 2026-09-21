// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Text;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 本星球实体普查——把逻辑帧的耗时分项<b>翻译成可以动手的对象数量</b>。
    ///
    /// <b>为什么要有它。</b> 游戏自带的性能面板（统计面板 → 性能测试）会告诉你
    /// 「生产设施 12.94 ms、传送带附属设施 7.08 ms」，但不会告诉你那 12.94 ms 是
    /// 一千台装配机、三千台采矿机还是研究站堆出来的——而这三者要采取的行动完全不同。
    /// 这张普查按<b>性能面板自己的分项</b>列出对象数，两边对着看一遍就知道该动哪里。
    ///
    /// 这是本仓库自己的老规矩：<b>画面说不通的时候去枚举对象，别再重读你已经相信的
    /// 那条路径。</b>（<c>ModelRenderCensus</c> 当年就是这么结束九轮的；
    /// <c>MultiProductUIPatches.DumpBox</c> 也是。）这一次的代价是：先猜「分拣器和
    /// 传送带通常占七成」，实测是 <b>1%</b>——那颗星球几乎没有皮带和爪子。
    /// 拿一般规律代替测量，这是第二次了。
    ///
    /// <b>分项和引擎的对应关系是实测的</b>，不是按名字猜的。
    /// <c>GameLogic.ContextCollect_FactoryComponents_MultiMain</c> 里每个
    /// <c>ScatterTaskContext</c> 各自 <c>ResetFrame</c> 一次，而
    /// <c>FactoryCargoTrafficMiscGameTick_Parallel</c> 的方法体里就四个调用：
    /// piler、monitor、spraycoater、station_output——所以「传送带附属设施」这一栏
    /// 里<b>没有传送带</b>，它是集装机 + 流速计 + 喷涂机 + 物流站输出格。
    ///
    /// <b>顺带记一条这张表存在的最大理由</b>：并行的工作项是<b>一整颗星球</b>
    /// （每个 <c>ResetFrame</c> 的工作项数都是 <c>GameLogic.factoryCount</c>，
    /// 而 <c>_inserter_parallel</c> 取到工作项之后是
    /// <c>factories[...]</c> 再整个 <c>inserterCursor</c> 跑完）。所以一颗星球的
    /// 逻辑帧<b>加核心没有用</b>，唯一的两条路是「这颗星球上的对象更少」和
    /// 「把工厂摊到更多星球」。这张表回答的就是前一条。
    /// </summary>
    [HarmonyPatch]
    internal static class PlanetCensus
    {
        /// <summary>多久对一次表。比对本身只读十几个 cursor，很便宜。</summary>
        private const float PollSeconds = 10f;

        private static float _nextPoll;
        private static int _lastPlanet = -1;
        private static long _lastSignature = -1;
        private static int _entered;

        /// <summary>
        /// 挂在 <c>UIGame._OnUpdate</c> 上，因为它<b>在主线程</b>：这里要读
        /// <c>Time.realtimeSinceStartup</c>，而它在工作线程上不可用。
        ///
        /// 节流用 <c>realtimeSinceStartup</c> 而不是 <c>GameMain.gameTick</c>——
        /// 后者在换存档时会倒退，于是 <c>next = tick + 间隔</c> 永远不再到期、
        /// 报告静默死掉，而那看起来和「一切正常」一模一样（本仓库第 4 号坑）。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate_Census()
        {
            if (System.Threading.Interlocked.Exchange(ref _entered, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    "本星球普查：挂点已跑到（UIGame._OnUpdate 的后置确实接上了）。"
                    + $"每 {PollSeconds:0} 秒对一次表，**只有换星球或者数量真的变了才会再报一行**。");

            float now = UnityEngine.Time.realtimeSinceStartup;

            if (now < _nextPoll) return;

            _nextPoll = now + PollSeconds;

            PlanetFactory factory = GameMain.localPlanet?.factory;

            if (factory == null)
            {
                // 星球没加载（在星际、在星图、在主菜单）。**照样把状态说清楚**，
                // 否则「不在星球上」和「普查坏了」在日志里长得一模一样。
                if (_lastPlanet == -1) return;

                _lastPlanet = -1;
                _lastSignature = -1;

                ProjectEdenPlugin.Log.LogInfo("本星球普查：已离开星球，下次落地再报。");

                return;
            }

            int planetId = GameMain.localPlanet.id;
            long signature = Signature(factory);

            // 数没变就不刷屏——但换了星球一定重报，哪怕数字碰巧一样。
            if (planetId == _lastPlanet && signature == _lastSignature) return;

            _lastPlanet = planetId;
            _lastSignature = signature;

            Dump(factory);
        }

        /// <summary>
        /// 便宜的变化探测：只读各个池子的游标，不扫池子本身。
        /// 乘上不同的质数是为了让「装配机 +1、采矿机 −1」这种抵消不掉的组合也能被看见。
        /// </summary>
        private static long Signature(PlanetFactory factory)
        {
            FactorySystem fs = factory.factorySystem;
            CargoTraffic ct = factory.cargoTraffic;
            PlanetTransport tp = factory.transport;

            long sig = factory.entityCursor;

            if (fs != null)
                sig = sig * 31 + fs.minerCursor
                    + fs.assemblerCursor * 7L + fs.labCursor * 13L + fs.inserterCursor * 17L
                    + fs.fractionatorCursor * 19L + fs.ejectorCursor * 23L + fs.siloCursor * 29L;

            if (ct != null)
                sig = sig * 31 + ct.pathCursor
                    + ct.beltCursor * 7L + ct.pilerCursor * 13L + ct.monitorCursor * 17L
                    + ct.spraycoaterCursor * 19L + ct.splitterCursor * 23L;

            if (tp != null) sig = sig * 31 + tp.stationCursor + tp.dispenserCursor * 7L;

            return sig;
        }

        /// <summary>
        /// 按<b>性能面板的分项</b>列出对象数。
        ///
        /// 扫池子只在真的要打印的时候做（换星球 / 数变了），所以几千个条目的遍历
        /// 每隔很久才发生一次，而且在主线程的 UI 更新里，不碰 tick 路径。
        /// </summary>
        private static void Dump(PlanetFactory factory)
        {
            FactorySystem fs = factory.factorySystem;
            CargoTraffic ct = factory.cargoTraffic;
            PlanetTransport tp = factory.transport;
            PowerSystem ps = factory.powerSystem;

            var sb = new StringBuilder(1024);

            sb.Append("本星球普查（").Append(GameMain.localPlanet?.displayName ?? "?")
              .Append("，实体 ").Append(factory.entityCursor - 1).Append(" 个）")
              .Append("——分项与「统计面板 → 性能测试」一一对应：");

            // ── 生产设施 Facilities ──────────────────────────────
            //
            // miner / assembler / fractionator / ejector / silo / labProduce 六个
            // ScatterTaskContext 都在这一栏里（ContextCollect_FactoryComponents_MultiMain）。
            if (fs != null)
            {
                int miners = Count(fs.minerCursor);
                int assemblers = Count(fs.assemblerCursor);
                int labs = Count(fs.labCursor);

                long veinRefs = SumMinerVeins(fs);
                int mega = CountMega(fs);
                int research = CountResearchLabs(fs);

                sb.Append("\n  【生产设施】采矿机 ").Append(miners);

                // **采矿机的真正成本是它覆盖的矿脉数，不是台数。**
                // MinerComponent.InternalUpdate 的 Vein 分支是逐条走 veins[] 的，
                // 而产量表达式里 veinCount 也是乘在里面的那一项。
                if (miners > 0)
                    sb.Append("（覆盖矿脉 ").Append(veinRefs)
                      .Append(" 条，平均 ").Append((veinRefs / (double)miners).ToString("0.#"))
                      .Append(" 条/台 —— 内层循环次数看这个，不是台数）");

                sb.Append("、装配机 ").Append(assemblers);

                if (mega > 0)
                {
                    // **这里曾经直接拿 cyclesPerTick 乘台数，报大了 3 到 6 倍。**
                    // 原版 InternalUpdate 按 recipeType 还有一道产出闸（装配 10 / 其余 20 /
                    // 冶炼约 100÷单次产量），实际上限是 min(cyclesPerTick, 闸)——
                    // 十六座巨型建筑里只有冶铸熔炉一座真能跑到 60。
                    // 现在逐台按它自己的配方类型算，而不是拿一个配置值乘台数。
                    long equiv = SumMegaCycles(fs);

                    sb.Append("（其中巨型 ").Append(mega)
                      .Append(" 台，按各自配方类型的产出闸逐台算，合计每 tick 最多 ").Append(equiv)
                      .Append(" 个周期，即相当于 ").Append(equiv)
                      .Append(" 台普通装配机）");
                }

                sb.Append("、研究站 ").Append(labs)
                  .Append("（研究模式 ").Append(research)
                  .Append(" / 产出模式 ").Append(labs - research).Append("）")
                  .Append("、分馏 ").Append(Count(fs.fractionatorCursor))
                  .Append("、弹射 ").Append(Count(fs.ejectorCursor))
                  .Append("、发射井 ").Append(Count(fs.siloCursor));
            }

            // ── 传送带附属设施 CargoTrafficMisc ───────────────────
            //
            // **这一栏里没有传送带。** FactoryCargoTrafficMiscGameTick_Parallel 的
            // 方法体里只有四个调用：piler、monitor、spraycoater、station_output。
            if (ct != null || tp != null)
            {
                sb.Append("\n  【传送带附属设施】集装机 ").Append(Count(ct?.pilerCursor ?? 1))
                  .Append("、流速计 ").Append(Count(ct?.monitorCursor ?? 1))
                  .Append("、喷涂机 ").Append(Count(ct?.spraycoaterCursor ?? 1));

                int stations = Count(tp?.stationCursor ?? 1);

                if (stations > 0)
                {
                    // UpdateOutputSlots 的后半段是 storage × slots 的嵌套循环
                    // （IL 0260–03E5），所以 30 格物流站在这一栏里是原版 6 格的 5 倍。
                    int kinds = MeasuredSlots(tp);

                    sb.Append("、物流站输出格 ").Append(stations).Append(" 站 × 最多 ")
                      .Append(kinds).Append(" 格（原版 6 格；UpdateOutputSlots 后半段是 ")
                      .Append("storage × slots 的嵌套循环，所以这一栏的开销跟格数成正比）");

                    AppendStationBreakdown(sb, factory, tp);
                }
            }

            // ── 其余分项 ──────────────────────────────────────────
            if (fs != null)
                sb.Append("\n  【分拣器】").Append(Count(fs.inserterCursor));

            if (ct != null)
                sb.Append("\n  【传送带】路径 ").Append(Count(ct.pathCursor))
                  .Append("、带体 ").Append(Count(ct.beltCursor))
                  .Append("、货物 ").Append(ct.container?.cursor ?? 0).Append(" 件")
                  .Append("、分流器 ").Append(Count(ct.splitterCursor)).Append("（分流器单独一栏）");

            if (tp != null)
                sb.Append("\n  【物流运输】物流站 ").Append(Count(tp.stationCursor))
                  .Append("、配送器 ").Append(Count(tp.dispenserCursor))
                  .Append("（每站每 tick 无条件走一遍 InternalTickLocal，2600 条指令）");

            if (ps != null)
                sb.Append("\n  【电力系统】发电 ").Append(Count(ps.genCursor))
                  .Append("、用电 ").Append(Count(ps.consumerCursor))
                  .Append("、节点 ").Append(Count(ps.nodeCursor))
                  .Append("、蓄电 ").Append(Count(ps.accCursor))
                  .Append("、枢纽 ").Append(Count(ps.excCursor));

            sb.Append("\n  用法：把这些数和性能面板的毫秒数对着看。**一颗星球是一个并行工作项**")
              .Append("（每个 ScatterTaskContext.ResetFrame 的工作项数都是 factoryCount），")
              .Append("所以单颗星球的逻辑帧加核心没用，只有「这颗星球上的对象更少」")
              .Append("和「把工厂摊到更多星球」两条路。");

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>池子的 0 号位是空的，游标本身是「下一个可用位」，所以实数是 cursor − 1。</summary>
        private static int Count(int cursor) => cursor > 1 ? cursor - 1 : 0;

        /// <summary>
        /// 把物流站按<b>归属</b>拆开，并算出 <c>UpdateOutputSlots</c> 内层循环的<b>准确次数</b>。
        ///
        /// <b>为什么要拆。</b>「传送带附属设施」那一栏实测 7 ms，而它只有四样东西，
        /// 其中集装机／流速计／喷涂机全是 0——所以那 7 ms 整个是
        /// <c>StationComponent.UpdateOutputSlots</c>。它的后半段是
        /// <c>storage × slots</c> 的嵌套循环（IL 0260–03E5），于是「一共迭代多少次」
        /// 才是能和毫秒数对上的量，而<b>站数和格数都不是</b>：一台只采一种矿的
        /// 小型速采机和一座 30 格的巨型建筑，在这个循环里差着一个数量级。
        ///
        /// <b>归属按事实判定，不按 protoId、更不按名字。</b> 站点所在实体上挂没挂
        /// 一台「速度够得上巨型」的组装机（<c>entityPool[entityId].assemblerId</c> →
        /// <c>assemblerPool[...].speed >= megaSpeedThreshold</c>），这就是全仓库
        /// 唯一那个判据；采集类则读 <c>isVeinCollector</c> / <c>isCollector</c>，
        /// 那两个标志<b>就是</b>「它是不是采集站」这件事本身。
        /// </summary>
        private static void AppendStationBreakdown(StringBuilder sb, PlanetFactory factory, PlanetTransport tp)
        {
            StationComponent[] pool = tp.stationPool;
            EntityData[] entities = factory.entityPool;
            AssemblerComponent[] asm = factory.factorySystem?.assemblerPool;

            if (pool == null || entities == null) return;

            int threshold = MegaBuildingRegistry.MegaSpeedThreshold;

            // 0 = 巨型建筑、1 = 采集类（采矿机／采集器）、2 = 其余物流站
            var count = new int[3];
            var slotSum = new long[3];
            var iterSum = new long[3];

            for (var i = 1; i < tp.stationCursor && i < pool.Length; i++)
            {
                StationComponent st = pool[i];

                if (st == null || st.id != i || st.storage == null) continue;

                int storageLen = st.storage.Length;
                int portLen = st.slots?.Length ?? 0;

                var kind = 2;

                if (st.isVeinCollector || st.isCollector)
                {
                    kind = 1;
                }
                else if (asm != null && st.entityId > 0 && st.entityId < entities.Length)
                {
                    int aid = entities[st.entityId].assemblerId;

                    if (aid > 0 && aid < asm.Length && asm[aid].speed >= threshold) kind = 0;
                }

                count[kind]++;
                slotSum[kind] += storageLen;
                iterSum[kind] += (long)storageLen * portLen;
            }

            long totalIter = iterSum[0] + iterSum[1] + iterSum[2];

            sb.Append("\n    ├ 按归属拆开（括号里是 storage × slots，也就是 UpdateOutputSlots 内层循环的次数/tick）：");

            AppendKind(sb, "巨型建筑", count[0], slotSum[0], iterSum[0], totalIter);
            AppendKind(sb, "采矿机/采集器", count[1], slotSum[1], iterSum[1], totalIter);
            AppendKind(sb, "其余物流站", count[2], slotSum[2], iterSum[2], totalIter);

            sb.Append("\n    └ 合计 ").Append(totalIter).Append(" 次/tick。")
              .Append("**占比最大的那一类就是这 7 ms 的主人。** 如果是采集类，那多半是白扔的")
              .Append("（它只采一种矿，用不到 30 格）；如果是巨型建筑，那些格子是真要用的，")
              .Append("省不掉，只能另想办法。");
        }

        private static void AppendKind(StringBuilder sb, string name, int count, long slots, long iter, long total)
        {
            if (count <= 0)
            {
                sb.Append("\n    │  ").Append(name).Append("：0 站");

                return;
            }

            sb.Append("\n    │  ").Append(name).Append("：").Append(count).Append(" 站，")
              .Append("平均 ").Append((slots / (double)count).ToString("0.#")).Append(" 格，")
              .Append("迭代 ").Append(iter).Append(" 次");

            if (total > 0) sb.Append("（占 ").Append((100.0 * iter / total).ToString("0.#")).Append("%）");
        }

        /// <summary>
        /// 所有采矿机覆盖的矿脉总数——<c>MinerComponent.InternalUpdate</c> 的
        /// Vein 分支逐条走 <c>veins[]</c>，所以这个数才是内层循环的次数。
        /// 台数相同而矿脉数差十倍的两颗星球，耗时也差十倍。
        /// </summary>
        private static long SumMinerVeins(FactorySystem fs)
        {
            MinerComponent[] pool = fs.minerPool;

            if (pool == null) return 0L;

            var sum = 0L;

            for (var i = 1; i < fs.minerCursor && i < pool.Length; i++)
            {
                if (pool[i].id != i) continue;

                int[] veins = pool[i].veins;

                if (veins != null) sum += veins.Length;
            }

            return sum;
        }

        /// <summary>
        /// 巨型建筑台数。判据沿用全仓库唯一那个——<c>speed >= megaSpeedThreshold</c>，
        /// <b>不是 protoId</b>：速度阈值和 assemblerSpeed 是解耦的，调速度不影响识别，
        /// 而按 protoId 数会漏掉 GenesisBook 的那些。
        /// </summary>
        /// <summary>
        /// 巨型建筑每 tick 真正能结算多少个周期，<b>逐台按它自己的配方类型算</b>。
        ///
        /// 上限是 <c>min(cyclesPerTick, 原版产出闸)</c>，而那个闸在
        /// <c>AssemblerComponent.InternalUpdate</c> @0138–0184 按 <c>recipeType</c> 三分：
        /// 冶炼 <c>produced + counts &lt;= 100</c>（上限约 100 ÷ 单次产量）、
        /// 装配 <c>produced &lt;= counts × 9</c>（10）、其余 <c>× 19</c>（20）。
        ///
        /// <see cref="MegaOutputGatePatches"/> 把后两档抬到了 <c>cyclesPerTick</c>，
        /// 所以这里读的是<b>抬完之后</b>的值——两边共用 <c>Scale</c>，
        /// 不会出现「改了闸门、普查还按老数报」那种两个数各说各话。
        /// </summary>
        private static long SumMegaCycles(FactorySystem fs)
        {
            AssemblerComponent[] pool = fs.assemblerPool;

            if (pool == null) return 0;

            int threshold = MegaBuildingRegistry.MegaSpeedThreshold;
            int cycles = MegaBuildingRegistry.Config?.cyclesPerTick ?? 1;

            if (cycles < 1) cycles = 1;

            var sum = 0L;

            for (var i = 1; i < fs.assemblerCursor && i < pool.Length; i++)
            {
                if (pool[i].id != i || pool[i].speed < threshold) continue;

                RecipeExecuteData data = pool[i].recipeExecuteData;

                // 没选配方的按 0 算：它这一 tick 一个周期也结算不了
                if (data?.productCounts == null || data.productCounts.Length == 0) continue;

                int counts = data.productCounts[0];

                if (counts < 1) counts = 1;

                int gate;

                if (pool[i].recipeType == ERecipeType.Smelt)
                {
                    // 加法闸：produced + counts <= 100，即最多 100 ÷ counts 个周期。
                    // 这一档 MegaOutputGatePatches 故意没动（理由见那里）。
                    gate = 100 / counts;
                }
                else
                {
                    // 乘法闸：produced <= counts × K，即最多 K + 1 个周期。
                    // K 取抬过之后的值，和转译器读同一个函数。
                    int vanilla = pool[i].recipeType == ERecipeType.Assemble ? 9 : 19;

                    gate = MegaOutputGatePatches.Scale(vanilla, ref pool[i]) + 1;
                }

                sum += gate < cycles ? gate : cycles;
            }

            return sum;
        }

        private static int CountMega(FactorySystem fs)
        {
            AssemblerComponent[] pool = fs.assemblerPool;

            if (pool == null) return 0;

            int threshold = MegaBuildingRegistry.MegaSpeedThreshold;
            var n = 0;

            for (var i = 1; i < fs.assemblerCursor && i < pool.Length; i++)
                if (pool[i].id == i && pool[i].speed >= threshold)
                    n++;

            return n;
        }

        /// <summary>
        /// 研究模式的研究站台数。两种模式走<b>完全不同的代码路径</b>
        /// （<c>InternalUpdateResearch</c> 只读 <c>GameHistoryData.techSpeed</c>，
        /// <c>InternalUpdateAssemble</c> 走 <c>speed</c>／<c>speedOverride</c>），
        /// 而且只有产出模式在 <c>_lab_produce_parallel</c> 里，所以分开数才对得上分项。
        /// </summary>
        private static int CountResearchLabs(FactorySystem fs)
        {
            LabComponent[] pool = fs.labPool;

            if (pool == null) return 0;

            var n = 0;

            for (var i = 1; i < fs.labCursor && i < pool.Length; i++)
                if (pool[i].id == i && pool[i].researchMode)
                    n++;

            return n;
        }

        /// <summary>
        /// 物流站实际的格数，<b>从活着的站点上量</b>，不读配置。
        ///
        /// 配置说的是「新建的站点会有几格」，而 <c>storage</c> 是建造那一刻烘进存档的
        /// （1 号坑），老站点要靠 <c>GameData.Import</c> 那一趟补。所以这里要的是
        /// <b>末态</b>——配置改没改成、老站点补没补上，量出来才算数。
        ///
        /// 取<b>最大值</b>而不是第一个：采集器和采矿机的格数本来就比物流站少，
        /// 碰巧扫到一台就会把结论带偏。
        /// </summary>
        private static int MeasuredSlots(PlanetTransport tp)
        {
            StationComponent[] pool = tp.stationPool;

            if (pool == null) return 0;

            var max = 0;

            for (var i = 1; i < tp.stationCursor && i < pool.Length; i++)
            {
                StationComponent st = pool[i];

                if (st == null || st.id != i || st.storage == null) continue;

                if (st.storage.Length > max) max = st.storage.Length;
            }

            return max;
        }

        /// <summary>
        /// 开机状态行：普查<b>接上了没有</b>。
        ///
        /// 读的是 <c>Harmony.GetAllPatchedMethods()</c>——<b>已生效的状态</b>，
        /// 而不是「我调了 PatchAll 而且没抛异常」。必须排在 PatchAll 之后。
        /// </summary>
        internal static void Report()
        {
            var hooked = false;

            foreach (System.Reflection.MethodBase mb in Harmony.GetAllPatchedMethods())
                if (mb?.DeclaringType == typeof(UIGame) && mb.Name == "_OnUpdate")
                {
                    hooked = true;

                    break;
                }

            if (hooked)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"本星球普查：已启用，落地后每 {PollSeconds:0} 秒对一次表，"
                    + "换星球或数量变化才报一行。它把性能面板的耗时分项翻译成对象数量，"
                    + "两边对着看就知道该动哪里。");

                return;
            }

            ProjectEdenPlugin.Log.LogWarning(
                "本星球普查：UIGame._OnUpdate 的后置没挂上，这张表整局都不会出现。");
        }
    }
}
