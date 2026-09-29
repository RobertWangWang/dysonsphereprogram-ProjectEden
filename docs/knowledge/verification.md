# 核验、版本更新和继续扩展

## 三层证据

1. **原版程序集**：本机磁盘 DLL，经 ILSpy / Cecil 导出；以 [baseline.json](baseline.json) 的 SHA-256、MVID 定位。
2. **mod 改写**：仓库中的 preloader、Harmony 补丁及 JSON；说明要指向对应源码，不能把 mod 数值写成原版事实。
3. **运行时状态**：用户覆盖文件、LDBTool 注册结果、当前科技、供电、存档和其他 mod；需要日志或游戏内测量。

方法 token、IL 偏移和反编译行号只在对应程序集内有效。C# 反编译中的局部变量名可能是工具重建的；修改 transpiler 时应核对 IL 指令形状、全部分支、跳转标签与异常区域。

## 游戏更新后

1. 先执行 `python -X utf8 tools/dsp_knowledge.py check`。哈希不一致时停止引用旧偏移。
2. 执行 `refresh` 生成新缓存。它不会自动更新人工笔记的 `baseline.json`，避免把「导出成功」误认为「机制仍然一样」。
3. 按专题逐项复查需要修改的结论，运行相关原有检查，再更新知识页与基线。若只核验部分专题，应拆分各页基线，而不是把全部页面一并标成已核验。
4. 需要发布插件时，按 [部署文档](../../部署.md) 构建和打包；本知识库工具不会部署 mod。

## 现有检查导航

| 检查 | 作用与边界 |
|---|---|
| `tools/LabLogisticsTests` | 使用游戏数据桩执行实际供料补丁；不模拟完整游戏调度 |
| `tools/verify_harmony.ps1 -Config Release` | 检查 Harmony 选择器、重载、参数名 |
| `tools/check_output_gate.ps1` | 检查实际原版制造出货闸 IL |
| `tools/sim_throttle.py` | 分频、结算与堵料的离线模型 |
| `tools/sim_labstock.py` | 研究站存量与消耗率推导 |
| `tools/verify_preloader.ps1 -Config Release` | Cargo 字段加宽和序列化检查；**不覆盖品质改写的全部阶段** |
| `tools/verify_quality.ps1 -Config Release` | 品质通道分析；不能直接当作整条品质改写的正确性证明 |

## 新增专题的最小格式

记录：问题 → 类型/完整方法签名 → 字段单位 → 已确认的分支与常量 → 调用方 → mod 对应补丁 → 验证命令 → 未确认部分。默认只添加中文开发说明，不复制整份旧设计稿。

完整程序集已由 `tools/dsp_full_decompile.py` 覆盖，使用方法见 [完整反编译](full-decompilation.md)。新增专题时直接从全量缓存阅读，不必先逐个添加类型。精选缓存仍由 `tools/dsp_knowledge.py` 的 `TYPES` 维护，`refresh` 后用 `catalog` 更新计数。全量缓存另用 `dsp_full_decompile.py --all-managed --check` 校验；不替代人工笔记基线核验。导出只是取得参考，不能自动认定其全部分支都已人工核验。
