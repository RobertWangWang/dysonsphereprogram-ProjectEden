using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 挂在能量枢纽窗口下方的一行：<c>◀ 服务哪一档蓄能柜 ▶</c>。
    ///
    /// <b>为什么非要这一行，而不是留着那个隐式入口。</b>
    /// 第一版把切档做成「手上拿着另一档的柜子去点窗口里的柜位图标」——理由是那个
    /// 点击处理本来就读 <c>player.inhandItemId</c>，白捡一个入口，不用做界面。
    /// 实测两件事都不成立：**玩家根本找不到它**（报上来的原话是「我没有看到切换页面」），
    /// 而且那个按钮在这台建筑上**手动放入也不工作**。
    ///
    /// 教训和本仓库反复记的那条是同一个：一个没人看得见的机制，等于没有这个机制。
    /// 日志里那条「状态行要能把『没生效』和『没触发』分开」是给读日志的人的，
    /// 面向玩家的等价物就是这一行控件。
    ///
    /// <b>做法照抄本仓库已有的那套，不和 Unity 控件抢所有权。</b>
    /// 两个纯 <c>Image</c> 当左右箭头、自己轮询鼠标；值只有一个来源
    /// （组件上的 <c>emptyId</c>/<c>fullId</c>），控件纯粹是它的投影。
    /// 面板用拉伸锚点吊在窗口下方——拿 <c>anchoredPosition</c> 硬推位置要看窗口自己的
    /// 锚点怎么配，按「左上角锚点」假设算出来的是一个横跨到屏幕外的巨大方块。
    /// 屏幕坐标转局部一律走 <c>UIRoot.ScreenPointIntoRect</c>：手写
    /// <c>RectTransformUtility</c> 会因为抓到内层 canvas（<c>worldCamera</c> 为 null）
    /// 而悄悄量出一片空白。
    /// </summary>
    [HarmonyPatch]
    internal static class MegaExchangerPanelPatches
    {
        private const float PanelHeight = 52f;
        private const float PanelGap = 4f;
        private const float SidePad = 14f;
        private const float ArrowSize = 22f;

        private static GameObject _panel;
        private static RectTransform _panelTrs;
        private static RectTransform _left;
        private static RectTransform _right;
        private static Text _label;
        private static Text _hint;

        private static bool _logged;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIPowerExchangerWindow), "_OnUpdate")]
        private static void UIPowerExchangerWindow_OnUpdate(UIPowerExchangerWindow __instance)
        {
            int id = __instance?.exchangerId ?? 0;
            PowerExchangerComponent[] pool = __instance?.powerSystem?.excPool;

            if (id <= 0 || pool == null || id >= pool.Length)
            {
                Hide();

                return;
            }

            long rate = pool[id].energyPerTick;

            // 只对本 mod 的巨型枢纽画这一行，而且只在它真的能切档时画
            // （配了 alsoServes 才有第二档；只有一档时这一行是噪音）
            if (CountPairs(rate) < 2)
            {
                Hide();

                return;
            }

            if (!EnsurePanel(__instance)) return;

            _panel.SetActive(true);

            MegaExchangerDefaultPatches.Pair cur = Current(rate, pool[id].emptyId);
            var busy = pool[id].emptyCount != 0 || pool[id].fullCount != 0;

            _label.text = cur != null
                ? $"服务：{Name(cur.EmptyId)}"
                : $"服务：{Name(pool[id].emptyId)}";

            // **说清楚为什么点不动**，而不是让箭头无声地没反应——
            // 「里头还有货」和「这功能坏了」在玩家那里长得一样
            _hint.text = busy
                ? "里头还有柜子，取空之后才能换档（换了的话那些柜子会取不出来）"
                : "左右箭头切换这台枢纽服务哪一档蓄能柜；选择会随存档保存";
            _hint.color = busy
                ? new Color(0.90f, 0.72f, 0.42f)
                : new Color(0.62f, 0.70f, 0.78f);

            if (!busy) HandleInput(pool, id, rate);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIPowerExchangerWindow), "_OnClose")]
        private static void UIPowerExchangerWindow_OnClose() => Hide();

        private static void HandleInput(PowerExchangerComponent[] pool, int id, long rate)
        {
            if (!Input.GetMouseButtonDown(0)) return;

            int step = Hit(_left) ? -1 : Hit(_right) ? 1 : 0;

            if (step == 0) return;

            MegaExchangerDefaultPatches.Pair next = Step(rate, pool[id].emptyId, step);

            if (next == null) return;

            pool[id].emptyId = next.EmptyId;
            pool[id].fullId = next.FullId;

            ProjectEdenPlugin.Log.LogInfo(
                $"巨型能量枢纽 #{id}：改服务「{next.Key}」（空 {next.EmptyId} / 满 {next.FullId}）。"
                + "emptyId/fullId 是逐组件字段且进存档，所以这个选择由原版自己存下来");
        }

        private static bool Hit(RectTransform target)
        {
            if (target == null) return false;
            if (!UIRoot.ScreenPointIntoRect(Input.mousePosition, target, out Vector2 p)) return false;

            Rect r = target.rect;

            return p.x >= r.xMin && p.x <= r.xMax && p.y >= r.yMin && p.y <= r.yMax;
        }

        private static int CountPairs(long rate)
        {
            var n = 0;

            foreach (MegaExchangerDefaultPatches.Pair p in MegaExchangerDefaultPatches.Pairs)
                if (p.EnergyPerTick == rate)
                    n++;

            return n;
        }

        private static MegaExchangerDefaultPatches.Pair Current(long rate, int emptyId)
        {
            foreach (MegaExchangerDefaultPatches.Pair p in MegaExchangerDefaultPatches.Pairs)
                if (p.EnergyPerTick == rate && p.EmptyId == emptyId)
                    return p;

            return null;
        }

        /// <summary>按当前档往前/往后走一格，循环。</summary>
        private static MegaExchangerDefaultPatches.Pair Step(long rate, int emptyId, int step)
        {
            var list = new System.Collections.Generic.List<MegaExchangerDefaultPatches.Pair>();

            foreach (MegaExchangerDefaultPatches.Pair p in MegaExchangerDefaultPatches.Pairs)
                if (p.EnergyPerTick == rate)
                    list.Add(p);

            if (list.Count < 2) return null;

            var at = 0;

            for (var i = 0; i < list.Count; i++)
                if (list[i].EmptyId == emptyId)
                    at = i;

            int next = (at + step + list.Count) % list.Count;

            return list[next] == null || list[next].EmptyId == emptyId ? null : list[next];
        }

        private static string Name(int itemId)
        {
            ItemProto item = itemId > 0 ? LDB.items.Select(itemId) : null;

            return item != null ? item.name : itemId.ToString();
        }

        private static void Hide()
        {
            if (_panel != null) _panel.SetActive(false);
        }

        private static bool EnsurePanel(UIPowerExchangerWindow window)
        {
            if (_panel != null) return true;

            // 这个窗口没有 windowTrans（那是 UIAssemblerWindow 自己的字段），
            // 直接用它本体的 RectTransform 当宿主
            var host = window.transform as RectTransform;

            if (host == null || host.rect.width <= 0f) return false;

            // 字体从窗口自己的某个 Text 上取——写死字体名在别的语言包下会静默拿到 null
            Text any = window.GetComponentInChildren<Text>(true);
            Font font = any != null ? any.font : null;

            if (font == null) return false;

            _panel = new GameObject("projecteden-exchanger-pair", typeof(RectTransform), typeof(Image));
            _panel.transform.SetParent(host, false);

            _panelTrs = (RectTransform)_panel.transform;
            _panelTrs.anchorMin = new Vector2(0f, 0f);
            _panelTrs.anchorMax = new Vector2(1f, 0f);
            _panelTrs.pivot = new Vector2(0.5f, 1f);
            _panelTrs.offsetMin = Vector2.zero;
            _panelTrs.offsetMax = Vector2.zero;
            _panelTrs.sizeDelta = new Vector2(0f, PanelHeight);
            _panelTrs.anchoredPosition = new Vector2(0f, -PanelGap);

            _panel.GetComponent<Image>().color = new Color(0.06f, 0.08f, 0.10f, 0.92f);

            _left = MakeArrow(font, "◀", SidePad);
            _label = MakeText(font, 13, TextAnchor.MiddleCenter,
                new Vector2(SidePad + ArrowSize + 6f, -6f), new Vector2(240f, 20f));
            _label.color = new Color(0.88f, 0.92f, 0.98f);
            _right = MakeArrow(font, "▶", SidePad + ArrowSize + 6f + 240f + 6f);

            _hint = MakeText(font, 11, TextAnchor.MiddleLeft,
                new Vector2(SidePad, -28f), new Vector2(520f, 18f));

            if (!_logged)
            {
                _logged = true;

                ProjectEdenPlugin.Log.LogInfo(
                    "巨型能量枢纽：服务档位选择行已挂到枢纽窗口下方"
                    + "（第一版做成了「手上拿着柜子点柜位图标」，玩家找不到，也不工作）");
            }

            return true;
        }

        private static RectTransform MakeArrow(Font font, string glyph, float x)
        {
            var go = new GameObject("arrow", typeof(RectTransform), typeof(Image));

            go.transform.SetParent(_panelTrs, false);

            var trs = (RectTransform)go.transform;

            trs.anchorMin = new Vector2(0f, 1f);
            trs.anchorMax = new Vector2(0f, 1f);
            trs.pivot = new Vector2(0f, 1f);
            trs.sizeDelta = new Vector2(ArrowSize, ArrowSize);
            trs.anchoredPosition = new Vector2(x, -5f);

            go.GetComponent<Image>().color = new Color(0.18f, 0.24f, 0.30f, 0.95f);

            Text t = MakeText(font, 15, TextAnchor.MiddleCenter, Vector2.zero,
                new Vector2(ArrowSize, ArrowSize));

            t.rectTransform.SetParent(trs, false);
            t.rectTransform.anchorMin = new Vector2(0f, 0f);
            t.rectTransform.anchorMax = new Vector2(1f, 1f);
            t.rectTransform.offsetMin = Vector2.zero;
            t.rectTransform.offsetMax = Vector2.zero;
            t.text = glyph;
            t.color = new Color(0.85f, 0.90f, 0.96f);

            return trs;
        }

        private static Text MakeText(Font font, int size, TextAnchor anchor, Vector2 pos, Vector2 box)
        {
            var go = new GameObject("text", typeof(RectTransform), typeof(Text));

            go.transform.SetParent(_panelTrs, false);

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
