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

## 无在途船时的星际更新

原版 `InternalTickRemote` 在曲速器补充之后计算飞行速度、幂/对数和坐标临时量，再遍历 `workShipCount`；尾部无条件调用 `ShipRenderersOnTick` 并递减 `priorityLocks`。因此没有在途船也不能直接跳过整个方法：曲速器补充、旁挂泊位位图对账和优先级锁仍需更新。`IdleRemoteTickPatches` 仅把补曲速器后的空船路径接到原尾部；`tools/IdleRemoteTests` 校验实际游戏IL并测试补充、飞行与尾部行为。

## 建索引与搬运的计时边界

`SupplyIndexProbe` 对聚变燃料/储能柜各自Supply和BuildIndex做全量嵌套计时；在Supply结束时统一提交，避免跨报告窗口错配总项与子项。Supply早退计入总轮次，BuildIndex仅在实际调用时计数；总项减索引项还包含遍历和配置判断，不能称为纯搬运。现有巨型虚空物流本身已采用汇总/搬运/回填，而非每台扫全星球；是否并行建索引应先看分段数据，不能从物流总耗时推断。

## 储能柜候选索引并行试验

`ParallelExchangerIndex` 按原轮转offset分成连续三段，各段只写独立列表，扫描完按0/1/2顺序合并，保证站序与格位序一致。后台仅访问托管字段并取storage锁；不并行执行Ship/Fill，也不缓存跨轮库存。全局忙时串行回退，异常必须等待所有线程再释放引用。`tools/ExchangerLogisticsTests/ExchangerReference.cs` 保留试验前算法用于逐字段对照。辅助池为两个后台线程，与需求刷新辅助池独立，实际收益需包含等待/合并开销评估。


## 巨型建筑按变化维护的候选索引

实验性巨型物流候选索引：`perfprobe.indexedMegaLogistics` 复用按物品、供需方向维护的站点/格位候选，六阶段只查询相关候选，保留原站序、轮转、同站供需和实时库存/增产/品质结算。每轮仍校验所有站点布局（含其他Mod直接改槽），仅变化站点更新索引；库存数量变化不会重建布局。`parallelMegaIndex` 在至少2048站位、CPU至少4逻辑核心时，用调用线程加2个常驻辅助线程分段校验及构建变化快照，等待完成后统一提交映射；实际搬运串行。辅助线程只读托管字段，逐站取库存锁，不访问Unity API或游戏原型。全局辅助池忙时串行准备；关闭并行仍保留候选索引，关闭索引恢复原扫描。构建异常等待辅助线程结束后上抛，本局后续回退原扫描。索引随PlanetTransport生命周期回收，拆除/重建/换槽及游标收缩会清理旧映射。两个开关内嵌默认false，当前测试profile开启并保留探针；修改配置后重启生效。`[巨型物流候选索引]` 报告轮次、并行轮次、校验/更新站位、六阶段候选累计及准备耗时（含等待/提交，不含候选查询和搬运）；实际帧率收益待游戏日志验证。测试包含2000组小世界×3轮及24组2053站世界×3轮与冻结原算法逐字段对照，另验缓存复用、直接改槽、拆除重建、游标伸缩、候选顺序、争用、关闭及故障回退。

实现入口：`MegaCandidateIndex.Prepare` → 并行或串行 `State.Scan` → 调用线程 `Commit` → 六阶段 `Select/Slots` → `Release`。缓存仅保存对象身份、巨型分类、itemId/localLogic和候选位置；每轮布局校验是当前兼容性成本，不能描述为完全事件驱动。多线程依赖游戏物流阶段内布局稳定的通常调度约束，不支持其他Mod无锁并发替换站点布局。`tools/MegaLogisticsTests/CandidateIndexTests.cs` 校验缓存生命周期与候选顺序，`MegaVirtualReference.cs` 为冻结原算法对照。总收益需要结合巨型物流整体耗时，不能只比较准备阶段。
