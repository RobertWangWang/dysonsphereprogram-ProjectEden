using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 巨型建筑的物流站**不再每 12.8 帧空扫一整圈配对环**。
    ///
    /// <para><b>这不是一次等价改写，是一个有代价的设计决定，所以默认关着。</b></para>
    /// 它让巨型建筑的站点**彻底不再派出行星内运输机**——而那正是
    /// <see cref="MegaVirtualLogisticsPatches"/> 一直想达成的效果（它的开机日志原话就是
    /// 「储物格之间直接搬运，无人机不再起飞」）。区别只在于：以前是「扫一圈、发现没活、
    /// 走人」，现在是「压根不扫」。
    ///
    /// <para><b>量出来的账。</b></para>
    /// 一颗 6397 个站的星球上，「物流运输」那一栏每帧约 8 ms，其中 86.5% 是原版
    /// <c>InternalTickLocal</c>（劈半探针实测，四个窗口稳定在 13.3–13.6% 是本 mod 的）。
    /// 而那 86.5% 几乎全压在派机扫描上——
    /// <list type="bullet">
    /// <item>@00A3 的错帧闸让 12.8 帧里只有 1 帧进得去（原版自适应，实测平均 12.8）；</item>
    /// <item>其余 11.8 帧只做「充能 + 20 条局部初始化 + 推进在途无人机」，而
    /// 推进那段是 <c>for (i &lt; workDroneCount)</c>（@1DA1 的 blt），**0 架时整段不进**，
    /// 合计约 40 条指令；</item>
    /// <item>进得去的那 1 帧，扫描的退出条件是
    /// <c>do { … } while (start != localPairProcess)</c>——**找到活就早退，找不到活就走完整圈**。
    /// 巨型建筑恰好永远找不到活。</item>
    /// </list>
    /// 4632 台 × 60/12.8 ≈ **每秒两万一千次整圈空扫**。
    ///
    /// <para><b>跳过之后什么不会坏——这是枚举出来的，不是读着像。</b></para>
    /// 方法体里全部副作用只有四类（stelem 0 处）：
    /// <list type="number">
    /// <item><b>充能</b>（@0013 0026 0040 0054）。**逐字复现**，见 <see cref="Charge"/>。</item>
    /// <item><b>记账</b>：<c>_tmp_iter_local</c>、<c>droneStatusCursor</c>、
    /// <c>droneTaskInterval</c>。三个字段的**全部读取点都在 <c>InternalTickLocal</c> 自己体内**
    /// （外加 Export/Import/Init/Reset 和 DebugFactoryData），枚举所得。冻住它们对外零影响，
    /// 而且它们唯一的作用就是给我们正要跳过的那个扫描定节奏。</item>
    /// <item><b>派机</b>：<c>DroneData</c> / <c>LocalLogisticOrder</c> 的写入、
    /// <c>idleDroneCount</c>/<c>workDroneCount</c>、统计登记。**这就是要跳掉的东西本身。**</item>
    /// <item><b>推进在途无人机</b>（@1297–1DAE，含到货时的 <c>AddItem</c>/<c>TakeItem</c>/
    /// <c>NotifyDroneDelivery</c> 和数组压缩）。**只在 <c>workDroneCount == 0</c> 时才跳**——
    /// 否则已经飞在天上的运输机会永远停在半路。</item>
    /// </list>
    ///
    /// <para><b>三道守卫，少一道都不行。</b></para>
    /// <list type="bullet">
    /// <item><c>virtualLogistics</c> 必须开着——不开的话巨型建筑靠的就是无人机，
    /// 跳掉派机等于把它饿死。</item>
    /// <item><c>workDroneCount == 0</c>：上面第 4 条。功能刚打开时天上可能还有货，
    /// 这些站点这一帧照常走原版，等飞完了自然就开始跳。</item>
    /// <item>确实是巨型建筑的站点：判据沿用全仓库同一条
    /// （<c>assembler.speed &gt;= MegaSpeedThreshold</c>），不是 protoId。</item>
    /// </list>
    /// </summary>
    [HarmonyPatch]
    internal static class MegaStationTickSkipPatches
    {
        private static long _skipped;
        private static long _ran;
        private static float _nextReport;
        private static int _logged;

        private static bool Enabled =>
            ProjectEdenPlugin.StationsConfig != null
            && ProjectEdenPlugin.StationsConfig.skipIdleMegaStationTick
            && MegaBuildingRegistry.Config != null
            && MegaBuildingRegistry.Config.virtualLogistics;

        /// <summary>
        /// 形参名必须和原版声明的一致（Harmony 按名字注入）：原版签名是
        /// <c>(PlanetFactory factory, Int32 timeGene, Single power, Single droneSpeed,
        /// Int32 droneCarries, StationComponent[] stationPool)</c>。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.InternalTickLocal))]
        private static bool SkipIdleMega(StationComponent __instance, PlanetFactory factory, float power)
        {
            if (!Enabled) return true;

            // **两个计数都在前置里数，不用后置。**
            // Harmony 的后置在前置返回 false 之后**照样会跑**，拿它数「照常跑了多少」
            // 会把跳过的那些也算进去，比例就是假的——而一个不可能变小的比例不是测量。
            //
            // 没有这一对计数，「开着但一次没跳」和「压根没接上」在日志里长得一模一样
            //（本仓库记过七次的那条）。

            // 天上还有货就照常走原版——@1297 那一段是唯一能把它们送到的地方
            if (__instance.workDroneCount > 0)
            {
                Interlocked.Increment(ref _ran);

                return true;
            }

            if (!IsMegaStation(__instance, factory))
            {
                Interlocked.Increment(ref _ran);

                return true;
            }

            Charge(__instance, power);

            Interlocked.Increment(ref _skipped);

            return false;
        }

        /// <summary>
        /// 原版 @0000–0058 的充能，**逐条对着 IL 抄的**：
        /// <code>
        /// energy += (long)(int)((float)energyPerTick * power);
        /// energy -= 1000;
        /// if (energy > energyMax) energy = energyMax; else if (energy &lt; 0) energy = 0;
        /// </code>
        /// 那个 <c>conv.r4 … mul … conv.i4 … conv.i8</c> 的来回不是多余的：
        /// 先截成 Int32 再扩回 Int64，和直接乘的结果在大数上不一样，所以照抄。
        /// </summary>
        private static void Charge(StationComponent s, float power)
        {
            long e = s.energy + (long)(int)(s.energyPerTick * power);

            e -= 1000;

            if (e > s.energyMax) e = s.energyMax;
            else if (e < 0) e = 0;

            s.energy = e;
        }

        /// <summary>
        /// 判据沿用全仓库同一条：<b>挂着的那台装配机速度够不够</b>，不是 protoId。
        /// 和 <see cref="MegaVirtualLogisticsPatches"/> 里那份保持一致——两边用不同判据
        /// 就会出现「虚拟物流认它、跳过不认它」这种谁也想不到的半开状态。
        /// </summary>
        private static bool IsMegaStation(StationComponent station, PlanetFactory factory)
        {
            if (factory == null || station.storage == null) return false;

            int entityId = station.entityId;

            if (entityId <= 0 || entityId >= factory.entityPool.Length) return false;

            int assemblerId = factory.entityPool[entityId].assemblerId;

            if (assemblerId <= 0) return false;

            AssemblerComponent[] pool = factory.factorySystem.assemblerPool;

            return assemblerId < pool.Length
                   && pool[assemblerId].id == assemblerId
                   && pool[assemblerId].speed >= MegaBuildingRegistry.MegaSpeedThreshold;
        }

        /// <summary>
        /// 自己挂一个主线程的挂点报表，**不搭别人的车**：上一轮把劈半探针的报表写在
        /// CPU 探针的结尾，结果那边一早退它就跟着沉默了——两个独立的东西不该共享失败路径。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate_SkipStat()
        {
            Tick(UnityEngine.Time.realtimeSinceStartup);
        }

        /// <summary>
        /// 每 60 秒报一次增量。计数发生在 ~31 个工作线程上（所以用 <c>Interlocked</c>），
        /// 报表在主线程。
        /// </summary>
        private static void Tick(float now)
        {
            if (!Enabled) return;

            if (_nextReport <= 0f)
            {
                _nextReport = now + 60f;

                return;
            }

            if (now < _nextReport) return;

            _nextReport = now + 60f;

            long skipped = Interlocked.Exchange(ref _skipped, 0);
            long ran = Interlocked.Exchange(ref _ran, 0);

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型建筑物流站·跳过空扫：过去 60 秒跳过 {skipped} 次、照常跑 {ran} 次"
                + $"（{(skipped + ran > 0 ? skipped * 100.0 / (skipped + ran) : 0):0.0}%）。"
                + "跳掉的每一次都省下一整圈配对环空扫，落在性能面板的「物流运输」一栏。"
                + "**跳过 0 次**就说明守卫没过：要么虚拟物流没开，要么这些站点天上还有货。");
        }

        internal static void Report()
        {
            if (ProjectEdenPlugin.StationsConfig == null)
            {
                ProjectEdenPlugin.Log.LogWarning("巨型建筑物流站·跳过空扫：读不到 stations.json，未启用");

                return;
            }

            if (!ProjectEdenPlugin.StationsConfig.skipIdleMegaStationTick)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "巨型建筑物流站·跳过空扫：已关闭（默认值）。打开它，巨型建筑的站点会**彻底不再派出"
                    + "行星内运输机**——那本来就是虚拟物流想达成的效果，区别只是从「扫一圈发现没活」"
                    + "变成「压根不扫」。实测一颗 6397 站的星球上，那一圈空扫占「物流运输」的大头。"
                    + "开关在 stations.json 的 skipIdleMegaStationTick");

                return;
            }

            if (MegaBuildingRegistry.Config == null || !MegaBuildingRegistry.Config.virtualLogistics)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "巨型建筑物流站·跳过空扫：开关开着，但虚拟物流是关的，所以**不会生效**。"
                    + "两者必须同时开——虚拟物流不开的话，巨型建筑靠的就是无人机，跳掉派机等于把它饿死");

                return;
            }

            if (Interlocked.Exchange(ref _logged, 1) == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    "巨型建筑物流站·跳过空扫：**已开启**。巨型建筑的站点不再派出行星内运输机"
                    + "（货由虚拟物流直接搬）；充能照旧，天上已有的运输机会照常飞完再开始跳。"
                    + "每 60 秒报一次跳过比例");
        }
    }
}
