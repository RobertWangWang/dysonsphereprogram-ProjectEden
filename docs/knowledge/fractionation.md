# 分馏：货物数量、实物数量与成功概率

基线见 [baseline.json](baseline.json)，完整实现见 [FractionatorComponent](generated/full/Assembly-CSharp/source/FractionatorComponent.cs)。本页核验 SetRecipe、SetPCState、InternalUpdate 的处理部分与输入数量单位。

## 原版已核验

分馏不是普通 Assembler 的按配方批次消耗。`SetRecipe` 遍历 `RecipeProto.fractionatorRecipes`，用配方首项原料匹配输入物品；产物取首项结果，`produceProb = ResultCounts[0] / ItemCounts[0]`。这两个数量表达基础成功概率，而非每次取走一整批 ItemCounts。

已从本机原版资产核对：Recipe ID 115「重氢分馏」的 ItemCounts[0] 为 100、ResultCounts[0] 为 1，基础成功概率即 1%；完整记录可用 `python -X utf8 tools/dsp_proto_query.py 115 --table RecipeProtoSet` 查询。运行时 mod 仍可能改写该值。

`fluidInputCount` 是实际物品数，`fluidInputCargoCount` 是浮点货物数。输入一格带货时分别加 stack 和 1；二者比值就是当前平均堆叠数 S。不要把 cargoCount 当物品数量，也不能为了“堆叠修复”简单把二者同步加 stack。

当 power < 0.1 时整个 InternalUpdate 立即返回。能够处理的前提是输入非空、产物输出未满、回流输出未满。每次调用的进度增长近似为：

```text
S = fluidInputCount / fluidInputCargoCount
Δprogress = int(power × 166.6666667 × min(fluidInputCargoCount, 30) × S + 0.75)
progress = min(progress + Δprogress, 100000)
每 10000 progress 执行一次独立尝试
```

这里使用 while，因此一次调用可以尝试多次；但 progress 截断为 100000，使单次调用最多进行十次尝试。忽略整数舍入、缓冲堵塞和启动过程，以每秒 60 次调用估算，长期尝试率约为 `min(600, power × min(cargoCount, 30) × S)` 次/秒。它是尝试率，不是产物率。

每次尝试计算当前平均增产点 `fluidInputInc / fluidInputCount`，最多按 10 档索引；成功概率为 `produceProb × (1 + Cargo.accTableMilli[档位])`。种子通过组件自己的整数递推式更新，比较归一化随机值，不是每次调用系统随机数。改抽样次数或插入额外抽样会改变后续随机序列。

成功时增加一件产物并记录一件原料消耗；失败时增加一件回流原料，并把本次平均增产点转移到 fluidOutputInc。两种分支都会扣一件输入及对应点数，同时 cargoCount 减少 `1 / S`。因此失败不是直接销毁物品，而是进入回流缓冲，后续必须能从侧向传送带送出。

`SetPCState` 同样使用 S 和货物数量，并乘 `Cargo.powerTableRatio[incLevel]` 计算功耗倍率。提高堆叠后不能只看成功概率和带速，还要检查供电，避免名义吞吐增加但 power 下降。

## ProjectEden 对应位置

[QualityFieldAnalyzer](../../ProjectEden.Preloader/QualityFieldAnalyzer.cs) 和 [QualityTransform](../../ProjectEden.Preloader/QualityTransform.cs) 已将 fluidInputInc、fluidOutputInc 及分馏入口纳入品质分析/变换范围。磁盘原版读取到的 inc 算法不能直接当作运行时已加载 preloader 后的完整品质逻辑。

## 修改前应核对

极高堆叠会碰到十次/调用上限；仅扩大货物字段不会自动解除此门槛。输出容量判断在 while 之前，循环体没有逐次重复同一个容量检查；调整进度上限、处理次数或缓冲容量时要检查整次调用中的出入账。实际配方概率和最终速度应以运行时原型、供电、堆叠及回流情况计算。

```powershell
python -X utf8 tools/dsp_knowledge.py search FractionatorComponent::InternalUpdate --full --callers
python -X utf8 tools/dsp_knowledge.py search FractionatorComponent::SetPCState --full --calls
```
