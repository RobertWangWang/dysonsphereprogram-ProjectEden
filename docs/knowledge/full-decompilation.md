# 全量反编译与功能导航

## 范围和完成标准

游戏主程序集 `Assembly-CSharp.dll` 已按整程序集导出，而不是只挑选某些类。当前包含 **3,496 个类型、33,376 个方法、34,010 个字段，其中 26,776 个方法有托管方法体**，生成 3,114 份 C# 文件。C# 会把嵌套类型、匿名函数等重建到顶层文件中，所以文件数不等于元数据类型数。

本机完整导出放在 `docs/knowledge/generated/full/<程序集名>/`：

Managed 目录 **95 / 95 个程序集**现已全部完成：合计 19,832 个类型、188,638 个方法、114,510 个字段、14,672 份 C# 文件。其中包含运行库与平台 SDK，游戏主程序集的数量单独列在上面。逐程序集数量、SHA-256 和覆盖核验见 [程序集清单](assembly-inventory.md)。

- `source/`：完整反编译项目、按类型拆分的 C# 及程序集资源。
- `assembly.il`：ILSpy 输出的整程序集 IL，包含 C# 重建时可能折叠的成员。
- `metadata/symbols.json`：全部类型（含嵌套类型）、字段、方法、元数据 token、静态调用引用。
- `metadata/type-*.il`：按元数据 token 命名的逐类型 IL，避免泛型/嵌套类型名中的非法路径字符。
- `manifest.json`：完成标记、DLL/依赖/输出文件哈希、工具版本和数量统计。

完整类型入口见 [全量目录](full-type-catalog.md)。已有专题笔记见 [知识库首页](README.md)。74 类精选缓存仍保留，供既有链接使用；它的数量不代表全量覆盖范围。

## 使用

```powershell
# 完整导出游戏逻辑程序集；已验证的缓存可以复用
python -X utf8 tools/dsp_full_decompile.py

# 包括 Managed 下全部 Unity、.NET 和平台依赖
python -X utf8 tools/dsp_full_decompile.py --all-managed

# 校验每个输出文件及当前游戏/依赖的哈希
python -X utf8 tools/dsp_full_decompile.py --all-managed --check

# 额外交叉比较 ILSpy 的类型/方法数量与 Cecil 元数据，并更新程序集清单
python -X utf8 tools/dsp_verify_full.py

# 生成覆盖全部游戏类型的导航目录
python -X utf8 tools/dsp_full_catalog.py

# 全量字段、方法、调用方检索
python -X utf8 tools/dsp_knowledge.py search DysonSphere::GameTick --full --calls
python -X utf8 tools/dsp_knowledge.py search AddPrebuildDataWithComponents --full --callers
rg --no-ignore -n '你的关键词' docs/knowledge/generated/full/Assembly-CSharp/source
```

工具只读取安装目录；不会加载执行游戏程序集。失败时不写完成标记，再次运行会重建失败的程序集。现有完整输出的有效性由 check 判断；项目导出成功不意味着可以不经修整重新编译成等价游戏。

## 已核对的新增功能入口

| 功能 | 入口与实际职责 | 已核验边界 |
|---|---|---|
| 建造和蓝图 | `BuildTool_*`、`BlueprintData`、`BlueprintBuilding` | [门槛、物品扣除、重叠与序列化](building-blueprints.md) |
| 机甲 | `Mecha.GenerateEnergy`、`MechaForge.GameTick`、`MechaLab.GameTick` | [能源、返壳、手搓与研究单位](mecha.md) |
| 戴森球功率 | `DysonSphere.BeforeGameTick` | 重算闸门、太阳帆/节点/壳体汇总与黑雾扣减，见下文 |
| 戴森球运动与施工 | `DysonSphere.GameTick`、`RocketGameTick`、`DysonSwarm.GameTick` | 主更新入口分离，不能只补一个 GameTick 就认为控制全部过程 |
| 太空黑雾 | `SpaceSector.GameTick`、`EnemyDFHiveSystem` | 全体每 tick 更新与分摊 KeyTick 分开，见下文 |
| 地面黑雾 | `EnemyDFGroundSystem` | 有 Prepare/Anim/Base/Turret/Unit/KeyTick/Post 等独立入口；具体战斗平衡尚未逐分支核验 |
| 发射设施 | `EjectorComponent.InternalUpdate`、`SiloComponent.InternalUpdate` | 入口签名可全量查询，发射限制与实际吞吐需继续读方法体 |

### 戴森球功率与更新

[DysonSphere](generated/full/Assembly-CSharp/source/DysonSphere.cs) 的 `BeforeGameTick` 先清零 energyReqCurrentTick；只有 needRecalculatePower 为真才重算发电。太阳帆数量乘 energyGenPerSail，再累加最多十层的节点贡献与壳体 cellPoint × energyGenPerShell。保存未扣减值后，最终乘 energyDFHivesDebuffCoef。因此改变发电参数时还要核对触发重算；UI 旧值不一定意味着参数改写失败。

`DysonSphere.GameTick` 遍历层更新并上传节点旋转 GPU 缓冲，火箭另有 RocketGameTick。[DysonSwarm.GameTick](generated/full/Assembly-CSharp/source/DysonSwarm.cs) 设置并派发 compute shader，随后按到期队列回收太阳帆、按吸收队列执行 ConstructCp 并触发功率重算。C# 只给出了 GPU 调度入口，不能据此声称已获得 compute shader 的全部实现。

### 太空黑雾调度

[SpaceSector.GameTick](generated/full/Assembly-CSharp/source/SpaceSector.cs) 按 PrepareTick → GameTick → AfterTick 驱动 creationSystem 和 skillSystem，并更新 blackboxSystem。每 tick 对全部恒星下的 hive 链调用 GameTickLogic 和延迟变更处理；DecisionAI/KeyTickLogic 则按照 `(time-1)*恒星槽数/60` 到 `time*恒星槽数/60` 的窗口分摊。不要把两种时钟混用，否则演化、仇恨和战斗执行速度会一起失真。

## 尚不能宣称已经恢复的内容

完整反编译是指现有托管 DLL 中可读取的代码和元数据。原始注释、源代码仓库、完整局部变量命名、构建过程不会因此自动恢复。抽象方法、接口、P/Invoke、Unity InternalCall 原本就没有托管方法体；其声明已经导出，不应伪造 C# 实现填空。

`UnityPlayer.dll`、平台原生插件和游戏资源中的 shader 不包含在托管 C# 反编译结果中；现已另建 [原生与 GPU 导出](native-and-gpu.md)，shader 字节码已导出，原生函数进度单独记录。地图/配方等运行时资产也不能只靠反编译代码推断实际值。全量类型目录按名称分组，人工知识页只保证明确写出的结论；没有把数万方法都标为人工审查完成。
