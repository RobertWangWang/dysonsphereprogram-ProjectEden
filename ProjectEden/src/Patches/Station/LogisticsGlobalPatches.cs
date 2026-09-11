using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 物流类全局数值的覆盖：运输机 / 运输船运载量，以及集装（堆叠）层数。
    ///
    /// 这些都存在 GameHistoryData 里，由科技逐级累加并写进存档：
    ///   · logisticDroneCarries / logisticShipCarries —— 单次运载量
    ///   · inserterStackInput / inserterStackOutput  —— 分拣器堆叠层数（解锁函数 41 / 39）
    ///   · stationPilerLevel                          —— 物流塔集装层数（解锁函数 29）
    ///
    /// 全部是全局值，在每帧的读取方之前覆盖即可，不必改科技数据、也不动存档结构。
    /// </summary>
    [HarmonyPatch]
    internal static class LogisticsGlobalPatches
    {
        private static StationsConfig Config => ProjectEdenPlugin.StationsConfig;

        private static bool _reported;

        /// <summary>
        /// 读档后还欠一次分拣器刷新。<b>用 int + Interlocked 而不是 bool</b>：
        /// Apply() 挂在 PlanetTransport.GameTick 上，那是跨行星并行的（~31 个工作线程），
        /// 「先读后写」的 bool 会让好几个线程都判定该刷新。这是 CLAUDE.md 里
        /// 一次性日志那条同款陷阱。
        /// </summary>
        private static int _needPostLoadRefresh = 1;

        /// <summary>行星内物流：运输机运载量。</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void PlanetTransport_GameTick_Prefix() => Apply();

        /// <summary>星际物流：运输船运载量。</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GalacticTransport), nameof(GalacticTransport.GameTick))]
        private static void GalacticTransport_GameTick_Prefix() => Apply();

        private static void Apply()
        {
            if (Config == null) return;

            GameHistoryData history = GameMain.history;

            if (history == null) return;

            // 运载量按配置<b>对齐</b>，可升可降。
            //
            // 这两项和下面几项不一样：它们已经被写进存档了（GameHistoryData 里的字段），
            // 沿用「只升不降」的话，把配置从 10 万调回 1 万在老存档上<b>一点效果都没有</b>——
            // 存档里那个 10 万比配置大，Raise 直接跳过。想调低就必须真的写下去。
            // 覆盖玩家科技的风险可以忽略：原版这两个值靠科技也只到两位数，
            // 配置里随便填一个都远在其上，不存在「把玩家研究出来的数值压回去」。
            Align(ref history.logisticDroneCarries, Config.droneCarries, "运输机运载量");
            Align(ref history.logisticShipCarries, Config.shipCarries, "运输船运载量");

            // **这两项必须能降，所以用 Align 而不是 Raise。**
            //
            // 它们都存在 GameHistoryData 里、会进存档，而 Raise 只升不降：
            // 上一局写进去 5000，这一局就算算出来只该给 1，Raise 看到 5000 >= 1 直接跳过，
            // 新上限永远下不去——实测就是这么翻车的：日志一边打「按 1 垛生效」，
            // 一边把 history 里的 5000 刷进每台分拣器，货照吞。
            //
            // CLAUDE.md 早写过这条（运载量当初就是这么栽的），只是当时判断
            // 「集装/分拣没有调低的诉求」。分拣器上限现在是**推导出来的**、会随集装层数变，
            // 那个判断就不成立了。
            Align(ref history.stationPilerLevel, Config.stationPilerLevel, "物流塔集装层数");

            // 分拣器的堆叠层数还要同步到已建成的分拣器上：
            // InserterComponent.stackInput/stackOutput 是建造时从 history 取的，
            // OnInserterTechChange 正是原版在科技变化时用来刷新它们的入口。
            // 夹在 255：这两个值最终要塞进 InserterComponent 的 Byte 字段（带 conv.u1），
            // 超了不是"更快"，是回绕成一个更小的数。详见 PilerLevelPatches.InserterAbsoluteMax。
            int inserterMax = PilerLevelPatches.InserterTarget;

            bool inserterChanged = Set(ref history.inserterStackInput, inserterMax)
                                 | Set(ref history.inserterStackOutput, inserterMax);

            if (inserterChanged) GameMain.data?.OnInserterTechChange();

            // 读档之后必须再刷一次，即使 history 没变。
            //
            // 每台分拣器的 stackInput/stackOutput 是**存在存档里**的，而存档那两个字节被
            // 夹在了 255（格式故意不改，见 preloader 的 ClampByteWrites）。于是读档回来
            // 每台分拣器都是 255，而 history 里是真值 5000——Raise 看 history 没变化就跳过，
            // 刷新也就不会发生，表现为「配置写了 5000，分拣器还是 255」。
            // OnInserterTechChange 是原版自己的刷新入口，按 history 重写每台分拣器，跑一次就够。
            if (CargoWidening.InserterIsWide && Interlocked.Exchange(ref _needPostLoadRefresh, 0) == 1)
            {
                GameMain.data?.OnInserterTechChange();

                ProjectEdenPlugin.Log.LogInfo(
                    $"读档后已按 history 把分拣器堆叠层数刷回 {history.inserterStackInput}/{history.inserterStackOutput}" +
                    "（存档里每台存的是夹在 255 的副本）");
            }

            if (_reported) return;

            _reported = true;

            ProjectEdenPlugin.Log.LogInfo(
                $"物流全局值：运载量 运输机 {history.logisticDroneCarries} / 运输船 {history.logisticShipCarries}，" +
                $"堆叠 输入 {history.inserterStackInput} / 输出 {history.inserterStackOutput}，" +
                $"物流塔集装 {history.stationPilerLevel}");
        }

        /// <summary>
        /// 按配置对齐一个全局值，升降都写。第一次真的改动时报一行，好确认配置生效了。
        /// </summary>
        /// <summary>读档会把每台分拣器的层数换成存档里那份夹过的副本，所以要再欠一次刷新。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
        private static void GameData_Import_Postfix() => Interlocked.Exchange(ref _needPostLoadRefresh, 1);

        private static void Align(ref int field, int wanted, string label)
        {
            if (wanted <= 0 || field == wanted) return;

            if (!_reported)
                ProjectEdenPlugin.Log.LogInfo($"{label}由 {field} 调整为 {wanted}（按 stations.json）");

            field = wanted;
        }

        /// <summary>
        /// 直接抬高输出集装数量的实参。
        ///
        /// 集装层数有两条读取路径：GameLogic.FactoryTransportOutput 与 _station_output_parallel
        /// 都是自己读 GameMain.history.stationPilerLevel 再往下传，不经过 PlanetTransport.GameTick，
        /// 所以只覆盖 history 存在时序风险。这两条最终都汇到下面这两个方法，
        /// 在这里改实参最直接，不受调用顺序影响。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick_OutputToBelt))]
        private static void PlanetTransport_GameTick_OutputToBelt(ref int maxPilerCount)
        {
            int wanted = Config?.stationPilerLevel ?? 0;

            if (wanted > maxPilerCount) maxPilerCount = wanted;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.UpdateOutputSlots))]
        private static void StationComponent_UpdateOutputSlots(StationComponent __instance, ref int maxPilerCount)
        {
            int wanted = Config?.stationPilerLevel ?? 0;

            if (wanted <= 0) return;

            if (wanted > maxPilerCount) maxPilerCount = wanted;

            // pilerCount 非 0 时会盖过科技上限（面板上取消勾选「使用科技上限」就是这种情况），
            // 低于目标值时改回 0，让它跟随科技上限
            if (__instance.pilerCount != 0 && __instance.pilerCount < wanted) __instance.pilerCount = 0;
        }

        /// <summary>对齐到 wanted（可升可降），返回是否改动过。</summary>
        private static bool Set(ref int current, int wanted)
        {
            if (wanted <= 0 || current == wanted) return false;

            current = wanted;

            return true;
        }

        /// <summary>把目标值抬到 wanted，已经更高则不动。返回是否改动过。</summary>
        private static bool Raise(ref int current, int wanted)
        {
            if (wanted <= 0 || current >= wanted) return false;

            current = wanted;

            return true;
        }
    }
}
