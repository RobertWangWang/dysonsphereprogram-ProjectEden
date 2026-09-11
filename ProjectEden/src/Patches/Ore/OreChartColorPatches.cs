using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让本 mod 新增的矿脉出现在「矿脉分布」图表里。
    ///
    /// <b>图表的色块是按矿种编号直接查调色板的。</b> 链路是：
    ///     AstroResourceStatPlan.AddDetailedStatData(astroId, <b>VeinData.type</b>, groupIndex, amount)
    ///     → UIChartAstroResource.Refresh：blockDatas[n].colorIndex = detailedStatData.protoId
    ///     → colorBuffer.SetData(graphColors)；material.SetBuffer("_ColorBuffer", colorBuffer)
    /// 也就是说着色器拿<b>矿种编号</b>当下标去 graphColors 里取色。
    ///
    /// 而 graphColors 是<b>在 Unity 预制体里序列化好的定长数组</b>，只覆盖原版的 1~14。
    /// 新矿种编号从 15 起，下标越界，那一块就画不出来——数据一路都是全的
    /// （AddPlanetResources / AddPlanetDetailedResources 里只有 type == 7 的原油特判，
    /// 没有任何矿种上界），纯粹卡在调色板长度上。
    ///
    /// 所以这里在 _OnCreate 之后把调色板按最大矿种号加长、填上各自的颜色，并按新长度重建
    /// colorBuffer（原来那个是按旧长度建的）。Refresh 每次都会重新 SetData 和 SetBuffer，
    /// 所以补在这里就够了。
    /// </summary>
    [HarmonyPatch]
    internal static class OreChartColorPatches
    {
        private static bool _logged;

        /// <summary>
        /// 挂两个点：_OnCreate 只在窗口第一次创建时跑，而 DSP 的窗口是<b>懒创建</b>的；
        /// Refresh 每次刷新都会走，补在那里才保证真正用到时一定生效。
        ///
        /// <b>必须用 TargetMethods 而不是叠 [HarmonyPatch]。</b> 多个 [HarmonyPatch] 特性叠在
        /// 同一个方法上是「合并成<b>一个</b>目标描述」，后写的会覆盖先写的，不是「两个目标」——
        /// 之前就是这么写的，结果最多只挂上一处，而且完全没有提示。
        /// OreVeinRangePatches 能稳定挂到 7 个方法，靠的就是 TargetMethods。
        ///
        /// 这里顺便在解析目标时打一行日志：这样<b>启动时</b>就能知道补丁挂没挂上，
        /// 不必等到打开那个窗口才有信息。
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase create = AccessTools.Method(typeof(UIChartAstroResource), "_OnCreate");
            MethodBase refresh = AccessTools.Method(typeof(UIChartAstroResource), nameof(UIChartAstroResource.Refresh));

            ProjectEdenPlugin.Log.LogInfo(
                $"矿脉分布图表调色板补丁：_OnCreate {(create == null ? "未找到" : "已挂")}，" +
                $"Refresh {(refresh == null ? "未找到" : "已挂")}");

            if (create != null) yield return create;
            if (refresh != null) yield return refresh;
        }

        [HarmonyPrefix]
        private static void UIChartAstroResource_EnsurePalette(UIChartAstroResource __instance)
        {
            if (!OreRegistry.Enabled) return;

            int maxVeinId = OreRegistry.MaxVeinId;

            if (maxVeinId <= 0) return;

            // 先把调色板加长到能容纳编号最大的那个矿种，再逐个填色
            Grow(ref __instance.graphColors, maxVeinId, Color.gray);
            Grow(ref __instance.barColors, maxVeinId, Color.gray);

            foreach (OreRegistry.Ore ore in OreRegistry.Ores)
            {
                Color color = ChartColor(ore);

                if (__instance.graphColors != null && ore.VeinId < __instance.graphColors.Length)
                    __instance.graphColors[ore.VeinId] = color;

                if (__instance.barColors != null && ore.VeinId < __instance.barColors.Length)
                    __instance.barColors[ore.VeinId] = color;
            }

            if (__instance.graphColors == null) return;

            // colorBuffer 是 _OnCreate 里按旧长度建的，加长之后必须重建，
            // 否则 SetData 会因为长度对不上而失败
            if (__instance.colorBuffer != null && __instance.colorBuffer.count < __instance.graphColors.Length)
            {
                __instance.colorBuffer.Release();
                __instance.colorBuffer = new ComputeBuffer(__instance.graphColors.Length, 16, ComputeBufferType.Default);
            }

            if (_logged) return;

            _logged = true;

            ProjectEdenPlugin.Log.LogInfo(
                $"矿脉分布图表已就绪：调色板 {__instance.graphColors.Length} 格，已填入 {OreRegistry.Ores.Count} 个自定义矿种的颜色；" +
                "这行只在你真正打开那个图表时才会出现");
        }

        /// <summary>把调色板补到能容纳这个矿种编号，空出来的格子一并填上同一个颜色。</summary>
        private static void Grow(ref Color[] colors, int index, Color color)
        {
            if (colors == null || colors.Length > index) return;

            int old = colors.Length;

            Array.Resize(ref colors, index + 1);

            for (int i = old; i < colors.Length; i++) colors[i] = color;
        }

        /// <summary>
        /// 图表里该矿种的色块颜色。复用 ores.json 里的 veinColor——
        /// 图表的调色板就是明明白白按矿种编号取色，正好用得上。
        /// </summary>
        private static Color ChartColor(OreRegistry.Ore ore)
        {
            float[] rgb = ore.Entry.veinColor;

            return rgb != null && rgb.Length >= 3 ? new Color(rgb[0], rgb[1], rgb[2]) : Color.gray;
        }
    }
}
