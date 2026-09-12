using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 矩阵实验室界面的一次性勘察：为「加第七种矩阵」而问。
    ///
    /// <b>离线已经能确定的部分，先写在这里，免得重读。</b>
    ///
    /// <list type="number">
    /// <item><b>数据层已经是数据驱动的。</b> <c>LabComponent.matrixIds</c> 是
    /// <c>static int[6] = {6001…6006}</c>，而 <c>SetFunction</c> 用
    /// <c>new int[matrixIds.Length]</c> 开 <c>matrixServed</c> / <c>matrixIncServed</c>，
    /// <c>UILabWindow._OnInit</c> 也用同一个长度开 <c>matrixProtos</c> / <c>matrixRecipes</c>。
    /// <c>TechProto.matrixIds</c> 与它<b>共用同一个静态数据块</b>（哈希 E4C7F588…）。</item>
    ///
    /// <item><b>只有三个方法把六个槽写死了</b>（全程序 28 个碰这四个数组的方法里，
    /// 只有这三个用字面下标 0–5）：<c>InternalUpdateResearch</c>（45 处）、
    /// <c>UpdateOutputToNext</c>（42 处）、<c>UpdateNeedsResearch</c>（6 处）。
    /// 和物流站那次「原版把 storage[0..5] 展开」是同一个形状，解法也一样：换成完整扫描。</item>
    ///
    /// <item><b>界面不会因为多一种矩阵而崩。</b> <c>_OnUpdate</c> 里三个物品循环
    /// <u>全部以 <c>itemButtons.Length</c> 为界</u>，<c>matrixProtos</c> / <c>matrixRecipes</c>
    /// 是在循环体内被索引的——所以控件少于矩阵种类时只会少画一格，不会越界。
    /// <b>这一条是这次没走「先加了再看崩不崩」那条路的原因。</b></item>
    /// </list>
    ///
    /// <b>剩下唯一离线读不到的，就是这些控件数组的长度</b>——它们在代码里从不赋值，
    /// 全来自 prefab，而 prefab 在 <c>resources.assets</c> 里。这正是
    /// <c>UIEntityBriefInfo.icons</c> 把物流站钉在 5 格的同一个形状：
    /// <b>格子数不是逻辑定的，是美术资源定的。</b>
    ///
    /// 所以本探针只问三件事，问完就能定方案：
    /// 控件数组多长、六个控件数组是否等长（<c>_OnUpdate</c> 用同一个 <c>i</c> 索引它们六个，
    /// 不等长本身就是个隐患）、以及相邻两格的间距（要克隆第七套控件时照它摆）。
    /// </summary>
    [HarmonyPatch]
    internal static class LabWindowSurvey
    {
        private static int _done;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UILabWindow), "_OnUpdate")]
        private static void UILabWindow_OnUpdate(UILabWindow __instance)
        {
            // 先抢占再拼字符串：这是每帧都会进来的地方
            if (Interlocked.Exchange(ref _done, 1) != 0) return;

            var sb = new StringBuilder("── 矩阵实验室界面勘察（只打一次）──");

            sb.Append("\n  控件数组长度：")
              .Append($"itemButtons={Len(__instance.itemButtons)} ")
              .Append($"itemIcons={Len(__instance.itemIcons)} ")
              .Append($"itemIncs={Len(__instance.itemIncs)} ")
              .Append($"itemPercents={Len(__instance.itemPercents)} ")
              .Append($"itemCountTexts={Len(__instance.itemCountTexts)} ")
              .Append($"itemLocks={Len(__instance.itemLocks)}");

            sb.Append($"\n  矩阵表：matrixProtos={Len(__instance.matrixProtos)} ")
              .Append($"matrixRecipes={Len(__instance.matrixRecipes)} ")
              .Append($"centerObjs={Len(__instance.centerObjs)} ")
              .Append($"speedArrows={Len(__instance.speedArrows)}");

            sb.Append($"\n  引擎侧：LabComponent.matrixIds={Len(LabComponent.matrixIds)} ")
              .Append($"matrixPoints={Len(LabComponent.matrixPoints)} ")
              .Append($"TechProto.matrixIds={Len(TechProto.matrixIds)}");

            AppendIds(sb, "  matrixIds 内容：", LabComponent.matrixIds);
            AppendIds(sb, "  matrixPoints 内容：", LabComponent.matrixPoints);

            // 五个按 i 索引，itemIncs 按 i*3+k 索引（每格三个增产剂箭头）。
            // <b>头一版把 itemIncs 也按 i 算，于是把 18 报成了「不等长」的隐患 —— 那是误报。</b>
            // 判据要照真实的索引方式写，而那得去读 _OnUpdate 的 IL：
            // 05DA / 05EF / 0606 三处都是 ldloc i ; ldc.i4.3 ; mul (; add k) ; ldelem。
            int n = Len(__instance.itemButtons);
            bool aligned = Len(__instance.itemIcons) == n
                           && Len(__instance.itemPercents) == n
                           && Len(__instance.itemCountTexts) == n
                           && Len(__instance.itemLocks) == n;
            bool incOk = Len(__instance.itemIncs) == n * 3;

            sb.Append(aligned && incOk
                ? $"\n  控件数组步长一致 ✓（五个按 i，itemIncs 按 i*3+k，{n}×3={n * 3}）"
                : $"\n  **控件数组步长对不上**：五个应各为 {n}，itemIncs 应为 {n * 3}");

            // <b>六格的位置要全量打出来，不能只打头尾。</b>
            // 实测第 0 格在 (95, 21.5)、第 1 格在 (-59, -90.5)、第 5 格在 (0, -9.5) 也就是正中——
            // 这不是一排，更像「几个围一圈 + 一个在中心」。
            // 要加第七格就得先看清整圈的半径与角度，而那只能把六个全打出来自己看。
            // （MultiProductUIPatches 那次的教训：布局必须量，不能按表推。）
            sb.Append($"\n  六格位置（父节点 {Parent(__instance, 0)}）：");

            for (var i = 0; i < n; i++)
            {
                var trs = __instance.itemButtons[i]?.transform as RectTransform;

                if (trs == null)
                {
                    sb.Append($"\n    [{i}] （空）");

                    continue;
                }

                Vector2 p = trs.anchoredPosition;

                sb.Append($"\n    [{i}] pos={p}  尺寸={trs.sizeDelta}"
                          + $"  极坐标：半径 {p.magnitude:0.0}，角度 {Mathf.Atan2(p.y, p.x) * Mathf.Rad2Deg:0.0}°");
            }

            sb.Append("\n  → 控件数 < 矩阵种类时只会少画一格，不会越界："
                      + "_OnUpdate 的三个物品循环都以 itemButtons.Length 为界，"
                      + "matrixProtos / matrixRecipes 是在循环体内被索引的（离线已核对）");

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }

        private static int Len(System.Array a) => a?.Length ?? -1;

        private static string Parent(UILabWindow w, int i)
            => (w.itemButtons != null && i < w.itemButtons.Length
                ? w.itemButtons[i]?.transform.parent?.name
                : null) ?? "?";

        private static void AppendIds(StringBuilder sb, string label, int[] a)
        {
            sb.Append('\n').Append(label);

            if (a == null)
            {
                sb.Append("（null）");

                return;
            }

            for (var i = 0; i < a.Length; i++)
            {
                if (i > 0) sb.Append(", ");

                sb.Append(a[i]);
            }
        }
    }
}
