# 配方原型、科技和存档

证据：[RecipeProto.cs](generated/RecipeProto.cs)、[RecipeExecuteData.cs](generated/RecipeExecuteData.cs)、[GameHistoryData.cs](generated/GameHistoryData.cs)、[GameSave.cs](generated/GameSave.cs)、[GameConfig.cs](generated/GameConfig.cs)。版本见 [baseline.json](baseline.json)。

## 配方原型不是正在执行的配方

磁盘原版资产现已解码，见 [原型表目录](prototype-catalog.md)。25 张表均完成逐字节往返核验，可查询 175 个物品、162 个配方、315 个科技等实际记录。这些是初始化输入，不能替代加载 LDBTool 和 mod 配置后的运行时值。

`RecipeProto.InitRecipeItems` 为各配方创建 `RecipeExecuteData`，主计时阈值是 `TimeSpend × 10000`，增产阈值是 `TimeSpend × 100000`。原型的时间是逻辑帧，执行计时器使用放大值。

`RecipeExecuteData` 构造函数为输入/输出 ID 与数量创建新数组，并调用 `Array.Copy`。因此修改 `RecipeProto.Items` 不等于已缓存的执行配方同步更新；修改静态配方表也不等于每台机器已持有的新对象。需要核对初始化时机、机器字段赋值以及读档恢复路径。

数组字段声明为 readonly 只禁止重新赋值引用，不使数组元素不可变。若逐建筑改执行配方，应判断该对象是否共享，避免改一台影响所有同配方建筑。ProjectEden 的合金配比和活性复合材机制要结合各自补丁处理。

## 科技与矩阵需求

`GameHistoryData.UnlockTechFunction` 的功能编号 22 累加 `techSpeed`。研究站科研速度来自全局科技状态，不能由生产侧 `speed` 等字段替代。

`GameHistoryData.Import` 的相关段先清空 `LabComponent.matrixPoints`，再按当前科技的 `Items` 与 `ItemPoints` 重建，矩阵索引使用 `itemId - 6001`，并检查数组范围。新增科研矩阵不仅要有一个物品 proto，还要处理密集 ID、数组长度、科技需求和硬编码方法。

## 存档分层

`GameSave.SaveCurrentGame` 经 BinaryWriter 调用 `GameData.Export`；`LoadCurrentGame` 经 BinaryReader 调用 `GameData.Import`。GameSave 是文件与头部入口，不包含所有组件各自的序列化布局。

不同组件有自己的 Export/Import 和版本分支。例：CargoContainer 原版块版本为 2；ProjectEden preloader 修改后的货物布局与 mod 自身保存数据版本不是同一个编号。

[Plugin.cs](../../ProjectEden/src/Plugin.cs) 当前 `SaveVersion=5`，依次保存站点配置、合金配比、催化床和品质建筑数据，读回时有条件分支。新增持久化状态必须同时检查 Export、Import 和切档清理；不能把插件发行版本 1.13.2 当作二进制存档格式版本。

## 游戏版本的可确认范围

这份 DLL 的 `GameConfig` 初始化游戏主版本为 **0.10.35**。`build` 是另一个静态字段，`gameVersion` 也有 setter；本轮没有运行游戏获取最终显示版本，所以不填未经确认的完整 build。

程序集元数据版本仍为 0.0.0.0；实际核验使用 SHA-256 / MVID。磁盘 DLL 的初始值、运行时覆盖值和旧 README 的版本描述必须分开看。

## 常用查询

```powershell
python -X utf8 tools/dsp_knowledge.py search RecipeProto::InitRecipeItems --calls
python -X utf8 tools/dsp_knowledge.py search UnlockTechFunction --callers
python -X utf8 tools/dsp_knowledge.py search GameSave::LoadCurrentGame --calls
```

`ItemProto`、`PrefabDesc`、`StorageComponent` 也已完整导出供检索，但未逐字段写成人工结论。原版资源包内的配方实际值、prefab 默认参数和 LDBTool 运行时改写结果仍应以资源读取或启动调查为准。
