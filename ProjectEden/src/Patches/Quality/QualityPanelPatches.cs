using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 第 4 阶段的<b>最小显示</b>：物流站每个槽位的数量后面跟上品质。
    ///
    /// 品质是<b>容器的属性，不是物品原型的属性</b>——同一个 <c>ItemProto</c> 在不同格子里
    /// 品质不同，所以它不可能出现在物品 tip 里（那里只有原型）。它只能显示在
    /// 「一格具体的货」旁边，而物流站的槽位行正是这样一个地方。
    ///
    /// <b>挂在 <c>RefreshValues</c> 而不是 <c>_OnUpdate</c></b>：数量文本就是那里写的，
    /// 跟着它走就永远不会出现「数量变了品质没变」。反过来，凡是 vanilla 会重写的东西,
    /// 我们就不要自己另存一份——这是 <c>MultiProductUIPatches.RestoreSlot1</c> 的教训。
    ///
    /// 显示的是<b>每件品质分</b>而不是总分：总分随件数变，看不出好坏；每件分才是玩家要比的量。
    /// 件数为 0 时不显示，因为 0 件的「每件多少分」没有意义。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityPanelPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIStationStorage), nameof(UIStationStorage.RefreshValues))]
        private static void ShowQuality(UIStationStorage __instance)
        {
            if (!QualityAccess.Ready) return;

            StationComponent station = __instance.station;

            if (station?.storage == null) return;

            int i = __instance.index;

            if (i < 0 || i >= station.storage.Length) return;

            int count = station.storage[i].count;

            if (count <= 0) return;

            int qua = QualityAccess.GetStationQua(ref station.storage[i]);

            if (qua <= 0) return;

            UnityEngine.UI.Text text = __instance.countValueText;

            if (text == null) return;

            text.text = text.text + "  品质 " + (qua / count);
        }
    }
}
