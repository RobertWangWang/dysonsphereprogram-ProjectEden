using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 在**使用点**保证 <c>ItemProto.fuelNeeds</c> 够长。
    ///
    /// <b>这是一条实测崩溃换来的补丁，不是预防性代码。</b>
    /// <c>ItemProto.InitProductionMask</c> IL 0265–0271 是
    /// <c>ldsfld fuelNeeds ; ldfld PrefabDesc::fuelMask ; ldelem.ref</c>
    /// ——**拿发电机的燃料掩码当数组下标**。本 mod 的两座裂变电厂掩码是 64 / 128，
    /// 而数组默认只有 64 格（<c>ItemProto..cctor</c> 里写死的 <c>new int[64][]</c>），
    /// 于是越界。
    ///
    /// <b>这里是唯一可行的位置，不是「保险的那一道」——这一点试过才知道。</b>
    /// 撑长曾经排在 <c>PostAddDataAction</c> 的**链首**，实测无效：那一刻两座裂变电厂的
    /// <c>prefabDesc</c> 还没连上，扫出来的最大掩码是 32，于是照旧按 64 格放过
    /// （那次还顺带打了一行「64 格，36 组配对可达」——**真事实，假结论**，
    /// 因为它数的是一个还没连完的世界）。
    ///
    /// <c>prefabDesc</c> 是在 <c>MegaBuildingRegistry.ProtoPreload()</c> 里由
    /// <c>ItemProto.Preload(index)</c> 连上的，而 <c>InitProductionMask</c> 就是
    /// **同一个方法紧接着调的**。所以「所有 prefabDesc 都连好了」和「开始查表」之间
    /// 没有任何时刻可以插进去——链上任何位置都要么太早、要么太晚。
    ///
    /// 本仓库为矿种数组写过同一条结论，这里是它的第二个实例，而且更彻底：
    /// <b>排在使用点之前的准备步骤可以被跳过、重排或被别的 mod 抢先；这一次是根本
    /// 不存在那个位置。</b>
    ///
    /// 代价接近零：<c>EnsureFuelNeedsCapacity</c> 在长度已经够时第一时间返回，
    /// 而 <c>InitProductionMask</c> 一局只跑个位数次。
    /// </summary>
    [HarmonyPatch]
    internal static class FuelNeedsCapacityPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemProto), nameof(ItemProto.InitProductionMask))]
        private static void InitProductionMask_Prefix()
        {
            // 返回 true 表示这次真的撑长了。**这是预期路径，不是异常**——
            // 见类注释：prefabDesc 是在调用方同一个方法里刚连上的，
            // 更早的任何位置都扫不到新电厂的掩码。所以这行是 INFO 不是 WARNING，
            // 而且它说明的是「为什么必须在这里做」，不是「顺序有问题」
            if (ProjectEdenPlugin.EnsureFuelNeedsCapacity())
                ProjectEdenPlugin.Log.LogInfo(
                    "燃料白名单在使用点（InitProductionMask）撑长——这是预期路径："
                    + "新电厂的 prefabDesc 是调用方刚刚连上的，链上更早的位置都扫不到它的掩码");
        }
    }
}
