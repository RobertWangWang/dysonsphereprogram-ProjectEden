# 原版原型表目录

覆盖 LDB 声明的全部 **25 张表、2,828 条记录**。资源内 Unity 版本标记为 `2022.3.62f3c1`，不是游戏展示版本。

来源为 resources.assets；用 UnityPy 1.25.3 与 TypeTreeGeneratorAPI 0.0.10 根据本机托管程序集的序列化字段生成类型树。每个对象完整读取，并解码后重新编码，与原始对象字节完全一致。另核对 LDB 表覆盖、配方 ID/数量数组与物品引用、科技前置和解锁配方引用。

这些是磁盘原版资产：LDBTool、自定义 JSON、Harmony、科技状态和其他 mod 的修改不包含在内。缓存哈希见 generated/prototypes/manifest.json；游戏升级后重新导出。

| 表 | Path ID | 条数 | 序列化字节 | 本机数据 |
|---|---:|---:|---:|---|
| AbnormalityProtoSet | 136296 | 25 | 3,760 | [JSON](generated/prototypes/AbnormalityProtoSet.json) |
| AchievementProtoSet | 136297 | 128 | 25,900 | [JSON](generated/prototypes/AchievementProtoSet.json) |
| AdvisorTipProtoSet | 136298 | 100 | 10,632 | [JSON](generated/prototypes/AdvisorTipProtoSet.json) |
| AudioProtoSet | 136299 | 339 | 32,012 | [JSON](generated/prototypes/AudioProtoSet.json) |
| CosmicMessageProtoSet | 136300 | 21 | 844 | [JSON](generated/prototypes/CosmicMessageProtoSet.json) |
| DoodadProtoSet | 136301 | 5 | 772 | [JSON](generated/prototypes/DoodadProtoSet.json) |
| EffectEmitterProtoSet | 136302 | 16 | 1,060 | [JSON](generated/prototypes/EffectEmitterProtoSet.json) |
| EnemyProtoSet | 136303 | 31 | 2,936 | [JSON](generated/prototypes/EnemyProtoSet.json) |
| FleetProtoSet | 136304 | 6 | 588 | [JSON](generated/prototypes/FleetProtoSet.json) |
| GoalProtoSet | 136305 | 148 | 24,480 | [JSON](generated/prototypes/GoalProtoSet.json) |
| ItemProtoSet | 136306 | 175 | 49,040 | [JSON](generated/prototypes/ItemProtoSet.json) |
| JournalPatternProtoSet | 136307 | 42 | 2,932 | [JSON](generated/prototypes/JournalPatternProtoSet.json) |
| MIDIProtoSet | 136308 | 24 | 8,936 | [JSON](generated/prototypes/MIDIProtoSet.json) |
| MilestoneProtoSet | 136309 | 42 | 7,224 | [JSON](generated/prototypes/MilestoneProtoSet.json) |
| ModelProtoSet | 136310 | 664 | 79,264 | [JSON](generated/prototypes/ModelProtoSet.json) |
| PlayerProtoSet | 136311 | 1 | 180 | [JSON](generated/prototypes/PlayerProtoSet.json) |
| PromptProtoSet | 136312 | 28 | 964 | [JSON](generated/prototypes/PromptProtoSet.json) |
| RecipeProtoSet | 136313 | 162 | 20,620 | [JSON](generated/prototypes/RecipeProtoSet.json) |
| SignalProtoSet | 136314 | 40 | 2,948 | [JSON](generated/prototypes/SignalProtoSet.json) |
| TechProtoSet | 136315 | 315 | 83,952 | [JSON](generated/prototypes/TechProtoSet.json) |
| ThemeProtoSet | 136316 | 25 | 10,752 | [JSON](generated/prototypes/ThemeProtoSet.json) |
| TutorialProtoSet | 136317 | 22 | 2,000 | [JSON](generated/prototypes/TutorialProtoSet.json) |
| VegeProtoSet | 136318 | 286 | 38,952 | [JSON](generated/prototypes/VegeProtoSet.json) |
| VehiclePartProtoSet | 136319 | 169 | 30,428 | [JSON](generated/prototypes/VehiclePartProtoSet.json) |
| VeinProtoSet | 136320 | 14 | 1,864 | [JSON](generated/prototypes/VeinProtoSet.json) |

## 查询

```powershell
python -X utf8 tools/dsp_proto_query.py 宇宙矩阵 --table RecipeProtoSet
python -X utf8 tools/dsp_proto_query.py 115 --table RecipeProtoSet
python -X utf8 tools/dsp_proto_query.py --verify
```

导出用安装 UnityPy/TypeTreeGeneratorAPI 的 Python 执行 tools/dsp_proto_export.py；查询不需要这两个库。名称按子串匹配；纯数字只匹配 ID。
