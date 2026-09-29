# 采矿：速度、消耗率与产物映射

证据：[MinerComponent.cs](generated/MinerComponent.cs)、[VeinData.cs](generated/VeinData.cs)；版本见 [baseline.json](baseline.json)。本页读取的是游戏磁盘原版，未包含 mod 改写。

## 原版已核验

`MinerComponent.InternalUpdate(PlanetFactory factory, VeinData[] veinPool, float power, float miningRate, float miningSpeed, int[] productRegister)` 区分矿脉、油井和取水三条路径。供电小于 0.1 直接返回。

矿脉的计时推进包含 `power × speedDamper × speed × miningSpeed × veinCount`。这里 `miningSpeed` 影响产出节奏，`miningRate` 参与矿量消耗，不能因为都与采矿有关就混成一个倍率。

矿脉分支使用 `time / period` 得到本轮可结算数量，**不是研究站那种每次调用只结算一次主周期**。随后还受矿脉剩余量、库存及当前矿点合法性约束。修改一种机器的速度公式，不能照搬到另一种机器。

库存入口同时检查 `productCount < 50` 和 `productId == 0 || productId == veinPool[current].productId`。因此矿石直接变成锭的补丁必须同时处理「赋值」和「是否同种物品」的比较；只改赋值会在有库存后被原料 ID 比较拦住。

当前版本的矿量扣除累计在 `double costFrac`，不是每件矿独立随机掷骰子的实现。`miningRate > 0` 才走相应扣矿分支。油井另有 `amount > 2500` 的衰减边界，以及 `amount × VeinData.oilSpeedMultiplier` 的速率项，不能套用固体矿的矿点数公式。

`SetPCState` 的工作能耗请求含 `speedDamper × speed² / 100000000`。速度改变不仅影响产出，能量请求也可能发生非线性变化；还需检查 mod 是否重写相应功率字段。

## ProjectEden 对应入口

- [AdvancedMinerPatches.cs](../../ProjectEden/src/Patches/AdvancedMiner/AdvancedMinerPatches.cs)：采矿速度、缓冲和矿物映射。
- [AlienVeinMinerGatePatches.cs](../../ProjectEden/src/Patches/AdvancedMiner/AlienVeinMinerGatePatches.cs)：外星矿脉开采条件。
- [LavaPumpPatches.cs](../../ProjectEden/src/Patches/AdvancedMiner/LavaPumpPatches.cs)：岩浆取水路径。
- [advancedminer.json](../../ProjectEden/data/advancedminer.json)：项目配置，而非原版默认值。

## 常用查询

```powershell
python -X utf8 tools/dsp_knowledge.py search MinerComponent::InternalUpdate --calls
python -X utf8 tools/dsp_knowledge.py search costFrac
rg --no-ignore -n 'productCount < 50|productId|costFrac|2500' docs/knowledge/generated/MinerComponent.cs
```

真实矿速还依赖矿点数、科技、供电、矿量和物流出货。此处只确认实现结构，不根据面板速度推断当前存档的实际产量。
