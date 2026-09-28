# 更新日志

## 1.13.2

### 调整：反物质主线再次提速 10 倍

四座主线建筑的 `cyclesPerTick` 从 10 同步改为 100，分频 70 和黑洞星系 ×2 加成保持不变。满供电、原料充足、无增产时，一组 1:1:1:1 主线在普通星系约产 10,285.71 个反物质/分钟，在黑洞星系约产 20,571.43 个/分钟。原料及满蓄能柜供给需相应扩容。

### 修复：宇宙矩阵无法从其他研究站取得矩阵

- 同星球研究站现在可以直接互供产物，优先补齐生产配方原料，再供科研；剩余产物继续送进本地需求物流站。
- 互供需要同时开启 `logisticSupply` / `logisticOutput`，并遵守 `outputReserveItems`，无需物流站中转或重建研究站。
- 取料统一按实际件数记账，仅科研缓冲区使用 ×3600 换算，修复矩阵作为生产原料时数量计算错误。科研只补完整物品，避免不足一件的缺口造成取整损失。
- 离线回归：`dotnet run --project tools/LabLogisticsTests`，执行实际补丁源码，覆盖宇宙矩阵七种输入、混合模式、出货保留、开关及数量守恒。

### 验证与升级

研究站供料修复已获游戏内反馈确认。反物质速率按配置与代码计算，满供电、满供料、无增产时成立。升级后重启游戏，无需重建研究站；自定义 megabuildings.json 会覆盖内置提速参数，需要同步调整四座建筑的 cyclesPerTick。

---

这个文件只写**当前版本**。往期更新日志在仓库的 `ProjectEden/CHANGELOG-history.md`：
<https://github.com/RobertWangWang/dysonsphereprogram-ProjectEden/blob/main/ProjectEden/CHANGELOG-history.md>
