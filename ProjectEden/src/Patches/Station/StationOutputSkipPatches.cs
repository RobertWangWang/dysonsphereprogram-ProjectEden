// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 没有输出口的物流站，跳过整个 <c>StationComponent.UpdateOutputSlots</c>。
    ///
    /// <b>这是本仓库第一条「纯性能、零行为变化」的补丁，所以等价性必须是证明出来的，
    /// 不是论证出来的。</b>
    ///
    /// <b>为什么值得做。</b> 实测一颗星球：性能面板的「传送带附属设施」7.075 ms，
    /// 占逻辑帧 28%。那一栏只有四样东西（集装机 / 流速计 / 喷涂机 / 物流站输出格），
    /// 而前三样在那颗星球上<b>全是 0</b>——所以 7.075 ms 整个是这一个方法。
    /// 它的后半段是 <c>storage × slots</c> 的嵌套循环（IL 0260–03E5），而本 mod 把
    /// 物流站扩到了 30 格，于是每站每 tick 是 30 × 12 = 360 次内层迭代，原版是 72 次。
    /// 那颗星球 1975 个站点、一共只有 411 条传送带，也就是<b>绝大多数站点连一个输出口
    /// 都没有</b>，却照付全价。
    ///
    /// <b>等价性的证明，来自两个循环各自的第一条判断（实测 IL）：</b>
    /// <code>
    /// 004C  第一个循环   ldfld SlotData::dir ; ldc.i4.1 ; bne.un → continue   // != IODir.Output
    /// 0085               ldfld SlotData::beltId ; brfalse    → continue
    /// 0268  嵌套循环     ldfld SlotData::dir ; ldc.i4.1 ; bne.un → continue
    /// 0293               ldfld SlotData::beltId ; brfalse    → continue
    /// </code>
    /// 而这个方法<b>全部</b>的写都在这两道判断的下游——枚举过一遍：
    /// <c>StationStore::count</c>／<c>inc</c>（只在 <c>TryInsertItemAtHeadAndFillBlank</c>
    /// 成功之后）、<c>SlotData::counter</c>／<c>beltId</c>、<c>SignData::iconType</c>／
    /// <c>iconId0</c>（传送带接口上的图标）、<c>StationComponent::warperCount</c>。
    /// 所以<b>一个 <c>dir == Output &amp;&amp; beltId != 0</c> 的槽都没有的站点，
    /// 这个方法不可能产生任何可观察的效果</b>，跳过它是按构造等价的。
    ///
    /// <b>唯一一处「跳过就不会发生」的写，单独说清楚</b>：方法末尾 IL 03F2 无条件写
    /// <c>StationComponent::outSlotOffset</c>——那是输出槽之间轮转的游标。没有输出口时
    /// 没有任何东西读它（读它的正是我们跳掉的那个嵌套循环），而一旦玩家接上带子，
    /// 下面的判据立刻不再跳过，游标照常恢复推进。所以它不是行为差异，是无人观察的状态。
    ///
    /// <b>代价是 O(slots)：</b>12 次判断换掉 12 + 30×12 = 372 次。接了带子的站点多付
    /// 那 12 次，占它自己开销的 3%；没接的站点省掉 97%。
    ///
    /// <b>它对巨型建筑尤其干净</b>，而这一点是查出来的不是猜的：巨型建筑的传送带 I/O
    /// 走的是本 mod 自己的 <c>SlotDataStore</c>（按 <c>(planetId, entityId)</c> 存，
    /// 走 DSPModSave），和 <c>StationComponent.slots</c> 是<b>两张互不相干的表</b>。
    /// 所以这里跳掉的从来就不是巨型建筑的出货路径。
    /// </summary>
    [HarmonyPatch]
    internal static class StationOutputSkipPatches
    {
        private static long _skipped;
        private static long _ran;

        /// <summary>
        /// <b>前置返回 false = 跳过原版。</b>
        ///
        /// 只注入 <c>__instance</c>，不碰任何形参名——原版这个方法的形参会不会被
        /// preloader 加宽是另一回事，而按名字注入是本仓库记过三次的整站崩溃陷阱。
        ///
        /// 这条跑在 <c>_station_output_parallel</c> 上，<b>并行且按星球分线程</b>，
        /// 所以计数只能用 <c>Interlocked</c>，而且绝不在这里碰 Unity 的任何 API。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.UpdateOutputSlots))]
        private static bool UpdateOutputSlots_Prefix(StationComponent __instance)
        {
            SlotData[] slots = __instance.slots;

            // 连槽位数组都没有 → 更没有输出口。原版进去也是一圈空转。
            if (slots == null || slots.Length == 0)
            {
                System.Threading.Interlocked.Increment(ref _skipped);

                return false;
            }

            for (var i = 0; i < slots.Length; i++)
            {
                // 判据逐字抄原版两个循环的头两条判断，**不是另写一个等价条件**——
                // 自己发明一个判据就会在边界上和原版各说各话（钻头消耗那次的教训）。
                if (slots[i].dir != IODir.Output) continue;
                if (slots[i].beltId == 0) continue;

                System.Threading.Interlocked.Increment(ref _ran);

                return true;
            }

            System.Threading.Interlocked.Increment(ref _skipped);

            return false;
        }

        private static float _nextReport;
        private static long _lastSkipped;
        private static long _lastRan;
        private static int _entered;

        /// <summary>
        /// 每 60 秒报一次跳过比例。
        ///
        /// 挂 <c>UIGame._OnUpdate</c> 是因为它<b>在主线程</b>：计数在并行的物流站 tick 上
        /// 累加，而 <c>Time.realtimeSinceStartup</c> 只能主线程读。节流用它而不是
        /// <c>GameMain.gameTick</c>——后者换存档时会倒退，报告会静默死掉（第 4 号坑）。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate_SkipReport()
        {
            if (System.Threading.Interlocked.Exchange(ref _entered, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    "物流站·空输出口跳过：统计挂点已跑到，之后每 60 秒报一次增量。");

            float now = UnityEngine.Time.realtimeSinceStartup;

            if (now < _nextReport) return;

            // 第一次只对表不报：否则报的是开局那段还没建成的工厂，把数稀释掉。
            if (_nextReport <= 0f)
            {
                _nextReport = now + 60f;
                _lastSkipped = System.Threading.Interlocked.Read(ref _skipped);
                _lastRan = System.Threading.Interlocked.Read(ref _ran);

                return;
            }

            _nextReport = now + 60f;

            long skipNow = System.Threading.Interlocked.Read(ref _skipped);
            long ranNow = System.Threading.Interlocked.Read(ref _ran);

            long skipped = skipNow - _lastSkipped;
            long ran = ranNow - _lastRan;

            _lastSkipped = skipNow;
            _lastRan = ranNow;

            long total = skipped + ran;

            // 一次都没调到 = 这颗存档里没有物流站。**照样报一行**，
            // 否则「没有物流站」和「统计坏了」在日志里长得一样。
            if (total <= 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "物流站·空输出口跳过：过去 60 秒一次都没调到——这颗存档里没有物流站。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"物流站·空输出口跳过：过去 60 秒跳过 {skipped} 次、照常跑 {ran} 次，"
                + $"即 {total} 次调用里跳掉了 {(100.0 * skipped / total):0.#}%。"
                + "跳掉的每一次都省下一整趟 storage × slots 的嵌套循环，"
                + "落在性能面板的「传送带附属设施」一项上。"
                + "**跳过是按构造等价的**：没有 dir==Output 且 beltId!=0 的槽位时，"
                + "原版那个方法的每一处写都够不着。");
        }

        /// <summary>
        /// 开机状态行：这条补丁<b>接上了没有</b>。
        ///
        /// 读的是 <c>Harmony.GetAllPatchedMethods()</c>——已生效的状态，而不是
        /// 「我调了 PatchAll 而且没抛异常」。必须排在 PatchAll 之后。
        /// </summary>
        internal static void Report()
        {
            var hooked = false;

            foreach (System.Reflection.MethodBase mb in Harmony.GetAllPatchedMethods())
                if (mb?.DeclaringType == typeof(StationComponent) && mb.Name == "UpdateOutputSlots")
                {
                    hooked = true;

                    break;
                }

            if (hooked)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "物流站·空输出口跳过：已启用。没有任何 dir==Output 且 beltId!=0 槽位的站点，"
                    + "整个 UpdateOutputSlots 都跳过——按构造等价（该方法全部的写都在那两道"
                    + "判断的下游），省下的是 storage × slots 的嵌套循环。每 60 秒报一次比例。");

                return;
            }

            ProjectEdenPlugin.Log.LogWarning(
                "物流站·空输出口跳过：前置没挂到 StationComponent.UpdateOutputSlots 上，"
                + "这条优化整局都不会生效。");
        }
    }
}
