// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 行星内物流运输机：一帧派出多架，而不是一架。
    ///
    /// <b>先把原版的形状量出来</b>（<c>StationComponent.InternalTickLocal</c>，2600 条 IL）。
    /// 派机被两道闸夹着，两道都是节流：
    ///
    /// <code>
    /// 00A3  if (timeGene % droneTaskInterval != id % droneTaskInterval) goto 1297;  // 错帧闸
    /// 010B  if (localPairCount &lt;= 0) goto 11C8;
    /// 0117  if (idleDroneCount &lt;= 0) goto 11C8;
    /// 0123  if (energy &lt;= 800000)    goto 11C8;
    /// 0134  localPairProcess %= localPairCount;
    ///       int start = localPairProcess;                       // V_22
    /// 015F  do {                                                // ← 循环头
    ///           ref pair = localPairs[localPairProcess];
    ///           ... 三条派机分支 ...
    ///           // 派出去了  → br 11A5    ← **跳出循环**
    ///           // 电不够    → blt 11A5   ← 跳出循环
    ///           // 这一对没活儿 →
    /// 1175      localPairProcess = (localPairProcess + 1) % localPairCount;
    /// 11A0  } while (start != localPairProcess);
    /// 11A5  localPairProcess = (localPairProcess + 1) % localPairCount;
    /// </code>
    ///
    /// <b>所以那个循环不是「派机循环」，是「找活儿的扫描」</b>——它走遍整个配对环，
    /// 但只要真的派出一架就立刻 <c>br</c> 出去。于是每站每个「自己的帧」最多一架。
    ///
    /// <b>第二道闸是自适应的，这一点决定了为什么光调它不够</b>（IL 11C8–1292）：
    /// <c>droneDispatchStatus</c> 是 <c>new byte[30]</c>，每个「自己的帧」清零一格、
    /// 派出一架就 +1；转满一圈后按 <c>busy = 总和 / 30</c> 调整间隔——
    /// <c>busy &lt; 0.75</c> 放大间隔、<c>&gt; 0.9</c> 缩小间隔（×0.8）、中间是回滞带，
    /// 最后夹在 <c>[1, 总机数 &gt;= 75 ? 10 : 20]</c>。**也就是说间隔最好也只能到 1**，
    /// 一个站的天花板就是 60 架/秒，而且忙起来原版自己就会把它压到 1。
    /// 真正卡住吞吐的是「一帧一架」这条，不是间隔。
    ///
    /// <b>改法：把「派出去了 → 跳出」改成「派出去了 → 问一句还能不能再派」。</b>
    /// 三处跳出改成先调 <see cref="BurstContinue"/>，返回 true 就跳到 <c>1175</c>
    /// ——那正是原版「这一对没活儿」的续扫路径，和跳出路径做的事一模一样
    /// （游标 +1 带回绕），差别只有那句回跳测试。所以**原版的派机判断一条都没有被重写**，
    /// 我们只是不再从它自己的循环里提前退出。
    ///
    /// <b>为什么不能用 RunExtraCycles 那一招（重复调用整个方法）。</b> 星际那边
    /// <c>DetermineDispatch</c> 是纯派机、没有回跳，重复调它就是顺着游标往下走——
    /// CLAUDE.md 里记的就是那条。<b>这个方法不是</b>：它把四件事焊在一起，重复调会
    /// 各自出错——(1) 开头 IL 0002–0054 给站点<b>充电</b>，重复调就是一帧充两次；
    /// (2) IL 00BD–00F8 推进 <c>droneStatusCursor</c>，重复调会把那 30 格的自适应
    /// 采样窗按倍数烧掉、把 busy 算错；(3) IL 11C8–1292 重算 <c>droneTaskInterval</c>；
    /// (4) IL 1297 之后是<b>飞行推进</b>循环（<c>t += direction × 速度</c>），重复调
    /// 等于让所有在途的飞机按倍数加速。要压掉的是四段，不是一段——比改三个分支贵得多。
    ///
    /// <b>护栏必须跟着进循环，这是本补丁唯一真正危险的地方。</b>
    /// <c>idleDroneCount &lt;= 0</c> 那道闸在<b>循环外</b>（IL 0117），循环体里原版
    /// <b>从不复查</b>它——因为它派一架就走，一次检查就够。而派机写的是
    /// <c>workDroneDatas[workDroneCount]</c>，这个数组的长度是
    /// <c>prefabDesc.stationMaxDroneCount</c>（<c>Init</c> IL 0128–0135），
    /// 也就是 <c>workDroneCount + idleDroneCount</c> 的上界。**一旦让它连发而不复查，
    /// 第一发把 idleDroneCount 打到 0 之后，下一发就是数组越界。**
    /// 所以 <see cref="BurstContinue"/> 把原版循环外那两道闸<b>逐字抄进来</b>
    /// （不是另写一个等价条件——钻头消耗那次的教训）。
    ///
    /// 「电不够」的两处跳出（IL 0410 / 0EE3，<c>blt</c>）<b>保持原样</b>：
    /// 那是每一趟按距离算出来的精确成本，比循环外那道 800000 的地板严得多，
    /// 而且它本来就在循环体内，连发时每一发都会重新过一遍。
    ///
    /// <b>代价的形状，说准一点。</b> 外层循环的总迭代数<b>本来就</b>被
    /// <c>start != localPairProcess</c> 夹在 <c>localPairCount</c> 以内，连发不会突破它——
    /// 也就是说<b>连发的最坏情况等于原版的最坏情况</b>（一圈都没活儿时走满一圈）。
    /// 变的是典型情况：原版一有收获就停，现在会继续到额度用完。
    /// 每多派一架 = 多一次外层迭代，而<b>「需求侧」那条分支的每次外层迭代自带一趟内层扫描</b>
    /// （IL 0A3A–0ECF，同样以整个配对环为界）。所以增量成本 ≈
    /// 「多派的架次 × 原版第一次迭代的开销」——而一次<b>成功</b>的迭代按定义就是很快找到货的那种，
    /// 于是成本是跟着吞吐一起长的，不是凭空多出来的。真要衡量就看性能面板的
    /// 「物流运输」一栏，以及本类每 60 秒报的实际倍率。
    ///
    /// <b>上限 200 不是随手挑的</b>：<c>droneDispatchStatus</c> 是 <c>byte[]</c>，
    /// 每个「自己的帧」先清零再按架次累加，所以一帧派 256 架就会回绕。
    /// 200 留着余量。顺带一提连发会把 <c>busy</c> 推到 1 以上，于是自适应控制器
    /// 会把 <c>droneTaskInterval</c> 一路压到 1 并停在那儿——这正是想要的结果。
    /// </summary>
    [HarmonyPatch]
    internal static class LocalDispatchBurstPatches
    {
        /// <summary>原版循环外那道能量地板，IL 0123 的字面量。**抄的，不是挑的。**</summary>
        private const long EnergyFloor = 800000L;

        /// <summary>
        /// 一帧最多派几架。<c>droneDispatchStatus</c> 是 byte[]，每帧清零后按架次累加，
        /// 所以硬上限是 255；留余量夹在 200。
        /// </summary>
        private const int HardCap = 200;

        /// <summary>
        /// 这一趟 <c>InternalTickLocal</c> 还能再派几架。
        ///
        /// <c>PlanetTransport.GameTick</c> 是**按星球并行**的（~31 个工作线程），
        /// 但单个站点的 <c>InternalTickLocal</c> 从头到尾在同一个线程上跑完，
        /// 所以 <c>[ThreadStatic]</c> 正好隔离。前置每次调用都会重置它，
        /// 因此不需要懒加载——那条规矩是给引用类型的
        /// （<c>[ThreadStatic]</c> 的初始化器只在第一个线程上跑）。
        /// </summary>
        [System.ThreadStatic] private static int _left;

        private static int _max = -1;

        /// <summary>派出去的总架次。每一架恰好调一次 <see cref="BurstContinue"/>。</summary>
        private static long _dispatches;

        /// <summary>
        /// 「至少派出过一架」的趟数 —— <b>这就是原版会派出的架次</b>，因为原版派一架就跳出。
        ///
        /// 判据是 <c>_left == max</c>：前置把额度重置成 max，只有这一趟的<b>第一架</b>
        /// 看到的 <c>_left</c> 还没被扣过。<c>max == 1</c> 时额度不会被扣，
        /// 但那时一趟本来也只可能派一架，仍然对。
        ///
        /// <b>第一版没有这个计数器，于是倍率是算错的</b>：当时报的是
        /// <c>1 + 允许继续的次数 / 总架次</c>，而只要额度不吃紧、那个比值就恒等于 ≈2.00，
        /// <b>无论真实倍率是多少</b>。读数不随被测量的东西变化，就等于没在测。
        /// </summary>
        private static long _attempts;

        /// <summary>连发被「每帧额度用完」终止的次数。小 = 调大配置也没用。</summary>
        private static long _stopBudget;

        /// <summary>连发被「没有闲置运输机了」终止的次数。</summary>
        private static long _stopIdle;

        /// <summary>连发被「电不够」终止的次数。</summary>
        private static long _stopEnergy;

        /// <summary>配置值，解析一次。0 或缺配置 = 1 = 原版行为。</summary>
        private static int Resolve()
        {
            int n = ProjectEdenPlugin.StationsConfig?.localDispatchPerTick ?? 0;

            if (n <= 0) return 1;

            return n > HardCap ? HardCap : n;
        }

        /// <summary>
        /// 重置这一趟的额度。
        ///
        /// 无参前置，只写一个静态 int——这条跑在每站每帧上，所以除了这一次写
        /// 什么都不做，尤其不碰 Unity 的任何 API（并行线程）。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.InternalTickLocal))]
        private static void InternalTickLocal_Prefix()
        {
            int max = _max;

            if (max < 0) max = _max = Resolve();

            _left = max;
        }

        /// <summary>
        /// 「刚派出去一架，还能再派吗」。返回 true = 回到原版的续扫路径。
        ///
        /// 三条判断，后两条是原版循环外那两道闸的逐字副本：
        /// <list type="bullet">
        /// <item>额度用完 → 停。</item>
        /// <item><c>idleDroneCount &lt;= 0</c>（IL 0117）→ 停。
        ///       <b>这条是硬性的</b>，见类注释：不查就是 <c>workDroneDatas</c> 越界。</item>
        /// <item><c>energy &lt;= 800000</c>（IL 0123）→ 停。每趟的精确成本由循环体内
        ///       原版自己的 <c>blt</c> 再把关一次。</item>
        /// </list>
        /// </summary>
        private static bool BurstContinue(StationComponent station)
        {
            int left = _left;

            System.Threading.Interlocked.Increment(ref _dispatches);

            // 这一趟的第一架 —— 也就是原版会派出、然后跳出循环的那一架。
            if (left == _max) System.Threading.Interlocked.Increment(ref _attempts);

            // 刚刚那一架就是这次额度里的一架，所以 <=1 意味着用完了。
            if (left <= 1)
            {
                System.Threading.Interlocked.Increment(ref _stopBudget);

                return false;
            }

            _left = left - 1;

            if (station == null) return false;

            if (station.idleDroneCount <= 0)
            {
                System.Threading.Interlocked.Increment(ref _stopIdle);

                return false;
            }

            if (station.energy <= EnergyFloor)
            {
                System.Threading.Interlocked.Increment(ref _stopEnergy);

                return false;
            }

            return true;
        }

        /// <summary>
        /// 把三处「派出去了 → 跳出循环」改成「派出去了 → 问一句」。
        ///
        /// <b>定位靠形状，不靠偏移</b>。锚点是那段「游标 +1 带回绕」的序列
        /// <c>ldarg.0 ; ldarg.0 ; ldfld localPairProcess ; ldc.i4.1 ; add ; stfld localPairProcess</c>——
        /// 全方法只有两处（另外四处写 <c>localPairProcess</c> 的不是 <c>+1</c>：
        /// 两处是 <c>rem</c>、两处是 <c>ldc.i4.0</c>）。
        /// 第一处是续扫口 <c>1175</c>，第二处是出口 <c>11A5</c>。
        ///
        /// 然后**按分支种类分类**跳到出口的那几条，这是判据里最要紧的一条：
        /// <c>br</c> / <c>brtrue</c> 是「派出去了」（各 2 / 1 条），
        /// <c>blt</c> 是「电不够」（2 条，<b>不动</b>）。数目对不上就整条不改、大声报错——
        /// 不转译永远好过转译错。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.InternalTickLocal))]
        private static IEnumerable<CodeInstruction> InternalTickLocal_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(instructions);

            FieldInfo cursor = AccessTools.Field(typeof(StationComponent), "localPairProcess");

            // 先解析、判空、再建匹配器。解析不到就发 call null，而那会在 Harmony 的
            // 写出阶段抛 ArgumentNullException，堆栈指向 Harmony 而不是这一行。
            MethodInfo cont = AccessTools.Method(
                typeof(LocalDispatchBurstPatches), nameof(BurstContinue));

            if (cursor == null || cont == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "行星内连发派机：解析不到 StationComponent.localPairProcess 或 BurstContinue，"
                    + "整条转译放弃，派机保持原版的一帧一架。");

                return code;
            }

            var starts = new List<int>();

            for (var i = 0; i + 5 < code.Count; i++)
                if (code[i].opcode == OpCodes.Ldarg_0
                 && code[i + 1].opcode == OpCodes.Ldarg_0
                 && code[i + 2].opcode == OpCodes.Ldfld && cursor.Equals(code[i + 2].operand)
                 && code[i + 3].opcode == OpCodes.Ldc_I4_1
                 && code[i + 4].opcode == OpCodes.Add
                 && code[i + 5].opcode == OpCodes.Stfld && cursor.Equals(code[i + 5].operand))
                    starts.Add(i);

            if (starts.Count != 2)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"行星内连发派机：「游标 +1」的锚点应当有 2 处，实际 {starts.Count} 处——"
                    + "原版 InternalTickLocal 的形状变了。整条转译放弃，派机保持原版的一帧一架。");

                return code;
            }

            int iContinue = starts[0];
            int iExit = starts[1];

            // 出口那条指令身上挂着的标签，就是各处跳出用的标签。
            var exitLabels = new HashSet<Label>(code[iExit].labels);

            if (exitLabels.Count == 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    "行星内连发派机：出口那条指令身上一个标签都没有，说明锚点认错了。"
                    + "整条转译放弃，派机保持原版的一帧一架。");

                return code;
            }

            Label lContinue = generator.DefineLabel();
            code[iContinue].labels.Add(lContinue);

            Label lExit = generator.DefineLabel();
            code[iExit].labels.Add(lExit);

            var unconditional = new List<int>();
            var conditional = new List<int>();
            var giveUp = 0;
            var unknown = 0;

            for (var i = 0; i < code.Count; i++)
            {
                if (i == iContinue || i == iExit) continue;

                if (!(code[i].operand is Label lab)) continue;

                if (!exitLabels.Contains(lab)) continue;

                if (code[i].opcode == OpCodes.Br || code[i].opcode == OpCodes.Br_S)
                    unconditional.Add(i);
                else if (code[i].opcode == OpCodes.Brtrue || code[i].opcode == OpCodes.Brtrue_S)
                    conditional.Add(i);
                else if (code[i].opcode == OpCodes.Blt || code[i].opcode == OpCodes.Blt_S)
                    giveUp++;   // 「电不够」，保持原样
                else
                    unknown++;
            }

            if (unconditional.Count != 2 || conditional.Count != 1 || giveUp != 2 || unknown != 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"行星内连发派机：跳到出口的分支应当是 2 条 br（派出去了）、1 条 brtrue（派出去了）、"
                    + $"2 条 blt（电不够，不动）、0 条其它；实际 {unconditional.Count}/{conditional.Count}/"
                    + $"{giveUp}/{unknown}。原版 InternalTickLocal 的形状变了，"
                    + "整条转译放弃，派机保持原版的一帧一架。");

                return code;
            }

            // 从后往前改，前面的下标才不会被插入顶走。
            var sites = new List<int>();
            sites.AddRange(unconditional);
            sites.AddRange(conditional);
            sites.Sort();
            sites.Reverse();

            foreach (int i in sites)
            {
                bool isConditional = conditional.Contains(i);

                if (isConditional)
                {
                    // 栈上是「派出去了没有」这个 bool。原版：为真就跳出。
                    // 改成：为假就跳过这一段（走原版的下一条），为真就问一句。
                    Label notDispatched = generator.DefineLabel();

                    // 改写现有指令而不是换一个新的——换掉会丢掉它身上的标签。
                    code[i].opcode = OpCodes.Brfalse;
                    code[i].operand = notDispatched;

                    code.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                    code.Insert(i + 2, new CodeInstruction(OpCodes.Call, cont));
                    code.Insert(i + 3, new CodeInstruction(OpCodes.Brtrue, lContinue));
                    code.Insert(i + 4, new CodeInstruction(OpCodes.Br, lExit));

                    code[i + 5].labels.Add(notDispatched);

                    continue;
                }

                // 无条件跳出：栈上是空的，直接问一句。
                code[i].opcode = OpCodes.Ldarg_0;
                code[i].operand = null;

                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, cont));
                code.Insert(i + 2, new CodeInstruction(OpCodes.Brtrue, lContinue));
                code.Insert(i + 3, new CodeInstruction(OpCodes.Br, lExit));
            }

            _patched = sites.Count;

            ProjectEdenPlugin.Log.LogInfo(
                $"行星内连发派机：已改写 {sites.Count} 处「派出去了就跳出循环」"
                + "（2 条 br + 1 条 brtrue），2 条「电不够」的跳出保持原样。");

            return code;
        }

        private static int _patched;

        private static float _nextReport;
        private static long _lastDispatches;
        private static long _lastAttempts;
        private static long _lastStopBudget;
        private static int _entered;
        private static float _windowStart;

        /// <summary>首次统计窗口（秒）。之后每 <see cref="ReportSeconds"/> 秒一次。</summary>
        private const float FirstReportSeconds = 20f;

        private const float ReportSeconds = 60f;

        /// <summary>
        /// 每 60 秒报一次增量，外加本地星球的 <c>droneTaskInterval</c> 分布。
        ///
        /// 后面那半是**为了分清两道闸**：如果间隔普遍已经是 1，那瓶颈就是「一帧一架」
        /// （本补丁正对症）；如果间隔还挂在 10–20，那是自适应控制器认为这些站不忙，
        /// 说明卡的是别处。没有这一行，两种情况在日志里长得一样。
        ///
        /// 挂 <c>UIGame._OnUpdate</c> 是因为它在主线程：计数在并行的物流站 tick 上累加，
        /// 而 <c>Time.realtimeSinceStartup</c> 只能主线程读；用 <c>GameMain.gameTick</c>
        /// 节流会在换存档时倒退，报告会静默死掉（第 4 号坑）。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate_BurstReport()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;

            if (now < _nextReport) return;

            if (_nextReport <= 0f)
            {
                _nextReport = now + FirstReportSeconds;
                _windowStart = now;
                _lastDispatches = System.Threading.Interlocked.Read(ref _dispatches);
                _lastAttempts = System.Threading.Interlocked.Read(ref _attempts);
                _lastStopBudget = System.Threading.Interlocked.Read(ref _stopBudget);

                // **统计挂点的「我还活着」行。** 没有它，「这一局没待够窗口」和
                // 「这个统计压根没跑到」在日志里长得一模一样——本文件为这个形状
                // 付过六次代价，而这条补丁第一次进游戏就又撞上了：那一局不到 60 秒，
                // 于是日志里一条增量都没有，分不清是哪一种。
                if (System.Threading.Interlocked.Exchange(ref _entered, 1) == 0)
                    ProjectEdenPlugin.Log.LogInfo(
                        $"行星内连发派机：统计挂点已跑到，{FirstReportSeconds:0} 秒后报第一次、"
                        + $"之后每 {ReportSeconds:0} 秒一次。"
                        + "第一次那一窗包含读档后工厂启动的那段，看趋势请以后面几次为准。");

                return;
            }

            _nextReport = now + ReportSeconds;

            // 报的是实测窗口，不是写死的 60——首次窗口是 20 秒，而且掉帧时 _OnUpdate
            // 也可能晚到。写死一个数就等于在日志里撒谎。
            float window = now - _windowStart;
            _windowStart = now;

            long dispatchesNow = System.Threading.Interlocked.Read(ref _dispatches);
            long attemptsNow = System.Threading.Interlocked.Read(ref _attempts);
            long stopBudgetNow = System.Threading.Interlocked.Read(ref _stopBudget);

            // 这两个正常应当是 0。不是 0 就说明连发是被护栏拦下的，而不是被额度或者
            // 「没货了」拦下的——**计数器只写不读就只是个主张**，所以照样报出来。
            long starved = System.Threading.Interlocked.Read(ref _stopIdle);
            long browned = System.Threading.Interlocked.Read(ref _stopEnergy);

            long dispatches = dispatchesNow - _lastDispatches;
            long attempts = attemptsNow - _lastAttempts;
            long stopBudget = stopBudgetNow - _lastStopBudget;

            _lastDispatches = dispatchesNow;
            _lastAttempts = attemptsNow;
            _lastStopBudget = stopBudgetNow;

            string intervals = DescribeIntervals();

            if (dispatches <= 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"行星内连发派机：过去 {window:0} 秒一架都没派出去——"
                    + "要么这颗存档里没有物流站，要么所有站点都没有活儿可干。"
                    + intervals);

                return;
            }

            // 倍率 = 总架次 / 「原版也会派」的趟数。**分母才是原版的产出**，
            // 因为原版派一架就跳出循环，一趟至多贡献一架。
            double ratio = attempts > 0 ? (double)dispatches / attempts : 1.0;

            // 撞到额度上限的比例：它小，就说明调大 localDispatchPerTick 不会有效果，
            // 真正的限制在别处（没货可送，或者外面那道错帧闸）。
            double capBite = attempts > 0 ? 100.0 * stopBudget / attempts : 0.0;

            ProjectEdenPlugin.Log.LogInfo(
                $"行星内连发派机：过去 {window:0} 秒派出 {dispatches} 架次，"
                + $"分布在 {attempts} 趟里（原版这 {attempts} 趟每趟只会派 1 架），"
                + $"**实测倍率 {ratio:0.##}×**，每帧上限 {_max}。"
                + $"其中 {stopBudget} 趟（{capBite:0.#}%）是被每帧额度打断的——"
                + "这个比例低就说明调大 localDispatchPerTick 不会再有效果，"
                + "限制在「没货可送」或外面那道错帧闸上。"
                + (starved > 0 || browned > 0
                    ? $" 另有累计 {starved} 趟是「闲置运输机用光」、{browned} 趟是「电不够」被护栏拦下的"
                      + "（这两道闸是逐字抄原版循环外那两道的，拦下来就是原版也不会派）。"
                    : "")
                + intervals);
        }

        /// <summary>
        /// 本地星球的 <c>droneTaskInterval</c> 分布，主线程读，零 tick 路径开销。
        /// </summary>
        private static string DescribeIntervals()
        {
            PlanetTransport transport = GameMain.localPlanet?.factory?.transport;

            if (transport == null) return "（本地没有加载中的星球，间隔分布这次不报。）";

            StationComponent[] pool = transport.stationPool;

            if (pool == null) return "（本地星球没有物流站。）";

            int cursor = transport.stationCursor;
            var n = 0;
            var atOne = 0;
            var sum = 0;
            int worst = 0;

            for (var i = 1; i < cursor && i < pool.Length; i++)
            {
                StationComponent s = pool[i];

                if (s == null || s.id != i) continue;

                n++;
                sum += s.droneTaskInterval;

                if (s.droneTaskInterval <= 1) atOne++;

                if (s.droneTaskInterval > worst) worst = s.droneTaskInterval;
            }

            if (n == 0) return "（本地星球没有物流站。）";

            return $" 本地星球 {n} 个站点的派机间隔：平均 {((double)sum / n):0.#} 帧、"
                 + $"最长 {worst} 帧、已经压到每帧一次的有 {atOne} 个"
                 + $"（{(100.0 * atOne / n):0.#}%）。间隔是原版自己按忙闲调的，夹在 1..20；"
                 + "它普遍是 1 就说明瓶颈确实是「一帧只派一架」。";
        }

        /// <summary>
        /// 开机状态行：这条补丁**接上了没有**，以及额度是多少。
        ///
        /// 读 <c>Harmony.GetAllPatchedMethods()</c>——已生效的状态，而不是
        /// 「我调了 PatchAll 而且没抛异常」。必须排在 PatchAll 之后。
        /// </summary>
        internal static void Report()
        {
            if (_max < 0) _max = Resolve();

            var hooked = false;

            foreach (MethodBase mb in Harmony.GetAllPatchedMethods())
                if (mb?.DeclaringType == typeof(StationComponent)
                 && mb.Name == nameof(StationComponent.InternalTickLocal))
                {
                    hooked = true;

                    break;
                }

            if (!hooked)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "行星内连发派机：补丁没挂到 StationComponent.InternalTickLocal 上，"
                    + "整局都会保持原版的一帧一架。");

                return;
            }

            if (_patched != 3)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"行星内连发派机：挂上了，但转译只改写了 {_patched} 处（应当 3 处），"
                    + "派机保持原版的一帧一架。上面应当有一条 ERROR 说明原因。");

                return;
            }

            int raw = ProjectEdenPlugin.StationsConfig?.localDispatchPerTick ?? 0;

            if (_max <= 1)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"行星内连发派机：已接上，但 stations.json 的 localDispatchPerTick = {raw}，"
                    + "按原版行为跑（每站每帧最多一架）。把它调大即可生效，不用重新编译——"
                    + "配置文件放在 BepInEx/config/ProjectEden/stations.json。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"行星内连发派机：已启用，每站每帧最多派 {_max} 架"
                + (raw > HardCap ? $"（配置写的是 {raw}，夹到了硬上限 {HardCap}）" : "")
                + "。原版是一架——它的派机循环本来就会走遍整个配对环，"
                + "只是派出一架就跳出去了；现在改成问一句还有没有额度、有就接着扫。"
                + "vanilla 的派机判断一条都没重写，「电不够」的两处跳出也保持原样。"
                + "每 60 秒报一次实际倍率和派机间隔分布。");
        }
    }
}
