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

反物质四座当前已取消独立节流和黑洞加成，跟随全局 `cyclesPerTick=60`、`globalTickDivider=2`，即轮到时预算120周期。满供料、满供电、出货畅通且无增产时，每逻辑分钟216000周期；末端每周期50个反物质，即10800000个/逻辑分钟。四座主线配比1:1:1:1。视界蒸发炉每周期消耗1只满充电浆蓄能柜，因此供柜速率也是实际瓶颈之一。

上式是配置理论值。缺料、供电不足、输出满、物流节流、增产、游戏逻辑帧率和其他补丁都需要另行核对。配方显示秒数本身不能代表 mod 运行时产能。

## 核验入口

```powershell
python -X utf8 tools/sim_throttle.py
powershell -ExecutionPolicy Bypass -File tools/check_output_gate.ps1
```

`sim_throttle.py` 是结算状态的离线模型；`check_output_gate.ps1` 读取本机游戏实际 IL。两者用途不同，模型通过不能替代 IL 匹配检查或游戏内验证。


## 均匀增产批量路径

`MegaProliferatorBatch` 接在真实补跑调用之后，支持原版类型 1–5 的普通配方（增产/加速两模式）。每槽 `incServed % served == 0` 保证原版 `split_inc_level` 的余数恒为零，因而可合并扣点。`incUsed` 是曾经喷涂的历史位，原版扣料只会将其置 true；不能据此断言当前库存带增产点。

计划阶段只模拟标量计时器，遵守额外产出→主产物闸→扣料→条件推进的顺序；原料或输出边界由真实调用接续处理，不改变周期预算与倍率。品质及自定义类型不参与此新路径。首次及每 65536 次命中在副本上逐项回放对照，失败整局关闭批量。旧无增产路径保留。

验证命令：`python -X utf8 tools/ProliferatorBatchTests/prepare.py`，随后 `dotnet run --project tools/ProliferatorBatchTests -c Release`。准备脚本提取本机反编译的真实方法与增产表，只把产物闸替换成 mod 的 Scale 调用；生成代码留在被忽略的 tools/out。测试链接实际新批量源码，并提取实际补跑循环，覆盖 40000 组状态；这不能替代游戏中第三方补丁组合的验证。

存档事件的结束日志改为直接写出，避免退出时等待 UI 帧刷新队列而丢失。


旧存档修复：MegaTick 原先在 ApplySpeed 之前按组件速度返回，旧低速字段无法自愈；现在低速组件先核对物品原型的 assemblerSpeed，只有巨型原型才继续 ApplySpeed。AntimatterLineProbe 在 systemTiming 下每20秒输出本地机器库存/供电/实测速率，实际用户瓶颈需结合重启后的日志确认。离线测试覆盖四类配方的旧批量路径满载吞吐及低速入口。

## 巨型建筑储物格同步快速路径

`MegaStationPatches.UpdateStationStorage` 的耗时包括布局管理及双向搬运，并非配方结算本身。常见的连续、唯一物品布局由 `HasDirectLayout` 在库存锁内逐次验证，直接定位输入输出格；后方任何物品标签、容量异常、重复物品或催化反应器均回退原布局逻辑。不延迟旧配方货物取空后的清理，也不缓存库存。`tools/ProductionStorageTests` 保存改动前完整实现，用同一组动态操作逐字段对照。离线微基准不能替代游戏探针。

## 巨型建筑传送带槽位选择

`BeltSlotSelection.Prepare` 每次扫描实时方向和beltId，清理无方向的残留beltId/counter，然后按升序输出有效槽位索引，保持先输出后输入。不改变接带/拆带生效时间，也不缓存科技等级；只在有输出连接时读取集装科技。原有I/O方法体保留，32格以上使用线性选择。`tools/BeltSlotSelectionTests` 对照旧双循环的访问顺序、连接变化与残留清理。

## 组装机并行任务均衡试验

原版 `_assembler_parallel` 已按建筑分散到工作线程，入口批大小为24+线程数、保护范围3、Redispatch尝试上限2。`ScatterTaskContext` 初始按数量分配，Redispatch在锁内拆分剩余区间，因此数量相近不代表巨型建筑计算成本相近。实验开关仅调整为批大小16/尝试8，保持原任务所有权与屏障。`tools/ProductionSchedulingTests` 依赖本机知识库generated中的ScatterTaskContext、ScatterThreadContext、SimpleLock反编译缓存；测试工作体是模拟负载。接手成功不等于FPS提升，需要同时比较实际生产与整体耗时。
