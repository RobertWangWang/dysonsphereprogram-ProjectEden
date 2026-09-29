# 全原生模块重定位入口补充

本轮把 Unity 的代码指针发现扩展到清单中的全部 12 个 PE 模块。针对 PE32 的 `HIGHLOW` 与 PE32+ 的 `DIR64` 重定位，读取原始指针，筛选落在已初始化可执行区间中的目标，再与已选分析索引、此前补充入口去重。游戏文件 SHA-256、原索引哈希和导出文件清单均已核验。

这是对固定二进制的入口候选发现。重定位可以指向内部标签、跳转表分支或共享代码；不能把每个候选都当作独立源码函数，也不能仅凭生成 C 就认定语义正确。

## 发现范围

| 模块 | 指向代码的槽位 | 不同目标 | 原索引及此前补充未收录的候选 |
| --- | ---: | ---: | ---: |
| DSPGAME.exe | 46 | 37 | 4 |
| Microsoft_Xbox_Services_141_GDK_C_Thunks.dll | 1,816 | 1,003 | 0 |
| rail_api.dll（32 位） | 102,279 | 77,652 | 25,483 |
| rail_api64.dll | 39,324 | 20,046 | 312 |
| rail_wrapper.dll（32 位） | 1,184 | 486 | 122 |
| rail_wrapper64.dll | 831 | 324 | 5 |
| steam_api64.dll | 96 | 75 | 7 |
| XGamingRuntimeThunks.dll | 2 | 2 | 0 |
| mono-2.0-bdwgc.dll | 999 | 933 | 0 |
| MonoPosixHelper.dll | 85 | 71 | 12 |
| UnityPlayer.dll | 40,617 | 23,072 | 1 |
| winhttp.dll（mod 加载器） | 0 | 0 | 0 |

共 25,946 个候选。Unity 的唯一剩余地址 `1814b5e50` 已在此前检查中确认为现有函数 `181493160` 的内部地址，本轮不重复建函数。其余 25,945 个候选交给只读 Ghidra 逐项检查。`rail_api.dll` 和 `rail_wrapper.dll` 虽位于 `x86_64` 目录，实际 PE 格式为 32 位，指针宽度由文件头确定。

## 导出与核验

| 模块 | 新增 C | 跳过已有函数体内地址 | 建函数失败 | C 导出失败 |
| --- | ---: | ---: | ---: | ---: |
| DSPGAME.exe | 4 | 0 | 0 | 0 |
| rail_api.dll | 14,776 | 9,142 | 1,561 | 4 |
| rail_api64.dll | 312 | 0 | 0 | 0 |
| rail_wrapper.dll | 92 | 25 | 5 | 0 |
| rail_wrapper64.dll | 5 | 0 | 0 | 0 |
| steam_api64.dll | 7 | 0 | 0 | 0 |
| MonoPosixHelper.dll | 12 | 0 | 0 | 0 |
| 合计 | **15,208** | **9,167** | **1,566** | **4** |

15,212 个导出函数体的 **645,346 字节、175,855 条指令**已与原游戏文件逐条核对，检查入口、范围、指令覆盖、重叠与计数。这包括 4 个未导出 C 的函数体；共享尾部可能重复计入不同函数体，因此字节数不是全局去重覆盖率。633 个函数含反编译警告，原文保留。

此前 Unity 已补充 7,136 个入口，本轮新增 15,208 个，合计 **22,344 个补充入口的 C 输出**。这不是全游戏独立函数总数，也不是全功能恢复完成标记。

4 个 C 失败均来自 `rail_api.dll`：`1052489e`、`105248a6`、`105248ae` 报 `Cannot properly adjust input varnodes`；`1091f3b0` 为反编译超时。前三个地址的片段涉及共享后续代码，后续需要检查上游跳转表、入口寄存器和栈上下文，不能通过强行声明参数掩盖错误。1,566 个建函数失败候选也保留地址，未列入成功数量。

## 文件与复现

### 后续失败审计与重试

保留上表作为原批处理结果，后续只读审计覆盖全部 1,570 个失败项。原项目中 1,566 个建函数失败目标全部为数据单元；对引用指令再次独立解码并与真实机器字节核对，得到：

| 分类 | 数量 | 证据 |
| --- | ---: | --- |
| 索引跳转表地址 | 1,253 | 原始 `JMP [index*4 + address]` 的内存位移直接指向候选 |
| 字节索引表地址 | 103 | 原始 `MOVZX` 的单字节内存操作数指向候选 |
| 尚无独立解码引用的数据项 | 210 | 原 Ghidra 数据定义及 32 字节原文件快照；用途仍待追踪 |
| 已有指令但 C 失败 | 4 | 与原失败集合一致，继续单独处理 |

这些表地址位于可执行节，初筛因此纳入候选。已确认的 1,356 个表地址不应强行建立函数；这次核验不确定表长、索引边界或全部分支行为。其余 210 项在后续通过重定位操作数找回 195 项实际表引用，其中 193 张表恢复边界和完整映射，15 处原项目尚未解码，详见 [重定位操作数引用](native-relocation-operands.md)。原始分类保留为历史输入。

对 `1091f3b0` 将单次反编译时限从 30 秒调整为 180 秒后，已成功输出 **无警告 C**。其 **5,805 字节、1,650 条指令**与历史汇编文件完全一致，并重新逐字节核验，没有通过修改代码或函数体获得成功。原失败记录保留，新输出位于 `relocation-retry/`，统一查询标记为 `relocation-retry-decompiled`。C 表现为多个全局状态检查与间接清理调用，具体对象身份及外部方法行为仍需追踪。

本轮系列累计新增 C 因此为 **15,209**，连同 Unity 既有补充，共 **22,345**。历史 1,570 项经重试剩余 1,569 项，其中 1,356 项已有表数据证据，210 项数据用途未定。另 3 项随后确认为同一父函数的跳转表标签，父函数已补全，见 [SSL 分派恢复](native-rail-ssl-switch.md)。历史失败原文保留，不把历史失败总数解释为缺失函数数。

原始 Ghidra 审计在 `relocation-callbacks/failure-audit.json`，独立分类与依赖哈希在 `relocation-failure-triage/`。后续命令：

```powershell
# 需要安装 capstone 的 Python 环境
python -X utf8 tools/dsp_native_relocation_failures.py
python -X utf8 tools/dsp_native_crt_callbacks.py --relocation-module rail_api.dll --retry
python -X utf8 tools/dsp_native_relocation_summary.py
python -X utf8 tools/dsp_native_query.py 1091f3b0 --module rail_api.dll --limit 1 --show
```

`DspAuditRelocationFailures.java` 参数为旧导出 `report.json` 和审计 JSON 输出路径；在原项目使用 `-readOnly -noanalysis`。重试仍用 `DspCrtCallbacks.java`，三个原参数后追加超时秒数，输入列表仅含待重试地址；不覆盖历史批量导出。

### 原始批次文件

每个已选模块目录下：

- `relocation-audit/`：原始槽位、候选目标、输入列表、来源和哈希。
- `relocation-callbacks/`：逐项状态、成功的 C、可导出的完整汇编、逐字节核验报告和文件哈希。失败记录不会被成功清单覆盖。

全局 `generated/native/relocation-audit-summary.json` 保存发现统计；`relocation-export-summary.json` 保存已核验导出统计和 **1,570 个未解决项**。Unity 那一个已知内部地址保留为历史发现输入，不计入这 1,570 项。

```powershell
# 从当前固定版本的模块和已选分析重新发现候选
python -X utf8 tools/dsp_native_all_pointer_discovery.py

# 对已完成的模块导出执行原始字节/函数体核验（七个模块分别运行）
python -X utf8 tools/dsp_native_crt_callbacks.py --relocation-module rail_api.dll

# 核验全部发现/导出清单并汇总失败；不重新执行反编译
python -X utf8 tools/dsp_native_relocation_summary.py

# 查询已接入统一索引的新增结果，保留 C 中的警告
python -X utf8 tools/dsp_native_query.py 10002a80 --module rail_api.dll --limit 1 --show
python -X utf8 tools/dsp_native_query.py 18007b310 --module rail_api64.dll --limit 1 --show
```

首次导出需对对应 Ghidra 项目执行 `DspCrtCallbacks.java`，依次传入该模块的 `relocation-audit/missing-entries.txt`、`relocation-callbacks` 输出目录、`relocation-audit/report.json`。使用 `-readOnly -noanalysis`；脚本校验 SHA、指针宽度及槽值，并回滚临时建函数事务。发现脚本刻意保留导出前的候选输入，不将新生成的 `relocation-callbacks` 加入去重集合，便于重放和核验。

目前仍未覆盖所有相对地址表、动态注册与间接调用目标，也未逐一证明 C 的调用约定和行为。后续优先处理未解决项与带警告输出，再扩展发现范围。
