# 原生属性描述对象与粒子模块属性目录

## 扩大描述对象扫描后的增量

提取脚本现已从三个种子地址扩大到全部节中与初始格式一致的记录：8 字节对齐、前 16 字节为零、随后三个字符串指针与种子一致，且 80 字节范围可从文件完整读取。共识别 **238 个描述对象候选**。这是格式筛选范围的扩展，不证明已经涵盖全部运行时类型。

原有 347 条粒子记录逐条保持不变。扩大后另发现 `181c496f8` 处的 `Debug / 181c42b00 / 0` 三元组，故生成目录现有 **348 条候选具名记录**、19 个连续地址区间。`Debug` 的用途未确认，单独保留为候选，不计为新增粒子属性或模块。其余 235 个格式匹配对象未产生本规则接受的具名三元组。下文 347 条、22 个模块与 18 个区间的统计仍专指原粒子属性子集。

原始引用报告仍仅覆盖三个种子地址及其 `+20` 字段；扩大格式扫描不等于已经追踪全部 238 个对象的代码引用。

后续 [重定位代码指针发现](native-pointer-discovery.md) 又补出 `18043d930`：它按输入字节选择 `True` / `False` 并追加到字符串。静态转换记录将它与 `181c42160` 配对，进一步支持布尔行为解释；这仍不等于取得正式注册名称。

此次沿读取接口返回的三个描述地址继续追踪，直接从固定 UnityPlayer PE 提取了 **347 条具名属性记录**，涉及 **22 个模块名前缀**。每条保存 24 字节原始三元组（名称指针、描述对象指针、数字标识）、属性字符串地址及字符串。查询时校验游戏 SHA-256 和目录文件哈希。

## 描述对象：原始内容与行为解释分开记录

`181c42110`、`181c42160`、`181c42480` 的初始 80 字节均含以下指针字段：`+10` 指向 `[UNREGISTERED]`，`+18` 指向空字符串，`+20` 指向 `undefined`。这是**磁盘初始状态**，不能据此将它们命名为 undefined 类型，也不能声称已获取实际注册后的名称。

| 地址 | 本目录记录数 | 当前可用解释 |
| --- | ---: | --- |
| `181c42110` | 313 | 浮点描述对象的行为解释：对应 scalar、minScalar、range 分量等属性，且此前读取接口的默认类型路径与浮点返回对应 |
| `181c42160` | 34 | 布尔描述对象的行为解释：对应多个 enabled 等开关，且此前类型选择与布尔标志读取路径对应 |
| `181c42480` | 0 | 有其他静态引用及类型选择路径，确切类型仍未确认，不能仅凭 32 位读取就命名为整数类型 |

前两项解释由属性名及读取行为相互支持，仍不是注册后类型名称的直接证据。原始引用报告覆盖三个对象基址及其 `+20` 字段；原 Ghidra 数据库没有记录后者的直接引用，这不排除间接寻址、尚未分析的代码或运行时注册写入。

## 目录覆盖

| 模块名前缀 | 属性记录数 | 模块名前缀 | 属性记录数 |
| --- | ---: | --- | ---: |
| EmissionModule | 54 | ShapeModule | 37 |
| CustomDataModule | 33 | TrailModule | 30 |
| InitialModule | 30 | NoiseModule | 25 |
| VelocityModule | 23 | CollisionModule | 12 |
| ClampVelocityModule | 12 | ColorBySpeedModule | 11 |
| LightsModule | 10 | RotationBySpeedModule | 9 |
| SizeBySpeedModule | 9 | ColorModule | 9 |
| UVModule | 8 | ForceModule | 8 |
| SizeModule | 7 | RotationModule | 7 |
| LifetimeByEmitterSpeedModule | 5 | InheritVelocityModule | 3 |
| ExternalForcesModule | 3 | TriggerModule | 2 |

例如，`LifetimeByEmitterSpeedModule` 的五条记录从 `181a6b940` 开始，每条 24 字节，数字标识为 0..4：`enabled`、`m_Curve.scalar`、`m_Curve.minScalar`、`m_Range.x`、`m_Range.y`。

匹配记录在地址上构成 18 个连续区间。连续区间内可能出现数字标识重新从 0 开始，也存在首条标识为 3 的区间。因此**地址连续不等于同一张完整表**，数字也不能当作全局属性 ID。这只是筛选出的具名记录：其他描述类型、未命名记录、运行时生成属性和完整表边界均未穷举。

## 查询与复现

证据保存于 `generated/native/UnityPlayer.dll/property-catalog/`：`catalog.json` 包含全部记录、初始描述字节和指针引用位置；`references.json` 为六个地址的 Ghidra 引用报告，包含的指令字节再次与 DLL 对比；`manifest.json` 保存完整性哈希。

```powershell
# 按名称子串查询
python -X utf8 tools/dsp_native_property_catalog.py --query LifetimeByEmitterSpeedModule
python -X utf8 tools/dsp_native_property_catalog.py --query EmissionModule

# 从当前固定版本 PE 重建目录
python -X utf8 tools/dsp_native_property_catalog.py
```

需要同时重建引用证据时，用 `DspReaderTableRefs.java` 的首个参数指定报告路径，后续参数依次传入三个基址及其 `+20` 地址；再将报告通过 `--references` 交给目录脚本。目录重建和查询均不运行游戏、不执行属性读写。

本轮新增的是原生功能导航和可核验的元数据，不增加函数反编译完成数量。与 [读取接口及分发表](native-float-readers.md) 配合使用，可继续定位属性分派、类型注册与具体字段实现。
