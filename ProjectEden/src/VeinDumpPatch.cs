using System;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden
{
    /// <summary>
    /// 按键打印当前星球的矿脉储量。只读，不修改任何游戏状态。
    /// </summary>
    [HarmonyPatch]   // 必须有类级特性，PatchAll(Assembly) 才会扫描到本类
    public static class VeinDumpPatch
    {
        private const KeyCode DumpKey = KeyCode.F8;

        // 不能用 EVeinType.Max 定长：创世之书的预加载器会删掉 Max 并追加 6 个矿种（15~20）。
        // 运行时按枚举实际上界建表，装了内容 mod 也能统计到新矿种。
        private static readonly long[] Totals = new long[GetVeinTypeCount()];

        private static int GetVeinTypeCount()
        {
            var max = 0;

            // 必须按 EVeinType 取出：该枚举底层类型是 byte，
            // 把装箱值直接拆成 int 会抛 InvalidCastException。
            foreach (EVeinType value in Enum.GetValues(typeof(EVeinType)))
            {
                int v = (int)value;
                if (v > max) max = v;
            }

            return max + 1;
        }

        // 按键检测挂 Update 而不是 FixedUpdate：
        // FixedUpdate 每渲染帧可能跑 0 次或多次，GetKeyDown 会漏触发或重复触发
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameMain), "Update")]   // private 实例方法，只能用字符串名指定
        private static void OnUpdate()
        {
            // 这里每帧都会执行，只做一次按键判断，不分配内存
            if (!Input.GetKeyDown(DumpKey)) return;
            DumpVeins();
        }

        private static void DumpVeins()
        {
            PlanetFactory factory = GameMain.mainPlayer?.factory;
            if (factory == null)
            {
                ProjectEdenPlugin.Log.LogWarning("当前不在星球上，无法统计矿脉");
                return;
            }

            Array.Clear(Totals, 0, Totals.Length);
            long solidTotal = 0;
            int veinCount = 0;

            VeinData[] pool = factory.veinPool;
            // 槽位从 1 开始；id != i 表示该槽位已被回收，跳过
            for (int i = 1; i < factory.veinCursor; i++)
            {
                if (pool[i].id != i) continue;

                int type = (int)pool[i].type;
                if (type <= 0 || type >= Totals.Length) continue;

                Totals[type] += pool[i].amount;
                veinCount++;

                // 原油的 amount 与固体矿单位不同，不并入总储量
                if (pool[i].type != EVeinType.Oil) solidTotal += pool[i].amount;
            }

            string planet = factory.planet != null ? factory.planet.displayName : "未知星球";
            ProjectEdenPlugin.Log.LogInfo($"[{planet}] 矿脉 {veinCount} 处，固体矿总储量 {solidTotal:N0}");

            for (int t = 1; t < Totals.Length; t++)
            {
                if (Totals[t] == 0) continue;
                var veinType = (EVeinType)t;
                string name = System.Enum.GetName(typeof(EVeinType), veinType) ?? t.ToString();
                string note = veinType == EVeinType.Oil ? "  ← 单位与固体矿不同，未计入总量" : "";
                ProjectEdenPlugin.Log.LogInfo($"    {name,-12}{Totals[t],16:N0}{note}");
            }
        }
    }
}
