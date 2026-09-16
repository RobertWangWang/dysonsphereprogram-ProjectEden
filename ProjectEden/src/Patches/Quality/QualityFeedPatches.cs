using System;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// <b>喂料侧：把品质送进机器的投料格。</b>
    ///
    /// 分拣器喂料一直是好的（<c>PlanetFactory.InsertInto</c> 两个重载各有 6 处
    /// <c>quaServed</c>，1c 自动孪生出来的）。**手动喂料的四条路一条都没接上**——
    /// 全程序集扫一遍「谁写 <c>AssemblerComponent.incServed</c>」，缺孪生的正好是这四个：
    ///
    /// <list type="table">
    /// <item><term><c>PlanetFactory.EntityFastFillIn</c></term>
    /// <description>Shift 点一下，把背包/手上的料塞进机器</description></item>
    /// <item><term><c>UIAssemblerWindow.OnServingBoxChange</c></term>
    /// <description>打开面板时把投料格铺进那个临时储物框（机器 → 框）</description></item>
    /// <item><term><c>UIAssemblerWindow.SyncServingStorage</c></term>
    /// <description>面板开着时每帧刷新那个框（机器 → 框）</description></item>
    /// <item><term><c>UIAssemblerWindow.OnManualServingContentChange</c></term>
    /// <description>玩家往框里拖东西之后写回机器（框 → 机器）</description></item>
    /// </list>
    ///
    /// <b>三个 UI 的那三条是同一块镜子的两面，而且必须三条一起接。</b>
    /// 只接「框 → 机器」的话，每帧的刷新会把框里的品质抹成 0，下一次拖拽就把 0 写回机器；
    /// 只接「机器 → 框」的话，玩家放进去的好料一写回就变 0。
    /// 中间那个 <c>servingStorage</c> 是个真的 <c>StorageComponent</c>，
    /// 它的 <c>GRID.qua</c> 早就是主干道载荷——鼠标拖拽（<c>UIStorageGrid.HandPut/HandTake</c>）
    /// 已经在给它写品质了，缺的只是它和 <c>quaServed</c> 之间的这两句拷贝。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityFeedPatches
    {
        private static int _reportedFast;
        private static int _reportedPanel;

        internal static void Report()
        {
            if (!QualityAccess.CraftReady || !QualityAccess.GridWritable
                || QualityAccess.GetInhandQua == null)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质·喂料：**没接上**——quaServed / GRID.qua / 手上那一格，三个访问器缺了至少一个。"
                    + "这种情况下 Shift 塞料和面板手动投料会把品质丢在门口，而且不报错。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "物品品质·喂料：已接线（Shift 塞料 / 面板那个临时投料框来回两个方向）。"
                + "分拣器喂料本来就是好的。第一次真的喂进品质时会再报一行。");
        }

        // ── 面板上那个临时投料框 ────────────────────────────────

        /// <summary>机器 → 框：把 <c>quaServed[i]</c> 铺进框里那一格，和 <c>incServed</c> 并排。</summary>
        private static void MachineToBox(UIAssemblerWindow win)
        {
            if (!Bind(win, out AssemblerComponent[] pool, out int asmId, out StorageComponent box)) return;

            int[] qs = QualityAccess.GetQuaServed(ref pool[asmId]);

            if (qs == null) return;

            for (var i = 0; i < box.grids.Length && i < qs.Length; i++)
                QualityAccess.SetGridQua(ref box.grids[i], qs[i]);
        }

        /// <summary>框 → 机器：玩家拖完之后，把框里那一格的品质写回投料格。</summary>
        private static void BoxToMachine(UIAssemblerWindow win)
        {
            if (!Bind(win, out AssemblerComponent[] pool, out int asmId, out StorageComponent box)) return;

            int[] qs = QualityAccess.GetQuaServed(ref pool[asmId]);

            if (qs == null) return;

            var any = false;

            for (var i = 0; i < box.grids.Length && i < qs.Length; i++)
            {
                qs[i] = QualityAccess.GetGridQua(ref box.grids[i]);

                if (qs[i] > 0) any = true;
            }

            if (any) ReportOnce(ref _reportedPanel, "面板手动投料");
        }

        /// <summary>
        /// 三个 UI 挂点共用的解绑。**每个挂点都自己重新解一遍**，
        /// 因为原版那三个方法各有各的提前返回，而这里是后置——
        /// 靠「它跑到了哪一步」来推断状态，是这份文件记过好几次的那种错。
        /// </summary>
        private static bool Bind(UIAssemblerWindow win, out AssemblerComponent[] pool,
            out int asmId, out StorageComponent box)
        {
            pool = null;
            asmId = 0;
            box = null;

            if (win == null || !QualityAccess.CraftReady || !QualityAccess.GridWritable) return false;

            asmId = win.assemblerId;

            if (asmId <= 0) return false;

            pool = win.factorySystem?.assemblerPool;

            if (pool == null || asmId >= pool.Length || pool[asmId].id != asmId) return false;
            if (pool[asmId].recipeId <= 0) return false;

            box = win.servingStorage;

            return box?.grids != null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIAssemblerWindow), nameof(UIAssemblerWindow.OnServingBoxChange))]
        private static void OnServingBoxChange_Postfix(UIAssemblerWindow __instance) => MachineToBox(__instance);

        /// <summary>
        /// <b>必须显式写空参数表选重载。</b> <c>SyncServingStorage</c> 有两个
        /// （无参的那个只是转调 <c>(ref AssemblerComponent)</c> 那个），
        /// 而按裸名字打一个重载方法会在 <c>PatchAll</c> 抛 <c>AmbiguousMatchException</c>
        /// ——那是整个 mod 当场倒掉，不是这一个补丁失效。
        /// 挂无参那个就够：它跑完的时候框已经刷好了。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIAssemblerWindow), nameof(UIAssemblerWindow.SyncServingStorage), new Type[0])]
        private static void SyncServingStorage_Postfix(UIAssemblerWindow __instance) => MachineToBox(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIAssemblerWindow), nameof(UIAssemblerWindow.OnManualServingContentChange))]
        private static void OnManualServingContentChange_Postfix(UIAssemblerWindow __instance) =>
            BoxToMachine(__instance);

        // ── Shift 点一下塞料 ────────────────────────────────────

        private static int[] _servedBefore;
        private static int[] _playerBefore;
        private static int _armedAsm;

        /// <summary>
        /// <b>「玩家身上少了多少分」是量出来的，不是从侧信道接的——而这一次是被迫的。</b>
        ///
        /// 协议本来是「被调方在返回前写寄存器，调用方读回来」。实测
        /// <c>Player.TakeItemFromPlayer</c> **两条分支都把它擦掉了**：
        /// 背包那一支在 <c>TakeTailItems</c> 之后紧跟 <c>ldc.i4.0 ; stsfld Q0</c>（IL 0025），
        /// 手上那一支算出了拿走的那份品质（<c>ProjectEdenQualityChannel::Split</c>，IL 00A6）
        /// 却只用它算了个余数，随后同样擦掉（IL 00E6）。
        /// 所以 <c>EntityFastFillIn</c> 读回来的永远是 0 ——**这是「擦除不等于覆盖」那条的又一例**，
        /// 而且它在被调方体内，调用方这边再怎么转译都够不着。
        ///
        /// 但玩家那一侧**已经正确扣掉了**（背包格子和手上那一格都是孪生过的），
        /// 所以「玩家少了多少」就是「机器该多多少」，量差值是精确的，不是近似。
        ///
        /// 这条路只由玩家操作触发，不在 tick 上，所以这里用普通静态字段。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.EntityFastFillIn))]
        private static void EntityFastFillIn_Prefix(PlanetFactory __instance, int entityId)
        {
            _armedAsm = 0;

            if (!QualityAccess.CraftReady || !QualityAccess.GridReady) return;
            if (__instance?.entityPool == null || entityId <= 0) return;
            if (entityId >= __instance.entityPool.Length) return;

            int asmId = __instance.entityPool[entityId].assemblerId;

            if (asmId <= 0) return;

            AssemblerComponent[] pool = __instance.factorySystem?.assemblerPool;

            if (pool == null || asmId >= pool.Length || pool[asmId].id != asmId) return;
            if (pool[asmId].recipeId <= 0) return;

            int[] requires = pool[asmId].recipeExecuteData?.requires;
            int[] served = pool[asmId].served;

            if (requires == null || served == null) return;

            int n = requires.Length < served.Length ? requires.Length : served.Length;

            if (n <= 0) return;

            if (_servedBefore == null || _servedBefore.Length < n) _servedBefore = new int[n];
            if (_playerBefore == null || _playerBefore.Length < n) _playerBefore = new int[n];

            for (var i = 0; i < n; i++)
            {
                _servedBefore[i] = served[i];
                _playerBefore[i] = PlayerQua(requires[i]);
            }

            _armedAsm = asmId;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.EntityFastFillIn))]
        private static void EntityFastFillIn_Postfix(PlanetFactory __instance)
        {
            int asmId = _armedAsm;

            _armedAsm = 0;

            if (asmId <= 0) return;

            AssemblerComponent[] pool = __instance?.factorySystem?.assemblerPool;

            if (pool == null || asmId >= pool.Length) return;

            int[] requires = pool[asmId].recipeExecuteData?.requires;
            int[] served = pool[asmId].served;
            int[] qs = QualityAccess.GetQuaServed(ref pool[asmId]);

            if (requires == null || served == null || qs == null) return;

            var credited = 0;

            for (var i = 0; i < _servedBefore.Length && i < served.Length && i < qs.Length; i++)
            {
                int gained = served[i] - _servedBefore[i];

                if (gained <= 0) continue;

                // 同一种料出现在两个格子里（原版配方没有这种，但不该靠它）：
                // 玩家那边的差值是这两格共同造成的，所以按各自进料的件数分。
                int share = SameItemGained(requires, served, i);

                if (share <= 0) continue;

                int lost = _playerBefore[i] - PlayerQua(requires[i]);

                if (lost <= 0) continue;

                var give = (int)((long)lost * gained / share);

                qs[i] += give;
                credited += give;
            }

            if (credited > 0) ReportOnce(ref _reportedFast, "Shift 塞料");
        }

        /// <summary>这一次进料里，和 <paramref name="slot"/> 同一种料的总件数。</summary>
        private static int SameItemGained(int[] requires, int[] served, int slot)
        {
            var total = 0;

            for (var j = 0; j < _servedBefore.Length && j < served.Length && j < requires.Length; j++)
            {
                if (requires[j] != requires[slot]) continue;

                int gained = served[j] - _servedBefore[j];

                if (gained > 0) total += gained;
            }

            return total;
        }

        /// <summary>玩家身上这一种货的品质总分：背包 + 鼠标手上那一格。</summary>
        private static int PlayerQua(int itemId)
        {
            if (itemId <= 0) return 0;

            Player p = GameMain.mainPlayer;

            if (p == null) return 0;

            var qua = 0;

            StorageComponent.GRID[] grids = p.package?.grids;

            if (grids != null)
                for (var i = 0; i < grids.Length; i++)
                {
                    if (grids[i].count <= 0 || grids[i].itemId != itemId) continue;

                    qua += QualityAccess.GetGridQua(ref grids[i]);
                }

            if (p.inhandItemId == itemId && p.inhandItemCount > 0 && QualityAccess.GetInhandQua != null)
                qua += QualityAccess.GetInhandQua(p);

            return qua;
        }

        private static void ReportOnce(ref int flag, string what)
        {
            if (System.Threading.Interlocked.Exchange(ref flag, 1) != 0) return;

            ProjectEdenPlugin.Log.LogWarning(
                $"物品品质·喂料：**{what}第一次把品质送进了投料格**。"
                + "在这之前这条路上的品质恒为 0——只有分拣器喂料是好的，"
                + "所以「手动塞进去的料造出来是 0 分」不是结算那一段的问题。整局每条路各报一次。");
        }
    }
}
