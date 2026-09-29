# 矿脉生成：算法覆盖、随机流与数组容量

证据：[PlanetAlgorithm.cs](generated/PlanetAlgorithm.cs)、派生类 [目录](type-catalog.md)、[ThemeProto.cs](generated/ThemeProto.cs)、[VeinProto.cs](generated/VeinProto.cs)。对应 [程序集基线](baseline.json)。

## 先区分声明数量与有效实现

本轮导出基类和全部 `PlanetAlgorithm0..14`，元数据中共有六个 `GenerateVeins` 声明：

| 类型 | 已确认情况 |
|---|---|
| `PlanetAlgorithm` | 基础生成实现 |
| `PlanetAlgorithm0` | 空实现，不生成矿脉 |
| `PlanetAlgorithm7` | 独立 override |
| `PlanetAlgorithm11` | 独立 override |
| `PlanetAlgorithm12` | 独立 override |
| `PlanetAlgorithm13` | 独立 override |

所以 ProjectEden 要覆盖的是五份非空实现，而不是只改基类，也不是机械地要求匹配六份相同指令。其余派生类没有另行声明该方法，沿继承使用实现。派生类已取得完整代码；本页详细机制以下述基类为核验对象，不声称五份方法的全部分支完全一致。

## 基类的主题数据映射

`GenerateVeins` 根据 `planet.theme` 查 `ThemeProto`。临时数组长度取 `PlanetModelingManager.veinProtos.Length`，把 `VeinSpot`、`VeinCount`、`VeinOpacity` 从目标下标 1 开始拷贝。

`RareVeins` 中的每个矿种 ID 直接用作数组下标。`RareSettings` 每种矿取连续四项：出生星系概率、其他星系概率、追加矿簇概率、数量/丰度相关参数。出生判断是 `planet.star.index == 0`，不是「本星系第一颗行星」。

因此新矿种 ID 需要与原型表、产品表、模型索引表和生成临时数组一起定容。只注册 VeinProto 并不能证明所有按矿种 ID 索引的数组都足够长。

## 随机流与选址

基类以 `planet.seed` 构造 `DotNet35Random`，消费若干值生成出生资源种子及第二条随机流。稀有矿判定和后续矿点选址使用不同位置的随机结果。补丁向既有流多取一次随机数，会改变后续结果，即使种子没变。

稀有矿初始命中概率还有星体参数的幂变换，不等于配置数组中的原始概率。命中后还会决定追加矿簇，并经过地形高度、水面、距离等选址判断。因此「概率为 1」与「最终一定落出可采矿点」不是同一个命题。

矿簇方向缓存 `veinVectors` 基础容量为 512。矿种相关数组足够长，不代表容纳全行星矿簇的向量缓存也足够；它们是两个独立容量问题。

`planet.radius` 参与角距离和地形判断，但矿簇数量初始化来自主题 VeinSpot 等数据。扩大表面积不会自动保证矿簇数量按面积同比增加。

## ProjectEden 对应入口

- [OreVeinRangePatches.cs](../../ProjectEden/src/Patches/Ore/OreVeinRangePatches.cs)：矿种遍历范围。
- [VeinProtoArrayPatches.cs](../../ProjectEden/src/Patches/Ore/VeinProtoArrayPatches.cs)：原型相关数组容量。
- [VeinVectorCapacityPatches.cs](../../ProjectEden/src/Patches/Ore/VeinVectorCapacityPatches.cs)：矿簇方向缓存容量。
- [StarVeinPatches.cs](../../ProjectEden/src/Patches/Ore/StarVeinPatches.cs)：星体限定矿和独立随机流。
- [VeinScalingPatches.cs](../../ProjectEden/src/Patches/Planet/VeinScalingPatches.cs)：矿脉数量随行星规模调整。
- [RareVeinProspector.cs](../../ProjectEden/src/Patches/Ore/RareVeinProspector.cs)：在副本上运行实际生成流程后汇总。

```powershell
python -X utf8 tools/dsp_knowledge.py search GenerateVeins --callers
rg --no-ignore -n 'RareSettings|veinProtos.Length|veinVectors' docs/knowledge/generated/PlanetAlgorithm*.cs
```

Windows PowerShell 下如果 rg 不展开末尾通配符，可改用 `rg --no-ignore -n -g 'PlanetAlgorithm*.cs' 'RareSettings|veinVectors' docs/knowledge/generated`。
