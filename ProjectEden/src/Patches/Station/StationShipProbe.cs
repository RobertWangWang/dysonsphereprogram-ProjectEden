using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace ProjectEden.Patches.Station
{
    /// <summary>
    /// 星际物流站（含综合物流枢纽）的运输船诊断。
    ///
    /// <b>它回答的是一个具体问题：船位到底是不是瓶颈。</b> 问「能不能把运输船上限调高」
    /// 之前得先知道现在有没有用满，而这件事在游戏里看不出来——站面板只显示当前数量，
    /// 不告诉你这一局的峰值。
    ///
    /// <b>原本 64 是类型级硬上限——1.12.14 起不是了。</b>
    /// <c>StationComponent.idleShipIndices</c> 和 <c>workShipIndices</c> 是 <c>UInt64</c>，
    /// 三处取位一律 <c>1L &lt;&lt; (i &amp; 63)</c>，所以第 65 艘的位会折回第 0 位。
    /// <see cref="StationShipBank"/> 把那八个翻位方法整体换成了旁挂位图，墙就没了。
    /// <b>这一行因此报的是「这座站实际有几个泊位」，不再是一个固定的 64。</b>
    ///
    /// <b>额外报一件事：已建好的站可能比 prefab 矮一截。</b> <c>StationComponent.Init</c> 从
    /// <c>PrefabDesc.stationMaxShipCount</c> 开六个数组（@016D / @017F / @0191 / @01A3 /
    /// @022F / @0241），而读档那一路<b>不重算</b>——<c>StationComponent.Import</c> @021B 先
    /// <c>ReadInt32()</c> 再按那个数 <c>newarr</c>，长度来自存档。对照无人机那一侧，原版自己
    /// 是有补正的（<c>PlanetTransport.Import</c> @00C3 现读 prefab 的
    /// <c>stationMaxDroneCount</c>，@00D4 调 <c>PatchDroneArray</c>），<b>但全程序集只有这一个
    /// <c>Patch*Array</c>，运输船没有对应的</b>。所以 1.12.10 把枢纽从 50 抬到 64 之后，
    /// 老枢纽仍然是 50，重建才会变。这一栏就是让这个差值看得见。
    ///
    /// <b>不设开关、只在变化时打印。</b> 这是 <c>PlanetCensus</c> 的形状：常驻但不刷屏，
    /// 数字不动就不出声，动了才报一行。峰值是会话级的，所以「这一局有没有顶到上限」
    /// 抓一次日志就答得上。
    /// </summary>
    [HarmonyPatch]
    internal static class StationShipProbe
    {
        /// <summary>原版 <c>UInt64</c> 位图能表示的泊位数。<b>它不再是上限</b>，
        /// 只是「超过这个数就说明旁挂位图确实在起作用」的分界线。</summary>
        private const int VanillaBitmaskWidth = 64;

        /// <summary>每 10 秒看一眼。用取模而不是「下次在 tick + 600」——后者在读入另一个存档、
        /// <c>gameTick</c> 往回跳之后永远不会再到期，而且是静默的（本仓库记过）。</summary>
        private const int ReportInterval = 600;

        /// <summary>按**物品原型**分桶。「打印一次」指的是每一种东西一次，不是每局一次——
        /// 本文件记过四次的那条。</summary>
        private static readonly ConcurrentDictionary<int, Kind> Kinds = new ConcurrentDictionary<int, Kind>();

        private sealed class Kind
        {
            /// <summary>这一局里单站同时在飞的最大值。**这一栏才是「船位够不够」的答案。**</summary>
            internal int PeakWork;

            /// <summary>这一局里单站的最大机队规模（在飞 + 待命）。</summary>
            internal int PeakFleet;

            /// <summary>上一次打印的内容。一样就不再打。</summary>
            internal string LastLine;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetTransport), nameof(PlanetTransport.GameTick))]
        private static void PlanetTransport_GameTick(PlanetTransport __instance)
        {
            // 诊断打点：放在一切 return 之前（理由见 LabLogisticSupplyPatches 同一行）
            Diagnostics.TransportSplitProbe.Phase("运输船诊断");

            if (GameMain.gameTick % ReportInterval != 0) return;

            // 只报脚下这颗星球。PlanetTransport.GameTick 跑在
            // FactoryTransportGameTick_Parallel 上、每颗星球一个工作线程，
            // 这一句同时也把并发收敛成了一条线；静态表仍然用 Concurrent 的，
            // 因为「只有一个线程会进来」是条件推出来的，不是类型保证的。
            PlanetData local = GameMain.localPlanet;

            if (local == null || __instance.planet == null || __instance.planet.id != local.id) return;

            Scan(__instance);
        }

        private static void Scan(PlanetTransport transport)
        {
            StationComponent[] pool = transport.stationPool;

            if (pool == null) return;

            // 每种原型一行。桶里存的是本次扫描的汇总，峰值单独记在 Kinds 里。
            var tally = new ConcurrentDictionary<int, int[]>();

            int cursor = transport.stationCursor < pool.Length ? transport.stationCursor : pool.Length;

            for (var i = 1; i < cursor; i++)
            {
                StationComponent station = pool[i];

                if (station == null || station.id != i) continue;

                // **判据是「有没有船位数组」，不是物品号。** 行星内物流站的
                // workShipDatas 长度就是 0（Init 按 prefab 的 stationMaxShipCount 开），
                // 所以这一句同时也是「它是不是星际站」的答案——和本仓库
                // 「用 isVeinCollector 而不是物品号」是同一条判据。
                if (station.workShipDatas == null || station.workShipDatas.Length == 0) continue;

                int protoId = transport.factory.entityPool[station.entityId].protoId;

                int[] row = tally.GetOrAdd(protoId, _ => new int[5]);

                row[0]++;                                  // 台数
                row[1] += station.workShipCount;            // 在飞
                row[2] += station.idleShipCount;            // 待命
                if (station.workShipDatas.Length > row[3])
                    row[3] = station.workShipDatas.Length;  // 实际船位（存档里烘进去的那个）
                if (station.workShipCount > row[4])
                    row[4] = station.workShipCount;         // 本次单站最大在飞

                Kind kind = Kinds.GetOrAdd(protoId, _ => new Kind());

                if (station.workShipCount > kind.PeakWork) kind.PeakWork = station.workShipCount;

                int fleet = station.workShipCount + station.idleShipCount;

                if (fleet > kind.PeakFleet) kind.PeakFleet = fleet;
            }

            foreach (var pair in tally) Emit(pair.Key, pair.Value);
        }

        private static void Emit(int protoId, int[] row)
        {
            Kind kind = Kinds.GetOrAdd(protoId, _ => new Kind());

            ItemProto proto = LDB.items.Select(protoId);
            string name = proto?.name ?? protoId.ToString();

            // prefab 现在写的是多少。和上面那个「实际船位」对不上，就说明这些站是
            // 抬高之前建的——它们不会自己长，Import 不重算（见类注释）。
            int prefabCap = proto?.prefabDesc?.stationMaxShipCount ?? 0;

            var sb = new StringBuilder();

            sb.Append("运输船：").Append(name)
              .Append(" ").Append(row[0]).Append(" 台｜在飞 ").Append(row[1])
              .Append("／待命 ").Append(row[2])
              .Append("｜单站在飞峰值 ").Append(kind.PeakWork)
              .Append("（本局机队峰值 ").Append(kind.PeakFleet).Append("）")
              .Append("｜实际船位 ").Append(row[3]);

            if (prefabCap > 0 && prefabCap != row[3])
                sb.Append("，但 prefab 现在写的是 ").Append(prefabCap)
                  .Append("（**这些站是抬高之前建的**：读档不重算船位数组，"
                          + "原版只给无人机补了 PatchDroneArray，运输船没有对应的，所以要么拆了重建、要么忍着）");

            if (row[3] > VanillaBitmaskWidth)
                sb.Append("（已超过原版 ").Append(VanillaBitmaskWidth)
                  .Append(" 位位图，旁挂位图在起作用）");

            // **这一句才是玩家真正要的结论。** 峰值贴着船位跑才叫船位不够；
            // 长期离得远，说明卡在派船节奏或者能量上（每次跃迁 100 MJ），
            // 加船位一点用都没有——1.10.7 那轮量到的就是这个。
            if (row[3] > 0)
            {
                if (kind.PeakWork >= row[3])
                    sb.Append(" → **顶满过**，船位是瓶颈");
                else if (kind.PeakWork * 4 >= row[3] * 3)
                    sb.Append(" → 峰值已过 3/4，接近瓶颈");
                else
                    sb.Append(" → 远没用满，瓶颈不在船位（先看派船节奏和充能）");
            }

            string line = sb.ToString();

            // 数字不动就不出声。第一次一定会打，所以「一行都没有」只意味着
            // 这颗星球上没有星际物流站，不会和「补丁没挂上」混淆——那一半由 Report() 回答。
            if (line == kind.LastLine) return;

            kind.LastLine = line;

            ProjectEdenPlugin.Log.LogInfo(line);
        }

        /// <summary>
        /// 开机状态行。<b>读的是 Harmony 自己的补丁表，不是「我调过 PatchAll 没抛异常」</b>——
        /// 状态行回答「接上了没有」，事件行回答「它决定了什么」，一个替不了另一个。
        /// 本仓库为这条付过七次账，这里不再付第八次。
        /// </summary>
        internal static void Report()
        {
            var attached = false;

            foreach (MethodBase patched in Harmony.GetAllPatchedMethods())
            {
                if (patched.DeclaringType != typeof(PlanetTransport)
                    || patched.Name != nameof(PlanetTransport.GameTick)) continue;

                HarmonyLib.Patches info = Harmony.GetPatchInfo(patched);

                if (info?.Postfixes == null) continue;

                foreach (Patch p in info.Postfixes)
                    if (p.PatchMethod?.DeclaringType == typeof(StationShipProbe))
                        attached = true;
            }

            if (!attached)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "运输船诊断：**补丁没挂上**，这一局不会有任何「运输船：…」的行。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "运输船诊断已接上：每 10 秒看一眼脚下这颗星球的星际物流站，"
                + "数字有变化才报一行（在飞／待命／单站峰值／实际船位）。"
                + "**峰值长期离船位很远就说明瓶颈不在船位**，而在派船节奏或充能。");
        }
    }
}
