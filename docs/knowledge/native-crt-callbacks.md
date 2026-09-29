# CRT 回调入口补充发现

## 退出回调的外部目标与警告核验

对第二批 195 个退出回调的全部 1,970 条导出指令，以 Capstone 独立核验指令边界并检查 CALL/JMP。找到 **55 条外部边**：10 条到已索引入口的直接尾跳转、33 条到 `KERNEL32.dll!CloseHandle`、12 条到 `KERNEL32.dll!DeleteCriticalSection`。未解析间接边和未索引直接目标均为 0。此结论仅覆盖这批回调，不表示全模块的函数发现已经结束。

45 处无法恢复跳转表的警告，逐一与实际跳转指令及 PE 导入槽匹配，均为上述两个 API 的尾调用，对应的 45 次“将间接跳转当作调用”警告也有了静态解释。原 C 和警告仍保留；另 1 次全局符号重叠警告不在此次核验范围。

所有 45 个包装函数又经过完整连续函数体的机器字节模板核验：

| 数量 | 导入函数 | RCX 参数 | 指令行为 |
| --- | --- | --- | --- |
| 33 | `CloseHandle`，槽位 `1818907d0` | 从对应全局地址读取的 64 位句柄值 | 先另读一个全局 DWORD 到 EAX，再装载 RCX 并尾跳转；20 字节 |
| 12 | `DeleteCriticalSection`，槽位 `181890858` | 对应全局临界区对象的地址 | LEA 装载 RCX 后尾跳转；14 字节 |

这些包装函数自身无条件分支、不调整 RSP。不能从中推导传入句柄一定有效、临界区一定已初始化或系统 API 一定成功；额外的 EAX 读取也保留在证据中，不擅自删除。没有调用游戏或系统清理 API。

证据保存于 `generated/native/UnityPlayer.dll/exit-edges/`，包括每条边、每个包装函数的参数地址、来源文件哈希及空的缺失入口清单。查询工具会验证清单再显示 API 与参数说明。复现及查询：

```powershell
python -X utf8 tools/dsp_native_crt_edges.py --exit-callbacks
python -X utf8 tools/dsp_native_query.py 18187e1f0 --module UnityPlayer.dll --show
python -X utf8 tools/dsp_native_query.py 18187eeb0 --module UnityPlayer.dll --show
```

## atexit 清理回调的第二批补充

在原先已索引的 CRT 初始化函数中，进一步发现 195 个未索引的退出回调参数。它们通过 `atexit` 注册，属于数据参数引用，不在直接 CALL/JMP 目标审计的覆盖范围。`dsp_native_exit_discovery.py` 先由 C 参数引用提名，再独立核验对应的 `LEA RCX,[target]` 与到 `181813ca4`（`atexit`）的直接 CALL/JMP；反向局部检查遇到控制转移或 RCX 写入就停止。

这 **195 个入口全部补建并导出**，1,970 条指令共 8,832 字节，逐字节与函数体覆盖核验通过。46 份 C 带警告，记录 90 次其他警告和 1 次全局符号重叠警告，不能当作语义已完成。连同第一批，累计新增入口为 **2,905 个**，原索引及 Ghidra 项目仍不修改。

发现证据在 `exit-discovery/`，导出与验证清单在 `exit-callbacks/`。查询已经合并两批补充入口：

```powershell
python -X utf8 tools/dsp_native_query.py 18187c780 --module UnityPlayer.dll --show
python -X utf8 tools/dsp_native_crt_callbacks.py --exit-callbacks
```

复现 Ghidra 阶段仍用 `DspCrtCallbacks.java`，前两个参数为退出入口清单和输出目录，第三参数为 `exit-discovery/report.json`。脚本逐项复核原程序中的参数装载与 atexit 控制转移地址后才接受入口，生成 `exit_callback_*` 名称并回滚项目。登记证据证明有这些注册路径，不证明初始化一定成功、注册返回值或实际退出清理执行顺序。

新导出的第一批 2,710 个 CRT 回调中的可识别 atexit C 参数另有 1,014 个不同目标，初步检查均已在原索引中；本轮新增来自原有初始化函数。这个 C 文本筛查不替代对全程序所有回调注册机制的穷举。

UnityPlayer 的 CRT 初始化表包含 3,018 个唯一非空目标。原 Ghidra 索引中仅有 308 个精确对应入口；其余 **2,710 个**现已补建并独立导出为 C 与汇编。不要将原来“已发现函数均已导出”的统计解释为“模块所有函数均已发现”。

本次结果：2,710 份 C 全部生成，23,691 条指令共 137,473 字节；每条指令的机器字节与固定游戏 DLL 对比通过，每个导出函数体的地址集合被汇编完整覆盖。集合恰好等于之前保存的缺失清单，未与原索引入口重复。

其中 **1,499 份带警告**，包括 1,498 次重叠全局符号警告及 2 次其他警告。同一份 C 可有多个警告。成功导出不代表类型、边界或运行语义已完全恢复。本次不修改原 `functions.json` 或原项目，所有 Ghidra 新建操作在只读运行中回滚。

两次其他警告集中在 `180045c64`：`180045c6b` 的间接跳转未恢复，被当作调用处理。这一入口需进一步解析真实目标，当前 C 不作为控制流已恢复的证明。

### 后续核验：导入尾调用与全组控制流边

上述入口的 14 字节实际指令是 `LEA RCX,[181cbdd80]`，然后 `JMP qword ptr [1818904e0]`。从固定 PE 的导入目录独立解析，槽位 `1818904e0` 对应 `KERNEL32.dll!InitializeSListHead`。这是向系统导入的尾调用包装，没有额外调整 RSP；不能当作多目标跳转表。原 C 已表达该 API 调用，本次保留原警告，附上导入槽身份的证据，不通过删警告伪造新 C 质量。

`tools/dsp_native_crt_edges.py` 独立解码新增回调的每条导出指令，确认边界，检查所有 CALL/JMP。落在当前函数体内的边单独排除；余下 **1,285 条外部边**都能对应精确已索引入口或 PE 导入槽。未解析间接边为 0，未索引直接目标为 0。因此这批回调的直接外部目标没有再产生新入口队列，不能将其推广为其他代码区域也没有缺口。

证据在 `crt-edges/`，包括完整边列表、来源清单哈希、导入身份和空的候选目标清单。导入槽是加载器应解析的符号身份，并未验证真实进程中的 DLL 解析结果、动态钩子或 API 执行效果。两个旧警告的控制流疑点在此静态范围内已有解释；全局符号/类型警告仍待继续处理。

新增材料位于 `generated/native/UnityPlayer.dll/crt-callbacks/`：

- `functions/*.c`、`functions/*.asm`：新增入口的候选 C 与完整函数体汇编。
- `report.json`：地址、推断签名、函数体范围、警告及回滚状态。
- `verification.json`：入口集合、逐字节/范围核验统计及输入清单哈希。
- `manifest.json`：全部输出哈希；查询工具仅在核验清单后合并补充入口。

查询示例：

```powershell
python -X utf8 tools/dsp_native_query.py 180042420 --module UnityPlayer.dll --show
python -X utf8 tools/dsp_native_query.py crt_callback_1800426e0 --module UnityPlayer.dll --show
```

这两个入口分别初始化辐照度计算的前三分量掩码和单精度 1/3 向量；其实际指令模拟证据另见 [FMA4 分析](native-fma4.md)。其余新增回调需要继续按用途分类及检查警告。

复现：在固定 Unity Ghidra 项目上以 `-readOnly -noanalysis` 运行 `DspCrtCallbacks.java`，参数为 `irradiance-globals/missing-callbacks.txt` 和补充输出目录；该脚本确认每个入口来自原始 CRT 表。然后执行 `python -X utf8 tools/dsp_native_crt_callbacks.py` 核验并生成清单。`GhidraScript.createFunction` 负责从已解码指令建函数体，不以相邻表项距离强行截断。

当前补齐的是这张表的已列出入口。未穷举动态注册回调、其他函数指针表、未引用代码或新导出中调用的潜在未识别目标；因此全量发现和语义恢复目标仍未完成。
