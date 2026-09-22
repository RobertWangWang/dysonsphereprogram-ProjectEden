using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把「参考速率」和「理论产能」两个面板上的巨型建筑 / 研究站数值夹回<b>真实上限</b>。
    ///
    /// <para><b>病因：那两个面板算的是一条只看 speed 的式子，而它不知道引擎每 tick 只结算一个周期。</b></para>
    /// 原版两处都是同一行（<c>UIReferenceSpeedTip.AddEntryDataWithFactory</c> @0191 等五处）：
    /// <code>
    ///     每分钟合成数 = 3600 × AssemblerComponent.speed / recipeExecuteData.timeSpend
    /// </code>
    /// 本 mod 把 <c>speed</c> 抬到 1e8（10000 倍），于是一条 1 秒配方算出
    /// <c>3600 × 1e8 / 600000 = 600,000/min</c>——而真实产能是
    /// <c>每 tick 的周期数 × 3600</c>，在玩家报的那张图里是 216,000/min。
    /// **两个数差 2.78 倍，玩家看到的是「生产 600k、消耗 216k，对不上账」**，
    /// 而账其实是平的：差额进了仓库，面板上的仓储数量就在涨。
    ///
    /// <para><b>那条式子本来就等价于「3600 × 每 tick 能跑几个周期」</b></para>
    /// <c>speed / timeSpend</c> 正是「一次 <c>time</c> 累加够跑几个周期」，所以夹成
    /// <c>min(原值, 上限周期数 × 3600)</c> 单位天然一致，不用另造一套换算，
    /// 而且对原版机器<b>天然不生效</b>——1 倍机器连 1 个周期/tick 都填不满。
    ///
    /// <para><b>上限是两道闸取小，第二道是原版自己的，之前没人算进去</b></para>
    /// <list type="number">
    /// <item><c>megabuildings.json</c> 的 <c>cyclesPerTick</c>（逐台可覆盖，见 <see cref="MegaThrottle"/>）。</item>
    /// <item><b>原版 <c>AssemblerComponent.InternalUpdate</c> 的产出闸</b>，按配方类型分三档
    /// （IL 0138–0184 单产物、01D1–02F5 多产物，两条路同一张表）：
    /// <code>
    ///     冶炼 Smelt(1)   : produced[j] + productCounts[j] &gt; 100      → 拒绝   ⇒ 100 / 件数
    ///     组装 Assemble(4): produced[j] &gt; productCounts[j] × 9        → 拒绝   ⇒ 10
    ///     其余（含本 mod 的 9~17）: produced[j] &gt; productCounts[j] × 19 → 拒绝   ⇒ 20
    /// </code>
    /// </item>
    /// </list>
    /// **所以 <c>cyclesPerTick = 60</c> 只有冶炼类配方（且每次出 1 件）够得着**：
    /// 组装类实际上限是 10 个周期/tick，其余是 20。这条和 <c>megabuildings.json</c> 里
    /// 实测的「21,423 周期/tick ÷ 1079 台 ≈ 19.85」互相印证——那个均值贴着 20，
    /// 不是贴着 60。
    ///
    /// <para><b>为什么夹在「乘产出件数」那一步之前，而不是除法那一步</b></para>
    /// 除法之后原版还会折进增产剂：<c>加速模式</c>下 <c>每分钟合成数 ×= accMulti</c>
    /// （@01FD 等处），<c>增产模式</c>下不乘。而**加速对巨型建筑一点用都没有**——
    /// <c>speedOverride</c> 再大，一次 <c>InternalUpdate</c> 也只结算一个周期。
    /// 夹在除法处会被随后的 <c>accMulti</c> 顶回去（最多 3.5 倍），
    /// 夹在「乘件数」之前则两种模式都对，而且 <c>min</c> 幂等、多夹几次没有副作用。
    ///
    /// <para><b>站点是数出来的，不是找到一处就收手</b></para>
    /// 全程序集扫 <c>AssemblerComponent.speed</c> / <c>LabComponent.speed</c> 的读取点，
    /// 落在这两个显示方法里的共 <b>5 处</b>（参考速率 3：装配消耗 / 装配产出 / 研究站；
    /// 理论产能 2：装配 / 研究站），而这 5 处一共有 <b>9 个</b>「乘产出件数」的落点——
    /// 研究站那一处在两个面板里都被供需两侧各用一次。**只改第一处会让同一个面板的
    /// 产出侧和消耗侧报两个不同的理论值**，那比不改更难看懂。计数不对就整条不改写。
    ///
    /// <para><b>研究站的上限恒为 1 周期/tick</b></para>
    /// <c>GameLogic</c> 里只有 <c>_lab_produce_parallel</c>，每 tick 只调一次
    /// <c>InternalUpdateAssemble</c>，没有补跑周期这回事（<c>lab.json</c> 的
    /// <c>assembleSpeed</c> 注释里也写着「产能本来就是每秒 60 个」）。所以夹在 3600/min。
    ///
    /// <para><b>夹的是「满电满光」的额定值</b></para>
    /// <c>powerScalesCycles</c> 和生物温室的日照缩放都在运行时再缩周期数。
    /// 参考速率是<b>产能</b>而不是当前产量，所以这里按额定值算，和原版的语义一致。
    /// </summary>
    [HarmonyPatch]
    internal static class ReferenceRatePatches
    {
        /// <summary>每个方法应当改写几处。数目不对就一处都不改——理由见类注释。</summary>
        private static readonly Dictionary<string, int> Expected = new Dictionary<string, int>
        {
            { "AddEntryDataWithFactory", 4 }, // 参考速率：装配消耗 1 + 装配产出 1 + 研究站 2
            { "CalculateFactory", 4 },        // 理论产能：装配 2 + 研究站 2
        };

        /// <summary>每个方法应当认出几个 speed 站点。</summary>
        private static readonly Dictionary<string, int> ExpectedSites = new Dictionary<string, int>
        {
            { "AddEntryDataWithFactory", 3 },
            { "CalculateFactory", 2 },
        };

        private static readonly Dictionary<string, int> Applied = new Dictionary<string, int>();

        private static int _clampReported;

        internal static IEnumerable<MethodBase> TargetMethods()
        {
            var list = new List<MethodBase>();

            MethodBase tip = AccessTools.Method(typeof(UIReferenceSpeedTip), "AddEntryDataWithFactory");
            MethodBase calc = AccessTools.Method(typeof(ProductionExtraInfoCalculator), "CalculateFactory");

            if (tip != null) list.Add(tip);
            else ProjectEdenPlugin.Log.LogError("参考速率：找不到 UIReferenceSpeedTip.AddEntryDataWithFactory。");

            if (calc != null) list.Add(calc);
            else ProjectEdenPlugin.Log.LogError("参考速率：找不到 ProductionExtraInfoCalculator.CalculateFactory。");

            return list;
        }

        /// <summary>一个 speed 站点：它的组件局部，以及这一段里所有「乘产出件数」的落点。</summary>
        private struct Site
        {
            public CodeInstruction CompLoad;
            public bool IsLab;
            public List<int> Uses;
        }

        internal static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);
            string name = original?.Name ?? "?";

            FieldInfo asmSpeed = AccessTools.Field(typeof(AssemblerComponent), "speed");
            FieldInfo labSpeed = AccessTools.Field(typeof(LabComponent), "speed");
            MethodInfo clampAsm = AccessTools.Method(typeof(ReferenceRatePatches), nameof(ClampAssembler));
            MethodInfo clampLab = AccessTools.Method(typeof(ReferenceRatePatches), nameof(ClampLab));

            // 解析不到就一个字都不改。**绝不能把 null 当操作数发出去**——那会活到
            // Harmony 写回 IL 时才抛 ArgumentNullException，栈里指不到这里一个字。
            if (asmSpeed == null || labSpeed == null || clampAsm == null || clampLab == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"参考速率：{name} 的反射解析失败（speed 字段或夹取方法），本方法不改写。");

                return code;
            }

            // speed 站点的下标，先扫一遍：每个站点管到下一个站点为止
            var speedAt = new List<int>();

            for (var i = 1; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldfld) continue;

                var f = code[i].operand as FieldInfo;

                if (f == asmSpeed || f == labSpeed) speedAt.Add(i);
            }

            var sites = new List<Site>();

            for (var s = 0; s < speedAt.Count; s++)
            {
                int i = speedAt[s];
                var f = code[i].operand as FieldInfo;
                bool isLab = f == labSpeed;

                // 形状：ldloc 组件 ; ldfld speed ; conv.r4 ; mul ...... div ; stloc X
                if (!IsLdloc(code[i - 1])) continue;
                if (i + 2 >= code.Count) continue;
                if (code[i + 1].opcode != OpCodes.Conv_R4 || code[i + 2].opcode != OpCodes.Mul) continue;

                var div = -1;

                for (int j = i + 3; j < code.Count && j <= i + 16; j++)
                {
                    if (code[j].opcode == OpCodes.Div) { div = j; break; }
                }

                if (div < 0 || div + 1 >= code.Count || !IsStloc(code[div + 1])) continue;

                int local = LocalIndex(code[div + 1]);

                if (local < 0) continue;

                // 理论产能那两处会先原样搬进另一个局部，再在它上面折增产剂
                if (div + 3 < code.Count && IsLdloc(code[div + 2]) && LocalIndex(code[div + 2]) == local
                                         && IsStloc(code[div + 3]) && LocalIndex(code[div + 3]) >= 0)
                    local = LocalIndex(code[div + 3]);

                // 管辖区间：到下一个 speed 站点为止（最后一个管到方法末尾）
                int stop = s + 1 < speedAt.Count ? speedAt[s + 1] : code.Count;

                var uses = new List<int>();

                for (int k = div + 2; k + 5 < stop; k++)
                {
                    if (!IsLdloc(code[k]) || LocalIndex(code[k]) != local) continue;
                    if (!IsLdloc(code[k + 1]) || !IsLdloc(code[k + 2])) continue;
                    if (code[k + 3].opcode != OpCodes.Ldelem_I4) continue;
                    if (code[k + 4].opcode != OpCodes.Conv_R4) continue;
                    if (code[k + 5].opcode != OpCodes.Mul) continue;

                    uses.Add(k);
                }

                if (uses.Count == 0) continue;

                sites.Add(new Site { CompLoad = code[i - 1], IsLab = isLab, Uses = uses });
            }

            var found = 0;

            foreach (Site site in sites) found += site.Uses.Count;

            int wantSites = ExpectedSites.TryGetValue(name, out int ws) ? ws : -1;
            int wantUses = Expected.TryGetValue(name, out int wu) ? wu : -1;

            if (sites.Count != wantSites || found != wantUses)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"参考速率：{name} 认出 {sites.Count} 个 speed 站点、{found} 个落点，"
                    + $"期望 {wantSites} / {wantUses}——**本方法一处都不改**。"
                    + "半套改写会让同一个面板的产出侧和消耗侧报两个不同的理论值，比不改更难懂。");

                Applied[name] = 0;

                return code;
            }

            int factoryArg = FactoryArgIndex(original);

            if (factoryArg < 0)
                ProjectEdenPlugin.Log.LogWarning(
                    $"参考速率：{name} 的参数表里找不到 PlanetFactory，逐台的 cyclesPerTick 覆盖会退回全局值。");

            // 倒着插，否则先插入的会把后面的下标全顶走
            var points = new List<KeyValuePair<int, Site>>();

            foreach (Site site in sites)
                foreach (int use in site.Uses)
                    points.Add(new KeyValuePair<int, Site>(use, site));

            points.Sort((a, b) => b.Key.CompareTo(a.Key));

            foreach (KeyValuePair<int, Site> p in points)
            {
                var ins = new List<CodeInstruction>(3)
                {
                    // 组件局部在这两个方法里全是 `XxxComponent&`（实测），所以克隆这一条
                    // 就是现成的 ref 实参，不用 ldloca
                    new CodeInstruction(p.Value.CompLoad.opcode, p.Value.CompLoad.operand),
                };

                if (p.Value.IsLab)
                {
                    ins.Add(new CodeInstruction(OpCodes.Call, clampLab));
                }
                else
                {
                    ins.Add(LoadArg(factoryArg));
                    ins.Add(new CodeInstruction(OpCodes.Call, clampAsm));
                }

                code.InsertRange(p.Key + 1, ins);
            }

            Applied[name] = found;

            ProjectEdenPlugin.Log.LogInfo(
                $"参考速率：{name} 已夹取 {found} 处（{sites.Count} 个 speed 站点）。");

            return code;
        }

        /// <summary>方法参数表里 PlanetFactory 的 ldarg 下标；找不到返回 −1。</summary>
        private static int FactoryArgIndex(MethodBase original)
        {
            if (original == null) return -1;

            ParameterInfo[] ps = original.GetParameters();
            int shift = original.IsStatic ? 0 : 1;

            for (var i = 0; i < ps.Length; i++)
                if (ps[i].ParameterType == typeof(PlanetFactory))
                    return i + shift;

            return -1;
        }

        private static CodeInstruction LoadArg(int index)
        {
            // 下标是从参数表数出来的，不写死；没有 PlanetFactory 就推 null，
            // 夹取那边会退回全局 cyclesPerTick
            switch (index)
            {
                case -1: return new CodeInstruction(OpCodes.Ldnull);
                case 0: return new CodeInstruction(OpCodes.Ldarg_0);
                case 1: return new CodeInstruction(OpCodes.Ldarg_1);
                case 2: return new CodeInstruction(OpCodes.Ldarg_2);
                case 3: return new CodeInstruction(OpCodes.Ldarg_3);
                default: return new CodeInstruction(OpCodes.Ldarg_S, (byte)index);
            }
        }

        private static bool IsLdloc(CodeInstruction c)
        {
            OpCode o = c.opcode;

            return o == OpCodes.Ldloc || o == OpCodes.Ldloc_S
                                      || o == OpCodes.Ldloc_0 || o == OpCodes.Ldloc_1
                                      || o == OpCodes.Ldloc_2 || o == OpCodes.Ldloc_3;
        }

        private static bool IsStloc(CodeInstruction c)
        {
            OpCode o = c.opcode;

            return o == OpCodes.Stloc || o == OpCodes.Stloc_S
                                      || o == OpCodes.Stloc_0 || o == OpCodes.Stloc_1
                                      || o == OpCodes.Stloc_2 || o == OpCodes.Stloc_3;
        }

        /// <summary>局部编号。**自己写而不是用 Harmony 的扩展**：那个扩展在不同版本里进出过，
        /// 而这里只需要六种操作码加一个操作数，没有必要为它押一个版本。</summary>
        private static int LocalIndex(CodeInstruction c)
        {
            OpCode o = c.opcode;

            if (o == OpCodes.Ldloc_0 || o == OpCodes.Stloc_0) return 0;
            if (o == OpCodes.Ldloc_1 || o == OpCodes.Stloc_1) return 1;
            if (o == OpCodes.Ldloc_2 || o == OpCodes.Stloc_2) return 2;
            if (o == OpCodes.Ldloc_3 || o == OpCodes.Stloc_3) return 3;

            object v = c.operand;

            if (v is LocalVariableInfo lvi) return lvi.LocalIndex; // LocalBuilder 也是它的子类
            if (v is int n) return n;
            if (v is byte b) return b;
            if (v is sbyte sb) return sb;

            return -1;
        }

        // ── 夹取本身 ─────────────────────────────────────────────

        /// <summary>
        /// 装配类（含全部巨型建筑）的每分钟合成数上限。
        /// <paramref name="factory"/> 只用来查逐台的 <c>cyclesPerTick</c> 覆盖，为 null 时退回全局值。
        /// </summary>
        internal static float ClampAssembler(float perMinute, ref AssemblerComponent comp, PlanetFactory factory)
        {
            if (perMinute <= 0f) return perMinute;

            int cap = 1; // 引擎硬上限：每 tick 一个配方周期。原版机器连这个都填不满，所以天然不生效
            var divider = 1;

            if (comp.speed >= MegaBuildingRegistry.MegaSpeedThreshold)
            {
                int cycles = MegaBuildingRegistry.Config?.cyclesPerTick ?? 1;

                if (cycles < 1) cycles = 1;

                if (factory != null)
                {
                    cycles = MegaThrottle.CyclesFor(factory, comp.entityId, cycles);

                    // **分频的建筑要再除一次。** 反物质那四座配的是 tickDivider，
                    // 即「几个 tick 才结算一次」——不除的话面板照样报满速，
                    // 而那正是上一版刚修过的那类「报一个引擎不允许的数」。
                    divider = MegaThrottle.DividerFor(factory, comp.entityId);

                    if (divider < 1) divider = 1;
                }

                int gate = OutputGate(ref comp);

                cap = cycles < gate ? cycles : gate;
            }

            float real = cap * 3600f / divider;

            if (real < perMinute) ReportClampOnce(perMinute, real, cap);

            return perMinute < real ? perMinute : real;
        }

        /// <summary>
        /// 研究站。<c>GameLogic</c> 里只有 <c>_lab_produce_parallel</c>，每 tick 一次
        /// <c>InternalUpdateAssemble</c>，所以上限恒为 1 周期/tick，和是不是本 mod 抬过速度无关。
        /// </summary>
        internal static float ClampLab(float perMinute, ref LabComponent comp)
        {
            if (perMinute <= 3600f) return perMinute;

            ReportClampOnce(perMinute, 3600f, 1);

            return 3600f;
        }

        /// <summary>
        /// 原版 <c>AssemblerComponent.InternalUpdate</c> 的产出闸能放行几个周期。
        /// 三档全部照抄 IL（单产物 0138–0184、多产物 01D1–02F5 用的是同一张表，逐产物判）。
        /// 认不出就返回 <see cref="int.MaxValue"/>，让 <c>cyclesPerTick</c> 单独说了算——
        /// **宁可少夹一点，也不报一个编出来的数**。
        /// </summary>
        /// <summary>
        /// 这台机器每 tick 最多能结算几个周期——也就是原版那道产出闸。
        ///
        /// <b>两档乘法闸的系数必须问 <see cref="MegaOutputGatePatches.Scale"/>，不能写死。</b>
        /// 那个转译器把巨型建筑的闸从 9 / 19 抬到了 <c>cyclesPerTick − 1</c>，
        /// 而这里曾经把 10 / 20 写死——于是面板反过来<b>少报 3 到 6 倍</b>，
        /// 症状和当初「报一个引擎不允许的数」正好相反，而且一样不报错。
        ///
        /// 这是同一次改动里漏掉的第二份拷贝：普查那行当时改成了读同一个函数，
        /// 这里没有。**一个事实有两份手工维护的拷贝，分叉只是时间问题**——
        /// 现在两处都走 <c>Scale</c>，转译器怎么抬，面板就怎么跟。
        /// </summary>
        private static int OutputGate(ref AssemblerComponent comp)
        {
            ERecipeType type = comp.recipeType;
            int[] counts = comp.recipeExecuteData?.productCounts;

            if (counts == null || counts.Length == 0) return int.MaxValue;

            // produced[j] > productCounts[j] × K → 拒绝，于是能结算 0..K 共 K+1 个，和件数无关。
            // K 取抬过之后的值：普通装配机拿回原版的 9 / 19，巨型建筑拿 cyclesPerTick − 1。
            if (type == ERecipeType.Assemble) return MegaOutputGatePatches.Scale(9, ref comp) + 1;

            // 冶炼是唯一和件数有关的一档：produced[j] + productCounts[j] > 100 → 拒绝。
            // 这一档 MegaOutputGatePatches 故意没改（加法形状，本来就不受它限），所以照原样算。
            if (type == ERecipeType.Smelt)
            {
                int cap = int.MaxValue;

                for (var j = 0; j < counts.Length; j++)
                {
                    int c = counts[j];

                    if (c <= 0) continue;

                    int one = 100 / c;

                    if (one < cap) cap = one;
                }

                return cap;
            }

            // 其余全部（原版 2/3/5 加本 mod 的 9~17）：produced[j] > productCounts[j] × K → 拒绝
            return MegaOutputGatePatches.Scale(19, ref comp) + 1;
        }

        /// <summary>
        /// 第一次真的夹到东西时报一行。
        ///
        /// **这是事件行，不是状态行**——它只说明「夹取确实动手了」，而
        /// 「补丁有没有接上」由 <see cref="Report"/> 在开机时回答。本仓库为这两者
        /// 混为一谈付过七次来回，所以两行都留着。
        /// </summary>
        private static void ReportClampOnce(float shown, float real, int cap)
        {
            if (System.Threading.Interlocked.Exchange(ref _clampReported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"参考速率：首次夹取——原版算出 {shown:N0}/min，真实上限 {real:N0}/min"
                + $"（{cap} 个配方周期/tick × 3600）。"
                + "原版那条式子只看 speed，不知道引擎每 tick 只结算一个周期，"
                + "也不知道产出闸按配方类型分 100/10/20 三档。");
        }

        /// <summary>
        /// 开机状态行。读的是 <c>Harmony.GetAllPatchedMethods()</c> 里的<b>既成事实</b>，
        /// 不是「我调过 PatchAll 而且没抛异常」——两者的区别本仓库记过七次。
        /// 必须排在 <c>PatchAll</c> 之后。
        /// </summary>
        internal static void Report()
        {
            var attached = 0;

            foreach (MethodBase m in Harmony.GetAllPatchedMethods())
            {
                if (m == null) continue;
                if (m.Name == "AddEntryDataWithFactory" || m.Name == "CalculateFactory") attached++;
            }

            var sum = 0;

            foreach (KeyValuePair<string, int> kv in Applied) sum += kv.Value;

            if (attached == 0 || sum == 0)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "参考速率：**未生效**——"
                    + $"挂上的方法 {attached} 个、改写的落点 {sum} 处。"
                    + "面板上的「参考速率 / 理论产能」会继续按 speed 报一个到不了的数。");

                return;
            }

            int baseCycles = MegaBuildingRegistry.Config?.cyclesPerTick ?? 1;

            // **全局分频把周期数和分频数同时乘了 G**，所以：闸和周期都按 G 放大，
            // 而每分钟的产量除以 G 又把它约掉。这一行以前写 Math.Max(20, cyclesPerTick)，
            // 那就是这个数的又一份手抄件——自己的下一句还写着「不是另抄一份」。
            int g = MegaThrottle.GlobalDivider;
            int cycles = baseCycles * g;
            int gate = Math.Max(20, cycles);

            // 每分钟：min(周期, 闸) × 3600 ÷ 分频。G 在这里约掉，所以报的仍是每座的真实产量。
            int smelt = Math.Min(cycles, 100) * 3600 / g;
            int other = Math.Min(cycles, gate) * 3600 / g;

            ProjectEdenPlugin.Log.LogInfo(
                $"参考速率：已接上（{attached} 个方法、{sum} 处落点）。"
                + $"巨型建筑按 min(周期数={cycles}, 产出闸) × 3600 ÷ 分频 报，"
                + $"产出闸原版分三档（冶炼 100/件数、组装 10、其余 20），而 MegaOutputGatePatches "
                + $"把后两档对巨型建筑抬到了 {gate}——**这一行的数跟着它走，不是另抄一份**，"
                + $"所以冶炼类到 {smelt:N0}/min、组装类到 {other:N0}/min、其余到 {other:N0}/min；"
                + $"研究站没有补跑周期，恒为 3,600/min。"
                + (g > 1
                    ? $"（全局分频 G = {g}：周期和分频同乘 G，产量里约掉，所以这几个数和 G = 1 时一样。）"
                    : ""));
        }
    }
}
