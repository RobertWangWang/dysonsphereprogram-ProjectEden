using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 第 4 阶段的<b>最小显示</b>：物流站每个槽位的数量旁边跟上品质。
    ///
    /// 品质是<b>容器的属性，不是物品原型的属性</b>——同一个 <c>ItemProto</c> 在不同格子里
    /// 品质不同，所以它不可能出现在物品 tip 里（那里只有原型）。它只能显示在
    /// 「一格具体的货」旁边，而物流站的槽位行正是这样一个地方。
    ///
    /// <b>不能往 <c>countValueText</c> 后面接字。</b> 第一版就是那么写的，实测撞了：
    /// 那个 Text 是**右对齐**的，字串变长时整串往左长，于是数字钻到「当前」标签底下,
    /// 屏幕上显示成「当前5000 品质 10」——数字被标签盖掉了一半。
    /// 往一个右对齐的文本后面追加内容，视觉上等于往它**前面**插内容。
    ///
    /// 所以品质单独一个 Text，**挂成数字的子物体并锚在它的右边缘**。
    /// 这样不需要知道父级的布局、锚点和 pivot 是什么（那些在 prefab 里，离线读不到），
    /// 而且 vanilla 或 <c>StationExpandPatches</c> 把那一行挪到哪里，它都跟着走。
    ///
    /// 显示的是<b>每件品质分</b>而不是总分：总分随件数变，看不出好坏；每件分才是玩家要比的量。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityPanelPatches
    {
        private const string LabelName = "projecteden-qua";

        /// <summary>每个槽位行一个标签。UI 只在主线程跑，用普通字典就够。</summary>
        private static readonly Dictionary<UIStationStorage, Text> Labels =
            new Dictionary<UIStationStorage, Text>();

        private static bool _reported;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIStationStorage), nameof(UIStationStorage.RefreshValues))]
        private static void ShowQuality(UIStationStorage __instance)
        {
            if (!QualityAccess.Ready) return;

            Text label = Label(__instance);

            if (label == null) return;

            StationComponent station = __instance.station;
            int i = __instance.index;

            if (station?.storage == null || i < 0 || i >= station.storage.Length)
            {
                label.enabled = false;

                return;
            }

            int count = station.storage[i].count;
            int qua = count > 0 ? QualityAccess.GetStationQua(ref station.storage[i]) : 0;

            // **空了就要关掉。** 不关的话上一格的品质会留在屏幕上，而那正是本仓库
            // 在建造栏、产物槽上反复踩过的「腾空的位置从来没人清理」。
            if (qua <= 0 || count <= 0)
            {
                label.enabled = false;

                return;
            }

            label.enabled = true;
            label.text = "品质 " + (qua / count);
        }

        private static Text Label(UIStationStorage ui)
        {
            if (Labels.TryGetValue(ui, out Text cached) && cached != null) return cached;

            Text src = ui.countValueText;

            if (src == null) return null;

            // 已经建过就捡回来：换场景之后字典是空的，而物体还在。
            // 不捡的话会在克隆体里再克隆一层，一层套一层——那是 MultiProductUIPatches
            // 记过的「克隆出两份、你写的那份不是画在上面的那份」。
            Transform had = src.transform.Find(LabelName);
            Text label = had != null ? had.GetComponent<Text>() : Build(src);

            if (label == null) return null;

            Labels[ui] = label;

            return label;
        }

        private static Text Build(Text src)
        {
            var go = Object.Instantiate(src.gameObject, src.transform, false);

            go.name = LabelName;

            var label = go.GetComponent<Text>();

            if (label == null) { Object.Destroy(go); return null; }

            // 克隆体自己也可能带进来一份子物体（上一次的标签），清掉
            for (int k = label.transform.childCount - 1; k >= 0; k--)
                Object.Destroy(label.transform.GetChild(k).gameObject);

            RectTransform r = label.rectTransform;

            // 锚在源文本的**右边缘**，pivot 在自己的左边：字往右长，永远碰不到左边的标签。
            r.anchorMin = new Vector2(1f, 0.5f);
            r.anchorMax = new Vector2(1f, 0.5f);
            r.pivot = new Vector2(0f, 0.5f);
            r.anchoredPosition = new Vector2(10f, 0f);
            r.sizeDelta = new Vector2(150f, src.rectTransform.rect.height);

            label.alignment = TextAnchor.MiddleLeft;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.raycastTarget = false;
            label.enabled = false;

            if (!_reported)
            {
                _reported = true;

                // 布局出问题时要能从**数字**上改，不是从截图上估——这条规矩本仓库
                // 在物流站面板上付过三次学费。
                ProjectEdenPlugin.Log.LogInfo(
                    $"物品品质：槽位品质标签已建。源文本 rect={src.rectTransform.rect}、" +
                    $"锚点 {src.rectTransform.anchorMin}-{src.rectTransform.anchorMax}、" +
                    $"pivot {src.rectTransform.pivot}、对齐 {src.alignment}；" +
                    $"标签挂在它右边 10 像素处。");
            }

            return label;
        }
    }
}
