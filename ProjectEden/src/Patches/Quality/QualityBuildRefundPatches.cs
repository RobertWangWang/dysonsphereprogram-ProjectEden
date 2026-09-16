using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// <b>拆掉建筑时，把当初造它用的材料品质还给退回来的那件货。</b>
    ///
    /// 玩家报的是：放下一座带品质的建筑、X 键拆掉、拿回背包——品质变 0。
    ///
    /// <b>那不是丢了，是从来没还过。</b> 建筑的品质存在 <see cref="QualityBuildStore"/> 里，
    /// 是一个<b>耗电折扣</b>，不是货物属性；原版拆除返还的是一件全新的建筑物品，
    /// 它从来没有过品质。所以「放下再捡起来」是个纯粹的品质销毁器——
    /// 而挪建筑是玩家天天干的事。
    ///
    /// <b>这和本仓库已经修过的另外两条拆除路不对称</b>：拆储物箱会把箱里的分还回来
    /// （<c>TakeBackItems_Storage</c>，1c 自动孪生的），拆装配机会把产物缓冲的分还回来
    /// （<see cref="QualityTakeBackAssemblerPatches"/>），唯独拆建筑本身不还。
    /// 补上这一条之后，三条拆除路口径一致。
    ///
    /// <b>挂点是 <c>PlayerAction_Build.DoDismantleObject</c>，而且它是唯一挂点</b>：
    /// 拖拽框选那条路（<c>BuildTool_Dismantle.DismantleAction</c>）的两处
    /// （IL 06ED / 08AE）都是转调它。
    ///
    /// <b>为什么前置写侧信道就够，不用转译器。</b> 那个方法体里退货那一步是
    /// <code>
    /// 0152: … player.TryAddItemToPackage(itemId, count, inc=0, true, objId, false)
    /// 0163: ldc.i4.0 ; stsfld Q0     ← 擦除在调用【之后】
    /// 0182: … DismantleFinally(player, objId, out protoId)
    /// </code>
    /// ——**调用之前没有任何人写 Q0**，所以前置写进去的值会被这次调用直接消费。
    /// 而 <c>DismantleFinally</c>（里面才会 <c>RemoveEntityWithComponents</c> 把
    /// 存档条目删掉）排在退货<b>后面</b>，所以前置去查的时候那条记录还在。
    ///
    /// <b>这条依赖是显式的，而且看得见。</b> 它依赖「1c 没有在那次调用前插入写入」——
    /// 哪天插了，这里写的值会被盖掉。所以一次性日志把「存了多少分」打出来：
    /// 那一行在而背包里的货是 0 分，就是被盖了。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityBuildRefundPatches
    {
        private static int _reported;

        internal static void Report()
        {
            if (QualityAccess.SetChannel0 == null)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质·拆建筑退货：**没接上**——侧信道写入器缺失。"
                    + "这种情况下拆掉带品质的建筑，拿回来的是 0 分的货，而且不报错。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "物品品质·拆建筑退货：已接线。拆掉用好料造的建筑时，那份品质跟着退回来的建筑一起回背包"
                + "（拆储物箱、拆装配机早就会还，唯独拆建筑本身以前不还）。第一次真的退到时会再报一行。");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerAction_Build), nameof(PlayerAction_Build.DoDismantleObject))]
        private static void Pre(PlayerAction_Build __instance, int objId)
        {
            // 蓝图桩（objId < 0）身上没有实体，也就没有品质记录
            if (objId <= 0 || QualityAccess.SetChannel0 == null) return;

            PlanetFactory factory = __instance?.player?.factory;

            if (factory?.planet == null) return;

            if (!QualityBuildStore.TryGet(factory.planet.id, objId, out int perItem)) return;

            if (perItem <= 0) return;

            // 退几件由原版自己算（升级过的建筑退的是升级后那一件），这里照着同一个数走。
            // **侧信道存的是整批总分**，和 GRID.qua 的口径一致，所以要乘件数。
            int count = __instance.ObjectAssetValue(objId);

            if (count <= 0) return;

            QualityAccess.SetChannel0(perItem * count);

            ReportOnce(objId, count, perItem);
        }

        private static void ReportOnce(int objId, int count, int perItem)
        {
            if (System.Threading.Interlocked.Exchange(ref _reported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogWarning(
                $"物品品质·拆建筑退货：第一次把建筑的品质退回背包——实体 {objId}，"
                + $"退 {count} 件、每件 {perItem} 分。"
                + "**这一行在而背包里那件货仍是 0 分**，就说明 1c 后来在那次 "
                + "TryAddItemToPackage 之前插了写入，把这里写的值盖掉了（去看类注释里那段 IL）。"
                + "整局只报一次。");
        }
    }
}
