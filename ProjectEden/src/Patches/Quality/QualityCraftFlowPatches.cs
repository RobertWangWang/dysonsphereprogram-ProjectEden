using System;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// <b>制造这一环：投入的品质变成产物的品质。</b>
    ///
    /// 在这之前，品质能走到机器的投料格（<c>quaServed</c> 由搬运层填好）然后<b>停在那里</b>
    /// ——实测 <c>AssemblerComponent.InternalUpdate</c> 读 <c>incServed</c> 4 次、
    /// 读 <c>quaServed</c> <b>0 次</b>，而 <c>produced</c> 根本没有对应的品质数组。
    /// 于是提纯过的料进了装配机，造出来的东西是 0 分，链路到此为止。
    ///
    /// <b>为什么 1c 不能自动补上这一段。</b> 搬运层可以照着 <c>inc</c> 机械复制，因为那里
    /// 每一次 <c>inc</c> 访问都是「把点数搬到另一个容器」。而这里不是搬运，是<b>结算</b>：
    /// 原版读 <c>incServed</c> 是为了算增产加成（多出几个、快多少），照抄那套数学
    /// 会让品质变成第二种增产剂。**规则必须由人定**，这个文件就是那条规则。
    ///
    /// <b>规则：按件数加权平均。</b> 产物的<b>每件分数</b> = 这一次吃掉的那些投入的
    /// 每件分数，按件数加权平均。掺进普通料就往下拉，而产物永远不会比最好的那种料更好。
    ///
    /// <b>这条规则换过一次，旧的那条（求和）错在哪里值得记下来。</b>
    /// 旧规则是「产物品质 = 投入品质之和，按产出件数摊开」，口号写着
    /// 「品质是可加点数，永远守恒，只会被稀释」。前半句对合并和拆分都成立，
    /// <b>对合成不成立</b>：合成把多件变少件，于是每件分数必然往上翻——
    /// 50 分的铁块 ×2 加 50 分的齿轮 ×1，造出来一台马达是 <b>150 分</b>。
    ///
    /// 而翻多少倍由 <c>requireCounts</c> 决定，那是个纯粹的平衡数字，没人是为品质挑的：
    /// 4:1 的配方翻四倍，1:4 的配方砍四分之一。**那不是设计，是算术漏出来的。**
    /// 更要命的是它和设计的头条自相矛盾——启动日志每局都打「品质的来源只有提纯厂」，
    /// 可在求和规则下，顺着生产链每一级都在凭配方比例造品质。
    ///
    /// 加权平均把三件事一起解决：口号变成真的（真的只会被稀释）、提纯厂重新是唯一来源、
    /// <b>而且构造上就出不了上限</b>——产物每件分数 ≤ 最好的那种料的每件分数，
    /// 所以这里不需要任何夹子。（求和那版需要，而夹子本身又制造了新的不一致：
    /// 机器不夹、手搓夹、巡检 30 秒后再夹一次，同一台马达的分数会自己变。）
    ///
    /// <b>「这一 tick 到底造出来没有」是量出来的，不是照着原版的条件重算的。</b>
    /// 前置记下 <c>served</c>，后置比差值——这和 <c>RunExtraCycles</c> 数
    /// <c>produced[0]</c> 是同一个做法，理由也一样：原版哪天多加一道门，
    /// 重算那一套会悄悄错位，而量差值不会。
    ///
    /// <b>这是 tick 路径，而且是并行的</b>（<c>GameLogic._assembler_parallel</c>）。
    /// 所以：快照缓冲区是 <c>[ThreadStatic]</c> 且惰性建；没有品质就<b>第一时间返回</b>，
    /// 不分配、不拼字符串、不走 LINQ。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityCraftFlowPatches
    {
        /// <summary>前置记下的投料件数。<b>逐线程一份</b>——装配机结算是跨线程并行的。</summary>
        [ThreadStatic] private static int[] _servedBefore;

        /// <summary>前置记下的产物件数。</summary>
        [ThreadStatic] private static int[] _producedBefore;

        /// <summary>这一次调用值不值得在后置里算——前置说了算，省掉后置的重复判断。</summary>
        [ThreadStatic] private static bool _armed;

        private static int _saidOnce;

        /// <summary>「投料格里第一次出现品质」只报一次。并行 tick，所以用 Interlocked 抢。</summary>
        private static int _fedOnce;

        internal static void Report()
        {
            if (!QualityAccess.CraftReady)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质·制造：**没接上**——AssemblerComponent 上找不到 quaServed / quaProduced。"
                    + "多半是 preloader 没装或者版本对不上；这种情况下造出来的东西一律 0 分，"
                    + "而搬运层照常工作，所以表现是「料有品质、产物没有」。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "物品品质·制造：已接线。产物的每件分数 = 这一次吃掉的投入的每件分数，**按件数加权平均**"
                + "——掺进普通料就往下拉，产物永远不会比最好的那种料更好，所以合成只传递品质、从不创造。"
                + "第一次真的结算出品质时会再报一行。");
        }

        /// <summary>够用就不动，不够才重开——tick 路径上不许每次分配。</summary>
        private static int[] Fit(int[] buf, int n) => buf != null && buf.Length >= n ? buf : new int[n];

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AssemblerComponent), nameof(AssemblerComponent.InternalUpdate))]
        private static void Pre(ref AssemblerComponent __instance)
        {
            _armed = false;

            if (!QualityAccess.CraftReady) return;

            int[] served = __instance.served;
            int[] produced = __instance.produced;

            if (served == null || produced == null || served.Length == 0 || produced.Length == 0) return;

            int[] qs = QualityAccess.GetQuaServed(ref __instance);

            if (qs == null || qs.Length < served.Length) return;

            // **一点品质都没有就别记账。** 绝大多数机器在绝大多数 tick 都走这一支，
            // 它必须比下面整套都便宜——几个 int 比较，不碰内存以外的东西。
            //
            // **「在途池里还有货」也算数，这一条是补上的漏洞。** 只看投料格的话：
            // 带品质的料被吃掉之后 <c>quaServed</c> 就空了，玩家接着喂普通料，
            // 于是产物出来的那一 tick 布不了防、池子不结算——**分数卡在在途池里永远出不来**，
            // 而且一声不吭。判据得是「这台机器和品质有没有关系」，不是「这一刻投料格里有没有」。
            var any = QualityAccess.GetQuaPendingItems(ref __instance) > 0;

            for (var i = 0; !any && i < served.Length; i++)
                if (qs[i] > 0)
                    any = true;

            if (!any) return;

            // **「投料格里有品质了」这一行必须有，哪怕它什么都没发生。**
            //
            // 上一版只在「真的结算出品质」时说话，于是「你还没把料喂进来」和
            // 「喂进来了但我的代码没触发」在日志里长得一模一样——两次来回全花在分这两种情况上。
            // 这是本仓库记了六次的同一条规矩，这里又犯了一次：
            // **「什么都没发生」的那条分支也要留一行。**
            if (System.Threading.Interlocked.Exchange(ref _fedOnce, 1) == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质·制造：**投料格里第一次出现品质**——料已经进到机器里了。"
                    + "接下来该看的是「第一次结算出品质」那一行；"
                    + "**这一行在而那一行迟迟不来**，就是结算那一段的问题，不是喂料没喂到。");

            _servedBefore = Fit(_servedBefore, served.Length);
            _producedBefore = Fit(_producedBefore, produced.Length);

            for (var i = 0; i < served.Length; i++) _servedBefore[i] = served[i];
            for (var j = 0; j < produced.Length; j++) _producedBefore[j] = produced[j];

            _armed = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AssemblerComponent), nameof(AssemblerComponent.InternalUpdate))]
        private static void Post(ref AssemblerComponent __instance)
        {
            if (!_armed) return;

            _armed = false;

            int[] served = __instance.served;
            int[] produced = __instance.produced;

            if (served == null || produced == null) return;

            int[] qs = QualityAccess.GetQuaServed(ref __instance);

            if (qs == null || qs.Length < served.Length) return;

            // ① 这一次吃掉了多少投入，就按比例把那份品质挪进**在途池**
            //
            // <b>不能直接给产物。</b> 原版一个 cycle 的两端不在同一 tick：开工时扣 served，
            // 跑够 timeSpend 才往 produced 里加，中间隔着几十帧。直接给的话，
            // 扣料那一 tick「没产出」、出货那一 tick「没消耗」，两头落空。
            //
            // <b>点数和件数一起进池子。</b> 平均值的分子和分母必须同时跨 tick——
            // 只留分子，出货那一端就没法还原「这些分是多少件料带来的」。
            long taken = 0;
            long takenItems = 0;

            for (var i = 0; i < served.Length && i < _servedBefore.Length; i++)
            {
                int before = _servedBefore[i];
                int used = before - served[i];

                if (used <= 0 || before <= 0) continue;

                // **件数无条件计入，哪怕这一格一分都没有。**
                // 普通料也是分母的一部分——它就是「掺进来把平均分拉低」的那一半，
                // 漏掉它，掺了粗料反而不掉分，整条设计压力就没了。
                takenItems += used;

                if (qs[i] <= 0) continue;

                // 整格吃光就整格带走，免得整数除法留下永远出不去的零头
                int take = used >= before ? qs[i] : (int)((long)qs[i] * used / before);

                qs[i] -= take;
                taken += take;
            }

            int pending = QualityAccess.GetQuaPending(ref __instance);
            int pendingItems = QualityAccess.GetQuaPendingItems(ref __instance);

            if (takenItems > 0)
            {
                pending += (int)taken;
                pendingItems += (int)takenItems;

                QualityAccess.SetQuaPending(ref __instance, pending);
                QualityAccess.SetQuaPendingItems(ref __instance, pendingItems);
            }

            // ② 有产物出来了，就把在途池整个倒进去
            long madeTotal = 0;

            for (var j = 0; j < produced.Length && j < _producedBefore.Length; j++)
            {
                int made = produced[j] - _producedBefore[j];

                if (made > 0) madeTotal += made;
            }

            if (madeTotal <= 0 || pendingItems <= 0) return;

            // **产物的每件分数 = 在途点数 ÷ 在途件数。** 这就是加权平均本身：
            // 分子是各格按吃掉件数摊出来的分，分母是吃掉的总件数。
            //
            // 池子一次清空。**不留余数**——旧的求和规则要守恒，所以零头得跟着走；
            // 平均值规则下总分本来就不守恒（多件变少件时点数是会少的），
            // 留零头反而会让下一批产物平白多出几分。
            long points = pending;
            long items = pendingItems;

            QualityAccess.SetQuaPending(ref __instance, 0);
            QualityAccess.SetQuaPendingItems(ref __instance, 0);

            if (points <= 0) return;

            int[] qp = QualityAccess.GetQuaProduced(ref __instance);

            if (qp == null || qp.Length != produced.Length)
            {
                qp = new int[produced.Length];

                QualityAccess.SetQuaProduced(ref __instance, qp);
            }

            // 先乘后除，别先算出每件分数再乘——每件分数取整之后再乘产量，
            // 少的那部分随产量线性放大，而巨型建筑一 tick 就是上万件。
            long given = 0;

            for (var j = 0; j < produced.Length && j < _producedBefore.Length; j++)
            {
                int made = produced[j] - _producedBefore[j];

                if (made <= 0) continue;

                var share = (int)(points * made / items);

                qp[j] += share;
                given += share;
            }

            if (System.Threading.Interlocked.Exchange(ref _saidOnce, 1) != 0) return;

            ProjectEdenPlugin.Log.LogWarning(
                $"物品品质·制造：第一次结算出品质——吃掉的 {items} 件料带着 {points} 分"
                + $"（每件 {points / items} 分），造出 {madeTotal} 件产物，"
                + $"每件同样 {points / items} 分，合计 {given} 分。"
                + "**规则是按件数加权平均，不是求和**：产物永远不会比最好的那种料更好，"
                + "掺进普通料就往下拉。**这一行在，就说明【提纯 → 合成】通了**；"
                + "产物还要走出机器才看得见，那是出货口那一侧的事。整局只报一次。");
        }
    }
}
