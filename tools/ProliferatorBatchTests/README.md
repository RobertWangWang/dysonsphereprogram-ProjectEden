# 均匀增产批量离线验证

在仓库根目录运行：

```powershell
python -X utf8 tools/ProliferatorBatchTests/prepare.py
dotnet run --project tools/ProliferatorBatchTests -c Release
```

准备脚本要求本机已有游戏反编译缓存，提取实际 InternalUpdate、split_inc_level、Cargo 表和当前补跑循环。生成代码位于忽略目录 tools/out，不进入仓库。七处产物闸按 mod 规则替换，测试中的 Scale 固定为119；实际 IL 匹配另由 tools/check_output_gate.ps1 检查。

测试链接实际 MegaProliferatorBatch.cs，比较40000组状态及完整循环结果，涵盖供电、增产/加速、计时余量、缺料、堵料、多输入/产物、历史incUsed，另测混合点数/品质/特殊配方/溢出回退、自检负例和1000组并发执行。Harmony与品质访问使用替身，不声称覆盖其他mod或preloader组合。
