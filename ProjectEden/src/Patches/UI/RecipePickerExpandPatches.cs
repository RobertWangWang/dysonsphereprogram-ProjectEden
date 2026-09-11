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
    /// 给「配方选取」窗口（UIRecipePicker）加横向翻页，并在打开时自动跳到第一条可用配方。
    ///
    /// <b>为什么必须做这个。</b> 合成面板第 1 页原本就几乎排满（8 行 × 14 列只剩一格），
    /// 所以 ProtoSlots.ResolveGridIndex 把本 mod 的新配方排到了第 15 列以后。
    /// 合成器那边有 ReplicatorExpandPatches 撑着看得见，但配方选取窗口里
    /// <c>RefreshIcons</c> 有一句硬裁剪：
    /// <code>
    ///   if (row &lt; 0 || col &lt; 0 || row &gt;= 8 || col &gt;= 14) continue;
    ///   if (page != currentType) continue;
    /// </code>
    /// <b>第 15 列往后一条都画不出来</b>——症状就是新机器打开配方选取是一片空白，
    /// 而且换哪个标签都一样，很容易误判成「配方没和机器挂上钩」。
    ///
    /// 注入点和合成器那边完全相同：在 <c>col = GridIndex % 100 - 1</c> 之后再减一次列偏移，
    /// 越界的由原版那两行判断自然裁掉。命中测试（TestMouseIndex）拿的是鼠标在网格里的
    /// <b>可见坐标</b>（position → index → protoArray[index]），和写入用的是同一套坐标系，
    /// 所以一个字都不用改。
    ///
    /// <b>另外补一条自动跳转。</b> currentType（标签页）是跨窗口保留的，_OnOpen 只在它为 0
    /// 时才置 1。所以给一台只认自定义配方类型的机器开窗口时，很可能停在上次那个标签上、
    /// 一条配方都没有。带筛选打开且当前一条都看不到时，直接跳到第一条匹配配方所在的
    /// 标签 + 列页。
    /// </summary>
    [HarmonyPatch]
    internal static class RecipePickerExpandPatches
    {
        /// <summary>原版一页的列数，格位映射与命中测试里写死的那个 14。</summary>
        private const int ColsPerPage = 14;

        /// <summary>原版的行数上限，RefreshIcons 里的 row >= 8。</summary>
        private const int Rows = 8;

        private const float BarHeight = 12f;

        private const float BarGap = 6f;

        private static int _page;
        private static int _maxPage;
        private static Scrollbar _bar;
        private static bool _syncing;

        /// <summary>正在执行「打开时自动跳转」。切标签的后置会清列页，跳转时得让它闭嘴。</summary>
        private static bool _jumping;

        private static MegaBuildingsConfig Config => MegaBuildingRegistry.Config;

        /// <summary>总共几列页；和合成器共用一个配置项，两边的列扩展本来就是同一件事。</summary>
        private static int Pages
        {
            get
            {
                int pages = Config?.replicatorPages ?? 0;

                return pages < 1 ? 1 : pages > 8 ? 8 : pages;
            }
        }

        /// <summary>当前列偏移。RefreshIcons 注入的那次减法用的就是它。</summary>
        public static int ColOffset() => _page * ColsPerPage;

        // ── 一、格位映射加上列偏移 ────────────────────────────

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIRecipePicker), "RefreshIcons")]
        private static IEnumerable<CodeInstruction> RefreshIcons_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo offset = AccessTools.Method(typeof(RecipePickerExpandPatches), nameof(ColOffset));

            var code = new List<CodeInstruction>(instructions);

            if (offset == null)
            {
                ProjectEdenPlugin.Log.LogError("配方选取横向翻页：ColOffset 没解析出来，补丁未生效");

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

                code.InsertRange(i + 4, new[]
                {
                    new CodeInstruction(OpCodes.Call, offset),
                    new CodeInstruction(OpCodes.Sub),
                });

                count++;
                i += 5;
            }

            if (count == 0)
                ProjectEdenPlugin.Log.LogError("配方选取横向翻页：没找到列号的计算（GridIndex % 100 - 1），补丁未生效");
            else
                ProjectEdenPlugin.Log.LogInfo($"配方选取横向翻页：列号计算已接管 {count} 处");

            return code;
        }

        // ── 二、打开时跳到第一条能用的配方 ────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIRecipePicker), "_OnOpen")]
        private static void UIRecipePicker_OnOpen(UIRecipePicker __instance)
        {
            // 没有筛选就是玩家自己翻的，别抢他的位置
            if (__instance.filter == ERecipeType.None) return;

            if (HasVisible(__instance)) return;

            if (!FindFirst(__instance.filter, out int page, out int colPage)) return;

            _page = colPage;
            _jumping = true;

            try
            {
                // OnTypeButtonClick 内部会 RefreshIcons，列偏移已经就位
                __instance.OnTypeButtonClick(page);
            }
            finally
            {
                _jumping = false;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"配方选取：筛选类型 {(int)__instance.filter} 在当前标签下没有可见配方，已跳到第 {page} 页第 {colPage + 1} 列页");
        }

        private static bool HasVisible(UIRecipePicker picker)
        {
            RecipeProto[] protos = picker.protoArray;

            if (protos == null) return false;

            for (var i = 0; i < protos.Length; i++)
                if (protos[i] != null)
                    return true;

            return false;
        }

        /// <summary>找出这个筛选类型下第一条已解锁配方所在的标签页与列页。</summary>
        private static bool FindFirst(ERecipeType filter, out int page, out int colPage)
        {
            page = 1;
            colPage = 0;

            GameHistoryData history = GameMain.history;

            if (history == null) return false;

            var found = false;
            var bestPage = int.MaxValue;
            var bestCol = int.MaxValue;

            foreach (RecipeProto recipe in LDB.recipes.dataArray)
            {
                if (recipe == null || recipe.GridIndex < 1101) continue;
                if (recipe.Type != filter) continue;
                if (!history.RecipeUnlocked(recipe.ID)) continue;

                int p = recipe.GridIndex / 1000;
                int row = recipe.GridIndex % 1000 / 100 - 1;
                int col = recipe.GridIndex % 100 - 1;

                if (row < 0 || col < 0 || row >= Rows) continue;

                if (p > bestPage || (p == bestPage && col >= bestCol)) continue;

                bestPage = p;
                bestCol = col;
                found = true;
            }

            if (!found) return false;

            page = bestPage;
            colPage = Mathf.Clamp(bestCol / ColsPerPage, 0, Pages - 1);

            return true;
        }

        // ── 三、底部滚动条 ────────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIRecipePicker), "_OnUpdate")]
        private static void UIRecipePicker_OnUpdate(UIRecipePicker __instance)
        {
            if (Pages <= 1) return;

            _maxPage = Pages - 1;

            EnsureScrollbar(__instance);
            SyncScrollbar();
        }

        /// <summary>换标签时回到第一列页，否则会停在一个空页上。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIRecipePicker), "OnTypeButtonClick")]
        private static void UIRecipePicker_OnTypeButtonClick(UIRecipePicker __instance)
        {
            // 这个后置也会被 _OnOpen 的自动跳转间接触发，那时列页是刚设好的，不能清掉
            if (_page == 0 || _jumping) return;

            _page = 0;

            __instance.RefreshIcons();
        }

        private static void EnsureScrollbar(UIRecipePicker picker)
        {
            if (_bar != null) return;

            RawImage grid = picker.iconImage;

            if (grid == null) return;

            RectTransform gridTrs = grid.rectTransform;

            if (gridTrs.rect.width <= 0f) return;

            var barGO = new GameObject("projecteden-picker-hscrollbar",
                typeof(RectTransform), typeof(Image), typeof(Scrollbar));

            // 挂在网格自身之下、用拉伸锚点定位。拿 anchoredPosition / rect 去推位置
            // 要看它自己的锚点怎么配，按「左上角锚点」假设算出来的是一个横跨到窗口外的
            // 巨大方块——合成器那根就是这么翻过车的。
            barGO.transform.SetParent(gridTrs, false);

            var barTrs = (RectTransform)barGO.transform;

            barTrs.anchorMin = new Vector2(0f, 0f);
            barTrs.anchorMax = new Vector2(1f, 0f);
            barTrs.pivot = new Vector2(0.5f, 1f);
            barTrs.offsetMin = Vector2.zero;
            barTrs.offsetMax = Vector2.zero;
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

            ProjectEdenPlugin.Log.LogInfo($"配方选取已加入横向翻页滚动条：共 {Pages} 列页，每页 {ColsPerPage} 列");
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

            UIRoot.instance?.uiGame?.recipePicker?.RefreshIcons();
        }
    }
}
