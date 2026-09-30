# 建造工具与蓝图

基线见 [baseline.json](baseline.json)。本页核验了普通点击建造入口、蓝图重叠闸门和蓝图容器序列化；不代表建造工具所有分支都已审查。

## 原版已核验

`BuildTool_Click` 在更新中调用 `CheckBuildConditions`，把结果交给 `ConfirmOperation`，确认后进入 `CreatePrebuilds`。其中创建新预建体的分支要求 `condition == Ok && coverObjId == 0`，复制位置、旋转、配方、过滤器和 parameters 后，优先消耗手持物品，否则从背包尾部取一件。实际取得一件才调用 `PlanetFactory.AddPrebuildDataWithComponents`；预览的 `objId` 记录为预建体编号的负值。放行条件不会自动生成建筑物品。

蓝图粘贴另有入口：`BuildTool_BlueprintPaste.CreatePrebuilds` 先跳过 `bpgpuiModelId <= 0`，随后才检查 `condition`，这里允许 `Ok` 或 `NotEnoughItem`。不要把点击工具的门槛直接套到蓝图工具。

`ArrangeOverlapBP` 对部分同位置预览同时写 `coverbp`、`bpgpuiModelId = -1` 和 `BlueprintBPOverlap`。仅清除 condition 仍会在 CreatePrebuilds 被跳过。反过来，`CheckBuildConditions` 中相互 coverbp 的预览会跳过对应的间距碰撞判断，因此 coverbp 并不是一个可以随意清空的错误标志。

`BlueprintData.ToBase64String` 的内容由二进制 Export → GZip → Base64 得到，拼入 header 后用 `MD5F.Compute` 生成签名。不要直接用标准 MD5 实现替代 `MD5F` 而不核对算法。Import 读取版本、区域和建筑后，再按临时输入/输出下标恢复对象引用，最后执行 DataRepair。

当前 Export 写 `Version = 2`、`Patch = 1`。Import 接受最多 64 个区域、1,048,576 个建筑；这只是该方法的数据检查范围，不是游戏 UI 支持规模的承诺。Version >= 2 才走当前地基 reformData 的读取分支。

`BlueprintBuilding` 不只有物品 ID 和坐标，还保存连接槽位、输入输出偏移、配方、过滤器、parameters 和 content。复制建筑时遗漏这些数据，会造成“外观正常但功能设置或连接丢失”。

## ProjectEden 对应补丁

- [BuildConditionCheatPatches](../../ProjectEden/src/Patches/Cheat/BuildConditionCheatPatches.cs)：放宽条件后重新汇总结果；覆盖多个工具。
- [BlueprintOverlapPatches](../../ProjectEden/src/Patches/Cheat/BlueprintOverlapPatches.cs)：快照恢复模型编号与条件，保留 coverbp 关联。
- [QualityBuildPatches](../../ProjectEden/src/Patches/Quality/QualityBuildPatches.cs)：品质建造通道，应与实际扣除物品和预建体编号一起核查。

## 查询

```powershell
python -X utf8 tools/dsp_knowledge.py search CreatePrebuilds --full --calls
python -X utf8 tools/dsp_knowledge.py search AddPrebuildDataWithComponents --full --callers
rg --no-ignore -n 'bpgpuiModelId|coverbp|BlueprintBPOverlap' docs/knowledge/generated/BuildTool_BlueprintPaste.cs
```

参考：[点击工具](generated/BuildTool_Click.cs)、[蓝图粘贴](generated/BuildTool_BlueprintPaste.cs)、[蓝图容器](generated/BlueprintData.cs)、[建筑记录](generated/BlueprintBuilding.cs)。拆除退款、升级、地基、无人机施工与品质回收尚需分别核验；上述“建立预建体”不等于实体已经施工完成。


## 点击落地时的强制垃圾回收

当前反编译 `BuildTool_BlueprintPaste.CreatePrebuilds` 在 `NotifyBlueprintUsed` 之后、返回前无条件调用无参 `GC.Collect()`（generated/BuildTool_BlueprintPaste.cs:4355）。它请求回收整个托管堆，不只处理本次蓝图数据，是点击落地时的一个停顿来源；此前约177ms固定开销可能与此有关，但尚无实机对照证明其占比。

`BlueprintPasteGcPatches` 仅将唯一且紧邻末尾返回的调用原地替换为 nop，保留标签和其他指令。不匹配则回退原方法。自动GC继续正常工作，不后台调用GC，也不修改预建体/库存/连接或释放游戏资源的流程。`tools/BlueprintGcTests` 检查实际游戏IL只改一条指令，并完成Harmony编译、替身落地副作用、回收移除、标签和回退验证。不能据此宣称复制框选、预览检查或大批量实体创建的卡顿已解决。


## 400座蓝图的后续实测与分段诊断

2026-09-30日志中，400座蓝图的CheckBuildConditions分别耗时4166ms和10140.4ms，CreatePrebuilds仅1.06/1.08ms；物理重叠查询8/11.35ms，错误消息0.08/0.17ms。说明主要停顿在条件检查未细分的区域。秒建另有最慢100座868.09ms，BuildFinally是该阶段主要成本。取消末尾GC未解决这些停顿。

BlueprintConditionDetailProbe在现有专项开关blueprintprobe.enabled开启时，按后向分支识别59个循环，用最内层区域排他计时。跨区域分支入口及顺序边界插入标记；原指令和分支目标保留，标签移至标记。报告中的I编号为当前Harmony指令序号，不是IL字节偏移，附循环尾部边界字段。只在超过专项尖峰阈值或异常时报告前12项；关闭时不读计时器但保留入口标志检查。异常区域/不匹配形状保留原方法。嵌套调用保存恢复线程上下文，finalizer不吞原异常。它测量包含探针开销，不是纯原版成本。

InstantBuildPatches默认使用4ms/渲染帧软预算，数量上限仍100/批。构建下一座之前判断时间，至少允许当前批首座完成；清理和事件通知照常执行，含收尾总耗时记入同帧账目，同帧多个逻辑tick不能重复花满预算。单座/收尾不可抢占，不承诺帧时<=4ms；扫描全为缺料的预建物尚未改为增量。范围仅本mod付费秒建路径，不限流原版沙盒FastBuild或普通建设机器人。配置instantBuildFrameBudgetMs=0恢复仅数量限制。

验证：tools/BlueprintPerformanceTests包含确定性时间预算测试、80组实际Harmony循环/分支对照、异常与嵌套上下文恢复，并核对实际游戏IL插桩后剔除标记与原指令完全相同。实际游戏方法在独立net472测试进程中连恒等转译器也因Unity ECall限制无法JIT，因此完整方法编译/运行须在游戏验证，不宣称该项离线通过。当前profile保留已开启的蓝图专项探针，不打开系统性能探针。


## 2026-09-30 碰撞体查号热点与跳过检查

最新70座蓝图条件检查2717.921ms中，循环8/11/53排他耗时分别1410.742/648.926/649.915ms（约99.7%）。这些循环处理碰撞体；PlanetPhysics.GetColliderData调用NearColliderLogic.FindColliderId，后者逐次枚举整个colliderObjs并执行Unity对象比较。此前只量OverlapBoxNonAlloc，遗漏了查询后的编号反查成本。

BlueprintColliderLookupPatches只在一次CheckBuildConditions范围内惰性构建碰撞体InstanceID→ColliderObject.id反向表。保持枚举第一项优先，未命中返回0；Unity空/销毁对象走原版。按NearColliderLogic对象分表，源字典替换/数量变化重建，已知增删入口Init/Free/DeleteDeadColliders/UpdatePlayerPosNear/UpdateCursorNear/ActiveBuildPreviewsNear/ActiveEntityBuildCollidersInArea/ActiveEnemyBuildingColliderInArea/ActiveCollidersInArea前后使当前及嵌套作用域索引失效。finalizer释放引用并恢复外层，不跨检查保留缓存。不支持其他mod绕过这些入口在检查期间原地同数量篡改字典/ColliderObject字段。报告[蓝图碰撞索引]给出查询数、建表次数、遍历条目与建表时间。

BlueprintAssemblerCollisionSkip仅在cheats.enabled和noConditionBuild开启，且isAssembler、不属于belt/inserter/multiLevel/addon/miner/tank/storage/lab/splitter时，让CheckBuildConditions唯一hasBuildCollider入口返回false。该类碰撞与覆盖结论此前已由BuildConditionCheatPatches清除；其他检查、传送带连接及采矿参数生成仍执行。不把整个CheckBuildConditions直接return true。

用户要求不按时间分帧：内嵌及当前profile的instantBuildFrameBudgetMs改0，原每批100座上限保留。蓝图专项探针保持开启。新增离线验证2万条碰撞体表/2000查询只建表一次，命中/负查找/重复对象优先顺序/表替换/失效/Unity空对象回退/嵌套清理与碰撞跳过守卫矩阵。实际Unity运行加速仍待日志。


## BuildFinally结算分段探针

2026-09-30 20:49日志：630座蓝图条件检查约45–47ms，碰撞索引190512次查询仅建表一次（约0.6ms）。同期730座实际建造中BuildFinally累计4578.07ms，最慢100座批次967.87ms，说明当前主要剩余成本在实际建造。两版蓝图规模不同，不将其解释为严格性能倍率。

`BuildFinallyProbe` 使用Harmony prefix/finalizer对17个唯一名称方法做作用域计时，范围为BuildFinally及其调用链。包括FlattenTerrain、AddEntityDataWithComponents、AddEntityData、HandleObjectConnChangeWhenBuild、CreateEntityLogicComponents、CreateEntityDisplayComponents、RemovePrebuildWithComponents、OnBeltBuilt、OnInserterBuilt、OnAddonBuilt、OnBuildEntity、OnSinglyBuildEntity、CheckDysonSphereConditionAfterConstruction、PlayerAction_Build.NotifyBuilt、GameHistoryData.MarkItemBuilt和GameScenarioLogic.NotifyOnBuild。显示组件项包括模型、碰撞体及音频，暂未再拆内部。

每个节点用本身总时间减直接子节点时间得到排他时间；最外层返回后才一次合并整份数据。因此各项和等于总量，实体创建其余不重复包含内部逻辑/显示组件。递归BuildFinally共用最外层统计，不把内层总时重复累计。finalizer恢复线程上下文，保持异常传播；异常数量为最外层异常结束次数。关闭专项探针时不创建计时作用域、不读取时间；范围外子方法不计入。按blueprintprobe.reportSeconds（当前5秒）由UIGame更新输出，有建造样本才报，无逐建筑刷屏。统计包括所有经过该入口的建造，不仅蓝图；包含计时、作用域分配、等待和补丁开销，不能与父项相加，也不等于单帧耗时。批次Finish/EndFlattenTerrain在BuildFinally之外，继续看原秒建分段日志。

测试`tools/BuildFinallyProbeTests`链接实际探针，使用实际Harmony运行5000次并发建造及并发取窗口，校验排他和、每项调用数、建造副作用不变；另验提前返回、异常传播、上下文清理、嵌套、关闭和范围外调用。反射验证本机游戏17个方法目标唯一。未在离线进程执行真实Unity建造，最终耗时与兼容性待游戏日志确认。当前profile只沿用蓝图专项enabled=true，系统探针关闭，instantBuildFrameBudgetMs=0不变。


## 秒建批次合并电力显示刷新

21:03日志中多组BuildFinally的94.0–94.4%时间在CreateEntityLogicComponents，其末尾对耗电建筑调用FactoryModel.RefreshPowerConsumers。PowerSystemRenderer.RefreshPowerConsumers扫描全部consumerPool，以及monitor/marker，再SetData上传conn/cover/consumer/gen/exc缓冲。原代码这处没有batchBuild守卫。

BatchPowerConsumerRefresh只替换CreateEntityLogicComponents中唯一的FactoryModel.RefreshPowerConsumers调用为作用域感知包装；入口不匹配保持原样。InstantBuildPatches在开始本mod付费秒建批次后建立线程作用域，匹配当前FactoryModel的请求标脏，创建结束的finally中Dispose统一刷新，再执行Finish和批量事件。scope先恢复外层再真正刷新，异常不滞留上下文或模型引用，Dispose幂等。无请求不刷新，其他模型/其他调用点/普通建造仍即时执行。不会全局拦截刷新或推迟组件、供电连接、物品扣除；原版沙盒FastBuild未接管。只合并这一入口，同批其他来源的刷新可能另行执行。

反编译托管代码中consumerArr等显示缓存由PowerSystemRenderer使用，CreateEntityLogicComponents继续创建使用的是PowerSystem真实组件池；没有发现该创建路径依赖延迟的显示数组。批次收尾在渲染及批量建造回调之前发布。第三方逐座建造回调如果直接读取显示缓存，批次内可能看到旧显示数据；不承诺任意反射Mod的中途读取兼容性。

专项探针开启时报告[批量电力显示刷新]的请求/刷新/省去次数及耗时。合并后真实刷新移出BuildFinally进入批次收尾，必须连同原秒建收尾日志评估，不能仅看BuildFinally缩短。关闭诊断不关闭合并功能。4ms时间预算仍为0，数量上限仍100。

验证tools/BatchPowerRefreshTests链接实际补丁，Harmony模拟250批各100座，共25000次建造仅250次刷新；检查组件即时创建、最终显示结果、单座/空批次/嵌套/其他模型、异常刷新与上下文恢复、重复Dispose、关闭探针和形状回退。Cecil核验实际游戏唯一调用锚点。BuildFinallyProbe原5000次并发测试继续通过。完整Unity建造速度待实机日志，不将模拟次数降幅直接等同FPS提升。


## 1.13.6 实机验证（2026-09-30 21:12 日志）

64个批次：6312次刷新请求合并为64次，省去6248次（约99%），收尾实际刷新累计200.741ms；刷新和BuildFinally诊断异常数均为0。后段100座批次均值164.84–167.67ms、最慢167.6–175.7ms，前段混合批次均值324.48–348.01ms，不能将后段结果推广到所有建筑。后段BuildFinally每座1.559–1.645ms，逻辑组件创建仍占剩余耗时的大头。1890预览检查387.71/424.91ms，与此前630预览不是同规模对照。时间预算仍0，系统探针关闭，测试profile蓝图专项探针开启。日志另有地形数据80802字节与precision400所需321602字节不匹配、重建数据的错误；这是另一个未解决问题。
