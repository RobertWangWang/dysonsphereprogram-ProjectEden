using System.Text;
using HarmonyLib;
using UnityEngine.UI;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让品质在<b>物品提示栏</b>里看得见：鼠标悬停到一格货上，属性表末尾多出一行
    /// 「品质　顶尖（82）」。
    ///
    /// <b>为什么是提示栏，而不是属性行。</b> 设计稿第八节第 1 条原本写的是走
    /// <c>ItemProto.GetPropName/GetPropValue</c>，和可燃液体那条「工作温度」同一个做法——
    /// <b>那条是错的，这里订正它</b>：属性行是逐<b>原型</b>的，而品质是<b>容器</b>的属性。
    /// 同一个 <c>ItemProto</c> 在不同格子里品质不同，做成属性行的话，
    /// 全图每一堆铜块都会显示同一个数，而那个数不属于任何一格具体的货。
    ///
    /// 提示栏则天然握着那个上下文：
    /// <c>UIItemTip.SetTip(itemId, corner, offset, parent, <b>itemCount</b>, <b>incCount</b>, …)</c>
    /// —— 件数和整堆增产点数都是传进来的，也就是说调用方手里正是一格具体的货，
    /// 原版自己也在这里画增产剂那一块。品质和增产点数是同构的量，放同一个地方最省解释。
    ///
    /// <b>品质那个数是查出来的，不是猜的。</b> <c>SetTip</c> 的签名里没有品质
    /// （那是 preloader 新加的孪生字段，原版签名里没有它的位置），所以这里反过来问鼠标：
    /// <c>VFInput.mouseInStorage</c> 指着鼠标当前所在的储物格控件，
    /// <c>mouseOnX / mouseOnY</c> 是它里面的第几格。这一条覆盖<b>储物箱和机甲背包</b>——
    /// 两者用的是同一个 <c>UIStorageGrid</c>，一处都不用额外接线。
    ///
    /// <b>查出来的那一格必须和提示栏说的是同一样东西</b>（<c>itemId</c> 相等）。
    /// 没有这道核对，物品选取窗口、配方面板那些同样会弹提示栏的地方
    /// 会挂上鼠标底下某个储物格的品质——数字看着很合理，只是属于别的货，
    /// 而那种错比不显示难查得多。
    ///
    /// <b>追加不会失控</b>，虽然 <c>SetTip</c> 每帧都被调一次：它在 IL 0497 / 04AB 处
    /// 先把两列属性文本整个重写一遍，所以我们每次拿到的都是干净的原版文本。
    /// （这一条必须验，不能假设——每帧往同一个 <c>Text</c> 后面接一行，
    /// 一分钟就能把提示栏撑满整屏。）
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
        /// 「顶尖（82）」。三档只是<b>显示分层</b>，内部一直是连续分数——
        /// 效果层将来按分数线性插值，不按档跳。
        /// </summary>
        private static string Describe(int perItem)
        {
            string band = perItem >= TopFrom ? "顶尖".Translate()
                : perItem >= GoodFrom ? "优秀".Translate()
                : "普通".Translate();

            return string.Format("{0}（{1}）".Translate(), band, perItem);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIItemTip), nameof(UIItemTip.SetTip))]
        private static void UIItemTip_SetTip(UIItemTip __instance, int itemId)
        {
            if (__instance == null || itemId <= 0) return;

            int perItem = FromHoveredStorage(itemId);

            // **0 分不画。** 没提纯过的货本来就是 0 分，给每一格货都加一行
            // 「品质 普通（0）」只是噪声——0 分和「查不到」在显示上应当一样。
            if (perItem <= 0) return;

            Text props = __instance.propsText;
            Text values = __instance.valuesText;

            if (props == null || values == null) return;

            // 追加到原版那两列（左属性名、右值）的末尾。**追加而不是替换**：
            // 这两列上面还写着堆叠上限、燃料热值这些原版内容，覆盖掉就是砸别人的窗口。
            props.text = Append(props.text, "品质".Translate());
            values.text = Append(values.text, Describe(perItem));
        }

        /// <summary>
        /// 鼠标底下那一格储物格的单件品质。不在储物格上、格子空着、
        /// 或者那一格装的不是提示栏正在说的东西，都返回 0。
        /// </summary>
        private static int FromHoveredStorage(int itemId)
        {
            if (!QualityAccess.GridReady) return 0;

            UIStorageGrid ui = VFInput.mouseInStorage;

            if (ui == null || ui.storage?.grids == null) return 0;
            if (ui.mouseOnX < 0 || ui.mouseOnY < 0 || ui.colCount <= 0) return 0;

            int index = ui.mouseOnY * ui.colCount + ui.mouseOnX;

            if (index < 0 || index >= ui.storage.grids.Length) return 0;

            // **同一样东西才算数。** 别的窗口（物品选取、配方面板）也会弹提示栏，
            // 而那时鼠标可能正好压在背包上——没有这一句，它们会挂上背包那一格的品质。
            if (ui.storage.grids[index].itemId != itemId) return 0;

            int count = ui.storage.grids[index].count;

            if (count <= 0) return 0;

            return QualityAccess.GetGridQua(ref ui.storage.grids[index]) / count;
        }

        private static string Append(string s, string line)
        {
            if (string.IsNullOrEmpty(s)) return line;

            var sb = new StringBuilder(s.Length + line.Length + 1);

            sb.Append(s);

            if (!s.EndsWith("\n")) sb.Append('\n');

            sb.Append(line);

            return sb.ToString();
        }
    }
}
