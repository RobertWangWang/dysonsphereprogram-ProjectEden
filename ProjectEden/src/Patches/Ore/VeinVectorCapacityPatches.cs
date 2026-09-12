using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把 <c>PlanetAlgorithm</c> 的矿脉点位缓冲区按矿脉类型数等比放大。
    ///
    /// <b>这是「抬高硬编码上限会激活原版的潜伏 bug」的又一例，而且这次直接崩了扫描线程。</b>
    ///
    /// <c>PlanetAlgorithm..ctor</c> 里写死 <c>veinVectors = new Vector3[512]</c>
    /// （以及并行的 <c>veinVectorTypes</c>）。<c>GenerateVeins</c> 的结构是两层循环：
    ///
    /// <code>
    /// for (type = 1; type &lt; 15; type++)          // 外层：矿脉类型（IL 086C 那个 15，被本 mod 抬到了 24）
    ///     for (spot = 0; spot &lt; spots; spot++)    // 内层：该类型的点位
    ///         veinVectors[veinVectorCount] = ...   // IL 0815
    ///         veinVectorTypes[veinVectorCount] = ...
    ///         if (++veinVectorCount == veinVectors.Length) goto 0864;   // IL 0853
    /// // 0864 是**外层的自增**——所以满了只跳出内层，外层换下一种类型接着写
    /// </code>
    ///
    /// <b>满了之后它只跳出内层循环。</b> 外层继续，下一种矿脉类型的第一次写入就是
    /// <c>veinVectors[512]</c>，<c>IndexOutOfRangeException</c>。
    /// 原版 14 种类型凑不满 512，所以这个洞一直是死的；
    /// 本 mod 把类型抬到 23 种之后它活了，表现为
    /// <c>Scanning Thread Error: IndexOutOfRangeException at PlanetAlgorithm.GenerateVeins</c>，
    /// 而报错里点名的是我们的 <c>OreVeinRangePatches</c> —— 它确实是导火索，但洞是原版的。
    ///
    /// <b>为什么是放大数组，而不是去修那个 goto。</b> 那个 <c>beq</c> 比的是
    /// <c>veinVectors.Length</c>（动态读，不是常量 512），所以把数组换大之后
    /// <b>原版自己的停止逻辑原样还在</b>，只是门槛抬高了——不用碰任何分支，
    /// 也就没有「改跳转把标签弄丢」那类风险。反过来去转译那个 goto，
    /// 等于重写一段带两层循环的控制流，代价和风险都大得多。
    ///
    /// <b>容量是推出来的，不是拍的。</b> 原版 14 种类型配 512 个点位，
    /// 即每种类型约 36.6 个；按同样的人均预算乘上现在的类型数，
    /// 再向上取整到 64 的倍数。23 种 → 896。
    /// 一个 <c>PlanetAlgorithm</c> 实例因此多占约 12 KB，而同时存在的实例只有个位数
    /// （扫描线程一个、加载中的星球一个），可以忽略。
    /// </summary>
    [HarmonyPatch]
    internal static class VeinVectorCapacityPatches
    {
        /// <summary>原版的点位容量。</summary>
        private const int VanillaCapacity = 512;

        /// <summary>原版的矿脉类型数（1~14）。</summary>
        private const int VanillaTypes = 14;

        private static int _capacity;
        private static int _logged;

        /// <summary>
        /// 派生类（<c>PlanetAlgorithm7/11/12/13</c> 等）的构造函数都会调基类的，
        /// 所以只补这一处就覆盖全部。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetAlgorithm), MethodType.Constructor)]
        private static void Ctor(PlanetAlgorithm __instance)
        {
            if (_capacity == 0) _capacity = Derive();

            if (_capacity <= VanillaCapacity) return;

            __instance.veinVectors = new Vector3[_capacity];
            __instance.veinVectorTypes = new EVeinType[_capacity];
        }

        private static int Derive()
        {
            int types = OreRegistry.MaxVeinId;

            if (types <= VanillaTypes)
            {
                Report(VanillaCapacity, types, false);

                return VanillaCapacity;
            }

            // 人均预算照抄原版，再向上取整到 64 的倍数
            int want = VanillaCapacity * types / VanillaTypes;
            want = (want + 63) / 64 * 64;

            Report(want, types, true);

            return want;
        }

        private static void Report(int capacity, int types, bool raised)
        {
            if (Interlocked.Exchange(ref _logged, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"矿脉点位缓冲：{VanillaCapacity} → {capacity}（矿脉类型 {VanillaTypes} → {types}，人均预算照抄原版）"
                + (raised
                    ? "。原版满了只跳出内层循环、外层换下一种类型继续写，"
                      + "14 种类型凑不满 512 所以那个洞一直是死的；类型变多之后它会越界崩掉扫描线程"
                    : "：类型数没超过原版，保持原样"));
        }
    }
}
