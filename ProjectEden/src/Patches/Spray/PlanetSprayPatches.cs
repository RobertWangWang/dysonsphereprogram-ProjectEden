// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 行星喷涂中枢：一座 30 格的行星内物流站，第一格放增产剂，
    /// <b>这颗星球上所有传送带的货物自动带上增产点数</b>，不用再铺喷涂机。
    ///
    /// <para><b>原版喷涂机的模型只有三个数，全部实测自 IL，不是估的：</b></para>
    /// <list type="bullet">
    /// <item>喷涂等级 = <c>ItemProto.Ability</c>（<c>SpraycoaterComponent.InternalUpdate</c> @013D）</item>
    /// <item>一份增产剂能喷几次 = <c>ItemProto.HpMax</c>（同上 @014A）</item>
    /// <item>写入就是 <c>cargo.inc = stack × Ability</c>（同上 @03CB）</item>
    /// </list>
    /// <c>PrefabDesc.incItemId</c> 是「这台喷涂机认哪些增产剂」的白名单，
    /// 和 <c>waterTypes</c> / <c>fuelNeeds</c> 同一族。本功能不走喷涂机，所以那张表用不上，
    /// 认哪些增产剂改由 <c>spray.json</c> 的白名单决定（默认按 <c>Ability &gt; 0</c> 自动认）。
    ///
    /// <para><b>为什么挂在 <see cref="CargoContainer.AddCargo"/> 上——一件货的 inc 是在「诞生」时确立的。</b>
    /// 枚举过全部 18 个调用点，分三族：<c>CargoPath</c> 的六处真正入带
    /// （<c>InsertItemDirect</c> / <c>InsertItemDirectWithFastCheck</c> / <c>TryInsertItemAtHead</c> ×2 /
    /// <c>TryInsertItemAtHeadAndFillBlank</c> / <c>TryUpdateItemAtHeadAndFillBlank</c>）、
    /// <c>PilerComponent.InternalUpdate</c> 五处（堆叠／拆堆会销毁重建）、
    /// <c>CargoTraffic.UpdateSplitter(Async)</c> 六处（分流器同样重建），
    /// 外加 <c>AlterBeltConnections</c> 一处（改带子拓扑）。
    /// <b>而带内的「带到带」传递不走这里</b>——那是 <c>CargoPath.Update</c> 里的缓冲区搬移。
    /// 所以这个挂点的语义正好是「每件货诞生时喷一次」：<b>O(1)，零扫描，没有每 tick 遍历传送带</b>。</para>
    ///
    /// <para><b>四个坑，都是先量出来才写的：</b></para>
    /// <list type="number">
    /// <item><b><c>CargoContainer</c> 没有指回星球的引用。</b> 它的字段只有 <c>cargoPool</c> /
    /// <c>recycleIds</c> / GPU 那几个；有 <c>planet</c> / <c>factory</c> / <c>container</c> 的是
    /// <c>CargoTraffic</c>。好在容器一共只在两处被构造（<c>PlanetFactory.Init</c> @00E5 和
    /// <c>Import</c> @11E5），所以「容器 → 星球」是两个**冷路径**钩子，不是每件货查一次族谱。</item>
    /// <item><b><c>AddCargo</c> 的 <c>inc</c> 形参正是本仓库自己的 preloader 加宽过的那个</b>
    /// （<c>Byte</c> → <c>Int16</c>）。所以用 <see cref="TargetMethods"/> **按名字**选目标——
    /// 写 <c>typeof(byte)</c> 的签名在加宽生效后解析不到，而且那种失败是**静默的**；
    /// 而且干脆**不接 <c>inc</c> 形参**，改从返回的货物号去写池子（两个重载都返回 Int32 货物号）。
    /// 写字段本身走 <see cref="CargoWidening.SetInc"/>，理由见那里。</item>
    /// <item><b>第一格的容量是「饿死别人的杠杆」，不能用物流站的默认值。</b> 本地需求格会按
    /// <c>max</c> 向物流网要货，而 <c>StationCapacityPatches</c> 把物流站格位抬到了三亿——
    /// 第一座枢纽会把全网的增产剂吸干，症状是「我别的喷涂机全停了」，而病因指不到这里。
    /// 所以第一格单独给一个小上限（<c>slotCapacity</c>，默认 3000），和钻头槽的
    /// <c>bitSlotCapacity</c> 是同一条教训。</item>
    /// <item><b>热路径 + 并行。</b> <c>AddCargo</c> 跑在并行的传送带 / 物流线程上，所以查表只用
    /// <c>ConcurrentDictionary</c> 读、扣次数用 <c>Interlocked</c>，一次分配都不做。</item>
    /// </list>
    ///
    /// <para><b>代价不大，而原因是集装。</b> 一次喷涂覆盖<b>整个堆</b>（<c>inc = stack × Ability</c>），
    /// 所以消耗是按**货物件数**算而不是按物品件数算：在 5000 层集装下，一份增产剂
    /// （<c>HpMax</c> 次）能覆盖 <c>HpMax × 5000</c> 件物品。启动时会把实测的这两个数打出来。</para>
    ///
    /// <para><b>和真实喷涂机共存，只抬不降。</b> 已经带着更高点数的货（玩家自己铺的喷涂机、
    /// 或者本 mod 的活性增产剂）不会被压低，也不会白扣一次喷涂次数——
    /// 只有真的把点数抬上去了才记账。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class PlanetSprayPatches
    {
        internal static SprayConfig Config;

        /// <summary>容器 → 星球号。冷路径填（见类注释第 1 条），热路径只读。</summary>
        private static readonly ConcurrentDictionary<CargoContainer, int> Owner =
            new ConcurrentDictionary<CargoContainer, int>();

        /// <summary>星球号 → 这颗星球上的喷涂状态。没有枢纽的星球压根不在表里。</summary>
        private static readonly ConcurrentDictionary<int, HubState> Hubs =
            new ConcurrentDictionary<int, HubState>();

        /// <summary>
        /// 一颗星球的喷涂状态。<b>它是一个类而不是结构体</b>：热路径要对
        /// <see cref="Remaining"/> 做 <c>Interlocked</c>，而对结构体字段做那件事需要
        /// 拿到 <c>ref</c>，从 <c>ConcurrentDictionary</c> 里拿不到。
        /// </summary>
        internal sealed class HubState
        {
            /// <summary>喷涂等级 = <c>ItemProto.Ability</c>。0 表示这一刻喷不了。</summary>
            internal int Level;

            /// <summary>剩余喷涂次数。<b>只用 Interlocked 改</b>（并行 tick 路径）。</summary>
            internal int Remaining;

            /// <summary>当前认的增产剂物品号，只为日志好读。</summary>
            internal int ItemId;

            /// <summary>这颗星球上枢纽实体数，只为日志好读。</summary>
            internal int Hubs;

            /// <summary>这颗星球上枢纽的站点号，缓存下来好让补料每 tick 也能跑得起（O(枢纽数)）。</summary>
            internal int[] StationIds;

            /// <summary>本窗口真的喷出去的次数。<b>只用 Interlocked 改。</b></summary>
            internal int UsedThisWindow;

            /// <summary>本窗口因为配额用光而<b>没喷到</b>的次数——这就是「部分物品喷不到」那个数。</summary>
            internal int StarvedThisWindow;

            /// <summary>整局累计没喷到的次数，报告用。</summary>
            internal int StarvedTotal;

            /// <summary>
            /// 观测到的单 tick 峰值需求（喷出去的 + 没喷到的）。缓冲按它定容。
            /// <b>单位是「每 tick」而不是「每 refillIntervalTicks」</b>——补料每 tick 都跑，
            /// 所以统计窗口就是一个 tick。第一版的日志把它标成「/窗口」，读的人会去乘 30。
            /// </summary>
            internal int PeakDemand;

            /// <summary>当前那一档增产剂一份喷几次，存下来只为让 <see cref="Tally"/> 能算目标。</summary>
            internal int PerItem;
        }

        // ── 容器 → 星球：两个冷路径钩子 ────────────────────────────────

        /// <summary>
        /// <c>CargoContainer</c> 在整个程序集里只有两处 <c>newobj</c>：
        /// <c>PlanetFactory.Init</c> 和 <c>PlanetFactory.Import</c>（枚举过）。
        /// 所以这两个后置就是完整的登记口，不需要每件货去查族谱。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.Init))]
        private static void Init_Postfix(PlanetFactory __instance) => Register(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.Import))]
        private static void Import_Postfix(PlanetFactory __instance) => Register(__instance);

        private static void Register(PlanetFactory factory)
        {
            CargoContainer c = factory?.cargoTraffic?.container;

            if (c == null) return;

            Owner[c] = factory.planetId;
        }

        // ── 喷涂本体 ──────────────────────────────────────────────────

        /// <summary>
        /// 一件货刚诞生：如果这颗星球有带电的枢纽，就把它的增产点数抬到 <c>层数 × 等级</c>。
        /// 由 <see cref="PlanetSprayCargoPatches"/> 的后置调用（那个类只装选择器，理由见那里）。
        ///
        /// <b>只抬不降，而且只有真的抬上去了才扣次数。</b> 已经带着更高点数的货（玩家自己铺的
        /// 喷涂机、活性增产剂）不该被压低，也不该白吃一次喷涂配额。
        /// </summary>
        internal static void OnCargoBorn(CargoContainer container, int cargoId)
        {
            if (cargoId <= 0 || container == null) return;

            // 没登记过的容器（理论上不该有）直接放过：宁可不喷，也不猜它属于哪颗星球
            if (!Owner.TryGetValue(container, out int planetId)) return;

            if (!Hubs.TryGetValue(planetId, out HubState hub) || hub.Level <= 0) return;

            Cargo[] pool = container.cargoPool;

            int stack = CargoWidening.GetStack(pool, cargoId);

            if (stack <= 0) return;

            int want = stack * hub.Level;

            if (want > CargoWidening.IncMax) want = CargoWidening.IncMax;

            // 已经不低于目标就什么都不做——不改、不扣
            if (CargoWidening.GetInc(pool, cargoId) >= want) return;

            // **先抢额度再写。** 反过来（先写再扣）会在额度刚好用尽的那一刻多喷一件，
            // 而那一件是凭空的增产点数；并行路径上这种竞争每秒有几千次机会。
            if (Interlocked.Decrement(ref hub.Remaining) < 0)
            {
                // 抢不到：把刚扣的那一次还回去，免得 Remaining 一路负下去
                Interlocked.Increment(ref hub.Remaining);

                // **这一件就是「喷不到」的那一件。** 数它，因为缓冲是按这个数定容的，
                // 而且没有它这个失败是完全静默的——玩家只会看到「有的喷了有的没喷」。
                Interlocked.Increment(ref hub.StarvedThisWindow);
                Interlocked.Increment(ref hub.StarvedTotal);

                return;
            }

            CargoWidening.SetInc(pool, cargoId, want);

            Interlocked.Increment(ref hub.UsedThisWindow);
            Interlocked.Increment(ref _sprayed);
        }

        // ── 补料：每隔若干 tick 从枢纽第一格换喷涂次数 ──────────────────

        /// <summary>
        /// 跑在 <c>PlanetTransport.GameTick</c> 的后置上，和实验室虚拟供料同一条路。
        /// <b>那条路是按星球并行的</b>（约 31 个工作线程，每个在不同星球上），
        /// 所以这里只碰本星球的数据，静态容器一律 <c>ConcurrentDictionary</c>。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void PlanetTransport_GameTick(PlanetTransport __instance)
        {
            if (Config == null || !Config.enabled) return;

            PlanetFactory factory = __instance?.factory;

            if (factory == null) return;

            int interval = Config.refillIntervalTicks > 0 ? Config.refillIntervalTicks : 30;

            // **发现**（走一遍站点表找枢纽）按间隔跑，它是 O(站点数) 的，和物流配对刷新同一个道理。
            if (GameMain.gameTick % interval == factory.planetId % interval)
            {
                Refill(factory, __instance);

                return;
            }

            // **而补料每 tick 都跑**，用缓存下来的枢纽站点号，代价是 O(枢纽数)。
            //
            // 原先补料也只在发现那一 tick 跑，于是配额只有「一个间隔的量」——
            // 而这一版实测出来的症状就是**部分物品喷不到**：分流器每过一次货、堆叠机每堆一次
            // 都会走货物诞生那个入口，一颗建满的星球每秒的诞生数远超一个窗口备的量，
            // 超出的先到先得、剩下的静默不喷。**这个失败模式我自己在 spray.json 的注释里
            // 写过**（「备货少于一个间隔的用量就会断喷」），然后给了个太小的默认值，
            // 而且没有任何计数去报它——那正是本仓库反复记的「加守卫的同时就要加状态行」。
            TopUpCached(factory, __instance);
        }

        private static void Refill(PlanetFactory factory, PlanetTransport transport)
        {
            StationComponent[] pool = transport.stationPool;

            if (pool == null) return;

            HubState hub = null;
            var ids = new List<int>();

            for (var i = 1; i < transport.stationCursor && i < pool.Length; i++)
            {
                StationComponent sc = pool[i];

                if (sc == null || sc.id != i || sc.storage == null) continue;
                if (!IsHub(factory, sc.entityId)) continue;

                ids.Add(i);

                if (hub == null)
                    hub = Hubs.GetOrAdd(factory.planetId, _ => new HubState());

                LayoutSlot(factory, sc);
                Charge(factory, sc, hub);
            }

            if (ids.Count == 0)
            {
                // 枢纽被拆干净了：把这颗星球整条摘掉，热路径那句 TryGetValue 立刻不再命中
                Hubs.TryRemove(factory.planetId, out HubState _);

                return;
            }

            hub.Hubs = ids.Count;
            // 存下来给每 tick 的补料用。整个赋值是原子的（引用赋值），
            // 而读那一侧只读一次到局部，所以不会看到半张表。
            hub.StationIds = ids.ToArray();
        }

        /// <summary>
        /// 每 tick 的补料：只碰缓存下来的那几座枢纽，代价 O(枢纽数)。
        /// 站点号是上一次<b>发现</b>存下的，可能已经失效（枢纽刚被拆），所以逐个重新校验
        /// <c>id</c> 和 <c>IsHub</c>——失效的那一个这一 tick 跳过，下一次发现会把表更新掉。
        /// </summary>
        private static void TopUpCached(PlanetFactory factory, PlanetTransport transport)
        {
            if (!Hubs.TryGetValue(factory.planetId, out HubState hub)) return;

            int[] ids = hub.StationIds;
            StationComponent[] pool = transport.stationPool;

            if (ids == null || pool == null) return;

            foreach (int i in ids)
            {
                if (i <= 0 || i >= pool.Length) continue;

                StationComponent sc = pool[i];

                if (sc == null || sc.id != i || sc.storage == null) continue;
                if (!IsHub(factory, sc.entityId)) continue;

                Charge(factory, sc, hub);
            }
        }

        private static bool IsHub(PlanetFactory factory, int entityId)
        {
            EntityData[] pool = factory?.entityPool;

            if (pool == null || entityId <= 0 || entityId >= pool.Length) return false;

            return pool[entityId].protoId == HubItemId;
        }

        private static int _hubItemId = -1;

        /// <summary>
        /// 枢纽的物品号。<b>按 <c>machines.json</c> 里的 key 解析，不写死数字。</b>
        /// <c>ProtoSlots.ResolveItemId</c> 撞号时会平移，而且只是一条 WARNING——
        /// 写死的号会在那之后静默指向另一个 proto（本仓库在聚变线上按同一条办的）。
        /// </summary>
        internal static int HubItemId
        {
            get
            {
                if (_hubItemId >= 0) return _hubItemId;

                _hubItemId = MachineRegistry.MachineItemIdByKey(HubKey);

                if (_hubItemId <= 0)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"行星喷涂中枢：machines.json 里找不到 key = \"{HubKey}\" 的建筑，喷涂不会生效。");

                return _hubItemId;
            }
        }

        internal const string HubKey = "spray-hub";

        /// <summary>
        /// 给第一格一个<b>小</b>上限。<b>不写物品、不写方向——那两件事归玩家。</b>
        ///
        /// <para><b>上限不能用物流站的默认值。</b> 本地需求格会按 <c>max</c> 向物流网要货，而
        /// <c>StationCapacityPatches</c> 把物流站格位抬到了三亿——第一座枢纽会把全网的增产剂
        /// 吸干，而症状（「我别的喷涂机全停了」）指不到这里。钻头槽踩过同一条。</para>
        ///
        /// <para><b>但「钉死第一格放哪一种增产剂」是错的，第一版就是那么写的。</b>
        /// 它在第一格为空时自动填上一种、每个补料 tick 都填一次，于是玩家清空或者换档之后
        /// 下一个 tick 就被写回去——那正是本仓库记过两次的「和玩家抢方向盘」：
        /// <c>MegaStationPatches.Apply</c>（「方向只在这一格刚被指派时写一次，之后归玩家」，
        /// 原先每 tick 强制写回，后果是面板上那三个按钮成了摆设）和
        /// <c>StationCapacityPatches</c>（「配置的数是默认值，不是锁」）。
        /// 想喷哪一档是**玩家的选择**——四档之间是真取舍：等级高的每堆点数多，
        /// 喷涂次数多的省增产剂。所以现在一个字都不写，玩家自己在面板上设。</para>
        ///
        /// <para><b>而容量也不能每 tick 强制写，第一版又错在这里——而且这是同一条规矩的第三次。</b>
        /// CLAUDE.md 点名了这个字段：<c>storage[].max</c> <b>就是物流站面板上那个「每格容量」输入框
        /// 写的字段</b>，每 tick 推回去的后果是玩家改完一松手就弹回来。那条规矩写着
        /// 「充能滑条先栽过一次，然后是 <c>max</c>」——所以现在是第三次，
        /// 而且栽在它明确点名的那个字段上。规矩本身就一句：<b>配置的数是默认值，不是锁。</b></para>
        ///
        /// <para>所以改成 <see cref="StationCapacityPatches"/> 一样的形状：<b>每座枢纽只引导一次</b>
        /// （用 <c>ConcurrentDictionary</c> 领号，因为这条 tick 是按星球并行的；换存档时清掉，
        /// 因为站点号会被复用）。新建的枢纽拿到一个合理的默认值，之后那个输入框归玩家，
        /// 想调到三亿就调到三亿。<c>slotCapacity</c> 填 <b>0 = 一次都不碰</b>。</para>
        ///
        /// <para>只碰第一格的容量：其余 29 格是普普通通的行星内物流站格位，全归玩家。</para>
        /// </summary>
        private static void LayoutSlot(PlanetFactory factory, StationComponent station)
        {
            if (station.storage.Length < 1) return;

            // 0 = 一次都不碰。这是「我自己管容量」的出口。
            int cap = Config.slotCapacity;

            if (cap <= 0) return;

            ref StationStore slot = ref station.storage[0];

            // 玩家还没设、或者设的不是增产剂：什么都不做。枢纽这时喷不了，状态行会说明是哪一种情况。
            if (slot.itemId <= 0 || !IsProliferator(slot.itemId)) return;

            // **每座只引导一次。** 领号在改值之前，免得并行的两个 tick 各引导一遍。
            if (!Capped.TryAdd((factory.planetId, station.id), 0)) return;

            // 只往下调，而且只在真的超了的时候——玩家一开始就设小的话一个字都不动
            if (slot.max <= cap) return;

            int was = slot.max;

            slot.max = cap;

            ReportCapOnce(was, cap);
        }

        /// <summary>
        /// 已经引导过容量的枢纽。按 (星球号, 站点号) 领号：这条 tick 按星球并行，
        /// 而站点号在新存档里会被复用，所以 <see cref="Reset"/> 要清掉。
        /// </summary>
        private static readonly ConcurrentDictionary<(int, int), byte> Capped =
            new ConcurrentDictionary<(int, int), byte>();

        private static int _capReported;
        private static int _starvedReported;

        /// <summary>
        /// 第一次出现「配额用光、有货没喷到」时报一次，并说清楚它会自己长上去。
        /// <b>没有这一行的话这个失败是完全静默的</b>——玩家只会看到「有的喷了有的没喷」，
        /// 而且会去怀疑传送带速度之类和它毫无关系的东西（实测就是这么被问到的）。
        /// </summary>
        private static void ReportStarvedOnce()
        {
            if (Interlocked.Exchange(ref _starvedReported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                "行星喷涂中枢：这一窗口有货因为**喷涂配额用光**而没喷到——"
                + "那就是「部分物品喷不到」的原因，和传送带速度无关（喷涂挂在货物诞生那一刻，"
                + "不是去扫带子）。缓冲会按实测峰值自动往上长（峰值的 2 倍），"
                + "所以一般几秒内就自己追上；持续报就说明第一格的增产剂供不上，"
                + "或者 spray.json 的 refillIntervalTicks 太长。每局只报这一行，"
                + "逐分钟的实际次数看后面那条增量。");
        }

        /// <summary>
        /// 引导容量时报一次，<b>并且说清楚它是默认值而不是锁</b>——不说的话玩家
        /// 在面板上看到一个自己没设过的数字，只能猜是不是被锁住了。
        /// </summary>
        private static void ReportCapOnce(int was, int cap)
        {
            if (Interlocked.Exchange(ref _capReported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"行星喷涂中枢：第一格容量按默认值调了一次，{was:N0} → {cap:N0}。"
                + "**这是默认值不是锁**——物流站格位默认是几亿，而本地需求格会按这个数向物流网要货，"
                + "照抄会让第一座枢纽把全网的增产剂吸干（钻头槽和催化剂槽踩过同一条）。"
                + "每座枢纽只调这一次，之后面板上那个「每格容量」输入框完全归你，改多大都不会被写回去。"
                + "想一次都不调就把 spray.json 的 slotCapacity 填 0。");
        }

        /// <summary>
        /// 把第一格里的增产剂换成喷涂次数。<b>一次只换一件</b>，剩余次数低于一件的份量才换，
        /// 所以断电或断料时不会先把一整格烧掉。
        /// </summary>
        private static void Charge(PlanetFactory factory, StationComponent station, HubState hub)
        {
            ref StationStore slot = ref station.storage[0];

            int itemId = slot.itemId;

            if (itemId <= 0 || !IsProliferator(itemId))
            {
                hub.Level = 0;

                // 「第一格还没设」和「设的不是增产剂」是两种不同的状况，玩家要采取的动作也不同，
                // 所以分开报——各报一次，不刷屏
                ReportIdleOnce(itemId);

                return;
            }

            ItemProto proto = LDB.items.Select(itemId);

            if (proto == null || proto.Ability <= 0 || proto.HpMax <= 0)
            {
                hub.Level = 0;

                return;
            }

            // 断电就停喷。判据抄原版喷涂机自己那条（consumerRatio > 0.1），不自己发明
            if (!Powered(factory, station))
            {
                hub.Level = 0;

                return;
            }

            hub.ItemId = itemId;
            hub.Level = proto.Ability;

            int perItem = proto.HpMax;

            hub.PerItem = perItem;

            // ── 缓冲按**实测需求**定容，不按拍出来的份数 ──────────────────
            //
            // 配置里那个 refillBatches 只是**下界**。真正的目标是「这颗星球一个窗口里
            // 到底要喷多少次」——而那个数只有运行时知道：它取决于这颗星球有多少分流器、
            // 堆叠机、入带口，以及它们此刻有多忙。**拍一个数必然在某些存档上太小，
            // 而太小的表现是「部分物品喷不到」且一声不响**（这一版实测到的正是这个）。
            //
            // 需求 = 真喷出去的 + 因为配额用光而没喷到的。后者不数进来的话，
            // 峰值会被配额自己截断——量出来的就永远是「刚好够」，这是本仓库
            // 「一个读数如果不会随被测量的东西变化，那就不是测量」那一条。
            int used = Interlocked.Exchange(ref hub.UsedThisWindow, 0);
            int starved = Interlocked.Exchange(ref hub.StarvedThisWindow, 0);
            int demand = used + starved;

            if (demand > hub.PeakDemand) hub.PeakDemand = demand;

            int target = TargetFor(hub, perItem);

            if (starved > 0) ReportStarvedOnce();

            while (Volatile.Read(ref hub.Remaining) < target && slot.count > 0)
            {
                slot.count--;

                // 增产剂自己也可能是被喷过的，那部分点数按件数扣掉，和物流站其它搬运一致
                if (slot.inc > 0 && slot.count >= 0)
                {
                    int drop = slot.count > 0 ? slot.inc / (slot.count + 1) : slot.inc;

                    slot.inc -= drop;
                }

                Interlocked.Add(ref hub.Remaining, perItem);
            }

            ReportOnce(hub, proto, perItem);
        }

        /// <summary>
        /// 缓冲该备到多少次喷涂：<b>配置那个份数只是下界，真正的目标是实测峰值的 2 倍</b>。
        ///
        /// <para><b>它是个方法而不是两处各算一遍，因为那正是这一版栽过三次的那个错。</b>
        /// 第一版 <see cref="Tally"/> 自己算了个 <c>峰值 × 2</c> 印出来，而 <see cref="Charge"/>
        /// 用的是 <c>max(下界, 峰值 × 2)</c>——实测日志里诊断说「缓冲已长到 40」而剩余喷涂
        /// 稳定在 240 附近，两个数各说各话。同一轮里 <c>PlanetCensus</c> 和
        /// <c>ReferenceRatePatches</c> 已经各栽过一次同形的错（诊断没有读真函数），
        /// 所以这里收拢成一个出口：<b>要印这个数就得调这个方法。</b></para>
        /// </summary>
        private static int TargetFor(HubState hub, int perItem)
        {
            int floor = perItem * (Config.refillBatches > 0 ? Config.refillBatches : 4);
            int wanted = hub.PeakDemand * 2;

            return wanted > floor ? wanted : floor;
        }

        private static bool Powered(PlanetFactory factory, StationComponent station)
        {
            PowerSystem ps = factory.powerSystem;

            if (ps == null || station.pcId <= 0 || station.pcId >= ps.consumerPool.Length) return false;

            int net = ps.consumerPool[station.pcId].networkId;

            if (net <= 0 || ps.netPool == null || net >= ps.netPool.Length) return false;

            PowerNetwork pn = ps.netPool[net];

            return pn != null && pn.consumerRatio > 0.1;
        }

        // ── 认哪些增产剂 ──────────────────────────────────────────────

        private static int[] _whitelist;

        /// <summary>
        /// 判据是<b>喷涂机 prefab 自己的 <c>PrefabDesc.incItemId[]</c></b>——那是原版对
        /// 「什么算增产剂」这件事的唯一答案（<c>SpraycoaterComponent.InternalUpdate</c>
        /// 拿 <c>Cargo.item</c> 逐个去比这张数组），而且 <see cref="ProliferatorPatches"/>
        /// 已经把本 mod 那四档追加进去了，所以 Mk.IV / Mk.V 自动被认、一行都不用补。
        ///
        /// <para><b>第一版用的是 <c>Ability &gt; 0 &amp;&amp; HpMax &gt; 0</c>，那是错的，而且错得危险。</b>
        /// 当时的理由是「那两个字段就是这件事本身」——听起来正好是本仓库偏爱的那种判据，
        /// 但它是假的：<b>那两个字段是增产剂和弹药共用的</b>。
        /// <c>ItemProto.Ability</c> 同时是<b>弹药伤害</b>（<c>TurretComponent.SetNewItem</c> 里
        /// <c>bulletDamage = proto.Ability</c>），<c>HpMax</c> 同时是<b>每箱发数</b>——
        /// 本 mod 自己的 <see cref="AmmoRegistry"/> 用的正是这两个字段，五档合金弹药的
        /// <c>Ability</c> 一路排到 2240。于是那个判据把每一种弹药都认成增产剂，
        /// 而「取 Ability 最高的当默认」必然选中<b>弹药</b>而不是任何增产剂。
        /// 后果不只是选错：枢纽会拿弹药当 2240 级的增产剂去喷，夹到 32767 点，
        /// 等于凭空的满级增产。</para>
        ///
        /// <para>教训是本仓库「同一个常量两种含义」那一条在<b>字段</b>上的版本：
        /// 一个字段「就是」某件事，只在没有第二个消费者的时候才成立——
        /// 而这里的第二个消费者是我们自己加的。<b>先枚举谁还读这个字段，再把它当判据。</b></para>
        /// </summary>
        private static bool IsProliferator(int itemId)
        {
            if (itemId <= 0) return false;

            // 配置里显式列了就以它为准（留空是默认，也是推荐）
            int[] over = Config?.proliferatorIds;

            if (over != null && over.Length > 0) return Array.IndexOf(over, itemId) >= 0;

            int[] list = _whitelist;

            if (list == null)
            {
                list = BuildWhitelist();

                // 还没建好（ProliferatorPatches 的追加发生在 PostAddData）就先不认，
                // 下一次补料再试。**宁可暂时不喷，也不拿一个会把弹药算进来的判据凑合。**
                if (list == null) return false;

                _whitelist = list;
            }

            return Array.IndexOf(list, itemId) >= 0;
        }

        /// <summary>
        /// 把所有喷涂机 prefab 的 <c>incItemId</c> 并起来。
        /// <b>按「谁有这张表」发现，不写死喷涂机的物品号</b>——和
        /// <see cref="ProliferatorPatches"/> 追加时用的是同一种发现方式，两边不会走散。
        /// </summary>
        private static int[] BuildWhitelist()
        {
            ItemProto[] items = LDB.items?.dataArray;

            if (items == null) return null;

            var set = new List<int>();

            foreach (ItemProto proto in items)
            {
                PrefabDesc desc = proto?.prefabDesc;

                if (desc?.incItemId == null || desc.incItemId.Length == 0) continue;

                foreach (int id in desc.incItemId)
                    if (id > 0 && !set.Contains(id))
                        set.Add(id);
            }

            if (set.Count == 0) return null;

            int[] arr = set.ToArray();

            ProjectEdenPlugin.Log.LogInfo(
                $"行星喷涂中枢：认得的增产剂共 {arr.Length} 种，来源是喷涂机 prefab 自己的 "
                + "incItemId 白名单（原版对「什么算增产剂」的唯一答案，活性增产剂那四档已由 "
                + "ProliferatorPatches 追加进去）："
                + string.Join("、", Array.ConvertAll(arr, id => LDB.items.Select(id)?.name ?? id.ToString())));

            return arr;
        }

        // ── 日志 ──────────────────────────────────────────────────────

        private static int _sprayed;
        private static int _reported;
        private static int _statusDone;
        private static int _idleReported;

        /// <summary>
        /// 第一格没配好时各报一次。<b>「空着」和「放了别的货」要分开说</b>——
        /// 前者是「还没设」，后者是「设错了」，玩家要做的事不一样。
        /// 而且这一行本身就是这个功能的「它决定了什么」：没有它，
        /// 「枢纽在等你配第一格」和「喷涂整个坏了」在日志里长得一模一样。
        /// </summary>
        private static void ReportIdleOnce(int itemId)
        {
            int tag = itemId <= 0 ? -1 : itemId;

            if (Interlocked.Exchange(ref _idleReported, tag) == tag) return;

            if (itemId <= 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "行星喷涂中枢：第一格还是空的，所以这一刻不喷。"
                    + "**放哪一种增产剂由你自己在面板上设**——四档之间是真取舍："
                    + "等级高的每堆点数多，喷涂次数多的省增产剂。"
                    + "设好之后把那一格挂成「本地需求」，物流网就会自动补货。");

                return;
            }

            ProjectEdenPlugin.Log.LogWarning(
                $"行星喷涂中枢：第一格放的是「{LDB.items.Select(itemId)?.name ?? itemId.ToString()}」，"
                + "它不在喷涂机的增产剂白名单里（PrefabDesc.incItemId），所以不喷。"
                + "那张表就是原版对「什么算增产剂」的答案；换成任意一种增产剂即可。");
        }

        /// <summary>
        /// 开机状态行。<b>读的是 Harmony 自己的补丁表，也就是「已生效状态」</b>，
        /// 而不是「我调过 PatchAll 而且没抛异常」——本仓库为这条付过七次账。
        /// 而且**无论开没开都打一行**：只在开启时才打的话，「关着」和「这段代码根本没进 DLL」
        /// 在日志里长得一模一样。
        /// </summary>
        internal static void Report()
        {
            if (Interlocked.Exchange(ref _statusDone, 1) != 0) return;

            if (Config == null)
            {
                ProjectEdenPlugin.Log.LogInfo("行星喷涂中枢：spray.json 没读到，功能关着。");

                return;
            }

            if (!Config.enabled)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "行星喷涂中枢：配置里关着（spray.json 的 enabled）。建筑仍然能造，但不会喷。");

                return;
            }

            var attached = false;

            foreach (MethodBase m in Harmony.GetAllPatchedMethods())
            {
                if (m.DeclaringType != typeof(CargoContainer) || m.Name != nameof(CargoContainer.AddCargo)) continue;

                HarmonyLib.Patches info = Harmony.GetPatchInfo(m);

                if (info?.Postfixes == null) continue;

                foreach (Patch p in info.Postfixes)
                    // 选择器那半边住在 PlanetSprayCargoPatches 里（理由见那个类的注释），
                    // 所以状态行要查的是**那个**类，不是本类——查错了就永远报「没挂上」
                    if (p.PatchMethod?.DeclaringType == typeof(PlanetSprayCargoPatches))
                        attached = true;
            }

            if (!attached)
            {
                ProjectEdenPlugin.Log.LogError(
                    "行星喷涂中枢：**补丁没挂上**（CargoContainer.AddCargo 上找不到本类的后置）。"
                    + "建筑能造、第一格能收增产剂，但一件货都不会被喷。"
                    + "两个重载是按名字选的，所以这通常意味着方法改名了。");

                return;
            }

            if (!CargoWidening.CargoFieldsReady)
            {
                ProjectEdenPlugin.Log.LogError(
                    "行星喷涂中枢：Cargo 的 inc / stack 字段访问器发射失败，喷涂整体不生效。"
                    + "那对访问器是运行时按字段实际宽度发射的（preloader 会把它们改成 Int16），"
                    + "发射不出来说明字段名或类型和预期不符。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"行星喷涂中枢：已接上（CargoContainer.AddCargo 两个重载）。"
                + $"第一格放增产剂，本星球上**新诞生**的每件货自动带上 层数 × 等级 的点数——"
                + $"挂在货物诞生那一刻，所以没有每 tick 遍历传送带这回事。"
                + $"上限 {CargoWidening.IncMax}（Cargo.inc 的实际宽度），"
                + $"第一格容量 {(Config.slotCapacity > 0 ? Config.slotCapacity : 3000)}，"
                + $"补料间隔 {(Config.refillIntervalTicks > 0 ? Config.refillIntervalTicks : 30)} tick。");
        }

        /// <summary>
        /// 事件行：**按增产剂种类各报一次**。报的是实测的那两个数（等级、一份喷几次）
        /// 和折算出来的覆盖量——「一份增产剂够喷多少件物品」只有把集装层数乘进去才有意义，
        /// 而那正是这条线代价小的原因。
        /// </summary>
        private static void ReportOnce(HubState hub, ItemProto proto, int perItem)
        {
            if (Interlocked.Exchange(ref _reported, proto.ID) == proto.ID) return;

            int stackLevel = GameMain.history != null ? GameMain.history.stationPilerLevel : 1;

            if (stackLevel < 1) stackLevel = 1;

            ProjectEdenPlugin.Log.LogInfo(
                $"行星喷涂中枢开始喷涂：用的是「{proto.name}」，等级 {proto.Ability}（ItemProto.Ability）、"
                + $"一份喷 {perItem} 次（ItemProto.HpMax）。"
                + $"一次喷涂覆盖**整个堆**，所以按当前集装 {stackLevel} 层算，"
                + $"一份增产剂能覆盖约 {(long)perItem * stackLevel:N0} 件物品——"
                + "消耗是按货物件数算的，不是按物品件数算的。"
                + "这一行每种增产剂只打一次。");

            // ── 等级越高，能喷满的集装层数越少 ────────────────────────────
            //
            // 写入是 cargo.inc = 层数 × 等级，而 inc 是 Int16（上限 32767）。所以高档增产剂
            // 和高集装是**互相挤**的：等级 6 时最多只能喷满 32767 ÷ 6 = 5461 层。
            // 超了不会崩、也不会写坏——SetInc 在唯一出口上夹住，表现是「这一堆的增产等级
            // 比标称低一点」，是确定的降级而不是静默腐坏。但那是个看不见的降级，
            // 所以在这里明说，而且**用实测的集装层数算**：那个数在存档里，离线读不到。
            long need = (long)stackLevel * proto.Ability;

            if (need > CargoWidening.IncMax)
                ProjectEdenPlugin.Log.LogWarning(
                    $"  ⚠ 「{proto.name}」等级 {proto.Ability} 配上 {stackLevel} 层集装需要 "
                    + $"{need} 点，超过 Cargo.inc 的上限 {CargoWidening.IncMax}——"
                    + $"满堆的货会被夹到 {CargoWidening.IncMax} 点，等价于每件约 "
                    + $"{CargoWidening.IncMax / (double)stackLevel:0.##} 级而不是 {proto.Ability} 级。"
                    + $"要吃满这一档，集装层数不能超过 {CargoWidening.IncMax / proto.Ability}"
                    + "（stations.json 的 stationPilerLevel），或者换一档等级低、喷涂次数多的增产剂。");
        }

        private static float _nextTally;
        private static int _tallyEntered;

        /// <summary>
        /// 每 60 秒一次的增量：喷了多少件货、每颗星球还剩多少次喷涂。<b>没喷也要报</b>。
        ///
        /// 挂在 <c>UIGame._OnUpdate</c> 上是因为它<b>在主线程</b>：计数在并行的货物路径上累加，
        /// 而 <c>Time.realtimeSinceStartup</c> 只能主线程读。节流用它而不是
        /// <c>GameMain.gameTick</c>——后者换存档时会倒退，于是 <c>next = tick + 间隔</c>
        /// 永远不再到期、报告静默死掉，而那看起来和「一切正常」一模一样（本仓库第 4 号坑）。
        /// 第一个窗口只等 15 秒，理由同 <c>MegaAssemblerPatches</c>：一局「进去看一眼就退」
        /// 的会话不该一行都拿不到。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate_Tally()
        {
            if (Config == null || !Config.enabled || !Config.report) return;

            // 入口无条件报一行，在任何闸之前：只要这个后置被调到过，日志里就一定有话
            if (Interlocked.Exchange(ref _tallyEntered, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    "行星喷涂中枢：统计挂点已跑到，之后每 60 秒报一次增量。");

            float now = UnityEngine.Time.realtimeSinceStartup;

            if (now < _nextTally) return;

            if (_nextTally <= 0f)
            {
                _nextTally = now + 15f;

                Interlocked.Exchange(ref _sprayed, 0);

                return;
            }

            _nextTally = now + 60f;

            int n = Interlocked.Exchange(ref _sprayed, 0);

            if (Hubs.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo("行星喷涂中枢：这一分钟没有任何星球上有带电带料的枢纽。");

                return;
            }

            var sb = new System.Text.StringBuilder();

            sb.Append("行星喷涂中枢：这一分钟喷了 ").Append(n).Append(" 件货。");

            foreach (KeyValuePair<int, HubState> kv in Hubs)
                sb.Append("　行星 ").Append(kv.Key)
                  .Append("：枢纽 ").Append(kv.Value.Hubs)
                  .Append(" 座，等级 ").Append(kv.Value.Level)
                  .Append("，剩余喷涂 ").Append(Volatile.Read(ref kv.Value.Remaining))
                  // **印的是真正在用的那个目标**（调 TargetFor），不是自己再算一遍 ——
                  // 第一版就是自己算了个「峰值 × 2」，于是诊断说 40、实际用 240，两个数各说各话
                  .Append(" 次，缓冲目标 ").Append(TargetFor(kv.Value, kv.Value.PerItem))
                  // 补料每 tick 都跑，所以「窗口」就是 1 个 tick，不是 refillIntervalTicks
                  .Append("（实测峰值 ").Append(kv.Value.PeakDemand)
                  .Append(" 次/tick，约 ").Append(kv.Value.PeakDemand * 60)
                  .Append(" 次/秒），累计没喷到 ").Append(Volatile.Read(ref kv.Value.StarvedTotal)).Append(" 件");

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>换存档时清干净：星球号和容器都会被复用。</summary>
        internal static void Reset()
        {
            Owner.Clear();
            Hubs.Clear();
            _hubItemId = -1;
            _idleReported = 0;
            // 站点号在新存档里会被复用，不清的话新存档的枢纽会被当成「已经引导过容量」
            Capped.Clear();
            _capReported = 0;
            // 白名单是按 LDB 建的、换存档不会变，所以刻意不清：清了只会在新存档里
            // 重新扫一遍全部 proto 并多打一行日志
        }
    }

    /// <summary>
    /// 只装 <see cref="CargoContainer.AddCargo"/> 那两个重载的后置。
    ///
    /// <para><b>它必须单独成一个类。</b> Harmony 不允许同一个类里既有选择器
    /// （<c>TargetMethod(s)</c>）又有逐方法的 <c>[HarmonyPatch]</c>，违反了会在
    /// <c>PatchAll</c> 里抛 <c>ArgumentException</c>——而那不是「一个补丁没生效」，
    /// 是<b>一个补丁都没生效、整个 mod 等于没装</b>。</para>
    ///
    /// <para><b>而且这一条是被 <c>tools/verify_harmony.ps1</c> 当场抓出来的，
    /// 在它自己先被补上一个盲点之后。</b> 那个检查器原先只认
    /// <c>[HarmonyTargetMethods]</c> <b>特性</b>，而 Harmony 同样按<b>方法名</b>识别
    /// 叫 <c>TargetMethods</c> 的方法（和 <c>Prepare</c> / <c>Cleanup</c> 一样是约定式发现）。
    /// 于是它对「用约定式写法混搭」这种情形报 OK——一个真事实、一个假结论。
    /// 这和本仓库记过五次的「按名字挑就会按名字漏」是同一族，只是换到了检查器自己身上。</para>
    ///
    /// <para><b>按名字选两个重载，不写签名。</b> 它们的 <c>inc</c> / <c>stack</c> 形参被本仓库的
    /// preloader 加宽了（<c>Byte</c> → <c>Int16</c>），写成 <c>typeof(byte)</c> 会在加宽生效后
    /// 解析不到——而那种失败是静默的：补丁根本没挂上，日志里一个字都没有。
    /// 后置也<b>不接 <c>inc</c> 形参</b>，只接 <c>__result</c>（两个重载都返回 Int32 货物号），
    /// 这样签名里压根不出现被加宽的类型。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class PlanetSprayCargoPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(CargoContainer)))
                if (m.Name == nameof(CargoContainer.AddCargo))
                    yield return m;
        }

        [HarmonyPostfix]
        private static void AddCargo_Postfix(CargoContainer __instance, int __result) =>
            PlanetSprayPatches.OnCargoBorn(__instance, __result);
    }

#pragma warning disable 649

    [Serializable]
    internal class SprayConfig
    {
        /// <summary>总开关。关着时建筑仍然能造，只是不喷。</summary>
        public bool enabled;

        /// <summary>第一格的容量上限（个）。物流站默认值会把全网增产剂吸干，所以单独给。</summary>
        public int slotCapacity;

        /// <summary>补料间隔 tick 数。</summary>
        public int refillIntervalTicks;

        /// <summary>一次备几份增产剂的喷涂次数。少于一个间隔的用量就会断喷。</summary>
        public int refillBatches;

        /// <summary>认哪些增产剂。留空 = 按 ItemProto.Ability &gt; 0 自动认（推荐）。</summary>
        public int[] proliferatorIds;

        /// <summary>每 60 秒报一次增量。</summary>
        public bool report;
    }

#pragma warning restore 649
}
