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

        /// <summary>
        /// 两条基础速度日志各自只报一次。<b>两个飞机各一个标志，不共用一个</b>——
        /// 共用的话先跑的那个会把后跑的那行吃掉，而「另一架改了没有」恰恰是要看的。
        /// Apply 跑在跨行星并行的 tick 上，所以用 Interlocked 认领。
        /// </summary>
        private static int _droneSpeedReported, _courierSpeedReported;

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

            // 配送运输机（送到机甲手上那种）的单次运载量。和上面两项同一个性质：
            // 存在 GameHistoryData 里、由科技累加（UnlockTechFunction @0525 是读-加-写）、进存档，
            // 所以同样用 Align 双向对齐。原版基础值是 5（ModeConfig..ctor @01A7）。
            Align(ref history.logisticCourierCarries, Config.courierCarries, "配送运输机运载量");

            ApplySpeeds(history);

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
                $"物流全局值：运载量 运输机 {history.logisticDroneCarries} / 运输船 {history.logisticShipCarries}" +
                $" / 配送机 {history.logisticCourierCarries}，" +
                $"速度 运输机 {history.logisticDroneSpeedModified:0.##}" +
                $" / 配送机 {history.logisticCourierSpeedModified:0.##}（都是基础 × 科技倍率），" +
                $"堆叠 输入 {history.inserterStackInput} / 输出 {history.inserterStackOutput}，" +
                $"物流塔集装 {history.stationPilerLevel}");
        }

        /// <summary>
        /// 两架飞机的基础速度。<b>为什么动基础值而不是科技那层倍率，是所有者选的</b>：
        /// 两个字段最终都乘进 <c>…SpeedModified</c>（= 基础 × Scale），而科技只写 Scale，
        /// 所以改基础值不和科技打架。细节和取值方式见 <see cref="ApplySpeed"/>。
        /// </summary>
        private static void ApplySpeeds(GameHistoryData history)
        {
            // 原版基础值活取自游戏模式配置——GameHistoryData.SetForNewGame 自己就是从这里取的
            // （运输机 @01F6–0200，配送机 @0210–0276）。写死数字会在游戏更新改了它之后静默失准。
            ModeConfig mode = Configs.freeMode;

            if (mode == null) return;

            ApplySpeed(ref history.logisticDroneSpeed, mode.logisticDroneSpeed,
                Config.droneSpeedMultiplier, history.logisticDroneSpeedScale,
                "运输机", ref _droneSpeedReported);

            ApplySpeed(ref history.logisticCourierSpeed, mode.logisticCourierSpeed,
                Config.courierSpeedMultiplier, history.logisticCourierSpeedScale,
                "配送运输机", ref _courierSpeedReported);
        }

        /// <summary>
        /// 一架飞机的基础速度：<b>按「原版基础值 × 倍率」写绝对值，绝不在现值上乘</b>。
        ///
        /// 运输机和配送运输机<b>结构完全一样</b>，所以共用这一个函数——同一段推理抄两遍，
        /// 早晚会有一份跟不上另一份。两边都枚举过写入点：
        ///
        /// <list type="bullet">
        /// <item><c>logisticDroneSpeed</c>：只有 <c>SetForNewGame</c> / <c>Import</c> 写；
        /// 科技走 <c>UnlockTechFunction</c> @0345，写的是 <c>logisticDroneSpeedScale</c>。</item>
        /// <item><c>logisticCourierSpeed</c>：同样只有 <c>SetForNewGame</c> / <c>Import</c> 写；
        /// 科技走 @0512，写的是 <c>logisticCourierSpeedScale</c>。</item>
        /// </list>
        ///
        /// 也就是说<b>科技从不碰基础值</b>，所以改基础值和科技不会打架。
        /// 代价是往后每一级速度科技的收益也跟着放大了——它加成的基数变大了。
        ///
        /// <b>为什么算绝对值。</b> 这两个字段都<b>进存档</b>
        /// （<c>Export</c> @0345 / @03A5）。在现值上乘一次倍率的话，第二局就是 100 倍、
        /// 第三局 1000 倍，而且一个字都不报。算绝对值跑多少次都是同一个数。
        /// </summary>
        private static void ApplySpeed(ref float field, float baseSpeed, float mult, float scale,
            string label, ref int reported)
        {
            // 0 或负数 = 保持原版，和其它几个旋钮一个口径
            if (mult <= 0f || baseSpeed <= 0f) return;

            float target = baseSpeed * mult;

            // 浮点数不比相等：连续写同一个值时的舍入会让「变了没有」一直为真，
            // 那条一次性日志就会变成每 tick 一行。给一个相对容差。
            if (System.Math.Abs(field - target) <= target * 1e-6f) return;

            float before = field;

            field = target;

            if (Interlocked.Exchange(ref reported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"{label}基础速度：{before:0.##} → {target:0.##}（原版基础 {baseSpeed:0.##} × {mult:0.##}）。" +
                $"实际速度还要再乘科技倍率 {scale:0.##}，也就是 {target * scale:0.##}。" +
                "科技只写倍率那一层、不碰基础值，所以两边不会打架。");
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
