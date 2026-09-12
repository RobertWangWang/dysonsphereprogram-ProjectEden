using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 保证 <c>PlanetModelingManager</c> 那四个<b>按矿种 ID 当下标</b>的数组装得下我们的矿种。
    ///
    /// <b>这是那个扫描线程崩溃的落点。</b> 报错是
    /// <c>IndexOutOfRangeException at PlanetAlgorithm.GenerateVeins (IL_04F9)</c>，
    /// 而 IL_04F9 是<b>转译之后</b>的偏移，直接去原版方法里找会落在一条读字段的指令中间、
    /// 什么也说明不了。离线把转译重算一遍才定到位：Harmony 读取时会把每个短跳转展开成长跳转
    /// （2 → 5 字节），我们的转译器又把 <c>ldc.i4.s 15</c> 换成 <c>call</c>（2 → 5 字节），
    /// 按这两条重排偏移，patched 04F9 = 原版 <c>IL_0496</c>：
    ///
    /// <code>
    /// 0496: ldloc.s V_11        // veinSpots，new int[veinProtos.Length]（IL 00AE）
    /// 0498: ldloc.s V_30        // 矿种 = theme.RareVeins[i]
    /// 049A: ldelema System.Int32   ← 越界就在这
    /// </code>
    ///
    /// 也就是稀有矿掷骰命中之后的 <c>veinSpots[veinType]++</c>。
    /// <b>所以它和「稀有矿终于开始出现」是同一件事的两面</b>——概率反转修好之前那个分支根本走不到。
    ///
    /// <b>数组长度从哪来。</b> <c>PrepareWorks</c> 的定容循环是
    /// <c>size = dataArray[i].ID + 1</c>——<b>赋值不取最大</b>，所以 size 等于
    /// <b>最后一个</b>元素的 ID + 1；四个数组连同 <c>veinProtos</c> 都按它 new，
    /// 而 <c>GenerateVeins</c> 里的 <c>veinSpots</c> / <c>veinCount</c> / <c>veinOpacity</c>
    /// 又都是 <c>new [veinProtos.Length]</c>。矿种 ID 一旦大于等于这个长度就越界。
    ///
    /// <b>为什么不只靠排序。</b> <c>OreRegistry</c> 已经把矿种按 ID 升序排过、
    /// <c>VerifyVeinArrayOrder</c> 也核对过末位——但那是在 PostAddData 量的，
    /// 而 <c>PrepareWorks</c> 在更晚的 <c>PlanetModelingManager.Start</c> 才跑，
    /// 中间隔着 LDBTool 建表和别的 mod。**「我这一步做对了」不等于「最终状态是对的」**，
    /// 而这次崩溃恰好就发生在那两者之间。所以这里改成在最终状态上把关：
    /// 量一遍真实长度，不够就按原版自己的填充方式补齐。
    ///
    /// 补齐做的事和 <c>PrepareWorks</c> 尾巴上那个循环<b>逐字一致</b>
    /// （<c>veinProducts[p.ID] = p.MiningItem</c> 等四行），所以重跑一遍是幂等的。
    /// </summary>
    [HarmonyPatch]
    internal static class VeinProtoArrayPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetModelingManager), nameof(PlanetModelingManager.PrepareWorks))]
        private static void PrepareWorks()
        {
            VeinProto[] protos = LDB.veins?.dataArray;

            if (protos == null || protos.Length == 0)
            {
                ProjectEdenPlugin.Log.LogWarning("矿种数组核对：LDB.veins 是空的，没法核对");

                return;
            }

            var maxId = 0;
            var lastId = 0;

            foreach (VeinProto p in protos)
            {
                if (p == null) continue;

                if (p.ID > maxId) maxId = p.ID;

                lastId = p.ID;
            }

            int need = maxId + 1;
            int have = PlanetModelingManager.veinProtos?.Length ?? 0;

            // 无论够不够都报，否则「够用」和「这段代码没跑」分不开
            ProjectEdenPlugin.Log.LogInfo(
                $"矿种数组核对：矿种表 {protos.Length} 条，最大编号 {maxId}，末位编号 {lastId}；"
                + $"PlanetModelingManager 按 {have} 定容，需要 {need}"
                + (have >= need ? " → 够用" : " → **不够，正在补齐**"));

            if (have >= need) return;

            if (lastId != maxId)
                ProjectEdenPlugin.Log.LogWarning(
                    $"矿种表末位是 {lastId} 而最大是 {maxId} —— PrepareWorks 的定容循环是"
                    + "「赋值不取最大」，按末位定容所以short 了。矿种表的顺序在 PostAddData 之后被人改过");

            Grow(need);
            Fill(protos);

            ProjectEdenPlugin.Log.LogInfo($"矿种数组已补齐到 {need}，并按原版方式重填了 {protos.Length} 条");
        }

        private static void Grow(int need)
        {
            Resize(ref PlanetModelingManager.veinProducts, need);
            Resize(ref PlanetModelingManager.veinModelIndexs, need);
            Resize(ref PlanetModelingManager.veinModelCounts, need);

            VeinProto[] p = PlanetModelingManager.veinProtos;

            if (p == null || p.Length < need)
            {
                System.Array.Resize(ref p, need);
                PlanetModelingManager.veinProtos = p;
            }
        }

        private static void Resize(ref int[] array, int need)
        {
            if (array != null && array.Length >= need) return;

            System.Array.Resize(ref array, need);
        }

        /// <summary>和 <c>PrepareWorks</c> 尾巴上那个循环逐字一致，所以重跑是幂等的。</summary>
        private static void Fill(VeinProto[] protos)
        {
            foreach (VeinProto p in protos)
            {
                if (p == null || p.ID < 0 || p.ID >= PlanetModelingManager.veinProtos.Length) continue;

                PlanetModelingManager.veinProducts[p.ID] = p.MiningItem;
                PlanetModelingManager.veinModelIndexs[p.ID] = p.ModelIndex;
                PlanetModelingManager.veinModelCounts[p.ID] = p.ModelCount;
                PlanetModelingManager.veinProtos[p.ID] = p;
            }
        }
    }
}
