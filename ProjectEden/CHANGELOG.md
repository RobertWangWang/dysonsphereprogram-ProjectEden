# 更新日志

## 1.12.13

### 堆叠粘贴时压在已有传送带上的带子不再凭空消失

报障：「蓝图粘贴，堆叠建造模式下，传送带**可能**会出现不建造的情况」。
**「可能」正是这条的指纹**——同一张蓝图里有的带子建得出来、有的建不出来。

**原版对「压在已有带子上的带子」有两条路，不是一条。**
`BuildTool_BlueprintPaste.CreatePrebuilds`：

```
012F: if (bp.coverObjId > 0            // 我盖住了一条已有实体
       && bp.desc.isBelt               // 我是传送带
       && bp.output != null            // 我有下游
       && bp.output.desc.isBelt        // 下游也是传送带
       && bp.output.coverObjId > 0     // ← 下游也压着东西
       && bp.output.condition == Ok)   // ← 而且下游能建
       bpIdWitchWillRebuildCoverBelt[cursor++] = i;   // 登记「要重建」

0196: if (bp.coverObjId != 0) continue;   // ← 否则到此为止
```

登记上的会在 **@07AB 另一条循环**里真正建成 `PrebuildData`；
**没登记上的就在 @019C 被静默丢掉**——不报错、不红字、什么都不发生。

**四个条件在下游那条带子身上，所以链一断就逐段消失**：链尾没有下游、
下游接的是建筑不是带子、下游伸出了老带子的范围（那一段 `coverObjId == 0`）——
任意一条都让这一段整个不建。正常粘贴多半整链成立，堆叠模式下断口远比平时多。

> **这一半是本仓库自己挖的。** 无碰撞开着时 `BuildConditionCheatPatches` 会清掉
> 预览的 `coverObjId`，**正是为了让建筑能叠**；但它对传送带和分拣器例外
> （`IsConnectionCarrier`），因为对那两类来说这个字段是「接到这条上去」的意图本身，
> 清掉会把带子断成互不连通的独立段——那是两次报障换来的。
> 于是缺口就在这里：**无碰撞让建筑能叠，却把带子留在原版那套「要么整链重建、
> 要么整段丢弃」的规则里**。又是那个形状——*本仓库为某个功能加的规则，
> 悄悄限制了后来加的另一个功能*——而这次**两边都是我们自己的**。

**修法只碰原版确定会丢掉的那些**：把那六个条件逐项复现，只对
「`coverObjId != 0` 且**没有**进重建名单」的带子清 `coverObjId`，让它建成新的一条。
会走重建路径的**一根手指都不碰**——那条路是连接复用，正是要保住的东西。

> **判据必须是复现而不是近似**，这是钻头那条教训：*一个在原版做决定之前跑的钩子，
> 必须复现那个决定，而不是它的名义形状*。少一项会碰到本该重建的带子，
> 多一项会漏掉本该救的。新增 `tools/check_bp_coverbelt.ps1` 离线断言这套锚点，
> 实测把 gate 的字段序列原样读了出来：
> `coverObjId, desc.isBelt, output, output.desc.isBelt, output.coverObjId, output.condition`。

> **代价说在前面**：粘到已有带子上时，原版的「复用老带子、不建新的」在这种情况下
> 会变成「叠一条新的上去」。这在堆叠模式下正是玩家要的，所以开关跟着**无碰撞**走；
> 无碰撞关着时这里整个不生效，行为和原版一模一样。

**顺带修掉探针的一处误报。** 它把 `coverObjId != 0` 一律报成「跳过了」，
可带子有第二条路——于是一条**建得好好的**带子也会被报成被拦。
**一个分不清「被拦」和「走了另一条路」的探针，测的不是它要测的东西。**
现在它区分两者，并在带子被拦时逐项打出断在哪一项（没有下游 / 下游不是带子 /
下游没压着东西 / 下游自己建不了）。

> 写这个检查器时它自己先报了三个 FAIL，而**三个都是检查器的 bug**：
> `bpIdWitchWillRebuildCoverBelt` 被读了**三**次不是两次（多出来的那次是
> `if (arr == null) arr = new int[...]` 的惰性初始化），而固定字节窗口把上一段
> `addonType` 分支的尾巴一起圈了进来。改成**按用途分类**（后面跟 `stelem` 是登记、
> 跟 `ldelem` 是消费、跟同名 `stfld` 是初始化）并把窗口锚在 gate 自己的第一条
> `coverObjId` 读上。**先确认失败是真的，再读检查器的沉默。**

---

这个文件只写**当前版本**。往期更新日志在仓库的 `ProjectEden/CHANGELOG-history.md`：
<https://github.com/RobertWangWang/dysonsphereprogram-ProjectEden/blob/main/ProjectEden/CHANGELOG-history.md>
