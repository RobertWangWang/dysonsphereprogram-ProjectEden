using System.Collections.Generic;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 新建的巨型能量枢纽**默认停在充电档**，而不是原版的待机档。
    ///
    /// <b>这不是便利功能，是修一个「每一步都成功、功能却用不了」的坑。</b>
    /// <c>PlanetFactory.EntityFastFillIn</c> 的枢纽分支（IL 12E0~1403）是**按模式分支**的：
    /// 先读 <c>state</c>，充电档才认 <c>emptyId</c>、放电档才认 <c>fullId</c>，
    /// <b>待机档两样都不收</b>。而枢纽新建出来就是待机。
    ///
    /// 于是玩家的体验是：建筑造好了、皮带接上了、柜子在手里，
    /// <b>怎么塞都塞不进去，而且没有任何提示</b>——实测报上来的就是这一句
    /// 「无法放入电浆储能柜」。原版的能量枢纽也是这个默认，只是玩家早就熟悉它。
    ///
    /// 默认改成充电，是因为这条线的第一步一定是「把空柜充满」：
    /// 满柜只能从这里来，而空柜是造出来的。放电是第二步，那时玩家已经打开过面板了。
    ///
    /// <b>只动本 mod 自己的枢纽。</b> 判据是 prefab 的 <c>exchangeEnergyPerTick</c>
    /// 是否等于配置里那个值——不是按物品号，因为号会随撞车顺延。
    /// </summary>
    [HarmonyPatch]
    internal static class MegaExchangerDefaultPatches
    {
        /// <summary>充电档。原版三档是 −1 放电 / 0 待机 / 1 充电，和面板三个按钮一一对应。</summary>
        private const float Charging = 1f;

        private static int _done;
        private static int _switched;

        /// <summary>一台巨型枢纽能服务的一对空/满蓄电器。</summary>
        internal class Pair
        {
            internal long EnergyPerTick;
            internal int EmptyId;
            internal int FullId;
            internal string Key;

            /// <summary>
            /// 充满这一档的<b>一个</b>柜子要多少焦耳，也就是
            /// <c>PowerExchangerComponent.maxPoolEnergy</c> 该有的值。
            /// 取自这一档空柜自己的 <c>prefabDesc.maxAcuEnergy</c>——**不是编出来的数**，
            /// 而且 <c>machines.json</c> 里那个容量倍率一改它就跟着走。
            /// </summary>
            internal long PoolEnergy;
        }

        internal static readonly List<Pair> Pairs = new List<Pair>();

        internal static void RegisterPair(long energyPerTick, int emptyId, int fullId, string key, long poolEnergy)
        {
            foreach (Pair p in Pairs)
                if (p.EnergyPerTick == energyPerTick && p.EmptyId == emptyId)
                    return;

            Pairs.Add(new Pair
            {
                EnergyPerTick = energyPerTick, EmptyId = emptyId, FullId = fullId, Key = key,
                PoolEnergy = poolEnergy,
            });
        }

        /// <summary>按空柜（或满柜）物品号找这一档；找不到返回 null。</summary>
        internal static Pair FindPair(long energyPerTick, int itemId)
        {
            foreach (Pair p in Pairs)
            {
                if (p.EnergyPerTick != energyPerTick) continue;
                if (p.EmptyId == itemId || p.FullId == itemId) return p;
            }

            return null;
        }

        /// <summary>
        /// 充满一个这种蓄电器要多少能量。读的是它自己的 <c>prefabDesc.maxAcuEnergy</c>，
        /// 也就是原版给「这个柜子能装多少电」用的那个字段。
        /// </summary>
        internal static long VaultEnergy(int emptyItemId)
        {
            ItemProto item = LDB.items.Select(emptyItemId);
            PrefabDesc desc = item?.prefabDesc;

            return desc == null || desc == PrefabDesc.none ? 0L : desc.maxAcuEnergy;
        }

        /// <summary>
        /// 拿着另一档的柜子去点柜位图标 → 这台枢纽就改服务那一档。
        ///
        /// <b>为什么挂在这里，而不是新做一个界面。</b> 原版这个点击处理本来就读
        /// <c>player.inhandItemId</c>（IL 003F~0045），也就是说「手上拿什么」已经是它的输入了。
        /// 于是玩家做最自然的那个动作——拿着过载柜去点柜位——正好可以当切档，
        /// 而且图标当场就变，反馈是天然的，不用再教。
        ///
        /// <b>只在两边都空着时才切。</b> 柜子还在里头就改 <c>emptyId</c>，
        /// 那些柜子会变成再也取不出来的幽灵——计数还在，而 id 已经指向别的物品了。
        ///
        /// 改动**不需要自己存档**：<c>emptyId</c>/<c>fullId</c> 是逐组件字段，
        /// 原版的 <c>Export</c>/<c>Import</c> 本来就写它们。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIPowerExchangerWindow),
            nameof(UIPowerExchangerWindow.OnEmptyOrFullUIButtonClick))]
        private static void OnEmptyOrFullUIButtonClick_Prefix(UIPowerExchangerWindow __instance)
        {
            int id = __instance?.exchangerId ?? 0;
            PowerExchangerComponent[] pool = __instance?.powerSystem?.excPool;

            if (id <= 0 || pool == null || id >= pool.Length) return;

            int held = __instance.player?.inhandItemId ?? 0;

            if (held <= 0) return;

            long rate = pool[id].energyPerTick;

            // 已经是当前这一对了，交给原版处理
            if (pool[id].emptyId == held || pool[id].fullId == held) return;

            // 里头还有货就不能切：计数留着而 id 变了，那些柜子就再也取不出来
            if (pool[id].emptyCount != 0 || pool[id].fullCount != 0) return;

            foreach (Pair p in Pairs)
            {
                if (p.EnergyPerTick != rate) continue;
                if (p.EmptyId != held && p.FullId != held) continue;

                pool[id].emptyId = p.EmptyId;
                pool[id].fullId = p.FullId;

                // **换档必须连 maxPoolEnergy 一起换。** 它是「充满一个柜子要多少能量」，
                // 而两档的容量差一倍；只改 id 不改它，等离子档会按超载档的份额攒电
                //（或者反过来），表现是「换了档就再也充不满」。
                if (p.PoolEnergy > 0L)
                {
                    pool[id].maxPoolEnergy = p.PoolEnergy;

                    if (pool[id].currPoolEnergy > p.PoolEnergy) pool[id].currPoolEnergy = p.PoolEnergy;
                }

                if (_switched++ < 4)
                    ProjectEdenPlugin.Log.LogInfo(
                        $"巨型能量枢纽 #{id}：按手上那件货改服务「{p.Key}」"
                        + $"（空 {p.EmptyId} / 满 {p.FullId}）。"
                        + "emptyId/fullId 是逐组件字段且进存档，所以这个选择由原版自己存下来");

                return;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PowerSystem), nameof(PowerSystem.NewExchangerComponent))]
        private static void NewExchangerComponent_Postfix(
            PowerSystem __instance, PrefabDesc desc, int __result)
        {
            if (__result <= 0 || desc == null || !IsOurs(desc)) return;

            PowerExchangerComponent[] pool = __instance?.excPool;

            if (pool == null || __result >= pool.Length) return;

            pool[__result].targetState = Charging;
            pool[__result].state = Charging;

            RepairPoolEnergy(ref pool[__result], "新建");

            // 一次就够，但要有——「默认档改了」和「这补丁没生效」在玩家那里长得一样，
            // 而后者的症状正是塞不进柜子
            if (_done++ > 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                "巨型能量枢纽：新建的默认停在**充电档**（原版默认待机）。"
                + "待机档 EntityFastFillIn 两样都不收，玩家会以为柜子塞不进去");
        }

        /// <summary>
        /// 把 <c>maxPoolEnergy</c> 补成「充满一个当前这一档的柜子要多少能量」。
        ///
        /// <para><b>这是本仓库记过的那条「逐字段复制，漏一个就静默失效」，这次漏在枢纽上。</b></para>
        /// <c>PowerSystem.NewExchangerComponent</c> 只从 <c>PrefabDesc</c> 取<b>五个</b>字段：
        /// <c>subId</c> / <c>exchangeEnergyPerTick</c> / <c>maxExcEnergy</c> / <c>emptyId</c> /
        /// <c>fullId</c>。而 <c>MegaBuildingRegistry.ApplyExchangers</c> 只设了后四个里的三个——
        /// <c>maxExcEnergy</c> 一次都没设过，于是它继承自被克隆的**物流运输站**，
        /// 而那台根本不是枢纽。
        ///
        /// <para><b>这个字段的含义是从 IL 里读出来的，不是猜的。</b></para>
        /// <c>PowerExchangerComponent.InputUpdate</c> @0023 是
        /// <c>if (本 tick 能量 &lt; maxPoolEnergy − currPoolEnergy) 只攒不换</c>，
        /// 攒够之后 @0055 <c>currPoolEnergy −= maxPoolEnergy</c> 并把一个空柜记成满柜。
        /// 所以它就是「充满一个柜子要多少焦耳」。继承来的值偏大，枢纽就会**一直攒、永远换不出一个满柜**，
        /// 玩家看到的正是「皮带把空柜送进去了，就是不充电」。
        ///
        /// <para><b>已建成的那些必须在运行时补（第 1 号坑）。</b></para>
        /// <c>maxPoolEnergy</c> 进存档（<c>Export</c> @009A / <c>Import</c> @00AE），
        /// 而 <c>Import</c> **不**像 <c>StorageComponent</c> 那样回头从 proto 重新推导，
        /// 所以老存档里那几台不会自愈。
        /// </summary>
        private static void RepairPoolEnergy(ref PowerExchangerComponent exc, string why)
        {
            Pair p = FindPair(exc.energyPerTick, exc.emptyId);

            if (p == null || p.PoolEnergy <= 0L) return;
            if (exc.maxPoolEnergy == p.PoolEnergy) return;

            long before = exc.maxPoolEnergy;

            exc.maxPoolEnergy = p.PoolEnergy;

            if (exc.currPoolEnergy > p.PoolEnergy) exc.currPoolEnergy = p.PoolEnergy;

            if (_repaired++ >= 4) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型能量枢纽 #{exc.id}（{why}）：单柜能量 {before / 1e9:0.###} GJ → "
                + $"{p.PoolEnergy / 1e9:0.###} GJ（服务「{p.Key}」）。"
                + "这个数是「攒够多少才换出一个满柜」，偏大就表现为怎么也充不满。");
        }

        private static int _repaired;
        private static int _dumped;

        /// <summary>
        /// 读档后补齐已建成的枢纽，并**把整条链一次性打出来**。
        ///
        /// <para><b>打的是每一道闸，不是我怀疑的那一道。</b></para>
        /// 这条链有五段，每一段断了都长成同一个样子——「皮带送进去了，不充电」：
        /// 接线（<c>belt0..3</c>）、档位（<c>state</c>）、物品对（<c>emptyId/fullId</c>）、
        /// 单柜能量（<c>maxPoolEnergy</c>）、电网（<c>networkId</c>）。本仓库为「五段一个症状」
        /// 的配送器链付过好几轮，结论是**先要状态转储，不要假设**。
        ///
        /// <para><b>顺带把一个离线读不到的数打出来：<c>slotPoses.Length</c>。</b></para>
        /// <c>PowerExchangerComponent</c> 只有 <c>belt0..belt3</c> 四个接口，而
        /// <c>AlterBelt</c>（IL 0000/008F/0121/01B3）是 slot 0/1/2/3 的四路链，
        /// <b>slot ≥ 4 直接跳到出口、一个字段都不写、也不报错</b>。而巨型建筑的底盘是克隆
        /// 物流运输站来的，它有多少个 <c>slotPoses</c> 只存在 <c>resources.assets</c> 里、
        /// 离线读不到。如果这一行打出来大于 4，那么**超过前四个的接口全是死口**，
        /// 玩家接上去没有任何反应——那会是下一个要修的东西。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
        private static void GameData_Import_Postfix(GameData __instance)
        {
            PlanetFactory[] factories = __instance?.factories;

            if (factories == null) return;

            for (var f = 0; f < factories.Length; f++)
            {
                PowerSystem power = factories[f]?.powerSystem;
                PowerExchangerComponent[] pool = power?.excPool;

                if (pool == null) continue;

                for (var i = 1; i < power.excCursor && i < pool.Length; i++)
                {
                    if (pool[i].id != i) continue;

                    // **必须排在下面那道闸之前。** 那道闸按 energyPerTick 认配对，
                    // 而这里要修的恰恰就是 energyPerTick ——先过闸就永远轮不到这一句。
                    AlignRate(factories[f], ref pool[i]);

                    if (FindPair(pool[i].energyPerTick, pool[i].emptyId) == null) continue;

                    RepairPoolEnergy(ref pool[i], "读档");
                    RescueBelts(factories[f], ref pool[i]);
                    DumpOnce(factories[f], ref pool[i]);
                }
            }
        }

        private static void DumpOnce(PlanetFactory factory, ref PowerExchangerComponent exc)
        {
            if (_dumped++ >= 4) return;

            int ports = PortCount(factory, exc.entityId);

            string mode = exc.state > 0.5f ? "充电" : exc.state < -0.5f ? "放电" : "待机";

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型能量枢纽 #{exc.id} 状态：档位 {mode}（state {exc.state:0.##} / 目标 {exc.targetState:0.##}）"
                + $"，电网 {exc.networkId}，空柜 {exc.emptyId} × {exc.emptyCount}，满柜 {exc.fullId} × {exc.fullCount}"
                + $"，单柜能量 {exc.maxPoolEnergy / 1e9:0.###} GJ（已攒 {exc.currPoolEnergy / 1e9:0.###} GJ）"
                + $"，充放 {exc.energyPerTick * 60 / 1e9:0.##} GW"
                + $"，接线 belt0..3 = {exc.belt0}/{exc.belt1}/{exc.belt2}/{exc.belt3}"
                + $"（出口 {exc.isOutput0}/{exc.isOutput1}/{exc.isOutput2}/{exc.isOutput3}）"
                + $"，底盘传送带接口数 portPoses = {ports}。");

            if (ports > 4)
                ProjectEdenPlugin.Log.LogInfo(
                    $"巨型能量枢纽：底盘有 {ports} 个传送带接口，而枢纽组件只有 belt0..belt3 四个，"
                    + "**PowerSystem.SetExchangerBelt 开头就是 `if (slot < 0 || slot > 3) return;`**——"
                    + $"接在第 5~{ports} 个口上的传送带原版会一声不吭地丢掉。"
                    + "本 mod 已在那里加了改口补丁，把它挪到空着的组件槽位上，所以哪个口都能接。");
        }

        /// <summary>
        /// 这座建筑有几个<b>传送带</b>接口。
        ///
        /// <para><b>注意字段名在两个类型之间是反的，这一点栽过一次。</b></para>
        /// <c>PrefabDesc.ReadPrefab</c> @11AD 把 <c>SlotConfig.slotPoses</c> 写进
        /// <c>PrefabDesc.<b>portPoses</b></c>（传送带口），@1211 把
        /// <c>SlotConfig.insertPoses</c> 写进 <c>PrefabDesc.<b>slotPoses</b></c>（分拣器口）。
        /// 所以「传送带接到第几个口」查的是 <c>portPoses</c>——
        /// <c>BuildTool.GetLocalPorts</c> @0089 返回的正是它，而那个下标就是
        /// <c>ReadObjectConn</c> / <c>SetExchangerBelt</c> 里的 slot。
        ///
        /// 上一版这里读的是 <c>slotPoses</c>，量出来 0，于是那条「大于 4 就报警」的诊断
        /// 永远不会响——**它测的根本不是要问的那个数**。
        /// </summary>
        private static int PortCount(PlanetFactory factory, int entityId)
        {
            EntityData[] entities = factory?.entityPool;

            if (entities == null || entityId <= 0 || entityId >= entities.Length) return -1;

            PrefabDesc desc = LDB.items.Select(entities[entityId].protoId)?.prefabDesc;

            if (desc == null || desc == PrefabDesc.none || desc.portPoses == null) return -1;

            return desc.portPoses.Length;
        }

        private static int _remapped;
        private static int _rescued;
        private static int _overflow;

        /// <summary>
        /// 开机状态行：改口补丁<b>接上了没有</b>。
        ///
        /// <para>本仓库为这条规矩付过七次：<b>状态行回答「接上了没有」，事件行回答「它决定了什么」，
        /// 一个替不了另一个。</b></para>
        /// 上一版这个功能只有事件行（读档时打一次状态转储），于是玩家说「还是不充电」时，
        /// 日志里那一行只能说明**读档那一刻**没有接线，分不开「补丁没生效」
        /// 和「这局还没接带子」。读的是 <c>Harmony.GetAllPatchedMethods()</c>——
        /// 已生效的状态，不是「我调了 PatchAll 没抛异常」，所以必须排在 PatchAll 之后。
        /// </summary>
        internal static void Report()
        {
            var hooked = false;

            foreach (System.Reflection.MethodBase mb in Harmony.GetAllPatchedMethods())
                if (mb?.DeclaringType == typeof(PowerSystem) && mb.Name == nameof(PowerSystem.SetExchangerBelt))
                {
                    hooked = true;

                    break;
                }

            if (!hooked)
            {
                ProjectEdenPlugin.Log.LogError(
                    "巨型能量枢纽·传送带改口：**没有挂上** PowerSystem.SetExchangerBelt。"
                    + "那么接在第五个及以后接口上的传送带会被原版静默丢弃，枢纽永远收不到柜子。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "巨型能量枢纽·传送带改口：已挂上 PowerSystem.SetExchangerBelt。"
                + "原版那里头两条指令就是 `if (slot < 0 || slot > 3) return;`，而巨型底盘克隆自"
                + "物流运输站、接口远不止四个——接在第五个口以后的带子原版会一声不吭地丢掉。"
                + "现在会挪到空着的组件槽位上；真接了带子才会有下一行「已改到组件槽位 N」。");
        }

        /// <summary>
        /// <b>接口号大于 3 的传送带，原版会一声不吭地丢掉；这里把它挪到一个空着的组件槽位上。</b>
        ///
        /// <para><b>这就是「皮带接上了、就是不充电」的病因。</b></para>
        /// 巨型建筑的底盘克隆自<b>物流运输站</b>，而物流站的传送带口远不止四个；
        /// <c>PowerExchangerComponent</c> 却只有 <c>belt0..belt3</c> 四个字段。
        /// <c>PowerSystem.SetExchangerBelt</c> 的<b>头两条指令</b>就是
        /// <c>if (slot &lt; 0 || slot &gt; 3) return;</c>（IL 0000~0008）——
        /// 于是玩家把带子接到第五个口以后，画面上连得好好的，
        /// <c>belt0..3</c> 全是 0，**没有任何报错**。
        ///
        /// <para><b>挪位是安全的，而这一点是查出来的、不是赌的。</b></para>
        /// 组件槽位号只是记账，不含几何：<c>InsertItemToBelt</c> / <c>PickItemFromBelt</c>
        /// 拿的是 <c>beltId</c>，<c>FindTheNextSlot</c> 只在四个槽位之间轮转。
        /// 更关键的是拆带子那一路——<c>PowerSystem.DisconnectToExchanger</c>
        /// (IL 0029/0051/0079/00A1) 是拿 <c>belt0..3</c> <b>逐个和被拆的 beltId 比</b>，
        /// 按**物品号**清而不是按槽位号清，所以我们把带子放进哪个槽位它都找得回来。
        ///
        /// <para><b>不改成「只留四个口」，是因为那会把已经接好的线全废掉。</b></para>
        /// 裁 <c>portPoses</c> 也能根治，但它立刻生效于已建成的建筑：
        /// 接在第 5 个口上的带子会连不上、还得玩家自己拆了重接。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PowerSystem), nameof(PowerSystem.SetExchangerBelt))]
        private static void SetExchangerBelt_Prefix(
            PowerSystem __instance, int excId, int beltId, ref int slot, bool isOutput)
        {
            // 0~3 是原版本来就认的，别碰
            if (slot >= 0 && slot <= 3) return;
            if (beltId <= 0 || excId <= 0) return;

            PowerExchangerComponent[] pool = __instance?.excPool;

            if (pool == null || excId >= pool.Length || pool[excId].id != excId) return;
            if (FindPair(pool[excId].energyPerTick, pool[excId].emptyId) == null) return;

            int target = FreeComponentSlot(ref pool[excId], beltId);

            if (target < 0)
            {
                if (_overflow++ < 4)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"巨型能量枢纽 #{excId}：第 {slot + 1} 个接口上的传送带接不进去了——"
                        + "枢纽组件一共只有 belt0..belt3 四个槽位，已经占满。"
                        + "这一条带子不会有任何反应，拆掉它或者拆掉别的一条。");

                return;
            }

            if (_remapped++ < 8)
                ProjectEdenPlugin.Log.LogInfo(
                    $"巨型能量枢纽 #{excId}：传送带接在第 {slot + 1} 个接口上，"
                    + $"原版只认前四个（`slot > 3` 直接 return），已改到组件槽位 {target}"
                    + $"（{(isOutput ? "出口" : "入口")}，beltId {beltId}）。"
                    + "拆带子那一路是按 beltId 比对的，所以挪位不影响拆除");

            slot = target;
        }

        /// <summary>
        /// 找一个能放这条带子的组件槽位：已经是它的优先，否则第一个空的；四个都满了返回 −1。
        /// </summary>
        private static int FreeComponentSlot(ref PowerExchangerComponent exc, int beltId)
        {
            if (exc.belt0 == beltId) return 0;
            if (exc.belt1 == beltId) return 1;
            if (exc.belt2 == beltId) return 2;
            if (exc.belt3 == beltId) return 3;

            if (exc.belt0 <= 0) return 0;
            if (exc.belt1 <= 0) return 1;
            if (exc.belt2 <= 0) return 2;
            if (exc.belt3 <= 0) return 3;

            return -1;
        }

        /// <summary>
        /// 读档时把已经接好、却从来没被登记过的传送带补回来。
        ///
        /// <para><b>光有上面那个改口补丁救不了存量，这一点要说清楚。</b></para>
        /// <c>belt0..3</c> 是<b>逐组件字段、进存档</b>，而
        /// <c>SetExchangerBelt</c> 只在<b>实体新建时</b>被调用
        /// （<c>PlanetFactory.CreateEntityLogicComponents</c>，以及拆/接带子的时候）。
        /// 读档走的是 <c>PowerSystem.Import</c>，它把 0 原样读回来，
        /// <b>不会再跑一遍连线</b>。所以老存档里那几台永远是 0——
        /// 这正是本仓库第 1 号坑的形状：值烧进存档，得另给一条运行时补路。
        ///
        /// <para>连线本身从 <c>entityConnPool</c> 现读，那是原版自己的真相：
        /// <c>ReadObjectConn</c> 按 <c>objId * 16 + slot</c> 取，和 <c>portPoses</c> 的下标同一套。</para>
        /// </summary>
        private static void RescueBelts(PlanetFactory factory, ref PowerExchangerComponent exc)
        {
            if (factory == null || exc.entityId <= 0) return;

            // 已经有线就不用救；只补「一条都没有」的
            if (exc.belt0 > 0 || exc.belt1 > 0 || exc.belt2 > 0 || exc.belt3 > 0) return;

            int ports = PortCount(factory, exc.entityId);

            if (ports <= 4) return;                 // 前四个口原版自己认得，没东西可救
            if (ports > 16) ports = 16;             // entityConnPool 的步长就是 16

            EntityData[] entities = factory.entityPool;
            var found = 0;

            for (var i = 0; i < ports && found < 4; i++)
            {
                factory.ReadObjectConn(exc.entityId, i, out bool isOutput, out int otherId, out int _);

                if (otherId <= 0 || otherId >= entities.Length) continue;

                int beltId = entities[otherId].beltId;

                if (beltId <= 0) continue;

                exc.AlterBelt(found, beltId, isOutput);
                found++;
            }

            if (found <= 0) return;

            if (_rescued++ < 4)
                ProjectEdenPlugin.Log.LogInfo(
                    $"巨型能量枢纽 #{exc.id}（读档）：底盘 {ports} 个接口里有 {found} 条传送带"
                    + "从来没被登记过（原版只认前四个接口，多出来的静默丢弃），已补进组件槽位。"
                    + "**接在第五个口以后的带子，在这一版之前是完全不工作的**");
        }

        /// <summary>
        /// 是不是本 mod 的巨型枢纽。按 <c>exchangeEnergyPerTick</c> 认，不按物品号——
        /// 物品号撞车时会顺延，而这个功率是配置里写死的一个很特别的数。
        /// </summary>
        /// <summary>已经报过「充放功率对齐」的建筑。<b>每种建筑一次</b>，不是每座一次。</summary>
        private static readonly System.Collections.Generic.HashSet<int> RateLogged =
            new System.Collections.Generic.HashSet<int>();

        /// <summary>
        /// 把已建成的枢纽的<b>充放功率</b>对齐到配置值。
        ///
        /// <para><b>为什么需要：这是陷阱一。</b> <c>energyPerTick</c> 进存档
        /// （<c>Export</c> @0082 / <c>Import</c> @0096），而 <c>Import</c> 是把存下来的数
        /// 原样读回，<b>不像 <c>StorageComponent.Import</c> 那样回头从 proto 重新推导</b>。
        /// 所以改了 <c>megabuildings.json</c> 只有新建的那些拿得到新值。</para>
        ///
        /// <para><b>为什么按 protoId 认，不按功率认</b>——这一条是这个修正存在的全部理由。
        /// 本文件其余地方都用 <c>energyPerTick</c> 当键（<see cref="FindPair"/>、
        /// <see cref="IsOurs"/>、换档那一处），那在功率没变过的时候没问题；可一旦配置值变了，
        /// 存档里的老厂拿的是<b>旧</b>功率，于是它们在 <see cref="FindPair"/> 那里全部认不出来
        /// —— 不光功率不变，连 <c>maxPoolEnergy</c> 的修复和换档<b>也一起失灵</b>，
        /// 而且一个字都不报。用建筑的 protoId 当键就没有这个问题：它不随配置变。</para>
        ///
        /// <para><b>双向对齐，不是只抬。</b> 判据是枚举写入口：<c>SetEmpty</c> / <c>Import</c> /
        /// <c>PowerSystem.Import</c> / <c>NewExchangerComponent</c>——<b>没有任何界面能写它</b>
        /// （<c>UIPowerExchangerWindow._OnUpdate</c> 只读来显示）。所以不适用
        /// 「配置值是默认值不是锁」那条（那条是给充能滑条那种<b>有</b>界面写入口的字段的），
        /// 只抬不降会让这个配置项变成单向的——<c>droneCarries</c> 就是这么栽的。</para>
        /// </summary>
        private static void AlignRate(PlanetFactory factory, ref PowerExchangerComponent exc)
        {
            long want = ConfiguredRate(factory, exc.entityId, out int protoId);

            if (want <= 0L || exc.energyPerTick == want) return;

            long before = exc.energyPerTick;

            exc.energyPerTick = want;

            if (!RateLogged.Add(protoId)) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型能量枢纽：{LDB.items.Select(protoId)?.name ?? protoId.ToString()} 的充放功率"
                + $" {before * 60 / 1e9:0.##} GW → {want * 60 / 1e9:0.##} GW（已建成的按配置对齐）。"
                + "这个字段进存档而原版读档不重新推导，所以老厂只能这样补；"
                + "没有界面能改它，所以调大调小都即时生效。这行每种建筑只报一次");
        }

        /// <summary>
        /// 这座建筑在 <c>megabuildings.json</c> 里配的充放功率，0 = 不是我们的能量枢纽。
        ///
        /// 用配置里的 <c>itemId</c> 比对，和 <c>MegaThrottle</c> 认建筑是同一个口径。
        /// </summary>
        private static long ConfiguredRate(PlanetFactory factory, int entityId, out int protoId)
        {
            protoId = 0;

            EntityData[] entities = factory?.entityPool;

            if (entities == null || entityId <= 0 || entityId >= entities.Length) return 0L;

            protoId = entities[entityId].protoId;

            MegaBuildingEntry[] all = MegaBuildingRegistry.Config?.buildings;

            if (all == null || protoId <= 0) return 0L;

            foreach (MegaBuildingEntry entry in all)
            {
                if (entry == null || entry.itemId != protoId) continue;

                MegaExchangerEntry exc = entry.exchanger;

                return exc != null && exc.energyPerTick > 0L ? exc.energyPerTick : 0L;
            }

            return 0L;
        }

        private static bool IsOurs(PrefabDesc desc)
        {
            MegaBuildingEntry[] all = MegaBuildingRegistry.Config?.buildings;

            if (all == null || !desc.isPowerExchanger) return false;

            foreach (MegaBuildingEntry entry in all)
            {
                MegaExchangerEntry exc = entry?.exchanger;

                if (exc != null && exc.energyPerTick > 0L && desc.exchangeEnergyPerTick == exc.energyPerTick)
                    return true;
            }

            return false;
        }
    }
}
