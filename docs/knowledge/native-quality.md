# 原生反编译警告分类

对选中的基础 C 输出逐文件核对哈希并扫描，共 261,368 个函数；补充恢复单独报告。以下是带该类警告的函数数，一个函数可同时计入多类。

| 类别 | 函数数 |
|---|---:|
| other | 36,759 |
| indirect-jump-as-call | 9,131 |
| jump-table | 9,130 |
| overlapping-globals | 8,635 |
| enum-names | 8,268 |
| unreachable-block | 1,229 |
| type-propagation | 907 |
| bad-instruction | 20 |

`jump-table` 与 `indirect-jump-as-call` 需要结合汇编区分未恢复 switch 与正常间接尾调用；不能把每条警告都视为真实缺失，也不能忽略。`bad-instruction` 需核查指令边界和代码/数据判断。枚举重名、全局符号重叠、不可达块等单独列出，不等同于丢失方法。

完整地址、名称、原始警告和 C 路径在 generated/native/quality-audit.json。运行 `python -X utf8 tools/dsp_native_quality.py` 重建。该审计检出风险线索，不证明没有警告的函数必然完整，也不把数值归零作为替代目标。

20 个 bad-instruction 函数的边界证据及独立修正见 [控制流定点核验](native-flow-repairs.md)；修正保留在旁路输出，本页仍统计基础导出。
