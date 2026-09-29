# PE 重定位代码指针补充发现

## 新增函数的外部转移审计与后续入口

对本批 4,225 个函数的全部 28,015 条指令重新独立解码并核对原始字节，排除落在当前函数体内的直接边后，得到：

| 转移类型 | 数量 |
| --- | ---: |
| 直接调用/跳转（含条件跳转） | 1,427 |
| 寄存器或非 RIP 内存间接转移 | 574 |
| 已由 PE 导入目录识别的槽位 | 9 |
| RIP 相对全局函数指针 | 1 |

唯一未在原索引与当时补充索引中的直接目标为 `180c3ed10`，由 `180c40b5f` 的 `E9 ACE1FFFF` 跳转到达。已从真实分支字节再次核验目标并补建函数，导出 **112 字节、34 条指令**，逐字节/函数体核验通过，C 无警告。候选 C 表达布尔优先返回 1.0、分母为零返回 0.0、否则将无符号计数转换为浮点做比例并限制上界的行为；未对此片段执行浮点边界模拟，不将 C 的数学表达当作精确舍入证明。累计补充入口增至 **7,136 个**。

该项证据在 `direct-callbacks/`。`pointer-edges/missing-entries.txt` 保留原发现时的一项候选，供复现导出，不因为已修复而删去其历史输入。

### 导入、全局加载槽与跨函数警告

9 个导入槽分别对应 `EnterCriticalSection`、`LeaveCriticalSection`（两处）、`GetDoubleClickTime`、`TlsGetValue`（两处）、`GetModuleHandleW`、`OutputDebugStringA` 和 `CryptHashData`。仅说明固定 PE 的符号身份，不证明实际加载地址和调用成功。

原 C 共出现 568 条无法恢复跳转表警告：552 条对应当前函数体中的非 RIP 间接转移，9 条对应上述导入槽，1 条对应全局槽 `181cdc170`，6 条位于当前导出函数体之外。后者逐一通过只读 Ghidra 核对归属及原始跳转指令，并匹配当前函数直接转移至所属函数入口的边：

| 当前函数 | 警告地址 | 原项目中的所属函数 |
| --- | --- | --- |
| `1809bb490` | `1809e3877` | `1809e3810` |
| `1809bb500` | `1809e38e7` | `1809e3880` |
| `1809bb850` | `1809e3877` | `1809e3810` |
| `180fb7250` | `180faff6b`、`180faff8a` | `180fafee0` |
| `181495580` | `1814cf319` | `1814cf250` |

因此，C 中出现这些地址不能直接解释为当前函数的汇编导出丢失；它们来自另一函数的路径。归属核验没有解决其动态间接目标，警告继续保留。

全局槽 `181cdc170` 在函数 `1810eb670` 的 `1810eb72c` 写入。原始 `1810eb718..1810eb733` 指令序列、字符串和 PE 导入目录共同确认：RCX 指向 `wglSwapIntervalEXT`，随后通过 `OPENGL32.dll!wglGetProcAddress` 获取返回值并保存到该槽。`1809bc9b0` 最后经该槽尾调用。这是已找到的初始化路径，不证明加载成功、路径必达或槽值后来没有改写；未调用 OpenGL 或运行游戏。

以上审计在 `pointer-edges/` 保存全边列表、警告映射、引用报告和清单，查询工具会验证证据后展示说明。复现：

```powershell
python -X utf8 tools/dsp_native_pointer_edges.py
python -X utf8 tools/dsp_native_crt_callbacks.py --direct-callbacks
python -X utf8 tools/dsp_native_query.py 1809bc9b0 --module UnityPlayer.dll
python -X utf8 tools/dsp_native_query.py 180c3ed10 --module UnityPlayer.dll --show
```

首次补建直接目标时，仍以只读 Ghidra 执行 `DspCrtCallbacks.java`，参数为 `pointer-edges/missing-entries.txt`、`direct-callbacks` 输出目录和 `pointer-edges/report.json`。本模式仅接受原始 5 字节 E8/E9 直接转移证据，不用猜测地址填补入口。

固定 UnityPlayer 的 PE 重定位目录位于 `181e78000`，长度 122,704 字节。逐块验证目录长度、记录类型及重复槽位，识别 **59,661 条 DIR64 重定位**和 167 条对齐用 ABSOLUTE 项。读取 DIR64 槽位的原始 64 位值，筛选指向可执行节已初始化范围 `[180001000,18188fa83)` 的指针。

结果为 **40,617 个代码指针槽、23,072 个不同目标**。扣除原索引和此前三批补充索引后，4,226 个目标尚无精确入口。重定位指向代码不自动证明是独立函数，因此进一步交由原 Ghidra 项目检查函数体归属。

## 导出结果

- **4,225 个入口成功补建并导出 C/汇编**；原项目中均无同址函数。
- `1814b5e50` 属于已有函数 `181493160` 的函数体，跳过，未强行拆分。
- 成功导出的函数体合计 **111,929 字节、28,015 条指令**，逐条字节与原 DLL 一致，每个导出函数体的地址集合被汇编完整覆盖。这是各函数体计数之和，共享尾声可能重复覆盖，不是全局去重字节数。
- **609 份 C 带警告**：45 次全局符号重叠、8 次不可达块、1,136 次其他警告；一份函数可包含多次警告。导出成功不代表已恢复类型或完整运行语义。
- 加上此前 2,910 个入口，累计补充入口为 **7,135 个**，新批次与既有补充入口不重复。原 `functions.json` 不修改；Ghidra 创建操作整体回滚。

代码指针来源包括静态接口表、回调表等，目录统一名称 `pointer-callbacks` 并不证明每项一定是运行时回调。此扫描不覆盖相对指针、代码即时数未列重定位的地址、动态生成表或未引用代码，不能据此宣称全模块发现完成。

## 工具及证据

`generated/native/UnityPlayer.dll/pointer-discovery/` 保存重定位目录统计、候选槽/目标、索引依赖哈希及完整候选清单。`pointer-callbacks/` 保存导出报告、逐字节与范围核验、每函数 C/汇编、哈希清单。保留跳过记录，避免将 4,226 个候选全部计作新函数。

```powershell
python -X utf8 tools/dsp_native_pointer_discovery.py
# 中间执行下述只读 Ghidra 导出
python -X utf8 tools/dsp_native_crt_callbacks.py --pointer-callbacks
python -X utf8 tools/dsp_native_query.py 18043d930 --module UnityPlayer.dll --show
```

Ghidra 阶段使用 `-readOnly -noanalysis`，执行 `DspCrtCallbacks.java`，参数依次为候选入口清单、导出目录、`pointer-discovery/report.json`。脚本从原程序内存再次核对每个证据槽的指针值，入口有归属时跳过，未归属时建函数并导出。查询工具验证全部清单并检查补充入口之间不存在重复，再将本批加入搜索。

其中 `18043d930` 补出了根据输入字节选择 `True`/`False` 并追加到字符串的实现；它在此前观察的静态转换记录中与 `181c42160` 配对，为该描述对象的布尔行为解释增加证据。其调用的字符串扩容、共享尾声及完整异常语义仍需结合相关函数核验。

后续需继续检查新增函数外部调用、间接跳转、共享尾声归属以及类型推断；本轮补齐的是入口发现与可查询输出。
