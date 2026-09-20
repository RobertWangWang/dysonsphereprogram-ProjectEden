using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// <b>掉在地上的东西在放大的星球上到处漂流，根因有两处，这里修第二处。</b>
    ///
    /// <para>第一处在 <see cref="PlanetRadiusPatches"/>：<c>AstroData.uRadius</c> 停在 200，
    /// 于是「地面」被取成 200.35，掉落物永远落不了地。那一条修好之后还剩这里——
    /// <c>TrashSystem.Gravity</c> 里有三个<b>按半径 200 写死</b>的常量。</para>
    ///
    /// <para>方法的骨架（V_7 = <c>astroPoses[i].uRadius</c>，V_9 = 掉落物到该天体中心的距离）：</para>
    /// <code>
    /// V_11 = 1;
    /// if (V_9 &lt; 800)                       // @0125
    ///     V_11 = Pow(10, (800 - V_9) / 150); // @0139 @0145  重力强度斜坡
    /// V_12 = V_7 + 0.35;                     // 默认「地面」＝一个光滑球面
    /// if (i == localPlanetId &amp;&amp; V_9 &lt; 210)   // @0177
    ///     V_12 = QueryModifiedHeight(dir) + 0.15;   // 只有这里才查真实地形
    /// if (V_9 &lt; V_12) { if (V_12 &gt; 600) 丢弃; else 落地 }  // @01FF
    /// </code>
    ///
    /// <para><b>三个常量在半径 400 上各自坏在哪：</b></para>
    /// <list type="number">
    /// <item><b>210</b>（查不查真实地形的闸）——我们的地表在 400.x，<c>V_9 &lt; 210</c>
    /// 永远不成立，于是<b>地形高度根本不查</b>，「地面」退化成半径 400.35 的光滑球：
    /// 山上的物品埋进去、谷里的浮在空中。</item>
    /// <item><b>800 / 150</b>（重力斜坡）——原版地表 200 处是
    /// <c>Pow(10, 600/150) = 10⁴</c>，我们地表 400 处只有 <c>Pow(10, 400/150) ≈ 464</c>，
    /// <b>重力弱了 21 倍</b>，掉落物压不住切向速度，贴着地面滑走。</item>
    /// <item><b>600</b>（能不能落地的判据，实际是「行星还是气态巨星」）——
    /// 气态巨星 <c>uRadius</c> 是 800，地面 800.35 &gt; 600 所以被排除。
    /// 半径 400 时我们的 400.35 仍 &lt; 600，<b>这一条现在还没坏</b>，
    /// 但半径一旦到 600 我们自己也会被当成气态巨星、掉落物直接消失。一并按同样规则修掉。</item>
    /// </list>
    ///
    /// <para><b>修法：每个常量加上「这颗天体比原版大出来的那一截」。</b>
    /// 半径 200 时增量为 0，<b>返回值和原版逐位相同</b>；半径 400 时三个常量变成
    /// 410 / 1000 / 800，于是「地表处的重力」「查地形的距离」「可落地的高度」
    /// 三件事都和原版在它自己地表上时完全一致。<c>150</c>（斜坡陡度）不动——
    /// 它是每十倍衰减多少格，和半径无关。</para>
    ///
    /// <para><b>判据是按天体 id 的标记表，不是半径值。</b>
    /// <c>astrosData</c> 里恒星和行星混在一起，而恒星的 <c>uRadius</c> 是
    /// <c>radius × 1200</c>、取值几百到上千，<b>有可能正好等于我们的半径</b>。
    /// 见 <see cref="PlanetRadiusPatches.IsResizedAstro"/>。</para>
    /// </summary>
    [HarmonyPatch(typeof(TrashSystem), nameof(TrashSystem.Gravity))]
    internal static class PlanetTrashGravityPatches
    {
        /// <summary>那三个常量各自出现几次，改写前后都断言这个数。</summary>
        private static readonly Dictionary<double, int> Expected = new Dictionary<double, int>
        {
            { 800.0, 2 },   // @0125 比较、@0139 Pow 的分子
            { 210.0, 1 },   // @0177 查地形的闸
            { 600.0, 1 },   // @01FF 行星 / 气态巨星的判据
        };

        private static int _rewritten;

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            if (!PlanetRadiusPatches.Active) return code;

            var fix = AccessTools.Method(typeof(PlanetTrashGravityPatches), nameof(ShiftForAstro));

            // 「装载这颗天体的下标」那条指令——直接从原方法体里抄一条，
            // 而不是自己造 ldloc：局部变量的下标在转译器里不是稳定可写的东西，
            // 抄一条现成的既准确又不依赖 Harmony 的版本差异。
            // 锚点：`ldarg.2 ; <装载下标> ; ldelema AstroData`（@003A–003D）。
            CodeInstruction loadIndex = null;

            for (int i = 0; i + 2 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldarg_2) continue;
                if (code[i + 2].opcode != OpCodes.Ldelema) continue;

                loadIndex = code[i + 1];

                break;
            }

            if (fix == null || loadIndex == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "行星放大·掉落物重力：找不到修正函数或「装载天体下标」的锚点，未改写。"
                    + "放大的星球上掉在地上的东西会到处漂流");

                return code;
            }

            var seen = new Dictionary<double, int>();

            foreach (CodeInstruction c in code)
            {
                if (c.opcode != OpCodes.Ldc_R8 || !(c.operand is double d)) continue;
                if (!Expected.ContainsKey(d)) continue;

                seen[d] = seen.TryGetValue(d, out int n) ? n + 1 : 1;
            }

            foreach (var kv in Expected)
            {
                if (seen.TryGetValue(kv.Key, out int got) && got == kv.Value) continue;

                ProjectEdenPlugin.Log.LogError(
                    $"行星放大·掉落物重力：常量 {kv.Key:0} 应出现 {kv.Value} 次，实际 "
                    + $"{(seen.TryGetValue(kv.Key, out int g) ? g : 0)} 次，**一处都不改**。"
                    + "游戏版本可能变了——放大的星球上掉落物会到处漂流");

                return code;
            }

            int done = 0;

            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldc_R8 || !(code[i].operand is double d)) continue;
                if (!Expected.ContainsKey(d)) continue;

                code.Insert(i + 1, new CodeInstruction(loadIndex.opcode, loadIndex.operand));
                code.Insert(i + 2, new CodeInstruction(OpCodes.Call, fix));

                done++;
                i += 2;
            }

            System.Threading.Interlocked.Exchange(ref _rewritten, done);

            return code;
        }

        /// <summary>
        /// 把按半径 200 写死的常量加上这颗天体比原版大出来的那一截。
        /// <b>没被我们放大的天体（恒星、气态巨星、别的 mod 动过的）原样返回</b>，
        /// 半径 200 时增量为 0，所以这个函数在原版语义下是恒等的。
        /// </summary>
        internal static double ShiftForAstro(double vanillaConst, int astroId)
        {
            if (!PlanetRadiusPatches.IsResizedAstro(astroId)) return vanillaConst;

            return vanillaConst + (PlanetRadiusPatches.Radius - 200.0);
        }

        internal static void Report()
        {
            if (!PlanetRadiusPatches.Active) return;

            int n = System.Threading.Interlocked.CompareExchange(ref _rewritten, 0, 0);

            if (n == 4)
                ProjectEdenPlugin.Log.LogWarning(
                    "行星放大·掉落物重力：四处按半径 200 写死的常量已改写"
                    + $"（210 → {210 + PlanetRadiusPatches.Radius - 200}、"
                    + $"800 ×2 → {800 + PlanetRadiusPatches.Radius - 200}、"
                    + $"600 → {600 + PlanetRadiusPatches.Radius - 200}）。"
                    + "原版把「查不查真实地形」的距离闸和重力斜坡都钉在 200 上——"
                    + "**放大之后掉落物既查不到地形、重力又弱 21 倍，于是贴着地面漂走**");
            else
                ProjectEdenPlugin.Log.LogError(
                    $"行星放大·掉落物重力：只改写了 {n}/4 处，**掉落物仍会漂流**");
        }
    }
}
