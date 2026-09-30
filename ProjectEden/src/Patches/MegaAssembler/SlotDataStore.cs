using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

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

        // 字典保持存档格式；热路径按星球/实体直接取同一数组，不缓存连接内容。
        // 仅在创建、拆除、导入和扩容时持有 Store 锁；数组引用用 Volatile 发布。
        private sealed class PlanetSlots { internal SlotData[][] Entities = new SlotData[0][]; }
        private static PlanetSlots[] _planets = new PlanetSlots[0];
        private const int MaxIndexedPlanet = 65535, MaxIndexedEntity = 1048575;
        private static bool CanIndex(int planet, int entity) => planet >= 0 && planet <= MaxIndexedPlanet
            && entity >= 0 && entity <= MaxIndexedEntity;

        internal static SlotData[] GetSlots(int planetId, int entityId)
        {
            if (CanIndex(planetId, entityId))
            {
                var planets = Volatile.Read(ref _planets);
                if (planetId < planets.Length)
                {
                    var planet = Volatile.Read(ref planets[planetId]);
                    if (planet != null)
                    {
                        var entries = Volatile.Read(ref planet.Entities);
                        if (entityId < entries.Length)
                        {
                            var cached = Volatile.Read(ref entries[entityId]);
                            if (cached != null) return cached;
                        }
                    }
                }
            }
            lock (Store)
            {
                var key = (planetId, entityId);
                if (!Store.TryGetValue(key, out SlotData[] slots) || slots == null)
                    Store[key] = slots = new SlotData[SlotCount];
                Index(planetId, entityId, slots);
                return slots;
            }
        }

        // 调用时持有 Store 锁；异常/第三方超大编号仅使用字典，避免巨量数组分配。
        private static void Index(int planetId, int entityId, SlotData[] slots)
        {
            if (!CanIndex(planetId, entityId)) return;
            var planets = _planets;
            if (planetId >= planets.Length)
            {
                int size = System.Math.Max(16, planets.Length);
                while (size <= planetId) size *= 2;
                var expanded = new PlanetSlots[size];
                System.Array.Copy(planets, expanded, planets.Length);
                Volatile.Write(ref _planets, planets = expanded);
            }
            var planet = planets[planetId];
            if (planet == null) Volatile.Write(ref planets[planetId], planet = new PlanetSlots());
            var entries = planet.Entities;
            if (entityId >= entries.Length)
            {
                int size = System.Math.Max(16, entries.Length);
                while (size <= entityId) size *= 2;
                var expanded = new SlotData[size][];
                System.Array.Copy(entries, expanded, entries.Length);
                Volatile.Write(ref planet.Entities, entries = expanded);
            }
            Volatile.Write(ref entries[entityId], slots);
        }

        internal static void Remove(int planetId, int entityId)
        {
            lock (Store)
            {
                Store.TryRemove((planetId, entityId), out _);
                // 删除不为不存在的站点创建索引。
                var planets = _planets;
                if (planetId < 0 || planetId >= planets.Length) return;
                var planet = planets[planetId];
                if (planet == null || entityId < 0 || entityId >= planet.Entities.Length) return;
                Volatile.Write(ref planet.Entities[entityId], null);
            }
        }

        internal static void Clear()
        {
            lock (Store) { Store.Clear(); Volatile.Write(ref _planets, new PlanetSlots[0]); }
        }

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
            lock (Store)
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
                    Index(planetId, entityId, slots);
                }
            }
        }
    }
}
