// Copyright (C) 2026 RobertWangWang and Project Eden contributors
//
// 按 GPL-3.0 发布，详见仓库根目录的 LICENSE 与 NOTICE。
// Released under GPL-3.0; see LICENSE and NOTICE at the repository root.

namespace ProjectEden.Patches
{
    /// <summary>
    /// 统一抬高**每一个物品**的堆叠上限（背包、储物箱、物流背包共用同一个值）。
    ///
    /// <b>先把链路量出来，因为它有一个反直觉的好消息和一个典型的坑。</b>
    ///
    /// <code>
    /// ItemProto.StackSize                                   ← 唯一的源头
    ///   ├─ StorageComponent.LoadStatic()  @00EC             → 静态表 itemStackCount[12000]
    ///   │     └─ AddItem / AddItemStacked / AddItemFiltered / AddCargo / Sort /
    ///   │        DeliveryPackage.SetDeliveryItem / Mecha.AutoReplenish* …
    ///   │        全都 ldsfld 这张表，再写进 GRID.stackSize
    ///   └─ StorageComponent.Import        @0158             → 直接重写 GRID.stackSize
    /// </code>
    ///
    /// <b>好消息：老存档自愈，不需要运行时补。</b> 这个仓库的第 1 号坑是「值在创建时
    /// 烤进存档」，而 <c>GRID.stackSize</c> 确实是存档字段（<c>Export</c> @00C9 /
    /// <c>Import</c> @00DB）——但 <c>Import</c> 紧接着在 @012C–015D 又按
    /// <c>LDB.items.Select(grid.itemId).StackSize</c> **把它覆盖了一遍**。
    /// 所以每次读档每一格都会重新取值，改 proto 就够了，两个方向都跟得上。
    /// <b>这是本仓库少数几个第 1 号坑不适用的地方，值得写下来</b>——否则下一个人
    /// 会照着惯例再写一遍多余的运行时补丁。
    ///
    /// <b>坑：那张静态表只建一次。</b> <c>LoadStatic</c> 开头就是
    /// <c>if (staticLoaded) return;</c>，而它唯一的调用点是
    /// <c>StorageComponent..ctor</c> ——也就是说**第一个 StorageComponent 一被构造，
    /// 表就定型了**，之后再改 proto 一个字也传不过去。这和
    /// <c>ItemProto.InitFluids</c> / <c>InitTurretNeeds</c> 是同一族问题，
    /// 解法也一样：<b>把原版自己的构建器再跑一遍</b>，而不是自己去补那张表。
    /// <c>LoadStatic</c> 是从 <c>LDB.items.dataArray</c> 整表重建的，
    /// 所以跑两次没有副作用。
    ///
    /// <b>核对的是末态，不是我自己的贡献。</b> 重建之后回头扫 <c>itemStackCount</c>，
    /// 凡是和 proto 对不上的都报出来。只数「我改了几个」会在别人先改过、或者
    /// 表根本没重建的时候给出一个漂亮而错误的绿灯（流体白名单那次的教训）。
    ///
    /// <b>作用域说清楚：这是 proto 级的，改的是「这个物品一格能放多少」。</b>
    /// 所以背包、储物箱、物流背包、机甲的弹药/燃料格全都跟着变。
    /// <b>做不到「只改背包」</b>——真要只改背包，就得和上面那条 <c>Import</c>
    /// 每次读档的重写对着干，变成一个每帧或每次读档的补丁，比现在脆弱得多。
    /// 不受影响的是另外两套：传送带上的集装层数是 <c>Cargo.stack</c>
    /// （见 <c>PilerLevelPatches</c>），物流站每格的容量是 <c>StationStore.max</c>
    /// （见 <c>StationCapacityPatches</c>）。
    /// </summary>
    internal static class ItemStackSizePatches
    {
        /// <summary>
        /// 原版把这张表铺满 12000 格。物品 id 超出就会在 <c>LoadStatic</c> 里越界，
        /// 那是原版自己的边界，这里只用它来判断「配置的值有没有可能撑爆」。
        /// </summary>
        private const int TableSize = 12000;

        /// <summary>
        /// 上限一千万。
        ///
        /// 约束是 <c>GRID.count</c> 是 Int32，而统计面板会把一个储物箱的**所有格子加起来**
        /// （<c>SingleStorageStatPlan.CalculateStatData</c>）。储物箱的格数来自
        /// <c>prefabDesc.storageCol × storageRow</c>，**那在 <c>resources.assets</c> 里、
        /// 离线读不到**，所以这里不写一个具体格数——一千万留的是两个数量级的余量：
        /// 即便某个箱子有两百格，总和也才 20 亿，刚好贴着 Int32 而不越过。
        /// 真要再往上抬，先在游戏里量一次最大的 <c>StorageComponent.size</c>。
        /// </summary>
        private const int HardCap = 10000000;

        internal static void OnPostAddData()
        {
            int want = ProjectEdenPlugin.StationsConfig?.inventoryStackSize ?? 0;

            // **每一种状态都打一行。** 「配置缺失」「关掉了」「没生效」在日志里长得
            // 一样的话，这个功能就没法诊断——本仓库为这个形状付过六次代价。
            if (ProjectEdenPlugin.StationsConfig == null)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "物品堆叠上限：stations.json 没读到，保持原版（固体 100 / 流体 20）。");

                return;
            }

            if (want <= 0)
            {
                ProjectEdenPlugin.Log.LogInfo(
                    "物品堆叠上限：stations.json 的 inventoryStackSize 是 0，保持原版"
                    + "（固体 100 / 流体 20，本 mod 自家物品仍是各自配置的值）。"
                    + "想改就把它设成正数，背包 / 储物箱 / 物流背包一起生效。");

                return;
            }

            int size = want > HardCap ? HardCap : want;

            if (size != want)
                ProjectEdenPlugin.Log.LogWarning(
                    $"物品堆叠上限：配置写的是 {want:N0}，夹到了上限 {HardCap:N0}——"
                    + "再大会让储物箱的统计求和越过 Int32（一个大箱子 90 格）。");

            ItemProto[] items = LDB.items?.dataArray;

            if (items == null || items.Length == 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    "物品堆叠上限：LDB.items 是空的，一个也没改。");

                return;
            }

            var changed = 0;
            var already = 0;
            var overflow = 0;

            foreach (ItemProto proto in items)
            {
                if (proto == null) continue;

                // 越过静态表边界的 id 交给原版自己处理（它会越界），这里只统计并报出来，
                // 免得把别的 mod 的问题算在自己头上。
                if (proto.ID < 0 || proto.ID >= TableSize)
                {
                    overflow++;

                    continue;
                }

                if (proto.StackSize == size)
                {
                    already++;

                    continue;
                }

                proto.StackSize = size;
                changed++;
            }

            if (overflow > 0)
                ProjectEdenPlugin.Log.LogWarning(
                    $"物品堆叠上限：有 {overflow} 个物品的 id 超出了原版那张 {TableSize} 格的静态表，"
                    + "跳过了它们——那不是本 mod 的物品编号范围，多半来自别的 mod。");

            // 关键的一步：原版那张静态表只在第一个 StorageComponent 构造时建一次，
            // 之后 staticLoaded 就把门关上了。把原版自己的构建器再跑一遍，
            // 它是从 dataArray 整表重建的，所以幂等。
            StorageComponent.staticLoaded = false;
            StorageComponent.LoadStatic();

            int mismatch = VerifyTable(items);

            if (mismatch > 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"物品堆叠上限：静态表重建之后仍有 {mismatch} 个物品对不上 proto，"
                    + "说明 LoadStatic 没有按预期整表重建。背包里新放入的货会沿用旧上限"
                    + "（已存的那些仍会在读档时由 Import 按 proto 修正）。");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"物品堆叠上限：已统一为每格 {size:N0}（原版固体 100 / 流体 20）——"
                + $"改了 {changed} 个物品，{already} 个本来就是这个值。"
                + "原版那张只建一次的静态表（StorageComponent.itemStackCount）已重建并逐项核对通过。"
                + "**老存档自动跟上**：StorageComponent.Import 每次读档都会按 proto 重写每一格的上限，"
                + "所以调大调小都即时生效，不会丢东西。"
                + "作用范围是背包 / 储物箱 / 物流背包 / 机甲的弹药与燃料格；"
                + "传送带集装（stationPilerLevel）和物流站格容量（slotCapacity）是另外两套，不受影响。");
        }

        /// <summary>
        /// 扫末态：<c>itemStackCount[id]</c> 和 proto 对不对得上。
        ///
        /// <b>量的是结果，不是「我改了几个」。</b> 只数自己的贡献，会在
        /// 「别人先改过」或者「表压根没重建」的时候给出一个漂亮而错误的绿灯——
        /// 流体白名单那次就是这么假报警的，方向相反、教训相同。
        /// </summary>
        private static int VerifyTable(ItemProto[] items)
        {
            int[] table = StorageComponent.itemStackCount;

            if (table == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "物品堆叠上限：StorageComponent.itemStackCount 重建之后仍然是 null。");

                return int.MaxValue;
            }

            var bad = 0;

            foreach (ItemProto proto in items)
            {
                if (proto == null) continue;

                if (proto.ID < 0 || proto.ID >= table.Length) continue;

                if (table[proto.ID] == proto.StackSize) continue;

                if (bad < 5)
                    ProjectEdenPlugin.Log.LogWarning(
                        $"物品堆叠上限·核对：「{proto.Name}」(id {proto.ID}) proto 是 "
                        + $"{proto.StackSize}，静态表里却是 {table[proto.ID]}。");

                bad++;
            }

            return bad;
        }
    }
}
