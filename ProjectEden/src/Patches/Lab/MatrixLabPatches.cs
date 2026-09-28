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
    /// 取 1 亿（10000 倍速）时 extraSpeed = speed × incTableMilli × 10 到 <b>4 亿</b>，
    /// 离 Int32 的 21.4 亿还差 5.4 倍。
    ///
    /// 上面这些数字<b>是 MatrixSurvey 每局实测出来的，不是手算的</b>——它按
    /// LabComponent.matrixIds 扫出最慢配方，再拿 Cargo.incTableMilli 的实际最大值核对。
    /// 这一段原本写的是 2.5 亿，那是按增产剂 Mk.III（表里的第 4 级）算的，
    /// 而喷涂表一共十一级、满级 +40%，本 mod 的活性增产剂够得着。
    /// 换句话说：手算的那个数<b>当时就偏小，而且没有任何东西会告诉你</b>。
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

        /// <summary>
        /// 原版的搬运速率：<c>UpdateOutputToNext</c> 里那十二处 <c>36000</c>，也就是
        /// <b>10 个/tick/格</b>。它是<b>速率</b>，和 <see cref="_matrixCapScaled"/>
        /// 那个<b>容量</b>不是一回事——详见 <c>ApplyTransferRate</c>。
        /// </summary>
        private const int VanillaTransferScaled = 36000;

        /// <summary>生效中的搬运速率，已经乘好 3600。</summary>
        private static int _transferRateScaled = VanillaTransferScaled;

        // ── proto 阶段 ────────────────────────────────────────

        /// <summary>proto 就绪后调用：改 prefabDesc，新建的研究站直接带上这个速度。</summary>
        internal static void ApplyPrefabSpeed()
        {
            _speed = 0;
            _matrixCapScaled = 0;
            _transferRateScaled = VanillaTransferScaled;

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

            ApplyTransferRate(wanted);
        }

        /// <summary>
        /// 堆叠研究站往上层搬矩阵的<b>速率</b>。
        ///
        /// <para><b>它和容量是两回事，而 1.12.17 及以前把两者混成了一个数。</b>
        /// CLAUDE.md 早就记着「<c>UpdateOutputToNext</c> 里那十二个 36000 是每 tick 的
        /// 搬运速率，不是存量上限」，而那个转译器偏偏把它们换成了
        /// <c>researchStorage × 3600</c>。</para>
        ///
        /// <para><b>速率等于容量的后果是「一 tick 搬空」。</b> 发货方写死「本层只留 2 个」
        /// （IL 0141 的 <c>7200</c> = 2 × 3600），停不停<b>只</b>由收货方的 <c>needs</c> 决定
        /// （IL 0124 那句 <c>next.needs[0] == 6001</c> 是唯一的闸）。所以只要速率 ≥ 容量，
        /// 下层每 tick 都会把存货一次性全推上去、自己永远停在 2 个——上层填满之前，
        /// 整座塔的货都在最顶上。玩家报的就是这个：
        /// 「矩阵研究站叠加到一起建造时……所有科研用的矩阵都跑到最上层去了」。</para>
        ///
        /// <para><b>默认回到原版的 10 个/tick。</b> 那是 600 个/秒，而研究模式每种矩阵
        /// 每秒最多吃 <c>ItemPoints × (techSpeed + 2) / 60</c> 个——原版能达到的 techSpeed
        /// 量级下那是零点几个。转译器仍然跑、仍然断言十二处，只是写回同一个常量：
        /// 这样游戏更新挪了那几处会当场失败，而不是静默失准。</para>
        /// </summary>
        private static void ApplyTransferRate(int capItems)
        {
            int rate = Config?.researchTransferRate ?? 0;

            if (rate <= 0)
            {
                _transferRateScaled = VanillaTransferScaled;

                ProjectEdenPlugin.Log.LogInfo(
                    $"堆叠研究站矩阵搬运速率：保持原版 {VanillaTransferScaled / MatrixScale} 个/tick/格" +
                    $"（{VanillaTransferScaled / MatrixScale * 60} 个/秒）。"
                    + "**这是速率不是容量**——两者相等就等于一 tick 搬空，下层永远停在 2 个、"
                    + "货全堆在最上层，那正是 1.12.17 及以前的表现。");

                return;
            }

            // 必须严格小于容量，否则又回到「一 tick 搬空」。不改成静默夹住：
            // 那会让配置说的和实际做的不一样，而这正是这次要修的那类错。
            if (rate >= capItems)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"researchTransferRate={rate} 不小于 researchStorage={capItems}："
                    + "搬运速率一旦追平容量，下层每 tick 都会被搬空、永远停在 2 个，"
                    + "货会全堆在最上层。已改用原版速率，请把它调到容量以下。");

                _transferRateScaled = VanillaTransferScaled;

                return;
            }

            _transferRateScaled = rate * MatrixScale;

            ProjectEdenPlugin.Log.LogInfo(
                $"堆叠研究站矩阵搬运速率：{VanillaTransferScaled / MatrixScale} → {rate} 个/tick/格"
                + $"（{rate * 60} 个/秒），容量上限是 {capItems} 个。");
        }

        // ── 矩阵配方时间 ──────────────────────────────────────

        /// <summary>
        /// 把每一种研究矩阵的配方时间改成配置值（<c>lab.json</c> 的 <c>matrixTimeSpend</c>，单位帧）。
        ///
        /// <b>判据是 <c>LabComponent.matrixIds</c>，不是七条硬编码的配方号。</b>
        /// 要求是「全部矩阵」，而那个数组<b>就是引擎自己对「什么算研究矩阵」的回答</b>；
        /// 生物矩阵已由 <see cref="BioMatrixPatches"/> 接在它后面，所以一起覆盖到，
        /// 将来再加第八种也不用回来改这里。把七个配方号抄进配置，是本文件反复记过的
        /// 「同一个事实存了 N 份手工副本」——总有一天只更新其中三份。
        ///
        /// <b>时机必须在 <c>PostAddDataAction</c>，而且要排在 <c>BioMatrixPatches</c> 之后。</b>
        /// 两件事：一是 <c>matrixIds</c> 是它接长的，早了就只扫到六种；
        /// 二是 LDBTool 在这个动作<b>之后</b>才调 <c>RecipeProto.InitRecipeItems</c>，
        /// 而 <c>timeSpend = TimeSpend × 10000</c> / <c>extraTimeSpend = × 100000</c>
        /// 正是在那里算的（IL @003C / @004A）——所以改 <c>TimeSpend</c> 是白捡的，
        /// 不需要自己去刷 <c>recipeExecuteData</c>。和宇宙矩阵加第七种原料同一个时机。
        ///
        /// <b>存档安全：改的是值，不是数组长度。</b> 本仓库的老账分得很清——
        /// 改 <c>Items</c>/<c>Results</c> 的<b>长度</b>会让 <c>Export</c> 写出的
        /// <c>served</c>/<c>produced</c> 条数对不上，而改 <c>TimeSpend</c> 只是个值。
        /// 已建成的研究站也不用运行时补：<c>LabComponent.Import</c> @0381–0391
        /// 是从 <c>RecipeProto.recipeExecuteData</c> 这张静态表里<b>重新取</b>的，
        /// 读档就拿到新值。存档里那个 <c>time</c> 要是比新的 <c>timeSpend</c> 还大，
        /// 下一帧直接结算掉，不会卡住。
        ///
        /// <b>副作用一条，已知且已被压住：</b><c>ProtoSignature.CalculateSignature</c>
        /// 把 <c>TimeSpend</c> 写进签名（IL @0180），所以这一改会触发
        /// <c>ABN_ProtoData</c> 的 recipes(4) 判定——本 mod 光是注册配方就已经在触发它了，
        /// <c>abnormality.json</c> 默认把它整个压住。
        /// </summary>
        internal static void ApplyMatrixTime()
        {
            int wanted = Config?.matrixTimeSpend ?? 0;

            if (wanted <= 0)
            {
                // 报无聊的那一面：保持原版和「这段代码没上线」不能长得一样
                ProjectEdenPlugin.Log.LogInfo(
                    $"矩阵配方时间：未启用（lab.json 的 matrixTimeSpend = {wanted}），保持原版");

                return;
            }

            // 原版自己的下限：RecipeProto.Preload @00B7 是 `if (TimeSpend < 1) TimeSpend = 1`。
            // 抄它的阈值，而不是自己定一个，两边就不会在边界上打架
            if (wanted < 1) wanted = 1;

            int[] ids = LabComponent.matrixIds;

            if (ids == null || ids.Length == 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    "矩阵配方时间：LabComponent.matrixIds 是空的，一条都没改 —— 这不正常，原版至少有六种");

                return;
            }

            RecipeProto[] recipes = LDB.recipes?.dataArray;

            if (recipes == null)
            {
                ProjectEdenPlugin.Log.LogError("矩阵配方时间：LDB.recipes 还没建好，一条都没改");

                return;
            }

            var changed = 0;
            var kinds = 0;

            foreach (int id in ids)
            {
                var hit = false;

                foreach (RecipeProto recipe in recipes)
                {
                    if (recipe?.Results == null) continue;

                    var makes = false;

                    for (var i = 0; i < recipe.Results.Length; i++)
                        if (recipe.Results[i] == id)
                            makes = true;

                    if (!makes) continue;

                    hit = true;

                    if (recipe.TimeSpend == wanted) continue;

                    int before = recipe.TimeSpend;

                    recipe.TimeSpend = wanted;
                    changed++;

                    ProjectEdenPlugin.Log.LogInfo(
                        $"  配方「{recipe.Name}」时间：{before} 帧（{before / 60.0:0.##} 秒）"
                        + $" → {wanted} 帧（{wanted / 60.0:0.##} 秒）");
                }

                if (hit)
                {
                    kinds++;

                    continue;
                }

                // 一种矩阵扫不到产出配方，说明槽位表和配方表脱节，
                // 而它在日志里和「我没扫」长得一样，所以显式说出来
                ItemProto item = LDB.items?.Select(id);

                ProjectEdenPlugin.Log.LogWarning(
                    $"矩阵配方时间：{item?.Name ?? id.ToString()}({id}) 没有任何配方产出它，跳过");
            }

            if (changed == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    $"矩阵配方时间：{kinds} 种矩阵都已经是 {wanted} 帧了，一条都没改 —— "
                    + "如果这不是你预期的，检查 matrixTimeSpend 是不是和原版值撞上了");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"矩阵配方时间：{kinds} 种矩阵、改了 {changed} 条配方 → 每条 {wanted / 60.0:0.##} 秒。"
                    + "注意研究站里的产能不会因此变化——引擎每帧只结算一个周期，"
                    + "本 mod 的速度早已让每条配方都在一帧内填满，改的是手搓时间和面板显示");
        }

        /// <summary>供 IL 调用：研究模式矩阵存量上限（放大值）。<b>容量</b>，不是速率。</summary>
        internal static int MatrixCapScaled() => _matrixCapScaled;

        /// <summary>
        /// 供 IL 调用：堆叠研究站往上层搬矩阵的<b>速率</b>上限（放大值，每 tick 每格）。
        /// 默认就是原版那个 36000，也就是 10 个/tick——和 <see cref="MatrixCapScaled"/>
        /// 是两个量，混用会让下层每 tick 被搬空（详见 <c>ApplyTransferRate</c>）。
        /// </summary>
        internal static int TransferRateScaled() =>
            _transferRateScaled > 0 ? _transferRateScaled : VanillaTransferScaled;

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
                                               AccessTools.Method(typeof(MatrixLabPatches), nameof(MatrixCapScaled)),
                                           TransferRateScaledMethod =
                                               AccessTools.Method(typeof(MatrixLabPatches), nameof(TransferRateScaled));

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
        ///
        /// <para><b>这段注释以前就是对的，而代码没照它写。</b> 上一版把这十二处换成了
        /// <c>MatrixCapScaled()</c>（= <c>researchStorage × 3600</c>），理由写的是
        /// 「10 个/tick 填不满放大后的仓」。理由本身没错（25 万的仓按 600 个/秒要填七分钟），
        /// 但结论把速率和容量画上了等号，而<b>速率 ≥ 容量就意味着一 tick 搬空</b>：
        /// 发货方写死「本层只留 2 个」（IL 0141 的 7200），停不停只由收货方的 needs 决定，
        /// 于是下层永远停在 2 个、货全堆在最上层。玩家报的就是这个。</para>
        ///
        /// <para>现在改用 <see cref="TransferRateScaled"/>，默认写回原版那个 36000
        /// ——<b>转译器照样跑、照样断言十二处</b>，这样游戏更新挪了那几处会当场失败，
        /// 而不是静默失准。7200 那个「本层至少留 2 个」不动。</para>
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

                // **速率**，不是容量——这两个量混用就是「一 tick 搬空」。
                matcher.Set(OpCodes.Call, TransferRateScaledMethod);
                matcher.Advance(1);

                replaced++;
            }

            // 六种矩阵各两处（比较值 + 赋值）
            if (replaced < 12)
                ProjectEdenPlugin.Log.LogWarning($"堆叠研究站的矩阵搬运速率只替换了 {replaced} 处（应为 12 处）");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    "堆叠研究站的矩阵搬运速率已接管（12 处）。**这是速率不是容量**；"
                    + "默认写回原版的 10 个/tick，所以行为与原版一致，"
                    + "但断言仍然守着那 12 处，游戏更新挪了位置会当场报出来");

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

        /// <summary>每一种研究矩阵的配方时间，单位帧（60 帧 = 1 秒）。0 保持原版。</summary>
        public int matrixTimeSpend;

        /// <summary>生产模式每个原料格的存量上限（个）。0 保持原版。</summary>
        public int assembleStorage;

        /// <summary>生产模式每个产物格的堆积上限（个）。0 保持原版。</summary>
        public int assembleOutputStorage;

        /// <summary>研究模式每种矩阵的存量上限（个）。0 保持原版（10 个）。</summary>
        public int researchStorage;

        /// <summary>
        /// 堆叠研究站往上层搬矩阵的<b>速率</b>（个/tick/格）。0 = 保持原版的 10。
        /// 和 <see cref="researchStorage"/> 是两个量：速率追平容量就等于一 tick 搬空，
        /// 下层永远停在 2 个、货全堆在最上层。必须小于 researchStorage。
        /// </summary>
        public int researchTransferRate;

        /// <summary>是否让研究站直接从本行星的供应物流站取料；同时开启出货时也从其他研究站取料</summary>
        public bool logisticSupply;

        /// <summary>取料的间隔 tick 数。0 = 10</summary>
        public int supplyIntervalTicks;

        /// <summary>生产模式囤多少份配方的原料。0 = 2000</summary>
        public int supplyAssembleBatches;

        /// <summary>研究模式每种矩阵囤多少个。0 = 1000</summary>
        public int supplyMatrixItems;

        /// <summary>是否让研究站产物直接供给本行星的其他研究站和需求物流站</summary>
        public bool logisticOutput;

        /// <summary>出货时每个产物格里留多少个不送走。0 = 全部送走</summary>
        public int outputReserveItems;

        /// <summary>终局科技要不要**直接**列出生物矩阵。false 时只靠宇宙矩阵配方间接需要。</summary>
        public bool bioMatrixInTechs;

        /// <summary>实验室 3D 动画里代表生物矩阵的数字；0 表示不改，保持原版。</summary>
        public int bioMatrixShaderDigit;

        /// <summary>那五个动画位置怎么排，每位填 6（宇宙矩阵）或 7（生物矩阵）。</summary>
        public int bioMatrixShaderPattern;
    }
}
