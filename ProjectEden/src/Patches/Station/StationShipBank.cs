using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ProjectEden.Patches.Station
{
    /// <summary>
    /// 把星际运输船的「哪个泊位空着」从一个 <c>UInt64</c> 位图搬到一张**旁挂位图**，
    /// 于是 64 艘这个硬上限没有了。
    ///
    /// <b>为什么不用 preloader。</b> 全程序集只有 13 个方法碰
    /// <c>idleShipIndices</c> / <c>workShipIndices</c>，而其中八个是纯翻位的小方法
    /// （最大的 <c>HasShipIndex</c> 才 25 条，<c>IdleShipGetToWork</c> 整个方法就是
    /// 「清 idle 那一位、置 work 那一位」）。整体前缀替换这八个，再转译
    /// <c>ShipRenderersOnTick</c> 里两处内联取位，位图就彻底换了主人——
    /// 改字段类型要 preloader，改八个小方法不用。
    ///
    /// <b>存档是免费的，这是本设计最关键的一点。</b> 位图**整个是派生量**：
    /// <list type="bullet">
    /// <item>work 位 ⇔ <c>workShipDatas[k].shipIndex</c>，k ∈ [0, workShipCount)。
    /// <c>workShipDatas</c> 前 <c>workShipCount</c> 项是**在飞船的紧凑表**
    /// （<c>InternalTickRemote</c> @0529 回港时用 <c>Array.Copy</c> 往前挪），
    /// 每艘身上带着自己的泊位号，而 <c>ShipData.Import</c> @0209 把它读回来了。</item>
    /// <item>idle 位由**原版自己**每 tick 对账补齐：<c>ShipRenderersOnTick</c>
    /// @0000–@00A1 先数出当前 idle 位数，再和 <c>idleShipCount</c> 求差，多退少补，
    /// 末尾还有一句 <c>Assert.Zero(delta)</c>。</item>
    /// </list>
    /// 所以读档之后一个 tick 之内就自己长回正确状态：**不用加存档块、不用升 SaveVersion、
    /// 老档和没装本 mod 的档都照常开**。这是「重跑派生，而不是存一份平行拷贝」那条
    /// （<c>InitFluids</c> 那一族）又一次生效。
    ///
    /// <b>对账器跑在每颗星球上，不只是脚下这颗。</b> 名字里的 Renderers 有误导性——
    /// 它是「对账 + 渲染」，而 <c>InternalTickRemote</c> @3082 是**无条件**调它的
    /// （@307B 起是直线代码，前面那个 <c>blt</c> 是上一个循环的回边）。
    /// 要是它只在本地星球跑，远方星球的 idle 位永远补不上，派船会直接停摆。
    /// </summary>
    [HarmonyPatch]
    internal static class StationShipBank
    {
        /// <summary>一个 <c>ulong</c> 装 64 位。</summary>
        private const int Bits = 64;

        /// <summary>
        /// 旁挂位图。<b>按引用作键</b>：<c>StationComponent</c> 是类（不是结构体），
        /// 而 <c>(planetId, id)</c> 在 <c>Reset</c> 里已经被清掉了，拿不到。
        /// </summary>
        private static readonly ConcurrentDictionary<StationComponent, Bank> Banks =
            new ConcurrentDictionary<StationComponent, Bank>(ByReference.Instance);

        /// <summary>
        /// 一格热缓存，<b>必须 <c>[ThreadStatic]</c></b>——<c>PlanetTransport.GameTick</c>
        /// 跑在 ~31 个工作线程上、每颗星球一个。
        ///
        /// <b>它不是可选的优化。</b> 对账器每 tick 每站要按泊位数循环一遍，256 泊位 ×
        /// 两千座站 × 60 tick/s ≈ 每秒三千万次查表；走字典是几百毫秒的 CPU，
        /// 走这一格引用比较是零点几纳秒。本仓库「绝不在 tick 路径上分配」的同一条理由。
        /// </summary>
        [ThreadStatic] private static StationComponent _hotOwner;

        [ThreadStatic] private static Bank _hot;

        [ThreadStatic] private static int _hotGeneration;

        /// <summary>
        /// 位图表的世代号。**没有它那格热缓存会脏掉，而且只在一种情况下脏**：
        /// 站被回收再重建（<c>Reset</c> 然后 <c>Init</c>）时，<c>StationComponent</c>
        /// 这个**对象是复用的**，于是别的线程手里那份 <c>_hotOwner</c> 引用比较照样通过，
        /// 拿回一张属于上一座站的位图。<c>[ThreadStatic]</c> 意味着在 A 线程清空清不到 B 线程，
        /// 所以只能让缓存自己认出过期——一个 int 比较。
        /// </summary>
        private static int _generation;

        private static long _grownSlots;
        private static int _reported;

        private sealed class Bank
        {
            internal ulong[] Idle;
            internal ulong[] Work;

            /// <summary>这张位图当前按多少个泊位开的。和 <c>workShipDatas.Length</c> 对齐。</summary>
            internal int Slots;
        }

        /// <summary>引用相等比较器。.NET Framework 4.7.2 没有内置的，十行自己写。
        /// 不用默认比较器是因为「<c>StationComponent</c> 没有重写 Equals」是个假设，
        /// 而这里不需要这个假设。</summary>
        private sealed class ByReference : IEqualityComparer<StationComponent>
        {
            internal static readonly ByReference Instance = new ByReference();

            public bool Equals(StationComponent a, StationComponent b) => ReferenceEquals(a, b);

            public int GetHashCode(StationComponent o) => RuntimeHelpers.GetHashCode(o);
        }

        // ── 位图本体 ───────────────────────────────────────────

        private static Bank For(StationComponent station)
        {
            int generation = System.Threading.Volatile.Read(ref _generation);

            if (_hot != null && _hotGeneration == generation && ReferenceEquals(_hotOwner, station)) return _hot;

            Bank bank = Banks.GetOrAdd(station, Derive);

            // 泊位数变了（读档补齐数组、或者拆了重建）就重开一张，并按当前状态重新派生。
            if (bank.Slots != SlotsOf(station)) Rebuild(bank, station);

            _hotOwner = station;
            _hot = bank;
            _hotGeneration = generation;

            return bank;
        }

        /// <summary>让所有线程手里那格热缓存作废。</summary>
        private static void BumpGeneration() => System.Threading.Interlocked.Increment(ref _generation);

        private static int SlotsOf(StationComponent station) =>
            station?.workShipDatas == null ? 0 : station.workShipDatas.Length;

        private static Bank Derive(StationComponent station)
        {
            var bank = new Bank();

            Rebuild(bank, station);

            return bank;
        }

        /// <summary>
        /// 按**存档里就有的东西**重新派生整张位图。
        ///
        /// work 位从在飞船的紧凑表里逐艘取 <c>shipIndex</c>；idle 位**故意留空**，
        /// 交给原版自己的对账器在下一个 tick 补到 <c>idleShipCount</c>。
        /// 少写一份等价逻辑，就少一处会和原版悄悄分叉的地方。
        /// </summary>
        private static void Rebuild(Bank bank, StationComponent station)
        {
            int slots = SlotsOf(station);
            int words = slots / Bits + 1;

            bank.Idle = new ulong[words];
            bank.Work = new ulong[words];
            bank.Slots = slots;

            if (station?.workShipDatas == null) return;

            int flying = station.workShipCount;

            if (flying > station.workShipDatas.Length) flying = station.workShipDatas.Length;

            for (var k = 0; k < flying; k++)
            {
                int slot = station.workShipDatas[k].shipIndex;

                if (slot >= 0 && slot < slots) bank.Work[slot / Bits] |= 1UL << (slot % Bits);
            }

            Mirror(bank, station);
        }

        /// <summary>
        /// 把低 64 位写回原版那两个字段。
        ///
        /// <b>不是为了让它们继续工作，是为了它们不撒谎。</b> 原版 <c>Export</c> @01D3/@01DF
        /// 照样会把这两个字段写进存档，别的 mod 也可能读它们；镜像一份低位之后，
        /// 那些读者看到的至少是**前 64 个泊位的真实状态**，而不是一份随机残留。
        /// </summary>
        private static void Mirror(Bank bank, StationComponent station)
        {
            station.idleShipIndices = bank.Idle.Length > 0 ? bank.Idle[0] : 0UL;
            station.workShipIndices = bank.Work.Length > 0 ? bank.Work[0] : 0UL;
        }

        private static bool Test(ulong[] words, int index) =>
            index >= 0 && index / Bits < words.Length
            && (words[index / Bits] & (1UL << (index % Bits))) != 0UL;

        private static void Set(ulong[] words, int index, bool on)
        {
            if (index < 0 || index / Bits >= words.Length) return;

            if (on) words[index / Bits] |= 1UL << (index % Bits);
            else words[index / Bits] &= ~(1UL << (index % Bits));
        }

        /// <summary>给转译进 <c>ShipRenderersOnTick</c> 的那两处内联取位用。</summary>
        internal static bool HasIdleBit(StationComponent station, int index) =>
            Test(For(station).Idle, index);

        // ── 八个整体替换的小方法 ──────────────────────────────
        //
        // 全部是前缀 + return false。它们在原版里各自都不超过 36 条指令、
        // 且**只做翻位**（见类注释），所以「整体替换」在这里不是接管一段逻辑，
        // 而是换掉一个存储后端。

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.AddIdleShip))]
        private static bool AddIdleShip(StationComponent __instance, int index)
        {
            Bank bank = For(__instance);

            Set(bank.Idle, index, true);
            Mirror(bank, __instance);

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.RemoveIdleShip))]
        private static bool RemoveIdleShip(StationComponent __instance, int index)
        {
            Bank bank = For(__instance);

            Set(bank.Idle, index, false);
            Mirror(bank, __instance);

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.IdleShipGetToWork))]
        private static bool IdleShipGetToWork(StationComponent __instance, int index)
        {
            Bank bank = For(__instance);

            Set(bank.Idle, index, false);
            Set(bank.Work, index, true);
            Mirror(bank, __instance);

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.WorkShipBackToIdle))]
        private static bool WorkShipBackToIdle(StationComponent __instance, int index)
        {
            Bank bank = For(__instance);

            Set(bank.Idle, index, true);
            Set(bank.Work, index, false);
            Mirror(bank, __instance);

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.HasIdleShipIndex))]
        private static bool HasIdleShipIndex(StationComponent __instance, int index, ref bool __result)
        {
            __result = Test(For(__instance).Idle, index);

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.HasWorkShipIndex))]
        private static bool HasWorkShipIndex(StationComponent __instance, int index, ref bool __result)
        {
            __result = Test(For(__instance).Work, index);

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.HasShipIndex))]
        private static bool HasShipIndex(StationComponent __instance, int index, ref bool __result)
        {
            Bank bank = For(__instance);

            __result = Test(bank.Idle, index) || Test(bank.Work, index);

            return false;
        }

        /// <summary>
        /// 原版逐字翻译（@0000–@0033）：从 <c>qIdx</c> 起绕着泊位环扫一圈，
        /// 找到第一个 idle 的就返回它的下标，一圈没有返回 −1。
        /// 唯一的差别是取位走旁挂位图，不再 <c>&amp; 63</c>。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.QueryIdleShip))]
        private static bool QueryIdleShip(StationComponent __instance, int qIdx, ref int __result)
        {
            Bank bank = For(__instance);
            int slots = bank.Slots;

            __result = -1;

            if (slots <= 0) return false;

            for (int i = qIdx; i < qIdx + slots; i++)
            {
                int slot = i % slots;

                if (!Test(bank.Idle, slot)) continue;

                __result = slot;

                return false;
            }

            return false;
        }

        // ── ShipRenderersOnTick：两处内联取位 ─────────────────

        /// <summary>
        /// 热缓存预热。这个方法每 tick 每站调一次，而它体内的对账循环要按泊位数
        /// 走一遍，所以在这里把 <see cref="_hot"/> 指对，后面全是引用比较。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.ShipRenderersOnTick))]
        private static void ShipRenderersOnTick_Prefix(StationComponent __instance) => For(__instance);

        /// <summary>
        /// 把两处**内联**的 <c>idleShipIndices &amp; (1L &lt;&lt; (i &amp; 63))</c> 换成
        /// <see cref="HasIdleBit"/>。这两处不走 <c>HasIdleShipIndex</c> 助手，所以
        /// 光换那八个方法够不着它们——漏掉的话对账器数出来的 idle 位永远只有低 64 个，
        /// 第 65 个泊位之后永远补不满，而且一个字都不报。
        ///
        /// <b>只改操作码和操作数，绝不删指令</b>（删掉会把落在上面的分支标签一起带走）——
        /// 和 <c>CargoIncClampPatches</c> 同一条。九条里留下首尾和中间那个 <c>ldloc</c>，
        /// 其余六条置 <c>nop</c>。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.ShipRenderersOnTick))]
        private static IEnumerable<CodeInstruction> ShipRenderersOnTick_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);

            // 先把目标方法解析出来，解析不到就整块不改写——
            // 发一个 `call null` 会活到 Harmony 的写入器里才炸，栈里指不到这行。
            MethodInfo helper = AccessTools.Method(typeof(StationShipBank), nameof(HasIdleBit));

            FieldInfo idle = AccessTools.Field(typeof(StationComponent), nameof(StationComponent.idleShipIndices));

            if (helper == null || idle == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "运输船泊位：ShipRenderersOnTick 的转译目标解析不到，整块不改写。"
                    + "第 65 个泊位起的 idle 位不会被补齐。");

                return code;
            }

            var hit = 0;

            for (var i = 0; i + 8 < code.Count; i++)
            {
                if (!code[i].LoadsField(idle)) continue;

                // 形状核对：ldfld idle ; ldc.i4.1 ; conv.i8 ; ldloc ; ldc.i4.s 63 ; and ; shl ; and
                //
                // **别拿 `Is(OpCodes.Ldc_I4_S, 63)` 比**：ldc.i4.s 的操作数是 sbyte，
                // 拿 int 去比会静默不匹配、一处都认不出来。本文件为 Cecil 那一侧记过同一条
                //（「不要测 Operand -is [int]」），Harmony 这一侧是同一个坑。
                if (code[i + 4].opcode != OpCodes.Ldc_I4_S) continue;
                if (code[i + 4].operand == null) continue;
                if (Convert.ToInt32(code[i + 4].operand) != 63) continue;
                if (code[i + 5].opcode != OpCodes.And) continue;
                if (code[i + 6].opcode != OpCodes.Shl) continue;
                if (code[i + 7].opcode != OpCodes.And) continue;

                // 留下 code[i+3]（那个 ldloc，就是泊位下标），其余化掉
                code[i].opcode = OpCodes.Nop;
                code[i].operand = null;
                code[i + 1].opcode = OpCodes.Nop;
                code[i + 1].operand = null;
                code[i + 2].opcode = OpCodes.Nop;
                code[i + 2].operand = null;
                code[i + 4].opcode = OpCodes.Nop;
                code[i + 4].operand = null;
                code[i + 5].opcode = OpCodes.Nop;
                code[i + 5].operand = null;
                code[i + 6].opcode = OpCodes.Nop;
                code[i + 6].operand = null;

                // 最后那条 and 变成调用：栈上此时是 (this, 下标)
                code[i + 7].opcode = OpCodes.Call;
                code[i + 7].operand = helper;

                hit++;
            }

            // 实测就是 2 处（@0013 数 idle 位、@0076 从尾巴往回找 idle 位）。
            // 数目不对说明游戏更新挪了形状——**整块不改写**，宁可功能不生效也不要改错一半。
            if (hit != 2)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"运输船泊位：ShipRenderersOnTick 里应当改写 2 处内联取位，实际认出 {hit} 处。"
                    + "整块回退不改写——半套改写比不改写更难查。");

                return new List<CodeInstruction>(instructions);
            }

            ProjectEdenPlugin.Log.LogInfo("运输船泊位：ShipRenderersOnTick 的 2 处内联取位已改写");

            return code;
        }

        // ── 生命周期 ──────────────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.Init))]
        private static void Init_Postfix(StationComponent __instance)
        {
            Banks[__instance] = Derive(__instance);
            BumpGeneration();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.Reset))]
        private static void Reset_Postfix(StationComponent __instance)
        {
            Banks.TryRemove(__instance, out Bank _);
            BumpGeneration();
        }

        /// <summary>读档补齐数组之后，整张位图按新泊位数重开一遍。</summary>
        internal static void Invalidate(StationComponent station)
        {
            if (station == null) return;

            Banks[station] = Derive(station);
            BumpGeneration();
        }

        internal static void CountGrownSlots(int slots) =>
            System.Threading.Interlocked.Add(ref _grownSlots, slots);

        /// <summary>
        /// 开机状态行。读的是 Harmony 自己的补丁表——**状态行回答「接上了没有」，
        /// 事件行回答「它决定了什么」**，一个替不了另一个。
        /// </summary>
        internal static void Report()
        {
            if (System.Threading.Interlocked.Exchange(ref _reported, 1) != 0) return;

            var prefixes = 0;
            var transpiled = false;

            foreach (MethodBase patched in Harmony.GetAllPatchedMethods())
            {
                if (patched.DeclaringType != typeof(StationComponent)) continue;

                HarmonyLib.Patches info = Harmony.GetPatchInfo(patched);

                if (info == null) continue;

                if (info.Prefixes != null)
                    foreach (Patch p in info.Prefixes)
                        if (p.PatchMethod?.DeclaringType == typeof(StationShipBank))
                            prefixes++;

                if (info.Transpilers != null)
                    foreach (Patch p in info.Transpilers)
                        if (p.PatchMethod?.DeclaringType == typeof(StationShipBank))
                            transpiled = true;
            }

            // 八个翻位方法 + ShipRenderersOnTick 的预热 = 9 个前缀
            if (prefixes != 9 || !transpiled)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"运输船泊位：**没有完全接上**（前缀 {prefixes}/9，转译 {transpiled}）。"
                    + "位图仍然是原版的 UInt64，超过 64 个泊位会互相覆盖——"
                    + "把 machines.json 的 maxShipCount 调回 64 再开局。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "运输船泊位：旁挂位图已接管（8 个翻位方法整体替换 + 2 处内联取位改写），"
                + "64 艘的类型上限已解除。位图整个是派生量（work 位取自在飞船的 shipIndex，"
                + "idle 位由原版自己的对账器补齐），所以**不占存档、老档照常开**。");
        }
    }
}
