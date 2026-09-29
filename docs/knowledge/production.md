# 生产结算：速度、周期、分频和堵料

证据：[AssemblerComponent.cs](generated/AssemblerComponent.cs) 的 `InternalUpdate`，及 [对应 IL](generated/AssemblerComponent.il)。本页适用 [核验基线](baseline.json)。

## 原版已核验

一次 `InternalUpdate` 的主要顺序：

1. 供电比例小于 0.1 时直接返回。
2. 结算已完成的增产计时器。
3. 若主计时器已满，先设 `replicating=false`，再检查产物缓冲是否可接收；通过后产出、更新统计、扣一次配方时间。
4. 若没有正在生产的周期，检查原料并扣料，设置增产/加速参数及 `replicating=true`。
5. 满足推进条件才累加 `time` 和 `extraTime`。

主周期是单次 `if` 结算。提高 `speedOverride` 只改变推进幅度，不能让原版的一次调用结算无限多个主周期。统计量 `productRegister`、`consumeRegister` 在实际产出/扣料时更新，并有锁。

主产物出货闸并不统一：按配方分支存在 `produced > productCounts × 9`、`×19` 和 `produced + productCounts > 100`。同一个方法要枚举全部分支，不能只改第一处。

堵料时可能留下 `replicating=false` 且 `time >= timeSpend`。这是上一轮原料已经付出、成品尚未落入缓冲的状态。如果补丁清掉满计时器或强行重新启动，就可能重复扣料。

`extraTime` 和 `time` 是两条独立轨道。只压主计时器会漏出增产；每次又把增产进度彻底清空，则永远攒不出增产。

## ProjectEden 改动与速率

源码：[MegaAssembler 目录](../../ProjectEden/src/Patches/MegaAssembler)。重点阅读：

| 文件 | 负责内容 |
|---|---|
| `MegaAssemblerPatches.cs` | 追加周期、缺料退出、调用各状态钩子 |
| `MegaThrottle.cs` | 每建筑周期数、分频、星系加成、供电缩放及错帧 |
| `MegaBatchSettle.cs` | 条件满足时批量结算，喷涂等复杂情况回退 |
| `MegaOutputGatePatches.cs` | 扩大巨型建筑产物闸，必须覆盖实际 IL 分支 |
| `MegaStationPatches.cs` | 机器缓冲与附属物流站之间搬运 |

满供电且结算速度足够时，无增产的长期周期速率：

`周期/秒 = 60 × cyclesPerTick ÷ EffectiveDivider`

全局分频 G 同时乘到周期预算和分频上，因此在上述条件下抵消。它改变批量大小和访问频率，不直接减半产能。对短周期或少量采样窗口，错帧和批量会造成瞬时波动。

反物质四座在 1.13.2 都是 `cyclesPerTick=100`、`tickDivider=70`，黑洞加成将分频变为 35。每次末端合成出 2 个反物质：普通星系 10285.714 个/分钟，黑洞 20571.429 个/分钟。四座周期相同，所以主线配比 1:1:1:1。

上式是配置理论值。缺料、供电不足、输出满、物流节流、增产、游戏逻辑帧率和其他补丁都需要另行核对。配方显示秒数本身不能代表 mod 运行时产能。

## 核验入口

```powershell
python -X utf8 tools/sim_throttle.py
powershell -ExecutionPolicy Bypass -File tools/check_output_gate.ps1
```

`sim_throttle.py` 是结算状态的离线模型；`check_output_gate.ps1` 读取本机游戏实际 IL。两者用途不同，模型通过不能替代 IL 匹配检查或游戏内验证。
