using BepInEx.Bootstrap;

namespace ProjectEden.Compatibility
{
    /// <summary>
    /// 第三方 mod 兼容层入口（借鉴 ProjectGenesis 的 src/Compatibility/ 组织方式：
    /// 一个已知 mod 一个文件，全部走 SoftDependency——装了就适配，没装照常跑）。
    /// </summary>
    internal static class CompatibilityRegistry
    {
        internal static void Init()
        {
            GenesisBookCompat.Init();
            GalacticScaleCompat.Init();
        }

        /// <summary>
        /// 需要手动打到第三方程序集上的补丁。必须在 Harmony 实例建好之后调用——
        /// PatchAll(Assembly) 只认本程序集里的 [HarmonyPatch]，够不到别的 mod。
        /// </summary>
        internal static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            GalacticScaleCompat.ApplyPatches(harmony);
            OreGalacticScaleCompat.ApplyPatches(harmony);
            OreVeinTipCompat.ApplyPatches(harmony);
            UXAssistCompat.ApplyPatches(harmony);
        }

        /// <summary>按 GUID 判断某个插件是否已加载。</summary>
        internal static bool IsLoaded(string guid) => Chainloader.PluginInfos.ContainsKey(guid);
    }
}
