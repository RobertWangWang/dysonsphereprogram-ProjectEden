using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 第七种科研矩阵（生物矩阵）接进实验室。完整勘察与推导见仓库根目录的 `生物矩阵V1.md`。
    ///
    /// <b>一条硬约束决定了整个做法：矩阵的物品号必须和 6001 连续。</b>
    /// <c>PlanetFactory.InsertInto</c>（投料进实验室那条路）IL 03F6 是
    /// <c>slot = itemId - 6001</c>，再判 <c>slot &lt; 0 || slot &gt;= 6</c> 就丢弃；
    /// <c>FactorySystem.GameTickLabResearchMode</c> 用同一个减法算 3D 动画状态。
    /// 所以槽位不是查表得来的，是<b>减法算出来的</b>——号一旦不连续，
    /// 矩阵既进不了实验室，也不会报任何错。生物矩阵因此定在 <b>6007</b>。
    ///
    /// <b>好消息是这一层几乎全是数据驱动的。</b> <c>LabComponent.SetFunction</c> 里
    /// <c>needs</c> / <c>matrixServed</c> / <c>matrixIncServed</c> 都按
    /// <c>matrixIds.Length</c> 开，而且 <c>needs</c> 那句还写着
    /// <c>if (needs.Length != matrixIds.Length) 重开</c>——<b>自愈的</b>。
    /// <c>UILabWindow._OnInit</c> 同样按这个长度开 <c>matrixProtos</c> / <c>matrixRecipes</c>。
    /// 所以把三张静态表从 6 扩到 7，大半个系统自己就跟上了。
    ///
    /// <b>剩下要手工补的只有四处</b>，也就是全程序里唯一用字面下标 0–5 的那三个方法，
    /// 加上 InsertInto 的那个上界常量：
    ///
    /// <list type="bullet">
    /// <item><c>InsertInto</c> 的 <c>slot &gt;= 6</c> —— 转译成按 <c>matrixIds.Length</c> 判。</item>
    /// <item><c>UpdateNeedsResearch</c> —— 后缀补第 7 槽的 needs。</item>
    /// <item><c>InternalUpdateResearch</c> —— 前缀限速 + 后缀扣料。</item>
    /// <item><c>UpdateOutputToNext</c> —— 后缀补第 7 槽的叠放传递。</item>
    /// </list>
    ///
    /// <b>三个都用前后缀而不是转译。</b> 那三个方法把六个槽<b>完全展开</b>写死
    /// （45 / 42 / 6 处字面下标），把展开块改写成循环是大手术；
    /// 而按仓库规矩本来就是「前后缀优先，转译是不得已」。
    /// 唯一的转译是 InsertInto 里那一个常量，锚在它前面的 <c>ldc.i4 6001 ; sub</c> 上，特征唯一。
    /// </summary>
    [HarmonyPatch]
    internal static class BioMatrixPatches
    {
        /// <summary>生物矩阵的物品 ID，0 表示这套东西没启用。</summary>
        internal static int MatrixId { get; private set; }

        /// <summary>它在矩阵数组里的下标。</summary>
        internal static int Slot { get; private set; } = -1;

        /// <summary>原版最后一种矩阵（宇宙矩阵）。科技追加以它为锚。</summary>
        private const int UniverseMatrix = 6006;

        /// <summary>
        /// 宇宙矩阵在动画表里的下标。<c>GameTickLabResearchMode</c> 对前五种矩阵用 5 位掩码
        /// （0..31），碰到宇宙矩阵就直接置 32 并跳出，所以 32 就是「要宇宙矩阵」那一档。
        /// </summary>
        private const int UniverseSlot = 32;

        private const string ItemKey = "bio-matrix";

        /// <summary>
        /// ores.json 里配的名字。核对「这个号上坐的确实是它」时用，
        /// 比的是 <c>ItemProto.Name</c>（原始键）而不是 <c>name</c>（译文）——
        /// 后者在英文客户端下是 Bio Matrix，比名字就全挂了。
        /// </summary>
        private const string ItemName = "生物矩阵";

        internal static bool Ready => MatrixId > 0 && Slot >= 0;

        // ── 注册：扩表 + 追加科技 ──────────────────────────────

        internal static void OnPostAddData()
        {
            MatrixId = 0;
            Slot = -1;

            int id = OreRegistry.FindItemIdByRef(ItemKey);

            if (id <= 0)
            {
                ProjectEdenPlugin.Log.LogInfo($"生物矩阵：ores.json 里没有「{ItemKey}」，第七种矩阵不启用");

                return;
            }

            // <b>核对末态，而不是相信自己申请的号。</b> FindItemIdByRef 给的是
            // ores.json 里配的（或本次解析出的）号，而 LDBTool 的 CustomID.cfg
            // <b>按名字记号，并在本 mod 注册之后反向盖回去</b>——所以「我们申请了 6007」
            // 和「6007 上真的坐着生物矩阵」是两件事。
            //
            // 对别的物品，号错了只是图标和格位错位；对矩阵是<b>整个功能无声失效</b>：
            // 矩阵表里写的号上没有 proto，而真正的 proto（比如被钉在 6644）算出来的槽位
            // 是 6644−6001=643，被 InsertInto 当场丢弃，一句报错都没有。
            // 所以这里宁可不启用，也不能扩一张指向空气的表。
            ItemProto proto = LDB.items.Select(id);

            if (proto == null || proto.Name != ItemName)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"生物矩阵本应占用物品 ID {id}，实际那里是「{proto?.Name ?? "空"}」，第七种矩阵不启用。"
                    + "这几乎可以肯定是 BepInEx/config/LDBTool/LDBTool.CustomID.cfg 里存着旧的绑定"
                    + "（它按名字记 ID，并在本 mod 之后反向覆盖）。"
                    + "**先退出游戏**，把该文件 [Item] 段的「生物矩阵」和 [Recipe] 段的"
                    + "「生物矩阵 · 菌丝培养」两行删掉，再重开，LDBTool 会按新值重新写入。");

                return;
            }

            int[] ids = LabComponent.matrixIds;

            if (ids == null || ids.Length == 0)
            {
                ProjectEdenPlugin.Log.LogError("生物矩阵：LabComponent.matrixIds 是空的，放弃");

                return;
            }

            if (Array.IndexOf(ids, id) >= 0)
            {
                // 热重载会重跑 PostAddData，这时表已经扩过了
                Slot = Array.IndexOf(ids, id);
                MatrixId = id;

                ProjectEdenPlugin.Log.LogInfo($"生物矩阵：矩阵表里已经有 {id} 了（第 {Slot} 槽），跳过扩表");

                SyncTechs();
                ApplyShaderState();

                return;
            }

            // <b>连续性是硬要求，不是风格。</b> 槽位 = itemId - matrixIds[0]，
            // 不连续的话 InsertInto 直接把它丢掉，而且一声不吭
            int want = ids[ids.Length - 1] + 1;

            if (id != want)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"生物矩阵的物品号是 {id}，但矩阵槽位是**按 itemId − {ids[0]} 算出来的**"
                    + $"（PlanetFactory.InsertInto IL 03F6），所以它必须紧接在上一种矩阵后面，也就是 {want}。"
                    + "现在这个号会让它永远进不了实验室，而且不会报错。第七种矩阵不启用");

                return;
            }

            MatrixId = id;
            Slot = ids.Length;

            Grow(ref LabComponent.matrixIds, id);
            Grow(ref TechProto.matrixIds, id);
            GrowPoints();

            ProjectEdenPlugin.Log.LogInfo(
                $"生物矩阵已接入矩阵表：物品 {id}，第 {Slot} 槽。"
                + $"LabComponent.matrixIds={LabComponent.matrixIds.Length} "
                + $"matrixPoints={LabComponent.matrixPoints.Length} "
                + $"TechProto.matrixIds={TechProto.matrixIds.Length}");

            SyncTechs();
            ApplyShaderState();
        }

        /// <summary>
        /// 让实验室建筑上那圈 3D 动画把生物矩阵也算进去。
        ///
        /// ── 这套编码是怎么回事 ──
        ///
        /// <c>FactorySystem.GameTickLabResearchMode</c>（IL 0111–0181）把当前科技要的矩阵
        /// 折成一个下标，再查表写进 <c>AnimData.working_length</c> 交给着色器：
        ///
        /// <code>
        /// int state = 0;
        /// foreach (int item in tech.Items) {
        ///     int slot = item - matrixIds[0];                 // = itemId − 6001
        ///     if (slot >= 0 &amp;&amp; slot &lt; 5) state |= 1 &lt;&lt; slot;  // 前五种：5 位掩码 0..31
        ///     else if (slot == 5) { state = 32; break; }      // 宇宙矩阵：直接跳到「全套」
        /// }
        /// working_length = (float)techShaderStates[clamp(state, 0, 32)] + 0.2f;
        /// </code>
        ///
        /// 表值是<b>五位数字，每位 = 建筑上该位置显示第几种矩阵</b>。对照原版数据即可确认：
        /// 索引 31（前五种全要）是 <c>23514</c>——五种各来一个；
        /// 索引 32（要宇宙矩阵）是 <c>66666</c>——五个位置全显示第 6 种。
        ///
        /// ── 为什么一个数组元素就够，不用转译 ──
        ///
        /// <c>state = 32</c> 恰好发生在「这个科技要宇宙矩阵」时，而本 mod 把生物矩阵
        /// 追加给了<b>所有</b>要宇宙矩阵的科技。<b>于是「下标 32」等价于「这个科技要生物矩阵」</b>，
        /// 改 <c>techShaderStates[32]</c> 就够了——不必去动那个每帧跑在
        /// <c>_lab_produce_parallel</c> 旁边的热方法，也不必扩掩码位宽。
        ///
        /// 这个等价关系是<b>本 mod 自己造出来的</b>，不是原版性质：哪天科技追加那条被砍掉，
        /// 这里就得跟着改。所以下面显式核对一遍再写。
        ///
        /// ── 唯一的未知，以及为什么它可以退化 ──
        ///
        /// <b>数字 7 这个着色器认不认，离线无法回答。</b> 原版表里从来没出现过 7，
        /// 而着色器是编译好的资产——CLAUDE.md 里矿脉换色那次的教训正是「只抄了打包的一半，
        /// 结果矿脉在地表根本不画」。所以这里不赌：
        ///
        /// <list type="number">
        /// <item>默认图案 <c>67676</c> <b>故意保留三个 6</b>。万一 7 不被认，
        /// 至少还有三格是对的，失败是<b>退化</b>而不是一片空白。</item>
        /// <item>整件事挂在 <c>lab.json</c> 的开关上，填 0 就回原版，<b>不用重新编译</b>。</item>
        /// <item>无论开没开都打一行日志——「没生效」和「这段代码根本没上线」不能长得一样。</item>
        /// </list>
        /// </summary>
        private static void ApplyShaderState()
        {
            LabConfig cfg = ProjectEdenPlugin.LabConfig;
            int digit = cfg?.bioMatrixShaderDigit ?? 0;
            int[] table = LabComponent.techShaderStates;

            if (table == null || UniverseSlot >= table.Length)
            {
                ProjectEdenPlugin.Log.LogWarning("生物矩阵 3D 动画：techShaderStates 读不到，跳过");

                return;
            }

            if (digit <= 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    $"生物矩阵 3D 动画：未启用（lab.json 的 bioMatrixShaderDigit = {digit}），"
                    + $"实验室上仍按原版的 {table[UniverseSlot]} 播放（五个位置全是宇宙矩阵）");

                return;
            }

            // 核对那个等价关系：要宇宙矩阵的科技，是不是真的都要生物矩阵
            var universe = 0;
            var both = 0;

            foreach (TechProto tech in LDB.techs?.dataArray ?? new TechProto[0])
            {
                if (tech?.Items == null || Array.IndexOf(tech.Items, UniverseMatrix) < 0) continue;

                universe++;

                if (Array.IndexOf(tech.Items, MatrixId) >= 0) both++;
            }

            // 前提有两种成立方式，满足其一即可：
            //   (a) 科技表里直接带着生物矩阵（bioMatrixInTechs = true 那条路）；
            //   (b) 宇宙矩阵的配方里含生物矩阵——那么「这个科技要宇宙矩阵」就蕴含
            //       「它要消耗生物矩阵」，只是隔了一层。
            // 默认走的是 (b)。两条都不成立才拒绝改动画。
            bool viaRecipe = UniverseMatrixPatches.Ready && RecipeHasBio();

            if (both != universe && !viaRecipe)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    $"生物矩阵 3D 动画：{universe} 个科技要宇宙矩阵，其中只有 {both} 个直接要生物矩阵，"
                    + "而宇宙矩阵的配方里也没有它——「动画下标 32 意味着要消耗生物矩阵」这个前提两条路都不成立，"
                    + "为免给用不到它的科技画上它，本次不改动画。");

                return;
            }

            int pattern = cfg.bioMatrixShaderPattern > 0 ? cfg.bioMatrixShaderPattern : 67676;
            int value = Rewrite(pattern, digit);
            int before = table[UniverseSlot];

            table[UniverseSlot] = value;

            ProjectEdenPlugin.Log.LogInfo(
                $"生物矩阵 3D 动画已接入：techShaderStates[{UniverseSlot}] {before} → {value}"
                + $"（五个动画位置里 {CountDigit(value, digit)} 个给生物矩阵，其余给宇宙矩阵），"
                + $"覆盖 {universe} 个要宇宙矩阵的科技（其中直接列出生物矩阵的 {both} 个）。"
                + "着色器认不认数字 7 只能在游戏里看——认不出的话那几格会空着或颜色不对，"
                + "不影响任何逻辑；想回原版把 lab.json 的 bioMatrixShaderDigit 填 0。");
        }

        /// <summary>宇宙矩阵的配方里是不是含生物矩阵。</summary>
        private static bool RecipeHasBio()
        {
            RecipeProto recipe = LDB.recipes?.Select(UniverseMatrixPatches.RecipeId);

            return recipe?.Items != null && Array.IndexOf(recipe.Items, MatrixId) >= 0;
        }

        /// <summary>把图案里的 7 换成实际要用的数字（图案用 7 写只是为了读着直观）。</summary>
        private static int Rewrite(int pattern, int digit)
        {
            var result = 0;
            var scale = 1;

            for (var i = 0; i < 5; i++)
            {
                int d = pattern % 10;
                pattern /= 10;

                if (d == 7) d = digit;

                result += d * scale;
                scale *= 10;
            }

            return result;
        }

        private static int CountDigit(int value, int digit)
        {
            var n = 0;

            for (var i = 0; i < 5; i++)
            {
                if (value % 10 == digit) n++;

                value /= 10;
            }

            return n;
        }

        private static void Grow(ref int[] array, int id)
        {
            if (array == null) return;
            if (Array.IndexOf(array, id) >= 0) return;

            int[] next = new int[array.Length + 1];
            Array.Copy(array, next, array.Length);
            next[array.Length] = id;
            array = next;
        }

        /// <summary>
        /// <c>matrixPoints</c> 存的是「当前科技每种矩阵要几份」，每帧由
        /// <c>FactorySystem.GameTickLabResearchMode</c> 清空后重填，所以只需要长度对。
        /// </summary>
        private static void GrowPoints()
        {
            int[] p = LabComponent.matrixPoints;

            if (p == null || p.Length >= LabComponent.matrixIds.Length) return;

            var next = new int[LabComponent.matrixIds.Length];
            Array.Copy(p, next, p.Length);
            LabComponent.matrixPoints = next;
        }

        /// <summary>
        /// 凡是要宇宙矩阵的科技，一并要生物矩阵，份数与宇宙矩阵相同。
        ///
        /// <b>选「改原版科技」而不是「新增科技节点」的理由</b>（设计稿 §2）：
        /// 用宇宙矩阵的科技本来就是终局那一批，而莫桑石是「跑遍外星系才找得到」的矿，
        /// 两者的位置天然对得上，不用另外设计门槛；也不用去碰科技树的布局。
        ///
        /// <b>必须幂等。</b> PostAddData 在热重载时会重跑，同一个科技追加两次就要双份——
        /// 按「Items 里已含该 ID 就跳过」挡住。
        /// </summary>
        /// <summary>
        /// 有没有科技<b>直接</b>要生物矩阵。这是从 <c>LDB.techs</c> 里数出来的，不是从开关推的——
        /// 开关说的是「本次要不要追加」，这里问的是「追加完之后事实如何」。
        /// 两者在热重载、或者别的 mod 也动了科技表时会不一样。
        /// </summary>
        internal static bool UsedByTechs { get; private set; }

        /// <summary>
        /// 按配置决定要不要把生物矩阵追加进终局科技，然后<b>核对末态</b>。
        ///
        /// <b>默认不追加。</b> 宇宙矩阵的配方里已经含了一份生物矩阵，科技再单独要一份就是双重需求；
        /// 砍掉之后生物矩阵仍然是终局的硬性需求，只是只算一遍。
        /// 想要双重需求就把 <c>lab.json</c> 的 <c>bioMatrixInTechs</c> 填 true。
        ///
        /// <c>UsedByTechs</c> 是后面三处的依据：科研模式的第七格要不要画、要不要索取，
        /// 以及 3D 动画按哪个前提成立。<b>它们全都跟着数据走，不各自写死一遍这个决定。</b>
        /// </summary>
        private static void SyncTechs()
        {
            bool want = ProjectEdenPlugin.LabConfig?.bioMatrixInTechs ?? false;

            if (want) AppendToTechs();

            var used = 0;

            foreach (TechProto tech in LDB.techs?.dataArray ?? new TechProto[0])
                if (tech?.Items != null && Array.IndexOf(tech.Items, MatrixId) >= 0)
                    used++;

            UsedByTechs = used > 0;

            if (!want)
                ProjectEdenPlugin.Log.LogInfo(
                    "生物矩阵：终局科技不直接要它（lab.json 的 bioMatrixInTechs = false）——"
                    + "科技只要宇宙矩阵，而宇宙矩阵的配方里含一份生物矩阵，所以它仍是终局的硬性需求，只是只算一遍。"
                    + (used > 0 ? $"　注意：仍有 {used} 个科技的表里带着它（八成是存档或别的 mod 留下的）。" : ""));
        }

        private static void AppendToTechs()
        {
            TechProto[] techs = LDB.techs?.dataArray;

            if (techs == null)
            {
                ProjectEdenPlugin.Log.LogWarning("生物矩阵：LDB.techs 还没建好，科技追加跳过");

                return;
            }

            var touched = 0;
            var already = 0;

            foreach (TechProto tech in techs)
            {
                if (tech?.Items == null || tech.ItemPoints == null) continue;
                if (tech.Items.Length != tech.ItemPoints.Length) continue;

                int at = Array.IndexOf(tech.Items, UniverseMatrix);

                if (at < 0) continue;

                if (Array.IndexOf(tech.Items, MatrixId) >= 0)
                {
                    already++;

                    continue;
                }

                var items = new int[tech.Items.Length + 1];
                var points = new int[tech.ItemPoints.Length + 1];

                Array.Copy(tech.Items, items, tech.Items.Length);
                Array.Copy(tech.ItemPoints, points, tech.ItemPoints.Length);

                items[tech.Items.Length] = MatrixId;
                points[tech.ItemPoints.Length] = tech.ItemPoints[at];

                tech.Items = items;
                tech.ItemPoints = points;

                touched++;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"生物矩阵已追加到 {touched} 个科技（凡是要宇宙矩阵的，份数与它相同）"
                + (already > 0 ? $"；另有 {already} 个之前就加过了，跳过" : ""));
        }

        // ── 三个展开方法的补丁 ────────────────────────────────

        /// <summary>
        /// <c>needs[i] = matrixServed[i] &lt; 36000 ? matrixIds[i] : 0</c>，原版展开了六次。
        /// 这里补第 7 槽。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LabComponent), nameof(LabComponent.UpdateNeedsResearch))]
        private static void UpdateNeedsResearch_Postfix(ref LabComponent __instance)
        {
            if (!Ready) return;

            int[] needs = __instance.needs;
            int[] served = __instance.matrixServed;

            if (needs == null || served == null) return;
            if (Slot >= needs.Length || Slot >= served.Length) return;

            // 没有任何科技要它的话就别索取。原版对六种矩阵是一律囤到 36000 的，
            // 照抄那个行为会让研究站永远往第七格搬一样永远用不掉的东西——
            // 在原版那是「暂时用不到的矩阵」，在这里是「根本不会被消耗的矩阵」，两回事。
            needs[Slot] = UsedByTechs && served[Slot] < 36000 ? MatrixId : 0;
        }

        /// <summary>
        /// 研究结算。原版算的是「各槽里够做几次」取最小，然后按那个次数扣料——
        /// 六个槽全展开写死，第 7 槽不在其中。
        ///
        /// <b>不转译那 45 处，改成前后缀夹住它：</b>
        ///
        /// <list type="number">
        /// <item><b>前缀限速。</b> 原版的上限起点是 <c>(int)(research_speed + 2)</c>，
        /// 所以把 <c>research_speed</c> 压到 <c>第7槽够做的次数 − 2</c>，
        /// 原版自己的 min 就会得出正确答案——<b>不用去碰它的循环</b>。
        /// 第 7 槽一份都不够时压成负数，原版取到 0 会自己停掉并返回 false。</item>
        /// <item><b>后缀扣料。</b> 实际做了几次，用一个<b>有需求的原版槽</b>的消耗量反推
        /// （原版每槽扣的是 <c>matrixPoints[i] × 次数</c>），再据此扣第 7 槽。
        /// 这样扣的是<b>真实发生的次数</b>，而不是我们以为会发生的次数。</item>
        /// </list>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LabComponent), nameof(LabComponent.InternalUpdateResearch))]
        private static void InternalUpdateResearch_Prefix(ref LabComponent __instance,
            ref float research_speed, out ResearchState __state)
        {
            __state = default(ResearchState);
            __state.Ref = -1;

            if (!Ready) return;

            int[] served = __instance.matrixServed;
            int[] points = LabComponent.matrixPoints;

            if (served == null || points == null) return;
            if (Slot >= served.Length || Slot >= points.Length) return;
            if (points[Slot] <= 0) return; // 当前科技不要这种矩阵

            // 记一个有需求的原版槽，后缀靠它反推实际做了几次
            for (var i = 0; i < Slot && i < points.Length && i < served.Length; i++)
                if (points[i] > 0)
                {
                    __state.Ref = i;
                    __state.Before = served[i];

                    break;
                }

            __state.Active = true;

            int can = served[Slot] / points[Slot];

            // <b>can 小于 2 时一律当作「停」，这不是保守，是边界必须。</b>
            // 原版上限是 (int)(research_speed + 2)，要让上限等于 can 就得把速度压到
            // [can−2, can−1)。can = 0 时压成 −2：上限取 0，原版在 IL 0037 处
            // 「上限为零就 replicating = false 并返回」——负数到不了后面算速率的地方，安全。
            // 但 can = 1 时速度必然是负的而上限是 1，不为零，**负速率会一路流到 IL 013D
            // 的 power × research_speed × … 里去**，把哈希数算成负的。
            // 所以 can < 2 直接走「停」那条路。代价是缺料到只剩一份时实验室干脆停下，
            // 而 matrixServed 是 ×3600 的量纲，can < 2 只会出现在几乎见底的那一刻。
            if (can < 2)
            {
                research_speed = -2f;

                return;
            }

            float cap = can - 2f;

            if (research_speed > cap) research_speed = cap;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(LabComponent), nameof(LabComponent.InternalUpdateResearch))]
        private static void InternalUpdateResearch_Postfix(ref LabComponent __instance, ResearchState __state)
        {
            if (!__state.Active || __state.Ref < 0) return;

            int[] served = __instance.matrixServed;
            int[] inc = __instance.matrixIncServed;
            int[] points = LabComponent.matrixPoints;

            if (served == null || points == null) return;
            if (Slot >= served.Length || __state.Ref >= served.Length) return;
            if (points[__state.Ref] <= 0) return;

            int used = (__state.Before - served[__state.Ref]) / points[__state.Ref];

            if (used <= 0) return;

            int take = points[Slot] * used;

            if (take > served[Slot]) take = served[Slot];

            // 扣数量的同时按比例扣增产剂点数，否则剩下的货会背上整堆的点数
            if (inc != null && Slot < inc.Length && served[Slot] > 0)
                inc[Slot] -= (int)((long)inc[Slot] * take / served[Slot]);

            served[Slot] -= take;
        }

        internal struct ResearchState
        {
            internal bool Active;
            internal int Ref;
            internal int Before;
        }

        /// <summary>
        /// 叠放的实验室之间往上传矩阵。原版同样展开了六次，每次判
        /// <c>next.needs[i] == matrixIds[i]</c> 且自己这槽 ≥ 7200，
        /// 然后按 3600 的整数倍搬、单次不超过 36000。这里照抄那套给第 7 槽。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LabComponent), nameof(LabComponent.UpdateOutputToNext))]
        private static void UpdateOutputToNext_Postfix(ref LabComponent __instance, LabComponent[] labPool)
        {
            if (!Ready) return;
            if (__instance.nextLabId <= 0 || labPool == null || __instance.nextLabId >= labPool.Length) return;

            int[] mine = __instance.matrixServed;
            int[] mineInc = __instance.matrixIncServed;
            LabComponent next = labPool[__instance.nextLabId];

            if (mine == null || mineInc == null) return;
            if (Slot >= mine.Length || Slot >= mineInc.Length) return;
            if (next.needs == null || Slot >= next.needs.Length) return;
            if (next.matrixServed == null || Slot >= next.matrixServed.Length) return;
            if (next.needs[Slot] != MatrixId) return;
            if (mine[Slot] < 7200) return;

            int move = (mine[Slot] - 7200) / 3600 * 3600;

            if (move > 36000) move = 36000;
            if (move <= 0) return;

            // 锁两边的数组，和原版同一套：这条路跑在 _lab_output_to_next_parallel 上
            lock (next.matrixServed)
            lock (mine)
            {
                if (mine[Slot] < move) move = mine[Slot];

                int incMove = mine[Slot] > 0 ? (int)((long)mineInc[Slot] * move / mine[Slot]) : 0;

                mine[Slot] -= move;
                mineInc[Slot] -= incMove;
                next.matrixServed[Slot] += move;

                if (next.matrixIncServed != null && Slot < next.matrixIncServed.Length)
                    next.matrixIncServed[Slot] += incMove;
            }
        }

        // ── 存档兼容：老实验室的数组还是 6 长 ────────────────

        /// <summary>
        /// <c>LabComponent.Import</c> 按存档里存的长度开数组，所以老档里的实验室是 6 长。
        /// <c>SetFunction</c> 是自愈的（长度不符就重开），但它只在玩家重新设定研究模式时才跑，
        /// 所以读档后要自己补一遍——和 <see cref="StationExpandPatches"/> 给存量物流站扩数组同理（陷阱一）。
        ///
        /// <b>只补长度，不动内容</b>：新槽是 0，等于「这台还没收到过生物矩阵」，正是想要的。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
        private static void GameData_Import(GameData __instance)
        {
            if (!Ready) return;

            PlanetFactory[] factories = __instance.factories;

            if (factories == null) return;

            int want = LabComponent.matrixIds.Length;
            var grown = 0;

            foreach (PlanetFactory factory in factories)
            {
                LabComponent[] pool = factory?.factorySystem?.labPool;

                if (pool == null) continue;

                for (var i = 1; i < factory.factorySystem.labCursor && i < pool.Length; i++)
                {
                    if (pool[i].id != i || !pool[i].researchMode) continue;

                    grown += Fit(ref pool[i].matrixServed, want);
                    grown += Fit(ref pool[i].matrixIncServed, want);
                    grown += Fit(ref pool[i].needs, want);
                }
            }

            if (grown > 0)
                ProjectEdenPlugin.Log.LogInfo($"生物矩阵：已把 {grown} 个存量实验室的矩阵数组扩到 {want} 格");
        }

        private static int Fit(ref int[] array, int want)
        {
            if (array == null || array.Length >= want) return 0;

            var next = new int[want];
            Array.Copy(array, next, array.Length);
            array = next;

            return 1;
        }
    }

    /// <summary>
    /// <c>PlanetFactory.InsertInto</c> 里矩阵槽的上界改成按表长判。
    ///
    /// <b>它必须自成一个补丁类。</b> Harmony 的补丁类只有两种模式二选一：
    /// 要么用 <c>TargetMethods()</c> 批量指定（类里所有补丁方法都打那批目标），
    /// 要么每个方法各自带 <c>[HarmonyPatch(...)]</c> 标注。
    /// 两者混在同一个类里会抛
    /// <c>You cannot combine TargetMethod, TargetMethods or PatchAll with individual annotations</c>，
    /// 而且和上一个错一样——<b>那是在 PatchAll 阶段抛的，整个 mod 加载失败</b>。
    /// 所以本类只装这一个批量补丁，<see cref="BioMatrixPatches"/> 那边留给按方法标注的那些。
    /// </summary>
    [HarmonyPatch]
    internal static class BioMatrixInsertPatches
    {
        /// <summary>供 IL 调用：矩阵槽的数量。</summary>
        internal static int MatrixSlotCount() => LabComponent.matrixIds?.Length ?? 6;

        /// <summary>
        /// 要改的是 <c>slot = itemId - 6001; if (slot &lt; 0 || slot &gt;= 6) 丢弃;</c> 里的那个 6。
        /// 转译器锚在它前面的 <c>ldc.i4 6001 ; sub</c> 上，所以匹配唯一——
        /// 方法里别处的 6 不会跟在这个减法后面。找不到就大声失败：
        /// 静默失效的话，生物矩阵会被实验室默默丢掉，而那是最难查的一种。
        ///
        /// <b>按名字取全部重载，不写参数类型。</b> 两个都要打：
        /// <c>InsertInto(Int32 entityId, …)</c> 与 <c>InsertInto(UInt32 ioTargetTypedId, …)</c>，
        /// 两个方法体里都有矩阵分支。
        ///
        /// <b>不能用 <c>[HarmonyPatch(typeof(T), nameof(...))]</c></b>：那对重载方法会在
        /// <c>PatchAll</c> 阶段抛 <c>AmbiguousMatchException</c>，而那一抛是<b>整个 mod 加载失败</b>，
        /// 不是这一个补丁失效。
        ///
        /// <b>也不能把参数类型写死</b>：本仓库的 preloader 会把这条链上的
        /// <c>byte itemCount / itemInc / out byte remainInc</c> 加宽成 <c>Int16</c>，
        /// 所以签名在运行时是变的——写死 <c>typeof(byte)</c> 在加宽生效时就找不到方法了。
        /// 按名字取则两种情况都对。
        /// </summary>
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (System.Reflection.MethodInfo m in typeof(PlanetFactory)
                         .GetMethods(AccessTools.all))
                if (m.Name == nameof(PlanetFactory.InsertInto))
                    yield return m;
        }

        // 批量模式下<b>方法上不能再挂 [HarmonyPatch]</b>，哪怕是不带参数的裸标注——
        // 目标由 TargetMethods() 统一给出，方法只需要说明自己是哪一类补丁
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> InsertInto_Transpiler(IEnumerable<CodeInstruction> instructions,
            System.Reflection.MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);

            // <b>先解析，为 null 就原样返回——绝不把 null 当操作数发出去。</b>
            // AccessTools.Method 解析不到时返回 null，而 null 操作数会在 Harmony 写回 IL 时
            // 抛 ArgumentNullException: Invalid argument for call NULL，
            // 报错点在 ILManipulator.WriteTo，<b>离真正写错的那一行很远</b>。
            // 这次就是拆类时把辅助方法一起搬走了，引用还写着旧类名——
            // 和仓库里记着的「CodeMatch(OpCodes.Call, null) 匹配到所有 call」是同一族：
            // **反射按名字找东西，找不到时不会喊，只会给你一个 null。**
            System.Reflection.MethodInfo bound =
                AccessTools.Method(typeof(BioMatrixInsertPatches), nameof(MatrixSlotCount));

            if (bound == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"PlanetFactory.InsertInto：解析不到 {nameof(MatrixSlotCount)}，转译放弃（不改总比发出 call null 好）");

                return code;
            }

            var hits = 0;

            for (var i = 0; i + 3 < code.Count; i++)
            {
                if (!code[i].LoadsConstant(6001)) continue;
                if (code[i + 1].opcode != OpCodes.Sub) continue;

                // 减法之后不远处那个和 6 比较的上界
                for (int j = i + 2; j < code.Count && j < i + 12; j++)
                {
                    if (!code[j].LoadsConstant(6)) continue;

                    code[j].opcode = OpCodes.Call;
                    code[j].operand = bound;
                    hits++;

                    break;
                }
            }

            string who = original.GetParameters().Length > 0
                ? original.GetParameters()[0].ParameterType.Name
                : "?";

            if (hits == 0)
                ProjectEdenPlugin.Log.LogError(
                    $"PlanetFactory.InsertInto({who} …)：没找到矩阵槽的上界"
                    + "（ldc.i4 6001 ; sub … ldc.i4.6），第七种矩阵将无法投进实验室，而且不会有任何报错");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"PlanetFactory.InsertInto({who} …)：矩阵槽上界已改为按表长判，{hits} 处");

            return code;
        }

    }

}
