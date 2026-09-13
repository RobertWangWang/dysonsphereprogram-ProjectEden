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
    /// 所以品质单独一个 Text，放在**进度条的右内侧**——条是从左往右填的，右端最不占用。
    ///
    /// <b>它必须是「整行」的最后一个子物体，挂进进度条里面不行。</b> 挂进滑块内部试过,
    /// 照样被白色填充盖住：<c>countBar</c> 是 <c>maxSlider</c> 的**兄弟节点**且排在它后面,
    /// 而层级顺序就是渲染顺序，所以滑块的任何子物体都在填充之下。
    /// 「画在最上面」只能靠成为**共同父级的最后一个子物体**。
    ///
    /// 位置是**照抄进度条的矩形**再靠右对齐：锚点、pivot、拉伸方式都在 prefab 里，
    /// 离线读不到，照抄就不必知道它们分别是什么。
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

            // **走翻译表，不能拼中文。** 键本身就是中文客户端要看到的那句话
            // （`I18N.ApplyLanguage` 给中文的是键的原文，只有别的语言才查表），
            // 所以键要写成带占位符的完整句子，而不是「品质标签」这种描述性名字——
            // 后者在中文客户端上会原样显示成「品质标签」。
            label.text = string.Format("品质 {0}".Translate(), qua / count);
        }

        private static Text Label(UIStationStorage ui)
        {
            if (Labels.TryGetValue(ui, out Text cached) && cached != null) return cached;

            Text src = ui.countValueText;

            if (src == null) return null;

            // **挂到整行上，排在最后**，而不是挂进进度条里面。
            //
            // 挂进滑块内部试过，仍然被白色填充盖住——因为 `countBar` 是 `maxSlider` 的
            // **兄弟节点**且排在它后面，滑块的任何子物体都在它下面。层级顺序就是渲染顺序,
            // 所以「画在最上面」只能靠成为**共同父级的最后一个子物体**，
            // 挂进其中一个兄弟里面是够不着的。
            var bar = ui.maxSlider != null ? ui.maxSlider.transform as RectTransform : null;
            Transform host = bar != null && bar.parent != null ? bar.parent : src.transform;

            // 已经建过就捡回来：换场景之后字典是空的，而物体还在。
            // 不捡的话会在克隆体里再克隆一层，一层套一层——那是 MultiProductUIPatches
            // 记过的「克隆出两份、你写的那份不是画在上面的那份」。
            Transform had = host.Find(LabelName);
            Text label = had != null ? had.GetComponent<Text>() : Build(src, host, bar);

            if (label == null) return null;

            Labels[ui] = label;

            return label;
        }

        private static Text Build(Text src, Transform host, RectTransform bar)
        {
            var go = Object.Instantiate(src.gameObject, host, false);

            go.name = LabelName;

            var label = go.GetComponent<Text>();

            if (label == null) { Object.Destroy(go); return null; }

            // 克隆体自己也可能带进来一份子物体（上一次的标签），清掉
            for (int k = label.transform.childCount - 1; k >= 0; k--)
                Object.Destroy(label.transform.GetChild(k).gameObject);

            // 排在最后 = 画在最上面。进度条、填充、滑块都是它的兄弟节点，排在前面。
            label.transform.SetAsLastSibling();

            RectTransform r = label.rectTransform;

            if (bar != null)
            {
                // **整个矩形照抄进度条**，再靠右对齐把字顶到条的右端。
                // 抄比算可靠：锚点、pivot、拉伸方式都是 prefab 里的，离线读不到,
                // 照抄就不必知道它们分别是什么。
                r.anchorMin = bar.anchorMin;
                r.anchorMax = bar.anchorMax;
                r.pivot = bar.pivot;
                r.anchoredPosition = bar.anchoredPosition;
                r.sizeDelta = bar.sizeDelta;
                r.offsetMin = bar.offsetMin;
                r.offsetMax = bar.offsetMax - new Vector2(10f, 0f);   // 右内侧留一点边
            }
            else
            {
                r.anchorMin = new Vector2(1f, 0.5f);
                r.anchorMax = new Vector2(1f, 0.5f);
                r.pivot = new Vector2(1f, 0.5f);
                r.anchoredPosition = new Vector2(-10f, 0f);
                r.sizeDelta = new Vector2(150f, src.rectTransform.rect.height);
            }

            label.alignment = TextAnchor.MiddleRight;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.raycastTarget = false;
            label.enabled = false;

            // 黑字（所有者指定）。
            //
            // **描边跟着翻成浅色，这不是装饰。** 这个标签所在的位置，底色会随着填充推进
            // 从深变浅：纯黑字在浅色填充上很清楚，在未填充的深色段上会直接消失。
            // 浅色描边只在深底那一半起作用，浅底那一半几乎看不出来。
            label.color = new Color(0.05f, 0.06f, 0.08f);

            var outline = go.GetComponent<Outline>() ?? go.AddComponent<Outline>();

            outline.effectColor = new Color(0.92f, 0.95f, 1f, 0.9f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            if (!_reported)
            {
                _reported = true;

                // 布局出问题时要能从**数字**上改，不是从截图上估——这条规矩本仓库
                // 在物流站面板上付过三次学费。
                ProjectEdenPlugin.Log.LogInfo(
                    $"物品品质：槽位品质标签已建，挂在 {host.name} 上，排第 " +
                    $"{label.transform.GetSiblingIndex() + 1}/{host.childCount} 个子物体。" +
                    $"进度条 rect={(bar != null ? bar.rect.ToString() : "（没找到）")}；" +
                    $"标签 rect={r.rect}。整个矩形照抄进度条，文字右对齐顶到右端。");
            }

            return label;
        }
    }
}
