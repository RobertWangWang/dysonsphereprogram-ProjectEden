using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 每台建筑的合金配比：<b>各可调槽的份数</b>（余量槽不存，它等于总份数减去其余）。
    ///
    /// <b>为什么必须自己存。</b> <c>AssemblerComponent.Import</c> 在读回 <c>recipeId</c> 之后，
    /// 会用 <c>LDB.recipes.Select(recipeId)</c> <b>重新推导</b>配方数据（IL 0181~0199，
    /// 随后在 0405 / 06BB 两处把 <c>recipeExecuteData</c> 写回成全局那一份）。
    /// 也就是说读档后每台建筑都会退回默认配比，我们贴上去的克隆一个都不剩。
    /// 这就是本仓库第 1 号坑的标准形状：存档里烘死的值，都要有第二条运行时补正路径。
    ///
    /// 键是 <c>(planetId, entityId)</c>，和 <c>SlotDataStore</c> 一致——
    /// entityId 在不同星球上会重号，只用它做键会串。
    ///
    /// <b>并发。</b> 装配器的 tick 是跨星球并行的，读取可能来自任意工作线程，
    /// 所以用 <c>ConcurrentDictionary</c>，不用普通 Dictionary（见 CLAUDE.md 第 4 号坑）。
    /// </summary>
    internal static class AlloyRatioStore
    {
        private static readonly ConcurrentDictionary<(int PlanetId, int EntityId), int[]> Ratios =
            new ConcurrentDictionary<(int, int), int[]>();

        /// <summary>
        /// 玩家当前设定的配比，<b>按配方分开记</b>。新建的建筑和蓝图粘贴出来的建筑继承它。
        ///
        /// <b>必须按配方分开。</b> 早先这里是一个全局 int，七种合金共用——
        /// 在硬质合金上把钴调到 45，再去建一台钴铬合金，那个 45 会被当成铬的份数硬套过去。
        /// 每种合金的可调槽个数和量纲都不一样，一个数根本表达不了。
        ///
        /// 蓝图本身只带 <c>recipeId</c>，塞不进配比（那要动 BuildingParameters）。
        /// 所以约定是：蓝图给默认，而<b>玩家一旦设过自定义配比，同配方的新建筑就跟着走</b>。
        /// </summary>
        private static readonly ConcurrentDictionary<int, int[]> PlayerDefaults =
            new ConcurrentDictionary<int, int[]>();

        internal static int Count => Ratios.Count;

        internal static bool TryGet(int planetId, int entityId, out int[] parts) =>
            Ratios.TryGetValue((planetId, entityId), out parts);

        internal static void Set(int planetId, int entityId, int[] parts) =>
            Ratios[(planetId, entityId)] = (int[])parts.Clone();

        internal static void Remove(int planetId, int entityId) =>
            Ratios.TryRemove((planetId, entityId), out _);

        internal static int[] GetPlayerDefault(int recipeId) =>
            PlayerDefaults.TryGetValue(recipeId, out int[] parts) ? parts : null;

        internal static void SetPlayerDefault(int recipeId, int[] parts) =>
            PlayerDefaults[recipeId] = (int[])parts.Clone();

        internal static IEnumerable<KeyValuePair<(int PlanetId, int EntityId), int[]>> All => Ratios;

        internal static void Clear()
        {
            Ratios.Clear();
            PlayerDefaults.Clear();
        }

        // ── 存档 ──────────────────────────────────────────────
        // 字节流是位置相关的：Export / Import / IntoOtherSave 必须一起改。
        // 这一块追加在 SlotDataStore 之后，靠 Plugin 里的 SaveVersion 控制旧档兼容。

        internal static void Export(BinaryWriter w)
        {
            w.Write(PlayerDefaults.Count);

            foreach (KeyValuePair<int, int[]> pair in PlayerDefaults)
            {
                w.Write(pair.Key);
                WriteParts(w, pair.Value);
            }

            w.Write(Ratios.Count);

            foreach (KeyValuePair<(int PlanetId, int EntityId), int[]> pair in Ratios)
            {
                w.Write(pair.Key.PlanetId);
                w.Write(pair.Key.EntityId);
                WriteParts(w, pair.Value);
            }
        }

        /// <summary>
        /// <paramref name="version"/> 是 Plugin 存档块的版本号。
        ///
        /// <b>版本 2 的布局不一样</b>：那时每台建筑只有一个可调槽，存的是一个 int，
        /// 而且玩家默认值是<b>一个全局 int</b>。这里把每台建筑那个 int 读成长度 1 的数组，
        /// 全局默认值直接丢掉——它跨配方本来就没有意义，重新设一次即可。
        /// </summary>
        internal static void Import(BinaryReader r, int version)
        {
            Clear();

            if (version < 3)
            {
                r.ReadInt32();   // 版本 2 的全局默认配比，跨配方无意义，丢弃

                int old = r.ReadInt32();

                for (var i = 0; i < old; i++)
                {
                    int planetId = r.ReadInt32();
                    int entityId = r.ReadInt32();

                    Ratios[(planetId, entityId)] = new[] { r.ReadInt32() };
                }

                if (old > 0)
                    ProjectEdenPlugin.Log.LogInfo($"合金配比：从版本 {version} 的存档迁移了 {old} 台的配比");

                return;
            }

            int defaults = r.ReadInt32();

            for (var i = 0; i < defaults; i++)
            {
                int recipeId = r.ReadInt32();

                PlayerDefaults[recipeId] = ReadParts(r);
            }

            int count = r.ReadInt32();

            for (var i = 0; i < count; i++)
            {
                int planetId = r.ReadInt32();
                int entityId = r.ReadInt32();

                Ratios[(planetId, entityId)] = ReadParts(r);
            }
        }

        private static void WriteParts(BinaryWriter w, int[] parts)
        {
            w.Write(parts.Length);

            for (var i = 0; i < parts.Length; i++) w.Write(parts[i]);
        }

        private static int[] ReadParts(BinaryReader r)
        {
            var n = r.ReadInt32();
            var parts = new int[n];

            for (var i = 0; i < n; i++) parts[i] = r.ReadInt32();

            return parts;
        }
    }
}
