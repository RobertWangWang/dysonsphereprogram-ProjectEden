using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让所有巨型建筑的配方无前置科技即可使用。
    ///
    /// 只把 RecipeProto.preTech 留空是不够的：DSP 的已解锁配方是一个 HashSet，
    /// 由科技解锁时写入，读档时从存档恢复。所以这里两头都补——
    /// 新开局在 Init 后塞进集合，读旧档则靠 RecipeUnlocked 直接返回 true。
    /// </summary>
    [HarmonyPatch]
    internal static class RecipeUnlockPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameHistoryData), nameof(GameHistoryData.Init))]
        private static void GameHistoryData_Init(GameHistoryData __instance) => Unlock(__instance);

        /// <summary>
        /// 新开局时 SetForNewGame 第一件事就是 recipeUnlocked.Clear()，
        /// 会把 Init 阶段加进去的配方清掉，所以这里要再加一次。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameHistoryData), nameof(GameHistoryData.SetForNewGame))]
        private static void GameHistoryData_SetForNewGame(GameHistoryData __instance) => Unlock(__instance);

        private static void Unlock(GameHistoryData history)
        {
            if (history?.recipeUnlocked == null) return;

            foreach (int recipeId in MegaBuildingRegistry.RecipeIds) history.recipeUnlocked.Add(recipeId);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameHistoryData), nameof(GameHistoryData.RecipeUnlocked))]
        private static void GameHistoryData_RecipeUnlocked(int recipeId, ref bool __result)
        {
            if (__result) return;

            if (MegaBuildingRegistry.RecipeIds.Contains(recipeId)) __result = true;
        }
    }
}
