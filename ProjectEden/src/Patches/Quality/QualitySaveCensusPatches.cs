using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// <b>存盘那一刻和读档那一刻，各把背包里的品质总分量一次。</b>
    ///
    /// 玩家报的是「菜单里覆盖存档，再读回来品质从 50 变成 0」，而且不好复现。
    /// 不好复现的 bug 最怕的就是靠猜——**两个数就能把问题夹住**，而且不需要复现成功：
    ///
    /// <list type="bullet">
    /// <item>存的时候 50、读回来 0 → <b>读的那一侧</b>（<c>Import</c> 的版本分流没走到，
    /// 或者读进来之后被谁清了）；</item>
    /// <item>存的时候就已经是 0 → <b>品质在存盘之前就没了</b>，和存档格式无关，
    /// 该查的是那一局里最后一次动过这格货的是谁。</item>
    /// </list>
    ///
    /// <b>这是本轮刚学到的那条方法的第二次应用</b>：连续两轮只能证伪的诊断，
    /// 换成「让事件自己报到」——不猜品质在哪一步丢的，而是在两个确定的时刻各测一次。
    ///
    /// 常驻、每次存读各一行。它不改任何游戏状态，只读。
    /// 两个挂点分在下面两个类里（<c>SaveCurrentGame</c> 重载，选择器不能和注解共存）。
    /// </summary>
    internal static class QualitySaveCensusPatches
    {
        internal static void Report()
        {
            var save = false;
            var load = false;

            foreach (MethodBase mb in Harmony.GetAllPatchedMethods())
            {
                if (mb?.DeclaringType == typeof(GameSave) && mb.Name == "SaveCurrentGame") save = true;
                if (mb?.DeclaringType == typeof(GameData) && mb.Name == nameof(GameData.Import)) load = true;
            }

            if (save && load)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "物品品质·存读普查：两个挂点都已打上（存盘一行、读档一行）。"
                    + "**存的时候有分、读回来没分 → 读的那一侧；存的时候就是 0 → 存之前就丢了。**");

                return;
            }

            ProjectEdenPlugin.Log.LogWarning(
                $"物品品质·存读普查：挂点没打全——GameSave.SaveCurrentGame={save}、"
                + $"GameData.Import={load}。缺的那一侧不会报数，而「没报数」和「数是 0」分不开。");
        }

        /// <summary>背包里所有带品质的货：共几种、几件、多少分，外加分最高的那一格。</summary>
        private static string Census()
        {
            Player p = GameMain.mainPlayer;
            StorageComponent.GRID[] grids = p?.package?.grids;

            if (grids == null || !QualityAccess.GridReady) return "（读不到背包或品质访问器没就位）";

            var kinds = 0;
            var items = 0;
            var points = 0;
            var bestPer = 0;
            var bestId = 0;

            for (var i = 0; i < grids.Length; i++)
            {
                if (grids[i].count <= 0) continue;

                int qua = QualityAccess.GetGridQua(ref grids[i]);

                if (qua <= 0) continue;

                kinds++;
                items += grids[i].count;
                points += qua;

                int per = qua / grids[i].count;

                if (per <= bestPer) continue;

                bestPer = per;
                bestId = grids[i].itemId;
            }

            if (kinds == 0) return "背包里**一格带品质的货都没有**";

            ItemProto proto = LDB.items.Select(bestId);

            return $"背包里 {kinds} 格带品质，共 {items} 件 / {points} 分，"
                   + $"最高的是「{(proto != null ? proto.name : bestId.ToString())}」每件 {bestPer} 分";
        }

        internal static void LogSave() =>
            ProjectEdenPlugin.Log.LogInfo($"物品品质·存盘前：{Census()}。");

        internal static void LogLoad() =>
            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质·读档后：{Census()}。"
                + "**和上一次「存盘前」那行对比**：那边有分这边没有，就是读的那一侧丢的。");
    }

    /// <summary>
    /// 存盘那一侧。<b>单独成类，因为 <c>SaveCurrentGame</c> 是重载的</b>，
    /// 而按名字选目标的 <c>TargetMethods</c> **不能和单独注解共存**——混在一起
    /// <c>PatchAll</c> 会抛「You cannot combine TargetMethod… with individual annotations」，
    /// 那是**整个 mod 一个补丁都打不上**。
    ///
    /// 第一版就是混着写的，<c>verify_harmony.ps1</c> 在进游戏之前拦下来了。
    /// 本仓库为这条记过两次账，这是第三次——**校验脚本比记性可靠**。
    /// </summary>
    [HarmonyPatch]
    internal static class QualitySaveCensusSavePatches
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(GameSave)))
                if (m.Name == "SaveCurrentGame")
                    yield return m;
        }

        /// <summary><b>前置</b>：要量的是「写进文件的那一份」，所以得在写之前。</summary>
        [HarmonyPrefix]
        private static void Save_Prefix() => QualitySaveCensusPatches.LogSave();
    }

    /// <summary>读档那一侧。<c>GameData.Import</c> 不重载，可以直接写注解。</summary>
    [HarmonyPatch]
    internal static class QualitySaveCensusLoadPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameData), nameof(GameData.Import))]
        private static void Load_Postfix() => QualitySaveCensusPatches.LogLoad();
    }
}
