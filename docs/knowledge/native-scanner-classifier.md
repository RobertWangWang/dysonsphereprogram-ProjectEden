# 扫描器双字节分类函数与寄存器效果

固定 32 位 `rail_api.dll` 的 `106392d0` 是扫描器 `10632090` 调用的辅助函数。本轮直接核验其 **57 字节、16 条指令**，以及 16 字节指针表、40 字节索引表；没有执行游戏或网络操作。

## 确定行为

函数只读取调用栈 `[ESP+4]`、`[ESP+8]` 的低字节。设其为 a、b：

| 条件（依次判断） | EAX 返回值 | 双字节组合数 |
| --- | ---: | ---: |
| a 为 `0xd8..0xdb` | 7 | 1,024 |
| a 为 `0xdc..0xdf` | 8 | 1,024 |
| a 为 `0xff` 且 b 为 `0xfe` 或 `0xff` | 0 | 2 |
| 其他 | 29 | 63,486 |

遍历全部 **65,536 个双字节组合**，两个栈参数的高 24 位分别设为非零固定模式。全部返回值一致，覆盖所有 16 条指令，且：

- ECX、EDX、EBX、EBP、ESI、EDI 保持原值。
- 无内存写入，参数与返回地址槽保持原字节。
- RET 只弹出返回地址，调用者负责清理参数。
- EAX 和 EFLAGS 可以改变；不能把它们列为保留状态。

这一寄存器结论同时与完整指令清单一致：运算目的寄存器只有 EAX，内存操作只有读取和 RET 的栈弹出。模拟使用固定寄存器哨兵，未声称枚举所有寄存器初始位模式。

将返回 7 的机器指令常量改成 9 后，负向用例能检出不符。更改已翻译代码时显式清除 Unicorn 的翻译缓存，确保测试执行变异后的字节。

## 对扫描器 C 的影响

`10632090` 的既有 C 含 `extraout_ECX`、`CONCAT44` 和把 EDX 拼为返回高位等表达。实际辅助函数保留 ECX/EDX，返回信息在 EAX 中；这些表达属于反编译器的调用约定/返回类型推断，不能据此认定辅助函数产生了新指针或 64 位返回值。

已尝试在临时 Ghidra 事务中把辅助函数返回类型标为 unsigned int 并设置内联。实验输出仍有两处 `Could not inline here`，且仍含 `extraout`，所以 **没有把这份实验 C 升级为默认结果**。后续另建 [手工重建参考](native-scanner-reference.md)，完成 6,816 组与原始扫描器及真实辅助机器码的整段对照，全部 160 条扫描指令已覆盖；真实分类表和调用前置条件仍待追踪。原始及实验文件保留，所有项目更改回滚。

## 复现与保存位置

```powershell
# 需要 Capstone、Unicorn
python -X utf8 tools/dsp_native_classifier_probe.py
python -X utf8 tools/dsp_native_query.py 106392d0 --module rail_api.dll --show
python -X utf8 tools/dsp_native_query.py 10632090 --module rail_api.dll
```

`classifier-probe/` 保存分类报告、完整代码汇编、56 字节表数据和哈希。实验内联稿位于 `scanner-inline-classifier/`；对 `DspRepairRailScanner.java` 追加参数 `inline-classifier` 可复现，输出目录应与 `scanner-body-repair` 分开。

这是对分类函数的语义与调用寄存器效果的核验，不代表整个扫描函数或所属解析功能已恢复完成；不增加函数入口计数。
