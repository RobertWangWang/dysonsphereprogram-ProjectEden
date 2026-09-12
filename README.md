# 伊甸园计划 / Project Eden

把《戴森球计划》的后期产线整体拉满的 BepInEx mod。

六座万倍速巨型建筑、满级采矿机与物流站、八种新矿脉与一整条从煤到合金的化工链、
逐台建筑可调的合金配比、5000 层传送带集装——**所有数值都写在 JSON 里，装完就能改。**

> 开发与测试基于游戏版本 **0.10.34.28529** ／ BepInEx **5.4.17**

💬 **交流群（QQ）：789720138** —— 报 bug、提需求、聊平衡都在这里。

**完整特性说明** —— 每一样新物品、新配方、新建筑，以及每个数值是怎么推出来的：

- 📘 **[中文：mod特性.md](https://github.com/RobertWangWang/dysonsphereprogram-ProjectEden/blob/main/mod%E7%89%B9%E6%80%A7.md)**
- 📗 **[English: mod_feature.md](https://github.com/RobertWangWang/dysonsphereprogram-ProjectEden/blob/main/mod_feature.md)**

---

## ⚠️ 先看这里：存档兼容性

**装了这个 mod 之后存的档，卸载后就打不开了。** 不是「缺物品图标」那种降级，是真的读不出来。

本 mod 带一个 **preloader**（`BepInEx/patchers/` 里那个 DLL），它在游戏程序集装载前把传送带货物的
两个字段加宽了——这是「高层集装 + 满级增产剂同时成立」的前提——货物块的存档格式因此从版本 2 升到 3。

- 装 mod **之前**存的老档仍然能开（会自动按老格式读），**反过来不行**
- 除此之外还会往存档写新物品、新配方、新矿脉类型，以及一个独立的 `.moddsv` 伴生文件
- **没有干净的退回原版的路。要退就回备份。**

**强烈建议开新档**，或者动手前把 `%USERPROFILE%\Documents\Dyson Sphere Program\Save` 整个复制一份。

---

## 安装

**1. 先装四个前置**，都在 Thunderstore 上，装进同一个 profile：

| 前置 | 版本 |
|---|---|
| BepInEx | 5.4.17 |
| LDBTool | 3.0.3 |
| CommonAPI | 1.6.7 |
| DSPModSave | 1.2.2 |

缺任意一个，游戏启动即报错。

**2. 导入本体**：r2modman 左侧 `Settings` → `Import local mod` → 选发布包的 zip，
然后从 r2modman 点 **`Start modded`** 启动（直接开 Steam 不会加载 mod）。

手动安装的话，包里的 `plugins/` 和 `patchers/` 要分别落到 profile 的两个目录下：

```
plugins/ProjectEden.dll          →  BepInEx/plugins/ProjectEden/ProjectEden.dll
plugins/Newtonsoft.Json.dll      →  BepInEx/plugins/ProjectEden/Newtonsoft.Json.dll
patchers/ProjectEden.Preloader.dll  →  BepInEx/patchers/ProjectEden/ProjectEden.Preloader.dll
```

> ⚠️ **`patchers/` 里那个不能漏，也不能放进 `plugins/`。** 放错位置**不会报错**：
> BepInEx 不去 `plugins/` 里找 patcher，插件启动后发现字段没被加宽，就按降级模式继续跑
> （集装退回 255、超过 63 层的增产剂被夹断），日志里写一行就过去了。

**3. 确认装对了**：`BepInEx/LogOutput.log` 里应该有这两行——

```
Project Eden v1.1.1 已加载
Cargo.inc / Cargo.stack 已加宽为 Int16（结构体 36 字节）：…
```

第二行是判断 preloader 有没有生效的唯一依据。完整说明见 **[部署.md](https://github.com/RobertWangWang/dysonsphereprogram-ProjectEden/blob/main/%E9%83%A8%E7%BD%B2.md)**。

---

## 改了什么

| | |
|---|---|
| **六座巨型建筑** | 建造栏新增一页：天工装配厂、冶铸熔炉、燔石化工厂、锤锻精工厂、观微对撞机、生物温室。10000 倍速，12 个传送带口直连，自带行星内物流站；外形是**代码生成**的，六座各不相同。生物温室还多一条规矩：**整座建筑受日照约束**，满日照满产、背光面停工 |
| **大型采矿机 / 抽水站 / 原油萃取站** | 速度拉满、矿脉不消耗、机内缓存 1000 万；部分矿石直接产出冶炼后的锭 |
| **物流** | 物流站 30 格 × 1000 万，星际站充能 30 GW，运载与集装拉满 |
| **矩阵研究站** | 只改生产侧（造矩阵），科研速度没动；与物流站双向直通，不用铺传送带 |
| **电力** | 卫星配电站全球覆盖；新增 1000 倍风力发电机集群 |
| **八种新矿脉** | 钴、铝、石膏、锂、锰、铬、钒、钨——素材全部复用铁矿脉运行时改色 |
| **C1 化工链** | 煤 + 水 → 合成气 → 甲醇 → 甲醛 / 乙烯，另有费托合成产精炼油 |
| **氮链 / 钨链** | 哈伯法制氨 → 奥斯特瓦尔德法制硝酸；白钨矿 → 三氧化钨 → 钨块 → 碳化钨 |
| **九种合金 + 四维属性** | 每种金属多四行属性（硬度／韧性／耐蚀／导电）；硬质合金的 WC:Co 配比**逐台建筑**用滑动条调，落到产量与制造时间上 |
| **五阶合金弹药** | 伤害与产量由**喂进去的那两种合金**决定，共用一条配方 |
| **传送带与集装** | 三档速度提到原版 4 倍；集装层数 5000（靠 preloader 加宽字段实现） |
| **界面** | 物品选取窗口加搜索框；合成面板、配方选取、建造栏加横向翻页；制造台与制造树支持两个以上产物 |
| **作弊开关** | 六个，**默认全关**：建造秒完成／无条件建造／无碰撞／穿墙／发电无间距／平地抽水 |
| **英文本地化** | 新增内容全部配了英文，游戏里切语言即可 |

### 内容是按真实化学与物理推的

这不是风味文案，是每个数值的来源。新物品的属性全部**推导**而来，不是挑出来的：

- 配方配比来自**配平的化学方程式** —— `2 Al₂O₃ + 3 C → 4 Al + 3 CO₂` 就是矿 ×2 + 煤 ×3 → 铝块 ×4 + CO₂ ×3
- `isFluid` 看常温常压下的相态；`fuelType` 看它现实中到底烧不烧
- 热值按**燃烧焓**换算，全表锚在原版煤一个点上（2.7 MJ ↔ 393.5 kJ/mol）
- 用哪台机器看**反应类别**，不看方便：MTO 是脱水不是氧化还原，所以它留在原版化工厂

> 哪种还原剂能炼哪种矿，是按 Ellingham 图判的，不是按平衡感：
> 一氧化碳还原不了氧化铝，乙烯可以——判据是裂解后给不给得出**单质碳**。

每条配方的化学依据都逐条写在特性文档里（[中文](https://github.com/RobertWangWang/dysonsphereprogram-ProjectEden/blob/main/mod%E7%89%B9%E6%80%A7.md) ／ [English](https://github.com/RobertWangWang/dysonsphereprogram-ProjectEden/blob/main/mod_feature.md)）。

---

## 已知取舍

不是 bug，是上面那些改动的必然副作用：

- 采矿机可以叠放，代价是它也能叠进别的建筑里，而且不能再原地重建/替换
- 普通采矿机也能采原油（放行的两处代码指令特征完全相同，没法只放开一处）
- 原版化工厂也能做新增的「原油X射线裂解」（配方按类型生效，不按建筑）
- 传送带直连喂不满巨型建筑 —— 请用它自带的物流站喂料
- 巨型建筑的 30 个储物格看不到也调不了（点开是配方面板，两个面板会互相顶掉）——格位按当前配方自动铺，本来也不用手动配
- 分拣器摆臂速度没有改，改的是集装层数和传送带速度
- 掉落过滤与信号选取窗口画不出第 14 列以后的物品（物品选取窗口有搜索框，不受影响）

## 兼容性

- **创世之书（GenesisBook）**：检测到就自动停用本 mod 的巨型建筑相关功能，避免同一台建筑被两套逻辑重复驱动
- **银河尺度（GalacticScale）**：新矿脉通过兼容层工作；另外顺手修了 GS2 一个会让存档读不出来的问题
- 其余 mod 未系统测试

## 配置

配置是 **JSON 嵌入在 DLL 里**的，装完的包里看不到单独的文件。两种改法：

- **不用编译**：在 profile 里新建 `BepInEx\config\ProjectEden\<同名>.json`，它会盖掉内嵌的那份，重开游戏生效
- **改源码重新编译**：内容类配置（矿种、配方、数值）本来就跟着版本走，一般走这条

> 用了覆盖文件的话，日志里每次都会打一条 WARNING 把路径写出来——
> 一份忘了删的覆盖文件会让之后所有对内嵌 JSON 的修改看起来「没生效」。

## 出问题了

`BepInEx/LogOutput.log` 里搜 `ProjectEden`。

本 mod 的每一处改动都会打一行确认；**匹配失败会打 ERROR 而不是静默失效**，
所以那份日志基本能直接指出是哪一块没生效——反馈问题时请带上它，比截图有用得多。

---

## 许可

**这个仓库不是单一许可的，代码和美术分开。**

### 版权

```
Copyright (C) 2026 RobertWangWang and Project Eden contributors
Copyright (C) 2022-2026 Awbugl and ProjectGenesis contributors
```

本项目基于创世之书（ProjectGenesis）开发，代码部分经其作者授权使用。
**按 GPL 的要求，上游的版权声明完整保留**；哪些文件属于其衍生作品、
哪些只是借鉴思路，逐文件列在 **[NOTICE](NOTICE)** 里，相关文件头也各自写明。

### 代码：GPL-3.0

`ProjectEden/src/`、`ProjectEden.Preloader/`、`tools/` 下的全部源码，以及 `ProjectEden/data/` 下的
JSON 配置，采用 **[GNU GPL v3.0](LICENSE)**。

### 美术：仅授权用于《戴森球计划》的 mod

`ProjectEden/assets/icons/` 下的全部 41 张图标（均由 `tools/make_icons.py` 生成），
以及运行时程序化生成的建筑几何与贴图（`ProjectEden/src/Model/`），授权范围是：

> **可以自由用在任何《戴森球计划》的 mod 里**，包括修改与再分发。
> **不授权用于本游戏之外的任何用途。**

> ⚠️ **这条限制和 GPL-3.0 是有张力的，下游需要知道。**
> GPL 要求整体可被任何人自由再分发和修改、不附加额外限制；
> 「仅限某个游戏的 mod」是一条额外限制。两者可以并存——代码归代码、美术归美术——
> 但这意味着**打包后的整体不是纯 GPL，在 FSF 的口径下属于非自由软件**。
> 引用本项目时请分别标注，不要笼统写成「GPL 项目」。

### 全部美术均为自绘

`assets/icons/` 下的 **41 张图标全部由 `tools/make_icons.py` 生成**（矢量源码在仓库里），
建筑的外形与贴图由 `ProjectEden/src/Model/` 在运行时程序化生成。

**本仓库不含任何第三方美术资源。**

> 六座巨型建筑的图标刻意做成和它们的 3D 模型同一个轮廓——
> 建造栏里认出来的，就是地上那一座。

## 关于创世之书

**本项目是在《创世之书》（GenesisBook）的基础上做起来的。** 这里的大部分非平凡机制都参考了它的实现，
遇到问题时第一件事就是去看它当初怎么解决同一个问题的。

- **代码部分已获得创世之书作者授权。** 在此致谢。
- **美术资源未获授权，也没有使用**——本仓库的图标与模型全部自绘，见上一节。

**反过来也一样：创世之书，以及任何其他《戴森球计划》的 mod，都可以自由 fork 本仓库。**
代码遵循 GPL-3.0 的条款；自绘美术按上面那条授权，用在 DSP mod 里不需要另行取得许可。

## 致谢

- **[创世之书 / GenesisBook](https://github.com/Awbugl/ProjectGenesis)**（作者 Awbugl）—— 本项目的起点。
- **[CheatEnabler / UXAssist](https://github.com/soarqin/DSP_Mods)**（作者 Soar Qin，MIT） ——
  作弊开关的设计参考了 CheatEnabler；英文本地化的做法参考了 `UXAssist.Common.I18N`
  （往 `Localization` 的字符串表里注册键，并在语言加载后补写）。

  > **是参考不是照搬，两处实现都走了另一条路**，理由写在特性文档里：
  > 「无间距」CheatEnabler 是把判定里的常量全局换掉，而本作版本里那个常量有三处是炮塔间距，
  > 一律替换会把炮塔规则一起废掉，所以本 mod 改成事后擦除建造预览的拒绝条件；
  > 本地化则没有用 CommonAPI 的 `RegisterString`，因为它对已存在的键照写不误，
  > 会覆盖掉原版的翻译，本 mod 自己加了一道防撞检查。

---

## 参与开发

- **[部署.md](https://github.com/RobertWangWang/dysonsphereprogram-ProjectEden/blob/main/%E9%83%A8%E7%BD%B2.md)** —— 装给别人用怎么装、发新版怎么打包（含 preloader 的离线校验流程）
- **[CLAUDE.md](https://github.com/RobertWangWang/dysonsphereprogram-ProjectEden/blob/main/CLAUDE.md)** —— 架构说明与踩过的坑。读 IL 胜过猜，这份文档里的每一条机制都是这么来的

```bash
dotnet build                                   # 编译并部署插件到 profile
dotnet build ProjectEden.Preloader/ProjectEden.Preloader.csproj -c Release
powershell -ExecutionPolicy Bypass -File tools\verify_preloader.ps1 -Config Release
python tools\pack_release.py                   # 打发布包
```

> **preloader 改动后必须跑 `verify_preloader.ps1`。** 它在游戏程序集交给 CLR 之前重写它，
> 写错的表现是游戏启动失败、报一个不指向本仓库任何代码的 CLR 类型加载错误——
> 到那一步，平时那套「读 IL、报匹配数、匹配为 0 就大声失败」的办法一个都用不上。
