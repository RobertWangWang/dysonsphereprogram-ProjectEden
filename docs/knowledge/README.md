# DSP 代码知识库

本库基于本机 `Assembly-CSharp.dll` 的实际反编译结果，记录 ProjectEden 开发最常用的游戏机制。现覆盖 **74 个类型、2,121 个方法、2,520 个字段**，详见 [类型目录](type-catalog.md)。包括研究站、生产结算、物流、能量枢纽、采矿、传送带配套设备、发电、配方/科技、存档、星系/行星与矿脉生成；不等于全部游戏逻辑均已人工核验。

## 按问题查

**完整游戏程序集已导出：3,496 个类型、33,376 个方法、34,010 个字段。** 上述 74 类是保留的精选缓存。全量使用方法见 [完整反编译](full-decompilation.md)，所有类型见 [全量类型与功能入口](full-type-catalog.md)。

| 要解决的问题 | 阅读入口 | 原版入口 |
|---|---|---|
| 宇宙矩阵缺料、科研矩阵数量不对、堆叠研究站 | [研究站](labs.md) | `LabComponent`、`PlanetFactory.InsertInto` |
| 改速度却不增产、产物堵塞时吞料、增产计时 | [生产结算](production.md) | `AssemblerComponent.InternalUpdate` |
| 分馏回流、集装吞吐、成功率与单次处理上限 | [分馏](fractionation.md) | `FractionatorComponent` |
| 反物质产能、分频与黑洞加成 | [生产结算](production.md) | 原版结算＋本 mod `MegaThrottle` |
| 自动取料、需求/供应、跨星球线程污染 | [物流与调度](logistics.md) | `PlanetTransport.GameTick`、`StationComponent` |
| 蓄能柜充电慢、旧存档功率不刷新 | [能量枢纽](power-exchangers.md) | `PowerExchangerComponent` |
| 采矿产能、矿量消耗、矿石直接变锭 | [采矿](mining.md) | `MinerComponent`、`VeinData` |
| 集装截断、增产溢出、货物存档 | [传送带与货物](cargo-belts.md) | `Cargo`、`CargoContainer`、`CargoPath` |
| 分拣器部分投料、集装点数丢失、喷涂与分流 | [传送带配套设备](belt-devices.md) | `InserterComponent`、`PilerComponent`、`CargoTraffic` |
| 黑洞行星数量、半径、加载和扫描线程 | [星系与行星](galaxy-planets.md) | `UniverseGen`、`StarGen`、`PlanetModelingManager` |
| 新矿越界、种子漂移、某些星球不出矿 | [矿脉生成](vein-generation.md) | `PlanetAlgorithm` 及其派生类 |
| 蓝图只建出一座、建造放行但不落地、参数丢失 | [建造与蓝图](building-blueprints.md) | `BuildTool_*`、`BlueprintData` |
| 机甲燃料返壳、手搓限速、研究点数 | [机甲](mecha.md) | `Mecha`、`MechaForge`、`MechaLab` |
| 戴森球、黑雾、战斗、UI 及其他游戏功能 | [全量导航](full-decompilation.md) | 完整 C# / IL、全类型符号索引 |
| 原生 DLL、shader、GPU 线程组与缓冲布局 | [原生与 GPU](native-and-gpu.md) | Ghidra、DXBC、[Shader 目录](shader-catalog.md) |
| CRT 初始化表补充发现的 2,710 个入口 | [CRT 回调导出](native-crt-callbacks.md) | 新增 C/汇编、范围与字节核验、警告清单 |
| 原生函数体是否遗漏分支、展开链分段与跳转表缺口 | [Unity 代码覆盖审计](native-pdata-discovery.md) | 93,878 条运行时记录、283 个待分析区间 |
| 原生属性读取值错误、浮点返回与虚函数尾调用 | [浮点读取函数](native-float-readers.md) | 三个 float ABI 修复、标志/数组布局与 12,792 组验证 |
| 粒子模块的原生属性名、描述对象与数字标识 | [原生属性目录](native-property-catalog.md) | 347 条具名记录、22 个模块前缀、原始三元组与字符串 |
| 从静态代码指针补齐原生函数入口 | [重定位指针发现](native-pointer-discovery.md) | 新增 4,225 份 C/汇编、跳过已有函数内部地址、完整性核验 |
| 查询 32/64 位 SDK、启动器和 Mono 辅助模块的遗漏入口 | [全模块入口补充](native-all-pointer-discovery.md) | 12 模块审计、新增 15,209 份 C、1,356 个表地址证据、剩余失败分类 |
| 原生补充函数体是否提前结束、直接目标遗漏和数据误报 | [补充入口流审计](native-supplement-flow.md) | 三轮新增 111 份 C、隔离八个表数据入口；分类跳过函数补全并通过 131,200 组测试 |
| 原生复制、双字节数值解析和命令分派的缺失代码 | [三处截断函数恢复](native-continuation-bodies.md) | 631 字节、271 条指令补全；167,897 组初始测试，另有 49,933 组真实同步/清零指令联调 |
| rail_api 的 SSL 初始化 C 提前结束、重复 case 遗漏 | [SSL 分派恢复](native-rail-ssl-switch.md) | 三个历史失败标签定位父函数、2,084 组分派验证、保留原始及修正 C |
| 数据地址没有交叉引用、两级跳转表索引与边界 | [重定位操作数引用](native-relocation-operands.md) | 找回 210 项引用、208 处统一有界分派、9,996 组模拟及两处专门验证 |
| C 展开范围超出导出函数体、扫描函数的分支缺口 | [扫描函数体修复](native-scanner-body.md) | 43→403 字节、160 条指令、完整静态图与 520 组分派验证 |
| 扫描 C 的 extraout 指针与拼接返回值是否可信 | [双字节分类器](native-scanner-classifier.md) | 65,536 个组合、保留 ECX/EDX、调用推断问题与内联实验限制 |
| 查看去除错误调用推断的扫描器参考实现 | [扫描器整段对照](native-scanner-reference.md) | 人工表＋两张静态原表共 146,190 组对照，分组记录末尾读取 |
| OpenGL 动态符号加载和扩展能力标志 | [WGL 初始化](native-wgl-initialization.md) | 6 个请求、4 个保存槽、11 个标志、4,160 组验证 |
| 发电效率、射线接收与光子限产 | [发电与射线接收](generators-rays.md) | `PowerGeneratorComponent`、`PowerSystem` |
| 改配方未生效、科技与存档版本 | [配方与存档](prototypes-saves.md) | `RecipeProto`、`GameHistoryData`、`GameSave` |
| 查询原版物品、配方、科技、星球主题实际数值 | [25 张原型表](prototype-catalog.md) | resources.assets＋程序集序列化字段 |
| 查询场景组件、UI 配置、模型参数与序列化字段 | [组件目录](component-catalog.md) | 43,998 个 MonoBehaviour 实例、731 个程序集＋类型组合 |
| 游戏升级后怎样重新核验 | [核验与维护](verification.md) | 哈希、Cecil IL、现有离线检查 |

## 一分钟查询

在仓库根目录执行（Python 3.11+）：

```powershell
# 首次使用或游戏更新后，重新生成本机参考
python -X utf8 tools/dsp_knowledge.py refresh

# 更新精选类型统计；refresh 可复用有效 C# 缓存，--force 强制重建
python -X utf8 tools/dsp_knowledge.py catalog

# 搜索全部游戏类型（先按完整反编译说明生成 full 缓存）
python -X utf8 tools/dsp_knowledge.py search GameTick --full --callers

# 确认游戏、生成文件与人工笔记基线一致
python -X utf8 tools/dsp_knowledge.py check

# 查询字段/完整方法签名，避免漏掉重载
python -X utf8 tools/dsp_knowledge.py search matrixServed
python -X utf8 tools/dsp_knowledge.py search PlanetFactory::InsertInto

# 已导出类型内的调用方，以及命中方法引用的方法
python -X utf8 tools/dsp_knowledge.py search InternalUpdateAssemble --callers
python -X utf8 tools/dsp_knowledge.py search InputUpdate --calls

# 原生函数名或地址查询；自动使用已选择的新分析与补充恢复
python -X utf8 tools/dsp_native_query.py mono_type_custom_modifier_count --module mono-2.0-bdwgc.dll --limit 1 --show

# 已逐字节匹配上游汇编的 Unity VP8 函数；详情见 native-edge-cases.md
python -X utf8 tools/dsp_native_query.py vp8_loop_filter_bv_y_sse2 --module UnityPlayer.dll

# 两级跳转表修正后的 C；保留原始结果与修正证据
python -X utf8 tools/dsp_native_query.py 181255510 --module UnityPlayer.dll --show

# 六表函数的第六项定点修正，附正常入口域证明
python -X utf8 tools/dsp_native_query.py 18177f9f0 --module UnityPlayer.dll --show

# OpenSSL SHA-512 XOP：规范化指令匹配的上游源码及完整反汇编
python -X utf8 tools/dsp_native_query.py sha512_block_data_order_xop --module rail_api64.dll

# 32 位 ChaCha20：标量/SSSE3/XOP 全范围重定位后逐字节匹配
python -X utf8 tools/dsp_native_query.py ChaCha20_xop --module rail_api.dll --show

# Unity FMA4 完整反汇编补充：保留原 C 警告，详见 native-fma4.md
python -X utf8 tools/dsp_native_query.py 1813b1070 --module UnityPlayer.dll --assembly --show

# 含显式未知融合运算的实验 C 候选，不能作为完整浮点语义恢复结果
python -X utf8 tools/dsp_native_query.py 1813b1070 --module UnityPlayer.dll --fma4-candidate --show

# 查询代码正文及 IL 偏移；generated 被 gitignore 忽略，所以 rg 要显式加 --no-ignore
rg --no-ignore -n 'matrixServed|3600' docs/knowledge/generated/LabComponent.cs
rg --no-ignore -n 'InputUpdate|IL_0042' docs/knowledge/generated/PowerExchangerComponent.il
```

`--calls` 是静态方法引用列表，包含方法地址引用，不等于运行时必经调用。`--callers` 只搜索当前导出的类型，不展开反射或虚方法实际派发；派生类 override 可能只通过基类签名被调用，不能用「零命中」证明全游戏无人调用。

## 文件与来源

- 本目录 Markdown：人工整理的解释、排错步骤和本 mod 对应改动，适合长期版本管理。
- [baseline.json](baseline.json)：人工结论对应的程序集哈希、MVID 和核验日期。
- `generated/*.cs`：ILSpy 生成的类型级 C#，保留完整上下文，供本机检索。
- `generated/*.il`：Cecil 导出的签名、元数据 token、局部变量、IL 指令和异常区入口。它是诊断清单，不是可重新汇编的完整 IL 源工程。
- `generated/symbols.json`：字段类型、常量、完整方法签名和静态方法引用。
- `generated/manifest.json`：完成标记及全部导出文件哈希；导出失败不会留下有效完成标记。

生成内容已加入 `.gitignore`，不进入 Git 或 mod 安装包；换机器运行 `refresh` 即可重建。导出脚本只读游戏程序集，不加载执行游戏代码，不修改游戏安装目录。

路径默认取根目录 `DefaultPath.props`，也支持环境变量 `PROJECTEDEN_MANAGED` / `PROJECTEDEN_BEPINEX`，或 `refresh`、`check` 的 `--managed` / `--bepinex` 参数。依赖已安装的 `ilspycmd` 和 BepInEx 的 `Mono.Cecil.dll`。

## 阅读约定

各页分开列出「原版已核验」和「ProjectEden 改动」。磁盘原版反编译结果不包含 preloader 加宽字段、品质字段或 Harmony 修改。原版 `AssemblyVersion=0.0.0.0` 不能用来判断游戏版本；本库以文件哈希定位。

配方真实数值、LDBTool 注册后的 proto、用户 JSON 覆盖、当前科技和供电状态，需要运行时读取。不能仅凭 DLL 或旧文档宣称某个存档的实测速率。
