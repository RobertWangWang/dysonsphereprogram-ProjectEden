// 冻结改造前实现，供等价性与微基准对照。
using HarmonyLib;
using ProjectEden.Patches.Diagnostics;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让 30 格物流站的<b>每一格都能被传送带喂料</b>，而不只是前五格。
    ///
    /// <b>症状</b>：扩容到 30 格之后，第 6 格起只收得到运输机送来的货，传送带插不进去。
    ///
    /// <b>原版那条链是这样的</b>（三处，全是实测读出来的）：
    /// <code>
    /// UpdateNeeds()                       // 把 needs[0..5] 从 storage[0..5] 填出来，写死展开六次
    /// UpdateInputSlots()                  // needIdx = -1;
    ///   TryPickItemAtRear(needs, out needIdx, …)   // 六个完整复制的块，第 k 块写 needIdx = k
    ///   InputItem(itemId, needIdx, …)              // storage[needIdx].count += stack
    /// </code>
    /// 所以<b>「白名单下标」和「储物格下标」是同一个数</b>，而两头都只认得 0..5。
    ///
    /// <b>先说一条我自己踩过的坑，因为它会丢东西。</b> 第一版以为 <c>needs</c> 是一张
    /// 可以随便填的白名单，于是把第 20 格的货填进 <c>needs[0]</c>。结果传送带把货取下来、
    /// 调 <c>InputItem(货, 0, …)</c>，而 <c>InputItem</c> 是这样的：
    /// <code>
    /// if (needIdx &lt; storage.Length &amp;&amp; storage[needIdx].itemId == itemId) 入库;
    /// else if (itemId == 1210) warperCount += stack;
    /// // else：什么都不做——而货已经从传送带上取下来了
    /// </code>
    /// **对不上就直接消失。** 教训很直白：<b>改一个字段的含义之前，要把它的消费方读完</b>。
    ///
    /// <b>现在的做法：白名单照填，但把下标的含义在 <c>InputItem</c> 门口翻译回来。</b>
    /// <list type="number">
    /// <item><c>UpdateNeeds</c> 前置：扩容站在原版库存锁内直接生成完整白名单，省去随即被覆盖的前五格写入；</item>
    /// <item><c>InputItem</c> 前置：<b>不信任传进来的 needIdx</b>，按 itemId 现扫一遍，
    /// 找到「这种货、还装得下」的那一格。</item>
    /// </list>
    ///
    /// <b>这样是结构上安全的</b>：不管白名单里填的是哪一格的货，进库的那一刻都会落到
    /// 真正要它的格子上；找不到就原样放行，退化成原版行为（最差也只是原版那个结果，
    /// 不会比不打这个补丁更糟）。而且<b>完全不用碰 <c>TryPickItemAtRear</c></b>——
    /// 那是货物 tick 的热路径，六个复制块改成循环是一次大重写，收益和风险不成比例。
    ///
    /// <b>想要货的格子多于 6 个时按秒轮换。</b> 这是本仓库已经用过一次的解法——
    /// 综合物流枢纽的配送器一次只服务一种货，那边的答案就是让 <c>filter</c> 每秒轮一轮。
    /// 与其让第 7 格永远拿不到传送带，不如让每一格都轮得到。相位由 <c>gameTick</c>
    /// 和站点号算出来，<b>不存任何状态</b>——这条路在并行 tick 上。
    ///
    /// <b>曲速器那一格原样保留</b>：<c>needs[5]</c> 在星际站上是 1210，那是原版填的，
    /// 抢了它星际站就补不了曲速器。只有它是 0（行星内物流站）时才拿来放货。
    /// </summary>
    [HarmonyPatch]
    internal static class BeforeOutputCacheReference
    {
        // 工作线程复用，仅保存本次候选物品号，不持有站点或存档引用。
        [System.ThreadStatic] private static int[] _wantingItems;
        // De Bruijn 表：将仅含最低有效位的32位掩码映射回格位下标。
        private static readonly int[] BitIndices = { 0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8, 31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9 };
        /// <summary>
        /// 把白名单从「只看前几格」改成「看全部 30 格」。
        /// 下标的含义由 <see cref="InputItem_Prefix"/> 负责翻译，所以这里填哪一格都安全。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.UpdateNeeds))]
        private static bool UpdateNeeds_Prefix(StationComponent __instance)
        {
            int[] needs = __instance?.needs;
            StationStore[] storage = __instance?.storage;
            // 只接管原有扩展路径；普通站及不足原版6位的异常形状照常走原版。
            if (needs == null || needs.Length < 6 || storage == null || storage.Length <= needs.Length) return true;
            bool measured = NeedsRefreshProbe.Active;
            if (measured) NeedsRefreshProbe.BeforeLock(storage.Length);
            try
            {
                lock (storage)
                {
                    if (measured) NeedsRefreshProbe.LockAcquired();
                    // 前五位马上会被完整重写，不必先读前五格再写一次原版结果。
                    if (needs.Length == 6)
                        needs[5] = __instance.isStellar && __instance.warperCount < __instance.warperMaxCount ? 1210 : 0;
                    // 更长白名单的最后一位原版从不写；保持旧扩展路径的保留规则。
                    BuildExpandedNeeds(__instance);
                }
            }
            finally { if (measured) NeedsRefreshProbe.ExpandedFinished(); }
            return false;
        }

        private static void BuildExpandedNeeds(StationComponent __instance)
        {
            int[] needs = __instance?.needs;
            StationStore[] storage = __instance?.storage;

            if (needs == null || storage == null || storage.Length <= needs.Length) return;

            // 最后一位若被原版填了曲速器就别动它
            int usable = needs.Length;

            if (usable > 0 && needs[usable - 1] != 0) usable--;
            if (usable <= 0) return;

            if (storage.Length <= 32)
            {
                BuildMaskedNeeds(__instance, storage, needs, usable);
                return;
            }

            var wanting = 0;
            int[] items = _wantingItems;
            if (items == null || items.Length < storage.Length)
                _wantingItems = items = new int[storage.Length];

            if (NeedsRefreshProbe.Active) NeedsRefreshProbe.SlotsVisited(storage.Length, false);
            for (var i = 0; i < storage.Length; i++)
                if (Wants(ref storage[i])) items[wanting++] = storage[i].itemId;

            NeedsRefreshProbe.ScanFinished();
            if (wanting == 0)
            {
                for (var n = 0; n < usable; n++) needs[n] = 0;

                return;
            }

            // 相位只在「想要货的比白名单多」时才有意义，否则每格都填得下
            int phase = wanting > usable
                ? (int)((LogisticsTickContext.Tick / 60 + __instance.id) % wanting)
                : 0;

            var filled = 0;
            // 只读一次完整储物格，随后从紧凑候选数组复制最多 usable 项。
            // 保留旧逻辑的两遍顺序，包括候选不足时第二遍重复候选的行为。
            for (int i = System.Math.Max(0, phase); i < wanting && filled < usable; i++)
                needs[filled++] = items[i];
            for (int i = 0; i < wanting && filled < usable; i++)
                needs[filled++] = items[i];

            for (; filled < usable; filled++) needs[filled] = 0;

            ReportOnce(__instance, wanting, usable);
        }

        private static void BuildMaskedNeeds(StationComponent station, StationStore[] storage, int[] needs, int usable)
        {
            uint mask = 0, bit = 1;
            int wanting = 0;
            var layout = StationConfiguredSlots.Get(storage, station.id, out bool rebuilt);
            // 稠密布局仍线性扫描，避免逐位解码比直接遍历更贵。
            bool sparse = layout.Count <= storage.Length / 2;
            if (NeedsRefreshProbe.Active)
                NeedsRefreshProbe.SlotsVisited((rebuilt ? storage.Length : 0) + (sparse ? layout.Count : storage.Length), rebuilt);
            if (sparse)
            {
                uint configured = layout.Mask;
                while (configured != 0)
                {
                    uint lowest = configured & unchecked(0u - configured);
                    int index = BitIndices[unchecked(lowest * 0x077CB531u) >> 27];
                    ref StationStore slot = ref storage[index];
                    if (slot.itemId != 0 && slot.count < slot.max) { mask |= lowest; wanting++; }
                    configured &= configured - 1;
                }
            }
            else
                for (int i = 0; i < storage.Length; i++, bit <<= 1)
                {
                    ref StationStore slot = ref storage[i];
                    if (slot.itemId == 0 || slot.count >= slot.max) continue;
                    mask |= bit;
                    wanting++;
                }
            NeedsRefreshProbe.ScanFinished();
            if (mask == 0)
            {
                for (int i = 0; i < usable; i++) needs[i] = 0;
                return;
            }
            int phase = wanting > usable ? (int)((LogisticsTickContext.Tick / 60 + station.id) % wanting) : 0;
            uint remaining = mask;
            for (int i = 0; i < phase; i++) remaining &= remaining - 1;
            int filled = 0;
            // 原有两遍顺序：从相位到尾部，再从头部补齐（含不足时的重复项）。
            for (int pass = 0; pass < 2 && filled < usable; pass++, remaining = mask)
                while (remaining != 0 && filled < usable)
                {
                    uint lowest = remaining & unchecked(0u - remaining);
                    int index = BitIndices[unchecked(lowest * 0x077CB531u) >> 27];
                    needs[filled++] = storage[index].itemId;
                    remaining &= remaining - 1;
                }
            for (; filled < usable; filled++) needs[filled] = 0;
            ReportOnce(station, wanting, usable);
        }

        /// <summary>
        /// <b>不信任传进来的 needIdx。</b> 它是白名单下标，而白名单现在装的是任意一格的货，
        /// 两者不再天然相等。按 itemId 现扫一遍，落到真正要它的那一格上。
        ///
        /// <b>这是整个补丁的安全保证</b>：对不上就原样放行，退化成原版行为；
        /// 绝不会出现「货从传送带上取下来了，却没有任何一格接住」——那就是第一版丢货的方式。
        ///
        /// 扫 30 格一次，只在真的有货要进站时发生，不在每 tick 的空转上。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.InputItem))]
        private static void InputItem_Prefix(StationComponent __instance, int itemId, ref int needIdx)
        {
            StationStore[] storage = __instance?.storage;

            if (storage == null || itemId <= 0) return;

            // 原版给的下标本来就对（前几格那些），什么都不用做
            if (needIdx >= 0 && needIdx < storage.Length &&
                storage[needIdx].itemId == itemId && storage[needIdx].count < storage[needIdx].max)
                return;

            for (var i = 0; i < storage.Length; i++)
            {
                if (storage[i].itemId != itemId || storage[i].count >= storage[i].max) continue;

                needIdx = i;

                return;
            }

            // 一格都没有：原样放行。原版会把它当成「对不上」处理，
            // 和不打这个补丁时的结果完全一样——不会更糟。
        }

        /// <summary>有货位、还装得下。曲速器格由原版单独处理，不在这里。</summary>
        private static bool Wants(ref StationStore slot) =>
            slot.itemId != 0 && slot.count < slot.max;

        private static int _reported;

        /// <summary>
        /// <b>无条件打，不要只在异常时说话。</b> 今天已经有四次因为「诊断只在特定条件下
        /// 才打」而白跑一轮。这一行说明白名单只有 6 个位子是原版的硬限制
        /// （<c>TryPickItemAtRear</c> 把它复制成了六个代码块），而轮换是我们能给的最好结果。
        /// </summary>
        private static void ReportOnce(StationComponent station, int wanting, int usable)
        {
            // 日志只需打印一次；之后不再让每座站竞争同一个原子写缓存行。
            if (System.Threading.Volatile.Read(ref _reported) != 0) return;
            if (System.Threading.Interlocked.Exchange(ref _reported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"物流站传送带入料已扩到全部储物格：{station.id} 号站此刻有 {wanting} 格想要货，" +
                $"传送带白名单 {usable} 个位子（原版把它复制成了六个代码块，改不了）。" +
                (wanting > usable
                    ? "多出来的按秒轮换，每一格都轮得到。"
                    : "够用，不需要轮换。") +
                "进库的格子由 itemId 现查，不再依赖白名单下标——第 6 格往后也收得到货。");
        }
    }
}
