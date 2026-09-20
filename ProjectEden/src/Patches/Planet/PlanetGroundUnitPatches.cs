using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// <b>地面单位的飞行高度被夹在「半径 200 的地面之上几格」，放大之后等于按进地里。</b>
    ///
    /// <para>玩家报的是「黑雾产生的敌人在 2 倍星球上掉到地面下」。根因是一批
    /// <b>绝对高度常量</b>——它们写的是「离星球中心多远」，而不是「离地面多高」，
    /// 所以隐含了半径 200。典型的一处是
    /// <c>EnemyUnitComponent.RunBehavior_Engage_GRaider</c> @052C–0553：</para>
    /// <code>
    /// if (alt + d &gt; 206) d = 206 - alt;        // 上夹
    /// else if (alt + d &lt; 202) d = 202 - alt;   // 下夹
    /// </code>
    /// 在原版那是「地面之上 2–6 格」；在半径 400 的星球上地面在 400 左右，
    /// 这个夹子把单位按到 <b>地下约 200 格</b>。
    ///
    /// <para><b>同一个 200 有两种含义，所以不能按值一刀切</b>（本文件记过的 trap 2）。
    /// 同一个方法里 @0260 的 <c>alt = max(alt, 200)</c> 是个<b>下限</b>，
    /// 在半径 400 上永远不触发、无害。区别不在值，在形状。</para>
    ///
    /// <para><b>判据：在这六个方法里，190–230 之间的浮点字面量全部是行星尺度的高度。</b>
    /// 这是数出来的，而且带外的邻居正好反证了它——<c>GGuardian</c> 里的 255/165 是颜色
    /// （橙 255,165,0）、<c>GRaider</c> 里的三个 270 是角度，**都落在带外**。
    /// 所以在这六个方法内按统一规则平移是安全的，而扩大波段就不再安全。</para>
    ///
    /// <para><b>平移而不是缩放。</b>「地面之上 2–6 格」在半径 400 上应该是 402–406，
    /// 不是 412。而 <c>GRanger</c> 里那两个 <c>vel / 200</c>（线速度换角速度）除数恰好就是
    /// 半径本身，平移后正好变成 400——两种用法在这六个方法里都对，因为除数那两处的值
    /// 正好是 200。<b>半径 200 时增量为 0，和原版逐位相同。</b></para>
    ///
    /// <para>六个方法的 <c>arg1</c> 都是 <c>PlanetFactory factory</c>（全是实例方法），
    /// 所以现取这颗星的半径不需要额外查找。</para>
    ///
    /// <para><b>另外补一道兜底</b>：见 <see cref="RescueToTerrain"/>。这一类常量很可能没被
    /// 穷尽（太空单位那一族形状相同但语义还没逐个读过），所以除了修已确认的这 18 处，
    /// 还把原版自己的「掉到地下捞回来」改成按<b>真实地形高度</b>兜底。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class PlanetGroundUnitPatches
    {
        /// <summary>
        /// 每个方法里应当改写几处。数目不对就整体放弃并大声失败——
        /// <b>改一半的后果是同一个单位的上下夹用了不同的基准</b>，那种错不会报，
        /// 只会让敌人以更怪的方式卡住。
        /// </summary>
        private static readonly Dictionary<string, int> Expected = new Dictionary<string, int>
        {
            { "RunBehavior_Engage_GRaider", 6 },              // 200 ×2（下限）+ 206 ×2 + 202 ×2（区间夹）
            { "RunBehavior_Engage_GGuardian", 2 },            // 228 ×2（区间夹）
            { "RunBehavior_Engage_GRanger", 4 },              // 225 + 212（区间）+ 200 ×2（线速度换角速度的除数）
            { "RunBehavior_Engage_AttackLaser_Ground", 2 },   // 225 + 212
            { "RunBehavior_Engage_AttackPlasma_Ground", 2 },  // 同上
            { "RunBehavior_Engage_DefenseShield_Ground", 2 }, // 同上
        };

        private const float BandLo = 190f;
        private const float BandHi = 230f;

        private static int _rewritten;

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.RunBehavior_Engage_GRaider))]
        [HarmonyPatch(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.RunBehavior_Engage_GGuardian))]
        [HarmonyPatch(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.RunBehavior_Engage_GRanger))]
        [HarmonyPatch(typeof(UnitComponent), nameof(UnitComponent.RunBehavior_Engage_AttackLaser_Ground))]
        [HarmonyPatch(typeof(UnitComponent), nameof(UnitComponent.RunBehavior_Engage_AttackPlasma_Ground))]
        [HarmonyPatch(typeof(UnitComponent), nameof(UnitComponent.RunBehavior_Engage_DefenseShield_Ground))]
        private static IEnumerable<CodeInstruction> ShiftGroundAltitudes(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);

            if (!PlanetRadiusPatches.Active) return code;

            MethodInfo fix = AccessTools.Method(typeof(PlanetGroundUnitPatches), nameof(ShiftAlt));

            if (fix == null || !Expected.TryGetValue(original.Name, out int want))
            {
                ProjectEdenPlugin.Log.LogError(
                    $"行星放大·地面单位高度：{original.Name} 不在预期表里或修正函数解析不到，未改写");

                return code;
            }

            int found = 0;

            foreach (CodeInstruction c in code)
            {
                if (c.opcode != OpCodes.Ldc_R4 || !(c.operand is float f)) continue;
                if (f < BandLo || f > BandHi) continue;

                found++;
            }

            if (found != want)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"行星放大·地面单位高度：{original.DeclaringType?.Name}.{original.Name} 里 "
                    + $"190–230 的浮点常量应有 {want} 处，实际 {found} 处，**一处都不改**。"
                    + "游戏版本可能变了——放大的星球上地面敌人会掉到地下");

                return code;
            }

            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldc_R4 || !(code[i].operand is float f)) continue;
                if (f < BandLo || f > BandHi) continue;

                // arg1 在这六个方法里都是 PlanetFactory（全是实例方法）
                code.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_1));
                code.Insert(i + 2, new CodeInstruction(OpCodes.Call, fix));

                i += 2;
            }

            System.Threading.Interlocked.Add(ref _rewritten, found);

            return code;
        }

        /// <summary>
        /// 把「离星球中心多远」的绝对高度常量平移到这颗星的地面上。
        /// 没被我们放大的行星原样返回；半径 200 时增量为 0。
        /// </summary>
        internal static float ShiftAlt(float vanilla, PlanetFactory factory)
        {
            PlanetData p = factory?.planet;

            if (p == null || !PlanetRadiusPatches.IsResizedAstro(p.id)) return vanilla;

            return vanilla + (PlanetRadiusPatches.Radius - 200f);
        }

        /// <summary>
        /// <b>兜底：把原版自己的「掉到地下捞回来」改成按真实地形高度。</b>
        ///
        /// <para>原版 <c>UndergroundRescue</c>（23 条指令）把低于 <c>realRadius + 0.2</c> 的单位
        /// 拉回那个高度——<b>它是跟着半径走的，本身没错</b>，但它兜的是<b>海平面</b>，
        /// 不是地面。山上地形在 400.9 时，一个卡在 400.5 的单位仍然埋在土里。</para>
        ///
        /// <para>原版不在乎这个差别，是因为它从来不会把单位放到那么低；而<b>放大之后，
        /// 任何一处没被发现的「绝对高度常量」都会把单位塞进地下</b>——这一族我已经
        /// 数过六个方法十八处，但太空单位那一批形状相同、语义还没逐个读过。
        /// 所以这里按真实地形兜一道底：**已知的修掉，未知的至少不会永远埋着。**</para>
        ///
        /// <para>只在放大过的行星上生效，原版尺寸下一个字节都不动——
        /// 改变原版在普通星球上的行为不是这个功能的职责。</para>
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnemyUnitComponent), nameof(EnemyUnitComponent.UndergroundRescue))]
        private static void RescueToTerrain(PlanetData planetData, ref EnemyData enemy)
        {
            if (!PlanetRadiusPatches.Active) return;
            if (planetData?.data == null) return;
            if (!PlanetRadiusPatches.IsResizedAstro(planetData.id)) return;

            VectorLF3 pos = enemy.pos;
            double mag = pos.magnitude;

            if (mag < 1.0) return;

            UnityEngine.Vector3 dir =
                new UnityEngine.Vector3((float)(pos.x / mag), (float)(pos.y / mag), (float)(pos.z / mag));

            float ground = planetData.data.QueryModifiedHeight(dir) + 0.2f;

            if (mag >= ground) return;

            enemy.pos = new VectorLF3(dir.x * ground, dir.y * ground, dir.z * ground);
        }

        internal static void Report()
        {
            if (!PlanetRadiusPatches.Active) return;

            int n = System.Threading.Interlocked.CompareExchange(ref _rewritten, 0, 0);

            if (n == 18)
                ProjectEdenPlugin.Log.LogWarning(
                    "行星放大·地面单位高度：六个方法共 18 处绝对高度常量已平移"
                    + $"（+{PlanetRadiusPatches.Radius - 200}）。原版把地面单位的飞行高度夹在"
                    + "「离星球中心 202–228」这种绝对区间里，那隐含了半径 200——"
                    + "**放大之后等于把黑雾地面单位按进地里**。"
                    + "另有一道按真实地形的兜底挂在 UndergroundRescue 上");
            else
                ProjectEdenPlugin.Log.LogError(
                    $"行星放大·地面单位高度：只改写了 {n}/18 处，**地面敌人仍可能掉到地下**");
        }
    }
}
