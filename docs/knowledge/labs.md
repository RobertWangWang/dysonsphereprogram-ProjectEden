# 研究站：缓冲单位、生产和科研

核验基线：[baseline.json](baseline.json)。证据：[LabComponent.cs](generated/LabComponent.cs)、[LabComponent.il](generated/LabComponent.il)、[PlanetFactory.cs](generated/PlanetFactory.cs)。行号可随反编译器变化，优先按类型和方法名查找。

## 原版已核验

| 字段 | 单位与用途 | 关键入口 |
|---|---|---|
| `served[]` | 配方原料的实际件数 | `InternalUpdateAssemble` |
| `produced[]` | 配方产物的实际件数 | `InternalUpdateAssemble` |
| `incServed[]` | 原料携带的增产点数 | `split_inc_level` |
| `matrixServed[]` | 科研矩阵点数，每个矩阵对应 3600 | `PlanetFactory.InsertInto` 两个重载 |
| `matrixPoints[]` | 当前科技每 hash 消耗的矩阵点数 | `InternalUpdateResearch` |
| `needs[]` | 需求物品 ID，0 表示不请求 | 两个 `UpdateNeeds*` |

不能用「itemId 是否为矩阵」决定单位。矩阵作为宇宙矩阵的原料时进入 `served[]`，只有科研模式的 `matrixServed[]` 使用 ×3600。

`UpdateNeedsAssemble` 和 `UpdateNeedsResearch` 明确展开六个位置。研究需求阈值为 36000 点（10 个），这与堆叠输送中同值的常量含义不同。第七槽既要扩数组，也要覆盖写死六槽的方法。

`UpdateOutputToNext` 的科研分支检查下一层 `needs`，本层保留 7200 点（2 个），每次最多搬 36000 点（10 个）。**这是搬运速率，不是仓储容量。** 把速率改成整层容量会让货物快速向顶层集中。

`InternalUpdateAssemble` 主产物结算用 `if` 而非 `while`，一次调用至多完成一次主周期；增产用另一个计时器。`InternalUpdateResearch` 则从参数 `research_speed` 算 hash 预算 `(int)(research_speed + 2f)`，不读取生产字段 `speed`。

[FactorySystem.cs](generated/FactorySystem.cs) 的 `GameTickLabResearchMode` 把 `history.techSpeed` 传入科研方法。生产既有 `GameTickLabProduceMode`，也有 [GameLogic.cs](generated/GameLogic.cs) 的 `_lab_produce_parallel` 直接调用组件。因此只拦 `FactorySystem` 的生产入口不能覆盖并行路径。

## ProjectEden 改动

- [MatrixLabPatches](../../ProjectEden/src/Patches/Lab/MatrixLabPatches.cs)：生产速度、三类存储及独立的堆叠搬运速率。
- `BioMatrixPatches` / `UniverseMatrixPatches` / `LabSeventhSlotPatches`：第七种矩阵与七原料配方的兼容。
- `LabLogisticSupplyPatches`：在行星物流 tick 后执行自动供料。汇总缺口 → 从同星球研究站产物取货 → 从 Supply 物流格补足 → 先生产、后科研分发 → 剩余产物送 Demand 物流格。

1.13.2 的取料暂存字典统一使用实际件数。科研缺口按 `(目标点数 - 当前点数) / 3600` 向下取整；写入科研缓冲区才乘 3600。这样每件来源物品都有完整去向，小于一件的缺口等待后续消耗。

研究站互供要求 `logisticSupply` 和 `logisticOutput` 同时开启，并遵守 `outputReserveItems`。临时字典是 `[ThreadStatic]`，因为不同星球的物流会并行执行。

## 排错顺序

1. 确认当前是生产还是科研模式，对应检查 `served` 或 `matrixServed`，不要混算。
2. 核对实际 `recipeExecuteData.requires`、数组长度和 `needs` 是否覆盖全部输入。
3. 看来源是否有可转移的 `produced`，是否被保留量拦住；物流来源必须是本地 Supply。
4. 看科研缺口是否不足一个完整矩阵，以及生产是否优先消耗了本轮库存。
5. 改补丁后运行 `dotnet run --project tools/LabLogisticsTests` 和对应构建配置的 `verify_harmony.ps1`。

仓库源码位于 `ProjectEden/src/Patches/Lab/`；[进入源码目录](../../ProjectEden/src/Patches/Lab)。
