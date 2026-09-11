using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ProjectEden.Compatibility
{
    /// <summary>
    /// 装了银河尺度时，让本 mod 新增的矿脉照样能生成。
    ///
    /// <b>GS2 完全接管了矿脉生成</b>：GenerateVeinsGS2 / GenerateVeinsGS2W / GenerateVeinsVanilla
    /// 加上它自己的 GSTheme 矿脉表，原版的 PlanetAlgorithm.GenerateVeins <b>根本不执行</b>。
    /// 所以 OreVeinRangePatches 把原版循环上界抬上去、OreRegistry 往 ThemeProto 里塞的那几格，
    /// 在这套 mod 组合下都走不到——补丁打上了，路没人走。
    ///
    /// <b>改成在出口处转换，不碰 GS2 的分配逻辑。</b> GS2 的两条路最后都汇进
    ///     InitializeVeinGroup(i, veinType, position, planet)      —— 定一个矿脉簇的矿种
    ///     AddVeinToPlanet(amount, veinType, position, groupIndex, planet) —— 往簇里加矿点
    /// 这里在这两个入口把<b>一部分铁矿脉簇改判成自定义矿种</b>。好处是 GS2 从头到尾不需要
    /// 知道这些矿的存在：稀有度、分布、避让、锁定矿种那些逻辑全按铁算完了，我们只在最后换个类型。
    ///
    /// 换成哪一种由 (星球 ID, 簇序号) 的确定性散列落进哪个区间决定——同一个星球每次读档
    /// 结果一样，不会出现「上次这里是钴，这次变回铁」。多个矿种按各自配置的稀有度分区间：
    /// 要让 铁 : 矿A : 矿B = 1 : rA : rB，矿A 就占 rA/(1+ΣR)、矿B 占 rB/(1+ΣR)，剩下的留给铁。
    ///
    /// GS2 内部按矿种查的两个表（veinModelIndexs / veinModelCounts）都来自
    /// PlanetModelingManager，长度按 LDB.veins 最大 ID + 1 算，新矿种不会越界。
    /// </summary>
    internal static class OreGalacticScaleCompat
    {
        /// <summary>散列落点的分桶：按 <see cref="End"/> 升序排，命中第一个 h &lt; End 的桶。</summary>
        private struct Bucket
        {
            internal int End;
            internal EVeinType Type;
        }

        private static Bucket[] _buckets = new Bucket[0];

        // 诊断：按星球统计「铁矿脉簇看到了几个 / 改判了几个」。
        // 之前只在第一次转换时打一行，结果完全看不出「某个星球到底有没有被处理过」——
        // 而这正是要判断的东西。前若干个星球逐个报，之后不再刷屏。
        private static readonly Dictionary<int, int[]> Seen = new Dictionary<int, int[]>();

        private const int ReportPlanets = 12;

        internal static void ApplyPatches(Harmony harmony)
        {
            if (harmony == null) return;
            if (!GalacticScaleCompat.Installed) return;
            if (!OreRegistry.Enabled) return;

            Type veinAlgorithms = AccessTools.TypeByName("GalacticScale.VeinAlgorithms");

            if (veinAlgorithms == null)
            {
                ProjectEdenPlugin.Log.LogWarning("检测到银河尺度，但找不到 GalacticScale.VeinAlgorithms，新矿脉在 GS2 星系里不会生成");

                return;
            }

            if (!BuildBuckets()) return;

            var ok = 0;

            ok += Patch(harmony, veinAlgorithms, "InitializeVeinGroup", nameof(InitializeVeinGroup_Prefix));
            ok += Patch(harmony, veinAlgorithms, "AddVeinToPlanet", nameof(AddVeinToPlanet_Prefix));

            if (ok == 2)
                ProjectEdenPlugin.Log.LogInfo($"银河尺度矿脉生成已接管：{Describe()}");
            else
                ProjectEdenPlugin.Log.LogError("银河尺度矿脉生成接管不完整，新矿脉可能不生成或与铁矿脉混簇");
        }

        /// <summary>
        /// 改判掉的铁矿脉簇最多占多大比例。<b>这道封顶是必需的，不是保险。</b>
        ///
        /// 原版路径和 GS2 路径对铁矿的影响根本不同：原版是往主题里<b>加</b>矿脉位，铁一点没少；
        /// GS2 这条是把铁矿脉簇<b>改判</b>过去，加的每一分都是从铁身上割的。
        /// 转换比例是 <c>Σr / (1 + Σr)</c>，而 <c>veinRarity</c> 是按「原版语义」配的——
        /// 矿种一多，Σr 轻易就到 7 以上，换算过去就是 <b>88% 的铁矿脉消失</b>。
        /// 所以这里按比例整体缩放，把总改判量摁在这个上限内，各矿之间的相对比例不变。
        /// </summary>
        private const float MaxIronConversion = 0.5f;

        /// <summary>按各矿种的稀有度切分 0~9999 的散列区间；返回 false 表示没有可转换的矿种。</summary>
        private static bool BuildBuckets()
        {
            var rarities = new List<KeyValuePair<OreRegistry.Ore, float>>();
            var total = 0f;

            foreach (OreRegistry.Ore ore in OreRegistry.Ores)
            {
                // 占稀有槽的矿种（钴、钒那种「整颗星球要么有要么没有」的）不按稀有度权重算——
                // 那个值在 rare 模式下本来就不参与计算。改用它的出现概率当权重，
                // 数量级才对得上，不然一个 veinRarity=4 的稀有矿会把铁吃光。
                Utils.PlacementEntry place = ore.Entry.placement;
                bool rare = place != null && place.mode == "rare";

                float rarity = rare
                    ? (place.chance > 0f ? place.chance : 0.1f)
                    : ore.Entry.veinRarity;

                if (rarity <= 0f) rarity = 0.35f;

                rarities.Add(new KeyValuePair<OreRegistry.Ore, float>(ore, rarity));
                total += rarity;
            }

            if (rarities.Count == 0 || total <= 0f)
            {
                ProjectEdenPlugin.Log.LogWarning("没有启用任何自定义矿种，银河尺度矿脉转换未接管");

                return false;
            }

            // Σr/(1+Σr) 超过上限就整体等比缩小。反解：Σr ≤ cap/(1-cap)
            float allowed = MaxIronConversion / (1f - MaxIronConversion);
            var scale = 1f;

            if (total > allowed)
            {
                scale = allowed / total;

                ProjectEdenPlugin.Log.LogWarning(
                    $"银河尺度：各矿稀有度之和 {total:0.##} 会让 {total / (1f + total):P0} 的铁矿脉簇被改判，" +
                    $"已整体缩放到上限 {MaxIronConversion:P0}（各矿相对比例不变）。" +
                    "原版路径是往主题里加矿脉位、不动铁，只有 GS2 这条是从铁身上割——所以矿种一多就得封顶");

                total *= scale;
            }

            _buckets = new Bucket[rarities.Count];

            var end = 0f;

            for (var i = 0; i < rarities.Count; i++)
            {
                // 转走 r/(1+ΣR)，剩下的铁与各矿之比才是 1 : r1 : r2 : …
                end += rarities[i].Value * scale / (1f + total) * 10000f;

                _buckets[i] = new Bucket
                {
                    End = (int)end,
                    Type = (EVeinType)rarities[i].Key.VeinId,
                };
            }

            return true;
        }

        private static string Describe()
        {
            var text = "";
            var prev = 0;

            for (var i = 0; i < _buckets.Length; i++)
            {
                OreRegistry.Ore ore = OreRegistry.Find((int)_buckets[i].Type);

                if (i > 0) text += "，";

                text += $"{(_buckets[i].End - prev) / 100f:0.#}% 的铁矿脉簇改判为{ore?.Entry.veinName ?? _buckets[i].Type.ToString()}（矿种 {(int)_buckets[i].Type}）";

                prev = _buckets[i].End;
            }

            return text;
        }

        private static int Patch(Harmony harmony, Type type, string method, string prefix)
        {
            MethodInfo target = AccessTools.Method(type, method);

            if (target == null)
            {
                ProjectEdenPlugin.Log.LogWarning($"找不到 GalacticScale.VeinAlgorithms.{method}，该入口未接管");

                return 0;
            }

            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(OreGalacticScaleCompat), prefix)));

                return 1;
            }
            catch (Exception e)
            {
                ProjectEdenPlugin.Log.LogError($"给 GalacticScale.VeinAlgorithms.{method} 挂前置失败：{e}");

                return 0;
            }
        }

        // Harmony 按<b>参数名</b>注入，所以这里的名字必须和 GS2 的签名逐字一致：
        //   InitializeVeinGroup(int i, EVeinType veinType, Vector3 position, PlanetData planet)
        //   AddVeinToPlanet(int amount, EVeinType veinType, Vector3 position, short groupIndex, PlanetData planet)

        public static void InitializeVeinGroup_Prefix(ref EVeinType veinType, int i, PlanetData planet) =>
            Convert(ref veinType, i, planet);

        public static void AddVeinToPlanet_Prefix(ref EVeinType veinType, short groupIndex, PlanetData planet) =>
            Convert(ref veinType, groupIndex, planet);

        private static void Convert(ref EVeinType veinType, int groupIndex, PlanetData planet)
        {
            // 只动铁矿脉：其他矿种照原样，GS2 的分布逻辑一点不受影响
            if (veinType != EVeinType.Iron || planet == null) return;

            int hash = Hash(planet.id, groupIndex);
            var converted = false;

            for (var i = 0; i < _buckets.Length; i++)
            {
                if (hash >= _buckets[i].End) continue;

                veinType = _buckets[i].Type;
                converted = true;

                break;
            }

            Record(planet, converted);
        }

        /// <summary>逐星球记账，前若干个星球各报一行：看到多少铁、改判多少。</summary>
        private static void Record(PlanetData planet, bool converted)
        {
            lock (Seen)
            {
                if (!Seen.TryGetValue(planet.id, out int[] tally))
                {
                    if (Seen.Count >= ReportPlanets) return;

                    tally = new int[3];
                    Seen[planet.id] = tally;
                }

                tally[0]++;

                if (converted) tally[1]++;

                // 第一次经手这颗星球时就报一行，别等「铺完」——没有可靠的结束信号
                if (tally[2] != 0) return;

                tally[2] = 1;

                ProjectEdenPlugin.Log.LogInfo($"新矿脉生成：正在处理 {planet.displayName}（id {planet.id}）的铁矿脉");
            }
        }

        /// <summary>
        /// (星球 ID, 簇序号) 的确定性散列，落在 0~9999。同一个星球每次生成都得到同样的判定，
        /// 所以读档前后矿脉不会变来变去；不同星球之间又是打散的。
        /// </summary>
        private static int Hash(int planetId, int groupIndex)
        {
            var h = (uint)(planetId * 73856093 ^ groupIndex * 19349663);

            h ^= h >> 13;
            h *= 2654435761u;
            h ^= h >> 16;

            return (int)(h % 10000);
        }
    }
}
