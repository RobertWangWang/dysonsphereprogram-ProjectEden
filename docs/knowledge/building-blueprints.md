# 建造工具与蓝图

基线见 [baseline.json](baseline.json)。本页核验了普通点击建造入口、蓝图重叠闸门和蓝图容器序列化；不代表建造工具所有分支都已审查。

## 原版已核验

`BuildTool_Click` 在更新中调用 `CheckBuildConditions`，把结果交给 `ConfirmOperation`，确认后进入 `CreatePrebuilds`。其中创建新预建体的分支要求 `condition == Ok && coverObjId == 0`，复制位置、旋转、配方、过滤器和 parameters 后，优先消耗手持物品，否则从背包尾部取一件。实际取得一件才调用 `PlanetFactory.AddPrebuildDataWithComponents`；预览的 `objId` 记录为预建体编号的负值。放行条件不会自动生成建筑物品。

蓝图粘贴另有入口：`BuildTool_BlueprintPaste.CreatePrebuilds` 先跳过 `bpgpuiModelId <= 0`，随后才检查 `condition`，这里允许 `Ok` 或 `NotEnoughItem`。不要把点击工具的门槛直接套到蓝图工具。

`ArrangeOverlapBP` 对部分同位置预览同时写 `coverbp`、`bpgpuiModelId = -1` 和 `BlueprintBPOverlap`。仅清除 condition 仍会在 CreatePrebuilds 被跳过。反过来，`CheckBuildConditions` 中相互 coverbp 的预览会跳过对应的间距碰撞判断，因此 coverbp 并不是一个可以随意清空的错误标志。

`BlueprintData.ToBase64String` 的内容由二进制 Export → GZip → Base64 得到，拼入 header 后用 `MD5F.Compute` 生成签名。不要直接用标准 MD5 实现替代 `MD5F` 而不核对算法。Import 读取版本、区域和建筑后，再按临时输入/输出下标恢复对象引用，最后执行 DataRepair。

当前 Export 写 `Version = 2`、`Patch = 1`。Import 接受最多 64 个区域、1,048,576 个建筑；这只是该方法的数据检查范围，不是游戏 UI 支持规模的承诺。Version >= 2 才走当前地基 reformData 的读取分支。

`BlueprintBuilding` 不只有物品 ID 和坐标，还保存连接槽位、输入输出偏移、配方、过滤器、parameters 和 content。复制建筑时遗漏这些数据，会造成“外观正常但功能设置或连接丢失”。

## ProjectEden 对应补丁

- [BuildConditionCheatPatches](../../ProjectEden/src/Patches/Cheat/BuildConditionCheatPatches.cs)：放宽条件后重新汇总结果；覆盖多个工具。
- [BlueprintOverlapPatches](../../ProjectEden/src/Patches/Cheat/BlueprintOverlapPatches.cs)：快照恢复模型编号与条件，保留 coverbp 关联。
- [QualityBuildPatches](../../ProjectEden/src/Patches/Quality/QualityBuildPatches.cs)：品质建造通道，应与实际扣除物品和预建体编号一起核查。

## 查询

```powershell
python -X utf8 tools/dsp_knowledge.py search CreatePrebuilds --full --calls
python -X utf8 tools/dsp_knowledge.py search AddPrebuildDataWithComponents --full --callers
rg --no-ignore -n 'bpgpuiModelId|coverbp|BlueprintBPOverlap' docs/knowledge/generated/BuildTool_BlueprintPaste.cs
```

参考：[点击工具](generated/BuildTool_Click.cs)、[蓝图粘贴](generated/BuildTool_BlueprintPaste.cs)、[蓝图容器](generated/BlueprintData.cs)、[建筑记录](generated/BlueprintBuilding.cs)。拆除退款、升级、地基、无人机施工与品质回收尚需分别核验；上述“建立预建体”不等于实体已经施工完成。
