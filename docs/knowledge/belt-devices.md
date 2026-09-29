# 分拣器、集装机、喷涂机与分流器

证据：[InserterComponent.cs](generated/InserterComponent.cs)、[PilerComponent.cs](generated/PilerComponent.cs)、[SpraycoaterComponent.cs](generated/SpraycoaterComponent.cs)、[SplitterComponent.cs](generated/SplitterComponent.cs)、[CargoTraffic.cs](generated/CargoTraffic.cs)。对应 [程序集基线](baseline.json)。

## 分拣器：按实际接收量扣账

当前原版存在三个更新入口：`InternalUpdate`、`InternalUpdate_Bidirectional`、`InternalUpdateNoAnim`。只改带动画的普通入口不能证明所有路径被覆盖。

`itemCount` 是携带的物品数量，`stackCount` 是本轮累计的货堆/投放批次状态；取到一个 Cargo 时可能给 `itemCount` 加上它的 stack，而只给 `stackCount` 加 1。不能把两字段当成同一单位。

投放路径调用 `PlanetFactory.InsertInto`，以返回值扣 `itemCount`，以 `投入增产点 - remainInc` 扣 `itemInc`。如果目标只收下一部分，剩余货和点数仍应留在分拣器。虚拟搬运补丁同样需要遵守「实际收下多少，来源才付多少」。

`InternalUpdateNoAnim` 有检查目标 `needs[0..5]` 是否全零的快捷路径。这是实际六槽硬编码之一；支持更多配方输入时，仅扩大机器数组不一定覆盖运输端的判断。是否被其他 mod 补丁改过，要另查运行时。

## 集装机：四处常量是一组账

在 `PilerComponent.InternalUpdate` 同物品合堆分支中，原版的 4 同时用于：

1. 判定总堆叠是否超过输出上限。
2. 按总增产点/总数量计算输出 4 件应分到的点数。
3. 实际调用 `AddCargo` 创建 4 件输出。
4. 从输入缓存扣掉已输出的 4 件。

改堆叠上限时必须四处一致。只增大点数分摊而输出数量仍是 4，会丢失增产点；只改输出数量而不改余量，则会破坏物品守恒。

反向拆堆分支使用另一组半堆计算和余量分摊，不应因为同在一个方法里就全部替换成集装上限。原版缓存字段和多处转换仍使用 byte，需要结合本 mod 的 preloader 分析。

项目入口：[PilerLevelPatches.cs](../../ProjectEden/src/Patches/Station/PilerLevelPatches.cs)、[Cargo 字段加宽](cargo-belts.md)。

## 喷涂机：物品属性与库存单位

`SpraycoaterComponent.InternalUpdate` 从建筑 `PrefabDesc.incItemId` 白名单匹配增产剂，读取该物品的 `Ability` 作为喷涂能力、`HpMax` 作为基础喷涂次数。不能仅因某物品有 Ability 就认定它是增产剂。

喷涂剂自己被增产时，额外次数进入 `extraIncCount`；基础次数是 `incCount`。喷一堆货需要按物品数量支付次数，不是每个 Cargo 只扣一次。

原版检查 `可喷数量 × incAbility > 当前货物总 inc` 后才喷，并把总 inc 写为目标值，先扣额外次数再扣基础次数。供电、传送带速度、喷涂计时器和次数库存都会限制实际工作。

本 mod 的行星自动喷涂入口：[PlanetSprayPatches.cs](../../ProjectEden/src/Patches/Spray/PlanetSprayPatches.cs)。它是额外机制，不能把喷涂机原版限制直接套成自动喷涂的实现。

## 分流器：状态与执行分开

`SplitterComponent` 保存 input/output 槽、优先级、过滤器和 top/bottom 连接；实际搬运入口在 `CargoTraffic.UpdateSplitter` 与 `UpdateSplitterAsync`。调度入口 `SplitterGameTick` 遍历有效组件后调用更新。

这些路径会取得输入路径末端货物，再按输出空间、过滤与优先级处理。查「有入口却不出货」时，除了字段，还要看输出路径是否有空位和上方储物连接，而非只搜 SplitterComponent 的方法。

```powershell
python -X utf8 tools/dsp_knowledge.py search InserterComponent::InternalUpdate
python -X utf8 tools/dsp_knowledge.py search UpdateSplitter --callers
python -X utf8 tools/dsp_knowledge.py search PilerComponent::InternalUpdate --calls
```
