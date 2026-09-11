using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectEden.Compatibility;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 在底部建造栏加一个"巨型建筑"分类。
    ///
    /// 原版 UIBuildMenu 只有 1~9 类：数组容量够（StaticLoad 接受到 15），但按钮是美术摆好的，
    /// 而且 _OnUpdate / SetCurrentCategory 里把上限 9 硬编码在了 IL 里。所以要做两件事：
    ///   1. 克隆一个现成的分类按钮，接到 categoryButtons[分类号] 上；
    ///   2. 把那几处硬编码的 9 抬高到我们的分类号。
    ///
    /// 做法参考 ProjectGenesis 的 UIBuildMenuPatches，但第 2 步改成按指令特征匹配，
    /// 不用它那种 Advance(n) 的位置定位——游戏一更新就会错位。
    /// </summary>
    [HarmonyPatch]
    internal static class BuildMenuCategoryPatches
    {
        /// <summary>原版硬编码的最大分类号。</summary>
        private const int VanillaMaxCategory = 9;

        private static readonly FieldInfo CurrentCategoryField =
            AccessTools.Field(typeof(UIBuildMenu), nameof(UIBuildMenu.currentCategory));

        private static int Category => MegaBuildingRegistry.Config?.buildCategory ?? 0;

        /// <summary>位置微调，写在 JSON 里，不满意可以直接改数值而不用动代码。</summary>
        private static float NudgeX => MegaBuildingRegistry.Config?.categoryButtonNudgeX ?? 0f;

        /// <summary>
        /// 创世之书也会新增分类，同时装载时让给它，免得两个 mod 抢同一个按钮位。
        /// </summary>
        private static bool Enabled => Category > VanillaMaxCategory && GenesisBookCompat.MegaAssemblerEnabled;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIBuildMenu), nameof(UIBuildMenu._OnCreate))]
        private static void UIBuildMenu_OnCreate(UIBuildMenu __instance)
        {
            if (!Enabled) return;

            if (__instance.categoryButtons == null || __instance.categoryButtons.Length <= Category)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"建造栏只有 {__instance.categoryButtons?.Length ?? 0} 个分类按钮位，放不下第 {Category} 类，跳过分类按钮创建");

                return;
            }

            UIButton btn = __instance.categoryButtons[Category];

            // 这一位必须是空的。categoryButtons 里索引 10 以上放的是拆除、蓝图等
            // 真实功能按钮，占用它们会把那些功能顶掉——分类号要选一个确实为 null 的下标。
            if (btn != null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"categoryButtons[{Category}] 已被占用（多半是拆除/蓝图等功能按钮），" +
                    "换一个空闲的分类号，否则会顶掉原版功能");

                return;
            }

            UIButton template = __instance.categoryButtons[1];

            if (template == null) return;

            {
                // 面板左边被「沙盒工具」标签占着，没有空位，所以不能直接往 1 号左边塞。
                // 做法是把现有按钮整体右移一格，新按钮占用腾出来的 1 号位。
                // 位移量取 1、2 号按钮的实测间距。
                Vector3 anchor = template.transform.localPosition;
                float spacing = ButtonSpacing(__instance, anchor);

                foreach (UIButton button in __instance.categoryButtons) Shift(button, spacing);

                Shift(__instance.blueprintButton, spacing);

                btn = Object.Instantiate(template, template.transform.parent);

                // 放回 1 号按钮原来的位置：整排的左边界因此没有变化，不会压到标签
                btn.transform.localPosition = new Vector3(anchor.x + NudgeX, anchor.y, anchor.z);
            }

            btn.gameObject.SetActive(true);

            // 按钮上可能挂着美术在编辑器里连好的 onClick，运行时删不掉持久化调用，
            // 只能把整个 Button 组件换成新的，确保点击一定走我们的分类号
            RemovePersistentCalls(btn.gameObject);

            btn.button.onClick.AddListener(OnCategoryButtonClick);
            btn.tips.tipTitle = MegaBuildingRegistry.Config.tabName;

            Image icon = btn.transform.GetChild(0).GetComponent<Image>();
            icon.sprite = Resources.Load<Sprite>(MegaBuildingRegistry.Config.tabIconPath);

            Text text = btn.transform.GetChild(1).GetComponent<Text>();
            text.text = "-";

            __instance.categoryIcons[Category] = icon;
            __instance.categoryTips[Category] = text;
            __instance.categoryButtons[Category] = btn;

            ProjectEdenPlugin.Log.LogInfo(
                $"建造栏第 {Category} 类「{MegaBuildingRegistry.Config.tabName}」已就绪" +
                $"（新建按钮并整排右移，x={btn.transform.localPosition.x:0.#}）");
        }

        private static void Shift(UIButton button, float dx)
        {
            if (button == null) return;

            Transform t = button.transform;
            Vector3 p = t.localPosition;

            t.localPosition = new Vector3(p.x + dx, p.y, p.z);
        }

        /// <summary>
        /// 分类按钮的水平间距。用 1、2 号按钮实测；取不到就退回按钮自身宽度。
        /// </summary>
        private static float ButtonSpacing(UIBuildMenu menu, Vector3 anchor)
        {
            UIButton next = menu.categoryButtons.Length > 2 ? menu.categoryButtons[2] : null;

            if (next != null)
            {
                float d = next.transform.localPosition.x - anchor.x;

                if (Mathf.Abs(d) > 1f) return Mathf.Abs(d);
            }

            var rect = menu.categoryButtons[1].transform as RectTransform;

            return rect != null && rect.sizeDelta.x > 1f ? rect.sizeDelta.x : 26f;
        }

        private static void RemovePersistentCalls(GameObject go)
        {
            Button button = go.GetComponent<Button>();
            UIButton uiButton = go.GetComponent<UIButton>();

            if (uiButton == null || button == null) return;

            Object.DestroyImmediate(button);
            uiButton.button = go.AddComponent<Button>();
        }

        private static void OnCategoryButtonClick() => UIRoot.instance.uiGame.buildMenu.OnCategoryButtonClick(Category);

        // ── 把硬编码的分类上限抬高 ─────────────────────────────

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIBuildMenu), nameof(UIBuildMenu.SetCurrentCategory))]
        private static IEnumerable<CodeInstruction> SetCurrentCategory_Transpiler(IEnumerable<CodeInstruction> instructions)
            => RaiseCategoryLimit(instructions, nameof(UIBuildMenu.SetCurrentCategory));

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIBuildMenu), nameof(UIBuildMenu._OnUpdate))]
        private static IEnumerable<CodeInstruction> OnUpdate_Transpiler(IEnumerable<CodeInstruction> instructions)
            => RaiseCategoryLimit(instructions, nameof(UIBuildMenu._OnUpdate));

        /// <summary>
        /// 把"分类号不能超过 9"的那几处上限判断抬到我们的分类号。
        ///
        /// 只改满足三个特征的位置：常量正好是 9、前一条在取分类号（currentCategory 字段 /
        /// SetCurrentCategory 的参数 / 遍历分类的局部变量）、后一条是<b>大小比较</b>分支。
        ///
        /// 最后一条是关键：原版对 9 有两种含义完全不同的判断——
        ///   · bgt / ble 这类大小比较 = "分类号是否越界"，要抬高；
        ///   · beq / bne 这类相等判断 = "当前是不是第 9 类（地基）"，抬高会让我们的分类
        ///     被当成地基模式，界面上叠出沙土计数和铺设工具。
        /// </summary>
        private static IEnumerable<CodeInstruction> RaiseCategoryLimit(IEnumerable<CodeInstruction> instructions, string method)
        {
            var list = new List<CodeInstruction>(instructions);

            if (!Enabled) return list;

            var replaced = 0;

            for (var i = 1; i < list.Count - 1; i++)
            {
                if (!list[i].LoadsConstant(VanillaMaxCategory)) continue;

                CodeInstruction prev = list[i - 1];

                // 只认「取当前分类号」的两种取值：currentCategory 字段，或 SetCurrentCategory 的参数。
                // 刻意排除 ldloc —— _OnUpdate 里那处 “ldloc.2; 9; ble” 是遍历分类的循环上限，
                // 抬高它会让循环去设置索引 10 以上的按钮状态，而那些位置放的是拆除、蓝图等
                // 真实功能按钮，不是空置的分类槽，结果就是整条建造栏被搅乱。
                bool loadsCategory = prev.LoadsField(CurrentCategoryField)
                                  || prev.opcode == OpCodes.Ldarg_1;

                if (!loadsCategory) continue;
                if (!IsOrderingBranch(list[i + 1].opcode)) continue;

                list[i].opcode = OpCodes.Ldc_I4_S;
                list[i].operand = (sbyte)Category;

                replaced++;
            }

            if (replaced == 0)
                ProjectEdenPlugin.Log.LogError(
                    $"UIBuildMenu.{method}：没找到任何分类上限判断，第 {Category} 类可能无法选中。" +
                    "游戏版本变化后这里需要重新对照 IL。");
            else
                ProjectEdenPlugin.Log.LogInfo($"UIBuildMenu.{method}：已把 {replaced} 处分类上限从 {VanillaMaxCategory} 抬到 {Category}");

            return list;
        }

        /// <summary>
        /// 是不是大小比较分支（bgt / bge / blt / ble 及其 .s / .un 变体）。
        /// 刻意排除 beq / bne——那是相等判断，含义是"当前分类是不是第 9 类"，改了会串味。
        /// </summary>
        private static bool IsOrderingBranch(OpCode opcode)
        {
            string name = opcode.Name;

            return name.StartsWith("bgt") || name.StartsWith("bge") || name.StartsWith("blt") || name.StartsWith("ble");
        }
    }
}
