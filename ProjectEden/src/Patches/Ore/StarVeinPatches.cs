using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 按**星体类型**投放矿脉（黑洞、中子星这一类），而不是按星球主题。
    ///
    /// <b>为什么非要新开一条路：单极磁石根本不走主题表。</b> 这是量出来的，两条独立证据：
    ///
    /// <list type="number">
    /// <item>启动日志的主题表 25 张，<b>矿种 14（单极磁石）在稀有槽里出现 0 次</b>。</item>
    /// <item><c>PlanetAlgorithm.GenerateVeins</c> IL 016A 读 <c>planet.star.type</c>，
    /// 随后在 0333 起的分支里直接 <c>veinSpots[14]++</c> —— 硬编码，按星体类型。</item>
    /// </list>
    ///
    /// 所以 <c>ores.json</c> 的 <c>placement</c>（它只写 <c>ThemeProto.RareVeins</c>）
    /// 对黑洞矿是够不着的。<b>先前的设计稿以为这是个「查一下是数据还是代码」的问题，
    /// 答案是代码。</b>
    ///
    /// <b>关键约束：绝不能消耗原版的随机数流。</b> <c>GenerateVeins</c> 里有两个
    /// <c>DotNet35Random</c>（V_3 / V_5），而从构造到稀有矿抽取之间的抽取次数是
    /// <b>数据相关</b>的（CLAUDE.md 为 <c>RareVeinProspector</c> 记过这一条）。
    /// 在中间多抽一次，后面每一次抽取都会错位——**症状不是报错，是悄悄换一张矿图**，
    /// 而且只影响还没生成的星球，老存档看不出来。所以这里用自己的
    /// <c>DotNet35Random</c>，种子从 <c>planet.seed</c> 派生。
    ///
    /// <b>注入点选在三次 <c>Array.Copy</c> 之后</b>：那一段是直线代码，
    /// <c>veinSpots</c> / <c>veinCount</c> / <c>veinOpacity</c> 刚从主题表拷完、
    /// 星体类型分支还没开始，而真正消费它们的放置循环远在后面。
    /// 选 <c>GenBirthPoints</c> 当锚点是不行的——它在一个条件分支里。
    /// </summary>
    [HarmonyPatch]
    internal static class StarVeinPatches
    {
        /// <summary>
        /// 原版把这个方法抄了五份（主算法 + 四个变体），和 <c>OreVeinRangePatches</c>
        /// 面对的是同一张清单。少改一个，那一类星球就悄悄没有矿。
        /// </summary>
        private static readonly string[] Algorithms =
        {
            "PlanetAlgorithm", "PlanetAlgorithm7", "PlanetAlgorithm11",
            "PlanetAlgorithm12", "PlanetAlgorithm13",
        };

        /// <summary>一条按星体类型投放的矿脉。</summary>
        internal class StarVein
        {
            internal int VeinId;
            internal string Name;
            internal EStarType[] StarTypes;
            internal float Chance;
            internal int Spots;
            internal float Count;
            internal float Opacity;

            /// <summary>该星体类型下**保底至少出一处**，避免整局找不到。</summary>
            internal bool Guarantee;
        }

        internal static readonly List<StarVein> Veins = new List<StarVein>();

        private static int _rewritten;
        private static int _reported;

        internal static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in Algorithms)
            {
                Type type = AccessTools.TypeByName(name);
                MethodInfo method = type == null ? null : AccessTools.Method(type, "GenerateVeins");

                if (method != null) yield return method;
            }
        }

        /// <summary>
        /// 在三次 <c>Array.Copy</c> 之后插一次调用，把三张表和 <c>this</c> 递进去。
        ///
        /// 三个局部（<c>veinSpots</c> int[]、<c>veinCount</c> float[]、
        /// <c>veinOpacity</c> float[]）**是按形状找出来的，不是写死的下标**：
        /// 它们正是方法开头那三次 <c>newarr</c> 存进去的局部。写死 V_11 之类在
        /// 五个变体里未必一致，而错一个下标就是往别的数组里写数——不报错。
        /// </summary>
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpile(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);

            MethodInfo hook = AccessTools.Method(typeof(StarVeinPatches), nameof(AddSpots));

            // 先解析、拿不到就整条不改——绝不把 null 当操作数发出去，
            // 那会在 Harmony 的写出阶段炸，栈里指不到这里（CLAUDE.md 记过两次）
            if (hook == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"星体矿脉：{original?.DeclaringType?.Name} 的钩子方法解析不到，这一份不改写");

                return code;
            }

            // 三次 newarr 各自的目的地局部
            int spots = -1, count = -1, opacity = -1;

            for (var i = 0; i + 1 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Newarr) continue;

                var t = code[i].operand as Type;
                int local = LocalIndex(code[i + 1]);

                if (local < 0) continue;

                if (t == typeof(int) && spots < 0) spots = local;
                else if (t == typeof(float) && count < 0) count = local;
                else if (t == typeof(float) && opacity < 0) opacity = local;
            }

            if (spots < 0 || count < 0 || opacity < 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"星体矿脉：{original?.DeclaringType?.Name} 里没认出矿脉三张表"
                    + $"（spots={spots} count={count} opacity={opacity}），这一份不改写");

                return code;
            }

            // 锚点：第三次 Array.Copy。那之后三张表都已从主题表填好，且仍是直线代码
            MethodInfo copy = AccessTools.Method(
                typeof(Array), nameof(Array.Copy),
                new[] { typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int) });

            var seen = 0;
            var at = -1;

            for (var i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Call || !Equals(code[i].operand, copy)) continue;

                if (++seen != 3) continue;

                at = i + 1;

                break;
            }

            if (at < 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"星体矿脉：{original?.DeclaringType?.Name} 里只找到 {seen} 次 Array.Copy（期望 3），"
                    + "锚点不成立，这一份不改写");

                return code;
            }

            code.InsertRange(at, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldloc, spots),
                new CodeInstruction(OpCodes.Ldloc, count),
                new CodeInstruction(OpCodes.Ldloc, opacity),
                new CodeInstruction(OpCodes.Call, hook),
            });

            _rewritten++;

            return code;
        }

        private static int LocalIndex(CodeInstruction ins)
        {
            if (ins.opcode == OpCodes.Stloc_0) return 0;
            if (ins.opcode == OpCodes.Stloc_1) return 1;
            if (ins.opcode == OpCodes.Stloc_2) return 2;
            if (ins.opcode == OpCodes.Stloc_3) return 3;

            if (ins.opcode != OpCodes.Stloc && ins.opcode != OpCodes.Stloc_S) return -1;

            if (ins.operand is LocalBuilder builder) return builder.LocalIndex;
            if (ins.operand is int index) return index;

            return -1;
        }

        /// <summary>
        /// 真正干活的那一下：按星体类型给三张表填格子。
        ///
        /// <b>自带随机数，绝不碰原版那两个。</b> 种子从 <c>planet.seed</c> 派生，
        /// 所以同一个星球每次生成的结果一致（原版也是这么保证可重现的），
        /// 而原版的抽取序列一次都没被消耗——老存档和没装这个功能时的新星球，
        /// 矿脉分布一模一样。
        /// </summary>
        internal static void AddSpots(PlanetAlgorithm algo, int[] veinSpots, float[] veinCount,
            float[] veinOpacity)
        {
            if (Veins.Count == 0) return;

            PlanetData planet = algo?.planet;
            StarData star = planet?.star;

            if (star == null || veinSpots == null || veinCount == null || veinOpacity == null) return;

            foreach (StarVein vein in Veins)
            {
                if (Array.IndexOf(vein.StarTypes, star.type) < 0) continue;

                // 表长不够就跳过并吼——那说明 veinProtos 的定容没跟上矿脉数，
                // 静默写越界是崩溃，静默跳过是「整局找不到这种矿」，两种都不能要
                if (vein.VeinId >= veinSpots.Length || vein.VeinId >= veinCount.Length
                                                    || vein.VeinId >= veinOpacity.Length)
                {
                    ProjectEdenPlugin.Log.LogError(
                        $"星体矿脉：{vein.Name} 的矿种号 {vein.VeinId} 超出矿脉表长度"
                        + $"（spots {veinSpots.Length} / count {veinCount.Length} / opacity {veinOpacity.Length}），"
                        + "这颗星球上不投放。检查 VeinProtoArrayPatches 的定容");

                    continue;
                }

                // **自己的随机数**。异或一个和矿种绑定的常数，让两种矿在同一颗星球上
                // 各自独立掷骰，而不是同生共死。
                //
                // **先烧几次再播种第二个，这不是迷信——是原版自己的做法，而且实测非它不可。**
                // DotNet35Random 是 .NET Random 那套减法生成器的移植，**第一次抽取和种子
                // 强相关**；同一个星系里的 planet.seed 数值相近，于是直接
                // `new DotNet35Random(seed).NextDouble()` 会把结果挤在一小段区间里。
                // 实测：六颗星球的首次抽取是 0.533 / 0.584 / 0.606 / 0.800 / 0.908，
                // **全部大于 0.5**，chance 这个旋钮实际上是坏的，而矿照常出现——
                // 只看结果完全看不出来。
                //
                // GenerateVeins 开头 IL 003A–006A 正是这个形状：构造一个、连烧六次 Next()、
                // 再拿结果去播种真正用的那个。照抄它，不自创。
                var seeder = new DotNet35Random(planet.seed ^ (vein.VeinId * 0x5F37));

                for (var burn = 0; burn < 6; burn++) seeder.Next();

                var rand = new DotNet35Random(seeder.Next());
                double roll = rand.NextDouble();

                var byRoll = roll < vein.Chance;

                // 保底：这一类星体的第一颗行星必出一处，否则「极稀有」和「整局没有」
                // 在玩家那里是同一件事——莫桑石为这条付过一次账
                var byGuarantee = !byRoll && vein.Guarantee && planet.index == 0;

                if (!byRoll && !byGuarantee) continue;

                veinSpots[vein.VeinId] = vein.Spots;
                veinCount[vein.VeinId] = vein.Count;
                veinOpacity[vein.VeinId] = vein.Opacity;

                if (_reported >= 8) continue;

                _reported++;

                // 「掷中」和「保底捞回来」必须分得开：全靠保底说明 chance 偏低，
                // 而那正是只看结果看不出来的事——第一版把 < 写死在格式串里，
                // 打出来是「掷点 0.533 < 0.5，保底」，自相矛盾
                ProjectEdenPlugin.Log.LogInfo(
                    $"星体矿脉：{planet.displayName}（{star.type}）投放 {vein.Name} {vein.Spots} 处"
                    + (byRoll
                        ? $"（掷中：{roll:0.###} < {vein.Chance}）"
                        : $"（掷点 {roll:0.###} ≥ {vein.Chance}，靠首星保底）"));
            }
        }

        /// <summary>
        /// 开机状态行。<b>它回答「接上了没有」，事件行回答「它决定了什么」，
        /// 两者不能互相替代</b>——本仓库为这条付过六次账。
        /// </summary>
        /// <summary>
        /// 自检：这套种子派生到底均不均匀。
        ///
        /// <b>为什么值得花这一下。</b> 第一版直接 <c>new DotNet35Random(planet.seed).NextDouble()</c>，
        /// 实测六颗星球的首次抽取是 0.533 / 0.584 / 0.606 / 0.800 / 0.908——**全部大于 0.5，
        /// 而且挤在一小段里**。那意味着 <c>chance</c> 这个旋钮实际上是坏的，
        /// 而矿照常出现、功能看着完全正常。改成「烧六次再播种第二个」之后样本好转了，
        /// 但**几个样本证明不了分布**——真要回答就得自己跑一遍。
        ///
        /// 用游戏自己的 <c>DotNet35Random</c> 跑，不是拿别的实现近似：
        /// 近似出来的结论可能和真家伙悄悄不一致，那比不测还糟。
        /// </summary>
        private static void SelfTestRng()
        {
            const int Samples = 2000;

            var below = 0;
            var sum = 0.0;
            var q = new int[4];

            for (var i = 0; i < Samples; i++)
            {
                // 用真实星球种子的量级，不是 0..2000——种子相近正是原来出问题的条件
                int seed = unchecked(1000000 + i * 7919);
                var seeder = new DotNet35Random(seed ^ (25 * 0x5F37));

                for (var burn = 0; burn < 6; burn++) seeder.Next();

                double roll = new DotNet35Random(seeder.Next()).NextDouble();

                sum += roll;

                if (roll < 0.5) below++;

                q[roll >= 1.0 ? 3 : (int)(roll * 4)]++;
            }

            double mean = sum / Samples;
            var skew = Math.Abs(mean - 0.5) > 0.03 || Math.Abs(below - Samples / 2) > Samples / 20;

            string line = $"星体矿脉·随机自检：{Samples} 个种子，均值 {mean:0.###}，"
                          + $"低于 0.5 的 {below} 个（期望 {Samples / 2}），"
                          + $"四分位 {q[0]}/{q[1]}/{q[2]}/{q[3]}";

            if (skew)
                ProjectEdenPlugin.Log.LogWarning(
                    line + " —— **偏了**。chance 这个旋钮不是线性的，配的概率和实际出现率对不上");
            else
                ProjectEdenPlugin.Log.LogInfo(line + " —— 均匀，chance 可以当真正的概率用");
        }

        internal static void Report()
        {
            if (Veins.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo("星体矿脉：一条都没配置（ores.json 里没有 mode 为 star 的矿脉）");

                return;
            }

            // **装了银河尺度就整条不生效，而且不是这里坏了。** GS2 把原版的矿脉生成
            // 整个换掉（VeinAlgorithms.GenerateVeinsGS2 那一族），所以本补丁改写的五份
            // GenerateVeins 一次都不会被调用——「补丁成功、功能缺席」，本仓库最怕的形状。
            // 其余矿脉靠 OreGalacticScaleCompat 改写铁矿组来绕过，而按星体类型投放
            // 在 GS2 那边要另写一条路，还没做。先说出来，别让玩家自己去猜
            if (AccessTools.TypeByName("GalacticScale.VeinAlgorithms") != null)
                ProjectEdenPlugin.Log.LogWarning(
                    "星体矿脉：检测到银河尺度（GalacticScale）。它把矿脉生成整个换掉了，"
                    + "所以按星体类型投放的矿**在 GS2 星系里不会出现**——补丁是好的，只是那条路没人走。"
                    + "GS2 侧的对应实现还没做");

            // 自检无条件跑：它回答的是「chance 这个旋钮能不能当概率用」，
            // 和改写了几份没有关系
            SelfTestRng();

            if (_rewritten != Algorithms.Length)
                ProjectEdenPlugin.Log.LogError(
                    $"星体矿脉：应当改写 {Algorithms.Length} 份 GenerateVeins，实际 {_rewritten} 份。"
                    + "少改的那一类星球上不会有这些矿，而且不会报错");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"星体矿脉：{Algorithms.Length} 份 GenerateVeins 全部改写，共 {Veins.Count} 种矿"
                    + $"（{string.Join("、", Veins.ConvertAll(v => $"{v.Name}→{string.Join("/", Array.ConvertAll(v.StarTypes, s => s.ToString()))}").ToArray())}）");
        }
    }
}
