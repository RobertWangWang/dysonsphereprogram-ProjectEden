using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让品质在<b>物品提示栏</b>里看得见：鼠标悬停到一格货上，属性表里多出一行
    /// 「品质　顶尖（82）」。
    ///
    /// <b>为什么是提示栏，而不是属性行。</b> 设计稿第八节第 1 条原本写的是走
    /// <c>ItemProto.GetPropName/GetPropValue</c>，和可燃液体那条「工作温度」同一个做法——
    /// <b>那条是错的，这里订正它</b>：属性行是逐<b>原型</b>的，而品质是<b>容器</b>的属性。
    /// 同一个 <c>ItemProto</c> 在不同格子里品质不同，做成属性行的话，
    /// 全图每一堆铜块都会显示同一个数，而那个数不属于任何一格具体的货。
    ///
    /// <b>品质那个数是查出来的，不是靠调用方喂。</b> <c>SetTip</c> 的签名里没有品质
    /// （那是 preloader 新加的孪生字段，原版签名里没有它的位置），所以这里反过来问鼠标：
    /// <c>VFInput.mouseInStorage</c> 指着鼠标当前所在的储物格控件，
    /// <c>mouseOnX / mouseOnY</c> 是它里面的第几格。这一条覆盖<b>储物箱和机甲背包</b>——
    /// 两者用的是同一个 <c>UIStorageGrid</c>，一处都不用额外接线。
    ///
    /// <b>查出来的那一格必须和提示栏说的是同一样东西</b>（<c>itemId</c> 相等）。
    /// 没有这道核对，物品选取窗口、配方面板那些同样会弹提示栏的地方
    /// 会挂上鼠标底下某个储物格的品质——数字看着很合理，只是属于别的货。
    ///
    /// <b>为什么是 transpiler 而不是后缀。</b> 第一版是后缀，直接往两列 <c>Text</c> 后面接一行,
    /// 结果<b>那一行压在下面那条分隔线上</b>——因为 <c>SetTip</c> 一路数着自己写了几行
    /// （局部 <c>V_11</c>），最后拿这个数去排下半截和整个窗口的高度。后缀在那之后跑，
    /// 排版早就按少一行算完了。
    ///
    /// 所以改成把这一行<b>插进原版自己的记账里</b>：在
    /// <c>propsText.text = …</c> 那两句之前改写两列字符串、并把行数 +1，
    /// 之后的高度计算由原版自己完成。<b>不要重算版面，去修改版面的输入。</b>
    ///
    /// <b>还没覆盖到的地方</b>：传送带窗口、装配机的进料/产物口、研究站、分馏塔。
    /// 那几个窗口自己画数量和增产箭头、不走这条提示栏，各需要一个类似
    /// <see cref="QualityPanelPatches"/> 的常驻标签。物流站槽位已经有了。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityTipPatches
    {
        /// <summary>三档的下界，取自设计稿 6.4 节：普通 0–33 / 优秀 34–66 / 顶尖 67–100。</summary>
        private const int GoodFrom = 34;

        private const int TopFrom = 67;

        /// <summary>
        /// 由 transpiler 在 <c>propsText.text = …</c> 之前调用：
        /// 往两列末尾各补一行，并把原版的行计数 +1。
        ///
        /// <b>每行以 <c>\n</c> 结尾</b>，这是原版自己的写法（见它拼「不能手动制造」那一行）。
        /// </summary>
        internal static void AddRow(ref string props, ref string values, ref int rows, int itemId)
        {
            if (itemId <= 0) return;

            int perItem = FromHoveredStorage(itemId, out int total);

            // **这里原先是 `if (perItem <= 0) return;`**，理由写的是「0 分和查不到
            // 在显示上应当一样」。那条理由把两件不同的事混在了一起，玩家也正是这么报的
            // （「没有提纯的正常材料 10 分，这个也没有显示」）：
            //
            // <list type="bullet">
            // <item><b>查不到</b>（鼠标不在储物格上、格子空着）——不该画，现在仍然不画：
            // <see cref="FromHoveredStorage"/> 那几条路是先 return 掉的，走不到这里；</item>
            // <item><b>查到了，是 0 分</b>——这是「没提纯过的普通货」，它有分数，
            // 就是底线分。该画。</item>
            // </list>
            //
            // <b>普通材料就是 0 分，不顶底线。</b>（所有者定的）曾经试过「读取时顶到 10」，
            // 而那会<b>把求和弄成假的</b>：品质存的是一格货的总分，普通货存 0 却显示 10，
            // 一旦和提纯货混进同一格，总分里那 10 分压根不存在——100×0 + 100×50 摊下来是 25，
            // 而按“每件都有分”的直觉应该是 30。读取时顶的底线和求和模型是互斥的。
            if (perItem == NotFound) return;

            props = (props ?? "") + "品质".Translate() + "\n";
            values = (values ?? "") + Describe(perItem, total) + "\n";

            rows++;
        }

        /// <summary>
        /// 「顶尖（82）　总 4100」。三档只是<b>显示分层</b>，内部一直是连续分数——
        /// 效果层将来按分数线性插值，不按档跳。
        ///
        /// <b>两个数各有各的用处，所以两个都写。</b> 括号里是<b>每件</b>分，它是
        /// 可比较的那个量——「这堆料比那堆好吗」只有它答得了，总分随件数变，答不了。
        /// 后面那个是<b>整格总分</b>，也就是真正存在字段里的数：品质是可加量，
        /// 合并就是两堆总分相加，对账要用它。所有者点名要这一个。
        ///
        /// <b>总分按原样打印，不跟着每件分一起夹。</b> 夹的是显示上的每件分
        /// （见 <see cref="FromHoveredStorage"/>），而总分是字段里的实数——
        /// 两者在上限巡检的两次之间可能对不上，那时候**让它对不上正好是有用的信息**：
        /// 「顶尖（100）　总 11140」一眼就能看出这一格越界了，等着被压回去。
        /// 拿夹过的每件分乘件数倒推总分会把这个信号抹掉。
        /// </summary>
        private static string Describe(int perItem, int total)
        {
            string band = perItem >= TopFrom ? "顶尖".Translate()
                : perItem >= GoodFrom ? "优秀".Translate()
                : "普通".Translate();

            return string.Format("{0}（{1}）　总 {2}".Translate(), band, perItem, total);
        }

        /// <summary>查不到。<b>与「查到了，是 0 分」严格区分</b>——
        /// 前者不该画行，后者该画（普通货有底线分）。
        /// 两者当初都返回 0，于是「0 分不画」连带把普通货也吞了。</summary>
        private const int NotFound = -1;

        /// <summary>
        /// 鼠标底下那一格储物格的单件品质。不在储物格上、格子空着、
        /// 或者那一格装的不是提示栏正在说的东西，都返回 <see cref="NotFound"/>。
        /// 真的查到了就返回分数，<b>包括 0</b>。
        ///
        /// <paramref name="total"/> 是<b>整格总分的原始值</b>——查不到时为 0，
        /// 查到了就照字段原样给出，不夹上限（理由见 <see cref="Describe"/>）。
        /// </summary>
        private static int FromHoveredStorage(int itemId, out int total)
        {
            total = 0;

            if (!QualityAccess.GridReady) return NotFound;

            UIStorageGrid ui = VFInput.mouseInStorage;

            if (ui == null || ui.storage?.grids == null) return NotFound;
            if (ui.mouseOnX < 0 || ui.mouseOnY < 0 || ui.colCount <= 0) return NotFound;

            int index = ui.mouseOnY * ui.colCount + ui.mouseOnX;

            if (index < 0 || index >= ui.storage.grids.Length) return NotFound;

            // **同一样东西才算数。** 别的窗口（物品选取、配方面板）也会弹提示栏，
            // 而那时鼠标可能正好压在背包上——没有这一句，它们会挂上背包那一格的品质。
            if (ui.storage.grids[index].itemId != itemId) return NotFound;

            int count = ui.storage.grids[index].count;

            if (count <= 0) return NotFound;

            total = QualityAccess.GetGridQua(ref ui.storage.grids[index]);

            int per = total / count;

            // **显示也压在上限之内。**
            //
            // 上限的执行在 QualityRepairPatches 那边，每 30 秒压一次——两次之间
            // 完全可能读到一个越了界的瞬时值。让玩家看到「品质 顶尖（11140）」，
            // 只会把一个本来在量纲内的设计（0~100）显示成一个看不懂的数。
            //
            // 这里**只压显示，不改数据**：改数据是那边的职责，显示层能写数据
            // 就多了一条谁都想不到的旁路。
            return per > QualityRefineryPatches.MaxPerItem
                ? QualityRefineryPatches.MaxPerItem
                : per;
        }


        // ── transpiler：把这一行插进原版自己的行计数里 ────────────

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIItemTip), nameof(UIItemTip.SetTip))]
        private static IEnumerable<CodeInstruction> SetTip_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            FieldInfo propsField = AccessTools.Field(typeof(UIItemTip), "propsText");
            FieldInfo valuesField = AccessTools.Field(typeof(UIItemTip), "valuesText");
            MethodInfo setText = AccessTools.PropertySetter(typeof(UnityEngine.UI.Text), "text");
            MethodInfo add = AccessTools.Method(typeof(QualityTipPatches), nameof(AddRow));

            if (propsField == null || valuesField == null || setText == null || add == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "物品品质：提示栏的品质行没接上——propsText / valuesText / set_text / AddRow " +
                    "有一个没解析出来。**宁可不插也不插 null 操作数**（那会在 Harmony 写回时才炸，" +
                    "堆栈指向的是写入器而不是这里）。");

                return code;
            }

            // 锚点：`ldarg.0 ; ldfld propsText ; ldloc V_12 ; callvirt set_text`，
            // 紧跟着 `ldarg.0 ; ldfld valuesText ; ldloc V_13 ; callvirt set_text`。
            var at = -1;

            for (var i = 0; i + 7 < code.Count; i++)
            {
                if (!code[i].LoadsField(propsField)) continue;
                if (!code[i + 1].IsLdloc() || !code[i + 2].Calls(setText)) continue;
                if (!code[i + 4].LoadsField(valuesField)) continue;
                if (!code[i + 5].IsLdloc() || !code[i + 6].Calls(setText)) continue;

                at = i;

                break;
            }

            if (at < 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    "物品品质：在 UIItemTip.SetTip 里没找到写两列属性文本的那一段，提示栏不显示品质。" +
                    "游戏更新动过这个方法的话，重新对一遍锚点。");

                return code;
            }

            LocalBuilder props = code[at + 1].operand as LocalBuilder;
            LocalBuilder values = code[at + 5].operand as LocalBuilder;
            LocalBuilder rows = FindRowCounter(code, at);

            if (props == null || values == null || rows == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "物品品质：提示栏的两列局部或行计数局部没认出来，不插品质行——" +
                    $"props={props != null}／values={values != null}／rows={rows != null}。");

                return code;
            }

            // 插在 `ldarg.0 ; ldfld propsText` 之前的那个 ldarg.0 上
            int insert = at > 0 && code[at - 1].opcode == OpCodes.Ldarg_0 ? at - 1 : at;

            var emit = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldloca_S, props),
                new CodeInstruction(OpCodes.Ldloca_S, values),
                new CodeInstruction(OpCodes.Ldloca_S, rows),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, add)
            };

            // **标签要跟着搬。** 插入点上挂的跳转标签必须落到新的第一条指令上，
            // 否则那些分支会跳过我们这一段——本仓库在 IL 改写上反复记过这一条。
            emit[0].labels.AddRange(code[insert].labels);
            code[insert].labels.Clear();

            code.InsertRange(insert, emit);

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质：提示栏的品质行已接进 UIItemTip.SetTip（第 {insert} 条指令前，" +
                $"两列局部 V_{props.LocalIndex} / V_{values.LocalIndex}，行计数 V_{rows.LocalIndex}）。");

            return code;
        }

        /// <summary>
        /// 找原版数行数用的那个局部。
        ///
        /// 它的形状是 <c>ldloc X ; ldc.i4.1 ; add ; stloc X</c>——但<b>光靠形状不够</b>：
        /// 锚点之前随便一个 <c>for</c> 循环的计数器长得一模一样，而认错了的后果是
        /// 我们去改一个循环变量，那是死循环或越界，不是排版错位。两道判据一起用：
        ///
        /// <list type="number">
        /// <item><b>数得最多的那个</b>，且至少 4 次——原版每写一行属性就 +1 一次，
        ///       实测在写两列文本之前有 5 次；循环计数器凑够 4 次要有四个用同一个局部的循环。</item>
        /// <item><b>锚点之后还被读</b>——行计数的用处正是排下半截的版面（实测 IL 0888 / 0971），
        ///       而锚点之前的循环计数器出了循环就没人再看它了。这一条才是真正的判据。</item>
        /// </list>
        /// </summary>
        private static LocalBuilder FindRowCounter(List<CodeInstruction> code, int anchor)
        {
            var hits = new Dictionary<LocalBuilder, int>();

            for (var i = 0; i + 3 < anchor; i++)
            {
                if (!code[i].IsLdloc() || !(code[i].operand is LocalBuilder v)) continue;
                if (v.LocalType != typeof(int)) continue;
                if (code[i + 1].opcode != OpCodes.Ldc_I4_1 || code[i + 2].opcode != OpCodes.Add) continue;
                if (!code[i + 3].IsStloc() || !ReferenceEquals(code[i + 3].operand, v)) continue;

                hits.TryGetValue(v, out int n);

                hits[v] = n + 1;
            }

            LocalBuilder best = null;
            var bestHits = 0;

            foreach (KeyValuePair<LocalBuilder, int> pair in hits)
            {
                if (pair.Value < 4 || pair.Value <= bestHits) continue;
                if (!ReadAfter(code, anchor, pair.Key)) continue;

                best = pair.Key;
                bestHits = pair.Value;
            }

            return best;
        }

        private static bool ReadAfter(List<CodeInstruction> code, int anchor, LocalBuilder v)
        {
            for (int i = anchor; i < code.Count; i++)
                if (code[i].IsLdloc() && ReferenceEquals(code[i].operand, v))
                    return true;

            return false;
        }
    }
}
