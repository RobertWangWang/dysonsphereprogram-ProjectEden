using System.Collections.Concurrent;
using System.IO;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 每座建筑是用什么品质的材料造出来的——<b>效果层唯一的输入</b>。
    ///
    /// 存的是<b>每件分数</b>（0~100），不是整批总分：建造消耗的件数各不相同，
    /// 而效果只关心「料有多好」，和用了几件无关。
    ///
    /// <b>为什么要自己存，而不是从建筑上现算。</b> 材料在建造那一刻就被消耗掉了，
    /// 之后这台建筑身上没有任何地方还记着它是用什么料造的——
    /// 品质是<b>一格货的属性</b>，货没了，属性就没了。所以必须在扣料那一刻截下来。
    ///
    /// 两段接力：扣料时按 <c>prebuildId</c> 记（那时实体还不存在，只有蓝图桩），
    /// <c>AddEntityDataWithComponents(entity, prebuildId)</c> 那一刻搬到 <c>entityId</c>。
    /// 这条路径是照 <see cref="AlloyRatioStore"/> 那套来的，同样进 <c>IModCanSave</c>。
    ///
    /// <b>不要去征用 <c>PrebuildData.grade</c>。</b> 它确实是个闲着的 Int32，
    /// 但那是升级层级，而且它进存档——同一个字段两种含义正是本仓库的第 2 号坑。
    /// </summary>
    internal static class QualityBuildStore
    {
        /// <summary>已经建好的实体：(星球, 实体) → 每件品质分。</summary>
        private static readonly ConcurrentDictionary<(int PlanetId, int EntityId), int> Built =
            new ConcurrentDictionary<(int, int), int>();

        /// <summary>
        /// 还没被造出来的蓝图桩：(星球, prebuildId) → 每件品质分。
        ///
        /// <b>不进存档。</b> 存档里本来就有蓝图桩，但「桩上那份品质」丢了最多是
        /// 一座建筑退回 0 分，而多存一张表就要多一个版本分支——
        /// 代价和收益不成比例。这一条是有意的取舍，不是遗漏。
        /// </summary>
        private static readonly ConcurrentDictionary<(int PlanetId, int PrebuildId), int> Pending =
            new ConcurrentDictionary<(int, int), int>();

        internal static int Count => Built.Count;

        internal static void SetPending(int planetId, int prebuildId, int perItem)
        {
            if (planetId <= 0 || prebuildId <= 0) return;

            Pending[(planetId, prebuildId)] = perItem;
        }

        /// <summary>蓝图桩变成实体：把品质搬过去，并把桩上那条去掉。</summary>
        internal static bool Promote(int planetId, int prebuildId, int entityId)
        {
            if (!Pending.TryRemove((planetId, prebuildId), out int perItem)) return false;

            Set(planetId, entityId, perItem);

            return true;
        }

        internal static void Set(int planetId, int entityId, int perItem)
        {
            if (planetId <= 0 || entityId <= 0) return;

            if (perItem <= 0)
            {
                Built.TryRemove((planetId, entityId), out _);

                return;
            }

            Built[(planetId, entityId)] = perItem > QualityRefineryPatches.MaxPerItem
                ? QualityRefineryPatches.MaxPerItem
                : perItem;
        }

        internal static bool TryGet(int planetId, int entityId, out int perItem) =>
            Built.TryGetValue((planetId, entityId), out perItem);

        /// <summary>拆建筑时清掉，免得实体号被回收后新建筑白捡一份品质。</summary>
        internal static void Remove(int planetId, int entityId) =>
            Built.TryRemove((planetId, entityId), out _);

        internal static void Clear()
        {
            Built.Clear();
            Pending.Clear();
        }

        // ── 存档 ────────────────────────────────────────────────

        internal static void Export(BinaryWriter w)
        {
            w.Write(Built.Count);

            foreach (var pair in Built)
            {
                w.Write(pair.Key.PlanetId);
                w.Write(pair.Key.EntityId);
                w.Write(pair.Value);
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
                int perItem = r.ReadInt32();

                Set(planetId, entityId, perItem);
            }

            if (count > 0)
                ProjectEdenPlugin.Log.LogInfo($"物品品质：读回 {count} 座建筑的建造材料品质");
        }
    }
}
