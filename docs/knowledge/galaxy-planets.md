# 星系、行星与生成调度

证据：[UniverseGen.cs](generated/UniverseGen.cs)、[StarGen.cs](generated/StarGen.cs)、[PlanetGen.cs](generated/PlanetGen.cs)、[PlanetData.cs](generated/PlanetData.cs)、[PlanetModelingManager.cs](generated/PlanetModelingManager.cs)。对应 [程序集基线](baseline.json)。

## 生成入口

```text
UniverseGen.CreateGalaxy
  → StarGen.CreateBirthStar / CreateStar
  → StarGen.CreateStarPlanets
      → PlanetGen.CreatePlanet

行星加载或扫描
  → PlanetModelingManager.Algorithm(planet)
      → 对应 PlanetAlgorithm0..14
      → GenerateTerrain → CalcWaterPercent
      → 非气态行星：GenerateVegetables → GenerateVeins
```

上图是生成阶段的导航，不表示所有调用在同一个线程中连续完成。原版建模管理器还有独立的加载与扫描线程路径；扫描路径会创建 `GetUnloadedCopy`，生成地形和矿物后汇总，再 `CopyScannedDataFrom` 并释放副本。

## 黑洞与中子星

`CreateGalaxy` 的黑洞数量使用 `CeilToInt(0.01 × starCount + random × 0.3)`，随后将末端一段恒星分配为黑洞。按这条原版公式，64 星时黑洞数为 1。其他星系生成 mod 可以替换该算法，所以这不是所有模组组合的保证。

`CreateStarPlanets` 的黑洞与中子星分支都显式设 `planetCount=1`，创建一颗非气态行星；白矮星等分支另有概率逻辑。不要从黑洞系的单行星规则外推全部恒星类型。

## 半径、真实半径与地形

`PlanetData` 默认 `radius=200`、`scale=1`，`realRadius=radius × scale`。不同消费者可能读取不同字段，改变其中一个不等于所有坐标、碰撞、轨道展示和地形都自动对齐。

`PlanetAlgorithm0.GenerateTerrain` 把 `planet.radius × 100` 转成 ushort 写入高度数据。此处证明至少这条路径存在高度编码的位宽边界；不能据此推断所有半径相关系统的最大安全值。

ProjectEden 入口：[PlanetRadiusPatches.cs](../../ProjectEden/src/Patches/Planet/PlanetRadiusPatches.cs)、[PlanetModPlanePatches.cs](../../ProjectEden/src/Patches/Planet/PlanetModPlanePatches.cs)。读取它们时要把原版半径字段、放大后的坐标以及存档状态分别跟踪。

## 后续排查

- 找错星球算法：先看 `planet.algoId` 及 `PlanetModelingManager.Algorithm`，不要凭主题名字推断类。
- 星系面板看不到新矿：区分生成时、扫描副本和已加载 factory 的矿量汇总。
- 不同种子结果漂移：检查补丁是否额外消耗原版随机数流。
- 扩大行星后矿显得少：半径、地形密度与矿脉数量不是同一配置，见 [矿脉生成](vein-generation.md)。

```powershell
python -X utf8 tools/dsp_knowledge.py search CreateStarPlanets --callers
python -X utf8 tools/dsp_knowledge.py search PlanetModelingManager::Algorithm --calls
python -X utf8 tools/dsp_knowledge.py search realRadius --callers
```
