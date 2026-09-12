using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 每台催化反应器自己的催化剂床：装了多少份、还能跑多少个产出 tick。
    ///
    /// <b>为什么不挤进 <see cref="AlloyRatioStore"/>。</b> 那个类是
    /// <c>ConcurrentDictionary&lt;(PlanetId, EntityId), int[]&gt;</c>，形状一模一样，
    /// 而且一台机器不可能既是合金机又是催化反应器——看起来完全可以共用一张表。
    /// 但 <c>entityId</c> 是<b>会回收的</b>：拆掉一台合金机、在同一个 id 上盖起反应器，
    /// 而 <c>Remove</c> 没被调到的话，那批残留的配比数就会被当成催化剂床读出来。
    /// 概率低、症状难查（床里凭空有了半床催化剂），不值得省这一个类。
    ///
    /// <b>并发。</b> 组装机 tick 跑在 <c>_assembler_parallel</c> 上，
    /// 而巨型建筑遍布多颗行星、由多个线程同时推进，所以必须是
    /// <c>ConcurrentDictionary</c>，不能是普通 <c>Dictionary</c>（CLAUDE.md 第 4 号坑）。
    /// </summary>
    internal static class CatalystBedStore
    {
        /// <summary>值是 { 装填份数, 剩余产出 tick 数 }。</summary>
        private static readonly ConcurrentDictionary<(int PlanetId, int EntityId), int[]> Beds =
            new ConcurrentDictionary<(int, int), int[]>();

        internal static int Count => Beds.Count;

        internal static IEnumerable<KeyValuePair<(int PlanetId, int EntityId), int[]>> All => Beds;

        /// <summary>
        /// 取出<b>数组本身</b>，让调用方原地改 <c>bed[1]</c>。
        ///
        /// 这是 tick 路径，<b>不许分配</b>——每 tick 为每台反应器重建一个 int[]
        /// 再塞回字典，等于把 GC 压力接到帧上。字典握的是同一个引用，
        /// 原地减对别的读者立刻可见；而一台建筑只会被它所在行星那一个线程推进，
        /// 不存在两个线程同时改同一床的情况。
        /// </summary>
        internal static bool TryGetBed(int planetId, int entityId, out int[] bed)
        {
            if (Beds.TryGetValue((planetId, entityId), out bed) && bed != null && bed.Length >= 2) return true;

            bed = null;

            return false;
        }

        internal static void Set(int planetId, int entityId, int charge, int life)
        {
            if (charge <= 0)
            {
                // 空床不占表：拆建筑没走到 Remove 的话，留着的就是垃圾
                Beds.TryRemove((planetId, entityId), out _);

                return;
            }

            Beds[(planetId, entityId)] = new[] { charge, life };
        }

        internal static void Remove(int planetId, int entityId) => Beds.TryRemove((planetId, entityId), out _);

        internal static void Clear() => Beds.Clear();

        // ── 存档 ──────────────────────────────────────────────
        // 字节流是位置相关的：Export / Import / IntoOtherSave 三处必须一起改，
        // 而且这一块是版本 4 才有的，读老档时不能去读它。

        internal static void Export(BinaryWriter w)
        {
            w.Write(Beds.Count);

            foreach (KeyValuePair<(int PlanetId, int EntityId), int[]> pair in Beds)
            {
                w.Write(pair.Key.PlanetId);
                w.Write(pair.Key.EntityId);
                w.Write(pair.Value[0]);
                w.Write(pair.Value[1]);
            }
        }

        internal static void Import(BinaryReader r)
        {
            Clear();

            int count = r.ReadInt32();

            for (var i = 0; i < count; i++)
            {
                int planetId = r.ReadInt32();
                int entityId = r.ReadInt32();
                int charge = r.ReadInt32();
                int life = r.ReadInt32();

                if (charge > 0) Beds[(planetId, entityId)] = new[] { charge, life };
            }

            if (count > 0) ProjectEdenPlugin.Log.LogInfo($"催化剂床：从存档读回 {count} 台反应器的装填状态");
        }
    }
}
