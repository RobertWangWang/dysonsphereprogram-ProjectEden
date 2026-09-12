using System.Text;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把生物矩阵加进<b>宇宙矩阵的配方</b>，作为第七样原料。
    ///
    /// <b>这和「32 个科技追加生物矩阵」是两件事，都在生效。</b> 前者是造宇宙矩阵要用它，
    /// 后者是用宇宙矩阵的科技另外还要用它——叠加之后生物矩阵在终局是双重需求。
    /// 这是两条独立的设计，不是重复实现；要减哪一条得是明确决定。
    ///
    /// ── 为什么不能只改配方表 ──
    ///
    /// 原版矩阵实验室的产出模式<b>把六个投料槽写死了</b>，第七样原料会**静默饿死**：
    /// 分拣器投不投料看的是 <c>needs[]</c>，而
    ///
    /// <code>
    /// SetFunction        IL 026D–0296:  if (needs.Length != 6) needs = new int[6];  Array.Clear(needs, 0, 6);
    /// UpdateNeedsAssemble IL 0042…00D3:  needs[0] … needs[5]     ← 完全展开，从不写下标 6
    /// </code>
    ///
    /// 没有 needs 槽 ⇒ 永远不被索取 ⇒ 实验室停在那儿等一样谁也不会送来的东西，
    /// <b>而且一条报错都没有</b>。这正是 CLAUDE.md 里物流站
    /// 「原版把 <c>storage[0..5]</c> 展开，超过 6 格货物凭空消失」那条的同一个形状。
    ///
    /// ── 名单是点过名的，不是挑出来的 ──
    ///
    /// 把<b>全程序</b>碰 <c>LabComponent.served / needs / incServed</c> 的方法全列了一遍，
    /// 按「下标是字面量（展开）还是循环」分类，产出模式的投料链上只有三处展开：
    ///
    /// <list type="bullet">
    /// <item><c>SetFunction</c> —— 写死的 <c>new int[6]</c>。</item>
    /// <item><c>UpdateNeedsAssemble</c> —— <c>needs[0..5]</c>，12 处字面下标。</item>
    /// <item><c>UpdateOutputToNext</c> —— 叠放实验室之间传 <c>served</c>，0..5。
    /// （<see cref="BioMatrixPatches"/> 那个后缀只管 <c>matrixServed</c>，管不到这里。）</item>
    /// </list>
    ///
    /// 其余<b>全部是 <c>ldlen</c> 循环</b>，不用管：<c>PlanetFactory.InsertInto</c> 两个重载、
    /// <c>InternalUpdateAssemble</c>、<c>TakeBackItems_Lab</c>、<c>ThrowItems_Lab</c>、
    /// <c>EntityFastFillIn</c>、<c>UILabWindow._OnUpdate</c> / <c>OnItemButtonClick</c>、
    /// 以及几个统计面板。
    ///
    /// ── 存档安全 ──
    ///
    /// <c>LabComponent.Import</c> IL 03A5–03D2 自己会自愈：
    /// <code>
    /// int n = recipeExecuteData.requires.Length;
    /// if (served.Length != n || incServed.Length != n) { Array.Resize(ref served, n); Array.Resize(ref incServed, n); }
    /// </code>
    /// 所以老存档里 6 长的 <c>served</c> 会被补到 7，旧值保留、新槽为 0。<c>needs</c> 没有这一段，
    /// 所以下面的后缀自己兜底：用之前先确认长度，短了就地补齐。
    ///
    /// ── 时机 ──
    ///
    /// 配方表必须在 <c>PostAddDataAction</c> 里改：<c>RecipeProto.InitRecipeItems</c>
    /// 是<b>整张表重建</b>（IL 0000 <c>newobj</c> + 0005 <c>stsfld</c>），
    /// 而 LDBTool 在 <c>PostAddDataAction</c> <b>之后</b>才调它，
    /// 所以改完 <c>Items</c> 就会被它自动吸收成新的 <c>RecipeExecuteData</c>，不用自己去刷。
    /// </summary>
    [HarmonyPatch]
    internal static class UniverseMatrixPatches
    {
        /// <summary>原版产出模式写死的投料槽数。</summary>
        private const int VanillaSlots = 6;

        /// <summary>宇宙矩阵的物品 ID。</summary>
        private const int UniverseMatrix = 6006;

        /// <summary>宇宙矩阵那条配方的 ID，0 表示没接上。</summary>
        internal static int RecipeId { get; private set; }

        internal static bool Ready => RecipeId > 0;

        // ── 注册：往配方里塞第七样原料 ──────────────────────

        internal static void OnPostAddData()
        {
            RecipeId = 0;

            if (!BioMatrixPatches.Ready)
            {
                ProjectEdenPlugin.Log.LogInfo("宇宙矩阵配方：生物矩阵没启用，不改配方");

                return;
            }

            int bio = BioMatrixPatches.MatrixId;
            RecipeProto recipe = FindRecipe();

            if (recipe?.Items == null || recipe.ItemCounts == null)
            {
                ProjectEdenPlugin.Log.LogError("宇宙矩阵配方：没找到产出宇宙矩阵的配方，第七样原料没加上");

                return;
            }

            RecipeId = recipe.ID;

            if (System.Array.IndexOf(recipe.Items, bio) >= 0)
            {
                // PostAddDataAction 热重载时会重跑，别加两遍
                ProjectEdenPlugin.Log.LogInfo($"宇宙矩阵配方里已经有生物矩阵了（配方 {recipe.ID}），跳过");

                return;
            }

            // 份数照抄同一条配方里其它矩阵的用量，不自己定一个数——
            // 原版这条配方里五种矩阵是同一个份数，跟着它走就不会和原版口径打架
            int count = MatrixCount(recipe);

            recipe.Items = Append(recipe.Items, bio);
            recipe.ItemCounts = Append(recipe.ItemCounts, count);

            var sb = new StringBuilder($"宇宙矩阵配方已加入生物矩阵（配方 {recipe.ID}「{recipe.Name}」）：");

            for (var i = 0; i < recipe.Items.Length; i++)
            {
                if (i > 0) sb.Append(" + ");

                sb.Append($"{LDB.items.Select(recipe.Items[i])?.Name ?? recipe.Items[i].ToString()}×{recipe.ItemCounts[i]}");
            }

            sb.Append($" → 宇宙矩阵。投料槽 {VanillaSlots} → {recipe.Items.Length}");

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());

            if (recipe.Items.Length > VanillaSlots)
                ProjectEdenPlugin.Log.LogInfo(
                    $"　原版产出模式只认 {VanillaSlots} 个投料槽（SetFunction 的 new int[6] 与 "
                    + "UpdateNeedsAssemble 的展开写死），已由本文件的三个后缀补齐；"
                    + "否则第七样原料永远不会被分拣器索取，而且不会报错。");
        }

        /// <summary>
        /// 找出产出宇宙矩阵的那条配方。
        ///
        /// <b>按产物扫，不写死配方号。</b> 原版配方号在 <c>resources.assets</c> 里，
        /// 离线枚举不到；写死一个数字万一对不上，就是悄悄改了别的配方。
        /// </summary>
        private static RecipeProto FindRecipe()
        {
            RecipeProto[] all = LDB.recipes?.dataArray;

            if (all == null) return null;

            RecipeProto found = null;
            var hits = 0;

            foreach (RecipeProto r in all)
            {
                if (r?.Results == null || System.Array.IndexOf(r.Results, UniverseMatrix) < 0) continue;

                hits++;

                if (found == null) found = r;
            }

            if (hits > 1)
                ProjectEdenPlugin.Log.LogWarning(
                    $"产出宇宙矩阵的配方有 {hits} 条，只改了第一条（{found.ID}「{found.Name}」）");

            return found;
        }

        /// <summary>这条配方里其它矩阵用几份——照抄它，别自己定。</summary>
        private static int MatrixCount(RecipeProto recipe)
        {
            for (var i = 0; i < recipe.Items.Length; i++)
            {
                int id = recipe.Items[i];

                // 原版五种矩阵：6001–6005
                if (id >= 6001 && id <= 6005) return recipe.ItemCounts[i];
            }

            return 1;
        }

        private static T[] Append<T>(T[] array, T item)
        {
            var next = new T[array.Length + 1];
            System.Array.Copy(array, next, array.Length);
            next[array.Length] = item;

            return next;
        }

        // ── 三个补丁：把写死的 6 个投料槽补成按配方长度 ──────

        /// <summary>
        /// <c>needs</c> 在产出模式下被写死成 6 长（<c>new int[6]</c>）。
        /// 换配方时就地补齐，放在这里而不是 tick 路径上——这条一次配方只跑一次。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LabComponent), nameof(LabComponent.SetFunction))]
        private static void SetFunction_Postfix(ref LabComponent __instance)
        {
            if (__instance.researchMode) return;

            GrowNeeds(ref __instance);
        }

        /// <summary>
        /// 让 <c>needs</c> 至少和投料槽一样长。
        ///
        /// <b>三处都要兜底，因为来源不止一个：</b><c>SetFunction</c> 写死 6，
        /// <c>Import</c> 按存档里的长度读回来（它只自愈 <c>served</c> / <c>incServed</c>，不管 <c>needs</c>），
        /// 而 <c>UpdateNeedsAssemble</c> 每 tick 都要写它。所以判长度这件事做在一处，三边都调。
        /// </summary>
        private static bool GrowNeeds(ref LabComponent lab)
        {
            int[] served = lab.served;

            if (served == null) return false;

            int want = served.Length;

            if (want <= VanillaSlots) return want > 0; // 原版形状，不碰

            int[] needs = lab.needs;

            if (needs != null && needs.Length >= want) return true;

            var grown = new int[want];

            if (needs != null) System.Array.Copy(needs, grown, needs.Length);

            lab.needs = grown;

            return true;
        }

        /// <summary>
        /// 补上 <c>needs[6..]</c>。
        ///
        /// 原版把六个槽<b>完全展开</b>（IL 0042 / 005F / 007C / 0099 / 00B6 / 00D3），
        /// 公式逐字抄自它自己：
        ///
        /// <code>
        /// int batch = timeSpend &gt; 5400000 ? 6 : 3 * ((speedOverride + 5001) / 10000) + 3;   // IL 0009–0034
        /// needs[i]  = (i &lt; served.Length &amp;&amp; served[i] &lt; batch) ? requires[i] : 0;          // IL 0041–005D
        /// </code>
        ///
        /// <b>抄而不是另立一个，是为了两边不会在边界上各说各话</b>——
        /// 第七槽的进料节奏必须和前六槽完全一致，否则同一条配方的七样原料会以不同速度堆积。
        ///
        /// 只补下标 6 起，前六槽原样留给原版：改动面越小，和原版走岔的可能越小。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LabComponent), nameof(LabComponent.UpdateNeedsAssemble))]
        private static void UpdateNeedsAssemble_Postfix(ref LabComponent __instance)
        {
            if (__instance.researchMode) return;
            if (!GrowNeeds(ref __instance)) return;

            int[] served = __instance.served;
            int[] needs = __instance.needs;
            RecipeExecuteData data = __instance.recipeExecuteData;

            if (needs == null || data?.requires == null) return;

            int[] requires = data.requires;

            if (requires.Length <= VanillaSlots) return;

            // 抄 IL 0009–0034 的分批量
            int batch = data.timeSpend > 5400000
                ? 6
                : 3 * ((__instance.speedOverride + 5001) / 10000) + 3;

            for (int i = VanillaSlots; i < requires.Length && i < needs.Length; i++)
                needs[i] = i < served.Length && served[i] < batch ? requires[i] : 0;
        }

        /// <summary>
        /// 叠放实验室之间，把第 7 槽起的产出模式投料也往上传。
        ///
        /// <b><see cref="BioMatrixPatches"/> 那个同名后缀管不到这里</b>——它搬的是
        /// <c>matrixServed</c>（科研模式的矩阵），这里搬的是 <c>served</c>（产出模式的原料）。
        /// 两个数组、两条路径，原版在同一个方法里把两者都展开到了 0..5。
        ///
        /// 传递规则抄原版：只往<b>下一台确实在要这样东西</b>（<c>next.needs[i]</c> 非 0）的槽里送，
        /// 并且按比例带走增产点数——只搬数量不搬点数，就是每传一次白送一次增产。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LabComponent), nameof(LabComponent.UpdateOutputToNext))]
        private static void UpdateOutputToNext_Postfix(ref LabComponent __instance, LabComponent[] labPool)
        {
            if (__instance.researchMode) return;
            if (__instance.nextLabId <= 0 || labPool == null || __instance.nextLabId >= labPool.Length) return;

            int[] mine = __instance.served;
            int[] mineInc = __instance.incServed;

            if (mine == null || mineInc == null || mine.Length <= VanillaSlots) return;

            LabComponent next = labPool[__instance.nextLabId];

            if (next.served == null || next.incServed == null || next.needs == null) return;

            for (int i = VanillaSlots; i < mine.Length; i++)
            {
                if (i >= mineInc.Length || i >= next.served.Length || i >= next.incServed.Length) continue;
                if (i >= next.needs.Length || next.needs[i] == 0) continue;
                if (mine[i] <= 0) continue;

                // 这条跑在 _lab_output_to_next_parallel 上，和原版锁同一组数组
                lock (next.served)
                lock (mine)
                {
                    int move = mine[i];

                    if (move <= 0) continue;

                    int incMove = (int)((long)mineInc[i] * move / mine[i]);

                    mine[i] -= move;
                    mineInc[i] -= incMove;
                    next.served[i] += move;
                    next.incServed[i] += incMove;
                }
            }
        }
    }
}
