# 原生函数剩余问题与覆盖证据

截至 2026-09-29，基础导出和补充恢复合计可查 261,436 个函数伪代码。另有一个 UnityPlayer 函数已通过重新汇编、重定位后逐字节匹配上游汇编源码；Mono 的一个函数仍未恢复完整 C。源码匹配单列，不增加 C 伪代码计数。全库还有 [原生质量审计](native-quality.md) 中的警告待核验，不能据此宣称所有功能均已完整还原。

## Mono：mono_method_to_ir

来源 SHA-256：`6f9713406d52d55a669db35fe8730e816e424cb60de3a4fd903d569de6721475`。匹配 PDB 指出入口 `1802b5740`；当前识别代码末端为 `180312691`，PDB 符号范围的排他末端则为 `180312c24`，包括尾部两张跳转表，不能混称代码末端。正常 Ghidra 分析只建立了 18 段、34,156 字节的方法体；`DspInspectFunction.java` 输出 7,725 条指令，主间接跳转 `1802bc991` 没有目标引用。

原始指令直接证明了两张 RVA 跳转表：

| 跳转指令 | 表地址 | 索引范围 | 项数 | 目标计算 |
|---|---|---|---:|---|
| `1802bc991` | `180312694` | 无符号索引 ≤ `0x147` | 328 | `imageBase + uint32(table[index])` |
| `1802ebae3` | `180312bb4` | 先减 2，再检查无符号索引 ≤ `0x1b` | 28 | 同上 |

第一张表有 106 个不同目标。补上它后方法体增至 374,332 字节，出现第二个未解析跳转；两表都补齐后，得到 379,619 字节、87,087 条指令，未解析的 computed jump 数降为 0。该结果证明控制流覆盖有实质增长，但还不能单凭它宣称 C 反编译成功。

`tools/DspRepairMonoSwitch.java` 先核对完整二进制哈希、跳转指令与索引上限，再从当前程序内存读取表，逐项验证目标位于 PDB 函数范围内。只临时添加 Ghidra 引用与反汇编，最后回滚；原游戏文件不修改。覆盖报告保存在当前选中 Mono 目录的 `switch-repair/report.json`。只有状态结束、C 输出存在且后续覆盖检查通过，才能纳入补充完成统计。

两表修复后的此次尝试已结束：900 秒 C 反编译超时，状态为 `timeout`，临时修改确认回滚。87,087 条指令的覆盖证据保留，但没有将这次尝试记为 C 恢复成功。

后续 `dsp_native_mono_dispatch.py` 直接执行两段原始分派指令，补充了动态边界证据。主分派从 `1802bc969` 开始（42 字节、7 条指令），嵌套分派从 `1802ebaaf` 开始（54 字节、10 条指令）。对每段分别测试输入 `0..65535` 以及 `7FFFFFFF/80000000/FFFFFFFD/FFFFFFFE/FFFFFFFF`，合计 **131,082 组**，全部 17 条指令执行覆盖。

- 主表仅接受无符号输入 `0..327`，328 项映射到 106 个不同目标；其余测试值进入 `18031131c`。
- 嵌套表先按 32 位回绕减 2，再做无符号比较，所以原始输入仅 `2..29` 命中 28 项、10 个不同目标；其余测试值进入 `1802ecf91`。表索引会写回 `[RBP+8C0]`；主表不改写 `[RBP+A08]`。
- 两表的原始 RVA 字节逐项与既有修复报告核对，验证实际跳转目标和栈/帧指针保持。将主表上限改小 1、将嵌套表偏移从 2 改为 3 的两项模拟内存负对照，均被检出。

可查询的分组索引保存在选中 Mono 输出目录的 `mono-dispatch-behavior/targets.md`，完整原始字节和测试计数见同目录 `report.json`，manifest 固定文件哈希及既有修复报告依赖。索引只标数字输入和机器地址，尚未将其擅自命名为 IL opcode；共享入口不等于完整 case 行为相同。模拟在分派目标入口停止，没有执行 case 主体，不增加已恢复 C 或函数计数。

```powershell
python -X utf8 tools/dsp_native_mono_dispatch.py
python -X utf8 tools/dsp_native_query.py mono_method_to_ir --module mono-2.0-bdwgc.dll --limit 1
```

#### 展开函数体的独立核验与六处边界

`DspRepairMonoSwitch.java` 新增 `normalize` 模式，仍临时修复两表并回滚，但使用较少简化的 decompiler action，独立输出 `switch-normalize/`，限制反编译时间为 120 秒。该次进程已结束，结果仍为 timeout，没有 C；没有覆盖原 900 秒尝试的证据。

此次额外保存了完整 ASM。`dsp_native_mono_body.py` 将 **379,619 字节、87,087 条指令** 与源 DLL 及独立 Capstone 解码逐项比较，核对指令无重叠、无范围缺口，并确认与原双表修复的函数体一致。在“调用可返回、条件分支均探索”的静态模型中，这些指令均从入口可达；两张已验证的表提供间接跳转后继，不存在未解析的间接跳转。共整理 **2,505 个直接调用点、306 个目标**，索引见 `mono-body-evidence/calls.json`。

该静态模型同时发现 **6 处调用后继不在已导出函数体内**，均调用 `18005c960`（PDB 名 `mono_assertion_message_unreachable`）：

| 调用地址 | 调用后指令地址 |
| --- | --- |
| `1802c4c29` | `1802c4c2e` |
| `1802cad86` | `1802cad8b` |
| `1802d9214` | `1802d9219` |
| `1802defdd` | `1802defe2` |
| `1802f3fbf` | `1802f3fc4` |
| `1802f72b1` | `1802f72b6` |

各后继起始字节可解码为 `xor eax,eax; test eax,eax; jne ...`，并非单纯填充。报告保存每处后继 32 字节及解码，供下一步核验断言函数终止语义和函数体边界。不能仅凭这些指令存在就判定实际断言会返回，也不能仅凭当前 body 完整匹配就声称所有条件下的控制流已完整覆盖。

结果及依赖哈希见 `mono-body-evidence/report.json` 和 manifest。该核验是静态图分析，不是执行全部 87,087 条指令；没有将有限的分派模拟扩大为整个 JIT 编译函数的运行验证。

```powershell
python -X utf8 tools/dsp_native_mono_body.py
```

#### 断言封装及六处调用后继的条件行为

`dsp_native_mono_assertion.py` 核验 `18005c960` / `18005c8d0` 两个原始封装，共 **95 字节、22 条指令、144 组模拟**。前者将文件指针和行号低 32 位转交给后者的可变参数布局，后者调用 `monoeg_g_logv_nofree(NULL,4,format,varargs)`，把返回值写到 `180750370`，然后经已核验 PE 导入调用 `KERNEL32!RaiseException(0xE0000001,1,0,NULL)`。

日志函数和异常 API 均为显式边界模型。异常 API 模型停止时不执行后继；返回模型下，原始栈恢复和 RET 可达。其 RAX 是模拟 API 留下的机器寄存器效果，不据此把原声明的 void 改为返回值契约。测试核验参数转接、可变参数 home slots、全局指针保存和返回栈位置；在模拟内存把异常 flags 从 1 改为 0 的负对照被正确检出。

六段排除的前缀均为同样的六字节 `33 c0 85 c0 75 e9`，每段三条指令。**96 组前缀测试**覆盖不同初始 RAX/EFLAGS：`xor eax,eax` 清零，`test eax,eax` 设置零标志，`jne` 不跳转，所以到达时必定顺序前进，而不是重新进入断言调用。六个顺序后继分别为 `1802c4c34`、`1802cad91`、`1802d921f`、`1802defe8`、`1802f3fca`、`1802f72bc`。

结果位于 `mono-assertion-behavior/` 并依赖函数体审计的 manifest。这里证明的是可返回边界模型下的控制流，未执行真实异常处理，也未执行日志内部实现，不能据此声称非可继续异常在真实系统会返回。其余 case 主体和后继闭包仍需核验；尚未改变原函数体或 C 恢复计数。

```powershell
python -X utf8 tools/dsp_native_mono_assertion.py
python -X utf8 tools/dsp_native_query.py mono_assertion_message_unreachable --module mono-2.0-bdwgc.dll --limit 1
```

#### 六处条件后继的闭包及合并汇编

`dsp_native_mono_continuations.py` 从六个排除入口解码原始字节，沿控制流追踪到已核验函数体的指令起点。只依据上一节证据排除六条不可取的 `jne` 后向边，其他条件分支原则上保留两个方向。逐条检查新指令与原函数体无重叠、目标不落入已有指令中间。

结果每处均只包含已知的三条前缀指令，随后直接接回原函数体：新增总量为 **36 字节、18 条指令**，无新调用、未解析出口或额外终止点。新增指令保持为条件补充，不作为六个独立函数，也未修改原始 Ghidra 项目。

独立合并视图 `mono-continuation-evidence/combined.asm` 共 **379,655 字节、87,105 条指令**；`continuations.asm` 仅列新增部分，`instructions.json` 逐项给出原始字节，`report.json` 给出每条边和接回地址。manifest 固定全部文件以及函数体/断言证据依赖。合并保留原有汇编指令文本，新片段采用独立 Capstone 文本，二者共用源 DLL 地址与字节。

查询 `--assembly` 可显式选择该条件视图。条件仍是断言调用返回；静态闭包结束不意味着真实异常能继续执行，更不表示完整 C 或 JIT 功能已恢复。默认查询仍保留 C 导出超时事实。

```powershell
python -X utf8 tools/dsp_native_mono_continuations.py
python -X utf8 tools/dsp_native_query.py mono_method_to_ir --module mono-2.0-bdwgc.dll --limit 1 --assembly
```

#### 匹配 PDB 的源码行和变量导航

通过本地 Ghidra `pdb.exe` 读取已保存的匹配 PDB，`dsp_native_mono_lines.py` 再次核对 DLL CodeView 与 PDB 的 GUID/age、DLL 哈希及 XML 根身份，导出该函数 **3,047 条源码行记录、61 条变量记录（含 8 个参数）**。原始 XML 函数片段及记录保存在 `mono-pdb-lines/`；记录了 PDB、导出器和完整 XML 的哈希。原始 XML 是符号导出，不是游戏代码执行结果。

所有行记录来自构建时路径 `C:\build\output\Unity-Technologies\mono\mono\mini\method-to-ir.c`。全部 **87,105 个已核验指令起点、379,655 个已核验代码字节** 落入行映射范围，主表 106 个和嵌套表 10 个目标分组均有对应行号。分组可在 `switch-lines.md` / `switch-lines.json` 查询；`lines.json` 保留地址区间，`variables.json` 保留 PDB 原始名称、类型和偏移。

八个参数名称为 `cfg`、`method`、`start_bblock`、`end_bblock`、`return_var`、`inline_args`、`inline_offset`、`emit_check_this`。变量偏移按 PDB 导出原样保存，未直接解释为当前执行点的 RSP/RBP 偏移。匹配符号提供调试元数据，不能据此声称完整源码正文或原始上游提交已经取得。

此次纠正了本页此前混淆的范围：PDB 长度 `0x5d4e4` 得到排他末端 `180312c24`，恰为嵌套跳转表 `180312bb4 + 28*4` 的末端。原识别代码末端 `180312691` 仍作为已分析指令范围边界；跳转表以及间隔字节不能计为代码。源码行覆盖与 C 语义恢复是不同的证据，完整 C 导出超时状态保持不变。

```powershell
python -X utf8 tools/dsp_native_mono_lines.py --xml <pdb.exe导出的XML> --exporter <pdb.exe路径>
python -X utf8 tools/dsp_native_query.py mono_method_to_ir --module mono-2.0-bdwgc.dll --limit 1
```

#### 构建版本、缺失的源文件校验值与官方候选

原始 `mono_get_runtime_build_info`（`18029b030`，50 字节）引用 `1806b2c34` 的版本字符串 `6.13.0`，以及 `1806b2be0` 的 `Visual Studio built mono`；这不是可定位源码版本的提交号。对应原始指令和字符串地址记录在 `mono-source-candidate/verification.json`。

新增 `tools/dsp_pdb_source_checksum.cpp`，用本机 Microsoft DIA SDK 读取匹配 PDB 的源文件记录。`method-to-ir.c` 返回 checksum type=0、零长度校验值，故无法用 PDB 校验值证明某个上游文件完全相同。工具只读取 PDB，不加载游戏；可用 Visual Studio x64 开发者命令环境、DIA SDK include/lib、`diaguids.lib ole32.lib oleaut32.lib` 编译，调用形式为 `dsp_pdb_source_checksum.exe <pdb路径> method-to-ir.c`。原始输出和工具哈希已经保存。

已从 [Unity 官方 Mono 仓库](https://github.com/Unity-Technologies/mono) 的 `unity-2022.3-mbe` 分支取得比较候选，并固定到提交 `b8eb56fab45dd52845fabba7b7ddc9a80b2b0498`。候选 [method-to-ir.c](https://github.com/Unity-Technologies/mono/blob/b8eb56fab45dd52845fabba7b7ddc9a80b2b0498/mono/mini/method-to-ir.c) 共 13,321 行，文件 SHA-256 为 `b3d750ed4a4868f334f653f6c66d2d801f0a763ac0e7335557f3ba1600cba5dd`，原始 LICENSE 一并保留。这里的分支选择仅为比较候选，不推断本游戏实际使用该提交。

`dsp_native_mono_source_candidate.py` 离线核验 PDB/DLL 身份、固定提交下载 URL、缓存哈希、DIA 输出和版本字节；统一查询明确标记 `candidate-only`。候选文件与若干行号的初步对应仅是后续比较线索，不是二进制等价或源码精确匹配证据，未增加 C 恢复数。

```powershell
python -X utf8 tools/dsp_native_mono_source_candidate.py
```

#### 主分派 opcode 与候选 case 标签的逐项对照

`dsp_native_mono_cases.py` 从同一匹配 PDB 的 XML 提取 `MonoOpcodeEnum`（330 个成员，包括边界/无效枚举），再按保留行号的词法结构提取候选 `switch(il_op)` 的顶层 case。字符串、注释和预处理行被遮蔽，嵌套 switch 的标签不混入主表；不执行宏展开或条件编译。

候选主 switch 有 **106 个标签组、257 个显式标签**。将机器主表的每个输入、PDB 枚举名称、目标地址的 PDB 源码行及该行所属的候选标签组对照，**328/328 个输入对应成功**；无显式 case 的输入与候选 default 组对应。完整结果位于 `mono-case-comparison/comparisons.json` / `.md`，原枚举与候选组也独立保存。

例如输入 0 对应 `MONO_CEE_NOP`；输入 `2/3/4/5/14/265` 共享的机器目标，对应候选的 `LDARG_0/1/2/3/S` 与 `LDARG` 标签组。名称来自匹配 PDB，候选标签来自固定提交文件，不是单凭数值推测。将候选 NOP 标签改为 BREAK 的负对照，成功检出输入 0 不再匹配。

这是对候选的结构一致性证据，尚未比较 106 个分支内部所有指令、宏实现、条件编译结果或代码生成行为；未证明源码提交完全一致，也未覆盖嵌套表的源码标签。不会据此将完整 C 的 timeout 状态改为恢复完成。

```powershell
python -X utf8 tools/dsp_native_mono_cases.py --xml <已核验PDB导出的XML>
python -X utf8 tools/dsp_native_query.py mono_method_to_ir --module mono-2.0-bdwgc.dll --limit 1
```

#### 嵌套类型表与只读字段常量候选分支

`dsp_native_mono_cases.py --nested` 使用同一 PDB 的 `MonoTypeEnum`（36 成员）和候选 `switch(ro_type)`，对嵌套表原始输入 `2..29` 做逐项比较。**28/28 项对应成功**，候选有 10 个标签组、22 个显式标签。完整枚举存为 `mono-type-case-comparison/type-enum.json`，目标与行号索引存为 `comparisons.json` / `.md`。主表 328 项也在工具扩展后重新通过。

候选源码上下文表明，`ro_type` 用于只读静态字段的常量处理，且枚举值类型可能先转为底层类型。这是对候选上下文的解读；本次机器证据仍限于枚举值、目标地址、源码行和标签一致，尚未验证进入该优化分支的全部条件。

| 原始输入 | PDB 类型标签组 | 候选分支职责（未做整段机器语义证明） |
| --- | --- | --- |
| 2、5 | BOOLEAN、U1 | 读取无符号 8 位值生成整数常量 |
| 4 | I1 | 读取有符号 8 位值 |
| 3、7 | CHAR、U2 | 读取无符号 16 位值 |
| 6 | I2 | 读取有符号 16 位值 |
| 8 | I4 | 读取有符号 32 位值 |
| 9 | U4 | 读取无符号 32 位值 |
| 15、24、25、27 | PTR、I、U、FNPTR | 指针常量及求值栈类型处理 |
| 14、18、20、28、29 | STRING、CLASS、ARRAY、OBJECT、SZARRAY | 根据 GC 是否移动决定能否生成指针常量 |
| 10、11 | I8、U8 | 64 位整数常量 |
| 12、13、16、17、19、21、22、23、26 | R4、R8、VALUETYPE 及无显式匹配的输入 | 候选清除 is_const |

将候选 BOOLEAN 标签错误替换成 CHAR 的负对照，检出输入 2 不匹配。宏和预处理分支没有执行，不能将标签对应扩展为内存读取、IR 分配、GC 安全性或完整源码等价的证明。嵌套表原先仅有数字输入，现在可按匹配 PDB 的类型名称检索，C 恢复计数不变。

```powershell
python -X utf8 tools/dsp_native_mono_cases.py --nested --xml <已核验PDB导出的XML>
```

#### 只读字段值的机器读取、扩展和写入

`dsp_native_mono_constants.py` 对九段真实读取片段进行隔离执行，共 **118 字节、27 条指令、133,339 组测试**。每段三条指令从 `[RBP+790]` 取字段地址，按分支位宽读取并扩展，随后写入传入 RAX 指向的 8 字节位置。RAX 已准备好输出位置是本片段的前提，不把它当作完整分支入口。

| 片段入口 | 已验证的机器操作 |
| --- | --- |
| `1802ebc18` | 8 位读取、零扩展至 64 位 |
| `1802ebe5d` | 8 位读取、符号扩展至 64 位 |
| `1802ec0a3` | 16 位读取、零扩展至 64 位 |
| `1802ec2e8` | 16 位读取、符号扩展至 64 位 |
| `1802ec533` | 32 位读取、符号扩展至 64 位 |
| `1802ec778` | 32 位读取、通过 ECX 写入零扩展至 64 位 |
| `1802ec9bc` / `1802ecc28` / `1802eceae` | 64 位读取并保留全部位模式 |

8 位与 16 位在对齐地址穷举全部取值；32/64 位测试低值和符号/最大值边界，所有片段另测 1–7 字节偏移的未对齐地址。核验读取地址与宽度、仅写输出 8 字节、输出两侧哨兵、原值不变以及 EFLAGS、RAX、RBP、RSP 保持。将无符号 8 位读取改成有符号指令的模拟内存负对照被正确检出。

报告保存在 `mono-constant-load-behavior/`，依赖嵌套类型对照 manifest。这为候选中的标量读取提供机器行为证据，但没有执行分支资格判断、IR 节点分配、IR opcode 初始化、链表更新或求值栈推进；引用类型的读取片段仍以先前 GC 分支允许该优化为前提。不要据此宣称整个常量生成分支已验证。

```powershell
python -X utf8 tools/dsp_native_mono_constants.py
```

#### 七个整数常量分支的完整局部 IR 生成

`dsp_native_mono_integer_ir.py` 从七个整数类型目标开始，执行到共同后继 `1802ecf9b`：无符号/有符号 8 位、16 位、32 位，以及 64 位。七段共 **4,048 字节、1,018 条指令**，**1,176 组模拟执行覆盖所有指令**，不再只验证三条读取片段。I2 分支后的对齐间隙未当作代码执行。

每段均调用 `180183a60` 请求 0x50 字节节点，将节点地址写入当前求值栈槽，设置数值 opcode（较小整数 0x181、64 位 0x182）、类型字节（1/2）、寄存器编号与源寄存器默认值，清零 next/prev 和常量槽，再复制编译上下文中的 IL 指针。常量按相应位宽扩展到 8 字节；目标寄存器分别通过 `18029d500(cfg,1)` 或 64 位路径的 `18029d480(cfg)` 返回模型提供。

已验证链表行为：`cfg+60` 指向当前块，块偏移 0 是尾指针，偏移 10 是头指针。空链表时两者均指向新节点；非空时原尾节点 `+18` 指向新节点、新节点 `+20` 指向旧尾，头不变，尾更新。新节点 next 保持空，求值栈槽写入节点后 `[RBP+8]` 增加 8。

测试组合包含每类型七个边界值、空/单节点旧链表、三个虚拟寄存器返回值、全零/0xA5 两种分配内容及对齐/偏移 3 的字段地址。比较节点完整 0x50 字节、输出哨兵、调用参数、上下文保持以及所检查寄存器和栈帧；不能因分配器通常清零而忽略节点未写字段。把求值栈推进从 8 改成 16 的模拟内存负对照被检出。

结果保存于 `mono-integer-ir-behavior/`，依赖读取片段 manifest。内存池和虚拟寄存器分配器没有执行真实实现，模型始终提供有效节点；尚未验证分配失败、进入类型分派之前的优化条件、引用类型的 GC 判断或生成代码实际运行。这里恢复的是完整局部分支行为，不是整个 `mono_method_to_ir` 的 C 等价证明。

```powershell
python -X utf8 tools/dsp_native_mono_integer_ir.py
```

[RetDec v5.0](https://github.com/avast/retdec/releases/tag/v5.0) 的选择性导出提供了 C、LLVM IR、反汇编和配置快照，保存在 `retdec-selected` 与 `retdec-noopts`。其日志有 CFG 边被跳过的提示，C 中还保留对主跳转表的间接读取；即使进程退出码为 0、产生了 C 文件，也不足以证明完整控制流已经恢复。因此这些文件仅作对照，未加入恢复数。

## UnityPlayer：1800145ca

来源 SHA-256：`b07814a510eea3f8b66a25117ab602237037d74668e6ce1f8de92f60c0ef28c4`。连续代码范围为 `1800145ca` 至 `180015300`，共 3,383 字节；最后一条为 `ret`，随后为 `int3` 填充。

Ghidra 的默认、仅 C、不同分析选项及临时移除局部类型尝试仍未生成 C，主要错误为 `Unable to find unique hash for varnode`。RetDec 的整文件解码尝试 900 秒超时；选择完整函数范围单独解码后产生了 C 和 LLVM IR。

该 RetDec C 含大量 `__asm_*` SIMD 占位调用，并把 `int128_t` 定义成 `int64_t`。这不能代表已经恢复了 128 位向量的完整类型和操作语义，因此也未计为恢复完成。原始机器指令、LLVM IR 和候选 C 都保留，便于继续核对。

### 已核验的上游汇编源码匹配

该函数精确匹配 libvpx v1.3.0 的 `vp8_loop_filter_bv_y_sse2`，固定提交为 `2e88f2f2ec777259bda1714e72f1ecd2519bceb5`。来源为 [WebM 官方源码](https://github.com/webmproject/libvpx/blob/2e88f2f2ec777259bda1714e72f1ecd2519bceb5/vp8/common/x86/loopfilter_block_sse2.asm#L279)。核验不只比较函数名或指令文本：

- 使用 NASM 3.02、Win64 ABI 重新汇编原始文件及 `x86_abi_support.asm`；两份输入文件哈希固定在脚本内。
- 本地配置仅设置 `CONFIG_PIC=0`，预包含 `section .text`，使旧 YASM 源码在 NASM 下明确建立代码节与函数符号；未改动上游函数指令。
- 根据 COFF 的 `REL32` 记录，将该函数的 54 处常量引用重定位到游戏中的常量表地址。
- 整个函数 **3,383 字节全部相等**；常量表 **128 字节全部相等**，没有忽略指令、立即数或常量差异。
- 代码 SHA-256：`708585ba91af6bc6274fd77660ec74fc6340b508078a66bf9a934bea0d85cbd4`；常量 SHA-256：`b7254e303b18ab5ef855477ef3ad7e0db6dfeb91dbd75c9ed995c840f5b5166a`。

函数签名为 `void vp8_loop_filter_bv_y_sse2(unsigned char *src_ptr, int src_pixel_step, const char *blimit, const char *limit, const char *thresh)`。这是 VP8 视频解码中的亮度垂直块边界去块滤波，原地更新图像；后三个指针提供滤波阈值。源码的 `LF_ABS`、`LF_FILTER_HEV_MASK`、`LF_FILTER` 宏保留了饱和字节运算和 128 位向量语义。

配套保存的 `loopfilter_filters.c` 可辅助理解标量算法；其中 `vp8_loop_filter_bv_c` 还处理色度，签名也不同，不能直接当作该 SIMD 函数的已验证等价替换。此次证明限定为完整汇编函数与常量的二进制匹配，不推断整个 Unity 使用的 libvpx 均来自同一未经修改的提交。

报告、COFF 对象、原始源码以及 LICENSE/PATENTS/AUTHORS 保存在 `generated/native/UnityPlayer.dll/source-match/`，均为本机缓存。查询命令：

```powershell
python -X utf8 tools/dsp_native_query.py vp8_loop_filter_bv_y_sse2 --module UnityPlayer.dll
python -X utf8 tools/dsp_native_query.py 1800145ca --module UnityPlayer.dll --show
```

重建命令为 `python -X utf8 tools/dsp_native_source_match.py --nasm <nasm.exe路径> --reference <上游文件目录>`。参考目录需包含固定提交中的 `vp8/common/x86/loopfilter_block_sse2.asm`（放在目录根）、`vpx_ports/x86_abi_support.asm`（保留子目录）、根目录 LICENSE/PATENTS/AUTHORS，以及 `vp8/common/loopfilter_filters.c`（放在目录根）。核验失败不会生成成功报告，游戏文件全程只读。

NASM 来自 [官方 3.02 Windows 包](https://www.nasm.us/pub/nasm/releasebuilds/3.02/win64/nasm-3.02-win64.zip)，本机压缩包 SHA-256 为 `161d0bfaff53c2f9e9f3e69fd0672323ebabafd1268976a5cec11be92a19aee7`，不冒充官方公布的摘要。具体汇编器版本、可执行文件哈希、命令及全部缓存文件哈希均记录于 `source-match/report.json`。

## 工具与复现

已生成 C 的函数也可能截断控制流。20 个 bad-instruction 函数的后续边界审计、XOP/FMA4 解码缺口，以及 Unity 两级索引表修正见 [控制流定点核验](native-flow-repairs.md)。这类修正单独保留，不通过增加导出数量表示进展。

RetDec 官方 Windows v5.0 压缩包本机 SHA-256 为 `496a2d86420115eaa21ac8ff9b202282525f0a46a28a78c983f2cca0643ab101`。这是本机记录，不冒充发布方提供的校验值。工具安装在任务工作目录；缺失的 OpenSSL 1.1 动态库使用本机 NVIDIA Nsight Systems 自带版本，仅复制到工具目录，未修改系统或游戏运行环境。

选择性导出用 `--select-functions mono_method_to_ir --pdb <已核验PDB>` 或 `--select-ranges 0x1800145ca-0x180015301`；加 `--select-decode-only` 仅解码目标函数，限制须在解释输出时保留。完整命令参数可从输出 `.config.json` 的 `decompParams` 查看。
