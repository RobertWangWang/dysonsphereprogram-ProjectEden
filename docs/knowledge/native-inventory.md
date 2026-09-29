# 原生模块清单

由 `python -X utf8 tools/dsp_native_inventory.py` 从安装目录 PE 头生成；没有启动游戏或加载 DLL。托管程序集排除在此表之外。

这是一份实际安装文件清单，不证明每个文件都是游戏原厂文件。根目录 winhttp.dll 单独标为 mod 加载相关，避免当作游戏业务逻辑。导入导出表已保存在本机 generated/native 下；函数伪代码的完成情况需另外检查 Ghidra 输出。

| 文件 | 机器类型 | 字节 | 范围 | SHA-256 |
|---|---|---:|---|---|
| `DSPGAME.exe` | 0x8664 | 666,624 | installed-native | `a4b0ab1ec431f1b3c48334784a7a461f3b0dc58693fc5e5577345aa413416065` |
| `DSPGAME_Data/Plugins/x86_64/Microsoft_Xbox_Services_141_GDK_C_Thunks.dll` | 0x8664 | 2,115,968 | installed-native | `01b474b568e0ef438af7cac43b197e262d694099d3d345366ec76f93af8a8bfa` |
| `DSPGAME_Data/Plugins/x86_64/rail_api.dll` | 0x14c | 15,898,080 | installed-native | `c0094678b3cf3a026413f3fcddab3982e3381f1f34ece3473bdb865f8184cad1` |
| `DSPGAME_Data/Plugins/x86_64/rail_api64.dll` | 0x8664 | 20,112,864 | installed-native | `e5a7f21572d4face6590d03bf12e94916a3882deac037d5c68e05c95e3bc8f42` |
| `DSPGAME_Data/Plugins/x86_64/rail_wrapper.dll` | 0x14c | 1,071,584 | installed-native | `38aa6e90154f362d98fdc3c806de3a3e99892fd11f885ade3c7e5907694f99e9` |
| `DSPGAME_Data/Plugins/x86_64/rail_wrapper64.dll` | 0x8664 | 1,382,880 | installed-native | `016816a60c589ffde1a474579b3b228808af20a2156832356b071bac5bfa0872` |
| `DSPGAME_Data/Plugins/x86_64/steam_api64.dll` | 0x8664 | 262,944 | installed-native | `473f5a312b56519f347741b63f3dea590946b96ea40ef3803d5f452c39af2f1e` |
| `DSPGAME_Data/Plugins/x86_64/XGamingRuntimeThunks.dll` | 0x8664 | 76,152 | installed-native | `b793aa1b2151a64411fba102390961e796bce5cc511334d5029a1002b6232319` |
| `MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll` | 0x8664 | 7,896,912 | installed-native | `6f9713406d52d55a669db35fe8730e816e424cb60de3a4fd903d569de6721475` |
| `MonoBleedingEdge/EmbedRuntime/MonoPosixHelper.dll` | 0x8664 | 610,640 | installed-native | `893dce35f0a0037feb6d2ea4c64c571985673a37529cb5331e3eb22a1c7e4881` |
| `UnityPlayer.dll` | 0x8664 | 31,200,080 | installed-native | `b07814a510eea3f8b66a25117ab602237037d74668e6ce1f8de92f60c0ef28c4` |
| `winhttp.dll` | 0x8664 | 25,088 | mod-loader | `cf9dd372ca0ddbe01153502c49f8f756197bb260001792fe766f6c0242dc7fc0` |
