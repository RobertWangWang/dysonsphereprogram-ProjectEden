using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// <b>地基基准面写死在半径 200 上，这是「放大后出生点是个洞」的真正根因。</b>
    ///
    /// <para><c>PlanetRawData.GetModPlane</c> 一共 24 条指令，末尾是：</para>
    /// <code>
    /// plane = (modData[index >> 1] >> …) &amp; 3;      // 0..3，两个 bit
    /// return (short)(plane * 133 + 20020);           // ← 20020 写死
    /// </code>
    /// <c>20020</c> 就是 <c>(200 + 0.2) × 100</c>——<b>半径 200 那颗星的地基基准面</b>，
    /// 用的是全仓库地形通用的「高度 × 100」单位。<c>133</c> 是每级 1.33 格。
    /// 全程序集里 <c>20020</c> <b>只出现这一处</b>（枚举所得）。
    ///
    /// <para><b>写入侧是对的，只有读取侧写死。</b>
    /// <c>PlanetFactory.FlattenTerrain</c> @0578–059C 算等级用的是
    /// <c>RoundToInt((pos.magnitude - 0.2 - <b>realRadius</b>) / 1.333333)</c>——跟着半径走；
    /// <c>FlattenTerrainReform</c> @00E4–0108 同形。所以地基等级存得没错，
    /// <b>错在把它读回来的时候拿 200.2 当基准</b>。</para>
    ///
    /// <para><b>放大之后的后果</b>：半径 400 的星球上，地基面仍然返回 200.2–204.19，
    /// 比真实地表低整整 200 格。而渲染和碰撞的高度是
    /// <c>h × (1-t) + 地基面 × t</c>（<c>ModelingPlanetMain</c> @0BB9–0BC8、
    /// <c>UpdateDirtyMeshVertices</c> @0126–0135），只要某格 <c>modLevel &gt; 0</c>
    /// 顶点就被拉向 200.2 —— <b>地面塌进星球内部，人走过去直接掉穿</b>。
    /// 实测那一片网格顶点的最低值正是 <b>200.200</b>，分毫不差。
    ///
    /// <para>而开局唯一会设置 <c>modLevel</c> 的地方，是
    /// <c>PlanetFactory.BuildFinally</c> @00A2 给初始部署仓调的 <c>FlattenTerrain</c>——
    /// 所以洞永远正好在出生点，大小就是那块整平区。这解释了之前所有互相矛盾的观测：
    /// <c>heightData</c> 没被动过所以是干的、<c>QueryHeight</c> 读 heightData 所以说没水、
    /// 几何塌了所以透出海洋球、人走过去掉下去。</para>
    ///
    /// <para><b>为什么修消费方而不是 <c>GetModPlane</c> 本身：它的返回类型是 Int16。</b>
    /// 正确值 <c>(realRadius + 0.2) × 100</c> 在半径 400 时是 40020，
    /// 超过 Int16 上限 32767，改常量会直接溢出成负数。
    /// 所以在三个消费方那里把原值<b>加上一个 float 偏移</b>，绕开这个宽度。</para>
    ///
    /// <para><b>消费方是闭集，一共三处</b>（枚举所得，不是估的）：</para>
    /// <list type="bullet">
    /// <item><c>PlanetModelingManager.ModelingPlanetMain</c> @0BA8 —— 建网格；</item>
    /// <item><c>PlanetData.UpdateDirtyMeshVertices</c> @0111 —— 地形改动后重建那一块
    /// （<b>0.10.35 之前这一段在 <c>UpdateDirtyMesh</c> 里</b>，那一版拆开了）；</item>
    /// <item><c>PlanetRawData.QueryModifiedHeight</c> @00C6 —— 查「算上地基之后的高度」。</item>
    /// </list>
    /// 三处都把返回值当「高度 × 100」用（第三处全程在原始单位里插值，@00B2–00FD），
    /// 所以<b>同一个加法修正覆盖全部三处</b>。
    ///
    /// <para><b>单独一个类，因为它用 <c>TargetMethods</c>。</b>
    /// <c>verify_harmony.ps1</c> 的第一项检查就是「<c>TargetMethods</c> 不能和逐个注解
    /// 共处一类」，而 <see cref="PlanetRadiusPatches"/> 里全是逐个注解。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class PlanetModPlanePatches
    {
        /// <summary>原版写死的基准面：<c>(200 + 0.2) × 100</c>。</summary>
        private const int StockPlaneBase = 20020;

        private static int _rewritten;

        /// <summary>
        /// 三个目标都按<b>参数类型</b>挑，不用裸名字：本仓库记过裸名字撞上重载会抛
        /// <c>AmbiguousMatchException</c> 并把整个 mod 带下水。这三个参数类型
        /// （<c>PlanetData</c> / <c>int</c> / <c>Vector3</c>）都没被本仓库的 preloader 加宽过，
        /// 写死是安全的。
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlanetModelingManager), "ModelingPlanetMain",
                new[] { typeof(PlanetData) });

            // **0.10.35 把这一段从 UpdateDirtyMesh 拆进了 UpdateDirtyMeshVertices。**
            // 两个方法都还在、签名都是 (int dirtyIdx)、都是 PlanetData 的实例方法，
            // 所以只有名字变了；但旧名字里已经没有 GetModPlane 了，继续指着它的后果是
            // **只改写 2/3 处、地基照样把地面拉到 200.2**——也就是当年查了七轮的那个
            // 「出生点是个洞」。它是靠计数大声失败才被抓到的，日志里那行写着
            // 「应为 1 处，实际 0 处」。
            yield return AccessTools.Method(typeof(PlanetData), "UpdateDirtyMeshVertices",
                new[] { typeof(int) });

            yield return AccessTools.Method(typeof(PlanetRawData), "QueryModifiedHeight",
                new[] { typeof(UnityEngine.Vector3) });
        }

        /// <summary>
        /// 把每处 <c>GetModPlane</c> 之后那条 <c>conv.r4</c> 换成
        /// 「<c>ldarg.0</c> + 调我们的修正函数」。
        ///
        /// <para><b>改写的是已有那条指令本身，不是删掉重建</b>——分支标签挂在指令对象上，
        /// 删掉会丢。插入的那条放在它后面，Harmony 的 <c>ILGenerator</c> 会自己重算偏移，
        /// 所以这里不存在 preloader 那边「插指令撑爆短跳转」的问题（那是 Cecil 直写字节的坑）。</para>
        ///
        /// <para>三个方法的 <c>ldarg.0</c> 分别是什么，是查过的：
        /// <c>ModelingPlanetMain</c> 是静态方法、首参 <c>PlanetData</c>；
        /// <c>UpdateDirtyMeshVertices</c> 是 <c>PlanetData</c> 的实例方法；
        /// <c>QueryModifiedHeight</c> 是 <c>PlanetRawData</c> 的实例方法。
        /// 所以按 arg0 的类型选对应的重载。</para>
        /// </summary>
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);

            MethodInfo target = AccessTools.Method(typeof(PlanetRawData), nameof(PlanetRawData.GetModPlane));

            if (target == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "行星放大·地基基准面：解析不到 PlanetRawData.GetModPlane，未改写。"
                    + "放大后的星球上地基会把地面拉到 200.2，出生点会是个洞");

                return code;
            }

            // arg0 是 PlanetData 还是 PlanetRawData
            System.Type arg0 = original.IsStatic
                ? original.GetParameters()[0].ParameterType
                : original.DeclaringType;

            MethodInfo fix = AccessTools.Method(typeof(PlanetModPlanePatches), nameof(ModPlaneRaw),
                new[] { typeof(int), arg0 });

            if (fix == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"行星放大·地基基准面：{original.DeclaringType?.Name}.{original.Name} 的 arg0 是 "
                    + $"{arg0?.Name}，没有对应的修正重载，未改写");

                return code;
            }

            int hits = 0;

            for (int i = 0; i + 1 < code.Count; i++)
            {
                if (!code[i].Calls(target)) continue;
                if (code[i + 1].opcode != System.Reflection.Emit.OpCodes.Conv_R4) continue;

                // 就地改写这一条（标签跟着它走），再插一条调用
                code[i + 1].opcode = System.Reflection.Emit.OpCodes.Ldarg_0;
                code[i + 1].operand = null;

                code.Insert(i + 2, new CodeInstruction(System.Reflection.Emit.OpCodes.Call, fix));

                hits++;
                i += 2;
            }

            if (hits != 1)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"行星放大·地基基准面：{original.DeclaringType?.Name}.{original.Name} 里"
                    + $"「GetModPlane 紧跟 conv.r4」应为 1 处，实际 {hits} 处。"
                    + "游戏版本可能变了——地基在放大的星球上会把地面拉到 200.2");

                return code;
            }

            System.Threading.Interlocked.Increment(ref _rewritten);

            return code;
        }

        /// <summary>
        /// 修正 <c>GetModPlane</c> 的原值：把写死的 200.2 基准面换成这颗星真正的
        /// <c>realRadius + 0.2</c>。返回的仍是「高度 × 100」的原始单位，
        /// 但类型是 <b>float</b>，所以不受 Int16 上限的约束。
        ///
        /// <para><b>半径 200 时返回值和原版逐位相同</b>（偏移为 0），
        /// 这是这个修正「只扩展、不改写原版行为」的判据。</para>
        /// </summary>
        internal static float ModPlaneRaw(int vanillaPlane, PlanetData planet)
        {
            if (planet == null) return vanillaPlane;

            return vanillaPlane + Offset(planet.radius, planet.scale);
        }

        /// <summary>
        /// <see cref="PlanetRawData"/> 那一处拿不到 <c>PlanetData</c>，
        /// 所以用 <c>precision</c> 认人——被放大的星球精度恒等于
        /// <see cref="PlanetRadiusPatches.Precision"/>，而气态巨星是 64、原版普通行星是 200。
        /// </summary>
        internal static float ModPlaneRaw(int vanillaPlane, PlanetRawData data)
        {
            if (data == null) return vanillaPlane;

            if (!PlanetRadiusPatches.IsResizedPrecision(data.precision)) return vanillaPlane;

            return vanillaPlane + (PlanetRadiusPatches.Radius - 200f) * 100f;
        }

        /// <summary>
        /// 只对<b>确实被我们放大过</b>的行星生效：半径对得上、而且 <c>scale</c> 是 1。
        /// 气态巨星（半径 80 / <c>scale</c> 10）和被别的 mod 动过的都原样放过——
        /// <b>核对末态，而不是核对自己那一份贡献</b>。
        /// </summary>
        private static float Offset(float radius, float scale)
        {
            if (!PlanetRadiusPatches.Active) return 0f;
            if (scale != 1f) return 0f;
            if (radius != PlanetRadiusPatches.Radius) return 0f;

            return (PlanetRadiusPatches.Radius - 200f) * 100f;
        }

        internal static void Report()
        {
            if (!PlanetRadiusPatches.Active) return;

            int n = System.Threading.Interlocked.CompareExchange(ref _rewritten, 0, 0);

            if (n == 3)
                ProjectEdenPlugin.Log.LogWarning(
                    $"行星放大·地基基准面：三处消费方已全部改写（建网格 / 重建网格 / 查改造后高度）。"
                    + $"原版把地基基准面写死成 20020 = (200+0.2)×100，在半径 "
                    + $"{PlanetRadiusPatches.Radius} 的星球上会把有地基的格子拉到 200.2——"
                    + "**那正是「出生点是个洞、走过去掉下去」的根因**。"
                    + $"现已改用 (realRadius + 0.2)×100，偏移 +{(PlanetRadiusPatches.Radius - 200f) * 100f:0}");
            else
                ProjectEdenPlugin.Log.LogError(
                    $"行星放大·地基基准面：只改写了 {n}/3 处消费方，**地基仍然是坏的**。"
                    + "放大后的星球上，任何带地基的格子都会把地面拉到 200.2，"
                    + "出生点和玩家铺的地基都会变成洞");
        }
    }
}
