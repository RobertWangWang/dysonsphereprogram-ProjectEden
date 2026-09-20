using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 给建造栏的子项行加横向滑动，让一个分类能放下超过一行的建筑。
    ///
    /// <b>刻意不改 _OnUpdate 和 OnChildButtonClick。</b> 直觉做法是把
    /// <c>protos.Get(currentCategory, j)</c> 的下标加上偏移、而 <c>childButtons[j]</c> 保持不变，
    /// 但那要在 _OnUpdate 里挑出<b>属于子项循环的那几个</b> protos.Get（同一个方法里还有
    /// 分类解锁扫描用的、下标含义完全不同的调用），再同步改 OnChildButtonClick、
    /// F 键映射和图标那一段。漏一处的症状就是建造栏每帧空引用——这个 UI 刚这么崩过一次。
    ///
    /// <b>改成搬运数据，不改读取逻辑。</b> 这里自己维护一张完整的子项表
    /// <c>_full[分类, 槽位]</c>（槽位可以排到 <see cref="MaxSlot"/>），每帧在 _OnUpdate <b>之前</b>
    /// 把当前窗口写进原版的 <c>UIBuildMenu.protos</c> 的前几格。原版从头到尾读它自己的数组，
    /// 点击、双击开合成器、F1~F12、提示框、图标全都自动对齐，一处都不用改。
    ///
    /// 顺带把安全性也拿回来了：窗口只写 1..可见按钮数，超出的一律清空，
    /// 所以<b>不可能</b>再出现「某个槽位有物品但没有按钮」的空引用。
    ///
    /// 原版 <c>UIBuildMenu.protos</c> 是 <c>[16, 13]</c>，容不下更多槽位——但那不要紧，
    /// 装不下的部分本来就在我们自己的 _full 里，原版数组只当窗口用。
    /// </summary>
    [HarmonyPatch]
    internal static class BuildMenuScrollPatches
    {
        /// <summary>一个分类最多能放多少子项。超过这个数的 BuildIndex 会被忽略。</summary>
        internal const int MaxSlot = 36;

        /// <summary>分类数，对齐原版 protos 的第一维。</summary>
        private const int Categories = 16;

        /// <summary>原版 protos 的第二维，窗口只能写到这里面。</summary>
        private const int VanillaSlots = 13;

        private const float BarHeight = 10f;

        private const float BarGap = 4f;

        /// <summary>完整的子项表，_OnUpdate 之前按窗口搬进原版数组。</summary>
        private static ItemProto[,] _full;

        /// <summary>建到哪个槽位为止，用来算页数。</summary>
        private static readonly int[] _maxSlot = new int[Categories];

        /// <summary>逐分类记住翻到第几页。</summary>
        private static readonly int[] _page = new int[Categories];

        /// <summary>一屏能画几个子项——由界面里真正存在的按钮数决定，不是配置。</summary>
        private static int _visible;

        private static int _builtFrom;

        /// <summary>滚动条的轨道，也是命中判定用的矩形。</summary>
        private static RectTransform _bar;

        /// <summary>滑块，每帧按页码摆位。</summary>
        private static RectTransform _handle;

        /// <summary>鼠标是不是正按在轨道上拖。</summary>
        private static bool _dragging;

        // ── 一、每帧把窗口搬进原版数组 ────────────────────────

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIBuildMenu), "_OnUpdate")]
        private static void UIBuildMenu_OnUpdate_Prefix(UIBuildMenu __instance)
        {
            if (!EnsureTable(__instance)) return;

            int category = __instance.currentCategory;

            if (category < 0 || category >= Categories) return;

            int pages = PageCount(category);

            if (_page[category] >= pages) _page[category] = pages - 1;
            if (_page[category] < 0) _page[category] = 0;

            int offset = _page[category] * _visible;

            UIButton[] buttons = __instance.childButtons;
            Image[] icons = __instance.childIcons;

            for (var slot = 1; slot < VanillaSlots; slot++)
            {
                int source = slot + offset;

                // 只写可见范围，其余一律清空——原版 childButtons[slot] 是不判空解引用的
                ItemProto proto = slot <= _visible && source <= MaxSlot ? _full[category, source] : null;

                UIBuildMenu.protos[category, slot] = proto;

                if (proto == null) HideButton(buttons, slot);
                else SyncIcon(icons, slot, proto);
            }

            // 槽位 0 原版从来不用，留着只会给「按钮为 null」制造机会
            UIBuildMenu.protos[category, 0] = null;
            HideButton(buttons, 0);
        }

        /// <summary>
        /// 让这一格的图标跟上它现在放的东西。
        ///
        /// <b>原版的图标是一次性初始化的：</b>
        /// <code>
        ///   if (childIcons[j].sprite == null)
        ///       childIcons[j].sprite = protos[currentCategory, j].iconSprite;
        /// </code>
        /// 一个分类的内容固定，设一次就够了——但翻页会换内容，而那时 sprite 已经非 null，
        /// 于是这一格永远停在第一次画上去的那张图。症状是<b>第二页第一格显示第一页第一格的图标</b>，
        /// 而名字、数量、点击行为全都是对的（那几样每帧都重写）。
        ///
        /// 直接按当前 proto 赋值，不用记状态：引用相同就不写，自我纠正。
        /// </summary>
        private static void SyncIcon(Image[] icons, int slot, ItemProto proto)
        {
            if (icons == null || slot >= icons.Length) return;

            Image icon = icons[slot];

            if (icon == null) return;

            Sprite sprite = proto.iconSprite;

            if (sprite != null && icon.sprite != sprite) icon.sprite = sprite;
        }

        /// <summary>
        /// 把一个空槽位的按钮收起来。
        ///
        /// <b>原版不会替我们做这件事。</b> _OnUpdate 的子项循环里，
        /// <c>if (protos[currentCategory, j] == null) continue;</c> ——空槽位<b>直接跳过</b>，
        /// 按钮保持上一帧的样子；只有「有 proto 但没解锁」那条分支才会去清空并 SetActive(false)。
        /// 原版永远不会出现「这一格从有变没有」，所以没这个需求；但翻页会。
        ///
        /// 症状很有迷惑性：从内容多的一页翻到内容少的一页，多出来的按钮原样留着，
        /// 看起来像<b>翻页没生效</b>；反过来翻回去，每一格都有 proto 会被全部刷新，又是对的。
        /// 一模一样照抄原版隐藏分支做的事。
        /// </summary>
        private static void HideButton(UIButton[] buttons, int slot)
        {
            if (buttons == null || slot >= buttons.Length) return;

            UIButton button = buttons[slot];

            if (button == null) return;

            button.tips.itemId = 0;
            button.tips.itemInc = 0;
            button.tips.itemCount = 0;

            if (button.button == null) return;

            button.button.interactable = false;
            button.button.gameObject.SetActive(false);
        }

        /// <summary>
        /// 建表：按 BuildIndex 把所有建筑归到 [分类, 槽位]，并数出界面真正有几个子项按钮。
        /// </summary>
        private static bool EnsureTable(UIBuildMenu menu)
        {
            UIButton[] buttons = menu.childButtons;
            Text[] texts = menu.childNumTexts;

            if (buttons == null || texts == null) return false;

            if (_visible <= 0)
            {
                // 从 1 开始数连续存在的按钮：原版渲染循环是 j < 12，且槽位是 1 起的
                for (var i = 1; i < VanillaSlots - 1 && i < buttons.Length && i < texts.Length; i++)
                {
                    if (buttons[i] == null) break;

                    _visible = i;
                }

                if (_visible <= 0)
                {
                    ProjectEdenPlugin.Log.LogError("建造栏子项滑动：一个可用的子项按钮都没数到，功能未启用");

                    return false;
                }

                ProjectEdenPlugin.Log.LogInfo($"建造栏子项滑动：界面一屏能画 {_visible} 个子项");
            }

            int itemCount = LDB.items.dataArray.Length;

            if (_full != null && _builtFrom == itemCount) return true;

            _builtFrom = itemCount;
            _full = new ItemProto[Categories, MaxSlot + 1];

            for (var i = 0; i < Categories; i++) _maxSlot[i] = 0;

            foreach (ItemProto item in LDB.items.dataArray)
            {
                if (item == null || item.BuildIndex <= 0) continue;

                int category = item.BuildIndex / 100;
                int slot = item.BuildIndex % 100;

                if (category < 0 || category >= Categories) continue;
                if (slot < 1 || slot > MaxSlot) continue;

                _full[category, slot] = item;

                if (slot > _maxSlot[category]) _maxSlot[category] = slot;
            }

            return true;
        }

        /// <summary>
        /// 某个格位在滑动表里落在第几页第几位。供建造栏自检用。
        ///
        /// <b>为什么需要这个：`UIBuildMenu.protos` 只是个窗口，不是真相。</b>
        /// 原版 <c>StaticLoad</c> 只填到第 12 格，而本类的 <c>_full</c> 排到
        /// <see cref="MaxSlot"/>。任何拿 <c>protos</c> 去判断「这台建筑在不在建造栏里」的
        /// 检查，对第 13 格往后的建筑都只会得到一个答案——而那个答案和「它真的丢了」
        /// 长得一模一样。
        ///
        /// 返回 false = 这个格位在滑动表里也是空的，那才是真的丢了。
        /// </summary>
        internal static bool Locate(int category, int slot, out int page, out int index, out int visible)
        {
            page = 0;
            index = 0;
            visible = _visible;

            if (_full == null || _visible <= 0) return false;
            if (category < 0 || category >= Categories) return false;
            if (slot < 1 || slot > MaxSlot) return false;
            if (_full[category, slot] == null) return false;

            page = (slot - 1) / _visible + 1;
            index = (slot - 1) % _visible + 1;

            return true;
        }

        private static int PageCount(int category)
        {
            if (_visible <= 0) return 1;

            int used = _maxSlot[category];

            return used <= _visible ? 1 : (used + _visible - 1) / _visible;
        }

        // ── 二、滚动条 ────────────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIBuildMenu), "_OnUpdate")]
        private static void UIBuildMenu_OnUpdate_Postfix(UIBuildMenu __instance)
        {
            if (_full == null || _visible <= 0) return;

            int category = __instance.currentCategory;

            if (category < 0 || category >= Categories) return;

            EnsureScrollbar(__instance);

            if (_bar == null) return;

            int pages = PageCount(category);
            bool show = pages > 1 && __instance.childGroup != null && __instance.childGroup.activeSelf;

            _bar.gameObject.SetActive(show);

            if (!show)
            {
                _dragging = false;

                return;
            }

            PollMouse(__instance, category, pages);

            // 滑块位置完全由 _page 决定——它是唯一的事实来源，不存在「控件自己有个值」
            float width = _bar.rect.width;

            if (width <= 0f) return;

            float slice = width / pages;

            _handle.sizeDelta = new Vector2(slice, 0f);
            _handle.anchoredPosition = new Vector2(_page[category] * slice, 0f);
        }

        private static void EnsureScrollbar(UIBuildMenu menu)
        {
            if (_bar != null) return;

            // <b>挂在子项按钮的同一个父级下、按按钮的坐标摆位。</b>
            // 之前是铺满 childGroup，而那个容器比按钮行宽得多，结果是一根横跨大半个屏幕的长条。
            var first = menu.childButtons[1]?.transform as RectTransform;
            var last = menu.childButtons[_visible]?.transform as RectTransform;

            if (first == null || last == null) return;

            var parent = first.parent as RectTransform;

            if (parent == null) return;

            float left = first.anchoredPosition.x - first.rect.width * first.pivot.x;
            float right = last.anchoredPosition.x + last.rect.width * (1f - last.pivot.x);
            float top = first.anchoredPosition.y + first.rect.height * (1f - first.pivot.y);

            if (right - left <= 1f) return;

            var barGO = new GameObject("projecteden-buildmenu-hscrollbar",
                typeof(RectTransform), typeof(Image));

            barGO.transform.SetParent(parent, false);

            var barTrs = (RectTransform)barGO.transform;

            // 锚点照抄按钮，这样坐标含义一致；宽度正好盖住这一行按钮，居中吊在它们上方
            barTrs.anchorMin = first.anchorMin;
            barTrs.anchorMax = first.anchorMax;
            barTrs.pivot = new Vector2(0.5f, 0f);
            barTrs.sizeDelta = new Vector2(right - left, BarHeight);
            barTrs.anchoredPosition = new Vector2((left + right) * 0.5f, top + BarGap);

            barGO.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.08f);

            // <b>不用 UnityEngine.UI.Scrollbar。</b> 它自己维护一个 value 和一套拖动/点击状态机，
            // 和我们每帧按 _page 回写会互相打架——实测症状是能从第 2 页拖回第 1 页、
            // 却拖不到第 2 页去。这里只画两个 Image，输入全部自己收，_page 是唯一事实来源。
            var handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));

            handleGO.transform.SetParent(barTrs, false);

            _handle = (RectTransform)handleGO.transform;

            // 贴左边、纵向铺满；宽度和位置每帧按页码算
            _handle.anchorMin = new Vector2(0f, 0f);
            _handle.anchorMax = new Vector2(0f, 1f);
            _handle.pivot = new Vector2(0f, 0.5f);
            _handle.offsetMin = Vector2.zero;
            _handle.offsetMax = Vector2.zero;

            handleGO.GetComponent<Image>().color = new Color(0.62f, 0.84f, 1f, 0.75f);

            _bar = barTrs;

            ProjectEdenPlugin.Log.LogInfo($"建造栏子项滑动已就绪：一屏 {_visible} 个，每类最多 {MaxSlot} 个");
        }

        /// <summary>
        /// 自己轮询鼠标翻页：点轨道跳到那一页，在子项行上滚滚轮翻一页。
        ///
        /// <b>为什么不用 EventSystem。</b> 先试过 Unity Scrollbar 自带的「点击轨道翻页」
        /// 和挂 EventTrigger 的 PointerClick，两条都没反应——射线能不能落到这根新控件上，
        /// 取决于建造栏那几个 CanvasGroup 的 blocksRaycasts / interactable 怎么配，
        /// 隔着一层猜不出来。_OnUpdate 本来就每帧在跑，直接量鼠标位置最省事也最确定。
        /// 后来连拖动也收回来自己做了——见 EnsureScrollbar 里为什么不用 Scrollbar 组件。
        ///
        /// <b>换算用游戏自己的 UIRoot.ScreenPointIntoRect。</b> 一开始用
        /// RectTransformUtility 加自己找的相机，量不到——GetComponentInParent&lt;Canvas&gt;
        /// 拿到的可能是一层嵌套画布，它的 worldCamera 是空的。
        /// UIRoot 那个helper 直接用 overlayCanvas.worldCamera，是这套 UI 的正解，
        /// 原版 UIRecipePicker.TestMouseIndex 命中测试用的也是它。
        /// </summary>
        private static void PollMouse(UIBuildMenu menu, int category, int pages)
        {
            if (_bar == null || pages <= 1) return;

            Vector3 mouse = Input.mousePosition;

            // 一、按下 / 拖动：落在轨道哪一段就翻到哪一页。
            // 按下时判一次是否命中，之后只要不松手就一直跟着走——手指滑出轨道也不断。
            if (Input.GetMouseButtonDown(0))
                _dragging = UIRoot.ScreenPointIntoRect(mouse, _bar, out Vector2 hit) && _bar.rect.Contains(hit);

            if (!Input.GetMouseButton(0)) _dragging = false;

            if (_dragging && UIRoot.ScreenPointIntoRect(mouse, _bar, out Vector2 local))
            {
                Rect rect = _bar.rect;

                if (rect.width > 0f)
                {
                    float t = (local.x - rect.xMin) / rect.width;

                    SetPage(category, Mathf.FloorToInt(t * pages), pages, "拖动");
                }
            }

            // 二、在子项行上滚滚轮
            float wheel = Input.mouseScrollDelta.y;

            if (Mathf.Approximately(wheel, 0f)) return;

            var rowTrs = menu.childGroup?.transform as RectTransform;

            if (rowTrs == null) return;
            if (!UIRoot.ScreenPointIntoRect(mouse, rowTrs, out Vector2 rowLocal)) return;
            if (!rowTrs.rect.Contains(rowLocal)) return;

            SetPage(category, _page[category] - (int)Mathf.Sign(wheel), pages, "滚轮");
        }

        private static void SetPage(int category, int page, int pages, string how)
        {
            page = Mathf.Clamp(page, 0, pages - 1);

            if (page == _page[category]) return;

            _page[category] = page;

            ProjectEdenPlugin.Log.LogInfo($"建造栏子项滑动：第 {category} 类翻到第 {page + 1}/{pages} 页（{how}）");
        }

    }
}
