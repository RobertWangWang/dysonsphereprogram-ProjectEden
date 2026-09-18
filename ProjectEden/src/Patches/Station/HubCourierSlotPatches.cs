using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 在综合物流枢纽的物流站面板上补一个<b>配送运输机</b>格子，长得和运输机 / 运输船那两格一样：
    /// 图标显示数量、点图标放入或取出、<b>右上角那个小箭头切换自动补充</b>。
    ///
    /// 原版这个格子长在配送器面板（UIDispenserWindow）上，而枢纽固定打开的是物流站面板
    /// （三个面板互相顶，见 HubCourierPatches），所以那个格子玩家够不着——
    /// 表现就是配送运输机永远 0 架、一动不动。
    ///
    /// <b>克隆的是运输机那格（droneBox），不是曲速器那格。</b> 第一版克隆了曲速器，
    /// 结果没有那个小箭头——曲速器本来就没有自动补充按钮，只有运输机和运输船有。
    ///
    /// 克隆件里怎么认出「哪个是图标按钮、哪个是箭头」：<b>按它在原件里的下标找</b>。
    /// Instantiate 保留层级顺序，所以 GetComponentsInChildren 在原件和克隆件上返回的顺序一致，
    /// 记下原件里 droneIconButton / droneAutoReplenishButton 的下标，套到克隆件上即可——
    /// 比按名字或尺寸猜稳得多。
    ///
    /// UIButton.onClick 是 C# 的 <c>Action&lt;int&gt;</c> 事件，运行时订阅不随 GameObject 克隆走，
    /// 所以克隆件是「长得一样但没接线」的空壳，直接 BindOnClickSafe 绑自己的即可。
    ///
    /// 放入/取出的规则照抄 UIDispenserWindow.OnCourierIconClick，自动补充那半照抄
    /// UIStationWindow.OnDroneAutoReplenishButtonClick。
    /// </summary>
    [HarmonyPatch]
    internal static class HubCourierSlotPatches
    {
        /// <summary>配送运输机的物品 ID，原版 UIDispenserWindow 里写死的那个。</summary>
        private const int CourierItemId = 5003;

        /// <summary>新格子和能量条之间留的空隙（像素）。</summary>
        private const float Margin = 16f;

        private static GameObject _box;
        private static Text _countText;
        private static UIButton _iconButton;
        private static UIButton _replenishButton;
        private static bool _frameCaptured;
        private static bool _frameStretched;
        private static bool _frameShifted;
        private static float _frameOriginalLeft;
        private static float _frameOriginalWidth;

        /// <summary>外框被推窄之前的<b>实际</b>宽度（实测 240），以及这一次推了多少像素。</summary>
        private static float _frameOriginalRect;

        private static float _frameShift;
        private static int _shiftLoggedOnce;
        private static int _restoredOnce;
        private static bool _dumped;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIStationWindow), "_OnUpdate")]
        private static void UIStationWindow_OnUpdate(UIStationWindow __instance)
        {
            DispenserComponent dispenser = Dispenser(__instance);

            // 不是枢纽就把格子藏起来，<b>并且把能量条还原</b>——同一个面板也会被原版物流站用
            if (dispenser == null)
            {
                if (_box != null) _box.SetActive(false);

                RestoreEnergyBar(__instance);

                return;
            }

            if (!EnsureBox(__instance)) return;

            _box.SetActive(true);

            // <b>推窄和还原必须成对，而且不能挂在「格子创建」这个一次性事件上。</b>
            // EnsureBox 第一句就是 if (_box != null) return true，所以推窄一辈子只做一次；
            // 而还原每次打开普通物流站面板都会发生。1.10.1 第一版就是这样，实测日志里
            // 只有一条「让出 92 像素」，在那之后的「已把能量条还原」之后再没有第二条——
            // 表现是配送运输机格子直接压在满宽的能量条上。
            if (!_frameShifted) ShiftEnergyBar(__instance);

            int max = MaxCourier(__instance, dispenser);
            int have = dispenser.idleCourierCount + dispenser.workCourierCount;

            if (_countText != null) _countText.text = $"{have}/{max}";

            // 箭头亮不亮跟着自动补充的开关走，和运输机那格一致
            if (_replenishButton != null) _replenishButton.highlighted = dispenser.courierAutoReplenish;

            // 原版这一帧刚按写死的 240 把电量读数摆过，按推窄之后的实际宽度拉回来
            FitEnergyText(__instance);
        }

        /// <summary>这个面板对应的建筑上有没有配送器（也就是是不是本 mod 的枢纽）。</summary>
        private static DispenserComponent Dispenser(UIStationWindow window)
        {
            if (window.stationId <= 0) return null;

            PlanetFactory factory = window.factory;
            PlanetTransport transport = window.transport;

            if (factory?.entityPool == null || transport?.stationPool == null) return null;
            if (window.stationId >= transport.stationPool.Length) return null;

            StationComponent station = transport.stationPool[window.stationId];

            if (station == null || station.id != window.stationId) return null;

            int entityId = station.entityId;

            if (entityId <= 0 || entityId >= factory.entityPool.Length) return null;

            int dispenserId = factory.entityPool[entityId].dispenserId;

            if (dispenserId <= 0 || transport.dispenserPool == null) return null;
            if (dispenserId >= transport.dispenserPool.Length) return null;

            DispenserComponent dispenser = transport.dispenserPool[dispenserId];

            return dispenser != null && dispenser.id == dispenserId ? dispenser : null;
        }

        private static int MaxCourier(UIStationWindow window, DispenserComponent dispenser)
        {
            ItemProto proto = LDB.items.Select(window.factory.entityPool[dispenser.entityId].protoId);

            return proto?.prefabDesc?.dispenserMaxCourierCount ?? 10;
        }

        // ── 克隆运输机那格做出配送运输机格子 ──────────────────

        private static bool EnsureBox(UIStationWindow window)
        {
            if (_box != null) return true;

            GameObject source = window.droneBox;

            var first = window.droneBox?.transform as RectTransform;
            var second = window.shipBox?.transform as RectTransform;
            var last = window.warperBox?.transform as RectTransform;

            if (source == null || first == null || second == null || last == null) return false;

            RectTransform parent = last.parent as RectTransform;

            if (parent == null) return false;

            // 原件里图标按钮和箭头各排第几个——Instantiate 保留层级顺序，克隆件里下标相同
            int iconIndex = IndexOf(source.GetComponentsInChildren<UIButton>(true), window.droneIconButton);
            int arrowIndex = IndexOf(source.GetComponentsInChildren<UIButton>(true), window.droneAutoReplenishButton);
            int textIndex = IndexOf(source.GetComponentsInChildren<Text>(true), window.droneCountText);
            int imageIndex = IndexOf(source.GetComponentsInChildren<Image>(true), window.droneIconImage);

            if (iconIndex < 0 || arrowIndex < 0)
            {
                ProjectEdenPlugin.Log.LogError("配送运输机格子：在运输机那格里认不出图标按钮/箭头，格子未创建");

                return false;
            }

            _box = Object.Instantiate(source, parent, false);
            _box.name = "projecteden-courier-box";

            var trs = (RectTransform)_box.transform;

            // 摆在曲速器那格右边，间距照抄运输机→运输船
            float gap = second.anchoredPosition.x - first.anchoredPosition.x;

            trs.anchorMin = last.anchorMin;
            trs.anchorMax = last.anchorMax;
            trs.pivot = last.pivot;
            trs.sizeDelta = last.sizeDelta;
            trs.anchoredPosition = last.anchoredPosition + new Vector2(gap, 0f);

            UIButton[] buttons = _box.GetComponentsInChildren<UIButton>(true);
            Text[] texts = _box.GetComponentsInChildren<Text>(true);
            Image[] images = _box.GetComponentsInChildren<Image>(true);

            _iconButton = iconIndex < buttons.Length ? buttons[iconIndex] : null;
            _replenishButton = arrowIndex < buttons.Length ? buttons[arrowIndex] : null;
            _countText = textIndex >= 0 && textIndex < texts.Length ? texts[textIndex] : null;

            Sprite sprite = LDB.items.Select(CourierItemId)?.iconSprite;

            if (imageIndex >= 0 && imageIndex < images.Length && sprite != null) images[imageIndex].sprite = sprite;

            // 克隆件不带运行时订阅（onClick 是 C# 事件，不随 GameObject 复制），
            // 所以这里是往空事件上绑，不用先拆原版的接线
            if (_iconButton != null)
            {
                _iconButton.BindOnClickSafe(OnCourierClick);
                _iconButton.tips.itemId = CourierItemId;
            }

            _replenishButton?.BindOnClickSafe(OnAutoReplenishClick);

            // 能量条让位不在这里做——那是每次显示格子都要重做的事，而这个方法只跑一次。
            // 调用点在 _OnUpdate 里，按 _frameShifted 决定要不要重推

            ProjectEdenPlugin.Log.LogInfo("综合物流枢纽的物流站面板已补上配送运输机格子（含自动补充箭头）");

            return true;
        }

        private static int IndexOf<T>(T[] array, T item) where T : Object
        {
            if (array == null || item == null) return -1;

            for (var i = 0; i < array.Length; i++)
                if (ReferenceEquals(array[i], item))
                    return i;

            return -1;
        }

        /// <summary>
        /// 让能量条给新格子让路：按<b>实际重叠了多少</b>把它的左边缘往右推，右边缘不动。
        ///
        /// <b>要推的是外框（energy-bar-bg），不是 energyBar 本身。</b> 后者是那根填充条，
        /// 矩形贴着外框拉伸、靠 fillAmount 表示电量，直接改它等于把填充几何弄乱——
        /// 表现就是「条变宽了、起点也不对」。
        ///
        /// <b>推多少要量，不能按「一个格子的宽度」估。</b> 实测外框总宽只有 180，
        /// 而一格间距是 70——照间距推等于砍掉它一半，读数（锚在外框左边缘 102 处）
        /// 还会被挤到条子外面。这里取世界坐标算真实重叠：格子和外框父级不同
        /// （panel-down / power-state），本地坐标没有可比性。
        ///
        /// 记下原始值再按绝对值设置，而不是每次累加：界面重建（读档）后格子会重新克隆，
        /// 累加的话能量条会被一路推到屏幕外。
        /// </summary>
        private static void ShiftEnergyBar(UIStationWindow window)
        {
            var frame = window.energyBar?.rectTransform?.parent as RectTransform;
            var box = _box?.transform as RectTransform;

            if (frame == null || box == null) return;

            DumpHierarchy(window);

            if (!_frameCaptured)
            {
                _frameCaptured = true;
                _frameStretched = !Mathf.Approximately(frame.anchorMin.x, frame.anchorMax.x);
                _frameOriginalLeft = _frameStretched ? frame.offsetMin.x : frame.anchoredPosition.x;
                _frameOriginalWidth = frame.sizeDelta.x;
            }

            // 先还原成原始位置再量，否则量到的是上一次推过之后的结果
            ApplyFrameShift(frame, 0f);
            _frameShift = 0f;

            float scale = frame.lossyScale.x;

            if (Mathf.Approximately(scale, 0f)) return;

            // 还原状态下的真实宽度，读数校正要拿它当分母（实测 240）
            _frameOriginalRect = frame.rect.width;

            float overlap = (WorldRight(box) - WorldLeft(frame)) / scale + Margin;

            // 没挡着就推 0。<b>但仍然记成「已经处理过」</b>——否则 _OnUpdate 里那个
            // if (!_frameShifted) 会让这一整段每帧重量一次
            if (overlap < 0f) overlap = 0f;

            ApplyFrameShift(frame, overlap);
            _frameShifted = true;
            _frameShift = overlap;

            // 这一行整局只打一次：现在每次从普通物流站切回枢纽都会重推一遍，
            // 不设一次性的话它会跟着切窗口刷屏
            if (overlap > 0f && System.Threading.Interlocked.Exchange(ref _shiftLoggedOnce, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"综合物流枢纽面板：能量条左边缘让出 {overlap:0.#} 像素给配送运输机格子（之后每次切回本面板都会重推一遍）");
        }

        /// <summary>
        /// 把能量条外框还原成原始位置。
        ///
        /// <b>这是必须的，不是收尾工作。</b> <c>energyBar</c> 是 <see cref="UIStationWindow"/> 的字段，
        /// 而这个窗口<b>三种物流站 + 大型采矿机共用同一个实例</b>；实测 UIStationWindow 全类
        /// 41 处 rect 几何写（_OnCreate / OnStationIdChange / _OnUpdate / RefreshTrans / RefreshTabs）
        /// <b>没有一处碰这个外框</b>——全是 panelDownTrans、各 *Group 和 energyText。
        /// 所以原版永远不会把它推回去：开过一次枢纽之后，本局内打开任何普通物流站，
        /// 能量条都少一截，而格子已经藏起来了，看上去像凭空变短。
        ///
        /// 这就是 <c>StationExpandPatches.LayoutStorageRows</c> 那次回归
        /// （「三个物流站的 ui 不兼容了」）的同一形状，规矩也是同一条：
        /// <b>你写的东西由你负责还原，还原的时机是别人接管这个控件的时候。</b>
        /// </summary>
        private static void RestoreEnergyBar(UIStationWindow window)
        {
            if (!_frameCaptured || !_frameShifted) return;

            var frame = window.energyBar?.rectTransform?.parent as RectTransform;

            if (frame == null) return;

            ApplyFrameShift(frame, 0f);
            _frameShifted = false;
            _frameShift = 0f;

            // <b>一次性，但必须有。</b> 推窄那一步有日志、还原这一步没有，
            // 那么「还原生效了」和「这一局根本没开过普通物流站」在日志里长得一模一样——
            // 而这正是本次要修的那个回归的验收条件。
            if (System.Threading.Interlocked.Exchange(ref _restoredOnce, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    "综合物流枢纽面板：已把能量条还原给普通物流站（这个窗口三种物流站 + 大型采矿机共用一个实例，" +
                    "原版全类 41 处 rect 几何写没有一处碰这个外框，不还原的话开过枢纽之后它会一直少一截）");
        }

        /// <summary>
        /// 把电量读数按<b>被推窄之后的实际宽度</b>重新摆一次。
        ///
        /// <b>原版的读数坐标是按一个写死的宽度算的，不读外框的矩形。</b>
        /// <c>UIStationWindow._OnUpdate</c> @0303 / @0351 每帧写
        /// <c>energyText.anchoredPosition.x = round(W × 电量比 ∓ 30)</c>，
        /// 而 W 是 <c>isStellar</c> 分支里的常量 180 / 240 / 300——**实测这台枢纽的外框正好宽
        /// 240**，两边对上了，所以 W 就是外框原宽。
        ///
        /// 于是推窄 92 像素之后外框只剩 148，原版照样把读数放到 207
        /// （<c>energyText</c> 锚在外框<b>左边缘</b>），满电时读数落在条子外面 59 像素。
        ///
        /// 按宽度比例线性拉回即可，<b>不需要知道原版走的是 ∓30 里的哪一支</b>——
        /// 整个表达式一起缩放，端点和留白都按同一比例走。
        ///
        /// 不用还原：这个字段原版每帧无条件重写，我们只是在它写完之后再改一次。
        /// </summary>
        private static void FitEnergyText(UIStationWindow window)
        {
            if (!_frameShifted || _frameOriginalRect <= 0f) return;

            RectTransform text = window.energyText?.rectTransform;

            if (text == null) return;

            float scale = (_frameOriginalRect - _frameShift) / _frameOriginalRect;

            if (scale <= 0f) return;

            Vector2 pos = text.anchoredPosition;

            text.anchoredPosition = new Vector2(pos.x * scale, pos.y);
        }

        /// <summary>
        /// 按<b>绝对值</b>设置外框的左边缘偏移，而不是每次累加：界面重建（读档）之后
        /// 格子会重新克隆一遍，累加的话能量条会被一路推到屏幕外。
        /// </summary>
        private static void ApplyFrameShift(RectTransform frame, float dx)
        {
            if (_frameStretched)
            {
                frame.offsetMin = new Vector2(_frameOriginalLeft + dx, frame.offsetMin.y);

                return;
            }

            frame.sizeDelta = new Vector2(_frameOriginalWidth - dx, frame.sizeDelta.y);
            frame.anchoredPosition = new Vector2(_frameOriginalLeft + dx * (1f - frame.pivot.x),
                frame.anchoredPosition.y);
        }

        private static float WorldLeft(RectTransform trs)
        {
            var corners = new Vector3[4];

            trs.GetWorldCorners(corners);

            return corners[0].x;
        }

        private static float WorldRight(RectTransform trs)
        {
            var corners = new Vector3[4];

            trs.GetWorldCorners(corners);

            return corners[2].x;
        }

        /// <summary>
        /// 把能量条那一片的层级和矩形打一次日志。
        ///
        /// 这块布局已经猜错两次了（先改了填充条、再算错间距），而预制体的锚点配置
        /// 离线看不到。一次性把「谁是谁的父级、各自的锚点和矩形」摆出来，
        /// 下次要调就有依据而不是继续猜。
        /// </summary>
        private static void DumpHierarchy(UIStationWindow window)
        {
            if (_dumped) return;

            _dumped = true;

            var sb = new System.Text.StringBuilder("综合物流枢纽面板布局：");

            Describe("energyBar", window.energyBar?.rectTransform);
            Describe("energyBar.parent", window.energyBar?.rectTransform?.parent as RectTransform);
            Describe("energyText", window.energyText?.rectTransform);
            Describe("droneBox", window.droneBox?.transform as RectTransform);
            Describe("warperBox", window.warperBox?.transform as RectTransform);
            Describe("courierBox", _box?.transform as RectTransform);

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());

            void Describe(string name, RectTransform trs)
            {
                if (trs == null)
                {
                    sb.AppendLine().Append($"  {name} = 空");

                    return;
                }

                Rect r = trs.rect;

                sb.AppendLine().Append($"  {name}: 父级 {trs.parent?.name ?? "无"}" +
                          $"，锚点 ({trs.anchorMin.x:0.##},{trs.anchorMin.y:0.##})-({trs.anchorMax.x:0.##},{trs.anchorMax.y:0.##})" +
                          $"，轴心 ({trs.pivot.x:0.##},{trs.pivot.y:0.##})" +
                          $"，位置 ({trs.anchoredPosition.x:0.#},{trs.anchoredPosition.y:0.#})" +
                          $"，尺寸差 ({trs.sizeDelta.x:0.#},{trs.sizeDelta.y:0.#})" +
                          $"，实际矩形 x[{r.xMin:0.#},{r.xMax:0.#}] 宽 {r.width:0.#}");
            }
        }

        // ── 自动补充：照抄 UIStationWindow.OnDroneAutoReplenishButtonClick ──

        private static void OnAutoReplenishClick(int _)
        {
            UIStationWindow window = UIRoot.instance?.uiGame?.stationWindow;

            if (window == null) return;

            DispenserComponent dispenser = Dispenser(window);

            if (dispenser == null) return;

            dispenser.courierAutoReplenish = !dispenser.courierAutoReplenish;

            if (_replenishButton != null) _replenishButton.highlighted = dispenser.courierAutoReplenish;

            // 打开的瞬间就补一次，和原版点运输机那个箭头的手感一致。
            // <b>不能用 UIStationWindow.ReplenishItemsIfNeeded</b>——那个只走
            // StationAutoReplenishIfNeeded，管的是运输机/运输船；补配送运输机的是
            // PlanetFactory.EntityAutoReplenishIfNeeded（它读 courierAutoReplenish 和物品 5003）。
            if (dispenser.courierAutoReplenish)
                window.factory.EntityAutoReplenishIfNeeded(dispenser.entityId, Vector2.zero, true);
        }

        // ── 放入 / 取出：照抄 UIDispenserWindow.OnCourierIconClick ──

        private static void OnCourierClick(int _)
        {
            UIStationWindow window = UIRoot.instance?.uiGame?.stationWindow;

            if (window == null) return;

            DispenserComponent dispenser = Dispenser(window);
            Player player = window.player;

            if (dispenser == null || player == null) return;

            if (player.inhandItemId > 0 && player.inhandItemCount > 0) PutIn(window, dispenser, player);
            else if (player.inhandItemId <= 0 && player.inhandItemCount <= 0) TakeOut(dispenser, player);
        }

        private static void PutIn(UIStationWindow window, DispenserComponent dispenser, Player player)
        {
            if (player.inhandItemId != CourierItemId)
            {
                string name = LDB.items.Select(CourierItemId)?.name ?? "配送运输机";

                UIRealtimeTip.Popup("只能放入".Translate() + name, true, 0);

                return;
            }

            int room = MaxCourier(window, dispenser) - (dispenser.idleCourierCount + dispenser.workCourierCount);

            if (room < 0) room = 0;

            int move = player.inhandItemCount < room ? player.inhandItemCount : room;

            if (move <= 0)
            {
                UIRealtimeTip.Popup("栏位已满".Translate(), true, 0);

                return;
            }

            dispenser.idleCourierCount += move;

            player.AddHandItemCount_Unsafe(-move);

            if (player.inhandItemCount > 0) return;

            player.SetHandItemId_Unsafe(0);
            player.SetHandItemCount_Unsafe(0);
            player.SetHandItemInc_Unsafe(0);
        }

        private static void TakeOut(DispenserComponent dispenser, Player player)
        {
            int count = dispenser.idleCourierCount;

            if (count <= 0) return;

            if (VFInput.shift || VFInput.control)
            {
                // **调用前先把侧信道清零。** preloader 把 TryAddItemToPackage 改写成了
                // 「从侧信道读品质」，协议是调用方在调用前写；不写的话它消费的是上一个
                // 调用者留下的值，品质凭空长出来。配送运输机本来就没有品质，所以清零即可。
                if (QualityAccess.ChannelClearable) QualityAccess.ClearChannel();

                int added = player.TryAddItemToPackage(CourierItemId, count, 0, false, 0, false);

                UIItemup.Up(CourierItemId, added);
            }
            else
            {
                player.SetHandItemId_Unsafe(CourierItemId);
                player.SetHandItemCount_Unsafe(count);
                player.SetHandItemInc_Unsafe(0);
            }

            dispenser.idleCourierCount = 0;
        }
    }
}
