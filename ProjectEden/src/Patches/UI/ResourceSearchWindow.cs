using System.Collections.Generic;
using HarmonyLib;
using ProjectEden.Patches.Ore;
using ProjectEden.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectEden.Patches.UI
{
    /// <summary>
    /// 游戏内的资源搜索窗：输入矿名 → 列出哪些星球有、各有多少矿脉 → 点一下把星图飞过去。
    ///
    /// <para><b>控件全部从零搭，不克隆原版窗口。</b> 本仓库克隆原版控件的几次
    /// （实验室第七格、配送机格子、多产物槽位）每次都要花好几轮，根因是一个格位
    /// 往往是八个互相嵌套的控件，<b>只能克隆那些「不被其它七个包含」的根</b>，
    /// 否则你写进去的那一份和画在最上面的那一份不是同一个。而
    /// <see cref="ItemPickerSearchPatches"/> 已经证明从零搭一条工具条是可行的，
    /// 这里沿用同一套造法。</para>
    ///
    /// <para><b>搜索框必须是真的 <c>UnityEngine.UI.InputField</c>。</b>
    /// <c>VFInput.UpdateGameStates</c> 每帧靠
    /// <c>EventSystem.current.currentSelectedGameObject.GetComponent&lt;InputField&gt;() != null</c>
    /// 推导 <c>VFInput.inputing</c>——没有注册、没有订阅。所以用原生 InputField 才能白拿
    /// 快捷键屏蔽；手画的文本框得自己维护那个标志。代价是 <c>inputing</c> 为真时
    /// 游戏自己的 Esc 不再响应，所以 Esc 要自己处理。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class ResourceSearchWindow
    {
        internal const string HotkeyText = "Ctrl + F";

        private const int MaxRows = 12;

        private const float Width = 540f;
        private const float Height = 470f;
        private const float RowHeight = 26f;

        /// <summary>行要给滚动条让出的右边距，否则最后几个字压在轨道下面。</summary>
        private const float ScrollbarGutter = 16f;

        private static RectTransform _root;
        private static InputField _input;
        private static Text _placeholder;
        private static Text _status;
        private static readonly List<Text> Rows = new List<Text>();
        private static readonly List<Button> RowButtons = new List<Button>();

        /// <summary>当前这一页对应的星球，和 <see cref="Rows"/> 同下标。</summary>
        private static readonly List<PlanetData> RowPlanets = new List<PlanetData>();

        private static readonly List<ResourceIndex.Entry> Matched = new List<ResourceIndex.Entry>();

        private static int _scroll;
        private static int _matchedItem;

        /// <summary>除了选中那一种，还有几种也命中了查询串——提示玩家把名字打全。</summary>
        private static int _alsoMatched;
        private static bool _open;
        private static bool _failed;

        /// <summary>滚动条的轨道和手柄。<b>两个普通 Image</b>，见 <see cref="PollScrollbar"/>。</summary>
        private static RectTransform _track;

        private static RectTransform _handle;

        /// <summary>鼠标按下时命中了轨道——之后不松手就一直跟着走。</summary>
        private static bool _dragging;

        internal static bool IsOpen => _open;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), nameof(UIGame._OnUpdate))]
        private static void UIGame_OnUpdate()
        {
            if (_failed) return;
            if (GameMain.isPaused || !GameMain.isRunning) return;

            // Ctrl+F 开关窗。放在 UIGame._OnUpdate 而不是自己的 MonoBehaviour——
            // 这里保证了 UIRoot 已经就绪，省掉一层「还没初始化」的判空
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                if (Input.GetKeyDown(KeyCode.F) && !VFInput.inputing)
                    Toggle();

            if (!_open) return;

            // Esc 自己处理：InputField 拿到焦点之后 VFInput.inputing 为真，
            // 游戏自己那套 Esc 就不响应了
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close();

                return;
            }

            PollScrollbar();

            // 扫描还在跑的时候每帧刷一下进度，扫完会多出新的星球
            if (ResourceIndex.Running) RefreshStatus();
        }

        private static void Toggle()
        {
            if (_open) Close();
            else Open();
        }

        // ── 滚动条 ──────────────────────────────────────────────

        /// <summary>
        /// 拖滚动条翻页。<b>手搓的两个 Image，不是 <c>UnityEngine.UI.Scrollbar</c>。</b>
        ///
        /// <para>本仓库为这件事栽过一次，教训写在建造栏那根滚动条上：
        /// 当时用的是原版 <c>Scrollbar</c>，每帧把 <c>value</c> 从自己的页码写回去——
        /// <b>它内部的拖动/点击状态机和那次写在抢同一个值</b>，症状是
        /// 「能往回翻，不能往前翻」。现在 <see cref="_scroll"/> 是唯一的真相，
        /// 手柄位置由它算出来，输入自己量。</para>
        ///
        /// <para><b>屏幕坐标换算必须用游戏自己的 <c>UIRoot.ScreenPointIntoRect</c>。</b>
        /// 自己拼 <c>RectTransformUtility</c> 加 <c>GetComponentInParent&lt;Canvas&gt;</c>
        /// 量不到东西——那可能拿到一层嵌套画布，而它的 <c>worldCamera</c> 是空的。
        /// 原版 <c>UIRecipePicker.TestMouseIndex</c> 命中测试用的也是这个 helper。</para>
        ///
        /// <para><b>按下时判一次是否命中，之后只要不松手就一直跟着走</b>——
        /// 手滑出轨道也不断，这是拖动该有的手感。</para>
        /// </summary>
        private static void PollScrollbar()
        {
            int max = Matched.Count - MaxRows;

            if (_track == null || max <= 0) return;

            Vector3 mouse = Input.mousePosition;

            if (Input.GetMouseButtonDown(0))
                _dragging = UIRoot.ScreenPointIntoRect(mouse, _track, out Vector2 hit)
                            && _track.rect.Contains(hit);

            if (!Input.GetMouseButton(0)) _dragging = false;

            if (!_dragging) return;
            if (!UIRoot.ScreenPointIntoRect(mouse, _track, out Vector2 local)) return;

            Rect rect = _track.rect;

            if (rect.height <= 0f) return;

            // 轨道自上而下对应 0..max：local.y 越大越靠上，所以要反过来
            float t = 1f - (local.y - rect.yMin) / rect.height;

            int want = Mathf.Clamp(Mathf.RoundToInt(t * max), 0, max);

            if (want == _scroll) return;

            _scroll = want;

            RefreshRows();
            LayoutHandle();
        }

        /// <summary>把手柄摆到 <see cref="_scroll"/> 对应的位置，并按可见比例定高。</summary>
        private static void LayoutHandle()
        {
            if (_track == null || _handle == null) return;

            int total = Matched.Count;
            bool show = total > MaxRows;

            _track.gameObject.SetActive(show);

            if (!show) return;

            float trackHeight = _track.rect.height;

            // 手柄高度 = 可见比例，留一个最小值，否则一千颗星球时它会细成一条线
            float ratio = (float)MaxRows / total;
            float height = Mathf.Max(trackHeight * ratio, 24f);

            int max = total - MaxRows;
            float t = max > 0 ? (float)_scroll / max : 0f;

            _handle.sizeDelta = new Vector2(0f, height);
            _handle.anchoredPosition = new Vector2(0f, -t * (trackHeight - height));
        }

        private static void Open()
        {
            if (!Build()) return;

            _open = true;
            _root.gameObject.SetActive(true);
            _scroll = 0;

            // 索引还没建过就顺手建一次——第一次按快捷键才付这笔扫描的钱
            if (ResourceIndex.Kinds == 0 && !ResourceIndex.Running) ResourceIndex.Rebuild();

            Requery();

            _input.ActivateInputField();
            _input.Select();
        }

        private static void Close()
        {
            _open = false;

            if (_input != null) _input.DeactivateInputField();

            if (_root != null) _root.gameObject.SetActive(false);
        }

        // ── 搜索 ────────────────────────────────────────────────

        private static void OnQueryChanged(string _) => Requery();

        /// <summary>
        /// 按查询串挑出一种资源，再把它的星球列表铺进行。
        ///
        /// <para><b>只认一种资源，不做「跨资源混排」</b>：玩家问的是「白钨矿在哪」，
        /// 混排会把 14 颗钴星和 2 颗钨星搅在一起，反而找不到。</para>
        ///
        /// <para><b>命中多种时按「匹配得有多准」挑，不是按稀有度。</b>
        /// 第一版写的是「取星球最少的那一种，稀有的才是玩家要找的」——
        /// 而那是把两件事搞混了。实测症状：搜「铁」弹出来的是
        /// <b>钒钛磁铁矿</b>（名字里含「铁」，只有 4 颗星球），而不是几十颗星球的铁矿石；
        /// 反倒是搜 <c>iron</c> 才对，因为英文名里只有铁矿石含 iron。
        /// <b>稀有度根本不是匹配质量</b>，见 <see cref="Rank"/>。</para>
        /// </summary>
        private static void Requery()
        {
            Matched.Clear();
            _scroll = 0;
            _matchedItem = 0;
            _alsoMatched = 0;

            string q = (_input?.text ?? "").Trim();

            if (q.Length > 0)
            {
                var best = int.MaxValue;

                foreach (int itemId in ResourceIndex.Items)
                {
                    ItemProto proto = LDB.items.Select(itemId);

                    if (proto == null) continue;

                    int rank = Rank(proto, q);

                    if (rank == int.MaxValue) continue;

                    List<ResourceIndex.Entry> list = ResourceIndex.Find(itemId);

                    if (list == null || list.Count == 0) continue;

                    _alsoMatched++;

                    // 同分时按物品 ID 定序，免得同一个查询串两次给出不同的结果
                    if (rank < best || (rank == best && itemId < _matchedItem))
                    {
                        best = rank;
                        _matchedItem = itemId;
                    }
                }

                if (_alsoMatched > 0) _alsoMatched--;

                if (_matchedItem > 0)
                {
                    List<ResourceIndex.Entry> list = ResourceIndex.Find(_matchedItem);

                    if (list != null) Matched.AddRange(list);
                }
            }

            RefreshRows();
            RefreshStatus();
            LayoutHandle();
        }

        /// <summary>
        /// 查询串命中这种资源吗。<b>三条都试，所以中英文客户端都能用中英文搜。</b>
        ///
        /// <list type="bullet">
        /// <item><c>proto.name</c> —— <b>当前语言</b>的显示名（英文客户端下它就是英文）</item>
        /// <item><c>proto.Name</c> —— <b>原始 key</b>，永远是中文
        /// （本仓库的规矩：<c>Name</c> 是数据，<c>name</c> 是表现）。
        /// 有了它，英文客户端也能输中文</item>
        /// <item><see cref="I18N.EnglishOf"/> —— 不管当前语言都能拿到的英文写法。
        /// 中文客户端输 <c>scheelite</c> 靠的就是这一条</item>
        /// </list>
        ///
        /// <para>外加纯数字按物品 ID 匹配——名字记不住但知道 ID 的场合。</para>
        /// </summary>
        private static int Rank(ItemProto proto, string q)
        {
            if (int.TryParse(q, out int id) && id == proto.ID) return 0;

            int best = System.Math.Min(
                RankOne(proto.name, q),
                System.Math.Min(RankOne(proto.Name, q), RankOne(I18N.EnglishOf(proto.Name), q)));

            return best;
        }

        /// <summary>
        /// 一个名字相对查询串的「准度」，<b>越小越准</b>：
        ///
        /// <list type="number">
        /// <item><b>完全相同</b> —— 10</item>
        /// <item><b>前缀</b> —— 100 + 多出来的字数。「铁矿石」对「铁」多 2 个字，
        /// 排在「钒钛磁铁矿」前面</item>
        /// <item><b>包含</b> —— 1000 + 多出来的字数 + 命中位置。
        /// 位置也算分，是因为「铁」出现在开头比出现在第四个字更像玩家要找的那个</item>
        /// </list>
        ///
        /// <para><b>「多出来的字数」是这里的关键</b>：只判包含的话，
        /// 「铁」会同时命中 铁矿石 / 钒钛磁铁矿 / 磁铁矿……而它们都是合法命中，
        /// 只是<b>离查询串的远近不一样</b>。长度差正是这个远近。</para>
        /// </summary>
        private static int RankOne(string name, string q)
        {
            if (string.IsNullOrEmpty(name)) return int.MaxValue;

            if (string.Equals(name, q, System.StringComparison.OrdinalIgnoreCase)) return 10;

            int at = name.IndexOf(q, System.StringComparison.OrdinalIgnoreCase);

            if (at < 0) return int.MaxValue;

            int extra = name.Length - q.Length;

            return at == 0 ? 100 + extra : 1000 + extra + at;
        }

        private static void RefreshRows()
        {
            RowPlanets.Clear();

            for (var i = 0; i < Rows.Count; i++)
            {
                int index = _scroll + i;

                if (index >= Matched.Count)
                {
                    Rows[i].text = "";
                    RowButtons[i].gameObject.SetActive(false);
                    RowPlanets.Add(null);

                    continue;
                }

                ResourceIndex.Entry e = Matched[index];

                RowButtons[i].gameObject.SetActive(true);
                RowPlanets.Add(e.Planet);

                string where = $"{e.Planet.star?.displayName} · {e.Planet.displayName}";

                Rows[i].text = e.Gas
                    ? where + "    " + string.Format(
                        I18N.Tr("气态    速率 {0}"), (e.Amount / 10000f).ToString("0.##"))
                    : where + "    " + string.Format(
                        I18N.Tr("{0} 个矿脉    储量 {1}"),
                        e.Veins, e.Amount.ToString("N0"));
            }
        }

        private static void RefreshStatus()
        {
            if (_status == null) return;

            string scan = ResourceIndex.Running
                ? string.Format(I18N.Tr("　正在后台扫描 {0}/{1} 颗星球…"),
                    ResourceIndex.Scanned, ResourceIndex.Total)
                : "";

            if (_matchedItem > 0)
            {
                ItemProto proto = LDB.items.Select(_matchedItem);

                _status.text = string.Format(I18N.Tr("{0}：{1} 颗星球"), proto?.name, Matched.Count)
                               + (Matched.Count > MaxRows ? I18N.Tr("（拖右边条翻页）") : "")
                               + (_alsoMatched > 0
                                   ? string.Format(I18N.Tr("　还匹配到另外 {0} 种，把名字打全一点"), _alsoMatched)
                                   : "")
                               + scan;
            }
            else
            {
                _status.text = (_input != null && _input.text.Trim().Length > 0
                                   ? I18N.Tr("没有找到这种资源")
                                   : I18N.Tr("输入矿石或气体的名字"))
                               + scan;
            }
        }

        // ── 点一行：把星图飞过去 ───────────────────────────────

        /// <summary>
        /// <b>先聚焦恒星，再聚焦行星，而且第二步是尽力而为。</b>
        /// <c>UIStarmap.planetUIs</c> 只在「已经进到那个星系视角」时才填得满，
        /// 所以直接找行星的 UI 往往找不到；而 <c>starUIs</c> 一直是全的。
        /// 先把镜头带到恒星，玩家就能看到那个星系，行星 UI 这时通常也在了。
        /// </summary>
        private static void FocusPlanet(int row)
        {
            if (row < 0 || row >= RowPlanets.Count) return;

            PlanetData planet = RowPlanets[row];

            if (planet == null) return;

            UIGame game = UIRoot.instance?.uiGame;
            UIStarmap map = game?.starmap;

            if (game == null || map == null) return;

            if (!game.starmap.active) game.OpenStarmap();

            if (map.starUIs != null && planet.star != null)
                foreach (UIStarmapStar su in map.starUIs)
                    if (su != null && su.star == planet.star)
                    {
                        map.OnStarClick(su);

                        break;
                    }

            if (map.planetUIs != null)
                foreach (UIStarmapPlanet pu in map.planetUIs)
                    if (pu != null && pu.planet == planet)
                    {
                        map.OnPlanetClick(pu);

                        break;
                    }
        }

        // ── 搭窗口 ──────────────────────────────────────────────

        private static bool Build()
        {
            if (_root != null) return true;

            UIRoot root = UIRoot.instance;
            Canvas canvas = root?.overlayCanvas;

            if (canvas == null)
            {
                ProjectEdenPlugin.Log.LogWarning("资源搜索窗：UIRoot.overlayCanvas 还没就绪，这次不建");

                return false;
            }

            Font font = FindFont(root);

            if (font == null)
            {
                _failed = true;

                ProjectEdenPlugin.Log.LogError(
                    "资源搜索窗：在 UIRoot 底下找不到任何 Text 的字体，放弃建窗。"
                    + "**不半建**——少了字体的 InputField 会每帧抛异常，比没有这个窗难查得多");

                return false;
            }

            var rootGO = new GameObject("projecteden-resource-search",
                typeof(RectTransform), typeof(Image));

            rootGO.transform.SetParent(canvas.transform, false);

            _root = (RectTransform)rootGO.transform;
            _root.anchorMin = new Vector2(0.5f, 0.5f);
            _root.anchorMax = new Vector2(0.5f, 0.5f);
            _root.pivot = new Vector2(0.5f, 0.5f);
            _root.sizeDelta = new Vector2(Width, Height);
            _root.anchoredPosition = Vector2.zero;

            rootGO.GetComponent<Image>().color = new Color(0.04f, 0.06f, 0.09f, 0.94f);

            MakeLabel(_root, "title", font, I18N.Tr("资源搜索"), 17, TextAnchor.MiddleLeft,
                new Color(0.92f, 0.96f, 1f), new Vector2(14f, -10f), new Vector2(300f, 24f));

            MakeLabel(_root, "hint", font, string.Format(I18N.Tr("{0} 开关　Esc 关闭"), HotkeyText), 12, TextAnchor.MiddleRight,
                new Color(0.92f, 0.96f, 1f, 0.45f), new Vector2(-14f, -12f), new Vector2(240f, 20f),
                rightAligned: true);

            BuildInput(font);

            _status = MakeLabel(_root, "status", font, "", 12, TextAnchor.MiddleLeft,
                new Color(0.92f, 0.96f, 1f, 0.6f), new Vector2(14f, -78f),
                new Vector2(Width - 28f, 20f));

            for (var i = 0; i < MaxRows; i++) BuildRow(font, i);

            BuildScrollbar();

            RowPlanets.Clear();

            for (var i = 0; i < MaxRows; i++) RowPlanets.Add(null);

            ProjectEdenPlugin.Log.LogInfo(
                $"资源搜索窗：已建好（{HotkeyText} 开关）。控件是从零搭的，没有克隆原版窗口");

            return true;
        }

        private static void BuildInput(Font font)
        {
            var fieldGO = new GameObject("input", typeof(RectTransform), typeof(Image), typeof(InputField));

            fieldGO.transform.SetParent(_root, false);

            var trs = (RectTransform)fieldGO.transform;

            trs.anchorMin = new Vector2(0f, 1f);
            trs.anchorMax = new Vector2(0f, 1f);
            trs.pivot = new Vector2(0f, 1f);
            trs.anchoredPosition = new Vector2(14f, -42f);
            trs.sizeDelta = new Vector2(Width - 28f, 28f);

            var bg = fieldGO.GetComponent<Image>();

            bg.color = new Color(1f, 1f, 1f, 0.10f);

            Text text = MakeText(trs, "Text", font, TextAnchor.MiddleLeft,
                new Color(0.92f, 0.96f, 1f), 14);

            _placeholder = MakeText(trs, "Placeholder", font, TextAnchor.MiddleLeft,
                new Color(0.92f, 0.96f, 1f, 0.35f), 14);

            _placeholder.text = I18N.Tr("矿石或气体的名字，例如 白钨矿");

            _input = fieldGO.GetComponent<InputField>();

            _input.targetGraphic = bg;
            _input.textComponent = text;
            _input.placeholder = _placeholder;
            _input.lineType = InputField.LineType.SingleLine;
            _input.characterLimit = 24;
            _input.customCaretColor = true;
            _input.caretColor = new Color(0.92f, 0.96f, 1f);
            _input.selectionColor = new Color(0.3f, 0.6f, 1f, 0.4f);
            _input.text = "";

            _input.onValueChanged.AddListener(OnQueryChanged);
        }

        private static void BuildRow(Font font, int index)
        {
            var go = new GameObject("row" + index, typeof(RectTransform), typeof(Image), typeof(Button));

            go.transform.SetParent(_root, false);

            var trs = (RectTransform)go.transform;

            trs.anchorMin = new Vector2(0f, 1f);
            trs.anchorMax = new Vector2(0f, 1f);
            trs.pivot = new Vector2(0f, 1f);
            trs.anchoredPosition = new Vector2(14f, -104f - index * RowHeight);
            trs.sizeDelta = new Vector2(Width - 28f - ScrollbarGutter, RowHeight - 2f);

            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, index % 2 == 0 ? 0.04f : 0.07f);

            Text label = MakeText(trs, "label", font, TextAnchor.MiddleLeft,
                new Color(0.88f, 0.93f, 1f), 13);

            Rows.Add(label);

            var button = go.GetComponent<Button>();

            // <b>闭包要捕获行号，不能捕获循环变量。</b> 这是老生常谈，
            // 但在 UI 上它的症状是「点哪一行都跳到最后一行那颗星球」，看着像定位算错了
            int row = index;

            button.onClick.AddListener(() => FocusPlanet(row));

            RowButtons.Add(button);
        }

        /// <summary>
        /// 轨道 + 手柄，两个普通 <c>Image</c>。
        /// <b>刻意不挂 <c>Button</c> 也不挂 <c>EventTrigger</c></b>：射线能不能落到一根
        /// 新控件上，取决于父链上那些 <c>CanvasGroup</c> 的 <c>blocksRaycasts</c> /
        /// <c>interactable</c> 怎么配，隔着一层猜不出来。而 <c>_OnUpdate</c> 本来就每帧在跑，
        /// 直接量鼠标位置最省事也最确定——建造栏那根滚动条就是这么收敛的。
        /// </summary>
        private static void BuildScrollbar()
        {
            var trackGO = new GameObject("scroll-track", typeof(RectTransform), typeof(Image));

            trackGO.transform.SetParent(_root, false);

            _track = (RectTransform)trackGO.transform;
            _track.anchorMin = new Vector2(1f, 1f);
            _track.anchorMax = new Vector2(1f, 1f);
            _track.pivot = new Vector2(1f, 1f);
            _track.anchoredPosition = new Vector2(-6f, -104f);
            _track.sizeDelta = new Vector2(10f, MaxRows * RowHeight - 2f);

            trackGO.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.07f);

            var handleGO = new GameObject("scroll-handle", typeof(RectTransform), typeof(Image));

            handleGO.transform.SetParent(_track, false);

            _handle = (RectTransform)handleGO.transform;
            _handle.anchorMin = new Vector2(0f, 1f);
            _handle.anchorMax = new Vector2(1f, 1f);
            _handle.pivot = new Vector2(0.5f, 1f);
            _handle.anchoredPosition = Vector2.zero;
            _handle.sizeDelta = new Vector2(0f, 40f);

            handleGO.GetComponent<Image>().color = new Color(0.55f, 0.78f, 1f, 0.55f);

            _track.gameObject.SetActive(false);
        }

        private static Text MakeLabel(RectTransform parent, string name, Font font, string content,
            int size, TextAnchor anchor, Color color, Vector2 pos, Vector2 sizeDelta,
            bool rightAligned = false)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));

            go.transform.SetParent(parent, false);

            var trs = (RectTransform)go.transform;

            trs.anchorMin = new Vector2(rightAligned ? 1f : 0f, 1f);
            trs.anchorMax = new Vector2(rightAligned ? 1f : 0f, 1f);
            trs.pivot = new Vector2(rightAligned ? 1f : 0f, 1f);
            trs.anchoredPosition = pos;
            trs.sizeDelta = sizeDelta;

            var text = go.GetComponent<Text>();

            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = color;
            text.text = content;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            return text;
        }

        private static Text MakeText(RectTransform parent, string name, Font font, TextAnchor anchor,
            Color color, int size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));

            go.transform.SetParent(parent, false);

            var trs = (RectTransform)go.transform;

            trs.anchorMin = Vector2.zero;
            trs.anchorMax = Vector2.one;
            trs.offsetMin = new Vector2(8f, 0f);
            trs.offsetMax = new Vector2(-8f, 0f);

            var text = go.GetComponent<Text>();

            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = color;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            return text;
        }

        private static Font FindFont(UIRoot root)
        {
            Text any = root.GetComponentInChildren<Text>(true);

            return any != null ? any.font : null;
        }

        /// <summary>
        /// 开机状态行。<b>不管开没开都要打</b>——这个仓库记过七次：
        /// 状态行回答「接上了没有」，事件行回答「它决定了什么」，一个替不了另一个。
        /// </summary>
        internal static void Report()
        {
            var hooked = false;

            foreach (System.Reflection.MethodBase mb in Harmony.GetAllPatchedMethods())
                if (mb.DeclaringType == typeof(UIGame) && mb.Name == nameof(UIGame._OnUpdate))
                    hooked = true;

            ProjectEdenPlugin.Log.LogInfo(
                hooked
                    ? $"资源搜索窗：挂点已接上，按 {HotkeyText} 打开。"
                      + "第一次打开时才会去建全星系资源索引（用游戏自己的扫描线程，一颗零点几秒）"
                    : "资源搜索窗：**没接上** UIGame._OnUpdate，快捷键不会有反应");
        }
    }
}
