using System.Collections.Generic;
using HarmonyLib;
using ProjectEden.Utils;
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
            // 八种模式共用这块面板，所以只要其中任何一种就绪就不能提前收起来。
            // 只判 AlloyRatioPatches.Count 的话，关掉 alloys.json 会连带让
            // 弹药、复合材、增产剂、燃烧厂和提纯厂的面板一起消失——那是几个不相干的功能。
            if (__instance?.factory == null
                || (AlloyRatioPatches.Count == 0 && !AmmoRegistry.Ready && !CompositeRegistry.Ready
                    && !ProliferatorPatches.Ready && !CatalystBedPatches.Ready && !RedoxRegistry.Ready
                    && !QualityRefineryRegistry.Ready))
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

            // 活性复合材：同一块面板的第三种模式。第一行是**选料行**（点左右半边换合金），
            // 第二行是**配比滑条**（拖，决定合金占几份）。两种控件同屏是这块面板此前
            // 没组合过的用法——LayoutRow 本来就是按行切的，所以不用改它。
            if (CompositePatches.Current(__instance.factory, entityId, out int[] comp))
            {
                if (!EnsurePanel(__instance)) return;

                _panel.SetActive(true);

                HandleCompositeInput(__instance.factory, entityId, comp);

                if (CompositePatches.Current(__instance.factory, entityId, out int[] nowComp)) comp = nowComp;

                RefreshComposite(comp);

                return;
            }

            // 烧结析出：只有一行选择器 —— 放哪一级进去，就出哪一样原版材料
            if (CompositeOutputPatches.Current(__instance.factory, entityId, out int[] outState))
            {
                if (!EnsurePanel(__instance)) return;

                _panel.SetActive(true);

                HandleOutputInput(__instance.factory, entityId, outState);

                if (CompositeOutputPatches.Current(__instance.factory, entityId, out int[] nowOut))
                    outState = nowOut;

                RefreshOutput(outState);

                return;
            }

            // 活性增产剂：第五种模式，和合金弹药同形——两行选料位，产物由这一对决定。
            // 不同的是产物是二维的（档次 × 性格），所以结果行要把两个分数都写出来，
            // 否则玩家只看到「换了一对，产物变了」，看不出是哪个维度在动。
            if (ProliferatorPatches.Current(__instance.factory, entityId, out int[] prolif))
            {
                if (!EnsurePanel(__instance)) return;

                _panel.SetActive(true);

                HandleProliferatorInput(__instance.factory, entityId, prolif);

                if (ProliferatorPatches.Current(__instance.factory, entityId, out int[] nowProlif))
                    prolif = nowProlif;

                RefreshProliferator(prolif);

                return;
            }

            // 氧化还原燃烧厂：第七种模式，也是**第一种三行的**——两行选料 + 一行滑条。
            // 它和活性复合材同形（选择器和滑条同屏），但多一行，而 LayoutRow 本来就是按行切的，
            // 所以不用改它。滑条调的是「配氧比」：氧化剂投料量相对化学计量的百分数，
            // 100 就是正好配平。
            if (RedoxBurnerPatches.Current(__instance.factory, entityId, out int[] redox))
            {
                if (!EnsurePanel(__instance)) return;

                _panel.SetActive(true);

                HandleRedoxInput(__instance.factory, entityId, redox);

                if (RedoxBurnerPatches.Current(__instance.factory, entityId, out int[] nowRedox))
                    redox = nowRedox;

                RefreshRedox(__instance.factory, entityId, redox);

                return;
            }

            // 同位提纯：第八种模式，也是**唯一一种候选项不是配置列出来而是推导出来的**——
            // 选料行里那串金属是运行时从矿脉原型和原版配方推出来的（QualityRefineryRegistry），
            // 三级提纯共用同一串。面板形状和烧结析出一样：一行选择器。
            if (QualityRefinerySelectPatches.Current(__instance.factory, entityId,
                    out QualityRefineryRegistry.Tier tier, out int[] refine))
            {
                if (!EnsurePanel(__instance)) return;

                _panel.SetActive(true);

                HandleRefineInput(__instance.factory, entityId, tier, refine);

                if (QualityRefinerySelectPatches.Current(__instance.factory, entityId,
                        out QualityRefineryRegistry.Tier nowTier, out int[] nowRefine))
                {
                    tier = nowTier;
                    refine = nowRefine;
                }

                RefreshRefine(tier, refine);

                return;
            }

            // 催化剂床：第六种模式，也是**唯一只读的一种**——催化剂由配方决定、不给玩家选，
            // 所以只有 Refresh 没有 HandleInput。它存在的理由是巨型建筑的 30 个储物格
            // 对玩家不可见（MegaStationWindowPatches 把 stationId 报成 0 让配方窗口顶上来），
            // 而催化剂床就住在那里面——没有这块面板，玩家没有任何途径看到它。
            if (CatalystBedPatches.Current(__instance.factory, entityId,
                    out int charge, out int life, out int stock, out int spent))
            {
                if (!EnsurePanel(__instance)) return;

                _panel.SetActive(true);

                RefreshCatalyst(charge, life, stock, spent);

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

        private static bool _prolifClickLatch;

        /// <summary>
        /// 活性增产剂的输入：两行选料位，点左右半边换材料。
        ///
        /// <b>自己一个 latch，不和弹药共用。</b> 共用的话在两种机器之间来回点，
        /// 前一次的按下状态会把后一次吃掉。
        /// </summary>
        private static void HandleProliferatorInput(PlanetFactory factory, int entityId, int[] pair)
        {
            if (!Input.GetMouseButton(0))
            {
                _prolifClickLatch = false;

                return;
            }

            if (_prolifClickLatch) return;

            List<int> pool = ProliferatorPatches.Candidates;

            if (pool.Count < 1) return;

            for (var i = 0; i < 2; i++)
            {
                if (!InRow(i, out Vector2 hit)) continue;

                _prolifClickLatch = true;

                Rect rect = Rows[i].Track.rect;
                int step = hit.x < rect.center.x ? -1 : 1;
                int at = pool.IndexOf(pair[i]);

                if (at < 0) at = 0;

                var next = (int[])pair.Clone();

                next[i] = pool[((at + step) % pool.Count + pool.Count) % pool.Count];

                if (!ProliferatorPatches.Apply(factory, entityId, next)) return;

                AlloyRatioStore.SetPlayerDefault(ProliferatorPatches.RecipeId, next);

                return;
            }
        }

        private static void RefreshProliferator(int[] pair)
        {
            _titleText.text = "活性增产剂面板标题".Translate();

            _panelTrs.sizeDelta = new Vector2(0f, HeadHeight + 2 * RowHeight + FootHeight);

            for (var i = 0; i < MaxRows; i++)
            {
                var on = i < 2;

                if (Rows[i].Root.activeSelf != on) Rows[i].Root.SetActive(on);

                if (!on) continue;

                Rows[i].Root.transform.localPosition = new Vector3(0f, -(HeadHeight + i * RowHeight), 0f);
                Rows[i].Label.text = i == 0 ? "投料一".Translate() : "投料二".Translate();

                ItemProto item = LDB.items.Select(pair[i]);

                LayoutRow(Rows[i], true);

                Rows[i].Value.text = "◀  " + (item != null ? item.name : "?") + "  ▶";
            }

            _resultText.rectTransform.anchoredPosition =
                new Vector2(SidePad, -(HeadHeight + 2 * RowHeight + 4f));

            ProliferatorPatches.Outcome outcome =
                ProliferatorPatches.Outcomes[ProliferatorPatches.OutcomeIndex(pair[0], pair[1])];

            // 把两个分数都写出来：产物是二维的，只报结果的话玩家分不清
            // 刚才那一下动的是「档次」还是「性格」
            _resultText.text =
                $"{"性格".Translate()} {ProliferatorPatches.Character(pair[0], pair[1]):0.00}"
                + $"  {"档次".Translate()} {ProliferatorPatches.Tier(pair[0], pair[1]):0.0}"
                + $"  →  {outcome.Entry.name} ×{outcome.Yield}"
                + $"   {"喷涂等级".Translate()} {outcome.Entry.ability}"
                + $" / {"可喷件数".Translate()} {outcome.Entry.hpMax}";

            _resultText.color = new Color(0.72f, 0.92f, 0.82f);
        }

        private static bool _outputClickLatch;

        /// <summary>烧结析出的输入：一行选择器，点左右半边换等级。</summary>
        private static void HandleOutputInput(PlanetFactory factory, int entityId, int[] state)
        {
            if (!Input.GetMouseButton(0))
            {
                _outputClickLatch = false;

                return;
            }

            if (_outputClickLatch) return;

            List<CompositeRegistry.Output> pool = CompositeRegistry.Outputs;

            if (pool.Count < 2 || !InRow(0, out Vector2 hit)) return;

            _outputClickLatch = true;

            Rect rect = Rows[0].Track.rect;
            int step = hit.x < rect.center.x ? -1 : 1;

            var at = 0;

            for (var i = 0; i < pool.Count; i++)
                if (pool[i].GradeItemId == state[0])
                    at = i;

            var next = new[] { pool[((at + step) % pool.Count + pool.Count) % pool.Count].GradeItemId };

            if (!CompositeOutputPatches.Apply(factory, entityId, next)) return;

            AlloyRatioStore.SetPlayerDefault(CompositeRegistry.OutputRecipeId, next);
        }

        private static void RefreshOutput(int[] state)
        {
            _titleText.text = "烧结析出面板标题".Translate();

            _panelTrs.sizeDelta = new Vector2(0f, HeadHeight + RowHeight + FootHeight);

            for (var i = 0; i < MaxRows; i++)
            {
                var on = i == 0;

                if (Rows[i].Root.activeSelf != on) Rows[i].Root.SetActive(on);
            }

            Rows[0].Root.transform.localPosition = new Vector3(0f, -HeadHeight, 0f);
            Rows[0].Label.text = "投入等级".Translate();

            LayoutRow(Rows[0], true);

            CompositeRegistry.Output pick = CompositeRegistry.FindOutput(state[0])
                                            ?? CompositeRegistry.Outputs[0];

            ItemProto grade = LDB.items.Select(pick.GradeItemId);

            Rows[0].Value.text = "◀  " + (grade != null ? grade.name : "?") + "  ▶";
            Rows[0].Fill.anchorMin = Vector2.zero;
            Rows[0].Fill.anchorMax = new Vector2(0f, 1f);
            Rows[0].Fill.offsetMin = Vector2.zero;
            Rows[0].Fill.offsetMax = Vector2.zero;
            Rows[0].TrackImage.color = new Color(1f, 1f, 1f, 0.12f);

            _resultText.rectTransform.anchoredPosition =
                new Vector2(SidePad, -(HeadHeight + RowHeight + 4f));

            ItemProto target = LDB.items.Select(pick.TargetItemId);

            _resultText.text =
                $"{(grade != null ? grade.name : "?")} ×{pick.Entry.input}"
                + $"  →  {(target != null ? target.name : "?")} ×{pick.Entry.count}"
                + $"   {pick.Entry.timeSpend / 60f:0.##}s";

            _resultText.color = new Color(0.92f, 0.86f, 0.70f);
        }

        private static bool _refineClickLatch;

        /// <summary>点开选择器的那一刻是哪台建筑——回调里拿不到别的上下文。</summary>
        private static PlanetFactory _refineFactory;
        private static int _refineEntity;
        private static int _refineRecipe;

        /// <summary>
        /// 同位提纯的输入：点这一行<b>打开原版的物品选择器</b>，限定在可提纯的金属里。
        ///
        /// <b>为什么不是 ◀ ▶ 那种循环。</b> 第一版是循环的，实测难用：候选表是推导出来的，
        /// 一局能有十几种金属，想选最后一种就得点十几下，而且中途看不到还有什么。
        /// 所有者的原话是「有没有更 human 的交互方式？比如带滑动条的下拉框之类的」。
        ///
        /// <b>而那个下拉框游戏里已经有了</b>：<c>UIItemPicker</c> 是一张带图标的网格，
        /// 有悬停提示、有翻页，本仓库还给它补过搜索框。手绘一个带滚动条的下拉框
        /// 意味着把命中测试、滚动、提示、翻页重写一遍，且只服务这一个面板。
        /// <see cref="ItemPickerSearchPatches.Restrict"/> 把它限定成一张短名单，
        /// 这一行就成了真正的下拉框。
        /// </summary>
        private static void HandleRefineInput(PlanetFactory factory, int entityId,
            QualityRefineryRegistry.Tier tier, int[] state)
        {
            if (!Input.GetMouseButton(0))
            {
                _refineClickLatch = false;

                return;
            }

            if (_refineClickLatch) return;

            List<QualityRefineryRegistry.Feed> pool = QualityRefineryRegistry.Feeds;

            if (pool.Count < 1 || !InRow(0, out Vector2 _)) return;
            if (UIItemPicker.isOpened) return;

            _refineClickLatch = true;

            _refineFactory = factory;
            _refineEntity = entityId;
            _refineRecipe = tier.RecipeId;

            var ids = new int[pool.Count];

            for (var i = 0; i < pool.Count; i++) ids[i] = pool[i].ItemId;

            ItemPickerSearchPatches.Restrict(ids, "只能选可提纯的金属");

            UIItemPicker.Popup(Vector2.zero, OnRefinePicked);

            // **没开起来就当场把白名单撤掉。** `Popup` 在 UIRoot 还没就绪或窗口已激活时
            // 直接 return（IL 000B / 002E），那时 `_OnClose` 永远不会来，
            // 名单就会一直挂在那儿，下一个打开物品选择器的人看到的是一张残留的短清单。
            if (!UIItemPicker.isOpened)
            {
                ItemPickerSearchPatches.Restrict(null, null);

                return;
            }

            PlacePickerNearRow();
        }

        /// <summary>
        /// <c>Popup</c> 只是把 <c>pickerTrans.anchoredPosition</c> 设成传进去的值（IL 0050~0057），
        /// 而那是<b>它自己父级里的锚定坐标</b>，和这块面板的父级未必是同一个。
        /// 所以传 0 让它先开，再用<b>世界坐标</b>把它挪到这一行旁边——世界坐标与锚点、pivot
        /// 无关，是本仓库在多产物面板上已经验证过的写法。
        /// </summary>
        private static void PlacePickerNearRow()
        {
            UIItemPicker picker = UIRoot.instance?.uiGame?.itemPicker;

            if (picker?.pickerTrans == null || Rows[0]?.Track == null) return;

            RectTransform row = Rows[0].Track;
            RectTransform p = picker.pickerTrans;

            // 挪到这一行的正上方一点：面板本身贴在装配器窗口下沿，往上开不会出屏。
            Vector3 at = row.position;

            p.position = at;

            // 再把整块拉回屏幕内。选择器是个模态小窗，飘出屏幕就等于点不到。
            var corners = new Vector3[4];

            p.GetWorldCorners(corners);

            float dx = 0f, dy = 0f;

            if (corners[0].x < 0f) dx = -corners[0].x;
            else if (corners[2].x > Screen.width) dx = Screen.width - corners[2].x;

            if (corners[0].y < 0f) dy = -corners[0].y;
            else if (corners[1].y > Screen.height) dy = Screen.height - corners[1].y;

            if (dx != 0f || dy != 0f) p.position += new Vector3(dx, dy, 0f);
        }

        /// <summary>
        /// 选择器回调。<b>要重新核对一遍</b>：白名单是显示层的过滤，不是保证——
        /// 玩家可能在选择器开着的时候关掉了面板，或者这台建筑已经换了配方。
        /// </summary>
        private static void OnRefinePicked(ItemProto proto)
        {
            PlanetFactory factory = _refineFactory;
            int entityId = _refineEntity;
            int recipeId = _refineRecipe;

            _refineFactory = null;
            _refineEntity = 0;
            _refineRecipe = 0;

            if (proto == null || factory == null || entityId <= 0) return;
            if (QualityRefineryRegistry.FindFeed(proto.ID) == null) return;

            var next = new[] { proto.ID };

            if (!QualityRefinerySelectPatches.Apply(factory, entityId, next)) return;

            AlloyRatioStore.SetPlayerDefault(recipeId, next);
        }

        /// <summary>
        /// 同位提纯的一行：选哪种金属。<b>进出是同一种物品，差别只在品质</b>，
        /// 所以结果行必须把品质那个数写出来——不写的话面板上是
        /// 「铜块 ×100 → 铜块 ×80」，看起来像一条纯亏料的废配方。
        ///
        /// 而品质没有别的地方能看：它是<b>容器</b>的属性，物品 tip 里放不下
        /// （那里只有原型），提纯厂自己那 30 个储物格对玩家也是不可见的
        /// （MegaStationWindowPatches 把 stationId 报成 0 让配方窗口顶上来）。
        /// </summary>
        private static void RefreshRefine(QualityRefineryRegistry.Tier tier, int[] state)
        {
            _titleText.text = "同位提纯　选料与产出".Translate();

            _panelTrs.sizeDelta = new Vector2(0f, HeadHeight + RowHeight + FootHeight);

            for (var i = 0; i < MaxRows; i++)
            {
                var on = i == 0;

                if (Rows[i].Root.activeSelf != on) Rows[i].Root.SetActive(on);
            }

            Rows[0].Root.transform.localPosition = new Vector3(0f, -HeadHeight, 0f);

            QualityRefineryRegistry.Feed feed = QualityRefineryRegistry.FindFeed(state[0]);

            // **画成下拉框而不是 ◀ ▶。** 这一行点开的是原版的物品选择器（限定名单 + 搜索框），
            // 不是左右循环，所以箭头会骗人——玩家会去点两端，然后发现两端和中间一个样。
            DropdownRow(Rows[0], "提纯金属".Translate(), feed?.Name);

            _resultText.rectTransform.anchoredPosition =
                new Vector2(SidePad, -(HeadHeight + RowHeight + 4f));

            if (feed == null)
            {
                // 配方原型上那一种没进候选表时会走到这里。
                // 报出来，别让它和「面板没画」长得一样。
                _resultText.text = "配料未就绪".Translate();
                _resultText.color = new Color(0.95f, 0.55f, 0.45f);

                return;
            }

            _resultText.text = string.Format(
                "{0} ×{1}  →  {0} ×{2}　品质 {3}/件　{4:0.##}s".Translate(),
                feed.Name, tier.InputUnits, QualityRefineryRegistry.OutputOf(tier),
                tier.Quality, tier.TimeSpend / 60f);

            _resultText.color = new Color(0.92f, 0.86f, 0.70f);
        }

        /// <summary>
        /// 催化剂床的三行：装填 / 活性 / 待生。<b>纯报数，不接输入。</b>
        ///
        /// 活性那一行是真的进度条（<c>LayoutRow(row, false)</c>），另外两行是仓位数字——
        /// 三行共用同一套控件，所以<b>每次刷新都要重跑 LayoutRow</b>：这块面板是
        /// 五种模式共用的，上一台机器要是选料模式，不重排的话这里会继承它的布局。
        /// </summary>
        private static bool _redoxClickLatch;

        /// <summary>
        /// 燃烧厂面板的输入。<b>三行三个量</b>：
        ///
        /// 第 0 / 1 行选还原剂和氧化剂，用<b>点</b>不用拖——选的是离散的一种物品，
        /// 拖动会一路扫过中间那些值，每扫过一个都触发一次 Apply（弹药和复合材那边同理）。
        ///
        /// 第 2 行调配氧比，用<b>拖</b>不用点——它是连续量，而且这正是这台机器要玩家
        /// 亲手做的那件事：把方程配平。拖的时候档次会在某个点跳一级，手感上要能扫过去看见。
        /// </summary>
        private static void HandleRedoxInput(PlanetFactory factory, int entityId, int[] state)
        {
            if (!Input.GetMouseButton(0))
            {
                _redoxClickLatch = false;
                _dragging = -1;

                return;
            }

            RedoxConfig cfg = RedoxRegistry.Config;

            int lo = cfg.ratioMin > 0 ? cfg.ratioMin : 70;
            int hi = cfg.ratioMax > 0 ? cfg.ratioMax : 130;

            // ── 第 2 行：配氧比滑条。按下那一帧抓住它，之后即使划出轨道也继续跟随 ──
            if (_dragging == 2 || (_dragging < 0 && Input.GetMouseButtonDown(0)
                                   && InRow(2, out Vector2 _)))
            {
                _dragging = 2;

                if (!UIRoot.ScreenPointIntoRect(Input.mousePosition, Rows[2].Track, out Vector2 p)) return;

                Rect r = Rows[2].Track.rect;

                if (r.width <= 0f) return;

                float f = Mathf.Clamp01((p.x - r.xMin) / r.width);

                int ratio = Mathf.Clamp(lo + Mathf.RoundToInt(f * (hi - lo)), lo, hi);

                if (ratio == state[2]) return;

                RedoxBurnerPatches.Apply(factory, entityId, new[] { state[0], state[1], ratio });

                return;
            }

            // ── 第 0 / 1 行：选料。左半格往前、右半格往后 ──
            if (_redoxClickLatch) return;

            var row = -1;
            Vector2 hit;

            if (InRow(0, out hit)) row = 0;
            else if (InRow(1, out hit)) row = 1;
            else return;

            _redoxClickLatch = true;

            List<RedoxRegistry.Agent> pool =
                row == 0 ? RedoxRegistry.Reducers : RedoxRegistry.Oxidizers;

            if (pool.Count == 0) return;

            Rect rect = Rows[row].Track.rect;
            int step = hit.x < rect.center.x ? -1 : 1;

            var at = 0;

            for (var i = 0; i < pool.Count; i++)
                if (pool[i].ItemId == state[row])
                    at = i;

            int picked = pool[((at + step) % pool.Count + pool.Count) % pool.Count].ItemId;

            var next = new[] { state[0], state[1], state[2] };
            next[row] = picked;

            RedoxBurnerPatches.Apply(factory, entityId, next);
        }

        /// <summary>
        /// 燃烧厂面板的显示。
        ///
        /// 结果行要同时写出<b>密度、档次、产量和当前发电功率</b>四个数，
        /// 因为它们分别对应玩家能动的三个量各自的后果：换还原剂改能量，
        /// 换氧化剂改配比，拖滑条两个都改。少写一个，玩家就看不出刚才那一下动了什么。
        ///
        /// 发电功率是<b>只读</b>的，而且必须从发电组件上现读——巨型建筑的三十个储物格
        /// 对玩家不可见（<c>MegaStationWindowPatches</c> 把 stationId 报成 0 让配方窗口顶上来），
        /// 燃料舱同样藏在里面。没有这一行，玩家没有任何途径知道它到底在不在发电。
        /// </summary>
        private static void RefreshRedox(PlanetFactory factory, int entityId, int[] state)
        {
            RedoxConfig cfg = RedoxRegistry.Config;

            _titleText.text = "氧化还原燃烧厂　配料与配氧比".Translate();

            _panelTrs.sizeDelta = new Vector2(0f, HeadHeight + 3 * RowHeight + FootHeight);

            for (var i = 0; i < MaxRows; i++)
            {
                var on = i < 3;

                if (Rows[i].Root.activeSelf != on) Rows[i].Root.SetActive(on);

                if (!on) continue;

                Rows[i].Root.transform.localPosition = new Vector3(0f, -(HeadHeight + i * RowHeight), 0f);
            }

            RedoxRegistry.Agent reducer = Find(RedoxRegistry.Reducers, state[0]);
            RedoxRegistry.Agent oxidizer = Find(RedoxRegistry.Oxidizers, state[1]);

            // 第 0 / 1 行：选料
            PickerRow(Rows[0], "还原剂".Translate(), reducer?.Name);
            PickerRow(Rows[1], "氧化剂".Translate(), oxidizer?.Name);

            // 第 2 行：配氧比滑条
            int lo = cfg.ratioMin > 0 ? cfg.ratioMin : 70;
            int hi = cfg.ratioMax > 0 ? cfg.ratioMax : 130;
            int ratio = Mathf.Clamp(state[2], lo, hi);

            Rows[2].Label.text = "配氧比".Translate();

            LayoutRow(Rows[2], false);

            float frac = hi > lo ? (ratio - lo) / (float)(hi - lo) : 0f;

            Rows[2].Fill.anchorMin = Vector2.zero;
            Rows[2].Fill.anchorMax = new Vector2(Mathf.Clamp01(frac), 1f);
            Rows[2].Fill.offsetMin = Vector2.zero;
            Rows[2].Fill.offsetMax = Vector2.zero;
            Rows[2].TrackImage.color = new Color(1f, 1f, 1f, 0.12f);
            Rows[2].Value.text = ratio + "%";

            _resultText.rectTransform.anchoredPosition =
                new Vector2(SidePad, -(HeadHeight + 3 * RowHeight + 4f));

            if (reducer == null || oxidizer == null)
            {
                _resultText.text = "配料未就绪".Translate();
                _resultText.color = new Color(0.95f, 0.7f, 0.5f);

                return;
            }

            float density = RedoxBurnerPatches.Density(reducer, oxidizer, ratio);
            int tier = RedoxBurnerPatches.TierIndex(density);
            int yield = RedoxBurnerPatches.Yield(reducer, tier, ratio);
            int oxCount = RedoxBurnerPatches.OxidizerCount(reducer, oxidizer, ratio);

            _resultText.text =
                $"{reducer.Name} ×{cfg.reducerParts} + {oxidizer.Name} ×{oxCount}"
                + $"  →  {RedoxRegistry.Tiers[tier].Entry.name} ×{yield}"
                + $"　{"能量密度".Translate()} {density:0.00} MJ/{"件".Translate()}"
                + $"　{"发电".Translate()} {Power(factory, entityId, false) / 1e9:0.##}"
                + $" / {Power(factory, entityId, true) / 1e9:0.##} GW";

            // 配平那一点是绿的，偏离就转暖色：滑条的最优位置要一眼看得出来
            _resultText.color = Mathf.Abs(ratio - 100) <= 2
                ? new Color(0.72f, 0.92f, 0.78f)
                : new Color(0.92f, 0.86f, 0.66f);
        }

        private static RedoxRegistry.Agent Find(List<RedoxRegistry.Agent> pool, int itemId)
        {
            for (var i = 0; i < pool.Count; i++)
                if (pool[i].ItemId == itemId)
                    return pool[i];

            return null;
        }

        /// <summary>选料行的通用摆法：轨道拉满、名字居中、◀ ▶ 落在左右两半。</summary>
        /// <summary>
        /// 点开一张清单的行。和 <see cref="PickerRow"/> 的区别只在<b>它长什么样</b>：
        /// <c>◀ 名字 ▶</c> 承诺的是「点两端会左右切换」，而这一行点哪里都是打开选择器，
        /// 所以写成 <c>名字 ▼</c>。控件形状要和它真实的交互对得上，否则玩家会先试错一轮。
        /// </summary>
        private static void DropdownRow(Row row, string label, string value)
        {
            row.Label.text = label;

            LayoutRow(row, true);

            row.Value.text = (value ?? "?") + "   ▼";
            row.Fill.anchorMin = Vector2.zero;
            row.Fill.anchorMax = new Vector2(0f, 1f);
            row.Fill.offsetMin = Vector2.zero;
            row.Fill.offsetMax = Vector2.zero;
            row.TrackImage.color = new Color(1f, 1f, 1f, 0.16f);
        }

        private static void PickerRow(Row row, string label, string value)
        {
            row.Label.text = label;

            LayoutRow(row, true);

            row.Value.text = "◀  " + (value ?? "?") + "  ▶";
            row.Fill.anchorMin = Vector2.zero;
            row.Fill.anchorMax = new Vector2(0f, 1f);
            row.Fill.offsetMin = Vector2.zero;
            row.Fill.offsetMax = Vector2.zero;
            row.TrackImage.color = new Color(1f, 1f, 1f, 0.12f);
        }

        /// <summary>
        /// 这台建筑的发电功率（瓦）。<paramref name="cap"/> 为真取<b>上限</b>，为假取<b>实发</b>。
        ///
        /// <b>两个都要显示，而且这一条是被一次误报逼出来的。</b>
        /// 早先只写 <c>generateCurrentTick</c>（电网真的取走了多少），结果面板上是
        /// 「发电 0.03 GW」而配置里写着 30 GW，看着像是数值或单位错了。
        /// 实际上两个数都对：<b>电网只取它当下需要的那么多</b>，所以一座空载的电厂
        /// 实发就是接近零——这正是 DSP 所有发电建筑的行为。
        ///
        /// 可是「实发远小于上限」还有第二个原因：<b>缺燃料</b>，那时 <c>capacityCurrentTick</c>
        /// 自己就会掉下来。只写实发的话，这两种情况在面板上长得一模一样，
        /// 而它们要的处理完全相反（一个不用管，一个要去看进料）。
        /// 并排写出「实发 / 上限」，两者才分得开。
        /// </summary>
        private static double Power(PlanetFactory factory, int entityId, bool cap)
        {
            if (factory?.entityPool == null || entityId <= 0 || entityId >= factory.entityPool.Length)
                return 0.0;

            int genId = factory.entityPool[entityId].powerGenId;

            if (genId <= 0 || factory.powerSystem?.genPool == null
                || genId >= factory.powerSystem.genPool.Length)
                return 0.0;

            ref PowerGeneratorComponent g = ref factory.powerSystem.genPool[genId];

            return (cap ? g.capacityCurrentTick : g.generateCurrentTick) * 60.0;
        }

        private static void RefreshCatalyst(int charge, int life, int stock, int spent)
        {
            CatalystConfig cfg = CatalystBedPatches.Config;
            int full = cfg != null && cfg.ticksPerCharge > 0 ? cfg.ticksPerCharge : 1;

            // **键必须就是中文原文。** I18N.ApplyLanguage 对中文那一侧写的是 `pair.Key` 本身
            // （非中文语言才取译文），所以键起成「xx面板标题」这种描述名的话，
            // 中文玩家看到的就是那五个字本身。
            _titleText.text = "催化剂床　装填与活性".Translate();

            const int rows = 3;

            _panelTrs.sizeDelta = new Vector2(0f, HeadHeight + rows * RowHeight + FootHeight);

            for (var i = 0; i < MaxRows; i++)
            {
                if (Rows[i] == null) continue;

                var on = i < rows;

                if (Rows[i].Root.activeSelf != on) Rows[i].Root.SetActive(on);

                if (!on) continue;

                Rows[i].Root.transform.localPosition = new Vector3(0f, -(HeadHeight + i * RowHeight), 0f);

                LayoutRow(Rows[i], false);

                Rows[i].TrackImage.color = new Color(1f, 1f, 1f, 0.06f);
            }

            // 第 0 行：床里装了多少
            Rows[0].Label.text = "床内装填".Translate();
            Rows[0].Value.text = charge > 0 ? charge.ToString() : "—";
            SetFill(Rows[0], charge > 0 ? 1f : 0f);

            // 第 1 行：活性。这是唯一一条真的会动的条
            Rows[1].Label.text = "剩余活性".Translate();
            Rows[1].Value.text = charge > 0 ? $"{life * 100f / full:0.#}%" : "—";
            SetFill(Rows[1], charge > 0 ? (float)life / full : 0f);

            // 第 2 行：待生仓。−1 是「这一格没排出来」，和「排出来了但空着」不是一回事
            Rows[2].Label.text = "待生仓".Translate();
            Rows[2].Value.text = spent < 0 ? "—" : spent.ToString();
            SetFill(Rows[2], 0f);

            _resultText.rectTransform.anchoredPosition =
                new Vector2(SidePad, -(HeadHeight + rows * RowHeight + 4f));

            string tail;
            Color tint;

            if (stock < 0 || spent < 0)
            {
                // 仓位没排出来：这条比「等催化剂」重要得多，它说明的是布局坏了而不是缺货
                tail = "储物格未就绪——催化剂进不来，去看布局".Translate();
                tint = new Color(0.95f, 0.55f, 0.45f);
            }
            else if (charge > 0)
            {
                tail = string.Format("还能跑 {0:0} 秒　　催化剂库存 {1}".Translate(), life / 60f, stock);
                tint = new Color(0.72f, 0.88f, 0.78f);
            }
            else
            {
                tail = string.Format("等待催化剂：库存 {0}，装一床需要 {1}".Translate(), stock,
                                     cfg != null ? cfg.chargeSize : 0);
                tint = new Color(0.95f, 0.80f, 0.45f);
            }

            _resultText.text = tail;
            _resultText.color = tint;
        }

        private static void SetFill(Row row, float t)
        {
            row.Fill.anchorMin = Vector2.zero;
            row.Fill.anchorMax = new Vector2(Mathf.Clamp01(t), 1f);
            row.Fill.offsetMin = Vector2.zero;
            row.Fill.offsetMax = Vector2.zero;
        }

        private static bool _compositeClickLatch;

        /// <summary>
        /// 复合材面板的输入。<b>两行两种交互</b>：
        ///
        /// 第 0 行选合金，用<b>点</b>不用拖——选的是离散的一种合金，
        /// 拖动会一路扫过中间那些值，每扫过一个都触发一次 Apply（弹药那边同理）。
        ///
        /// 第 1 行调配比，用<b>拖</b>不用点——它是连续量，而且拖到哪一段就出哪一级，
        /// 手感上要能一路扫过去看等级怎么变。
        /// </summary>
        private static void HandleCompositeInput(PlanetFactory factory, int entityId, int[] state)
        {
            if (!Input.GetMouseButton(0))
            {
                _compositeClickLatch = false;
                _dragging = -1;

                return;
            }

            List<int> pool = CompositeRegistry.Candidates;
            int total = CompositeRegistry.TotalParts;

            // ── 第 1 行：配比滑条。按下那一帧抓住它，之后即使划出轨道也继续跟随 ──
            if (_dragging == 1 || (_dragging < 0 && Input.GetMouseButtonDown(0)
                                   && InRow(1, out Vector2 _)))
            {
                _dragging = 1;

                if (!UIRoot.ScreenPointIntoRect(Input.mousePosition, Rows[1].Track, out Vector2 p)) return;

                Rect r = Rows[1].Track.rect;

                if (r.width <= 0f) return;

                float f = Mathf.Clamp01((p.x - r.xMin) / r.width);

                // 两端各留一份：全是基体或全是合金的配方没有意义
                int parts = Mathf.Clamp(1 + Mathf.RoundToInt(f * (total - 2)), 1, total - 1);

                if (parts == state[1]) return;

                var next = new[] { state[0], parts };

                if (!CompositePatches.Apply(factory, entityId, next)) return;

                AlloyRatioStore.SetPlayerDefault(CompositeRegistry.RecipeId, next);

                return;
            }

            // ── 第 0 行：选料。左半格往前、右半格往后 ──
            if (_compositeClickLatch || pool.Count == 0) return;

            if (!InRow(0, out Vector2 hit)) return;

            _compositeClickLatch = true;

            Rect rect = Rows[0].Track.rect;
            int step = hit.x < rect.center.x ? -1 : 1;
            int at = pool.IndexOf(state[0]);

            if (at < 0) at = 0;

            var pick = new[] { pool[((at + step) % pool.Count + pool.Count) % pool.Count], state[1] };

            if (!CompositePatches.Apply(factory, entityId, pick)) return;

            AlloyRatioStore.SetPlayerDefault(CompositeRegistry.RecipeId, pick);
        }

        /// <summary>鼠标是不是落在第 i 行的轨道里。</summary>
        private static bool InRow(int i, out Vector2 hit)
        {
            hit = Vector2.zero;

            if (Rows[i]?.Track == null) return false;
            if (!UIRoot.ScreenPointIntoRect(Input.mousePosition, Rows[i].Track, out hit)) return false;

            Rect rect = Rows[i].Track.rect;

            return hit.x >= rect.xMin && hit.x <= rect.xMax && hit.y >= rect.yMin && hit.y <= rect.yMax;
        }

        private static void RefreshComposite(int[] state)
        {
            _titleText.text = "活性复合材面板标题".Translate();

            _panelTrs.sizeDelta = new Vector2(0f, HeadHeight + 2 * RowHeight + FootHeight);

            int total = CompositeRegistry.TotalParts;
            int parts = Mathf.Clamp(state[1], 1, total - 1);

            for (var i = 0; i < MaxRows; i++)
            {
                var on = i < 2;

                if (Rows[i].Root.activeSelf != on) Rows[i].Root.SetActive(on);

                if (!on) continue;

                Rows[i].Root.transform.localPosition = new Vector3(0f, -(HeadHeight + i * RowHeight), 0f);
            }

            // 第 0 行：选料
            Rows[0].Label.text = "填料合金".Translate();

            LayoutRow(Rows[0], true);

            ItemProto alloy = LDB.items.Select(state[0]);

            Rows[0].Value.text = "◀  " + (alloy != null ? alloy.name : "?") + "  ▶";
            Rows[0].Fill.anchorMin = Vector2.zero;
            Rows[0].Fill.anchorMax = new Vector2(0f, 1f);
            Rows[0].Fill.offsetMin = Vector2.zero;
            Rows[0].Fill.offsetMax = Vector2.zero;
            Rows[0].TrackImage.color = new Color(1f, 1f, 1f, 0.12f);

            // 第 1 行：配比滑条
            Rows[1].Label.text = "合金配比".Translate();

            LayoutRow(Rows[1], false);

            float frac = total > 2 ? (parts - 1) / (float)(total - 2) : 0f;

            Rows[1].Fill.anchorMin = Vector2.zero;
            Rows[1].Fill.anchorMax = new Vector2(Mathf.Clamp01(frac), 1f);
            Rows[1].Fill.offsetMin = Vector2.zero;
            Rows[1].Fill.offsetMax = Vector2.zero;
            Rows[1].TrackImage.color = new Color(1f, 1f, 1f, 0.12f);
            Rows[1].Value.text = parts + " / " + total;

            _resultText.rectTransform.anchoredPosition =
                new Vector2(SidePad, -(HeadHeight + 2 * RowHeight + 4f));

            int gi = CompositeRegistry.GradeIndex(parts);
            CompositeRegistry.Grade grade = CompositeRegistry.Grades[gi];

            CompositePatches.Mix(state[0], CompositePatches.Vf(parts),
                out float h, out float t, out float c, out float e);

            _resultText.text =
                $"{AlloyRatioPatches.AxisName("hardness")} {h:0.0}  {AlloyRatioPatches.AxisName("toughness")} {t:0.0}  "
                + $"{AlloyRatioPatches.AxisName("corrosion")} {c:0.0}  {AlloyRatioPatches.AxisName("conductivity")} {e:0.0}"
                + $"  →  {grade.Entry.name} ×{CompositePatches.Yield(state[0])}";

            _resultText.color = new Color(0.72f, 0.92f, 0.78f);
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
