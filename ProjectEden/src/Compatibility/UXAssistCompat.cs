using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Compatibility
{
    /// <summary>
    /// 修好被本 mod 的 preloader 打断的两个 UXAssist 功能。
    ///
    /// <b>破坏是我们造成的，所以修它是我们的事。</b>
    /// <c>CargoIncWidener</c> 把传送带那条链上 <b>33 个签名</b>的
    /// <c>byte inc</c> / <c>byte stack</c> 加宽成了 <c>Int16</c>。UXAssist 是按<b>原版</b>
    /// 签名编译的，它 IL 里那条 MemberRef 于是解析不上，头一次执行到就抛
    /// <c>MissingMethodException</c>。
    ///
    /// <b>实测只有两处</b>（全模块比对加宽前后的签名，再扫 profile 里每个插件的调用点）：
    /// <list type="bullet">
    /// <item><c>BeltSignalsForBuyOut</c> → <c>CargoPath.TryInsertItem(int,int,byte,byte)</c>
    ///       ——「传送带信号购买」</item>
    /// <item><c>ProtectVeinsFromExhaustion</c> → <c>PlanetFactory.InsertInto(int,int,int,byte,byte,out byte)</c>
    ///       ——「矿脉保护」</item>
    /// </list>
    /// 其余七个插件（CommonAPI / DSPModSave / LDBTool / InstantDelivery / ErrorAnalyzer /
    /// CloseError / Newtonsoft.Json）一处都没有。
    ///
    /// <b>这两个功能在 1.7.0 里就已经是坏的</b>，只是没人报过——大概那两条路很少走到。
    ///
    /// <b>为什么不在游戏程序集里合成旧签名的重载。</b> 那样确实能让任何按原版编译的
    /// 程序集继续解析，但它会让<b>按名字打补丁</b>的 Harmony patch 多认出一个目标：
    /// 本 mod 自己的 <c>MegaAssemblerPatches</c> 就用 <c>TargetMethods()</c> 按名字
    /// 拿 <c>InsertInto</c> 的<b>每一个</b>重载，转发方法会被一起转译，于是同一份逻辑
    /// 跑两遍。别的 mod 怎么打补丁我们更控制不了。
    ///
    /// <b>做法是转译 UXAssist 自己那两个方法</b>，把对旧签名的调用换成本类的垫片，
    /// 垫片再经<b>运行时绑定的委托</b>去调实际存在的那个签名——和
    /// <see cref="Patches.CargoWidening"/> 处理本 mod 自己调用时是同一套办法。
    /// Harmony 的转译器跑在 JIT 之前，所以那条坏掉的 MemberRef <b>一次都不会被解析</b>。
    ///
    /// 没装 UXAssist、或者 preloader 没生效（签名还是 byte）时，这里整个不动。
    /// </summary>
    internal static class UXAssistCompat
    {
        internal const string Guid = "org.soardev.uxassist";

        internal static bool Installed => CompatibilityRegistry.IsLoaded(Guid);

        // ── 运行时绑定：只有加宽之后才存在的那两个签名 ──

        private delegate bool TryInsertWide(CargoPath path, int index, int itemId, short stack, short inc);

        private delegate int InsertIntoWide(PlanetFactory factory, int entityId, int slot, int itemId,
            short count, short inc, out short remainInc);

        private static TryInsertWide _tryInsert;
        private static InsertIntoWide _insertInto;

        private static int _patched;

        internal static void ApplyPatches(Harmony harmony)
        {
            if (harmony == null) return;

            if (!Installed)
            {
                ProjectEdenPlugin.Log.LogInfo("UXAssist 没装，跳过它的兼容补丁");

                return;
            }

            if (!Patches.CargoWidening.IsActive)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "UXAssist 兼容：Cargo.inc 没有被加宽（preloader 没装或放弃了改写），" +
                    "那两处调用本来就好的，不用动");

                return;
            }

            if (!Bind()) return;

            // 类型全名是从 UXAssist.dll 里读出来的，不是猜的——第一版写成了 PlanetPatch，
            // 实际是 FactoryPatch。猜错的后果是补丁静默不生效（只会多一条 WARNING）。
            Fix(harmony, "UXAssist.Patches.FactoryPatch+BeltSignalsForBuyOut",
                "GameLogic_OnFactoryFrameBegin_Postfix", "传送带信号购买");

            // **矿脉保护刻意不修，理由不是修不动，是修好了更糟。**
            //
            // 读它的源码才看清：ProtectVeinsFromExhaustion 的前置**返回 false**，
            // 整个重实现了 MinerComponent.InternalUpdate（矿脉 / 原油 / 抽水三条分支）。
            // 而本 mod 的 AdvancedMinerPatches 是**转译原版方法体**的——矿石→锭替换、
            // 缓存上限、钻头消耗、小型采矿机的节流分母，全在那个被跳过的方法体里。
            //
            // 所以把签名修好只会得到一个**静默的功能互斥**：矿脉保护能用了，
            // 但铜矿不再自动变铜块、缓存回到 50、钻头不再消耗，而且一条报错都没有。
            //
            // 而这两个功能本来就重叠：advancedminer.json 的 forceMiningCostRate 为 0，
            // 本 mod 对大型采矿机 / 抽水站 / 采油站早就是「矿脉完全不消耗」；
            // 它唯一多给的是小型采矿机，而那一块已经由 protectSmallMinerVeins 补上了。
            //
            // 于是这里改成**检测并说清楚**，检测点在采矿 tick 上（玩家可能中途才打开开关，
            // 启动时检查会漏）。见 CheckMinerConflictOnce。

            // 状态行无论成败都打：**「补了几处」是这条兼容唯一能被看见的证据**，
            // 而它修的那两个功能平时很少走到，坏了也不会有人立刻发现。
            if (_patched > 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"UXAssist 兼容：已重接 {_patched} 处被 Cargo.inc 加宽打断的调用" +
                    "（传送带信号购买 / 矿脉保护）");
            else
                ProjectEdenPlugin.Log.LogWarning(
                    "UXAssist 兼容：一处都没接上——它的内部类名或方法名可能变了。" +
                    "那两个功能在本 mod 下会抛 MissingMethodException，" +
                    "对照 UXAssist 的版本重新确认类名");
        }

        private static int _minerConflictChecked;

        /// <summary>
        /// 检测 UXAssist 的「矿脉保护」有没有真的挂在 <c>MinerComponent.InternalUpdate</c> 上，
        /// 挂了就把互斥说清楚。
        ///
        /// <b>检测点必须在采矿 tick 上，不能在启动时。</b> 那是个可以在游戏里随时勾的开关，
        /// UXAssist 是在勾选的那一刻才打补丁的——启动时查一定查不到。
        ///
        /// <b>查的是 Harmony 的实际补丁表，不是配置项。</b> 别人的配置字段名会变，
        /// 而「这个方法上到底挂了谁」是结果本身。
        ///
        /// 采矿 tick 跑在 <c>_miner_parallel</c> 上，一次性标志必须用 Interlocked 抢，
        /// 否则三十多个线程各打一行。
        /// </summary>
        internal static void CheckMinerConflictOnce()
        {
            if (!Installed) return;

            if (Interlocked.Exchange(ref _minerConflictChecked, 1) != 0) return;

            try
            {
                MethodInfo target = AccessTools.Method(typeof(MinerComponent), nameof(MinerComponent.InternalUpdate));

                if (target == null) return;

                // 写全名：HarmonyLib.Patches 和本仓库自己的 ProjectEden.Patches 命名空间同名
                HarmonyLib.Patches info = Harmony.GetPatchInfo(target);

                if (info?.Prefixes == null) return;

                bool theirs = info.Prefixes.Any(p =>
                    p.PatchMethod?.DeclaringType?.Name == "ProtectVeinsFromExhaustion");

                if (!theirs) return;

                ProjectEdenPlugin.Log.LogWarning(
                    "检测到 UXAssist 的「矿脉保护」已挂在采矿机上，而它和本 mod 的采矿机改造**互斥**：" +
                    "它的前置返回 false、整个跳过原版方法体，而本 mod 的矿石→锭替换、机内缓存上限、" +
                    "钻头消耗、小型采矿机节流全都在那个方法体里——这些会静默失效。");

                ProjectEdenPlugin.Log.LogWarning(
                    "建议关掉 UXAssist 的矿脉保护：本 mod 已经提供同样的效果——" +
                    "大型采矿机 / 抽水站 / 采油站由 advancedminer.json 的 forceMiningCostRate=0 覆盖，" +
                    "小型采矿机由 protectSmallMinerVeins 覆盖，两者都是矿脉完全不消耗。");
            }
            catch (Exception e)
            {
                ProjectEdenPlugin.Log.LogWarning($"检查 UXAssist 采矿机冲突时出错（不影响游戏）：{e.Message}");
            }
        }

        /// <summary>把加宽后的两个签名绑成委托。绑不上就整个不动，而不是打半截补丁。</summary>
        private static bool Bind()
        {
            try
            {
                MethodInfo insert = Pick(typeof(CargoPath), "TryInsertItem", 4, typeof(short));
                MethodInfo into = Pick(typeof(PlanetFactory), "InsertInto", 6, typeof(short));

                if (insert == null || into == null)
                {
                    ProjectEdenPlugin.Log.LogWarning(
                        "UXAssist 兼容：找不到加宽后的 TryInsertItem / InsertInto，跳过");

                    return false;
                }

                _tryInsert = AccessTools.MethodDelegate<TryInsertWide>(insert);
                _insertInto = AccessTools.MethodDelegate<InsertIntoWide>(into);

                return true;
            }
            catch (Exception e)
            {
                ProjectEdenPlugin.Log.LogWarning($"UXAssist 兼容：绑定加宽后的传送带 API 失败，跳过：{e.Message}");

                return false;
            }
        }

        /// <summary>
        /// 按<b>形状</b>挑重载，不按名字。<c>PlanetFactory.InsertInto</c> 有两个重载
        /// （<c>int entityId</c> 和 <c>uint ioTargetTypedId</c>），而且参数类型在加宽前后不同，
        /// 所以既不能按名字拿、也不能写死类型列表——只能按「参数个数 + 第一个参数是 Int32 +
        /// 含有那个被加宽的类型」来认。
        /// </summary>
        private static MethodInfo Pick(Type owner, string name, int argc, Type widened) =>
            AccessTools.GetDeclaredMethods(owner)
                .FirstOrDefault(m =>
                    m.Name == name &&
                    m.GetParameters().Length == argc &&
                    m.GetParameters()[0].ParameterType == typeof(int) &&
                    m.GetParameters().Any(p =>
                        p.ParameterType == widened ||
                        (p.ParameterType.IsByRef && p.ParameterType.GetElementType() == widened)));

        private static void Fix(Harmony harmony, string typeName, string methodName, string what)
        {
            Type t = AccessTools.TypeByName(typeName);

            if (t == null)
            {
                ProjectEdenPlugin.Log.LogWarning($"UXAssist 兼容：找不到类型 {typeName}（{what}），跳过");

                return;
            }

            MethodInfo target = AccessTools.Method(t, methodName);

            if (target == null)
            {
                ProjectEdenPlugin.Log.LogWarning($"UXAssist 兼容：找不到 {typeName}::{methodName}（{what}），跳过");

                return;
            }

            try
            {
                harmony.Patch(target,
                    transpiler: new HarmonyMethod(AccessTools.Method(typeof(UXAssistCompat), nameof(Reroute))));
            }
            catch (Exception e)
            {
                // **打全异常链，不要只打 Message。** HarmonyX 把真正的原因包在
                // 「IL Compile Error (unknown location)」里面，只看 Message 等于什么都没说，
                // 而这一条已经害得诊断多走了一轮。
                ProjectEdenPlugin.Log.LogWarning($"UXAssist 兼容：给 {what} 打补丁失败：{e.Message}");

                for (Exception inner = e.InnerException; inner != null; inner = inner.InnerException)
                    ProjectEdenPlugin.Log.LogWarning($"    ← {inner.GetType().Name}: {inner.Message}");
            }
        }

        /// <summary>
        /// 把对<b>原版字节签名</b>的调用换成本类的垫片。
        ///
        /// <b>判据是「操作数为 null」，这一点和直觉相反，是实测逼出来的。</b>
        /// 第一版按「方法名 + 参数里有 Byte」匹配，一处都没命中，而且让 Harmony 当场炸：
        /// <code>
        ///   Failed to patch ...: ArgumentNullException: Invalid argument for callvirt NULL
        /// </code>
        /// 原因是 Harmony 读 IL 时要把每条 MemberRef 解析成 <c>MethodInfo</c>，
        /// 而这条 MemberRef 指向的签名<b>已经被 preloader 改掉、不存在了</b>——
        /// 解析不出来，<c>operand</c> 就是 <c>null</c>。于是
        /// <c>ins.operand as MethodInfo</c> 永远是 null、永远不匹配，
        /// 那条 <c>callvirt null</c> 原样留下，Harmony 写回时抛异常。
        ///
        /// CLAUDE.md 里记的是这个错误的<b>另一半</b>——「<b>我们自己</b>把 null 当操作数发射出去」。
        /// 这次是反过来：<b>Harmony 读进来就是 null</b>。同一个异常，来源相反。
        ///
        /// 所以改成：找那条<b>操作数为 null 的调用</b>。身份信息已经在解析时丢了，
        /// 但离线实测早就确定了每个方法各自调的是哪一个（一个方法一处），
        /// 所以按 <paramref name="original"/> 的所属类型分派，并且<b>断言正好一处</b>——
        /// 多了少了都说明 UXAssist 变了，那时宁可不改。
        /// </summary>
        private static IEnumerable<CodeInstruction> Reroute(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);

            string owner = original?.DeclaringType?.Name ?? "?";

            MethodInfo shim =
                owner == "BeltSignalsForBuyOut"
                    ? AccessTools.Method(typeof(UXAssistCompat), nameof(ShimTryInsertItem))
                    : owner == "ProtectVeinsFromExhaustion"
                        ? AccessTools.Method(typeof(UXAssistCompat), nameof(ShimInsertInto))
                        : null;

            if (shim == null)
            {
                ProjectEdenPlugin.Log.LogWarning($"UXAssist 兼容：不认识的宿主类型 {owner}，不改");

                return code;
            }

            // **先把方法体里所有解析不出来的指令数一遍，不只是 call。**
            // 上一版只数 call，替换成功了、Harmony 仍然编译失败——说明还有别的空操作数，
            // 而我当时是靠猜去找它。数出来比猜快。
            var nulls = new List<string>();

            foreach (CodeInstruction ins in code)
                if (ins.operand == null && ins.opcode.OperandType != System.Reflection.Emit.OperandType.InlineNone)
                    nulls.Add(ins.opcode.Name);

            if (nulls.Count > 1)
                ProjectEdenPlugin.Log.LogWarning(
                    $"UXAssist 兼容：{owner} 里有 {nulls.Count} 条指令的操作数解析不出来" +
                    $"（{string.Join("、", nulls.Distinct().ToArray())}）——" +
                    "不止那一条调用，逐条确认之前不改");

            var spots = new List<int>();

            for (var i = 0; i < code.Count; i++)
                if ((code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt) &&
                    code[i].operand == null)
                    spots.Add(i);

            if (spots.Count != 1)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"UXAssist 兼容：{owner} 里解析不出来的调用有 {spots.Count} 处，期望正好 1 处——" +
                    "UXAssist 的实现可能变了，这次不改（它仍会抛 MissingMethodException）");

                return code;
            }

            // 就地改写而不是换对象：原指令上可能挂着跳转标签
            code[spots[0]].opcode = OpCodes.Call;
            code[spots[0]].operand = shim;

            _patched++;

            return code;
        }

        // ── 垫片：字节进、字节出，中间走加宽后的真实 API ──
        //
        // 夹在 255 而不是截断：截断会回绕成垃圾值，夹取是确定的降级。
        // 这两条路上的 stack / inc 本来也远到不了 255（UXAssist 传的是个位数）。

        internal static bool ShimTryInsertItem(CargoPath path, int index, int itemId, byte stack, byte inc) =>
            _tryInsert != null && _tryInsert(path, index, itemId, stack, inc);

        internal static int ShimInsertInto(PlanetFactory factory, int entityId, int slot, int itemId,
            byte count, byte inc, out byte remainInc)
        {
            if (_insertInto == null) { remainInc = inc; return 0; }

            int used = _insertInto(factory, entityId, slot, itemId, count, inc, out short remain);

            remainInc = (byte)(remain > 255 ? 255 : remain < 0 ? 0 : remain);

            return used;
        }
    }
}
