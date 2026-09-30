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


## 巨型建筑增产与加速修复（1.13.6 后续工作区）

原版在 `time < timeSpend && extraTime < extraTimeSpend` 时才推进计时器；旧 `RewindExtra` 却假定每次都加过整份 extraSpeed，分频中不断倒扣，可能留下长期负增产进度。加速模式只放大 speedOverride，而巨型结算次数仍受固定预算限制；额外产品也会提前占满旧产物闸。此前“真实原版回放与批量一致”只能证明批量优化等价，不能证明整套节流语义正确。

`MegaProliferatorTimingPatches` 在两处真实推进表达式中加入 helper：巨型主进度每次最多一个配方时间，额外增量 = 主进度增量 × extraSpeed / speedOverride；普通建筑返回原增量。主进度为暂停哨兵时两路均不推进。Hold/Release 不再倒扣或另发增产份额；Release 仍只放行已扣料周期。进入巨型结算时修复负额外进度并将旧主计时溢出归一到一个在制周期；日照/催化暂停保留已经赚取的额外进度。转译器形状不匹配则报错并保留旧时序。

加速预算按当前全部原料槽最低点数等级计算，混合喷涂沿用原版整除；不读取 incUsed 历史位。预算在本轮补跑开始时确定，轮内喷涂混合比例变化不会重新增加预算，稳定满喷涂时达到标称倍率。小数周期按实际分频轮次错峰分配。备料和7处乘法闸、2处冶炼加法闸按增产表/加速表的最高倍率预留缓冲；容量不是无条件生产指令，原料扣除、背压、真实生产功耗及增产点仍由原版方法处理。均匀喷涂批量计划同步使用相同计时与容量 helper，自检仍逐次回放。

验证：`python -X utf8 tools/MegaProliferatorTests/prepare.py` 后运行 `dotnet run --project tools/MegaProliferatorTests -c Release`。提取本机真实 InternalUpdate、喷涂表和当前 Hold/Release，通过真实Harmony挂接两个转译器；另以Cecil核对已安装游戏的2个计时推进、7个乘法闸、2个加法闸锚点。覆盖8种配方类型×3种产量×3种分频（1/2/4），三级增产剂+25%及2倍加速、0–10级档位、串行与批量一致、库存/统计守恒、无料、未喷涂原料、堵料、断电、普通机器、分频余量、负存档进度及小数预算。原有40000组旧路径批量回放仍通过。未在离线环境执行完整Unity工厂、品质preloader组合和第三方Mod组合，实机仍需重启后复测。


### 物流进料导致面板 0% 的断点

`MegaStationPatches.UpdateStationStorage` 原实现只执行 `storage.count -= take` 和 `served[i] += take`，既没有扣来源 `storage.inc`，也没有增加 `incServed[i]`。因此喷涂虽到了建筑的物流槽，原版 InternalUpdate 扣料时仍得到0级；UIAssemblerWindow.RefreshIncUIs 从 extraSpeed/speedOverride 计算的0%是生产状态的真实反映。上一轮直接预填 incServed 的测试无法发现这个入口断点。

现在使用 `MegaInputTransfer.Move`，在原库存锁内按取料前 count 比例以64位中间值转移inc，源和目的同步扣加；部分取料的余数留源，取空后点数清零。不猜测或返还过去漏传的喷涂点，也不改UI数值。已有未喷涂缓冲料与新料混合后按原版最低等级规则计算，需等待周转。

`ProductionStorageTests` 增加实际 UpdateStationStorage 稳定直达/回退布局的喷涂进料断言和10000组点数守恒、取空余数、大数乘法测试；冻结旧实现的布局回归改用无喷涂输入，避免把原本错误的inc行为当正确答案。`MegaProliferatorTests` 从物流槽开始调用实际转移helper，再运行真实Harmony补丁下的原版InternalUpdate，核对原版面板公式分别为+25%和+100%。这验证了状态和显示公式，尚未实际渲染Unity面板。


### 稳态增产数学规划（2026-09-30）

稳态增产数学结算：普通配方（原版类型1–5）在均匀喷涂、无品质、主进度每次恰好补回一周期且额外进度有界时，使用64位整数计算主/额外产出和计时余量。产物容量充足时一次求解，受限时二分最大安全周期数，避免规划阶段逐周期循环；保留原版先结算后推进顺序。其他状态仍走原有逐周期规划或真实InternalUpdate，不改变配方、产量、逻辑频率或线程数。原运行时完整状态回放自检继续生效，日志增加“数学合并”累计周期。离线验证通过；实际游戏耗时收益待同存档验证。

实现：`MegaProliferatorBatch.TryStablePlan`。记主阈值T、额外阈值E、当前额外进度e、每次额外步长x。仅当主步长=T、T≤time<2T、0≤e<2E、0≤x≤E、E-1+x≤int.MaxValue时使用公式。对于n次调用：

```text
extra(n) = floor((e + (n - 1) * x) / E)
extraTime(n) = e + n*x - extra(n)*E
主进度保持不变
总产出周期 = n + extra(n)
```

其中n-1来自原版先结算后推进，不能替换为n。输入预算和多产物写入上界仍由原规划器计算；主/额外产物合计不得超过容量。容量受限时对单调总产出做二分，不足两周期则交回真实调用。64位中间数避免乘法溢出，其他计时形状继续原循环；不跨tick或外部输入事件。

测试：`tools/MegaProliferatorTests/StablePlanTests.cs` 以旧逐周期递推作独立参考，小整数穷举及100000组大数边界对照；20000组实际Harmony补丁下的InternalUpdate回放中18117组命中数学路径，完整末态一致。既有40000组旧计时路径、8类型×3产量×3分频、从物流槽输入喷涂到原面板公式的测试通过。自定义配方类型仍不进入此均匀喷涂规划器，不宣称已完成所有生产设施的数学化。

需求刷新回退与生产槽位索引：实机未证明上一轮白名单来源映射缓存有收益，且填充/解锁占比上升，因此撤回该映射缓存，恢复原有掩码枚举；配置格位索引、需求并行及低争用探针继续保留。巨型建筑传送带槽位增加按星球/实体的直接索引，复用原数组；首次创建、扩容、拆除、清空及导入同步更新，超大编号退回字典。仍逐次检查连接，存档字节格式、储物格搬运和生产配方不变。并发创建、扩容、拆除重建、旧档兼容及超界回退测试通过；离线net472查询更快，Unity帧率收益待验证。

发布核验（1.13.7，2026-10-01）：需求刷新恢复到约5.07～5.14ms/tick线程累计；白名单填充/解锁占比回到7.8%；槽位抽样均值约1.03～1.08μs。末两窗FPS 10.3～10.4、UPS 21.1。不是严格A/B，不将总收益归给单项。下一轮储物格布局重复验证和本地空闲站点优化尚未实施。
