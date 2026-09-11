using System;
using HarmonyLib;
using ProjectEden.Utils;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 拦截 Resources.Load，把本 mod 前缀的路径转到嵌入资源里的 PNG。
    /// 这样自制图标不需要 AssetBundle，也就不需要 Unity Editor。
    /// 移植自 ProjectGenesis 的 TextureResourcesPatches。
    /// </summary>
    [HarmonyPatch]
    internal static class TextureResourcesPatches
    {
        /// <summary>本 mod 的虚拟资源前缀，对应 data/megaassembler.json 里的 iconPath。</summary>
        private const string Prefix = "Assets/projecteden/";

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Resources), nameof(Resources.Load), typeof(string), typeof(Type))]
        private static bool Resources_Load(string path, Type systemTypeInstance, ref UnityEngine.Object __result)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith(Prefix, StringComparison.Ordinal)) return true;

            string name = path.Substring(Prefix.Length);

            if (systemTypeInstance == typeof(Texture2D)) __result = TextureHelper.GetTexture(name);
            else if (systemTypeInstance == typeof(Sprite)) __result = TextureHelper.GetSprite(name);
            else return true;

            // 拿到了就跳过原方法；没拿到则放行，让游戏按原路径找（多半也找不到，但不至于吞掉错误）
            return __result == null;
        }
    }
}
