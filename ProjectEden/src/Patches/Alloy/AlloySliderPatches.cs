using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 在冶炼炉窗口底下挂一块「配比」面板：每一味可调原料一根滑动条，
    /// 下面实时显示混合出来的四维、命中的牌号、浪费了多少、耗时罚多少。
    ///
    /// <b>面板不认识任何一种具体合金</b>——行数、标签、范围、牌号名全部来自
    /// <see cref="AlloyRatioPatches"/> 查表的结果。在 <c>alloys.json</c> 里加一条合金，
    /// 这里不用改一个字。
    ///
    /// <b>「一味余量 + N 味可调」让联动问题消失了。</b> 早先是两根条互为补数，
    /// 三元时就要回答「拖一根，另两根怎么分」——比例分摊？加锁？都是额外的交互负担。
    /// 现在余量那一味（<c>slots[0]</c>，通常是基体金属）自动吸收所有变化，
    /// 每根可调条只管自己的上下限，二元三元用同一套逻辑，没有任何联动规则。
    /// 这也正是真实合金牌号的写法：「18% Cr, 8% Mn, 余量 Fe」。
    ///
    /// 只有一味可调时（二元），余量那一行也可以拖——它是唯一的补数，不存在歧义。
    /// 两味以上时余量行只读。
    ///
    /// <b>自绘 Image + 自己轮询输入，不用 UnityEngine.UI.Slider。</b>
    /// 本仓库在建造栏的横向滚动条上踩过这个坑：那是个 <c>Scrollbar</c>，
    /// 它内部的拖拽状态机和我们每帧写 <c>value</c> 互相打架，症状是往回翻得动、
    /// 往前翻不动。<b>别和 Unity 控件争一个值的所有权</b>——这里值只有一个来源
    /// （组件上的 <c>requireCounts</c>），控件纯粹是它的投影。
    ///
    /// 屏幕坐标转局部坐标一律走游戏自己的 <c>UIRoot.ScreenPointIntoRect</c>：
    /// 它是从 <c>overlayCanvas.worldCamera</c> 走的，手写 <c>RectTransformUtility</c>
    /// 会因为 <c>GetComponentInParent&lt;Canvas&gt;()</c> 抓到内层 canvas（worldCamera 为 null）
    /// 而悄悄量出一片空白。
    /// </summary>
    [HarmonyPatch]
    internal static class AlloySliderPatches
    {
        /// <summary>面板最多画几行。配置里槽位比这多会被截断并告警。</summary>
        private const int MaxRows = 4;

        private const float RowHeight = 24f;
        private const float HeadHeight = 26f;
        private const float FootHeight = 24f;
        private const float PanelGap = 6f;
        private const float TrackHeight = 14f;
        private const float LabelWidth = 84f;
        private const float ValueWidth = 46f;
        private const float SidePad = 12f;

        private static GameObject _panel;
        private static RectTransform _panelTrs;
        private static Text _titleText;
        private static Text _resultText;

        private static readonly Row[] Rows = new Row[MaxRows];

        /// <summary>正在拖第几行，−1 表示没在拖。松开鼠标才结束。</summary>
        private static int _dragging = -1;

        private class Row
        {
            internal GameObject Root;
            internal Text Label;
            internal RectTransform Track;
            internal RectTransform Fill;
            internal Image TrackImage;
            internal Text Value;
        }

        // ── 每帧：显示/隐藏 + 同步 + 输入 ─────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIAssemblerWindow), "_OnUpdate")]
        private static void UIAssemblerWindow_OnUpdate(UIAssemblerWindow __instance)
        {
            if (AlloyRatioPatches.Count == 0 || __instance?.factory == null)
            {
                Hide();

                return;
            }

            int assemblerId = __instance._assemblerId;

            if (assemblerId <= 0)
            {
                Hide();

                return;
            }

            int entityId = __instance.factory.factorySystem.assemblerPool[assemblerId].entityId;

            // 合金弹药复用同一块面板，只是每一行从「滑动条」变成「◀ 合金名 ▶」。
            // 另起一块面板要再抄三百行手绘 UI，没必要。
            if (AmmoPairPatches.Current(__instance.factory, entityId, out int[] pair))
            {
                if (!EnsurePanel(__instance)) return;

                _panel.SetActive(true);

                HandleAmmoInput(__instance.factory, entityId, pair);

                if (AmmoPairPatches.Current(__instance.factory, entityId, out int[] nowPair)) pair = nowPair;

                RefreshAmmo(pair);

                return;
            }

            // 这台机器跑的不是可配比的合金就整块收起来，
            // 别在别的配方下面挂一条看不懂的面板
            if (!AlloyRatioPatches.Current(__instance.factory, entityId,
                    out AlloyRatioPatches.Alloy alloy, out int[] parts))
            {
                Hide();

                return;
            }

            if (!EnsurePanel(__instance)) return;

            _panel.SetActive(true);

            HandleInput(__instance.factory, entityId, alloy, parts);

            // 拖过之后要重新读一次实时值，界面显示的必须是机器真的在跑的配比
            if (AlloyRatioPatches.Current(__instance.factory, entityId, out alloy, out int[] now))
                parts = now;

            Refresh(__instance.factory, entityId, alloy, parts);
        }

        /// <summary>窗口关掉时把面板一起收起来，否则它会浮在屏幕上。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIAssemblerWindow), "_OnClose")]
        private static void UIAssemblerWindow_OnClose() => Hide();

        private static void Hide()
        {
            _dragging = -1;

            if (_panel != null && _panel.activeSelf) _panel.SetActive(false);
        }

        // ── 输入 ──────────────────────────────────────────────

        private static void HandleInput(PlanetFactory factory, int entityId,
            AlloyRatioPatches.Alloy alloy, int[] parts)
        {
            if (!Input.GetMouseButton(0))
            {
                _dragging = -1;

                return;
            }

            int rows = RowCount(alloy);

            // 按下的那一帧决定抓住哪根条；之后即使鼠标划出轨道也继续跟随，
            // 这是滑动条该有的手感（松手才结束）
            if (_dragging < 0)
            {
                if (!Input.GetMouseButtonDown(0)) return;

                for (var i = 0; i < rows; i++)
                {
                    if (!Draggable(alloy, i)) continue;
                    if (!UIRoot.ScreenPointIntoRect(Input.mousePosition, Rows[i].Track, out Vector2 hit)) continue;

                    Rect rect = Rows[i].Track.rect;

                    if (hit.x < rect.xMin || hit.x > rect.xMax || hit.y < rect.yMin || hit.y > rect.yMax) continue;

                    _dragging = i;

                    break;
                }

                if (_dragging < 0) return;
            }

            if (!UIRoot.ScreenPointIntoRect(Input.mousePosition, Rows[_dragging].Track, out Vector2 p)) return;

            Rect r = Rows[_dragging].Track.rect;

            if (r.width <= 0f) return;

            float t = Mathf.Clamp01((p.x - r.xMin) / r.width);

            var next = (int[])parts.Clone();

            if (_dragging == 0)
            {
                // 只有一味可调时余量行才可拖：它是唯一的补数，拖它等于反向拖那一味
                AlloySlot only = alloy.Entry.slots[1];
                int lo = alloy.Entry.totalParts - only.max;
                int hi = alloy.Entry.totalParts - only.min;

                next[0] = alloy.Entry.totalParts - Mathf.RoundToInt(Mathf.Lerp(lo, hi, t));
            }
            else
            {
                AlloySlot slot = alloy.Entry.slots[_dragging];

                next[_dragging - 1] = Mathf.RoundToInt(Mathf.Lerp(slot.min, slot.max, t));
            }

            for (var i = 0; i < next.Length; i++) next[i] = AlloyRatioPatches.ClampParts(alloy, i, next[i]);

            var same = true;

            for (var i = 0; i < next.Length; i++)
                if (next[i] != parts[i])
                    same = false;

            if (same) return;

            // 被产物缓冲区挡住时不改值——Refresh 会把原因写在结果那一行
            if (AlloyRatioPatches.Apply(factory, entityId, next) != ApplyResult.Ok) return;

            AlloyRatioStore.SetPlayerDefault(alloy.Entry.recipeId, next);
        }

        // ── 合金弹药：两行选择器 ──────────────────────────────

        /// <summary>上一帧点过没有，避免按住鼠标时一路狂翻</summary>
        private static bool _ammoClickLatch;

        /// <summary>
        /// 点轨道换合金：左半格往前、右半格往后。
        ///
        /// 用「点」不用「拖」，是因为这里选的是<b>离散的一种合金</b>而不是一个连续量——
        /// 拖动条做离散选择会一路扫过中间那些值，每扫过一个都触发一次 Apply。
        /// </summary>
        private static void HandleAmmoInput(PlanetFactory factory, int entityId, int[] pair)
        {
            if (!Input.GetMouseButton(0))
            {
                _ammoClickLatch = false;

                return;
            }

            if (_ammoClickLatch) return;

            List<int> pool = AmmoRegistry.Candidates;

            if (pool.Count < 2) return;

            for (var i = 0; i < 2; i++)
            {
                if (!UIRoot.ScreenPointIntoRect(Input.mousePosition, Rows[i].Track, out Vector2 hit)) continue;

                Rect rect = Rows[i].Track.rect;

                if (hit.x < rect.xMin || hit.x > rect.xMax || hit.y < rect.yMin || hit.y > rect.yMax) continue;

                _ammoClickLatch = true;

                int step = hit.x < rect.center.x ? -1 : 1;
                int at = pool.IndexOf(pair[i]);

                if (at < 0) at = 0;

                var next = (int[])pair.Clone();

                next[i] = pool[((at + step) % pool.Count + pool.Count) % pool.Count];

                if (!AmmoPairPatches.Apply(factory, entityId, next)) return;

                AlloyRatioStore.SetPlayerDefault(AmmoRegistry.RecipeId, next);

                return;
            }
        }

        private static void RefreshAmmo(int[] pair)
        {
            _titleText.text = "合金弹药面板标题".Translate();

            _panelTrs.sizeDelta = new Vector2(0f, HeadHeight + 2 * RowHeight + FootHeight);

            for (var i = 0; i < MaxRows; i++)
            {
                var on = i < 2;

                if (Rows[i].Root.activeSelf != on) Rows[i].Root.SetActive(on);

                if (!on) continue;

                Rows[i].Root.transform.localPosition = new Vector3(0f, -(HeadHeight + i * RowHeight), 0f);

                Rows[i].Label.text = i == 0 ? "合金一".Translate() : "合金二".Translate();

                ItemProto item = LDB.items.Select(pair[i]);

                LayoutRow(Rows[i], true);

                Rows[i].Value.text = "◀  " + (item != null ? item.name : "?") + "  ▶";

                // 选择器没有「填充比例」，填充条收掉、只留轨道当底色，
                // 否则那条彩色填充会盖在名字下面像个进度条
                Rows[i].Fill.anchorMin = Vector2.zero;
                Rows[i].Fill.anchorMax = new Vector2(0f, 1f);
                Rows[i].Fill.offsetMin = Vector2.zero;
                Rows[i].Fill.offsetMax = Vector2.zero;
                Rows[i].TrackImage.color = new Color(1f, 1f, 1f, 0.12f);
            }

            _resultText.rectTransform.anchoredPosition =
                new Vector2(SidePad, -(HeadHeight + 2 * RowHeight + 4f));

            int tier = AmmoPairPatches.TierIndex(pair[0], pair[1]);
            int yield = AmmoPairPatches.Yield(pair[0], pair[1]);

            AmmoRegistry.Tier t = AmmoRegistry.Tiers[tier];

            AmmoPairPatches.Mix(pair[0], pair[1], out float h, out float tg, out float c, out float e);

            _resultText.text =
                $"{AlloyRatioPatches.AxisName("hardness")} {h:0.0}  {AlloyRatioPatches.AxisName("toughness")} {tg:0.0}  "
                + $"{AlloyRatioPatches.AxisName("corrosion")} {c:0.0}  {AlloyRatioPatches.AxisName("conductivity")} {e:0.0}"
                + $"  →  {t.Entry.name} ×{yield}   {"伤害".Translate()} {t.Damage}";

            _resultText.color = new Color(0.72f, 0.82f, 0.92f);
        }

        /// <summary>
        /// 按模式摆一行的布局。<b>两种模式共用同一批控件，所以每次刷新都要摆一遍</b>——
        /// 只在切换时摆的话，从弹药机器切到合金机器会留着上一种的布局。
        ///
        /// 滑动条模式：轨道占中段，数值右对齐占 <see cref="ValueWidth"/>（够放一个两位数）。
        /// 选择器模式：轨道一直拉到右边距，<b>名字居中压在轨道上</b>——
        /// 合金名有五六个字，塞进 46px 的数值位会朝左溢出压到轨道上，那正是「文字和条冲突」。
        /// 居中之后 ◀ ▶ 正好落在轨道左右两半，和「点左半格往前、右半格往后」的判定对得上。
        /// </summary>
        private static void LayoutRow(Row row, bool picker)
        {
            RectTransform track = row.Track;
            RectTransform value = row.Value.rectTransform;

            track.offsetMin = new Vector2(SidePad + LabelWidth, 0f);
            track.offsetMax = new Vector2(picker ? -SidePad : -(SidePad + ValueWidth), 0f);
            track.sizeDelta = new Vector2(track.sizeDelta.x, TrackHeight);
            track.anchoredPosition = new Vector2(SidePad + LabelWidth, -4f);

            if (picker)
            {
                // 和轨道同宽同位，文字压在轨道上居中
                value.anchorMin = new Vector2(0f, 1f);
                value.anchorMax = new Vector2(1f, 1f);
                value.pivot = new Vector2(0f, 1f);
                value.offsetMin = new Vector2(SidePad + LabelWidth, 0f);
                value.offsetMax = new Vector2(-SidePad, 0f);
                value.sizeDelta = new Vector2(value.sizeDelta.x, TrackHeight);
                value.anchoredPosition = new Vector2(SidePad + LabelWidth, -4f);

                row.Value.alignment = TextAnchor.MiddleCenter;

                return;
            }

            value.anchorMin = new Vector2(1f, 1f);
            value.anchorMax = new Vector2(1f, 1f);
            value.pivot = new Vector2(1f, 1f);
            value.sizeDelta = new Vector2(ValueWidth, TrackHeight);
            value.anchoredPosition = new Vector2(-SidePad, -4f);

            row.Value.alignment = TextAnchor.MiddleRight;
        }

        /// <summary>余量行只有在「只有一味可调」时才可拖，否则拖它没有唯一解。</summary>
        private static bool Draggable(AlloyRatioPatches.Alloy alloy, int row) =>
            row > 0 || alloy.Adjustable == 1;

        private static int RowCount(AlloyRatioPatches.Alloy alloy) =>
            Mathf.Min(alloy.Entry.slots.Length, MaxRows);

        // ── 显示 ──────────────────────────────────────────────

        private static void Refresh(PlanetFactory factory, int entityId,
            AlloyRatioPatches.Alloy alloy, int[] parts)
        {
            AlloyEntry cfg = alloy.Entry;
            int rows = RowCount(alloy);

            // 标题走本地化键而不是内插中文：切英文时整行都要变，
            // 而槽名（SlotName）本身是物品名，由原版的 Translate 自己跟着变
            _titleText.text = string.Format("合金配比面板标题".Translate(), cfg.totalParts, alloy.SlotName[0]);

            _panelTrs.sizeDelta = new Vector2(0f, HeadHeight + rows * RowHeight + FootHeight);

            for (var i = 0; i < MaxRows; i++)
            {
                if (Rows[i] == null) continue;

                var on = i < rows;

                if (Rows[i].Root.activeSelf != on) Rows[i].Root.SetActive(on);

                if (!on) continue;

                Rows[i].Root.transform.localPosition = new Vector3(0f, -(HeadHeight + i * RowHeight), 0f);

                int n = i == 0 ? AlloyRatioPatches.BalanceParts(alloy, parts) : parts[i - 1];

                Rows[i].Label.text = alloy.SlotName[i];
                LayoutRow(Rows[i], false);

                Rows[i].Value.text = n.ToString();

                float t;

                if (i == 0)
                {
                    AlloySlot only = cfg.slots.Length > 1 ? cfg.slots[1] : null;
                    int lo = only != null ? cfg.totalParts - only.max : 0;
                    int hi = only != null ? cfg.totalParts - only.min : cfg.totalParts;

                    t = hi > lo ? (float)(n - lo) / (hi - lo) : 0f;
                }
                else
                {
                    AlloySlot slot = cfg.slots[i];

                    t = slot.max > slot.min ? (float)(n - slot.min) / (slot.max - slot.min) : 0f;
                }

                Rows[i].Fill.anchorMax = new Vector2(Mathf.Clamp01(t), 1f);
                Rows[i].Fill.offsetMin = Vector2.zero;
                Rows[i].Fill.offsetMax = Vector2.zero;

                // 不可拖的行画得暗一点，别让玩家去拖一根拖不动的条
                Rows[i].TrackImage.color = Draggable(alloy, i)
                    ? new Color(1f, 1f, 1f, 0.10f)
                    : new Color(1f, 1f, 1f, 0.04f);
            }

            _resultText.rectTransform.anchoredPosition =
                new Vector2(SidePad, -(HeadHeight + rows * RowHeight + 4f));

            AlloyRatioPatches.Mix(alloy, parts, out float h, out float tg, out float c, out float e);

            AlloyRatioPatches.Settle(alloy, parts, out int yield, out int timeSpend);

            ItemProto product = LDB.items.Select(alloy.ProductItemId);
            string name = product != null ? product.name : "?";

            // 四维值实时显示，但**它们不是物品的属性**——一种合金只有一个物品、一行固定属性。
            // 这里显示的是「这一台炉子现在这炉料混出来是什么样」，以及它换来多少产量。
            _resultText.text =
                $"{AlloyRatioPatches.AxisName("hardness")} {h:0.0}  {AlloyRatioPatches.AxisName("toughness")} {tg:0.0}  "
                + $"{AlloyRatioPatches.AxisName("corrosion")} {c:0.0}  {AlloyRatioPatches.AxisName("conductivity")} {e:0.0}"
                + $"  →  {name} ×{yield}   {timeSpend / 60f:0.##} 秒"
                + $"   （基准 ×{alloy.BaseYield} / {alloy.BaseTime / 60f:0.##} 秒）";

            _resultText.color = new Color(0.72f, 0.82f, 0.92f);
        }

        // ── 建面板 ────────────────────────────────────────────

        private static bool EnsurePanel(UIAssemblerWindow window)
        {
            if (_panel != null) return true;

            RectTransform host = window.windowTrans;
            Font font = window.stateText != null ? window.stateText.font : null;

            if (host == null || font == null || host.rect.width <= 0f) return false;

            _panel = new GameObject("projecteden-alloy-ratio", typeof(RectTransform), typeof(Image));

            // 挂在窗口自己底下、用拉伸锚点吊在窗口下方。拿 anchoredPosition / rect
            // 去硬推位置要看它自己的锚点怎么配，按「左上角锚点」假设算出来的是一个
            // 横跨到屏幕外的巨大方块——合成器那根滚动条就是这么翻的车。
            _panel.transform.SetParent(host, false);

            _panelTrs = (RectTransform)_panel.transform;

            _panelTrs.anchorMin = new Vector2(0f, 0f);
            _panelTrs.anchorMax = new Vector2(1f, 0f);
            _panelTrs.pivot = new Vector2(0.5f, 1f);
            _panelTrs.offsetMin = Vector2.zero;
            _panelTrs.offsetMax = Vector2.zero;
            _panelTrs.sizeDelta = new Vector2(0f, HeadHeight + 2 * RowHeight + FootHeight);
            _panelTrs.anchoredPosition = new Vector2(0f, -PanelGap);

            _panel.GetComponent<Image>().color = new Color(0.06f, 0.08f, 0.10f, 0.92f);

            _titleText = MakeText(_panelTrs, font, 13, TextAnchor.MiddleLeft,
                new Vector2(SidePad, -5f), new Vector2(420f, 18f));
            _titleText.color = new Color(0.85f, 0.90f, 0.96f);

            var tint = new[]
            {
                new Color(0.48f, 0.53f, 0.58f),
                new Color(0.42f, 0.60f, 0.88f),
                new Color(0.55f, 0.78f, 0.55f),
                new Color(0.85f, 0.66f, 0.42f),
            };

            for (var i = 0; i < MaxRows; i++) Rows[i] = MakeRow(_panelTrs, font, tint[i]);

            _resultText = MakeText(_panelTrs, font, 12, TextAnchor.MiddleLeft,
                new Vector2(SidePad, -80f), new Vector2(620f, 18f));

            ProjectEdenPlugin.Log.LogInfo(
                $"合金配比面板已挂到冶炼炉窗口下方（最多 {MaxRows} 根滑动条，余量自动吸收）");

            return true;
        }

        private static Row MakeRow(RectTransform parent, Font font, Color fill)
        {
            var root = new GameObject("row", typeof(RectTransform));

            root.transform.SetParent(parent, false);

            var rootTrs = (RectTransform)root.transform;

            rootTrs.anchorMin = new Vector2(0f, 1f);
            rootTrs.anchorMax = new Vector2(1f, 1f);
            rootTrs.pivot = new Vector2(0.5f, 1f);
            rootTrs.offsetMin = new Vector2(0f, 0f);
            rootTrs.offsetMax = new Vector2(0f, 0f);
            rootTrs.sizeDelta = new Vector2(0f, RowHeight);

            Text label = MakeText(rootTrs, font, 12, TextAnchor.MiddleLeft,
                new Vector2(SidePad, -4f), new Vector2(LabelWidth, TrackHeight));

            var trackGO = new GameObject("track", typeof(RectTransform), typeof(Image));

            trackGO.transform.SetParent(rootTrs, false);

            var track = (RectTransform)trackGO.transform;

            // 轨道左右都拉伸，右边给数值文字留位置
            track.anchorMin = new Vector2(0f, 1f);
            track.anchorMax = new Vector2(1f, 1f);
            track.pivot = new Vector2(0f, 1f);
            track.offsetMin = new Vector2(SidePad + LabelWidth, 0f);
            track.offsetMax = new Vector2(-(SidePad + ValueWidth), 0f);
            track.sizeDelta = new Vector2(track.sizeDelta.x, TrackHeight);
            track.anchoredPosition = new Vector2(SidePad + LabelWidth, -4f);

            var trackImage = trackGO.GetComponent<Image>();

            trackImage.color = new Color(1f, 1f, 1f, 0.10f);

            var fillGO = new GameObject("fill", typeof(RectTransform), typeof(Image));

            fillGO.transform.SetParent(track, false);

            var fillTrs = (RectTransform)fillGO.transform;

            fillTrs.anchorMin = Vector2.zero;
            fillTrs.anchorMax = new Vector2(0f, 1f);
            fillTrs.offsetMin = Vector2.zero;
            fillTrs.offsetMax = Vector2.zero;

            fillGO.GetComponent<Image>().color = fill;

            Text value = MakeText(rootTrs, font, 12, TextAnchor.MiddleRight,
                new Vector2(0f, -4f), new Vector2(ValueWidth, TrackHeight));

            value.rectTransform.anchorMin = new Vector2(1f, 1f);
            value.rectTransform.anchorMax = new Vector2(1f, 1f);
            value.rectTransform.pivot = new Vector2(1f, 1f);
            value.rectTransform.anchoredPosition = new Vector2(-SidePad, -4f);

            return new Row
            {
                Root = root, Label = label, Track = track,
                Fill = fillTrs, TrackImage = trackImage, Value = value,
            };
        }

        private static Text MakeText(RectTransform parent, Font font, int size, TextAnchor anchor,
            Vector2 pos, Vector2 box)
        {
            var go = new GameObject("text", typeof(RectTransform), typeof(Text));

            go.transform.SetParent(parent, false);

            var trs = (RectTransform)go.transform;

            trs.anchorMin = new Vector2(0f, 1f);
            trs.anchorMax = new Vector2(0f, 1f);
            trs.pivot = new Vector2(0f, 1f);
            trs.sizeDelta = box;
            trs.anchoredPosition = pos;

            var text = go.GetComponent<Text>();

            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = new Color(0.78f, 0.84f, 0.90f);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            return text;
        }
    }
}
