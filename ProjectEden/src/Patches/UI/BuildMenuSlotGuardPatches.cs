using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 兜底：把「建造栏槽位没有按钮」从每帧崩溃降级成「这个建筑不在建造栏里 + 一条错误日志」。
    ///
    /// <b>为什么需要兜底。</b> UIBuildMenu._OnUpdate 的子项循环是：
    /// <code>
    ///   for (j = 0; j &lt; 12 &amp;&amp; j &lt; childNumTexts.Length; j++) {
    ///       if (protos[currentCategory, j] == null) continue;
    ///       childButtons[j].tips.itemId = …;      // ← 不判空
    ///   }
    /// </code>
    /// <c>childButtons</c> 是预制体里摆好的固定几个按钮，而 <c>protos</c> 是
    /// UIBuildMenu.StaticLoad 按每个物品的 <c>BuildIndex % 100</c> 填的。
    /// 只要某个物品占了一个没有按钮的槽位，<b>建造栏一显示就每帧 NullReferenceException</b>，
    /// 而且堆栈落在 _OnUpdate 上，看不出是哪个物品干的。
    ///
    /// BuildMenuScrollPatches 每帧只把「当前窗口」搬进 protos，正常不会踩到。
    /// 这里是第二道闸：真踩到了就把那一格清掉并点名，
    /// 代价是那台建筑暂时不在建造栏里，总好过游戏没法玩。
    ///
    /// <b>超出一屏的槽位不算异常</b>——那些本来就由滑动那套接管，StaticLoad 先塞进去、
    /// 搬运时再清掉是正常流程，静默处理即可，别当错误吼。
    ///
    /// 挂在 _OnOpen 上而不是 _OnUpdate：只在打开建造栏时查一次，热路径上不加东西。
    /// </summary>
    [HarmonyPatch]
    internal static class BuildMenuSlotGuardPatches
    {
        /// <summary>渲染循环的上界，_OnUpdate 里的 <c>j &lt; 12</c>。</summary>
        private const int MaxSlot = 12;

        private static bool _checked;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIBuildMenu), "_OnOpen")]
        private static void UIBuildMenu_OnOpen(UIBuildMenu __instance)
        {
            if (_checked) return;

            UIButton[] buttons = __instance.childButtons;

            // 按钮还没绑好就下次再查，别把 _checked 提前置上
            if (buttons == null || buttons.Length == 0) return;

            _checked = true;

            var cleared = 0;

            for (var category = 0; category < 16; category++)
            for (var slot = 0; slot < MaxSlot; slot++)
            {
                ItemProto proto = UIBuildMenu.protos[category, slot];

                if (proto == null) continue;

                // 有按钮就没事；数组不够长或那一格是 null 才是要命的
                if (slot < buttons.Length && buttons[slot] != null) continue;

                UIBuildMenu.protos[category, slot] = null;

                cleared++;

                ProjectEdenPlugin.Log.LogWarning(
                    $"建造栏第 {category} 类第 {slot} 槽放着「{proto.name}」（BuildIndex {proto.BuildIndex}），" +
                    "但界面在那个位置没有按钮。已把该格清空——正常情况下它由子项滑动接管，" +
                    "这条只是兜底记录；如果这台建筑在建造栏里始终看不到，就是滑动那套没生效。");
            }

            if (cleared == 0)
                ProjectEdenPlugin.Log.LogInfo($"建造栏槽位核对通过：界面有 {buttons.Length} 个子项按钮，没有悬空的格位");
        }
    }
}
