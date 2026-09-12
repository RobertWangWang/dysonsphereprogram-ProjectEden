// 本文件移植自 ProjectGenesis（创世之书），属于其衍生作品。
// Portions of this file are derived from ProjectGenesis (GenesisBook).
//
//     Copyright (C) 2022-2026 Awbugl and ProjectGenesis contributors
//     https://github.com/Awbugl/ProjectGenesis
//
// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 物流站扩容到 30 格。格数本身改 prefabDesc.stationMaxItemKinds 就行，
    /// 但原版有三处按固定格数写死，不一起处理的话扩容等于自毁：
    ///
    ///   1. StationComponent.AddItem 和四个供需查询被完全展开成 storage[0]…storage[5]，
    ///      第 7 格往后货物入库直接 return 0（凭空消失），供需对调度也不可见；
    ///   2. UIStationWindow 只有 6 个储物格控件，且窗口高度按全部格数算，30 格会撑出屏幕；
    ///   3. UIEntityBriefInfo 的 icons 是定长数组，悬停时按格数索引会越界。
    ///
    /// 做法整体移植自 ProjectGenesis 的 StationExpandPatches。
    /// </summary>
    [HarmonyPatch]
    internal static class StationExpandPatches
    {
        private static StationsConfig Config => ProjectEdenPlugin.StationsConfig;

        private static int MaxKinds => Config?.stationMaxItemKinds ?? 0;

        /// <summary>原版展开到 storage[5]，格数不超过 6 时无需接管。</summary>
        private const int VanillaSlots = 6;

        private const float RowHeight = 76f;

        /// <summary>悬停信息框按整行清空图标，每行 4 个，所以要向上取整到 4 的倍数。</summary>
        private static int RequiredIconCount => (MaxKinds + 3) / 4 * 4;

        /// <summary>
        /// <c>UIEntityBriefInfo.icons</c> 被扩到了能装下多少格。<b>0 = 没有扩容</b>。
        ///
        /// 别的地方（巨型建筑的储物格布局）要靠它判断自己能安全铺到第几格——
        /// 那个数组是 prefab 里的定长数组，悬停信息框按储物格种类数遍历它，超出即越界。
        /// 这里扩容之后上限就跟着抬了，所以不该再有人把 5 写死。
        /// </summary>
        internal static int ExpandedIconKinds => MaxKinds > VanillaSlots ? MaxKinds : 0;

        // ── 一、全格位查找 ──────────────────────────────────────

        /// <summary>
        /// 运输机到站靠 AddItem 入库，而原版把它展开成了六个 if，
        /// 第 7 格往后一个都匹配不上直接 return 0——货物凭空消失。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.AddItem))]
        private static bool StationComponent_AddItem(StationComponent __instance, int itemId, int count, int inc,
            ref int __result)
        {
            StationStore[] storage = __instance.storage;

            if (storage == null || storage.Length <= VanillaSlots) return true;

            __result = 0;

            if (itemId <= 0) return false;

            lock (storage)
            {
                for (var i = 0; i < storage.Length; i++)
                {
                    if (storage[i].itemId != itemId) continue;

                    storage[i].count += count;
                    storage[i].inc += inc;
                    __result = count;

                    return false;
                }
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.HasLocalSupply))]
        private static bool StationComponent_HasLocalSupply(StationComponent __instance, int itemId, int countAtLeast,
            ref int __result) => FindSlot(__instance, itemId, countAtLeast, true, true, ref __result);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.HasLocalDemand))]
        private static bool StationComponent_HasLocalDemand(StationComponent __instance, int itemId, int countAtLeast,
            ref int __result) => FindSlot(__instance, itemId, countAtLeast, true, false, ref __result);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.HasRemoteSupply))]
        private static bool StationComponent_HasRemoteSupply(StationComponent __instance, int itemId, int countAtLeast,
            ref int __result) => FindSlot(__instance, itemId, countAtLeast, false, true, ref __result);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationComponent), nameof(StationComponent.HasRemoteDemand))]
        private static bool StationComponent_HasRemoteDemand(StationComponent __instance, int itemId, int countAtLeast,
            ref int __result) => FindSlot(__instance, itemId, countAtLeast, false, false, ref __result);

        /// <summary>供应看存量够不够，需求看空间够不够——与原版四个方法的判断一致。返回格位下标，找不到为 -1。</summary>
        private static bool FindSlot(StationComponent station, int itemId, int countAtLeast, bool local, bool supply,
            ref int result)
        {
            StationStore[] storage = station.storage;

            if (storage == null || storage.Length <= VanillaSlots) return true;

            result = -1;

            for (var i = 0; i < storage.Length; i++)
            {
                if (storage[i].itemId != itemId) continue;

                ELogisticStorage logic = local ? storage[i].localLogic : storage[i].remoteLogic;

                if (logic != (supply ? ELogisticStorage.Supply : ELogisticStorage.Demand)) continue;

                if (supply
                        ? storage[i].count < countAtLeast
                        : storage[i].max - storage[i].count < countAtLeast)
                    continue;

                result = i;

                return false;
            }

            return false;
        }

        // ── 二、悬停信息框 ──────────────────────────────────────

        /// <summary>
        /// 悬停信息框会把格数向上取整到整行再拿去索引 icons，而 icons 是 prefab 里的定长数组。
        /// 在 _OnCreate 之前扩容即可——原版 _OnCreate 里的 for 循环会顺带把新槽位实例化出来。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIEntityBriefInfo), nameof(UIEntityBriefInfo._OnCreate))]
        private static void UIEntityBriefInfo_OnCreate(UIEntityBriefInfo __instance)
        {
            int required = RequiredIconCount;

            if (required <= 0 || __instance.icons == null || __instance.icons.Length >= required) return;

            Array.Resize(ref __instance.icons, required);
        }

        // ── 三、面板翻页与滚动条 ────────────────────────────────

        private static int _currentPage, _lastStationId, _currentMaxPage;

        private static Scrollbar _pageScrollbar;

        /// <summary>回写 Scrollbar.value 时置位，避免 onValueChanged 反过来又改页码。</summary>
        private static bool _syncingScrollbar;

        /// <summary>
        /// 按「我们实际画几行」和「原版以为要画几行」的<b>差值</b>修正窗口高度。
        ///
        /// 原版 <c>RefreshTrans</c> 每帧自己算一遍高度，用的行数是（IL 00B2–00D4）：
        ///
        /// <code>
        /// slots = (isCollector || isVeinCollector) ? collectionIds.Length : storage.Length;
        /// windowTrans.sizeDelta = new Vector2(x, 100 + 76 * slots + 36);
        /// </code>
        ///
        /// <b>两个方向都要修，而这一版之前只修了一个方向。</b>
        /// 30 格物流站是「原版以为 30 行、我们只画 5 行」，高度会撑到屏幕外，要缩；
        /// 大型采矿机是<b>反过来</b>——原版按 <c>collectionIds.Length</c> 算，那是 1，
        /// 而我们要画矿石加钻头两行，得<b>长</b>一行，否则第二行溢出窗口。
        /// 旧代码写的是 <c>if (count &lt;= visible) return;</c>，采矿机走的正是这条 return。
        ///
        /// 差值写法把两种情况统一了，而且对气体采集器恒等于 0（我们和原版数出来的行数一样），
        /// 所以不会去碰一个本来就正确的布局。行高 76 是原版自己的常数，见上面那段 IL。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIStationWindow), "RefreshTrans")]
        private static void UIStationWindow_RefreshTrans(UIStationWindow __instance, StationComponent station)
        {
            if (station == null) return;

            int shown = StorageCount(station);
            int visible = VisibleRows(__instance);

            // 控件就这么多，再多的格子靠翻页，所以实际画出来的行数封顶在控件数
            if (shown > visible) shown = visible;

            int assumed = VanillaRowCount(station);
            int delta = shown - assumed;

            if (delta == 0) return;

            // 多画行时再多留一点空隙。见下面 PanelClearance 的推导
            float grow = RowHeight * delta + (delta > 0 ? PanelClearance : 0f);

            Vector2 size = __instance.windowTrans.sizeDelta;

            __instance.windowTrans.sizeDelta = new Vector2(size.x, size.y + grow);

        }


        /// <summary>
        /// 多画储物格行时，窗口在「一行的高度」之外再多留的空隙。
        ///
        /// <b>为什么不需要去移动窗口下半块——这块布局改错了三次，结论和三次的方向都相反。</b>
        /// 实测（<c>UIStationWindow</c> 的真实 rect，不是从截图估的）：
        ///
        /// <code>
        /// 窗口     sizeDelta=(600, 456)              世界上下沿 54.4 / 49.9
        /// 最后一行 anchored=(40, -266) 高 70          世界上下沿 51.8 / 51.1
        /// 下半块   anchored=(0, 80)   高 150          世界上下沿 52.2 / 50.7
        ///          pivot=(0.5, 0)  anchorMin=(0,0)  anchorMax=(1,0)
        /// </code>
        ///
        /// 三件事一目了然，而且每一件都推翻了之前的一次改动：
        ///
        /// <list type="number">
        /// <item><b>下半块贴的是窗口底边</b>（<c>anchorMin/Max.y = 0</c>，<c>pivot.y = 0</c>），
        /// 而储物格贴的是顶边。<b>窗口一长高，两者自动分开</b>——
        /// 手动推它是多余的，三次改动修的是一个不该修的东西。</item>
        /// <item><b>它的矩形顶边不是可见内容的顶边。</b> 反推原版单行布局：
        /// 窗口 380 时第 0 行底边在底上方 120，而下半块矩形顶边在 230 ——
        /// <b>矩形上面约 110 单位是空白</b>。所以「量矩形重叠」必然多算 110，
        /// 那正是第三版把整块推出窗口的原因（110 + 间隙 &gt; 80，y 变负，掉到窗口外面）。</item>
        /// <item>窗口长高一行（456）之后，最后一行底边在底上方 121.6，
        /// 而下半块可见内容顶边 ≈ <c>y + 150 − 110 = 120</c>。
        /// <b>120 对 121.6，本来就是贴着的</b>——原版那套锚点自己就把事情办对了。</item>
        /// </list>
        ///
        /// 既然只差一点点，就把那一点点加在窗口高度上，而不是去动别人的位置：
        /// 多长 16 个单位，可见内容和最后一行之间就有约 16 的空隙。
        /// <b>这个数是从上面那组实测反推出来的，不是试出来的。</b>
        ///
        /// 过程上值得记一笔：这块布局试错三次，每一次都是从截图估数——
        /// 而截图既分不清「矩形边」和「可见内容边」，也读不出画布缩放，
        /// 那两样恰恰是三次全错的原因。<b>该在第二次就去打日志。</b>
        /// </summary>
        private const float PanelClearance = 16f;

        /// <summary>
        /// 原版 <c>RefreshTrans</c> 自己数出来的行数，逐字照抄它的判据（IL 00B2–00D4），
        /// 好让差值真的是差值。<b>别改成 <see cref="StorageCount"/></b>——
        /// 那个是「我们要画几行」，这个是「原版以为要画几行」，两者不同正是要修的原因。
        /// </summary>
        private static int VanillaRowCount(StationComponent station)
        {
            if (station.isCollector || station.isVeinCollector) return station.collectionIds?.Length ?? 0;

            return station.storage?.Length ?? 0;
        }

        /// <summary>
        /// 储物格控件只有 6 个（原版 _OnCreate 写死数量并钉在窗口上），格数更多时用翻页。
        /// 控件靠 index 字段绑定到 storage[index]，所以只改 index，完全不动布局。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIStationWindow), "_OnUpdate")]
        private static void UIStationWindow_OnUpdate(UIStationWindow __instance)
        {
            if (__instance.stationId == 0 || __instance.transport?.stationPool == null) return;
            if (__instance.stationId >= __instance.transport.stationPool.Length) return;

            StationComponent station = __instance.transport.stationPool[__instance.stationId];

            if (station == null || station.id != __instance.stationId) return;

            UIStationStorage[] uis = __instance.storageUIs;

            if (uis == null || uis.Length == 0) return;

            int count = StorageCount(station);

            if (count <= 0) return;

            if (__instance.stationId != _lastStationId)
            {
                _lastStationId = __instance.stationId;
                _currentPage = 0;
            }

            int maxPage = (count - 1) / uis.Length;

            if (maxPage > 0) _currentPage += PageStep(__instance);

            _currentMaxPage = maxPage;

            EnsureScrollbar(__instance);

            if (_currentPage < 0) _currentPage = 0;
            if (_currentPage > maxPage) _currentPage = maxPage;

            for (var i = 0; i < uis.Length; i++)
            {
                int index = _currentPage * uis.Length + i;

                if (index < count)
                {
                    if (uis[i].station == station && uis[i].index == index) continue;

                    uis[i].station = station;
                    uis[i].index = index;
                    uis[i]._Open();
                }
                else if (uis[i].station != null)
                {
                    uis[i].station = null;
                    uis[i].index = 0;
                    uis[i]._Close();
                }
            }

            LayoutVeinCollectorRows(station, uis, count);

            SyncScrollbar(maxPage);
        }

        /// <summary>
        /// 矿脉采集器的储物格行要自己摆位置，因为<b>原版在这条分支里不摆</b>。
        ///
        /// <c>UIStationWindow.OnStationIdChange</c> IL 08A4：
        ///
        /// <code>
        /// if (!station.isVeinCollector) {
        ///     storageUIs[0].rectTransform.anchoredPosition = new Vector2(40f, -90f);
        ///     veinCollectorPanel._Close();
        /// }
        /// </code>
        ///
        /// 采集器只画一行，位置无所谓，所以原版直接跳过了这段。
        /// 等本 mod 把钻头槽那一行也画出来，第二行就停在 prefab 的老位置上，
        /// 和第一行叠在一起——**看起来像渲染故障，其实是没人负责摆它**。
        /// 这和 <see cref="MultiProductUIPatches"/> 记的是同一条：
        /// <b>原版<u>不</u>重写的那部分，才是需要自己复位的部分</b>，
        /// 而这要靠读原版路径里的<b>写入</b>、不是读取，才看得出来。
        ///
        /// <b>行高 76 不是量出来的，是原版自己的常数。</b> 同一个方法 IL 08FF
        /// 在算采集器窗口高度时就是 <c>76 * 槽数 + 136</c>，
        /// <see cref="RowHeight"/> 用的就是这个数。
        ///
        /// 只对采集器做。普通物流站那条分支原版摆得好好的，碰它只会引入回归。
        /// </summary>
        private static void LayoutVeinCollectorRows(StationComponent station, UIStationStorage[] uis, int count)
        {
            // <b>只管矿脉采集器。</b> 气体采集器走的是上面那个 !isVeinCollector 分支，
            // 原版会给它摆位置，而且它在原版里本来就会显示多行（气态巨星有好几种气体）——
            // 也就是说那条路径的行距是经过验证的，碰它只会引入回归。
            if (!station.isVeinCollector) return;
            if (uis.Length < 2 || uis[0] == null) return;

            var first = uis[0].transform as RectTransform;

            if (first == null) return;

            Vector2 origin = first.anchoredPosition;

            for (var i = 1; i < uis.Length; i++)
            {
                if (uis[i] == null) continue;

                var trs = uis[i].transform as RectTransform;

                if (trs == null) continue;

                var want = new Vector2(origin.x, origin.y - RowHeight * i);

                if (trs.anchoredPosition != want) trs.anchoredPosition = want;
            }
        }

        /// <summary>
        /// 储物格布局仍是原版那 6 个控件，这里只额外挂一根滚动条来驱动页码。
        /// 它是独立控件，不需要 Mask / Viewport / Content 那套层级，不影响储物格渲染。
        /// 等到 _OnUpdate 才创建，是因为那时储物格的 rect 已经算好，可以据此定位。
        /// </summary>
        private static void EnsureScrollbar(UIStationWindow window)
        {
            if (_pageScrollbar != null) return;

            UIStationStorage[] uis = window.storageUIs;

            if (uis == null || uis.Length == 0 || uis[0] == null) return;

            var storageTrs = (RectTransform)uis[0].transform;
            Rect storageRect = storageTrs.rect;

            if (storageRect.width <= 0f) return;

            var barGO = new GameObject("projecteden-page-scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));

            barGO.transform.SetParent(storageTrs.parent, false);

            var barTrs = (RectTransform)barGO.transform;

            barTrs.anchorMin = new Vector2(0f, 1f);
            barTrs.anchorMax = new Vector2(0f, 1f);
            barTrs.pivot = new Vector2(0f, 1f);

            // 贴在储物格右侧，纵向覆盖全部可见行
            barTrs.anchoredPosition =
                new Vector2(storageTrs.anchoredPosition.x + storageRect.width + 4f, storageTrs.anchoredPosition.y);
            barTrs.sizeDelta = new Vector2(10f, RowHeight * uis.Length);

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

            handleImage.color = new Color(0.62f, 0.84f, 1f, 0.55f);

            _pageScrollbar = barGO.GetComponent<Scrollbar>();
            _pageScrollbar.direction = Scrollbar.Direction.TopToBottom;
            _pageScrollbar.handleRect = handleTrs;
            _pageScrollbar.targetGraphic = handleImage;
            _pageScrollbar.onValueChanged.AddListener(OnScrollbarValueChanged);

            ProjectEdenPlugin.Log.LogInfo($"物流站面板已加入页码滚动条（每页 {uis.Length} 格）");
        }

        private static void SyncScrollbar(int maxPage)
        {
            if (_pageScrollbar == null) return;

            bool show = maxPage > 0;

            if (_pageScrollbar.gameObject.activeSelf != show) _pageScrollbar.gameObject.SetActive(show);

            if (!show) return;

            _syncingScrollbar = true;

            _pageScrollbar.numberOfSteps = maxPage + 1;
            _pageScrollbar.size = 1f / (maxPage + 1);
            _pageScrollbar.value = (float)_currentPage / maxPage;

            _syncingScrollbar = false;
        }

        private static void OnScrollbarValueChanged(float value)
        {
            if (_syncingScrollbar || _currentMaxPage <= 0) return;

            _currentPage = Mathf.RoundToInt(value * _currentMaxPage);
        }

        /// <summary>这一帧要翻几页。滚轮优先，另给一组键盘快捷键兜底。</summary>
        private static int PageStep(UIStationWindow window)
        {
            if (Input.GetKeyDown(KeyCode.PageDown)) return 1;
            if (Input.GetKeyDown(KeyCode.PageUp)) return -1;

            // VFInput.mouseWheel 在输入框聚焦时会被归零，正好避免改容量数字时误翻页；
            // 它若一直是 0（被其它 UI 消费掉），退回读原始滚轮值
            float scroll = VFInput.mouseWheel;

            if (scroll == 0f) scroll = Input.mouseScrollDelta.y;
            if (scroll == 0f) return 0;

            return MouseOverWindow(window) ? scroll > 0f ? -1 : 1 : 0;
        }

        private static bool MouseOverWindow(UIStationWindow window)
        {
            RectTransform trs = window.windowTrans;

            if (trs == null) return false;

            // 画布不是 Overlay 模式时必须把相机传进去，否则命中判定永远为 false
            var canvas = window.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;

            return RectTransformUtility.RectangleContainsScreenPoint(trs, Input.mousePosition, camera);
        }

        private static int VisibleRows(UIStationWindow window)
        {
            UIStationStorage[] uis = window.storageUIs;

            return uis == null || uis.Length == 0 ? VanillaSlots : uis.Length;
        }

        /// <summary>
        /// 窗口要画几行储物格。
        ///
        /// <b>采集器不按 <c>storage.Length</c> 走</b>，因为 <c>StationComponent.Init</c>
        /// 的采集分支只铺到 <c>collectionIds.Length</c>，后面的格子是<b>没初始化过的</b>
        /// （<c>max</c> 为 0，装不下东西）。按数组长度画就会给气体采集器凭空多出一堆死格子，
        /// 顺带把 30 格物流站那套翻页界面拖到它身上来——那是 GasCollectorPatches 记着的老教训。
        ///
        /// <b>但「采集种类数」也不对，它正是钻头槽画不出来的原因。</b>
        /// 大型采矿机的 <c>collectionIds.Length</c> 是 1，于是无论
        /// <see cref="AlienVeinPatches"/> 把第 1 格布置成什么样，这里都只报 1 行，
        /// 窗口永远只有矿石那一行。玩家看到的就是「格子加了，界面上没有」。
        ///
        /// 所以判据改成<b>这一格能不能用</b>：采集种类数之后的格子，
        /// 只有被人为给过容量的才算。这条对原版采集器是恒等的——
        /// 没有任何东西会写它们多余格子的 <c>max</c>
        /// （<c>StationCapacityPatches</c> 只认 stations.json 的 itemIds 加巨型建筑，
        /// 采矿机和气体采集器都不在里面），所以它们仍然只画采集种类数那么多行。
        /// </summary>
        private static int StorageCount(StationComponent station)
        {
            StationStore[] storage = station.storage;

            if (!station.isCollector && !station.isVeinCollector) return storage?.Length ?? 0;

            int collect = station.collectionIds?.Length ?? 0;

            if (storage == null) return collect;
            if (collect > storage.Length) collect = storage.Length;

            // 从后往前找最后一个「有容量」的格子，中间的空格一并画出来，
            // 否则行号和 storage 下标会对不上
            for (int i = storage.Length - 1; i >= collect; i--)
                if (storage[i].max > 0)
                    return i + 1;

            return collect;
        }

        // ── 四、存档兼容 ────────────────────────────────────────

        /// <summary>
        /// storage 数组是建造时按 prefabDesc.stationMaxItemKinds 分配、随存档序列化的，
        /// 调大格数不影响已经建好的站，读档后补齐。新格子保持默认值，与原版空格一致。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
        private static void GameData_Import(GameData __instance)
        {
            if (MaxKinds <= VanillaSlots) return;

            PlanetFactory[] factories = __instance.factories;

            if (factories == null) return;

            var expanded = 0;

            for (var i = 0; i < __instance.factoryCount; i++)
            {
                PlanetFactory factory = factories[i];
                PlanetTransport transport = factory?.transport;

                if (transport?.stationPool == null) continue;

                for (var j = 1; j < transport.stationCursor; j++)
                {
                    StationComponent station = transport.stationPool[j];

                    if (station?.storage == null || station.id != j) continue;
                    if (station.isCollector || station.isVeinCollector) continue;

                    ModelProto model = LDB.models.Select(factory.entityPool[station.entityId].modelIndex);

                    if (model?.prefabDesc == null) continue;

                    int kinds = model.prefabDesc.stationMaxItemKinds;

                    if (station.storage.Length >= kinds) continue;

                    Array.Resize(ref station.storage, kinds);

                    if (station.priorityLocks != null && station.priorityLocks.Length < kinds)
                        Array.Resize(ref station.priorityLocks, kinds);

                    expanded++;
                }
            }

            if (expanded > 0) ProjectEdenPlugin.Log.LogInfo($"已把 {expanded} 个已建成物流站的储物格扩到 {MaxKinds} 格");
        }
    }
}
