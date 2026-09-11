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
        /// 原版 RefreshTrans 每帧按「全部格数 × 行高」算窗口高度，30 格会撑到屏幕外。
        /// 从算好的高度里扣掉放不下的行数，原版随集装科技变化的那套基础计算就都保留了。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIStationWindow), "RefreshTrans")]
        private static void UIStationWindow_RefreshTrans(UIStationWindow __instance, StationComponent station)
        {
            if (station == null) return;

            int count = StorageCount(station);
            int visible = VisibleRows(__instance);

            if (count <= visible) return;

            Vector2 size = __instance.windowTrans.sizeDelta;

            __instance.windowTrans.sizeDelta = new Vector2(size.x, size.y - RowHeight * (count - visible));
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

            SyncScrollbar(maxPage);
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

        private static int StorageCount(StationComponent station)
        {
            // 采集器类的格数取决于可采集物种类，不按 prefabDesc 走
            if (station.isCollector || station.isVeinCollector) return station.collectionIds?.Length ?? 0;

            return station.storage?.Length ?? 0;
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
