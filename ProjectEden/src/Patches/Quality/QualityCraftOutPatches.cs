using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// <b>出货侧：把产物缓冲里的品质带出机器。</b>
    ///
    /// 结算那一步（<see cref="QualityCraftFlowPatches"/>）已经把投入的品质算进了
    /// <c>quaProduced</c>，但那是个**只有本 mod 认识的新槽位**——原版没有 <c>incProduced</c>，
    /// 所以没有任何一条原版出货路径会去读它。货一搬出机器，品质就留在缓冲区里。
    ///
    /// <c>produced[]</c> 的四条出路，逐条接上：
    /// <list type="number">
    /// <item><b>分拣器取货</b> —— <c>PlanetFactory.PickFrom</c> 的装配机分支（本文件，前置/后置）；</item>
    /// <item><b>拆除退料</b> —— <c>FactorySystem.TakeBackItems_Assembler</c>（本文件，转译器）；</item>
    /// <item><b>面板点产物图标</b> —— <see cref="QualityTakeOutPatches"/>；</item>
    /// <item><b>巨型建筑自己的带子和物流槽位</b> —— <c>MegaAssemblerPatches.UpdateOutputSlots</c>
    /// 和 <c>MegaStationPatches.UpdateStationStorage</c>，两处都调
    /// <see cref="DrainSlot"/>。</item>
    /// </list>
    ///
    /// <b>四条都要接，不能只接一条。</b> 漏掉任何一条，那条路上的货就是「件数搬走了、
    /// 分数留在原地」——剩下的货顶着整格的点数，单件分数凭空往上跳。
    /// 这和本仓库在 <c>StationStore.inc</c> 上记过的「每次搬运都白送一次增产」
    /// 是同一个形状，只是换了个字段，而且同样<b>一个字都不报</b>。
    ///
    /// <b>「取走了多少」是量出来的，不是照着原版条件重算的。</b> 前置记下 <c>produced</c>，
    /// 后置比差值——和结算那一步、和 <c>RunExtraCycles</c> 数产量是同一个做法。
    ///
    /// <b>参数一个都不能多声明。</b> <c>PickFrom</c> 的 <c>out stack / out inc</c> 被
    /// preloader 从 <c>Byte&amp;</c> 加宽成了 <c>Int16&amp;</c>，而本程序集是按未加宽的签名编译的
    /// ——在补丁签名里写出它们会解析不到，**而且是静默的**。所以只收 <c>__instance</c>
    /// 和安全类型的实体号，其余一概不碰。同理不能用
    /// <c>[HarmonyPatch(..., new[]{ typeof(byte).MakeByRefType() })]</c> 去选重载，
    /// 只能用 <see cref="HarmonyTargetMethods"/> 按**首参类型**挑。
    /// </summary>
    internal static class QualityCraftOut
    {
        /// <summary>前置记下的产物件数，逐线程一份——取货是跨星球并行 tick 的。</summary>
        [System.ThreadStatic] private static int[] _before;

        [System.ThreadStatic] private static int _armedAsm;

        private static int _reported;

        internal static void Report()
        {
            if (!QualityAccess.CraftReady || QualityAccess.SetChannel0 == null)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质·出货：**没接上**——quaProduced 或侧信道写入器缺一个。"
                    + "这种情况下机器里算出来的品质搬不出来，而且不会报错。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "物品品质·出货：已接线（分拣器取货 / 拆除退料 / 面板取出 / 巨型建筑带子与槽位）。"
                + "产物离开机器时按件数把品质一起带走。第一次真的带出来时会再报一行。");
        }

        // ── 共用的一格出货 ────────────────────────────────────

        /// <summary>
        /// 从 <paramref name="index"/> 号产物格里，按<b>这一次搬走的件数</b>取出对应的品质并扣掉。
        ///
        /// <paramref name="had"/> 是**搬走之前**那一格的件数——比例必须拿扣减前的数算，
        /// 而调用点扣件数的时机各不相同，所以由调用点自己把它传进来，这里不去猜。
        ///
        /// 整格搬空就整格带走：否则整数除法的零头会永远留在缓冲区里，
        /// 而那正是本仓库在拆分那一步已经踩过的坑。
        /// </summary>
        internal static int DrainSlot(ref AssemblerComponent asm, int index, int units, int had)
        {
            int share = PeekSlot(ref asm, index, units, had);

            if (share <= 0) return 0;

            QualityAccess.GetQuaProduced(ref asm)[index] -= share;

            ReportOnce(share);

            return share;
        }

        /// <summary>
        /// 只算不扣。
        ///
        /// <b>存在的理由是「搬运可能失败」。</b> 巨型建筑往带子上塞货时，
        /// <c>InsertAtHead</c> 可能因为带子满了而返回 false，这时件数一件都没走。
        /// 先扣后搬的写法会在那一刻<b>把分数凭空销毁</b>，而且带子越堵丢得越快——
        /// 所以那条路上先 Peek 出要带走的分数，塞进去成功了再 <see cref="DrainSlot"/>。
        /// </summary>
        internal static int PeekSlot(ref AssemblerComponent asm, int index, int units, int had)
        {
            if (!QualityAccess.CraftReady || units <= 0 || had <= 0 || index < 0) return 0;

            int[] qp = QualityAccess.GetQuaProduced(ref asm);

            if (qp == null || index >= qp.Length || qp[index] <= 0) return 0;

            return units >= had ? qp[index] : (int)((long)qp[index] * units / had);
        }

        /// <summary>
        /// <b>按实体号</b>找到这台机器（<c>PickFrom(int entityId, …)</c> 那个重载用）。
        /// 实测它开头就是 <c>entityPool[entityId].beltId</c>，确实是实体号。
        /// </summary>
        internal static void ArmByEntity(PlanetFactory factory, int entityId)
        {
            _armedAsm = 0;

            if (!QualityAccess.CraftReady || factory?.entityPool == null) return;
            if (entityId <= 0 || entityId >= factory.entityPool.Length) return;

            ArmByAssembler(factory, factory.entityPool[entityId].assemblerId);
        }

        /// <summary>这台机器的产物缓冲里有没有品质；有就记下快照，等后置比差值。</summary>
        internal static void ArmByAssembler(PlanetFactory factory, int asmId)
        {
            _armedAsm = 0;

            if (!QualityAccess.CraftReady || asmId <= 0) return;

            AssemblerComponent[] pool = factory?.factorySystem?.assemblerPool;

            if (pool == null || asmId >= pool.Length || pool[asmId].id != asmId) return;

            int[] produced = pool[asmId].produced;
            int[] qp = QualityAccess.GetQuaProduced(ref pool[asmId]);

            if (produced == null || qp == null || qp.Length < produced.Length) return;

            var any = false;

            for (var j = 0; j < produced.Length; j++)
                if (qp[j] > 0)
                {
                    any = true;

                    break;
                }

            if (!any) return;

            if (_before == null || _before.Length < produced.Length) _before = new int[produced.Length];

            for (var j = 0; j < produced.Length; j++) _before[j] = produced[j];

            _armedAsm = asmId;
        }

        /// <summary>
        /// 取走了多少就带走多少品质，写进 0 号寄存器。
        ///
        /// <b>写在后置里是对的，不是将就。</b> 侧信道对出参的协议是「被调方在返回前写、
        /// 调用方在返回后读」，而 Harmony 的后置<b>跑在控制权交回调用方之前</b>——
        /// 分拣器那边紧跟着 <c>PickFrom</c> 的 <c>ldsfld Q0</c>（实测 IL 0127）读到的
        /// 就是这里写进去的值。
        ///
        /// <b>取的不是装配机的货就一个字都不写。</b> 从传送带取货时被调方已经把
        /// <c>Cargo.qua</c> 写进了 Q0，这里再写一次就是把真品质盖掉。
        /// 判据是「<c>produced</c> 真的少了」——没少就 <c>taken == 0</c>，直接返回。
        /// </summary>
        internal static void Fire(PlanetFactory factory)
        {
            int asmId = _armedAsm;

            _armedAsm = 0;

            if (asmId <= 0) return;

            AssemblerComponent[] pool = factory?.factorySystem?.assemblerPool;

            if (pool == null || asmId >= pool.Length) return;

            int[] produced = pool[asmId].produced;

            if (produced == null || _before == null) return;

            var taken = 0;

            for (var j = 0; j < produced.Length && j < _before.Length; j++)
            {
                int was = _before[j];

                taken += DrainSlot(ref pool[asmId], j, was - produced[j], was);
            }

            if (taken <= 0) return;

            QualityAccess.SetChannel0(taken);
        }

        /// <summary>
        /// 拆除退料时，这一格产物带走多少分。转译器把它插在
        /// <c>Q0 = 0</c> 那条常量上（<c>TakeBackItems_Assembler</c> IL 0081）。
        ///
        /// <b>整格带走，不按实际收下的件数按比例分。</b> 原版这里压根不扣
        /// <c>produced[]</c>——最后一句 <c>SetRecipe(0, …)</c> 把整个缓冲区丢掉，
        /// 背包满了收不下的那部分货<b>连同件数一起消失</b>。所以「按收下的比例给分」
        /// 会让剩下的分跟着货一起蒸发，而整格给至多是在背包满时把分数集中到收下的那几件上。
        /// 原版自己在投料侧就是这么干的（IL 0115 把整格 <c>quaServed</c> 写进 Q0，
        /// 不管实际收下几件），两侧口径一致。
        /// </summary>
        internal static int TakeBackQua(FactorySystem system, int asmId, int index)
        {
            if (!QualityAccess.CraftReady || system?.assemblerPool == null) return 0;
            if (asmId <= 0 || asmId >= system.assemblerPool.Length) return 0;

            AssemblerComponent[] pool = system.assemblerPool;

            if (pool[asmId].id != asmId) return 0;

            int[] qp = QualityAccess.GetQuaProduced(ref pool[asmId]);

            if (qp == null || index < 0 || index >= qp.Length) return 0;

            int qua = qp[index];

            if (qua <= 0) return 0;

            qp[index] = 0;

            ReportOnce(qua);

            return qua;
        }

        private static void ReportOnce(int taken)
        {
            if (System.Threading.Interlocked.Exchange(ref _reported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogWarning(
                $"物品品质·出货：第一次把品质带出机器——这一次搬走的货带着 {taken} 分。"
                + "**这一行在，就说明【提纯 → 合成 → 出货】整条链通了**："
                + "产物进箱子之后悬停就能看见品质。整局只报一次。");
        }
    }

    /// <summary>
    /// <c>PickFrom(int entityId, …)</c> 这个重载。**按首参类型挑，不按参数表挑**——
    /// 参数表里有被 preloader 加宽过的 <c>Int16&amp;</c>，写出来就解析不到。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityPickFromByEntityPatches
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            foreach (MethodInfo m in typeof(PlanetFactory).GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "PickFrom") continue;

                ParameterInfo[] ps = m.GetParameters();

                if (ps.Length > 0 && ps[0].ParameterType == typeof(int)) yield return m;
            }
        }

        [HarmonyPrefix]
        private static void Pre(PlanetFactory __instance, int entityId) =>
            QualityCraftOut.ArmByEntity(__instance, entityId);

        [HarmonyPostfix]
        private static void Post(PlanetFactory __instance) => QualityCraftOut.Fire(__instance);
    }

    /// <summary>
    /// <c>PickFrom(uint ioTargetTypedId, …)</c> 这个重载。
    ///
    /// <b>它不是转调，是另一份完整实现</b>（实测 1087 条指令，而按实体号那个是 1026 条），
    /// 所以两个都得打。
    ///
    /// <b>低 24 位不是实体号，这一点差点写错。</b> 开头是
    /// <c>id = typedId &amp; 0xFFFFFF; kind = typedId &amp; 0xFF000000;</c>，然后按 <c>kind</c>
    /// 跳到七个分支，而<b>每个分支把那 24 位当成自己那张池子的下标</b>：
    /// <c>0x01000000</c> 是 <c>beltPool</c>、<c>0x02000000</c> 是 <c>assemblerPool</c>、
    /// 再往后是实验室 / 储物箱 / 物流站……<b>都不是 entityPool</b>。
    /// 拿它去查 <c>entityPool[id].assemblerId</c> 会查到一台<b>毫不相干</b>的机器，
    /// 然后把那台机器的品质扣掉发给这一次的货——分数长在错的地方，而且不报错。
    /// 所以这里只在 <c>kind == 0x02000000</c> 时动手，那时 24 位本身就是装配机号。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityPickFromByTypedIdPatches
    {
        private const uint KindMask = 0xFF000000;

        /// <summary>装配机那一支，实测 <c>ldc.i4 33554432 ; beq</c>（IL 0031–0036）。</summary>
        private const uint KindAssembler = 0x02000000;

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            foreach (MethodInfo m in typeof(PlanetFactory).GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "PickFrom") continue;

                ParameterInfo[] ps = m.GetParameters();

                if (ps.Length > 0 && ps[0].ParameterType == typeof(uint)) yield return m;
            }
        }

        [HarmonyPrefix]
        private static void Pre(PlanetFactory __instance, uint ioTargetTypedId) =>
            QualityCraftOut.ArmByAssembler(__instance,
                (ioTargetTypedId & KindMask) == KindAssembler ? (int)(ioTargetTypedId & ~KindMask) : 0);

        [HarmonyPostfix]
        private static void Post(PlanetFactory __instance) => QualityCraftOut.Fire(__instance);
    }

    /// <summary>
    /// <b>拆装配机退料：产物那一半的品质。</b>
    ///
    /// 投料那一半 1c 已经接好了——它是 <c>incServed</c> 的孪生，实测
    /// IL 00D5 读 <c>quaServed[i]</c>、IL 0115–0117 在调用前写进 Q0。
    /// 产物那一半没有可孪生的字段（原版没有 <c>incProduced</c>），
    /// 于是同一个方法里两条路一条带分、一条恒为 0：
    ///
    /// <code>
    /// 0080: ldc.i4.0                 // TryAddItemToPackage 的最后一个 bool
    /// 0081: ldc.i4.0                 // ← 这一条就是「这批货 0 分」
    /// 0082: stsfld Q0
    /// 0087: callvirt TryAddItemToPackage(products[j], produced[j], 0, …)
    /// </code>
    ///
    /// <b>为什么必须是转译器。</b> 要写的值是<b>每一格产物各不相同</b>的，而前置只跑一次、
    /// 看不见循环变量；把品质写进去的时机又必须在 call <b>之前</b>。前置/后置都表达不了
    /// 「循环体内第 j 次迭代」，所以这里是本功能里唯一一个转译器。
    ///
    /// <b>靠形状定位，不靠偏移。</b> 匹配 <c>ldc.i4.0 ; stsfld Q0 ; call TryAddItemToPackage</c>
    /// ——投料那一条的前面是 <c>ldloc V_13</c>（真品质），调用<b>之后</b>的擦除后面跟的是
    /// <c>stloc</c> 而不是 call，两者都落不进这个形状。实测整个方法体里恰好 1 处，
    /// 数不对就一条都不改并大声报错。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityTakeBackAssemblerPatches
    {
        [HarmonyTargetMethod]
        private static MethodBase Target() =>
            AccessTools.Method(typeof(FactorySystem), nameof(FactorySystem.TakeBackItems_Assembler));

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            MethodInfo hook = AccessTools.Method(typeof(QualityCraftOut),
                nameof(QualityCraftOut.TakeBackQua));

            System.Type channel = AccessTools.TypeByName("ProjectEdenQualityChannel");

            FieldInfo q0 = channel != null ? AccessTools.Field(channel, "Q0") : null;

            // preloader 没装时这条路根本不存在，安静退出——不是失败
            if (hook == null || q0 == null)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "物品品质·拆机退料：程序集里没有品质侧信道，跳过（preloader 没装时的正常状态）。");

                return code;
            }

            var hits = 0;

            for (int i = code.Count - 3; i >= 0; i--)
            {
                if (!IsZero(code[i])) continue;
                if (code[i + 1].opcode != OpCodes.Stsfld || !IsField(code[i + 1], q0)) continue;
                if (!IsTryAdd(code[i + 2])) continue;

                CodeInstruction idx = LoopIndex(code, i);

                if (idx == null) continue;

                // **就地改第一条，剩下的插在后面**——原来那条指令上可能挂着分支标签，
                // 换成新对象就把标签丢了，而丢标签是不报错的。
                code[i].opcode = OpCodes.Ldarg_0;
                code[i].operand = null;

                code.InsertRange(i + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_2),
                    idx,
                    new CodeInstruction(OpCodes.Call, hook)
                });

                hits++;
            }

            if (hits != 1)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"物品品质·拆机退料：应当改 1 处，实际 {hits} 处——**一处都没改，本次不动**。"
                    + "拆掉装配机时产物那一半的品质会丢，投料那一半不受影响。");

                return instructions;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "物品品质·拆机退料：已改 1 处——拆掉装配机时，产物缓冲区里的品质跟着货进背包。");

            return code;
        }

        private static bool IsZero(CodeInstruction ins)
        {
            if (ins.opcode == OpCodes.Ldc_I4_0) return true;

            if (ins.opcode != OpCodes.Ldc_I4 && ins.opcode != OpCodes.Ldc_I4_S) return false;

            return ins.operand is int i && i == 0 || ins.operand is sbyte s && s == 0;
        }

        /// <summary>
        /// 按<b>声明类型 + 名字</b>比字段，不用 <c>ReferenceEquals</c>。
        /// 反射每次取到的 <c>FieldInfo</c> 未必是同一个对象，引用比较会静默地一处都匹配不上，
        /// 而那时报出来的是「应当 1 处、实际 0 处」——错在比较方式，看起来却像是形状变了。
        /// </summary>
        private static bool IsField(CodeInstruction ins, FieldInfo want) =>
            ins.operand is FieldInfo f && f.DeclaringType == want.DeclaringType && f.Name == want.Name;

        private static bool IsTryAdd(CodeInstruction ins) =>
            (ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)
            && ins.operand is MethodInfo m && m.Name == "TryAddItemToPackage";

        /// <summary>
        /// 循环变量是<b>找出来的</b>：往回走到 <c>ldfld AssemblerComponent::produced</c>，
        /// 紧跟它的那一条就是下标。和本仓库 <c>MinerProductStat_Transpiler</c> 定位
        /// 矿机局部的做法一样——按数据流找，不按偏移数。
        /// </summary>
        private static CodeInstruction LoopIndex(List<CodeInstruction> code, int at)
        {
            FieldInfo produced = AccessTools.Field(typeof(AssemblerComponent), "produced");

            if (produced == null) return null;

            for (int i = at; i >= 1; i--)
            {
                if (code[i].opcode != OpCodes.Ldfld || !IsField(code[i], produced)) continue;

                CodeInstruction next = code[i + 1];

                if (!next.IsLdloc()) return null;

                return new CodeInstruction(next.opcode, next.operand);
            }

            return null;
        }
    }
}
