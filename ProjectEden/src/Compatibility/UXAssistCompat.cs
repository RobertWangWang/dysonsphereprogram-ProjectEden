using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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

            Fix(harmony, "UXAssist.Patches.FactoryPatch+ProtectVeinsFromExhaustion",
                "MinerComponent_InternalUpdate_Prefix", "矿脉保护");

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
                ProjectEdenPlugin.Log.LogWarning($"UXAssist 兼容：给 {what} 打补丁失败：{e.Message}");
            }
        }

        /// <summary>
        /// 把对<b>原版字节签名</b>的调用换成本类的垫片。
        ///
        /// 判据是「方法名对上 + 参数里有 Byte」——加宽之后游戏里真正存在的那个签名
        /// 一个 Byte 都没有，所以这个判断不会误伤到已经好的调用。
        /// </summary>
        private static IEnumerable<CodeInstruction> Reroute(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo shimInsert = AccessTools.Method(typeof(UXAssistCompat), nameof(ShimTryInsertItem));
            MethodInfo shimInto = AccessTools.Method(typeof(UXAssistCompat), nameof(ShimInsertInto));

            var hits = 0;

            foreach (CodeInstruction ins in instructions)
            {
                var mr = ins.operand as MethodInfo;

                if (mr != null && HasByte(mr))
                {
                    if (mr.Name == "TryInsertItem" && mr.DeclaringType == typeof(CargoPath))
                    {
                        // 就地改写而不是换对象：原指令上可能挂着跳转标签
                        ins.opcode = OpCodes.Call;
                        ins.operand = shimInsert;

                        hits++;
                    }
                    else if (mr.Name == "InsertInto" && mr.DeclaringType == typeof(PlanetFactory))
                    {
                        ins.opcode = OpCodes.Call;
                        ins.operand = shimInto;

                        hits++;
                    }
                }

                yield return ins;
            }

            _patched += hits;
        }

        private static bool HasByte(MethodInfo m) =>
            m.GetParameters().Any(p =>
                p.ParameterType == typeof(byte) ||
                (p.ParameterType.IsByRef && p.ParameterType.GetElementType() == typeof(byte)));

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
