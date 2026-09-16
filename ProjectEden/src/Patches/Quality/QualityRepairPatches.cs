using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 品质的<b>总量上限</b>：一格货的品质总点数不得超过 <c>件数 × 每件上限</c>，
    /// 超了就削到上限。读档时扫一遍，之后每 30 秒再扫一遍。
    ///
    /// <b>这是所有者拍板的定位改变，值得写清楚。</b> 早先它叫「读档修复」，
    /// 立场是「病因修掉了，这是存量，修一次就该干净」——于是每修一次就 ERROR 一次，
    /// 把「还在漏」当成待查的缺陷。追了四轮，堵掉的洞一个比一个深
    /// （前缀顶掉改写过的方法、本 mod 直接调搬运方法没写侧信道、手写搬运只扣件数），
    /// 每次都还剩下一条。
    ///
    /// 所以立场改成：**不追了，但保证有界。** 品质是可加点数，
    /// 而可加量真正危险的不是「偏高」，是<b>没有上限</b>——那就是无限循环。
    /// 一条常设的上限把它钉死在「每件都是满分」这个物理意义上的天花板上，
    /// 剩下的偏差是有界的、可解释的，而且玩家看到的数永远在量纲内。
    ///
    /// <b>削而不清零。</b> 削到上限保留了「这批货是提纯过的」这个事实，
    /// 清零会把玩家真炼出来的东西一起没收。
    ///
    /// <b>不再报 ERROR。</b> 它现在是常设机制而不是缺陷探针——每 30 秒吼一次
    /// 只会把日志填满，而且会让真正的新问题淹在里面。整局只在第一次真的削了东西时
    /// 说一句，说明这条上限在工作。
    ///
    /// 传送带上的货（<c>Cargo.qua</c>）没扫：它在传送带上停留的时间以秒计，
    /// 而两头的容器都扫了，扫它只是在追一个正在流动的影子。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityRepairPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
        private static void GameData_Import()
        {
            _nextWatch = 0f;
            _saidSoOnce = false;

            Run("读档后");
        }

        // ── 玩的过程中也盯着，而不是只在读档时查一次 ──────────

        private static int _enteredOnce;

        /// <summary>
        /// 启动时报一行：这两个挂点到底有没有真的打上去。
        /// 读的是 Harmony 自己的补丁表，也就是<b>实际生效的状态</b>，
        /// 不是「我以为我注册了」。必须在 <c>PatchAll</c> 之后调。
        /// </summary>
        internal static void Report()
        {
            var upd = false;
            var imp = false;

            foreach (System.Reflection.MethodBase mb in Harmony.GetAllPatchedMethods())
            {
                if (mb?.DeclaringType == typeof(UIGame) && mb.Name == "_OnUpdate") upd = true;
                if (mb?.DeclaringType == typeof(GameData) && mb.Name == nameof(GameData.Import)) imp = true;
            }

            if (upd && imp)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "物品品质·巡检：两个挂点都已打上（UIGame._OnUpdate 每 30 秒一遍、"
                    + "GameData.Import 读档一遍）。");

                return;
            }

            ProjectEdenPlugin.Log.LogError(
                $"物品品质·巡检：挂点没打全——UIGame._OnUpdate={upd}、GameData.Import={imp}。"
                + "缺的那一个对应的巡检不会跑，而且不会报错。");
        }

        private static float _nextWatch;
        private static bool _saidSoOnce;

        /// <summary>
        /// 每 30 秒把上限重新压一遍：<b>机甲背包</b> + <b>当前所在星球</b>的物流站槽位。
        ///
        /// <b>它是执行机制，不是探针。</b> 读档那一次只能保证存档进来时是有界的；
        /// 品质是在玩的过程中流动的，不定期压一次就等于没有上限。
        ///
        /// 范围有意收窄到「玩家看得见的那一圈」：背包是一个 <c>StorageComponent</c>、
        /// 几十格；本星球的物流站是有界的一批。全图每颗星球扫一遍留给读档那一次，
        /// 每 30 秒做那件事会在大存档上变成一次可感的卡顿，而收益只是让看不见的货
        /// 早几分钟被压回去。
        ///
        /// 挂在 <c>UIGame._OnUpdate</c> 上是因为它<b>在主线程</b>——
        /// 物流站那条 tick 是跨星球并行的（约 31 个工作线程），在那上面改共享状态
        /// 是本仓库记过的第 4 号坑。节流用 <c>realtimeSinceStartup</c> 而不是
        /// <c>gameTick</c>：后者换存档时会往回跳，定时就再也不会到期，而且悄无声息。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate()
        {
            // **入口无条件报一行，在任何闸之前。**
            //
            // 这个探针已经连着三轮没能给出结论，每一轮的「没有那一行」都有
            // 不同的解释（挂在只跑一次的方法里 / 巡检不扫储物箱 / 巡检根本没跑）。
            // 每次我都得再猜一次它到底执行到哪一步——而猜就是一个往返。
            //
            // 现在：只要这个后置被调到过一次，日志里就一定有话。
            // 没话就只剩一种可能：<b>补丁根本没挂到 UIGame._OnUpdate 上</b>。
            if (System.Threading.Interlocked.Exchange(ref _enteredOnce, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    "物品品质·巡检：UIGame._OnUpdate 的后置已跑到（补丁确实挂上了）。"
                    + $"此刻 GridWritable={QualityAccess.GridWritable}、Ready={QualityAccess.Ready}、"
                    + $"GridReady={QualityAccess.GridReady}。这三个里只要有 false，下面的扫描就会静默跳过。");

            if (!QualityAccess.GridWritable && !QualityAccess.Ready) return;

            float now = UnityEngine.Time.realtimeSinceStartup;

            if (now < _nextWatch) return;

            _nextWatch = now + 30f;

            var worst = 0;
            var touched = 0;

            if (QualityAccess.GridWritable)
                touched += ClampStorage(GameMain.mainPlayer?.package, ref worst);

            // 本星球的物流站槽位。跨星球那一圈交给读档那一次。
            PlanetTransport transport = GameMain.localPlanet?.factory?.transport;

            if (QualityAccess.Ready && transport?.stationPool != null)
                for (var s = 1; s < transport.stationCursor; s++)
                {
                    StationComponent station = transport.stationPool[s];

                    if (station == null || station.id != s || station.storage == null) continue;

                    for (var k = 0; k < station.storage.Length; k++)
                        if (ClampStore(ref station.storage[k], ref worst))
                            touched++;
                }

            ReportArrivalOnce();

            // **这两个必须在 ReportArrivalOnce 外面调，不能挂在它里面。**
            //
            // 那个方法第一句是 `if (_arrival != 0) return;`——「终点到货」一报就闩死。
            // 而存档里本来就躺着带品质的货，所以它往往在<b>读档后第一趟</b>就闩上了,
            // 于是挂在它里面的东西一辈子只跑一次，正好错过玩家动手之后的全部变化。
            //
            // 实测代价：玩家按要求搭好了「箱子 → 分拣器 → 新装配机」，日志里一个字都没有,
            // 而两条普查的最后一次采样是在他动手<b>之前</b>。
            // 「不许闩死」这条本文件为 <see cref="_arrival"/> 专门写过一段警告，
            // 我却把不该闩的东西挂进了那个闩里——**第三次栽在同一个方法上**。
            ReportAssemblers();
            ReportBelts();

            if (touched <= 0 || _saidSoOnce) return;

            _saidSoOnce = true;

            // **整局只说一次。** 它是常设机制而不是缺陷探针——每 30 秒吼一次
            // 只会把日志填满，还会让真正的新问题淹在里面。说一次是为了让
            // 「上限在工作」和「这段代码根本没跑」在日志上分得开。
            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质：品质总量上限正在生效——这一局第一次压回，{touched} 格超过了" +
                $"「件数 × {QualityRefineryPatches.MaxPerItem}」，最高曾到每件 {worst} 分。" +
                "上限每 30 秒压一次，之后不再重复这一行。");
        }

        internal static void Run(string why)
        {
            if (!QualityAccess.Ready && !QualityAccess.GridWritable) return;

            GameData data = GameMain.data;

            if (data == null) return;

            var slots = 0;
            var grids = 0;
            var worst = 0;

            // 一、每颗星球的物流站槽位
            if (QualityAccess.Ready && data.factories != null)
                for (var i = 0; i < data.factoryCount; i++)
                {
                    PlanetTransport transport = data.factories[i]?.transport;

                    if (transport?.stationPool == null) continue;

                    for (var s = 1; s < transport.stationCursor; s++)
                    {
                        StationComponent station = transport.stationPool[s];

                        if (station == null || station.id != s || station.storage == null) continue;

                        for (var k = 0; k < station.storage.Length; k++)
                            if (ClampStore(ref station.storage[k], ref worst))
                                slots++;
                    }
                }

            if (!QualityAccess.GridWritable) { Report(why, slots, grids, worst); return; }

            // 二、每颗星球的储物箱
            if (data.factories != null)
                for (var i = 0; i < data.factoryCount; i++)
                {
                    FactoryStorage storage = data.factories[i]?.factoryStorage;

                    if (storage?.storagePool == null) continue;

                    for (var s = 1; s < storage.storageCursor; s++)
                        grids += ClampStorage(storage.storagePool[s], ref worst);
                }

            // 三、机甲背包。**它必须单独扫**：背包不挂在任何一颗星球上，
            // 而玩家手里那一堆恰恰是最容易被看到的那一堆。
            grids += ClampStorage(data.mainPlayer?.package, ref worst);

            Report(why, slots, grids, worst);
        }

        /// <summary>
        /// <b>空格子里的品质残留要点名，不能只计数。</b>
        ///
        /// <c>count == 0</c> 而 <c>qua &gt; 0</c> 这个形状有两种成因，且应对方式相反：
        /// <list type="bullet">
        /// <item><b>舍入灰尘</b>——扣减比例用整除向下取整，多次部分出货后会剩个位数。无害。</item>
        /// <item><b>有一条路扣了件数没扣品质</b>——残留是<b>整格的分数</b>，
        /// 而那一批货已经带着 0 分走了。这是真 bug。</item>
        /// </list>
        /// 两者的差别就在残留的<b>量级</b>，而原先这条日志只说「1 格」、
        /// 连 `worst` 都因为 <c>count &gt; 0</c> 不成立而停在 0——两种成因在日志上长得一模一样。
        /// 点一次名，就能把「哪一条路漏了」从猜变成读。
        /// </summary>
        private static int _negReported;

        /// <summary>
        /// 负品质只报一次。<b>它不应该存在</b>——品质是可加量，每一笔都是
        /// 非负数的加减，出负数就意味着某处回绕了。报出来比默默归零有用。
        /// </summary>
        private static void ReportNegativeOnce(string where, int itemId, int qua)
        {
            if (System.Threading.Interlocked.Exchange(ref _negReported, 1) != 0) return;

            ItemProto proto = LDB.items.Select(itemId);

            ProjectEdenPlugin.Log.LogWarning(
                $"物品品质·负数已归零：{where}里的「"
                + $"{(proto != null ? proto.name : itemId.ToString())}」带着 {qua} 分。"
                + "**品质是可加量，永远不该为负**——成因是发射时跟着原值做了 "
                + "conv.i2（品质是 Int32，不该收窄），已在 preloader 修掉；"
                + "这一行清的是存档里已经坏掉的存量。整局只报一次。");
        }

        private static int _namedStranded;

        /// <summary>找到带品质的货之后才闩上，从此不再查。</summary>
        private static int _arrival;

        /// <summary>
        /// 「此刻一格都没有」只说一次。<b>它和 <see cref="_arrival"/> 必须是两个标志</b>——
        /// 共用一个就是第三轮那个 bug：巡检在第一次提纯之前报了「没有」，然后永久闭嘴。
        /// </summary>
        private static int _saidEmpty;

        /// <summary>
        /// <b>终点探针：储物柜或背包里第一次出现带品质的货，报一行。</b>
        ///
        /// 之前每一轮都卡在同一件事上：注入那一行是对的，中间又没有任何日志，
        /// 于是「链路通了」和「链路断在某一段」只能靠玩家去悬停看一眼。
        /// 而中间那几段（槽位 → 带子 → 分拣器 → 柜子）大半是<b>原版方法</b>，
        /// 埋不进去也不该埋——但<b>终点是我们能看到的</b>，而终点有值就证明整条链通了。
        ///
        /// <b>第一版挂错了地方，记在这里。</b> 当时挂在 <see cref="Run"/> 里，
        /// 并声称「这个扫描本来就每 30 秒跑一遍」——<b>假的</b>：<c>Run</c> 只在
        /// <c>GameData.Import</c> 后跑一次，那时玩家还什么都没生产，探针永远打不出来。
        /// 每 30 秒那一遍是另一个方法，而且它<b>故意不扫储物箱</b>。
        /// 现在挂在那一遍里，并按它的口径只看当前星球。
        /// </summary>
        private static void ReportArrivalOnce()
        {
            if (_arrival != 0 || !QualityAccess.GridReady) return;

            var bestPer = 0;
            var bestItem = 0;
            var where = "";
            var boxes = 0;
            var cells = 0;

            void Scan(StorageComponent st, string tag)
            {
                if (st?.grids == null) return;

                boxes++;

                for (var i = 0; i < st.grids.Length; i++)
                {
                    int c = st.grids[i].count;

                    if (c <= 0 || st.grids[i].itemId <= 0) continue;

                    cells++;

                    int per = QualityAccess.GetGridQua(ref st.grids[i]) / c;

                    if (per <= bestPer) continue;

                    bestPer = per;
                    bestItem = st.grids[i].itemId;
                    where = tag;
                }
            }

            // **只扫当前星球的储物箱 + 背包。** 跟着这一遍已有的口径走（见上面那句
            // 「跨星球那一圈交给读档那一次」）：全图每 30 秒扫一遍储物箱，
            // 在大存档上是一次可感的卡顿，而这个探针只是为了回答一个一次性的问题。
            FactoryStorage fs = GameMain.localPlanet?.factory?.factoryStorage;

            if (fs?.storagePool != null)
                for (var k = 1; k < fs.storageCursor; k++) Scan(fs.storagePool[k], "储物柜");

            Scan(GameMain.mainPlayer?.package, "背包");


            // **第一次扫完就报，哪怕什么都没找到。**
            //
            // 连着两轮都是这么掉的坑：日志里没有这行，于是把它读成
            // 「链路还在丢」——而实际上第一次是<b>探针挂在只跑一次的方法里</b>，
            // 第二次是<b>巡检根本没轮到</b>。「没找到」和「没跑」在日志上长得一模一样，
            // 而两者要做的事情完全相反。这是本仓库记过不止五次的形状，
            // 而我把它写进了文档又在同一个探针上犯了两次。
            //
            // 所以：扫描跑到就报一行，把看了多少个箱子、多少格、最高多少分都写出来。
            //
            // **而「没找到」绝不能闩死——第三轮就是栽在这里。** 那一版两个分支共用一个
            // 一次性标志，于是巡检在<b>本局第一次提纯之前</b>就跑完并报了「一格都没有」，
            // 然后永久闭嘴。日志行号是铁证：巡检在 1170，提纯注入在 1238。
            // 那一行当时是对的，也完全没有意义——箱子里本来就还没有提纯货。
            //
            // 现在只有<b>找到</b>才闩。没找到就继续每 30 秒查，货一到就会报。
            // 「没找到」只说一次，免得刷屏，但它不再堵住后面的结论。
            if (_arrival != 0) return;

            if (bestPer <= 0)
            {
                if (System.Threading.Interlocked.Exchange(ref _saidEmpty, 1) != 0) return;

                ProjectEdenPlugin.Log.LogInfo(
                    $"物品品质·终点巡检：已经跑过了（扫了 {boxes} 个储物箱、{cells} 格有货），"
                    + "**此刻一格带品质的都没有**。这不代表链路断了——很可能只是还没生产、"
                    + "或者货还在提纯厂槽位里。**巡检会继续每 30 秒查下去**，"
                    + "货一到就会有「终点到货」那一行。这句只说一次。");

                return;
            }

            if (System.Threading.Interlocked.Exchange(ref _arrival, 1) != 0) return;

            ItemProto proto = LDB.items.Select(bestItem);

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质·终点到货：{where}里出现了带品质的「"
                + $"{(proto != null ? proto.name : bestItem.ToString())}」，每件 {bestPer} 分。"
                + "**这一行在则【提纯厂 → 带子 → 分拣器 → 柜子】整条链是通的**，"
                + "不在则中间某一段还在丢。整局只报一次，每 30 秒查一遍（当前星球 + 背包）。");
        }

        /// <summary>
        /// 上一次报出去的那组数。<b>只在数变了的时候再报</b>——静态不刷屏，
        /// 而玩家一动手（接上带子、换了配方）立刻就能在日志里看见。
        /// </summary>
        private static long _asmSign = -1;

        /// <summary>
        /// 目标状态达成（产物缓冲里真的出现品质）之后才彻底闭嘴。
        ///
        /// <b>「没找到」绝不能闩死</b>——第一版就是栽在这里：普查在读档后第一趟巡检就跑完，
        /// 报了「一台都没有」然后永久闭嘴，而玩家是<b>那之后</b>才去接带子的，
        /// 于是后面无论接没接好，日志都不会再有一个字。
        /// 这条规矩本文件上面为 <see cref="_arrival"/> 写过一遍，同一个探针又犯了一次。
        /// </summary>
        private static int _asmDone;

        /// <summary>
        /// <b>本星球的装配机普查：直接把事实写进日志，别再靠玩家描述他搭了什么。</b>
        ///
        /// 「制造那一环通没通」连着三轮卡在同一个地方——日志里没有那一行，
        /// 而「你还没喂料」「喂了但喂料路径丢品质」「喂到了但结算没触发」三种情况
        /// <b>长得一模一样</b>，每一轮都只能靠再问一次来分。事件式探针天生分不开这三种：
        /// 它们的共同点就是「什么都没发生」。
        ///
        /// 状态式普查能分：<b>有几台机器在跑配方、几台投料格里有品质、几台产物缓冲里有品质</b>。
        /// 三个数一摆，卡在哪一段是读出来的，不是问出来的。
        /// </summary>
        private static void ReportAssemblers()
        {
            if (_asmDone != 0 || !QualityAccess.CraftReady) return;

            FactorySystem fs = GameMain.localPlanet?.factory?.factorySystem;

            if (fs?.assemblerPool == null) return;

            var total = 0;
            var running = 0;
            var fed = 0;
            var made = 0;
            var pending = 0;

            for (var i = 1; i < fs.assemblerCursor; i++)
            {
                if (fs.assemblerPool[i].id != i) continue;

                total++;

                if (fs.assemblerPool[i].recipeId <= 0) continue;

                running++;

                int[] qs = QualityAccess.GetQuaServed(ref fs.assemblerPool[i]);

                if (qs != null)
                    for (var k = 0; k < qs.Length; k++)
                        if (qs[k] > 0) { fed++; break; }

                int[] qp = QualityAccess.GetQuaProduced(ref fs.assemblerPool[i]);

                if (qp != null)
                    for (var k = 0; k < qp.Length; k++)
                        if (qp[k] > 0) { made++; break; }

                if (QualityAccess.GetQuaPending(ref fs.assemblerPool[i]) > 0) pending++;
            }

            if (total == 0) return;

            // 数没变就不吭声；变了就报。这样「你刚接上带子」在日志里是看得见的。
            long sign = (((long)total * 4096 + running) * 4096 + fed) * 4096 + made;

            if (sign == _asmSign) return;

            _asmSign = sign;

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质·制造普查（本星球）：装配机 {total} 台，其中 {running} 台在跑配方；"
                + $"投料格里有品质的 {fed} 台，在途的 {pending} 台，产物缓冲里有品质的 {made} 台。"
                + "**三个数就是诊断**：在跑但「有品质」是 0 → 品质没喂进机器（喂料那一侧）；"
                + "有品质但「产物」是 0 → 结算那一段；产物有品质却搬不出来 → 出货口那一侧。"
                + "**数变了才会再报一行**，所以接上带子之后过 30 秒就能看到它动。");

            // **汇总数说不出「那一台到底装着什么」，所以逐台再报一遍。**
            //
            // 「在跑 N 台、有品质 0 台」这一行已经连着三轮指不出下一步：
            // 机器可能根本没在吃那批料（吃的是另一堆普通货）、可能吃到了但取货那侧丢了、
            // 也可能是我的普查自己读错了字段。三种都长成「0 台」。
            // 逐台把**配方、每个投料格的件数和品质**摊开，这三种立刻分得开。
            //
            // 只在<b>有机器在跑却一台带品质都没有</b>时打，而且最多四台——
            // 它是用来结束一次排查的，不是常设输出。
            if (running <= 0 || fed > 0) return;

            var shown = 0;

            for (var i = 1; i < fs.assemblerCursor && shown < 4; i++)
            {
                if (fs.assemblerPool[i].id != i || fs.assemblerPool[i].recipeId <= 0) continue;

                int[] served = fs.assemblerPool[i].served;
                int[] req = fs.assemblerPool[i].recipeExecuteData?.requires;
                int[] qs = QualityAccess.GetQuaServed(ref fs.assemblerPool[i]);

                if (served == null) continue;

                shown++;

                var line = "";

                for (var k = 0; k < served.Length; k++)
                {
                    int id = req != null && k < req.Length ? req[k] : 0;
                    ItemProto p = id > 0 ? LDB.items.Select(id) : null;

                    line += $"　[{(p != null ? p.name : id.ToString())}] {served[k]} 件"
                            + $"／品质 {(qs != null && k < qs.Length ? qs[k].ToString() : "读不到")}";
                }

                ProjectEdenPlugin.Log.LogInfo(
                    $"物品品质·制造普查·第 {i} 台（配方 {fs.assemblerPool[i].recipeId}）：{line}"
                    + "。**件数在涨而品质是 0，就说明它吃的那批货本身没品质**"
                    + "（同一种物品，提纯过的和没提纯的长得一模一样）。");
            }

            // 产物缓冲里真的出现品质 = 这一步要验的事成了，从此闭嘴。
            if (made > 0) System.Threading.Interlocked.Exchange(ref _asmDone, 1);
        }

        private static long _beltSign = -1;

        /// <summary>
        /// <b>传送带上的货到底带不带品质。</b>
        ///
        /// 玩家报「从带子上捡起来几个，品质都是 0」，而同一局的日志里
        /// 储物柜明明收到过 50 分的铁块——两件事对不上，说明至少有一处在丢，
        /// 但**症状分不开是哪一处**：
        /// <list type="bullet">
        /// <item>生产端就没把品质放上带子 → 带上的 <c>Cargo.qua</c> 本来就是 0；</item>
        /// <item>带上有品质，是<b>取货那一步</b>丢的 → 带上不是 0。</item>
        /// </list>
        /// 手里捡起来都是 0，看不出区别。所以直接读带子。
        ///
        /// 这和上限巡检故意不扫带子不冲突：那边是<b>执行机制</b>（追一个流动的影子没意义），
        /// 这边是<b>一次性的诊断</b>，而且只在数变化时才出声。
        /// </summary>
        private static void ReportBelts()
        {
            // **两个读取器都得在。** `stack` 也是 preloader 加宽过的字段，
            // 插件侧直接写 `cargo.stack` 会抛 MissingFieldException（实测崩过一次）。
            if (QualityAccess.GetCargoQua == null || QualityAccess.GetCargoStack == null) return;

            CargoTraffic traffic = GameMain.localPlanet?.factory?.cargoTraffic;
            CargoContainer cc = traffic?.container;

            if (cc?.cargoPool == null) return;

            var live = 0;
            var withQua = 0;
            var best = 0;

            // cursor 之后的槽位是空的；池子本身可能很大，但这是每 30 秒一次的主线程扫描
            for (var i = 0; i < cc.cursor && i < cc.cargoPool.Length; i++)
            {
                int stack = QualityAccess.GetCargoStack(ref cc.cargoPool[i]);

                if (stack <= 0) continue;

                live++;

                int qua = QualityAccess.GetCargoQua(ref cc.cargoPool[i]);

                if (qua <= 0) continue;

                withQua++;

                int per = qua / stack;

                if (per > best) best = per;
            }

            long sign = ((long)live * 100003 + withQua) * 100003 + best;

            if (sign == _beltSign) return;

            _beltSign = sign;

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质·带子普查（本星球）：带上 {live} 堆货，其中 {withQua} 堆带品质，"
                + $"最高每件 {best} 分。**带上是 0 就说明生产端没把品质放上去**"
                + "（该查提纯厂/装配机的出货口）；**带上不是 0 而手里捡起来是 0**，"
                + "那就是取货那一步在丢。数变了才会再报一行。");
        }

        private static bool ClampStore(ref StationStore store, ref int worst)
        {
            if (store.itemId <= 0) return false;

            int qua = QualityAccess.GetStationQua(ref store);

            // **负数也要修，而且原先不修。** 这一句本来是 `qua <= 0` 就返回，
            // 于是负品质会在存档里永远待下去——玩家看到的是「铁块品质 -32」。
            // 负数的来源（发射时跟着原值做了 conv.i2）已经在 preloader 里修掉，
            // 但**已经胀出去的存量自己不会好**，和当初加写入器是同一个理由。
            if (qua < 0)
            {
                QualityAccess.SetStationQua(ref store, 0);

                ReportNegativeOnce("物流槽位", store.itemId, qua);

                return true;
            }

            if (qua == 0) return false;

            int cap = store.count * QualityRefineryPatches.MaxPerItem;

            if (qua <= cap) return false;

            if (store.count <= 0 && System.Threading.Interlocked.Exchange(ref _namedStranded, 1) == 0)
            {
                ItemProto proto = LDB.items.Select(store.itemId);

                ProjectEdenPlugin.Log.LogWarning(
                    $"物品品质·空格残留：「{(proto != null ? proto.name : store.itemId.ToString())}」的槽位件数已经是 0，"
                    + $"却还留着 {qua} 分。**个位数是舍入灰尘，无害；成百上千就是有一条路"
                    + "扣了件数没扣品质**——那批货是带着 0 分走的，这正是「提纯完放进背包就没分」"
                    + "的形状。整局只报这一次。");
            }

            if (store.count > 0)
            {
                int per = qua / store.count;

                if (per > worst) worst = per;
            }

            QualityAccess.SetStationQua(ref store, cap);

            return true;
        }

        private static int ClampStorage(StorageComponent storage, ref int worst)
        {
            if (storage?.grids == null) return 0;

            var fixedUp = 0;

            for (var i = 0; i < storage.grids.Length; i++)
            {
                if (storage.grids[i].itemId <= 0) continue;

                int qua = QualityAccess.GetGridQua(ref storage.grids[i]);

                if (qua < 0)
                {
                    QualityAccess.SetGridQua(ref storage.grids[i], 0);

                    ReportNegativeOnce("储物格", storage.grids[i].itemId, qua);

                    fixedUp++;

                    continue;
                }

                if (qua == 0) continue;

                int cap = storage.grids[i].count * QualityRefineryPatches.MaxPerItem;

                if (qua <= cap) continue;

                if (storage.grids[i].count > 0)
                {
                    int per = qua / storage.grids[i].count;

                    if (per > worst) worst = per;
                }

                QualityAccess.SetGridQua(ref storage.grids[i], cap);

                fixedUp++;
            }

            return fixedUp;
        }

        /// <summary>
        /// <b>干净也要报一行。</b> 只在削了东西时说话，会让「这一局没有越界」和
        /// 「这段代码根本没跑」在日志上长得一模一样——本仓库为这个形状付过五次往返。
        ///
        /// <b>但它不再是 ERROR。</b> 上限是常设机制：削到了就是它在干活，
        /// 不是待查的缺陷。把它报成错误，只会让日志里真正的新问题被淹掉。
        /// </summary>
        private static void Report(string why, int slots, int grids, int worst)
        {
            if (slots == 0 && grids == 0)
            {
                ProjectEdenPlugin.Log.LogInfo($"物品品质：{why}核对完毕，没有超过总量上限的。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质：{why}按总量上限压回——物流站槽位 {slots} 格、储物格 {grids} 格，" +
                $"最高曾到每件 {worst} 分。上限是「件数 × {QualityRefineryPatches.MaxPerItem}」，" +
                "也就是「这一格每一件都是满分」——削而不清零，提纯过的事实保留着。");
        }
    }
}
