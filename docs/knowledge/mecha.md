# 机甲能源、手搓和研究

基线见 [baseline.json](baseline.json)。参考：[Mecha](generated/Mecha.cs)、[MechaForge](generated/MechaForge.cs)、[MechaLab](generated/MechaLab.cs)。

## 原版已核验

### 燃料与能量

`Mecha.GenerateEnergy(double dt)` 先增加 `corePowerGen * dt`，限制在 coreEnergyCap 内，然后计算反应炉可以补入的能量。`reactorPowerGen * 倍率 * dt` 受核心剩余容量限制。

倍率包含燃料 `ReactorInc + 1`：有增产点时，Productive 燃料使用 incTableMilli，其余使用 accTableMilli。取入新燃料时，HeatValue 只对 Productive 燃料乘 incTableMilli 对应的能量加成，不能把输出功率倍率和总热值倍率混为一谈。

补充燃料在 `while (reactorEnergy < 本次需要能量)` 内执行。读取下一件之前，若旧 `reactorItemId == 2207`，先将一件 2206 以旧 reactorItemInc 返给玩家并显示提示；然后才更新当前燃料 ID。返壳时点是“需要换入燃料”，不是独立的每帧余额检查。

### 手搓

`MechaForge.GameTick(long time, float deltaTime)` 只处理当前队列首任务。工作且玩家存活时，按 `replicateSpeed * 燃料倍率 * 10000 * 实际供能比例` 推进整数 tick。燃料 5206 的额外倍率来自 Ability 与 accTableMilli。

完成判断是一次 `if`，并非按溢出时间循环完成所有任务。提高 replicateSpeed 很多倍时，必须检查这个逐次结算门槛。完成后的产物可以供给父任务，或进入交付逻辑；不能假设所有中间产物立刻进入背包。totalTime 的估计用总剩余 tick / 600000 / 名义速度，不包含实际供能比例。

### 机甲研究

`MechaLab.itemPoints` 与研究站一样使用 **3600 点/件**。`ManageSupply` 按剩余科技需要计算点数，再向上取整需要的实物数量，实际拿到多少才增加多少点数。`ManageTakeback` 按整数除以 3600 返还，随后清空点数，余数不能当整件返还。

`MechaLab.GameTick` 先 QueryEnergy；ratio 不等于 1 则不研究。每次上传量从 techSpeed 开始，受各物品 `现有点数 / 每 hash 消耗点数` 的最小值限制。扣点、AddTechHash 后才 UseEnergy。它不是制造矩阵的方法，也不能套用制造站 served 的单位。

## ProjectEden 对应补丁

[MechaFuelShellPatches](../../ProjectEden/src/Patches/Power/MechaFuelShellPatches.cs) 将返壳判断时读取的满蓄电器 ID 映射到 2207，并将两处空壳常量替换成按实际燃料取配对 ID。真实 reactorItemId 保留到原版换燃料，避免丢失配对信息。

## 查询与边界

```powershell
python -X utf8 tools/dsp_knowledge.py search Mecha::GenerateEnergy --full --calls
python -X utf8 tools/dsp_knowledge.py search MechaForge::GameTick --full --callers
python -X utf8 tools/dsp_knowledge.py search MechaLab::GameTick --full --callers
```

本页未完整审查机甲移动、护盾、战斗、死亡回收、自动补燃料和任务递归展开；全量源码已提供这些入口，但运行时效果还取决于科技、原型和 mod 补丁。
