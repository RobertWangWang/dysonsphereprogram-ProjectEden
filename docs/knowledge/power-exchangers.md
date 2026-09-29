# 能量枢纽与蓄能柜

证据：[PowerExchangerComponent.cs](generated/PowerExchangerComponent.cs) 的 `InputUpdate`、`OutputUpdate`、`CalculateActualEnergyPerTick`、`Export` / `Import`；[对应 IL](generated/PowerExchangerComponent.il)。适用 [核验基线](baseline.json)。

## 原版已核验

| 字段 | 含义 |
|---|---|
| `energyPerTick` | 标称每逻辑帧能量吞吐，Int64 |
| `currEnergyPerTick` | 当前帧实际吞吐，放电写为负值 |
| `maxPoolEnergy` | 一份蓄电器的能量容量 |
| `currPoolEnergy` | 当前能量池累计值 |
| `emptyCount` / `fullCount` | 机内空/满蓄电器数量 |

`InputUpdate` 只在 `state == 1f` 充电；`OutputUpdate` 只在 `state == -1f` 放电。充电受可用电力 `remaining`、速率、空柜数量和满柜空间共同约束。

换一个柜子的分支是 `if`，没有重复换柜循环。充电要求 `emptyCount > 0 && fullCount < 20`；放电要求 `fullCount > 0 && emptyCount < 20`。仅提高功率不能消除每次调用最多换一个柜子或内部库存堵塞的限制。

`CalculateActualEnergyPerTick` 会考虑增产点对应的 `Cargo.accTableMilli`。空柜携带的点数可改变充电速度；它不是把柜子的额定容量也按同一倍数放大。

`Export` / `Import` 持久化能量与组件状态。改 proto 后不能假设旧存档组件里的 `energyPerTick` 自动刷新，必须检查加载后的对齐入口。

## ProjectEden 与反物质支线

配置：[megabuildings.json](../../ProjectEden/data/megabuildings.json)、[machines.json](../../ProjectEden/data/machines.json)。运行时入口在 [MegaAssembler](../../ProjectEden/src/Patches/MegaAssembler) 的能量枢纽相关补丁，另可用 `rg -n 'AlignRate|FindPair' ProjectEden/src` 定位存档对齐。

当前奇点储能厂配置 `energyPerTick=600000000000`，按 60 tick/s 折为 **36 TW**（充放电共用）。这是额定值；原版每 tick 最多转换一个柜子，电浆柜 / 过载柜的持续吞吐分别受 10.7892 / 21.5784 TW 限制。电浆柜容量倍率 333；仓库运行时调查记录原版容量 540 MJ，对应 **179.82 GJ**。后一个数依赖原版资源中的 prefab 值，不是此 DLL 单独证明的常量。

因此无喷涂时的充电能力约为 **10 个柜/秒**，前提是供电和进出货充足。1.13.2 反物质主线需要普通星系约 85.714 个满柜/秒、黑洞系约 171.429 个/秒，按此能力至少配 **9 / 18 座**充电厂；平均充电功率需求约 **15.413 / 30.826 TW**，未计其他设备。

当前视界蒸发配方只输出霍金辐射和高能γ光子，**不返还空柜**。柜体制造是持续消耗，不能把反物质这条支线当成普通的「空柜—充电—放电—返空柜」循环。

这些是满条件下的配置推算。`energyPerTick` 是能力上限，不代表电网随时能拿出对应功率；`fullCount` 堵满也会表现成充电变慢。
