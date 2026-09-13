using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 给「物品选取」窗口（UIItemPicker）加一条底部工具条：<b>搜索框 + 翻页按钮</b>。
    ///
    /// <b>为什么需要。</b> 物流站的物品槽、分拣器过滤、储物箱过滤全都走这一个窗口，
    /// 而它只画 14 列 × 8 行，<c>RefreshIcons</c> 里 <c>col &gt;= 14</c> 直接裁掉。
    /// 本 mod 的物品排在第 15～42 列，靠 <see cref="ItemPickerExpandPatches"/> 的横向翻页才够得着——
    /// 但那是「把 40 多列摊成 3 个横页去翻」，物流站有 30 个槽要配，一个个翻过去根本没法用。
    ///
    /// <b>搜索模式是怎么接管的。</b> 前缀 <c>RefreshIcons</c>：搜索框非空时自己填
    /// <c>indexArray</c> / <c>protoArray</c>，然后 <c>return false</c> 跳过原版。
    /// 填的时候<b>不按 GridIndex 定位，而是把命中项从第 0 格起顺序码放</b>，
    /// 所以格位在第几列彻底不影响能不能选到——这正是问题的根。
    /// 命中测试（<c>TestMouseIndex</c>）和悬浮提示读的都是 <c>hoveredIndex</c> 与
    /// <c>protoArray[hoveredIndex]</c>，用的是同一套顺序下标，一个字都不用改。
    ///
    /// <b>搜索跨标签页。</b> 原版按 <c>GridIndex / 1000 == currentType</c> 分「物品 / 建筑」两个标签，
    /// 搜索时故意不做这个过滤：玩家想找二氧化碳，不该先知道它在哪个标签下。
    ///
    /// <b>热键屏蔽是白拿的。</b> <c>VFInput.UpdateGameStates</c> 每帧都拿
    /// <c>EventSystem.current.currentSelectedGameObject.GetComponent&lt;InputField&gt;() != null</c>
    /// 去推 <c>VFInput.inputing</c>（IL 013F–0162）。所以只要用<b>原生 UnityEngine.UI.InputField</b>，
    /// 打字时游戏热键自动不响应，不需要我们自己去维护那个标志位——
    /// 反过来说，换成自绘输入框就得手动管，那是给自己找麻烦。
    /// </summary>
    [HarmonyPatch]
    internal static class ItemPickerSearchPatches
    {
        /// <summary>底部工具条的高度与它离网格下沿的间距。</summary>
        private const float RowHeight = 26f;

        private const float RowGap = 6f;

        /// <summary>右侧翻页区的宽度（◀ + 页码 + ▶）。</summary>
        private const float PageGroupWidth = 92f;

        private static RectTransform _row;
        private static InputField _input;
        private static Text _pageLabel;
        private static Text _placeholder;
        private static GameObject _prevGO;
        private static GameObject _nextGO;

        private static UIItemPicker _owner;
        private static string _query = "";
        private static bool _suppress;
        private static int _shown;
        private static int _total;
        private static bool _reported;

        /// <summary>搜索框里有东西。<see cref="ItemPickerExpandPatches"/> 会据此让出横向翻页。</summary>
        /// <summary>
        /// 「只让选这几样」的白名单。设了之后这个物品选择器就变成一张<b>限定清单</b>：
        /// 只画名单里的东西，从第 0 格起顺序排，搜索框照常在名单内过滤。
        ///
        /// <b>为什么加在这里而不是另画一个下拉框。</b> 搜索模式本来就<b>放弃了格位坐标</b>
        /// （前缀自己填 <c>protoArray</c>，从第 0 格顺序码进去，<c>GridIndex</c> 彻底不参与），
        /// 所以「只填名单里的」是同一条路上的一个 <c>continue</c>。而手绘一个带滚动条的下拉框
        /// 要重做命中测试、滚动、悬停提示和翻页——原版这个窗口这四样全有，
        /// 本仓库还给它补过搜索框。<b>能用引擎自己的实现就别再写一个</b>。
        ///
        /// 关窗即清空，所以不会泄漏到下一次别人打开这个选择器。
        /// </summary>
        private static HashSet<int> _allowed;

        private static string _hint;

        /// <summary>
        /// 下一次（也只有下一次）打开物品选择器时，只许在 <paramref name="items"/> 里选。
        /// <paramref name="hint"/> 是搜索框的占位提示，写成一句给玩家看的中文。
        /// </summary>
        internal static void Restrict(IEnumerable<int> items, string hint)
        {
            _allowed = items == null ? null : new HashSet<int>(items);
            _hint = hint;
        }

        /// <summary>白名单模式下不必解锁也能选：名单是调用方给的，它自己保证合理性。</summary>
        public static bool IsFiltering => _query.Length > 0 || _allowed != null;

        private static readonly List<ItemProto> Matched = new List<ItemProto>();

        // ── 一、搜索模式接管格位填充 ──────────────────────────

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIItemPicker), "RefreshIcons")]
        private static bool RefreshIcons_Prefix(UIItemPicker __instance)
        {
            if (!IsFiltering) return true;

            FillFiltered(__instance);

            return false;
        }

        /// <summary>
        /// 把命中的物品从第 0 格起顺序码进去。
        /// 解锁判定照抄原版那一句（<c>showAll || history.ItemUnlocked(id)</c>），
        /// 否则搜索会把没解锁的东西也翻出来，和不搜索时的口径对不上。
        /// </summary>
        private static void FillFiltered(UIItemPicker picker)
        {
            uint[] index = picker.indexArray;
            ItemProto[] protos = picker.protoArray;

            if (index == null || protos == null) return;

            Array.Clear(index, 0, index.Length);
            Array.Clear(protos, 0, protos.Length);

            _shown = 0;
            _total = 0;

            IconSet icons = GameMain.iconSet;

            if (icons?.itemIconIndex == null) return;

            GameHistoryData history = GameMain.history;

            Matched.Clear();

            ItemProto[] all = LDB.items.dataArray;

            for (var i = 0; i < all.Length; i++)
            {
                ItemProto item = all[i];

                if (item == null) continue;

                if (_allowed != null)
                {
                    // 白名单模式：名单说了算。**不查解锁、不查格位** ——
                    // 名单是调用方按游戏内的事实推出来的（比如「这种矿真的能炼出锭」），
                    // 再套一层原版的解锁判定只会让一部分合法选项凭空消失。
                    if (!_allowed.Contains(item.ID)) continue;
                }
                else
                {
                    // 原版就是从 1101 起算的：格位比这个小的是不打算让人选的内部道具
                    if (item.GridIndex < 1101) continue;

                    if (!UIItemPicker.showAll && (history == null || !history.ItemUnlocked(item.ID)))
                        continue;
                }

                if (_query.Length > 0 && !Matches(item, _query)) continue;

                Matched.Add(item);
            }

            Matched.Sort(CompareByGrid);

            int cells = Math.Min(index.Length,
                Math.Min(protos.Length, ProtoSlots.VisibleRows * ProtoSlots.VisibleCols));

            int n = Math.Min(Matched.Count, cells);

            for (var i = 0; i < n; i++)
            {
                ItemProto item = Matched[i];

                index[i] = (uint)item.ID < (uint)icons.itemIconIndex.Length
                    ? icons.itemIconIndex[item.ID]
                    : 0u;

                protos[i] = item;
            }

            _shown = n;
            _total = Matched.Count;

            Matched.Clear();
        }

        /// <summary>
        /// 名字用 <c>name</c>（已翻译，玩家看到什么就能打什么）和 <c>Name</c>（原始中文键）各匹配一次，
        /// 这样英文界面下打中文名也找得到。纯数字则按物品 ID 精确匹配。
        /// </summary>
        private static bool Matches(ItemProto item, string q)
        {
            string shown = item.name;

            if (!string.IsNullOrEmpty(shown) && shown.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string key = item.Name;

            if (!string.IsNullOrEmpty(key) && key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return int.TryParse(q, out int id) && item.ID == id;
        }

        private static int CompareByGrid(ItemProto a, ItemProto b) => a.GridIndex.CompareTo(b.GridIndex);

        // ── 二、开关窗时重置 ──────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIItemPicker), "_OnOpen")]
        private static void UIItemPicker_OnOpen(UIItemPicker __instance)
        {
            _owner = __instance;

            ClearQuery();

            EnsureRow(__instance);

            if (_input == null) return;

            // 占位符是建控件时贴上去的，而语言可以在两次开窗之间切换，所以每次开窗重贴。
            // 白名单模式下换成调用方给的提示——否则玩家看到一张只有十几样东西的表，
            // 会以为是选择器坏了而不是「这里只能选这些」。
            if (_placeholder != null)
                _placeholder.text = _allowed != null && !string.IsNullOrEmpty(_hint)
                    ? _hint.Translate()
                    : "搜索物品名或 ID".Translate();

            // 弹出来就聚焦：这是个模态小窗，打开它就是为了找东西。
            // 聚焦之后 VFInput.inputing 自动为 true，游戏热键不会被打字触发。
            _input.Select();
            _input.ActivateInputField();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIItemPicker), "_OnClose")]
        private static void UIItemPicker_OnClose()
        {
            // **白名单只活一次开窗。** 不清的话下一次谁打开这个选择器
            // （分拣器过滤、储物箱过滤、物流站槽位）都会看到一张残留的短名单，
            // 而那看起来就是「选择器坏了」——本仓库在 UIStationStorage 的共享控件上
            // 已经为「谁写的谁负责还原」付过一次学费。
            _allowed = null;
            _hint = null;

            ClearQuery();

            if (_input != null) _input.DeactivateInputField();

            if (_row != null) _row.gameObject.SetActive(false);
        }

        // ── 三、每帧维护 ──────────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIItemPicker), "_OnUpdate")]
        private static void UIItemPicker_OnUpdate(UIItemPicker __instance)
        {
            _owner = __instance;

            EnsureRow(__instance);

            if (_row == null) return;

            if (!_row.gameObject.activeSelf) _row.gameObject.SetActive(true);

            Reposition(__instance);

            RefreshLabels(__instance);

            // Esc：先退出输入，再关窗。输入框聚焦时 VFInput.inputing 为真，
            // 游戏自己那套关窗热键不会响应，所以这里必须自己接一下。
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_input != null) _input.DeactivateInputField();

                ClearQuery();

                UIItemPicker.Close();

                return;
            }

            // 回车：直接取第一个命中项。搜到唯一一个时这是最快的确认方式
            if (!Input.GetKeyDown(KeyCode.Return) && !Input.GetKeyDown(KeyCode.KeypadEnter)) return;

            if (!IsFiltering) return;

            ItemProto[] protos = __instance.protoArray;

            if (protos == null || protos.Length == 0 || protos[0] == null) return;

            __instance.selectedProto = protos[0];

            if (_input != null) _input.DeactivateInputField();

            ClearQuery();

            UIItemPicker.Close();
        }

        /// <summary>
        /// 每帧重算一次位置。看着像浪费，其实是两个真问题的解：
        /// 一是这是个<b>会移动的弹窗</b>（<c>UIStationStorage.OnSelectItemButtonClick</c> 把它
        /// 摆在物流站窗口左边 300px 处），挂点若在窗口之上就不会跟着走；
        /// 二是建工具条那一帧布局未必算完，只算一次就会永远停在错位置上。
        /// 代价是每帧几十次浮点运算，而且只在这个小窗开着时发生。
        /// </summary>
        private static void Reposition(UIItemPicker picker)
        {
            RawImage grid = picker.iconImage;

            if (grid == null) return;

            RectTransform gridTrs = grid.rectTransform;

            if (gridTrs.rect.width <= 0f) return;

            if (!(_row.parent is RectTransform host)) return;

            PlaceBelowGrid(_row, gridTrs, host, RowGap, RowHeight);
        }

        private static void RefreshLabels(UIItemPicker picker)
        {
            if (_pageLabel == null) return;

            if (IsFiltering)
            {
                if (_prevGO != null) _prevGO.SetActive(false);
                if (_nextGO != null) _nextGO.SetActive(false);

                _pageLabel.text = _total > _shown ? $"{_shown}/{_total}" : _total.ToString();

                _pageLabel.color = _total == 0
                    ? new Color(1f, 0.55f, 0.45f)
                    : new Color(0.72f, 0.86f, 1f);

                return;
            }

            int pages = ItemPickerExpandPatches.PageCount(picker.currentType);

            bool many = pages > 1;

            if (_prevGO != null) _prevGO.SetActive(many);
            if (_nextGO != null) _nextGO.SetActive(many);

            _pageLabel.color = new Color(0.72f, 0.86f, 1f);
            _pageLabel.text = many ? $"{ItemPickerExpandPatches.Page + 1}/{pages}" : "";
        }

        private static void ClearQuery()
        {
            _query = "";
            _shown = 0;
            _total = 0;

            if (_input == null || _input.text.Length <= 0) return;

            _suppress = true;
            _input.text = "";
            _suppress = false;
        }

        private static void OnQueryChanged(string value)
        {
            if (_suppress) return;

            string q = (value ?? "").Trim();

            if (q == _query) return;

            _query = q;

            if (_owner != null) _owner.RefreshIcons();
        }

        private static void OnPrevClick()
        {
            if (_owner != null) ItemPickerExpandPatches.SetPage(_owner, ItemPickerExpandPatches.Page - 1);
        }

        private static void OnNextClick()
        {
            if (_owner != null) ItemPickerExpandPatches.SetPage(_owner, ItemPickerExpandPatches.Page + 1);
        }

        // ── 四、搭工具条 ──────────────────────────────────────

        private static void EnsureRow(UIItemPicker picker)
        {
            // Unity 的「假 null」：控件被销毁后这里会重新为真，于是自动重建
            if (_row != null && _input != null) return;

            RawImage grid = picker.iconImage;

            if (grid == null) return;

            RectTransform gridTrs = grid.rectTransform;

            if (gridTrs.rect.width <= 0f) return;

            Font font = FindFont(picker);

            if (font == null)
            {
                if (_reported) return;

                _reported = true;

                ProjectEdenPlugin.Log.LogWarning(
                    "物品选取搜索框：找不到可用字体，搜索框未创建（物品仍可用横向翻页找到）");

                return;
            }

            if (_row != null) UnityEngine.Object.Destroy(_row.gameObject);

            RectTransform host = ResolveHost(gridTrs, out string hostWhy);

            var rowGO = new GameObject("projecteden-itempicker-searchrow", typeof(RectTransform));

            rowGO.transform.SetParent(host, false);

            _row = (RectTransform)rowGO.transform;

            PlaceBelowGrid(_row, gridTrs, host, RowGap, RowHeight);

            BuildInput(font);
            BuildPageGroup(font);

            _reported = true;

            ProjectEdenPlugin.Log.LogInfo(
                $"物品选取已加入搜索框（挂在 {host.name}，{hostWhy}）：" +
                "按物品名或 ID 过滤，回车取第一个命中项；右侧 ◀ ▶ 是横向翻页");
        }

        /// <summary>
        /// 字体从窗口自己的 Text 上取，样式才和原版一致；
        /// 取不到就往整个 UI 根上找一个。<b>宁可不建也不要用 null 字体</b>——
        /// Unity 会每帧刷一条错误，比没有搜索框糟得多。
        /// </summary>
        private static Font FindFont(UIItemPicker picker)
        {
            Text own = picker.GetComponentInChildren<Text>(true);

            if (own != null && own.font != null) return own.font;

            UIRoot root = UIRoot.instance;

            if (root == null) return null;

            Text any = root.GetComponentInChildren<Text>(true);

            return any != null ? any.font : null;
        }

        /// <summary>
        /// 往上找会裁剪的祖先，把工具条挂到裁剪层<b>之上</b>。
        /// 画在网格下沿以外的东西，一旦父链上有 Mask / RectMask2D 就会被整块裁掉——
        /// 那是「代码跑了、日志也说建好了、屏幕上什么都没有」的经典形状，
        /// 而且离线读不出来预制体里挂没挂遮罩，只能在运行时躲开。
        /// </summary>
        private static RectTransform ResolveHost(RectTransform grid, out string why)
        {
            RectTransform host = grid;

            why = "父链上没有遮罩";

            for (Transform t = grid; t != null; t = t.parent)
            {
                if (t.GetComponent<Canvas>() != null) break;

                if (t.GetComponent<Mask>() == null && t.GetComponent<RectMask2D>() == null) continue;

                if (!(t.parent is RectTransform above)) break;

                host = above;
                why = $"{t.name} 上有遮罩，已上提";
            }

            return host;
        }

        /// <summary>
        /// 用世界角点换算到宿主的局部坐标，而不是照搬网格的 anchoredPosition。
        /// 宿主的锚点、轴心怎么配是预制体的事，按「左上角锚点」硬推会算出一个
        /// 横跨到窗口外的巨大方块——合成器那根滚动条就是这么翻过车的。
        /// </summary>
        private static void PlaceBelowGrid(RectTransform bar, RectTransform grid, RectTransform host,
            float gap, float height)
        {
            var corners = new Vector3[4];

            grid.GetWorldCorners(corners); // 0=左下 1=左上 2=右上 3=右下

            Vector3 bl = host.InverseTransformPoint(corners[0]);
            Vector3 br = host.InverseTransformPoint(corners[3]);

            Rect hostRect = host.rect;

            bar.anchorMin = Vector2.zero;
            bar.anchorMax = Vector2.zero;
            bar.pivot = new Vector2(0f, 1f);
            bar.sizeDelta = new Vector2(Mathf.Abs(br.x - bl.x), height);

            // 锚点定在宿主左下角，偏移量因此要减去 rect 的左下角，
            // 这样无论宿主自己的轴心在哪都算得对
            bar.anchoredPosition = new Vector2(
                Mathf.Min(bl.x, br.x) - hostRect.xMin,
                bl.y - hostRect.yMin - gap);
        }

        private static void BuildInput(Font font)
        {
            var fieldGO = new GameObject("input", typeof(RectTransform), typeof(Image), typeof(InputField));

            fieldGO.transform.SetParent(_row, false);

            var fieldTrs = (RectTransform)fieldGO.transform;

            fieldTrs.anchorMin = Vector2.zero;
            fieldTrs.anchorMax = Vector2.one;
            fieldTrs.offsetMin = Vector2.zero;
            fieldTrs.offsetMax = new Vector2(-(PageGroupWidth + 6f), 0f);

            var bg = fieldGO.GetComponent<Image>();

            bg.color = new Color(1f, 1f, 1f, 0.10f);

            Text text = MakeText(fieldTrs, "Text", font, TextAnchor.MiddleLeft,
                new Color(0.92f, 0.96f, 1f));

            Text placeholder = MakeText(fieldTrs, "Placeholder", font, TextAnchor.MiddleLeft,
                new Color(0.92f, 0.96f, 1f, 0.35f));

            _placeholder = placeholder;

            placeholder.text = "搜索物品名或 ID".Translate();

            _input = fieldGO.GetComponent<InputField>();

            _input.targetGraphic = bg;
            _input.textComponent = text;
            _input.placeholder = placeholder;
            _input.lineType = InputField.LineType.SingleLine;
            _input.characterLimit = 24;
            _input.customCaretColor = true;
            _input.caretColor = new Color(0.92f, 0.96f, 1f);
            _input.selectionColor = new Color(0.3f, 0.6f, 1f, 0.4f);
            _input.text = "";

            _input.onValueChanged.AddListener(OnQueryChanged);
        }

        private static void BuildPageGroup(Font font)
        {
            var groupGO = new GameObject("pages", typeof(RectTransform));

            groupGO.transform.SetParent(_row, false);

            var groupTrs = (RectTransform)groupGO.transform;

            groupTrs.anchorMin = new Vector2(1f, 0f);
            groupTrs.anchorMax = new Vector2(1f, 1f);
            groupTrs.pivot = new Vector2(1f, 0.5f);
            groupTrs.sizeDelta = new Vector2(PageGroupWidth, 0f);
            groupTrs.anchoredPosition = Vector2.zero;

            _prevGO = MakeArrow(groupTrs, "prev", font, "◀", true, OnPrevClick);
            _nextGO = MakeArrow(groupTrs, "next", font, "▶", false, OnNextClick);

            _pageLabel = MakeText(groupTrs, "label", font, TextAnchor.MiddleCenter,
                new Color(0.72f, 0.86f, 1f));

            var labelTrs = (RectTransform)_pageLabel.transform;

            labelTrs.offsetMin = new Vector2(26f, 0f);
            labelTrs.offsetMax = new Vector2(-26f, 0f);

            _pageLabel.text = "";
        }

        private static GameObject MakeArrow(RectTransform parent, string name, Font font, string glyph,
            bool left, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));

            go.transform.SetParent(parent, false);

            var trs = (RectTransform)go.transform;

            trs.anchorMin = new Vector2(left ? 0f : 1f, 0f);
            trs.anchorMax = new Vector2(left ? 0f : 1f, 1f);
            trs.pivot = new Vector2(left ? 0f : 1f, 0.5f);
            trs.sizeDelta = new Vector2(24f, 0f);
            trs.anchoredPosition = Vector2.zero;

            var image = go.GetComponent<Image>();

            image.color = new Color(1f, 1f, 1f, 0.10f);

            Text label = MakeText(trs, "Text", font, TextAnchor.MiddleCenter,
                new Color(0.72f, 0.86f, 1f));

            label.text = glyph;

            var button = go.GetComponent<Button>();

            button.targetGraphic = image;
            button.onClick.AddListener(onClick);

            go.SetActive(false);

            return go;
        }

        private static Text MakeText(RectTransform parent, string name, Font font, TextAnchor anchor, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));

            go.transform.SetParent(parent, false);

            var trs = (RectTransform)go.transform;

            trs.anchorMin = Vector2.zero;
            trs.anchorMax = Vector2.one;
            trs.offsetMin = new Vector2(6f, 0f);
            trs.offsetMax = new Vector2(-6f, 0f);

            var text = go.GetComponent<Text>();

            text.font = font;
            text.fontSize = 14;
            text.alignment = anchor;
            text.color = color;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            return text;
        }
    }
}
