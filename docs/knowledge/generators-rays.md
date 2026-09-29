# 发电与射线接收站

证据：[PowerGeneratorComponent.cs](generated/PowerGeneratorComponent.cs)、[PowerSystem.cs](generated/PowerSystem.cs)、[ItemProto.cs](generated/ItemProto.cs)。版本见 [baseline.json](baseline.json)。

## 燃料发电

`EnergyCap_Fuel` 计算本 tick 可供容量；`GenEnergyByFuel` 按实际发电能量扣燃料。标称功率、当前容量、实际发电量和燃料热值是不同字段：

| 字段 | 含义 |
|---|---|
| `genEnergyPerTick` | 标称发电能力 |
| `useFuelPerTick` | 基础燃料能量消耗尺度 |
| `fuelHeat` / `fuelEnergy` | 当前燃料单件热值 / 剩余能量 |
| `capacityCurrentTick` | 本 tick 计算出的可供容量 |
| `generateCurrentTick` | 实际发电记录 |

普通无增产路径可从 `energy × useFuelPerTick / genEnergyPerTick` 看出燃料扣除比例。增产分支还读取 `ItemProto.Productive` 和 Cargo 表；不能对所有燃料用一个固定效率倍数。

`ItemProto.fuelNeeds` 原版分配 64 个条目，初始化循环按数组长度遍历，内部按 `FuelType` 位掩码匹配。扩展新燃料位同时要考虑表长度和组件掩码类型；`PowerGeneratorComponent.fuelMask` 是 Int16，而非无限宽整型。

## 射线接收

`EnergyCap_Gamma_Req` 将方向、连续接收预热 `warmup`、透镜/催化点数和模式共同纳入容量及戴森能量请求；不是只看恒星亮度或一个速度倍率。

`EnergyCap_Gamma` 在 `productId == 0` 时返回电力容量；生产模式返回 0 给发电路径，而 `GameTick_Gamma` 用 `capacityCurrentTick / productHeat` 累加产物。**发电模式与光子生产模式是两条输出路径**。

原版 `productCount` 是 float，保留小数进度；统计通过累加前后的整数部分之差计数。缓冲达到 20 时停止继续累加并夹紧，不及时取走光子会限产。

催化剂用 `catalystCount` 储存物品，用 `catalystPoint` 储存使用进度。`useCata` 时每次消耗一点，换入一件会补 3600 点后立即扣一点。此数表示有效消耗 tick，不应脱离调用条件直接当作任何情况下的现实时间寿命。

## ProjectEden 对应入口

- [LensPatches.cs](../../ProjectEden/src/Patches/Lens/LensPatches.cs)：活性透镜、接收功率与光子产量的独立改动。
- [FuelNeedsCapacityPatches.cs](../../ProjectEden/src/Patches/Power/FuelNeedsCapacityPatches.cs)：燃料表扩容。
- [FuelSurvey.cs](../../ProjectEden/src/Patches/Power/FuelSurvey.cs)：运行时读取热值及燃料属性；具体物品值不能只由方法体推断。

```powershell
python -X utf8 tools/dsp_knowledge.py search GameTick_Gamma --callers
python -X utf8 tools/dsp_knowledge.py search GenEnergyByFuel --calls
rg --no-ignore -n 'EnergyCap_Gamma|GameTick_Gamma' docs/knowledge/generated/PowerSystem.cs
```

PowerSystem 已纳入完整索引，但本页只核验上述相关调用和组件行为，不宣称已分析全部电网分配算法。
