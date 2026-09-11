#pragma warning disable 649 // LabConfig 的字段由 JSON 反序列化赋值

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 矩阵研究站：制造速度 + 内部存储。研究速度（科研算力）一点不动。
    ///
    /// 两侧走的是完全不同的两条路，互不影响：
    ///   · 生产模式 LabComponent.InternalUpdateAssemble 用 speed / speedOverride
    ///   · 研究模式 LabComponent.InternalUpdateResearch 只用 GameHistoryData.techSpeed，
    ///     <b>根本不读 speed</b>，所以这里改多少都碰不到科研算力
    ///
    /// 生产模式的时间累加是：
    ///     if (replicating &amp;&amp; time &lt; timeSpend &amp;&amp; extraTime &lt; extraTimeSpend) {
    ///         time      += (int)(power * speedOverride);
    ///         extraTime += (int)(power * extraSpeed);
    ///     }
    /// 每 tick 只加一次、到点即停，所以<b>引擎硬上限就是每 tick 一个配方周期 = 60 周期/秒</b>，
    /// 跟制造台一模一样。把 speed 调到「一个 tick 就能填满 timeSpend」就已经是顶了。
    ///
    /// 尺度：RecipeProto.InitRecipeItems 里 timeSpend = TimeSpend × 10000、
    /// extraTimeSpend = TimeSpend × 100000，TimeSpend 的单位是 tick。1 倍速 = 10000。
    /// 最慢的矩阵是引力矩阵 24 秒 = 1440 tick：timeSpend 1440 万、extraTimeSpend 1.44 亿。
    /// 取 1 亿（10000 倍速）时 extraSpeed = speed × incTableMilli × 10 到 2.5 亿、
    /// extraTime 峰值约 3.9 亿，离 Int32 的 21.4 亿还有一个数量级。
    /// </summary>
    [HarmonyPatch]
    internal static class MatrixLabPatches
    {
        private static LabConfig Config => ProjectEdenPlugin.LabConfig;

        /// <summary>生效中的制造速度，0 表示不改。</summary>
        private static int _speed;

        /// <summary>研究模式每种矩阵的存量上限，已经乘好 3600 并封顶；0 表示不改。</summary>
        private static int _matrixCapScaled;

        /// <summary>
        /// matrixServed 存的是<b>物品数 × 3600</b>（PlanetFactory.InsertInto 里
        /// <c>matrixServed[idx] += 3600 * itemCount</c>），而它是 Int32。
        ///
        /// 更要命的是 UpdateOutputToNext 往上层搬料时，上层可能先被 needs 放行到
        /// 接近上限、再一次收到最多「一个上限」的量，所以峰值要按 <b>2 倍上限</b>算：
        ///     2 × cap × 3600 &lt; 2^31  ⟹  cap &lt; 298,261
        /// 取 25 万留足余量。也就是说这一格<b>物理上放不下一千万</b>。
        /// </summary>
        private const int MaxMatrixItems = 250000;

        private const int MatrixScale = 3600;

        // ── proto 阶段 ────────────────────────────────────────

        /// <summary>proto 就绪后调用：改 prefabDesc，新建的研究站直接带上这个速度。</summary>
        internal static void ApplyPrefabSpeed()
        {
            _speed = 0;
            _matrixCapScaled = 0;

            if (Config == null) return;

            ApplyMatrixCap();

            if (Config.assembleSpeed <= 0) return;

            // 不写死物品 ID：凡是 prefabDesc 打了 isLab 的都算研究站，
            // 和气体采集器那边一个口径，第三方 mod 的研究站也能覆盖到。
            var found = 0;

            foreach (ItemProto item in LDB.items.dataArray)
            {
                if (item == null) continue;

                ModelProto model = LDB.models.Select(item.ModelIndex);

                if (model?.prefabDesc == null || !model.prefabDesc.isLab) continue;

                int before = model.prefabDesc.labAssembleSpeed;

                model.prefabDesc.labAssembleSpeed = Config.assembleSpeed;
                found++;

                ProjectEdenPlugin.Log.LogInfo(
                    $"{item.name} 矩阵制造速度：{before / 10000.0:0.##} 倍 → {Config.assembleSpeed / 10000.0:0.##} 倍");
            }

            if (found == 0)
            {
                ProjectEdenPlugin.Log.LogWarning("没找到任何研究站（prefabDesc.isLab），矩阵制造速度未改");

                return;
            }

            _speed = Config.assembleSpeed;
        }

        private static void ApplyMatrixCap()
        {
            int wanted = Config.researchStorage;

            if (wanted <= 0) return;

            if (wanted > MaxMatrixItems)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"研究模式矩阵存量 {wanted} 超出上限：matrixServed 是「个数 × {MatrixScale}」的 Int32，" +
                    $"再算上向上层搬料时的二次累加，最多只能到 {MaxMatrixItems}，已按该值处理");

                wanted = MaxMatrixItems;
            }

            _matrixCapScaled = wanted * MatrixScale;

            ProjectEdenPlugin.Log.LogInfo($"研究模式每种矩阵存量上限：10 → {wanted}");
        }

        /// <summary>供 IL 调用：研究模式矩阵存量上限（放大值）。</summary>
        internal static int MatrixCapScaled() => _matrixCapScaled;

        /// <summary>供 IL 调用：生产模式产物格的堆积上限。</summary>
        internal static int AssembleOutputCap()
        {
            int cap = Config?.assembleOutputStorage ?? 0;

            return cap > 0 ? cap : 10;
        }

        // ── 制造速度 ──────────────────────────────────────────

        /// <summary>
        /// 已建成研究站的补齐。speed 是建造时从 prefabDesc 拷进来、并且<b>进存档</b>的，
        /// 改 prefabDesc 只对新建的生效。
        ///
        /// 挂在组件方法上而不是 FactorySystem.GameTickLabProduceMode，是因为
        /// GameLogic._lab_produce_parallel 会绕过后者直接调这里——多线程那条路必须覆盖到。
        /// 反过来说研究模式不走这个方法，所以科研那侧天然不受影响。
        ///
        /// speedOverride 在本方法后半段会由 speed 重算，这里一并写上只是为了让
        /// UpdateNeedsAssemble / UpdateOutputToNext 在第一次结算之前也拿到正确的值。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LabComponent), nameof(LabComponent.InternalUpdateAssemble))]
        private static void InternalUpdateAssemble_Prefix(ref LabComponent __instance)
        {
            if (_speed <= 0) return;

            if (__instance.speed != _speed)
            {
                __instance.speed = _speed;
                __instance.speedOverride = _speed;
            }
        }

        // ── 内部存储 ──────────────────────────────────────────
        //
        // 三处存储各自独立，上限都不在存储本身，而在别处：
        //   · 原料 served[]      —— 明文个数，PlanetFactory.InsertInto 不查上限，
        //                          全靠 needs[] 让分拣器停手 → 改 UpdateNeedsAssemble
        //   · 产物 produced[]    —— 上限是 InternalUpdateAssemble 里那道
        //                          `produced[i] + productCounts[i] > 10 * ceil(speedOverride/10000)`
        //                          的提前 return → 只能转译
        //   · 矩阵 matrixServed[] —— 放大 3600 倍的个数，同样靠 needs[]
        //                          → 改 UpdateNeedsResearch
        //
        // 前两者用后置重算 needs[]，比转译安全得多；产物那道闸门是 return，绕不开转译。

        /// <summary>
        /// 生产模式原料上限。原版是：
        ///     limit = timeSpend > 5400000 ? 6 : 3 * ((speedOverride + 5001) / 10000) + 3;
        ///     needs[i] = (i &lt; served.Length &amp;&amp; served[i] &lt; limit) ? requires[i] : 0;
        /// 注意慢配方（超过 9 秒，信息 / 引力 / 宇宙矩阵）那条分支是<b>硬编码的 6</b>，
        /// 光提速根本不会让它多囤料——这里直接按配置重算。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LabComponent), nameof(LabComponent.UpdateNeedsAssemble))]
        private static void UpdateNeedsAssemble_Postfix(ref LabComponent __instance)
        {
            int limit = Config?.assembleStorage ?? 0;

            if (limit <= 0) return;

            int[] needs = __instance.needs;
            int[] served = __instance.served;
            RecipeExecuteData recipe = __instance.recipeExecuteData;

            if (needs == null || served == null || recipe?.requires == null) return;

            int[] requires = recipe.requires;
            int count = served.Length < requires.Length ? served.Length : requires.Length;

            for (var i = 0; i < needs.Length; i++)
                needs[i] = i < count && served[i] < limit ? requires[i] : 0;
        }

        /// <summary>
        /// 研究模式矩阵上限。原版是 <c>needs[i] = matrixServed[i] &lt; 36000 ? 6001 + i : 0</c>，
        /// 36000 / 3600 = <b>每种只存 10 个</b>。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LabComponent), nameof(LabComponent.UpdateNeedsResearch))]
        private static void UpdateNeedsResearch_Postfix(ref LabComponent __instance)
        {
            if (_matrixCapScaled <= 0) return;

            int[] needs = __instance.needs;
            int[] matrixServed = __instance.matrixServed;
            int[] matrixIds = LabComponent.matrixIds;

            if (needs == null || matrixServed == null || matrixIds == null) return;

            int count = needs.Length < matrixServed.Length ? needs.Length : matrixServed.Length;

            if (count > matrixIds.Length) count = matrixIds.Length;

            for (var i = 0; i < count; i++)
                needs[i] = matrixServed[i] < _matrixCapScaled ? matrixIds[i] : 0;
        }

        // ── IL 改写 ───────────────────────────────────────────

        private static readonly MethodInfo AssembleOutputCapMethod =
                                               AccessTools.Method(typeof(MatrixLabPatches), nameof(AssembleOutputCap)),
                                           MatrixCapScaledMethod =
                                               AccessTools.Method(typeof(MatrixLabPatches), nameof(MatrixCapScaled));

        /// <summary>
        /// 生产模式产物格的堆积上限。原版：
        ///     if (produced[i] + productCounts[i] &gt; 10 * ((speedOverride + 9999) / 10000)) return 0;
        /// 栈上依次是 [produced+productCounts, 10, speedOverride]，把后面
        /// `9999 add 10000 div mul` 这五条改成「弹掉两个、压入我们的上限」。
        ///
        /// <b>原地改 opcode 而不是替换指令对象</b>，这样万一有跳转标签落在这几条上也不会丢。
        ///
        /// 这道闸门有<b>两处</b>：单产物的展开快路径和多产物的循环各一份。矩阵配方都是
        /// 单产物、走第一处，但两处都要换，否则以后加个多产物配方就会露馅。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(LabComponent), nameof(LabComponent.InternalUpdateAssemble))]
        private static IEnumerable<CodeInstruction> InternalUpdateAssemble_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var matcher = new CodeMatcher(instructions);
            var replaced = 0;

            while (true)
            {
                matcher.MatchForward(false,
                    new CodeMatch(i => i.opcode == OpCodes.Ldc_I4 && i.operand is int a && a == 9999),
                    new CodeMatch(OpCodes.Add),
                    new CodeMatch(i => i.opcode == OpCodes.Ldc_I4 && i.operand is int b && b == 10000),
                    new CodeMatch(OpCodes.Div),
                    new CodeMatch(OpCodes.Mul));

                if (matcher.IsInvalid) break;

                Replace(matcher.Instruction, OpCodes.Pop);                          // 弹掉 speedOverride
                Replace(matcher.Advance(1).Instruction, OpCodes.Pop);               // 弹掉 10
                Replace(matcher.Advance(1).Instruction, OpCodes.Call, AssembleOutputCapMethod);
                Replace(matcher.Advance(1).Instruction, OpCodes.Nop);
                Replace(matcher.Advance(1).Instruction, OpCodes.Nop);
                matcher.Advance(1);

                replaced++;
            }

            if (replaced < 2)
                ProjectEdenPlugin.Log.LogError($"研究站产物格堆积上限只替换了 {replaced} 处（应为 2 处），产物可能仍会提前堆积");
            else
                ProjectEdenPlugin.Log.LogInfo("研究站产物格堆积上限已接管");

            return matcher.InstructionEnumeration();
        }

        /// <summary>
        /// 堆叠研究站往上层搬矩阵时，单次搬运量被 36000（10 个）夹住：
        ///     int move = (matrixServed[i] - 7200) / 3600 * 3600;
        ///     if (move &gt; 36000) move = 36000;
        /// 这是<b>速率</b>而不是存量上限——上层能存多少由它自己的 needs 决定。
        /// 但 10 个/tick/格（600 个/秒）填不满放大后的仓，所以一并放开，
        /// 否则改了存储上限也只是个填不满的空壳。7200 那个「本层至少留 2 个」不动。
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(LabComponent), nameof(LabComponent.UpdateOutputToNext))]
        private static IEnumerable<CodeInstruction> UpdateOutputToNext_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var matcher = new CodeMatcher(instructions);
            var replaced = 0;

            while (true)
            {
                matcher.MatchForward(false,
                    new CodeMatch(i => i.opcode == OpCodes.Ldc_I4 && i.operand is int v && v == 36000));

                if (matcher.IsInvalid) break;

                matcher.Set(OpCodes.Call, MatrixCapScaledMethod);
                matcher.Advance(1);

                replaced++;
            }

            // 六种矩阵各两处（比较值 + 赋值）
            if (replaced < 12)
                ProjectEdenPlugin.Log.LogWarning($"堆叠研究站的矩阵搬运上限只替换了 {replaced} 处（应为 12 处）");
            else
                ProjectEdenPlugin.Log.LogInfo("堆叠研究站的矩阵搬运上限已接管");

            return matcher.InstructionEnumeration();
        }

        private static void Replace(CodeInstruction instruction, OpCode opcode, object operand = null)
        {
            instruction.opcode = opcode;
            instruction.operand = operand;
        }
    }

    [Serializable]
    internal class LabConfig
    {
        /// <summary>矩阵制造速度，10000 = 1 倍速。0 保持原版。</summary>
        public int assembleSpeed;

        /// <summary>生产模式每个原料格的存量上限（个）。0 保持原版。</summary>
        public int assembleStorage;

        /// <summary>生产模式每个产物格的堆积上限（个）。0 保持原版。</summary>
        public int assembleOutputStorage;

        /// <summary>研究模式每种矩阵的存量上限（个）。0 保持原版（10 个）。</summary>
        public int researchStorage;

        /// <summary>是否让研究站直接从本行星的物流站取料</summary>
        public bool logisticSupply;

        /// <summary>取料的间隔 tick 数。0 = 10</summary>
        public int supplyIntervalTicks;

        /// <summary>生产模式囤多少份配方的原料。0 = 2000</summary>
        public int supplyAssembleBatches;

        /// <summary>研究模式每种矩阵囤多少个。0 = 1000</summary>
        public int supplyMatrixItems;

        /// <summary>是否让研究站把生产模式的产物直接送进本行星的物流站</summary>
        public bool logisticOutput;

        /// <summary>出货时每个产物格里留多少个不送走。0 = 全部送走</summary>
        public int outputReserveItems;
    }
}
