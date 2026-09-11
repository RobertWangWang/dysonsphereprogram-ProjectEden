using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 给自定义配方类型补上界面文字。
    ///
    /// 新配方类型本身是免费的：ERecipeType 是 int 枚举，(ERecipeType)9 不需要有名字也合法，
    /// 配方选择器的过滤是 <c>filter != recipe.Type</c> 这样的直接比较，
    /// AssemblerComponent.SetRecipe 又完全不校验类型。<b>唯一会露馅的是两处文字</b>，
    /// 它们都是 switch 到已知类型、走不到就落进 default：
    ///
    ///   · <c>RecipeProto.madeFromString</c> —— 配方提示栏的「制造于 ×××」
    ///   · <c>ItemProto.typeString</c>       —— 物品提示栏的「类型」那一行
    ///
    /// 后置改 __result 就够了，不用碰跳转表。做法照搬 ProjectGenesis 的 DisplayTextPatches。
    /// </summary>
    [HarmonyPatch]
    internal static class RecipeTypeNamePatches
    {
        /// <summary>
        /// 配方提示栏的「制造于」。原版 switch 不认识新类型，会留一个空串。
        ///
        /// 另外补一种原版没有的情况：<c>Type == None</c> 且能手搓，即<b>只能手搓</b>的配方
        /// （machines.json 的 <c>recipeHandcraftOnly</c>）。原版 switch 的 0 号分支返回
        /// 一个光秃秃的「-」，看着像数据缺失。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(RecipeProto), nameof(RecipeProto.madeFromString), MethodType.Getter)]
        private static void RecipeProto_madeFromString(RecipeProto __instance, ref string __result)
        {
            if (__instance.Type == ERecipeType.None)
            {
                if (__instance.Handcraft) __result = "手动合成".Translate();

                return;
            }

            string name = MachineRegistry.RecipeTypeMachineName((int)__instance.Type);

            // 过一次 Translate：这个值是 machines.json 里的中文，已经注册成本地化键了，
            // 切英文时要变成英文机器名，否则「制造于」会是一行孤立的中文
            if (name != null) __result = name.Translate();
        }

        /// <summary>
        /// 物品提示栏的「类型」。原版是 <c>assemblerRecipeType - 1</c> 的跳转表，
        /// 新类型落到 default，显示成一个不相干的兜底词。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemProto), nameof(ItemProto.typeString), MethodType.Getter)]
        private static void ItemProto_typeString(ItemProto __instance, ref string __result)
        {
            string name = MachineRegistry.MachineTypeName(__instance.ID);

            if (name != null) __result = name.Translate();
        }
    }
}
