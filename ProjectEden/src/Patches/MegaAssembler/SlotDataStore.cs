using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 巨型建筑机的槽位配置。原版 AssemblerComponent 没有槽位概念，
    /// 所以按 (planetId, entityId) 存在 mod 自己的字典里，并随存档一起读写。
    /// 结构沿用 ProjectGenesis：每台建筑固定 12 个槽。
    /// </summary>
    internal static class SlotDataStore
    {
        private const int SlotCount = 12;

        private static readonly ConcurrentDictionary<(int, int), SlotData[]> Store =
            new ConcurrentDictionary<(int, int), SlotData[]>();

        internal static SlotData[] GetSlots(int planetId, int entityId)
        {
            (int, int) key = (planetId, entityId);

            if (!Store.TryGetValue(key, out SlotData[] slots) || slots == null)
            {
                slots = new SlotData[SlotCount];
                Store[key] = slots;
            }

            return slots;
        }

        internal static void Remove(int planetId, int entityId) => Store.TryRemove((planetId, entityId), out _);

        internal static void Clear() => Store.Clear();

        /// <summary>
        /// 写入存档。注意：这是位置相关的字节流，字段顺序一旦发布就不能改，
        /// 否则旧存档读出来会错位。
        /// </summary>
        internal static void Export(BinaryWriter w)
        {
            lock (Store)
            {
                w.Write(Store.Count);

                foreach (KeyValuePair<(int, int), SlotData[]> pair in Store)
                {
                    w.Write(pair.Key.Item1);
                    w.Write(pair.Key.Item2);
                    w.Write(pair.Value.Length);

                    for (var i = 0; i < pair.Value.Length; i++)
                    {
                        w.Write((int)pair.Value[i].dir);
                        w.Write(pair.Value[i].beltId);
                        w.Write(pair.Value[i].storageIdx);
                        w.Write(pair.Value[i].counter);
                    }
                }
            }
        }

        internal static void Import(BinaryReader r)
        {
            Clear();

            int count = r.ReadInt32();

            for (var j = 0; j < count; j++)
            {
                int planetId = r.ReadInt32();
                int entityId = r.ReadInt32();
                int length = r.ReadInt32();

                var slots = new SlotData[length];

                for (var i = 0; i < length; i++)
                {
                    slots[i] = new SlotData
                    {
                        dir = (IODir)r.ReadInt32(),
                        beltId = r.ReadInt32(),
                        storageIdx = r.ReadInt32(),
                        counter = r.ReadInt32(),
                    };
                }

                Store[(planetId, entityId)] = slots;
            }
        }
    }
}
