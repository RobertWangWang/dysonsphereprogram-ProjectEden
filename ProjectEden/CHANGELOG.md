# 更新日志

## 1.10.5

**所有物品的堆叠上限统一抬到每格 10000**（原版固体 100 / 流体 20）。
配置是 `stations.json` 的 `inventoryStackSize`，填 **0 就是原版**，上限一千万。

作用范围是**背包、储物箱、物流背包、机甲的弹药格与燃料格**——这不是选择，是结构：
全游戏只有 `ItemProto.StackSize` 这一个源头，所以它们共用同一个值，
**做不到「只改背包」**。和另外两套没有关系：传送带上堆几层是 `Cargo.stack`
（`stationPilerLevel` 5000），物流站每格装多少是 `StationStore.max`（`slotCapacity` 1000 万）。

> **老存档自动跟上，而且两个方向都跟——这一条是量出来的，不是假设的。**
> 每一格的上限确实进存档（`StorageComponent.Export` @00C9），按本仓库的第 1 号坑
> （值在创建时烤进存档）应该要配一个运行时补齐。**但不用**：
> `StorageComponent.Import` 在 @012C–015D 紧接着又按
> `LDB.items.Select(grid.itemId).StackSize` 把它覆盖一遍。所以调大调小都即时生效、
> 一件东西都不会丢；调小时唯一的现象是已装超量的那一格暂时显示超额，用掉一些就正常。
> **写运行时补齐之前先读一遍 Import 有没有重新推导**，这次读了就省掉了一整块死代码。

> **真正的坑在另一头：那张静态表只建一次。**
> `StorageComponent.itemStackCount` 是 `new int[12000]`，由 `LoadStatic` 从
> `ItemProto.StackSize` 填出来，而所有写入路径（`AddItem` / `AddItemStacked` /
> `AddCargo` / `Sort` / `DeliveryPackage.SetDeliveryItem` / `Mecha.AutoReplenish*` …）
> 读的都是它。`LoadStatic` 开头是 `if (staticLoaded) return;`，唯一调用点是
> `StorageComponent..ctor`——**第一个储物组件一构造，表就定型**，之后改 proto 一个字
> 也传不过去。这和 `ItemProto.InitFluids` / `InitTurretNeeds` 是同一族，
> 解法也一样：把原版自己的构建器再跑一遍（它从 `dataArray` 整表重建，幂等），
> 而不是自己去补那张表。**但触发点不同**——它不在 `PreloadThread` 里，
> 所以「对着 0x0E11 比一下偏移」这条旧判据会漏掉它；真正的判据是
> 「谁守着重建、谁调用它」。

> 核对的是**末态**：重建之后回头逐项扫 `itemStackCount` 和 proto 对不对得上，
> 对不上就打 ERROR。只数「我改了几个」会在别人先改过、或者表根本没重建的时候
> 给出一个漂亮而错误的绿灯——流体白名单那次就是这么假报警的。

上限一千万的理由：`GRID.count` 是 Int32，而统计面板会把一个储物箱**所有格子加起来**。
箱子格数在 `resources.assets` 里离线读不到，所以不按某个具体格数算，留两个数量级余量。

---

这个文件只写**当前版本**。往期更新日志在仓库的 `ProjectEden/CHANGELOG-history.md`：
<https://github.com/RobertWangWang/dysonsphereprogram-ProjectEden/blob/main/ProjectEden/CHANGELOG-history.md>
