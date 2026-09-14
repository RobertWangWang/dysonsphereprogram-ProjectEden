using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 四条建造类作弊：无条件建造、无碰撞、发电建筑无间距、平地抽水。
    ///
    /// <b>四条共用一个实现，因为它们是同一件事</b>：<c>CheckBuildConditions</c> 把判定结果
    /// 写在每个 <c>BuildPreview.condition</c> 上，最后再汇总成返回值。所以后置遍历一遍预览、
    /// 把该放行的条件擦成 <c>Ok</c>、再按擦完的结果重算返回值即可，一条 IL 都不用写。
    ///
    /// <b>为什么不照抄 CheatEnabler 的转译。</b> 它的做法是把常量 110.25f / 144f 全局换成 1f。
    /// 在本作版本里 <c>BuildTool_BlueprintPaste.CheckBuildConditions</c> 的
    /// <b>110.25f 一共出现 7 次，其中 3 次是炮塔间距</b>（<c>TurretComponent</c> 那几段），
    /// 一律替换会把炮塔的规则一起废掉——CLAUDE.md 第 2 号坑的标准形状。
    /// 按 condition 擦除天然分得开：炮塔那几处根本不写 condition。
    ///
    /// 原版的三档发电间距（平方值，从 IL 读出）：
    /// <code>
    ///   required = desc.geothermal      ? 144f       // 12 米，地热发电站
    ///            : desc.windForcedPower ? 110.25f    // 10.5 米，风力涡轮机
    ///                                   : 12.25f;    // 3.5 米，任意两台发电建筑
    /// </code>
    /// 对应 <c>PowerTooClose(5)</c> / <c>WindTooClose(6)</c> / <c>GeothermalTooClose(7)</c>。
    /// 太阳能板吃的是第三档那条通用规则，所以「风力 + 太阳能」这个诉求落在同一个开关里。
    ///
    /// <b>「无碰撞」也必须在这里擦一刀，光关碰撞体对象池不够。</b> 这是第一版漏掉的：
    /// <see cref="NoCollisionPatches"/> 关掉的是 Unity 侧的碰撞体，而建造判定走的是
    /// <c>Physics.OverlapBoxNonAlloc</c> → <c>PlanetPhysics.GetColliderData</c> 那条链，
    /// 中途还会撞上别的建造预览自己的模型（layer 18 的 <c>BuildPreviewModel</c>）——
    /// 池子关了照样能走到 <c>Collide(34)</c>。<c>Collide</c> 在五个工具里都写，
    /// 而且和 <c>coverObjId</c> 是<b>同一段循环</b>写出来的，所以两样必须一起清。
    /// 这也是本仓库早就踩过的坑：见 <see cref="MinerBuildRulePatches"/> 的诊断结论。
    ///
    /// 挂钩挂在五个建造工具上。<b>优先级压到最后</b>，让
    /// <see cref="MinerBuildRulePatches"/> 的后置先跑完再动返回值，两边的结论才不会打架。
    /// </summary>
    [HarmonyPatch]
    internal static class BuildConditionCheatPatches
    {
        private static CheatsConfig Config => ProjectEdenPlugin.CheatsConfig;

        private static bool Any =>
            Config != null && Config.enabled &&
            (Config.noConditionBuild || Config.noCollision || Config.powerNoSpacing || Config.waterPumpAnywhere);

        /// <summary>每种被放行的条件只报一次。EBuildCondition 最大 202，开 256 够用且零分配</summary>
        private static readonly bool[] Reported = new bool[256];

        private static int _logHook;

        // ── 挂钩：五个建造工具 ────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(BuildTool_Click), nameof(BuildTool_Click.CheckBuildConditions))]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
        [HarmonyPatch(typeof(BuildTool_Path), nameof(BuildTool_Path.CheckBuildConditions))]
        [HarmonyPatch(typeof(BuildTool_Addon), nameof(BuildTool_Addon.CheckBuildConditions))]
        [HarmonyPatch(typeof(BuildTool_Inserter), nameof(BuildTool_Inserter.CheckBuildConditions))]
        private static void CheckBuildConditions(BuildTool __instance, ref bool __result) => Relax(__instance, ref __result);

        // ── 主体 ──────────────────────────────────────────────

        /// <summary>
        /// 拿着建筑时<b>每帧</b>都会跑，不能有任何分配：下标遍历 List、不取枚举器，
        /// 日志一律先抢占标志位再拼字符串（和 MinerBuildRulePatches 同一套规矩）。
        /// </summary>
        private static void Relax(BuildTool tool, ref bool result)
        {
            if (!Any) return;

            List<BuildPreview> previews = tool?.buildPreviews;

            if (previews == null || previews.Count == 0) return;

            if (Interlocked.Exchange(ref _logHook, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"作弊：建造条件放行已生效（无条件建造={Config.noConditionBuild}，无碰撞={Config.noCollision}，" +
                    $"发电无间距={Config.powerNoSpacing}，平地抽水={Config.waterPumpAnywhere}）");

            var cleared = false;
            var allOk = true;

            for (var i = 0; i < previews.Count; i++)
            {
                BuildPreview preview = previews[i];

                if (preview == null) continue;

                if (preview.condition != EBuildCondition.Ok && ShouldClear(preview))
                {
                    ReportOnce(preview.condition);

                    preview.condition = EBuildCondition.Ok;
                    cleared = true;
                }

                // 覆盖/重建标记是 condition 之外的第二道、而且是静默的闸门：
                // CreatePrebuilds 开头 `if (bp.coverObjId != 0) continue;`，
                // 条件明明是 Ok，点下去却什么都不发生。见 MinerBuildRulePatches.UncoverMiner。
                //
                // 它和 Collide 是<b>同一段循环</b>写出来的（IL 0E0B 那圈：拿到碰到的 collider，
                // 查出 objId 就写 coverObjId，查不出就置 collided）。所以放行碰撞的时候
                // 必须连它一起清，只清 condition 的话表现是「不报错了，但点下去还是没反应」。
                // **传送带例外：对 BuildTool_Path 绝不能清覆盖标记。**
                //
                // 对建筑来说 coverObjId 是一道静默闸门（压在别的建筑上 → 点下去没反应），
                // 所以要清。但对传送带来说，它恰恰是<b>「接到这条已有传送带上」的意图本身</b>——
                // 清掉之后每一段都按全新独立传送带建出来，两段挨着却各留一个端帽、不连通。
                // 实测报障：开着无碰撞时传送带没法再接到已有传送带的前半截上。
                //
                // 一开始我以为是无碰撞关掉了碰撞体对象池（那确实会废掉建造工具的射线取点），
                // 但把池子拆成独立开关、恢复之后问题照旧——**真凶是这里**。
                //
                // **而「按工具排除」是错的，这一条是第二次报障换来的。**
                // 原先写的是 `!(tool is BuildTool_Path)`——可蓝图粘贴里的传送带
                // <b>不走 BuildTool_Path</b>，它走 BuildTool_BlueprintPaste。
                // 于是上面那条传送带保护在「框选复制 + 粘贴」这条路上整个失效：
                // 带子各自独立成段，挨着却不连通，而分拣器因此找不到要挂的那条带——
                // 玩家报的原话是「分拣器没有挂到传送带上」，指向的地方和病因隔着两层。
                //
                // 正确的判据是<b>这个预览是什么</b>，不是<b>哪把工具在跑</b>：
                // 对传送带，coverObjId 是「接到这条上去」；对分拣器，它是「替换这台已有的」
                // （CreatePrebuilds IL 016F：coverObjId 非零就不建新 prebuild）。
                // 这两类的连接语义都藏在这个字段里，清掉就是断链。
                ReportConnection(tool, preview);

                if (!IsConnectionCarrier(preview) &&
                    ShouldUncover(preview) &&
                    (preview.coverObjId != 0 || preview.willRemoveCover || preview.willReconstructCover))
                {
                    preview.coverObjId = 0;
                    preview.willRemoveCover = false;
                    preview.willReconstructCover = false;

                    cleared = true;
                }

                if (preview.condition != EBuildCondition.Ok) allOk = false;
            }

            // <b>只抬不压。</b> 返回值只在「确实放行过东西、而且放完之后全部 Ok」时才改成 true，
            // 任何情况下都不把它改回 false。
            //
            // 这不是保守，是必须的：各个工具末尾判返回值时都有一条「不算失败」的豁免
            // （蓝图粘贴放过 NotEnoughItem，点击建造放过 NeedConn，见 MinerBuildRulePatches），
            // 照着 allOk 无脑覆盖会把那条豁免抹掉——粘一张材料不齐的蓝图本来能建一半，
            // 结果一个都建不出来，而且表现是「装了作弊 mod 之后蓝图反而不好使了」。
            if (cleared && allOk) result = true;
        }

        private static bool ShouldClear(BuildPreview preview)
        {
            switch (preview.condition)
            {
                // 「与其他物体碰撞」。<b>这一条才是「建筑能不能重叠」的真正闸门</b>，
                // 关掉碰撞体对象池并不足以绕过它：判定链是
                //   Physics.OverlapBoxNonAlloc(...)                  // IL 0DDF
                //     → PlanetPhysics.GetColliderData(collider, ...) // IL 0E23，查出碰到的是谁
                //     → 查得出 objId 就写 coverObjId，查不出就置 collided
                //     → collided ? condition = Collide               // IL 132F
                // 第一步打不到东西固然不会 collided，但同一段循环里还有 BuildPreviewModel
                // （layer 18，其它预览自己的模型）等别的来源，池子关掉照样能走到 Collide。
                // 所以这一条必须在结论上擦，不能指望上游。
                case EBuildCondition.Collide:
                    if (Config.noCollision) return true;

                    break;

                // 任意两台发电建筑 3.5 米（太阳能板、火力发电厂等走这条）
                case EBuildCondition.PowerTooClose:
                // 风力涡轮机 10.5 米
                case EBuildCondition.WindTooClose:
                // 地热发电站 12 米
                case EBuildCondition.GeothermalTooClose:
                    if (Config.powerNoSpacing) return true;

                    break;

                // 抽水站必须建在水面上
                case EBuildCondition.NeedWater:
                    if (Config.waterPumpAnywhere) return true;

                    break;
            }

            // 兜底：无条件建造把剩下的全放行，采矿机除外
            return Config.noConditionBuild && !IsMiner(preview);
        }

        /// <summary>
        /// 要不要清掉这个预览的覆盖/重建标记。
        ///
        /// 无碰撞一定要清（它和 Collide 是同一段循环写的）；
        /// 无条件建造也要清，但采矿机除外——那一条由 advancedminer.json 的
        /// <c>allowMinerOverlap</c> 单独管，两边都动会分不清是谁放行的。
        /// </summary>
        private static int _connReported;

        /// <summary>
        /// 分拣器/传送带预览拿到的连接信息，打前几条。
        ///
        /// <b>为什么要有这一行。</b> 「分拣器挂没挂上传送带」这件事，
        /// 在日志里<b>一个字都看不到</b>——补丁全都正常接管、零异常，而结果只在屏幕上。
        /// 于是每改一次就得请玩家进一次游戏用眼睛判断，改错了也说不清错在哪一环。
        /// 这条打出来之后，「有没有拿到连接对象」直接可查：
        /// <c>inputObjId</c> / <c>outputObjId</c> 为 0 就是没挂上，非 0 就是挂上了
        /// （负数是本批蓝图里的另一个预览，正数是已经存在的实体）。
        /// </summary>
        private static void ReportConnection(BuildTool tool, BuildPreview preview)
        {
            if (_connReported >= 6 || preview?.desc == null || !preview.desc.isInserter) return;

            _connReported++;

            ProjectEdenPlugin.Log.LogInfo(
                $"无碰撞·分拣器连接 #{_connReported}（{tool?.GetType().Name}）：" +
                $"input={preview.inputObjId} output={preview.outputObjId} " +
                $"cover={preview.coverObjId} 条件={preview.condition}。" +
                "input/output 有一个是 0 就是没挂上；负数表示接的是本批蓝图里的另一个预览。");
        }

        /// <summary>
        /// 这个预览的 <c>coverObjId</c> 是不是<b>连接语义</b>而不是障碍物。
        ///
        /// 传送带：「接到这条已有的带子上」；分拣器：「替换这台已有的分拣器」。
        /// 两者清掉都会断链，而且症状离病因很远——传送带那次表现为「接不上前半截」，
        /// 分拣器这次表现为「挂不到传送带上」。
        ///
        /// <b>按预览的 prefab 判，不按工具判</b>：同一类东西可以由点建、拖拽、
        /// 蓝图粘贴三条路造出来，按工具排除必然漏掉其中一条（实测漏的就是蓝图粘贴）。
        /// </summary>
        private static bool IsConnectionCarrier(BuildPreview preview)
        {
            PrefabDesc desc = preview?.desc;

            return desc != null && (desc.isBelt || desc.isInserter);
        }

        private static bool ShouldUncover(BuildPreview preview)
            => Config.noCollision || (Config.noConditionBuild && !IsMiner(preview));

        /// <summary>
        /// 采矿机不参与「无条件建造」。
        ///
        /// 它的建造判定同时在确认<b>范围内到底有没有矿</b>（NeedResource / NeedSingleResource），
        /// 强行放行会造出一台 <c>veins[]</c> 为空的采矿机——建得出来，永远不产东西。
        /// CheatEnabler 在这一点上的处理是一样的（它的 <c>CheckForMiner</c>）。
        /// 采矿机的重叠放置和采原油走 advancedminer.json，见 <see cref="MinerBuildRulePatches"/>。
        /// </summary>
        private static bool IsMiner(BuildPreview preview)
            => preview.desc != null && (preview.desc.veinMiner || preview.desc.oilMiner);

        // ── 光标上的红字 ──────────────────────────────────────
        //
        // 后置擦 condition 能让建筑<b>建得出来</b>，但光标旁边照样红字提示「无法建造」。
        // 原因是那段文字是 CheckBuildConditions <b>自己在方法末尾写的</b>，在后置之前：
        //
        //     bool ok = true;
        //     foreach (var bp in buildPreviews) {
        //         if (bp.condition == Ok) continue;
        //         if (bp.condition == <本工具容忍的那一条>) continue;
        //         ok = false;
        //         actionBuild.model.cursorState = -1;                  // 红叉
        //         actionBuild.model.cursorText  = bp.conditionText;    // 红字
        //     }
        //     if (ok) { cursorState = 0; cursorText = "点击鼠标建造" + …; /* 还有一大段 */ }
        //
        // 所以只能让<b>这个循环</b>看到 Ok。两条路：
        //   (a) 后置里自己把 cursorState / cursorText 补回去——要复刻 ok 分支那 700 字节 IL
        //       （计数「(3)」、地热强度读数等），抄漏一样就是新 bug；
        //   (b) 把这个循环读 condition 的那一两处换成我们的过滤函数，剩下的全交给原版。
        // 取 (b)：改动是<b>一条指令的 opcode</b>，而且作弊关掉时过滤函数原样返回，完全无副作用。
        //
        // 定位靠 `ldc.i4.m1 ; stfld BuildModel::cursorState` ——这一对在五个工具的
        // CheckBuildConditions 里<b>各只出现一次</b>（数过），就是上面那个 `cursorState = -1`。
        // 从它往回 24 条指令内，凡是形如 `ldfld condition` 且紧跟 `brfalse` 或
        // `ldc.i4.X + beq` 的读取，才是这个守卫；蓝图粘贴里近旁还有
        // `preview.output.condition` 和 AddErrorMessage 的读取，靠这个形状判据正好避开。

        private static readonly FieldInfo ConditionField =
                                             AccessTools.Field(typeof(BuildPreview), nameof(BuildPreview.condition)),
                                         CursorStateField =
                                             AccessTools.Field(typeof(BuildModel), nameof(BuildModel.cursorState));

        private static readonly MethodInfo EffectiveConditionMethod =
            AccessTools.Method(typeof(BuildConditionCheatPatches), nameof(EffectiveCondition));

        /// <summary>
        /// 供 IL 调用，签名必须能顶替 <c>ldfld BuildPreview::condition</c>：
        /// 栈上本来就是那个 BuildPreview，取而代之压回一个 EBuildCondition。
        ///
        /// 作弊没开时原样返回，所以这条转译在默认配置下等价于什么都没改。
        /// </summary>
        internal static EBuildCondition EffectiveCondition(BuildPreview preview)
        {
            if (preview == null) return EBuildCondition.Ok;

            EBuildCondition condition = preview.condition;

            if (condition == EBuildCondition.Ok || !Any) return condition;

            return ShouldClear(preview) ? EBuildCondition.Ok : condition;
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(BuildTool_Click), nameof(BuildTool_Click.CheckBuildConditions))]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
        [HarmonyPatch(typeof(BuildTool_Path), nameof(BuildTool_Path.CheckBuildConditions))]
        [HarmonyPatch(typeof(BuildTool_Addon), nameof(BuildTool_Addon.CheckBuildConditions))]
        [HarmonyPatch(typeof(BuildTool_Inserter), nameof(BuildTool_Inserter.CheckBuildConditions))]
        private static IEnumerable<CodeInstruction> CursorText_Transpiler(IEnumerable<CodeInstruction> instructions,
                                                                          MethodBase original)
        {
            // 先挡住 null：重载/字段解析失败时继续往下走会把方法改烂，而且悄无声息
            if (ConditionField == null || CursorStateField == null || EffectiveConditionMethod == null)
            {
                ProjectEdenPlugin.Log.LogError("解析不到 BuildPreview.condition / BuildModel.cursorState，光标提示仍会显示拒绝原因");

                return instructions;
            }

            var code = new List<CodeInstruction>(instructions);

            var anchor = -1;

            for (var i = 1; i < code.Count; i++)
                if (code[i].opcode == OpCodes.Stfld && CursorStateField.Equals(code[i].operand) &&
                    code[i - 1].opcode == OpCodes.Ldc_I4_M1)
                {
                    anchor = i;

                    break;
                }

            string where = original.DeclaringType?.Name + ".CheckBuildConditions";

            if (anchor < 0)
            {
                ProjectEdenPlugin.Log.LogError($"{where} 找不到 cursorState = -1 的位置，光标提示仍会显示拒绝原因");

                return code;
            }

            const int window = 24;

            var replaced = 0;

            for (int i = anchor - 1; i >= 0 && i >= anchor - window; i--)
            {
                if (code[i].opcode != OpCodes.Ldfld || !ConditionField.Equals(code[i].operand)) continue;
                if (!IsGuardRead(code, i)) continue;

                // 原地改 opcode，保留可能落在这条上的跳转标签
                code[i].opcode = OpCodes.Call;
                code[i].operand = EffectiveConditionMethod;

                replaced++;
            }

            if (replaced == 0)
                ProjectEdenPlugin.Log.LogError($"{where} 没找到汇总循环里的 condition 判定，光标提示仍会显示拒绝原因");
            else
                ProjectEdenPlugin.Log.LogInfo($"{where}：光标提示的条件判定已接管 {replaced} 处");

            return code;
        }

        /// <summary>
        /// 这处 <c>ldfld condition</c> 是不是汇总循环的守卫。
        /// 形状只有两种：<c>… brfalse</c>（等于 Ok 就跳过），或 <c>… ldc.i4.X ; beq</c>（本工具容忍的那一条）。
        /// </summary>
        private static bool IsGuardRead(List<CodeInstruction> code, int index)
        {
            if (index + 1 >= code.Count) return false;

            CodeInstruction next = code[index + 1];

            if (next.opcode == OpCodes.Brfalse || next.opcode == OpCodes.Brfalse_S) return true;

            if (index + 2 >= code.Count) return false;
            if (!next.opcode.Name.StartsWith("ldc.i4")) return false;

            OpCode after = code[index + 2].opcode;

            return after == OpCodes.Beq || after == OpCodes.Beq_S;
        }

        /// <summary>把放行掉的条件各报一次，好在日志里对上「到底放开了什么」。</summary>
        private static void ReportOnce(EBuildCondition condition)
        {
            var code = (int)condition;

            if ((uint)code >= 256 || Reported[code]) return;

            Reported[code] = true;

            ProjectEdenPlugin.Log.LogInfo($"作弊：已放行建造条件 {condition}（{code}）");
        }
    }
}
