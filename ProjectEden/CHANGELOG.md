# 更新日志

## 1.12.15

### 跟上游戏 0.10.35：修好被更新打断的两个功能，并给它们补上离线检查器

0.10.35 把两处 IL 挪了位置。**两个功能都是大声失败的**——日志里写着
「应当改写 7 处，实际 5 处」和「应为 1 处，实际 0 处」——所以没有变成静默的错。
但它们都是**在游戏里才发现的**，因为这两个功能没有离线检查器；
而这次更新里**带检查器的功能一个都没断**。这一条现在补上了。

#### 一、地基基准面：方法被拆开了，一行改名

`PlanetData.UpdateDirtyMesh` 里那段 `GetModPlane` 被拆进了新的
`UpdateDirtyMeshVertices`（两个方法都还在、签名都是 `(int dirtyIdx)`）。
补丁还指着旧名字，于是**三个消费方只改写了两个**，后果是
**放大星球上任何带地基的格子都会把地面拉到 200.2**——当年查了七轮的那个
「出生点是个洞」。改了一个方法名。

#### 二、活性透镜：原版把催化剂做成了数据，于是**删掉**四个转译器

这一条不是修锚点，是整个换设计——而且是原版现在自己的意图。

旧版那个 `cata = 2 × (1 + inc)` 里的字面量 `2`，硬写在三个方法里
（`EnergyCap_Gamma_Req` / `MaxOutputCurrent_Gamma` / `RequiresCurrent_Gamma`），
这正是本 mod 必须同时改三处的原因。0.10.35 把它换成了**和燃料同构的通用系统**：

```
ItemProto.CatalystType          位掩码，对应 PrefabDesc.powerCatalystMask
ItemProto.catalystNeeds         白名单数组，和 fuelNeeds / turretNeeds 同族
ItemProto.catalystAbilityById   倍率表：InitCatalystAbilityById 建，值 = Ability × 0.01
```

于是：

- **倍率只剩一份数据**，`×5` 就是 `Ability = 500`。三处 `cata` 转译器**删除**。
- **插入也走白名单了**（`EntityFastFillIn` 现在查 `catalystNeeds[catalystMask]`），
  所以那条「按 catalystId 点名要货」的转译器也**删除**——它不是坏了，是不需要了。
- `CatalystType` **抄原版引力透镜的，不写死**：它要和 `powerCatalystMask` 对位，
  两者都在 `resources.assets` 里，离线读不到，猜错的表现是「放不进接收站」且不报错。
- **两张表必须重跑**（陷阱 4b）：`InitCatalystNeeds` 在 `VFPreload.PreloadThread` @08AB、
  `InitCatalystAbilityById` 在 @08B0，LDBTool 两个都不会重跑。不重跑的话本 mod 的透镜
  根本不在表里，倍率读出来是 **0**——**比 1 还糟，接收站直接不发电**，而且不报错。

净结果：转译站点 **7 → 3**（只剩光子分母一处、传送带取货口两处），功能不变，更结实。

> **倍率的基数差点写错，值得记一笔。** 旧转译器是把硬写的 `2` **乘上** `powerMultiplier`，
> 所以 `×5` 的配置意味着最终 `cata = 10`。改写时顺手写成了 `Ability = powerMul × 100`，
> 那是 `cata = 5`——**悄悄砍掉一半，而且不会报错**。露馅的地方只有两份指南里那张
> 「发电 ×2 / ×10」的对照表，是更新文档这一趟才对出来的。现在基数取**原版透镜自己的
> `Ability`**（它在 `resources.assets` 里，写死就是猜），并且开机日志会把
> **相对倍率**打出来、和配置对不上就 WARNING——绝对值自己看不出对错，要两个数相除才知道。

#### 三、两个新检查器

```
tools/check_lens.ps1       13 条：数据表成员、Ability × 0.01 的算式、CatalystType 闸、
                           两个构建器仍在 preload 期（所以重跑仍是必需的）、
                           三个发电方法确实读倍率表、以及剩下那三处转译锚点
tools/check_modplane.ps1    9 条：GetModPlane 仍返回 Int16（所以要改消费方而不是它自己）、
                           20020 字面量还在、三个消费方一个不多一个不少且都跟着 conv.r4、
                           三个目标签名都能唯一解析
```

**`check_modplane.ps1` 做过自测**：把目标名改回 `UpdateDirtyMesh` 之后它确实 FAIL，
并且直接点名「有一个没被覆盖的新消费方 `UpdateDirtyMeshVertices`」。
一个没被验证过的守卫只是个主张。

> 两个检查器第一次跑各自都报了自己的假 FAIL（`powerProductHeat` 写错成了 `PrefabDesc`
> 上那个同名字段；`PickFrom` 四处里只有两处是催化剂口）。**先确认失败是真的，
> 再读检查器的结论**——本仓库这一条这次又验证了一遍。

### 顺手：运输船泊位补齐补上状态行

上一版那条补齐只在「真补了东西」时才出声，于是日志里没有它时分不开
「这颗星球没有站需要补」和「这条补丁压根没跑到」。现在两种情况各有一行。
这是 `StackedRenderPatches` 那次的教训：**针对一个你自己触发不了的场景做修复时，
要记录那个场景本身，而不只是记录修复**。

---

这个文件只写**当前版本**。往期更新日志在仓库的 `ProjectEden/CHANGELOG-history.md`：
<https://github.com/RobertWangWang/dysonsphereprogram-ProjectEden/blob/main/ProjectEden/CHANGELOG-history.md>
