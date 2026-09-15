using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 保证 <c>PlanetModelingManager</c> 那四个<b>按矿种 ID 当下标</b>的数组装得下我们的矿种。
    ///
    /// <b>这是那个扫描线程崩溃的落点。</b> 报错是
    /// <c>IndexOutOfRangeException at PlanetAlgorithm.GenerateVeins (IL_04F9)</c>，
    /// 而 IL_04F9 是<b>转译之后</b>的偏移，直接去原版方法里找会落在一条读字段的指令中间、
    /// 什么也说明不了。离线把转译重算一遍才定到位：Harmony 读取时会把每个短跳转展开成长跳转
    /// （2 → 5 字节），我们的转译器又把 <c>ldc.i4.s 15</c> 换成 <c>call</c>（2 → 5 字节），
    /// 按这两条重排偏移，patched 04F9 = 原版 <c>IL_0496</c>：
    ///
    /// <code>
    /// 0496: ldloc.s V_11        // veinSpots，new int[veinProtos.Length]（IL 00AE）
    /// 0498: ldloc.s V_30        // 矿种 = theme.RareVeins[i]
    /// 049A: ldelema System.Int32   ← 越界就在这
    /// </code>
    ///
    /// 也就是稀有矿掷骰命中之后的 <c>veinSpots[veinType]++</c>。
    /// <b>所以它和「稀有矿终于开始出现」是同一件事的两面</b>——概率反转修好之前那个分支根本走不到。
    ///
    /// <b>数组长度从哪来。</b> <c>PrepareWorks</c> 的定容循环是
    /// <c>size = dataArray[i].ID + 1</c>——<b>赋值不取最大</b>，所以 size 等于
    /// <b>最后一个</b>元素的 ID + 1；四个数组连同 <c>veinProtos</c> 都按它 new，
    /// 而 <c>GenerateVeins</c> 里的 <c>veinSpots</c> / <c>veinCount</c> / <c>veinOpacity</c>
    /// 又都是 <c>new [veinProtos.Length]</c>。矿种 ID 一旦大于等于这个长度就越界。
    ///
    /// <b>为什么不只靠排序。</b> <c>OreRegistry</c> 已经把矿种按 ID 升序排过、
    /// <c>VerifyVeinArrayOrder</c> 也核对过末位——但那是在 PostAddData 量的，
    /// 而 <c>PrepareWorks</c> 更晚才跑，中间隔着 LDBTool 建表和别的 mod。
    /// **「我这一步做对了」不等于「最终状态是对的」**，而那次崩溃恰好就发生在两者之间。
    ///
    /// ── 第二次崩溃，以及被实测推翻的那句话 ──
    ///
    /// 上面这段原本还写着「<c>PrepareWorks</c> 在 <c>PlanetModelingManager.Start</c> 跑，
    /// 远晚于 LDBTool 的 <c>PostAddDataAction</c>，所以时序天然没问题」。
    /// <b>那句话是错的，日志把它招了</b>：
    ///
    /// <code>
    /// 矿种数组核对：矿种表 14 条，最大编号 14 … 按 15 定容，需要 15 → 够用
    /// …（中间才是 LDBTool 建表、我们的矿种进 LDB）…
    /// 矿种数组核对：矿种表 23 条，最大编号 23 … 按 24 定容，需要 24 → 够用
    /// </code>
    ///
    /// <b><c>PrepareWorks</c> 跑了两次，第一次在我们的矿种进 LDB 之前</b>，
    /// 那时表里只有原版 14 条，数组被定成 15，而这个核对当场报了「够用」——
    /// <b>一句在那一刻完全正确、对最终状态却完全错误的假绿灯</b>。
    /// 健康的那一局是靠第二次调用救回来的；第二次没发生的那一局，数组停在 15，
    /// 于是 <c>LoadingPlanetFactoryMain</c> 里的 <c>veinProtos[veinData.type]</c>
    /// （原版 IL 0x035D–0x0370）拿矿种 15~23 一索引就
    /// <c>IndexOutOfRangeException</c>，堆栈里一个 mod 的名字都没有。
    ///
    /// 两条改动，缺一不可：
    ///
    /// <list type="number">
    /// <item><b>需要多大不能只问 <c>LDB.veins</c>。</b> 第一次调用时它还没有我们的矿种，
    /// 但 <see cref="OreRegistry.MaxVeinId"/> <b>在那之前就已经知道</b>了——
    /// 需求取两者的较大值，于是第一次就直接补到位，不再依赖「会有第二次」。</item>
    /// <item><b>在真正用到的地方再兜一次。</b> <c>LoadingPlanetFactoryMain</c> 的前置，
    /// 一次行星加载一次。<b>任何「早于使用点的准备工作」都可能被别的 mod、别的时序绕过去，
    /// 而使用点本身绕不过去。</b></item>
    /// </list>
    ///
    /// 补齐做的事和 <c>PrepareWorks</c> 尾巴上那个循环<b>逐字一致</b>
    /// （<c>veinProducts[p.ID] = p.MiningItem</c> 等四行），所以重跑一遍是幂等的。
    /// </summary>
    [HarmonyPatch]
    internal static class VeinProtoArrayPatches
    {
        /// <summary>上一次报过的「有多少/要多少」，一样就不再刷屏。</summary>
        private static int _reportedHave = -1;
        private static int _reportedNeed = -1;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetModelingManager), nameof(PlanetModelingManager.PrepareWorks))]
        private static void PrepareWorks() => Ensure("PrepareWorks 之后");

        /// <summary>
        /// <b>崩溃的落点本身</b>，也是最后一道保险。
        ///
        /// 原版这里是 <c>veinProtos[veinPool[i].type]</c>（IL 0x035D 取数组、0x0370 <c>ldelem.ref</c>），
        /// 按<b>矿种编号</b>索引。放在这里兜底的理由很简单：<b>它绕不过去</b>——
        /// 不管 <c>PrepareWorks</c> 什么时候跑、跑了几次、有没有别的 mod 插在中间，
        /// 要画这颗行星的矿脉就一定先经过这里。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlanetModelingManager), nameof(PlanetModelingManager.LoadingPlanetFactoryMain))]
        private static void LoadingPlanetFactoryMain_Prefix() => Ensure("加载行星工厂之前");

        private static void Ensure(string when)
        {
            VeinProto[] protos = LDB.veins?.dataArray;

            var maxId = 0;
            var lastId = 0;
            var count = 0;

            if (protos != null)
                foreach (VeinProto p in protos)
                {
                    if (p == null) continue;

                    count++;

                    if (p.ID > maxId) maxId = p.ID;

                    lastId = p.ID;
                }

            // **关键：不能只问 LDB。** PrepareWorks 的第一次调用早于我们的矿种进表，
            // 那时问 LDB 会得到 14，报「够用」然后把崩溃留到几分钟后的行星加载。
            // 自己的注册表在那之前就知道最大编号，两者取大。
            int need = System.Math.Max(maxId, OreRegistry.MaxVeinId) + 1;
            int have = PlanetModelingManager.veinProtos?.Length ?? 0;

            if (have < need)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"矿种数组核对（{when}）：**不够，正在补齐**。LDB 里 {count} 条、最大编号 {maxId}，"
                    + $"本 mod 注册表最大编号 {OreRegistry.MaxVeinId}；按 {have} 定容，需要 {need}。"
                    + "不补的话 LoadingPlanetFactoryMain 里的 veinProtos[矿种编号] 会越界，"
                    + "而那个堆栈里不会出现任何 mod 的名字。");

                if (protos != null && lastId != maxId)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"矿种表末位是 {lastId} 而最大是 {maxId} —— PrepareWorks 的定容循环是"
                        + "「赋值不取最大」，按末位定容所以 short 了。矿种表的顺序在 PostAddData 之后被人改过");

                Grow(need);
            }

            // **填充无条件做，不能挂在「刚才补过容量」上。**
            // 第一次 PrepareWorks 时容量可能已经够，但 LDB 里还没有我们的矿种，
            // 于是 15~23 号槽位是 null。原版 LoadingPlanetFactoryMain 对 null 是
            // `continue`——不崩，但矿脉在地表**一个都画不出来而且一声不吭**，
            // 正是本仓库记了好几遍的那个最坏形状。Fill 和原版尾巴上那个循环逐字一致，幂等。
            int filled = protos != null ? Fill(protos) : 0;

            if (have >= need && filled == 0)
            {
                // 够用也要报，否则「够用」和「这段代码没跑」分不开；但同样的数只报一次
                if (_reportedHave == have && _reportedNeed == need) return;

                _reportedHave = have;
                _reportedNeed = need;

                ProjectEdenPlugin.Log.LogInfo(
                    $"矿种数组核对（{when}）：LDB 里 {count} 条、最大编号 {maxId}，"
                    + $"本 mod 注册表最大编号 {OreRegistry.MaxVeinId}；"
                    + $"PlanetModelingManager 按 {have} 定容，需要 {need} → 够用，槽位也齐");

                return;
            }

            _reportedHave = System.Math.Max(have, need);
            _reportedNeed = need;

            ProjectEdenPlugin.Log.LogInfo(
                $"矿种数组（{when}）：容量 {have} → {System.Math.Max(have, need)}，本次补填了 {filled} 个空槽位"
                + (protos == null ? "（LDB 还是空的，等它有内容时会再填一次）" : $"，LDB 共 {count} 条"));
        }

        private static void Grow(int need)
        {
            Resize(ref PlanetModelingManager.veinProducts, need);
            Resize(ref PlanetModelingManager.veinModelIndexs, need);
            Resize(ref PlanetModelingManager.veinModelCounts, need);

            VeinProto[] p = PlanetModelingManager.veinProtos;

            if (p == null || p.Length < need)
            {
                System.Array.Resize(ref p, need);
                PlanetModelingManager.veinProtos = p;
            }
        }

        private static void Resize(ref int[] array, int need)
        {
            if (array != null && array.Length >= need) return;

            System.Array.Resize(ref array, need);
        }

        /// <summary>
        /// 和 <c>PrepareWorks</c> 尾巴上那个循环逐字一致，所以重跑是幂等的。
        /// 返回<b>本次真正补上的空槽数</b>——用它区分「本来就齐」和「刚补过」，
        /// 否则每次加载行星都会打一行一模一样的日志。
        /// </summary>
        private static int Fill(VeinProto[] protos)
        {
            VeinProto[] table = PlanetModelingManager.veinProtos;

            if (table == null) return 0;

            var filled = 0;

            foreach (VeinProto p in protos)
            {
                if (p == null || p.ID < 0 || p.ID >= table.Length) continue;

                if (table[p.ID] == null) filled++;

                PlanetModelingManager.veinProducts[p.ID] = p.MiningItem;
                PlanetModelingManager.veinModelIndexs[p.ID] = p.ModelIndex;
                PlanetModelingManager.veinModelCounts[p.ID] = p.ModelCount;
                table[p.ID] = p;
            }

            return filled;
        }
    }
}
