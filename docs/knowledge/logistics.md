# 物流、供需标记与并行调度

证据：[PlanetTransport.cs](generated/PlanetTransport.cs)、[StationComponent.cs](generated/StationComponent.cs)、[GameLogic.cs](generated/GameLogic.cs)。适用 [核验基线](baseline.json)。

## 原版已核验

当前原版完整入口签名是：

`PlanetTransport.GameTick(long time, bool isActive, bool multithreaded, int threadOrdinal)`

Harmony 补丁可以只声明它需要的目标参数。补丁里出现 `(__instance, time)` 不意味着原方法只有一个参数。用符号索引检查完整签名，尤其要区分重载。

`GameTick` 调用物流站 `InternalTickLocal` / `InternalTickRemote`，并处理配送器。`GameLogic.FactoryTransportGameTick_Parallel` 通过 `Interlocked.Increment` 分发工厂任务，然后分别调用各星球的 transport。多个行星可同时进入同一个静态补丁。

`StationComponent` 的配对逻辑使用 `localLogic` 与 `remoteLogic` 分别处理本地和星际供需。`SetStationStorage` 不只是改 `itemId`：它还处理容量和供需变化，触发相应配对更新。不能假设绕过这一入口直接写字段必然等价。

原版大量库存访问在 `lock(storage)` 内执行。物流库存里的 `count` 与 `inc` 是关联量；搬走数量却不扣增产点，会把剩余库存的单件增产点抬高。

## ProjectEden 改动

| 源码入口 | 用途 |
|---|---|
| [LabLogisticSupplyPatches.cs](../../ProjectEden/src/Patches/Lab/LabLogisticSupplyPatches.cs) | 研究站间互供及研究站与物流站互通 |
| [MegaStationPatches.cs](../../ProjectEden/src/Patches/MegaAssembler/MegaStationPatches.cs) | 巨型建筑制造缓冲和站内库存衔接 |
| [MegaVirtualLogisticsPatches.cs](../../ProjectEden/src/Patches/MegaAssembler/MegaVirtualLogisticsPatches.cs) | 同星球虚拟运输、轮转扫描 |
| [QualityAccess.cs](../../ProjectEden/src/Patches/Quality/QualityAccess.cs) | 运行时品质字段访问及分摊 |

本 mod 的研究站自动取料只从物流站 **本地 Supply** 拿货，自动出货只填 **本地 Demand**。这是 mod 的自动搬运约定，不能泛化成「原版所有插入入口都检查物流标记」。

1.13.2 的研究站互供先扣来源 `produced[]`，后续出货重新汇总剩余产物，避免同一批货发两次。需要同时检查生产原料的件数和科研缓冲的 ×3600 单位，详见 [研究站页](labs.md)。

跨星球复用临时字典时使用线程隔离或明确同步。锁某个站的 `storage` 并不能保护其他星球同时写一个普通静态 Dictionary。

## 快速排查

- 来源/目标的 `id` 是否与池下标一致，是否遇到已删除实体。
- 是本地还是星际物流设置，Supply / Demand 是否与搬运方向吻合。
- 取料目标是机器缓冲还是附属站库存，是否漏掉它们之间的搬运阶段。
- 数量、增产、品质是否按同一份货一起结算；比例的分母必须使用扣货前库存。
- 若总吞吐正确但某个站长期独占供货，检查扫描起点是否轮转。
- 并行入口中避免每台机器重新扫描全部站点；优先汇总需求、扫描来源、分发的线性结构。


## 增量配对表对账时序修复

`LocalPairIndex.Rebuild` 原实现将 `!DueForReconcile(...)` 放在增量入口条件里：到期时不执行本批 `RebuildMany`，却拿旧表 checksum 与当前槽位全量重建的结果比较。正常新增或改槽因此会误报并关闭增量。已用实际 C# 类在第200个变更上复现旧实现失败。

修复后顺序为：确认 delta 完整 → RebuildMany → 判断是否到期 → 对当前增量表与全量重建对账。未知/过大/不完整 delta 只建立全量新基线，不做这次增量判错；全量重建重置累计计数。无人机订单修复仍在每次冲刷后执行一次。

诊断按站记录配对数和顺序无关校验和，仅对账时分配 O(站点数) 摘要。失败打印本批变更站及最多8个差异站的槽位、计数和校验和，仍关闭增量并留下全量表。哈希对账不是逐条数学证明。

验证：`python -X utf8 tools/sim_pairindex.py`；`dotnet run --project tools/LocalPairIndexTests -c Release`。后者直接链接实际 LocalPairIndex，游戏对象与原版配对方法用独立替身，覆盖阈值处改槽、新增、拆除、双键换向、不完整 delta，以及主动注入错误的回退负例。实际蓝图存档仍需重启后核验日志。
