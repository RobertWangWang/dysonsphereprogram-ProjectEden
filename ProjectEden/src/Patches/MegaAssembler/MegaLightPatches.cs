using System.Threading;
using ProjectEden.Utils;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 受光照约束的巨型建筑：整座建筑的产能随日照强度线性变化，
    /// <b>满日照 = 配置的 10000 倍速满产，零日照 = 完全停工</b>。
    ///
    /// <b>算法不是自己发明的，是照抄太阳能板。</b>
    /// <c>PowerGeneratorComponent.EnergyCap_PV(sx, sy, sz, lumino)</c> 的全部内容就是：
    /// <code>
    /// currentStrength = clamp01((sun·pos) * 2.5f + 0.8572445f) * lumino;
    /// </code>
    /// 其中 <c>pos</c> 是发电机位置的单位向量（在球形星球上就是当地的地表法线），
    /// <c>sun</c> 来自 <c>PlanetData.runtimeLocalSunDirection</c>（归一化后），
    /// <c>lumino</c> 来自 <c>PlanetData.luminosity</c>——这两个都从
    /// <c>PowerSystem.GameTick</c> 的调用点读出来核对过（V_14 / V_11）。
    /// 那个 2.5 和 0.8572445 让强度在太阳落到地平线以下一点才归零，
    /// 也就是有「黄昏」而不是一刀切，抄过来正好。
    ///
    /// <b>按建筑判定，不按配方。</b> 早先的版本是按配方判定的（只有光合育林晒太阳，
    /// 罐子里的共培养和萃取照常），理由是化学上更讲得通。<b>这一版按所有者的要求改成整座建筑</b>：
    /// 生物温室在夜里整体停工，三条配方一起停。配置项因此挪到了
    /// <c>megabuildings.json</c> 的 <c>lightDependent</c>，<c>ores.json</c> 里那个同名的配方开关已删除——
    /// 两个开关都叫同一个名字、都只对巨型建筑生效，留着必然有人配错一个还以为生效了。
    ///
    /// <b>为什么缩放的是周期数而不是速度。</b> 巨型建筑的 speed 是 1 亿（10000 倍），
    /// 远超任何配方的 timeSpend，所以降速在跌破 timeSpend 之前毫无效果——
    /// 真正决定吞吐的是每 tick 结算几个配方周期（<c>MegaAssemblerPatches.RunExtraCycles</c>）。
    /// 降速还有个更糟的副作用：巨型建筑是靠 <c>speed &gt;= 阈值</c> 认出来的，
    /// 速度调到阈值以下，这台建筑下一 tick 就再也不会被接管，等于永久报废。
    /// 代价是<b>面板上的「制造速度」始终显示 10000 倍</b>，它读的是 <c>speed</c>；
    /// 真正随光照变化的是产量。
    ///
    /// <b>强度归零时为什么要写 time 而不是别的。</b> 我们的钩子插在原版
    /// <c>InternalUpdate</c> 调用<b>之前</b>，那一次调用拦不掉，它自己就能结算满一个周期
    /// （<c>time += power * speedOverride</c>，而 speedOverride 比 timeSpend 大两个数量级，
    /// 一 tick 必然跨过门槛）。所以要让它这一 tick 结算不了，只能把 time 预先压到
    /// 「加完之后仍然够不着 timeSpend」的位置——增量的上界正是 <c>speedOverride</c>
    /// （power ≤ 1），于是 <c>-speedOverride - 1</c> 是一个精确而不是拍脑袋的值。
    /// </summary>
    internal static class MegaLightPatches
    {
        /// <summary>太阳能板公式里的两个常数，原样照抄 <c>EnergyCap_PV</c> 的 IL。</summary>
        private const float SunSlope = 2.5f;

        private const float SunBias = 0.8572445f;

        /// <summary>
        /// 配了 <c>lightDependent</c> 的巨型建筑物品 ID。
        /// 一座建筑一条 int 比较，<b>不要在 tick 路径上建集合</b>：这个数组在注册期建好，此后只读。
        /// </summary>
        private static int[] _protoIds = new int[0];

        private static int _reported;

        /// <summary>
        /// 从 <c>megabuildings.json</c> 收集受光照约束的建筑。注册期调用一次。
        ///
        /// <b>两种状态都要报一行。</b> 一条都没有时什么都不打，就分不出
        /// 「配置里没开」和「这段代码根本没进 DLL」——这个坑本仓库已经踩过四次。
        /// </summary>
        internal static void Collect()
        {
            MegaBuildingEntry[] buildings = MegaBuildingRegistry.Config?.buildings;

            if (buildings == null)
            {
                _protoIds = new int[0];

                ProjectEdenPlugin.Log.LogWarning("光照约束：读不到 megabuildings.json 的 buildings，没有建筑会受日照影响");

                return;
            }

            var ids = new System.Collections.Generic.List<int>();
            var names = new System.Collections.Generic.List<string>();

            foreach (MegaBuildingEntry entry in buildings)
            {
                if (entry == null || !entry.lightDependent) continue;

                ids.Add(entry.itemId);
                names.Add(entry.displayName);
            }

            _protoIds = ids.ToArray();

            if (_protoIds.Length == 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "光照约束：megabuildings.json 里没有任何建筑配了 lightDependent，本功能不生效");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"光照约束：{_protoIds.Length} 座建筑随日照变化（{string.Join("、", names.ToArray())}），" +
                "满日照满产、背光面停工，曲线与太阳能板一致");
        }

        /// <summary>
        /// 这台建筑要不要看天吃饭。按<b>建筑</b>判定，它跑什么配方都一样。
        ///
        /// <c>protoId</c> 是 Int16，用 <c>EntityData</c> 现成的字段，不另外建映射表。
        /// </summary>
        internal static bool IsLightDependent(PlanetFactory factory, int entityId)
        {
            if (_protoIds.Length == 0) return false;
            if (factory == null) return false;

            EntityData[] pool = factory.entityPool;

            if (pool == null || entityId <= 0 || entityId >= pool.Length) return false;

            int protoId = pool[entityId].protoId;

            for (var i = 0; i < _protoIds.Length; i++)
                if (_protoIds[i] == protoId)
                    return true;

            return false;
        }

        /// <summary>
        /// 日照强度 0~1。读不到星球就返回 1（当作全日照）——
        /// 宁可让它照常生产，也不要因为一个取不到的字段把整条产线静默停掉。
        /// </summary>
        internal static float Strength(PlanetFactory factory, int entityId)
        {
            PlanetData planet = factory?.planet;

            if (planet == null) return 1f;

            EntityData[] pool = factory.entityPool;

            if (pool == null || entityId <= 0 || entityId >= pool.Length) return 1f;

            Vector3 sun = planet.runtimeLocalSunDirection.normalized;
            Vector3 up = pool[entityId].pos.normalized;

            float raw = Vector3.Dot(sun, up) * SunSlope + SunBias;

            return Mathf.Clamp01(raw) * planet.luminosity;
        }

        /// <summary>
        /// 把配置的每 tick 周期数按日照缩放。返回 0 表示这一 tick 一个周期都不该结算，
        /// 调用方要负责把原版那一次调用也压住（见 <see cref="Suppress"/>）。
        /// </summary>
        internal static int ScaleCycles(PlanetFactory factory, ref AssemblerComponent component, int cycles)
        {
            float strength = Strength(factory, component.entityId);

            // 四舍五入而不是截断：强度 0.9 时截断到 53 和 54 差别不大，
            // 但强度低于 1/cycles 时截断会一路归零，黄昏就成了断崖
            int scaled = Mathf.RoundToInt(cycles * strength);

            if (scaled < 0) scaled = 0;
            if (scaled > cycles) scaled = cycles;

            Report(factory, ref component, strength, cycles, scaled);

            return scaled;
        }

        /// <summary>
        /// 让原版紧随其后的那次 <c>InternalUpdate</c> 结算不了任何周期。
        ///
        /// 两条计时线都要压：<c>time</c> 是主产物，<c>extraTime</c> 是增产剂的额外产出，
        /// 它们在 <c>InternalUpdate</c> 里是两段独立的 <c>if</c>，只压一条会漏产。
        /// 压到负数而不是 0，是因为 0 加上一 tick 的增量就够门槛了。
        /// </summary>
        internal static void Suppress(ref AssemblerComponent component)
        {
            component.time = -component.speedOverride - 1;

            // **只在增产计时器真的会走的时候才压它。** extraSpeed == 0 时原版推进它的唯一
            // 一处（IL 0586 `extraTime += power * extraSpeed`）加的是 0，本来就跨不过门槛，
            // 哨兵毫无作用——而 extraTime 是**存档字段**、原版没有任何一处清零，于是那个
            // 负数会永久留在存档里。它自己不引发任何症状，却会让 MegaBatchSettle.CanBatch
            // 把这台建筑踢出批量结算（同一个坑在 MegaThrottle.RewindExtra 上实际发作过：
            // 全存档 36 万台次全中，生产设施 14 ms → 210 ms）。
            if (component.extraSpeed > 0) component.extraTime = -component.extraSpeed - 1;
        }

        /// <summary>
        /// 首次算出光照时报一行。<b>无论强度多少都报</b>——只在「晒得到」时才打印的话，
        /// 「现在是夜里」和「这段代码根本没进 DLL」在日志里长得一模一样。
        ///
        /// 用 Interlocked 领号：组装机 tick 跑在 <c>_assembler_parallel</c> 上，
        /// 普通的 <c>if (_done) return;</c> 挡不住并发，每个线程都会打一遍。
        /// </summary>
        private static void Report(PlanetFactory factory, ref AssemblerComponent component,
            float strength, int cycles, int scaled)
        {
            if (Interlocked.Exchange(ref _reported, 1) != 0) return;

            string planet = factory?.planet?.displayName ?? "未知星球";

            ProjectEdenPlugin.Log.LogInfo(
                $"光照建筑首次结算：{planet} 上的 1 台巨型建筑跑配方 {component.recipeId}，" +
                $"日照强度 {strength:0.###}（光照度 {factory?.planet?.luminosity ?? 0f:0.##}），" +
                $"每 tick 周期数 {cycles} → {scaled}" +
                (scaled == 0 ? "（背光面，本 tick 停工）" : $"（{scaled * 60} 周期/秒）"));
        }
    }
}
