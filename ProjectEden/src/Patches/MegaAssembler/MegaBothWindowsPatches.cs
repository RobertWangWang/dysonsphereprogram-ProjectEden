using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 巨型建筑<b>同时显示制造台窗口和物流站窗口</b>，并排放。
    ///
    /// 它本来就是「一台机器 + 一座行星内物流站」，可原先只看得见制造台那一半，
    /// 30 个储物格看不到、也就改不了。
    ///
    /// <b>做法：物流站窗口完全由我们自己开关，不进原版的记账。</b>
    /// <c>UIGame.OnPlayerInspecteeChange</c> 里每个组件各有一段
    /// 「上次没有而这次有 → <c>ShutAllFunctionWindow()</c> + 开自己的窗口」，
    /// 而 <see cref="MegaStationWindowPatches"/> 仍然把巨型建筑的 <c>stationId</c> 报成 0，
    /// 于是 <c>inspectStationId</c> 保持为 0，原版对这台建筑的窗口记账一个字都不写。
    ///
    /// <b>这一条是拿一次回归换来的，必须写下来。</b> 先前试过反过来做——不再过滤
    /// <c>stationId</c>，让原版去开物流站窗口，我们只补开制造台。结果是
    /// <c>inspectStationId</c> 被写成了一个非零值，而窗口最终却是关的；此后再点
    /// <b>任何</b>带物流站的建筑（大型采矿机、普通物流站），原版的开窗条件
    /// <c>inspectStationId == 0 &amp;&amp; stationId &gt; 0</c> 永远不成立，
    /// <b>全都开不出窗口了</b>。一个只想加功能的改动，破坏面扩散到了整类建筑。
    ///
    /// 教训：<b>不要和原版抢一个它自己记着状态的开关。</b> 要么完全交给它，
    /// 要么完全自己来；中间地带会把它的状态机留在一个它自己回不去的位置，
    /// 而症状出现在完全无关的建筑上。
    ///
    /// 所以现在是「完全自己来」：每帧看一眼制造台窗口开着没有，开着就把物流站窗口
    /// 也开上并摆到右边，关了就一起关。<b>幂等、不记状态机、不依赖任何一次调用的时序</b>，
    /// 最坏情况只是「物流站窗口没出来」，碰不到别的建筑。
    ///
    /// <b>窗口位置属于「你改了就得负责还回去」。</b> 制造台窗口是所有制造设备共用的，
    /// 挪到旁边之后不还原，下次点开一台普通制造台它还停在那儿。
    /// 这是本仓库 <c>MultiProductUIPatches.RestoreSlot1</c> 的同一条规矩：
    /// <b>原版不会重写的东西，动了就得自己还原</b>。
    /// </summary>
    [HarmonyPatch]
    internal static class MegaBothWindowsPatches
    {
        /// <summary>两个窗口之间留的空隙，像素。</summary>
        private const float Gap = 12f;

        private static bool _measured;
        private static Vector2 _assemblerHome;
        private static bool _weOpened;
        private static int _reported;

        private static readonly Vector3[] CornersA = new Vector3[4];
        private static readonly Vector3[] CornersS = new Vector3[4];

        /// <summary>
        /// 每帧一次。<b>判据是「制造台窗口开着」，不是「选中了哪台建筑」</b>——
        /// 这样按 Esc、点别处、<c>ShutAllFunctionWindow</c> 等所有关窗路径都自动跟上，
        /// 不需要我们去枚举它们，而枚举不全正是上一版翻车的原因。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate_Postfix(UIGame __instance)
        {
            UIAssemblerWindow assembler = __instance?.assemblerWindow;
            UIStationWindow station = __instance?.stationWindow;

            if (assembler?.windowTrans == null || station?.windowTrans == null) return;

            if (!_measured)
            {
                _assemblerHome = assembler.windowTrans.anchoredPosition;
                _measured = true;
            }

            int stationId = assembler.active ? MegaStationId(__instance.inspectAssemblerId) : 0;

            if (stationId <= 0)
            {
                // 制造台窗口关了，或者这台不是巨型建筑：收摊。
                if (_weOpened)
                {
                    station._Close();
                    _weOpened = false;
                }

                assembler.windowTrans.anchoredPosition = _assemblerHome;

                return;
            }

            // stationId 每帧重申：换一台巨型建筑时原版不会帮我们改它——它根本不知道有这回事。
            if (station.stationId != stationId) station.stationId = stationId;

            if (!station.active)
            {
                station._Open();
                _weOpened = true;
            }

            Place(assembler, station);
        }

        /// <summary>
        /// 这个制造台是不是巨型建筑；是的话返回它那座物流站的 id，否则 0。
        ///
        /// 从 <c>inspectAssemblerId</c> 出发而不是从选中实体出发：前者正是原版给
        /// 制造台窗口用的那个 id，它非零就说明制造台窗口讲的是这一台，
        /// 两个窗口因此一定说的是同一台建筑。
        /// </summary>
        private static int MegaStationId(int assemblerId)
        {
            if (assemblerId <= 0) return 0;

            PlanetFactory factory = GameMain.mainPlayer?.factory;
            AssemblerComponent[] pool = factory?.factorySystem?.assemblerPool;

            if (pool == null || assemblerId >= pool.Length) return 0;
            if (pool[assemblerId].id != assemblerId) return 0;
            if (pool[assemblerId].speed < MegaBuildingRegistry.MegaSpeedThreshold) return 0;

            int entityId = pool[assemblerId].entityId;

            if (entityId <= 0 || entityId >= factory.entityPool.Length) return 0;

            int stationId = factory.entityPool[entityId].stationId;
            StationComponent[] stations = factory.transport?.stationPool;

            if (stationId <= 0 || stations == null || stationId >= stations.Length) return 0;

            return stations[stationId] != null && stations[stationId].id == stationId ? stationId : 0;
        }

        /// <summary>
        /// 把制造台窗口挪到物流站窗口<b>左边</b>，用世界坐标算。
        ///
        /// 实测这两个窗口的锚点、父节点、轴心完全一样，所以 <c>anchoredPosition</c>
        /// 的算术本来也对；用 <c>GetWorldCorners</c> 只是不再依赖这个前提——
        /// 锚点一致时两者结果相同，不一致时只有它是对的。
        /// </summary>
        private static void Place(UIAssemblerWindow assembler, UIStationWindow station)
        {
            RectTransform aw = assembler.windowTrans;
            RectTransform sw = station.windowTrans;

            aw.GetWorldCorners(CornersA);
            sw.GetWorldCorners(CornersS);

            // 角点顺序：0 左下、1 左上、2 右上、3 右下
            float awWidth = CornersA[3].x - CornersA[0].x;

            if (awWidth <= 0f || CornersS[3].x - CornersS[0].x <= 0f) return;

            float scale = aw.rect.width > 0f ? awWidth / aw.rect.width : 1f;

            Vector3 pos = aw.position;

            pos.x += CornersS[0].x - Gap * scale - CornersA[3].x;   // 右边缘贴到物流站左边缘外侧
            pos.y += CornersS[1].y - CornersA[1].y;                 // 顶边对齐

            aw.position = pos;

            ReportOnce(assembler, station);
        }

        private static void ReportOnce(UIAssemblerWindow assembler, UIStationWindow station)
        {
            if (_reported != 0) return;

            _reported = 1;

            // **挪完再取一次角点。** 上一版直接用了 Place 里挪动之前的那份，
            // 于是日志报的是旧位置，看上去像是间隙算错了——诊断行报的必须是终态。
            assembler.windowTrans.GetWorldCorners(CornersA);
            station.windowTrans.GetWorldCorners(CornersS);

            ProjectEdenPlugin.Log.LogInfo(
                "巨型建筑：制造台窗口和物流站窗口已并排同时打开。" +
                $"制造台 active={assembler.active} 世界 x {CornersA[0].x:0.00}~{CornersA[2].x:0.00}；" +
                $"物流站 active={station.active} stationId={station.stationId} " +
                $"世界 x {CornersS[0].x:0.00}~{CornersS[2].x:0.00}。位置不对就照这行的数改。");
        }
    }
}
