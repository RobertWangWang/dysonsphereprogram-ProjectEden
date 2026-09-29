# 原生函数导出进度与质量

安装清单中的 12 / 12 个模块完成逐函数导出，已保存 261,368 个函数伪代码，失败 70 个；其中 59,529 个函数含 Ghidra WARNING。

上面及下表为基础导出数；[独立补充恢复](native-recovery.md) 已核验 68 个原失败函数，合计可查询 261,436 个函数伪代码，原失败列表仍有 2 个未恢复 C。恢复方式和原始签名单独保留。

这些未恢复 C 的函数中，另有 1 个通过重新汇编、重定位和完整机器码及常量比较，已匹配上游汇编源码；余下 1 个仍待恢复。源码匹配不增加 C 计数，详见 [覆盖证据](native-edge-cases.md)。全库警告另见 [质量审计](native-quality.md)。

这是已识别函数的导出结果，不保证发现了二进制中的每段代码，也不把“返回伪代码”视为控制流/类型恢复正确的证明。跳转表恢复、间接调用等警告要结合原始反汇编核验。无完成标记只表示没有可核验的完整输出，不能单凭标记缺失判断后台进程已停止。

| 模块 | 状态 | 伪代码 / 识别函数 | 失败 | 含警告函数 |
|---|---|---:|---:|---:|
| DSPGAME.exe | 已导出并校验 | 397 / 397 | 0 | 94 |
| DSPGAME_Data/Plugins/x86_64/Microsoft_Xbox_Services_141_GDK_C_Thunks.dll | 已导出并校验 | 6,543 / 6,543 | 0 | 2125 |
| DSPGAME_Data/Plugins/x86_64/rail_api.dll | 已导出并校验 | 87,429 / 87,429 | 0 | 14637 |
| DSPGAME_Data/Plugins/x86_64/rail_api64.dll | 已导出并校验 | 81,778 / 81,778 | 0 | 15925 |
| DSPGAME_Data/Plugins/x86_64/rail_wrapper.dll | 已导出并校验 | 5,938 / 5,938 | 0 | 567 |
| DSPGAME_Data/Plugins/x86_64/rail_wrapper64.dll | 已导出并校验 | 5,867 / 5,867 | 0 | 1832 |
| DSPGAME_Data/Plugins/x86_64/steam_api64.dll | 已导出并校验 | 956 / 956 | 0 | 284 |
| DSPGAME_Data/Plugins/x86_64/XGamingRuntimeThunks.dll | 已导出并校验 | 347 / 347 | 0 | 24 |
| MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll | 已导出并校验 | 18,608 / 18,677 | 69 | 10565 |
| MonoBleedingEdge/EmbedRuntime/MonoPosixHelper.dll | 已导出并校验 | 2,273 / 2,273 | 0 | 297 |
| UnityPlayer.dll | 已导出并校验 | 51,121 / 51,122 | 1 | 13088 |
| winhttp.dll | 已导出并校验 | 111 / 111 | 0 | 91 |
