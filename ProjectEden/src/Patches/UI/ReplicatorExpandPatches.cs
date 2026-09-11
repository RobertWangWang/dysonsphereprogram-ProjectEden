using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 给合成器（UIReplicatorWindow）加横向翻页：底部一根滚动条，左右拉动换一页列。
    ///
    /// <b>为什么需要</b>：原版每个分页是 8 行 × 14 列，第 1 页（物品）几乎是满的。
    /// 本 mod 的新矿石只抢到一个空格，锭被挤到第 9 行——而 RefreshRecipeIcons 里
    /// <c>if (row >= 8) continue;</c>，第 9 行<b>根本不会被画出来</b>。往列上扩才有意义。
    ///
    /// <b>做法只有一处注入。</b> 原版的格位映射是：
    /// <code>
    ///   page = GridIndex / 1000;
    ///   row  = (GridIndex - page * 1000) / 100 - 1;
    ///   col  = GridIndex % 100 - 1;
    ///   if (row &lt; 0 || col &lt; 0 || row &gt;= 8 || col &gt;= 14) continue;
    ///   index = row * 14 + col;
    /// </code>
    /// 只要在 col 算完之后减去列偏移，<b>越界的会被原版那两行判断自然裁掉</b>，
    /// 不用动数组、缓冲区或着色器。而命中测试（TestMouseRecipeIndex）拿的是
    /// 鼠标在网格里的<b>可见坐标</b>，和写入用的是同一套坐标系，所以一个字都不用改。
    ///
    /// 滚动条是独立控件，不需要 Mask / Viewport / Content 那套层级，
    /// 和物流站面板那根是同一个套路（见 StationExpandPatches.EnsureScrollbar）。
    /// </summary>
    [HarmonyPatch]
    internal static class ReplicatorExpandPatches
    {
        /// <summary>原版一页的列数，格位映射里写死的那个 14。</summary>
        private const int ColsPerPage = 14;

        /// <summary>滚动条高度与它离网格底边的间距（像素）。</summary>
        private const float BarHeight = 12f;

        private const float BarGap = 6f;

        /// <summary>原版一页的行数上限，RefreshRecipeIcons 里的 row &gt;= 8。</summary>
        private const int Rows = 8;

        private static int _page;
        private static int _maxPage;

        /// <summary>正在执行「跳到某条配方」。切标签的后置会清列页，跳转时得让它闭嘴。</summary>
        private static bool _jumping;
        private static Scrollbar _bar;
        private static bool _syncing;

        private static MegaBuildingsConfig Config => MegaBuildingRegistry.Config;

        /// <summary>总共几页；1 表示维持原版、不加滚动条。</summary>
        private static int Pages
        {
            get
            {
                int pages = Config?.replicatorPages ?? 0;

                return pages < 1 ? 1 : pages > 8 ? 8 : pages;
            }
        }

        /// <summary>当前列偏移。RefreshRecipeIcons 注入的那次减法用的就是它。</summary>
        public static int ColOffset() => _page * ColsPerPage;

        // ── 一、格位映射加上列偏移 ────────────────────────────

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIReplicatorWindow), "RefreshRecipeIcons")]
        private static IEnumerable<CodeInstruction> RefreshRecipeIcons_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo offset = AccessTools.Method(typeof(ReplicatorExpandPatches), nameof(ColOffset));

            var code = new List<CodeInstruction>(instructions);

            if (offset == null)
            {
                ProjectEdenPlugin.Log.LogError("合成器横向翻页：ColOffset 没解析出来，补丁未生效");

                return code;
            }

            var count = 0;

            // 找 col 的那三条：ldc.i4.s 100 / rem / ldc.i4.1 / sub
            // 行用的是 div，不是 rem，所以这个特征只会命中列
            for (var i = 0; i < code.Count - 3; i++)
            {
                if (!code[i].LoadsConstant(100)) continue;
                if (code[i + 1].opcode != OpCodes.Rem) continue;
                if (!code[i + 2].LoadsConstant(1)) continue;
                if (code[i + 3].opcode != OpCodes.Sub) continue;

                // 在 -1 之后再减一次偏移；后面原版的 col < 0 / col >= 14 负责裁剪
                code.InsertRange(i + 4, new[]
                {
                    new CodeInstruction(OpCodes.Call, offset),
                    new CodeInstruction(OpCodes.Sub),
                });

                count++;
                i += 5;
            }

            if (count == 0)
                ProjectEdenPlugin.Log.LogError("合成器横向翻页：没找到列号的计算（GridIndex % 100 - 1），补丁未生效");
            else
                ProjectEdenPlugin.Log.LogInfo($"合成器横向翻页：列号计算已接管 {count} 处");

            return code;
        }

        // ── 二、底部滚动条 ────────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIReplicatorWindow), "_OnUpdate")]
        private static void UIReplicatorWindow_OnUpdate(UIReplicatorWindow __instance)
        {
            if (Pages <= 1) return;

            _maxPage = Pages - 1;

            EnsureScrollbar(__instance);
            SyncScrollbar();
        }

        /// <summary>切分页时回到第一页，否则换到「建筑」页会发现停在一个空页上。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIReplicatorWindow), "OnTypeButtonClick")]
        private static void UIReplicatorWindow_OnTypeButtonClick(UIReplicatorWindow __instance)
        {
            // 「跳到某条配方」会顺带切标签，那时列页是刚算好的，不能清掉
            if (_page == 0 || _jumping) return;

            _page = 0;

            __instance.RefreshRecipeIcons();
        }

        // ── 三、双击建造栏跳到那条配方 ────────────────────────

        /// <summary>
        /// 接管 <c>SetSelectedRecipe</c> 里它处理不了的那部分。
        ///
        /// 原版的判定是：
        /// <code>
        ///   bool ok = (page == 1 || page == 2);
        ///   if (row &lt; 0 || col &lt; 0 || row &gt;= 8 || col &gt;= 14) ok = false;
        ///   if (!ok) { SetSelectedRecipeIndex(-1, …); return; }   // 连标签页都不切
        /// </code>
        /// 两道坎本 mod 的配方都会踩：<b>分页号写死成 1 或 2</b>，而巨型建筑在第 3 页
        /// （CommonAPI 加的那个标签）；<b>第 15 列往后一律不认</b>，而合成面板第 1、2 页
        /// 早就排满，新配方只能落在扩展列里——那些列要靠横向翻页才看得见，原版不知道有这回事。
        ///
        /// 症状就是在建造栏双击这些建筑，合成面板要么不开、要么开了什么也没选中。
        ///
        /// 这里只在原版<b>处理不了</b>时接管：算出列页、切标签、按可见坐标选中。
        /// 原版本来就对的情况（前 14 列、第 1/2 页）原样放行。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIReplicatorWindow), nameof(UIReplicatorWindow.SetSelectedRecipe))]
        private static bool UIReplicatorWindow_SetSelectedRecipe(UIReplicatorWindow __instance,
            RecipeProto recipe, bool notify)
        {
            // 形参名必须和原版逐字一致（recipe / notify），Harmony 是按名字注入的
            if (recipe == null) return true;

            // 没解锁的交回原版，它自己会 return
            if (!__instance.isInstantItem && !GameMain.history.RecipeUnlocked(recipe.ID)) return true;

            int page = recipe.GridIndex / 1000;
            int row = recipe.GridIndex % 1000 / 100 - 1;
            int col = recipe.GridIndex % 100 - 1;

            if (row < 0 || col < 0 || row >= Rows) return true;

            int colPage = col / ColsPerPage;

            // 原版能正确处理的情况原样放行
            if (colPage == 0 && (page == 1 || page == 2)) return true;

            if (colPage > Pages - 1) return true;

            _jumping = true;

            try
            {
                _page = colPage;

                // 切标签内部会 RefreshRecipeIcons，列偏移已经就位
                __instance.OnTypeButtonClick(page);
                __instance.SetSelectedRecipeIndex(row * ColsPerPage + col % ColsPerPage, notify);
            }
            finally
            {
                _jumping = false;
            }

            return false;
        }

        private static void EnsureScrollbar(UIReplicatorWindow window)
        {
            if (_bar != null) return;

            Image bg = window.recipeBg;

            if (bg == null) return;

            RectTransform gridTrs = bg.rectTransform;

            if (gridTrs.rect.width <= 0f) return;

            var barGO = new GameObject("projecteden-recipe-hscrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));

            // <b>挂在网格自身之下，用拉伸锚点定位。</b>
            // 上一版是拿 recipeBg 的 anchoredPosition / rect 去推位置，
            // 但这两个值的含义取决于它自己的锚点配置——按「左上角锚点」假设算出来的结果
            // 是一个横跨到窗口外的巨大方块。锚到父级底边就与父级怎么设锚点无关了。
            barGO.transform.SetParent(gridTrs, false);

            var barTrs = (RectTransform)barGO.transform;

            // 横向铺满网格，纵向吊在网格底边之下
            barTrs.anchorMin = new Vector2(0f, 0f);
            barTrs.anchorMax = new Vector2(1f, 0f);
            barTrs.pivot = new Vector2(0.5f, 1f);
            barTrs.offsetMin = new Vector2(0f, 0f);
            barTrs.offsetMax = new Vector2(0f, 0f);
            barTrs.sizeDelta = new Vector2(0f, BarHeight);
            barTrs.anchoredPosition = new Vector2(0f, -BarGap);

            barGO.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.08f);

            var areaGO = new GameObject("Sliding Area", typeof(RectTransform));

            areaGO.transform.SetParent(barTrs, false);

            var areaTrs = (RectTransform)areaGO.transform;

            areaTrs.anchorMin = Vector2.zero;
            areaTrs.anchorMax = Vector2.one;
            areaTrs.offsetMin = Vector2.zero;
            areaTrs.offsetMax = Vector2.zero;

            var handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));

            handleGO.transform.SetParent(areaTrs, false);

            var handleTrs = (RectTransform)handleGO.transform;

            handleTrs.offsetMin = Vector2.zero;
            handleTrs.offsetMax = Vector2.zero;

            var handleImage = handleGO.GetComponent<Image>();

            handleImage.color = new Color(0.62f, 0.84f, 1f, 0.75f);

            _bar = barGO.GetComponent<Scrollbar>();
            _bar.direction = Scrollbar.Direction.LeftToRight;
            _bar.handleRect = handleTrs;
            _bar.targetGraphic = handleImage;
            _bar.onValueChanged.AddListener(OnScrollbarValueChanged);

            ProjectEdenPlugin.Log.LogInfo($"合成器已加入横向翻页滚动条：共 {Pages} 页，每页 {ColsPerPage} 列");
        }

        private static void SyncScrollbar()
        {
            if (_bar == null || _maxPage <= 0) return;

            _syncing = true;

            _bar.numberOfSteps = _maxPage + 1;
            _bar.size = 1f / (_maxPage + 1);
            _bar.value = (float)_page / _maxPage;

            _syncing = false;
        }

        private static void OnScrollbarValueChanged(float value)
        {
            if (_syncing || _maxPage <= 0) return;

            int page = Mathf.Clamp(Mathf.RoundToInt(value * _maxPage), 0, _maxPage);

            if (page == _page) return;

            _page = page;

            // 图标是一次性刷进 ComputeBuffer 的，改了偏移必须重刷才会变
            UIRoot.instance?.uiGame?.replicator?.RefreshRecipeIcons();
        }
    }
}
