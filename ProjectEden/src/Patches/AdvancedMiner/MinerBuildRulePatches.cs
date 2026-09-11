using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using ProjectEden.Utils;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 取消大型采矿机的建造间距限制，外加一个「到底是哪条判定拦的」的诊断。
    ///
    /// 先说调查结论，免得下次再挖一遍：<b>原版并没有「两台大型采矿机不能建在同一位置」这条规则。</b>
    /// BuildTool_Click.CheckBuildConditions 里针对物流站的那段间距循环，开头就是
    ///     if (desc.isVeinCollector &amp;&amp; station.isVeinCollector) continue;
    /// 采矿机之间互相跳过；采矿机自己那条路（MinerComponent.IsTargetVeinInRange 附近）
    /// 也只判「范围内有没有矿」(NeedResource) 和「是不是单一矿种」(NeedSingleResource)，
    /// 没有任何「这片矿已被别人占了」的检查。
    ///
    /// 会挡路的是同一段循环算出来的两个结果：
    ///     limit = desc.isVeinCollector ? 625 : 225;              // 25 米 / 15 米，平方值
    ///     if (station.isStellar || desc.isStellarStation) limit = desc.isCollectStation ? 14297 : 841;
    ///     if (sqrDist &lt; limit)
    ///         condition = station.isVeinCollector ? MK2MinerTooClose : TowerTooClose;
    /// 这两条已经放行。但实测放置时它们一次都没触发，说明玩家遇到的提示来自<b>别的条件</b>——
    /// 提示文案对不上 key 是因为 BuildPreview.GetConditionText 返回的是中文 key
    /// （MK2MinerTooClose 的 key 是「距离大型采矿机太近」），再由 LDB.strings 映射成
    /// 措辞不同的显示文案，所以在 DLL 里按提示原文根本搜不到。
    ///
    /// 所以这里加了 ReportCondition：任何一次建造被拒都把真实的 EBuildCondition 打进日志，
    /// 每种只报一次。拿到条件号再决定放行哪一条，比continue猜下去快得多。
    ///
    /// 做法用后置擦 BuildPreview.condition 而不是转译：CheckBuildConditions 在几个工具里
    /// 有 618 ~ 10356 条指令，往里塞 IL 风险太高，而判定结果最终都写在 condition 上。
    /// </summary>
    [HarmonyPatch]
    internal static class MinerBuildRulePatches
    {
        private static AdvancedMinerConfig Config => ProjectEdenPlugin.MinerConfig;

        private const EBuildCondition Ok = EBuildCondition.Ok;

        /// <summary>
        /// 各工具末尾判定返回值时「不算失败」的那一个条件，逐个从 IL 读出来的：
        /// 点击建造放过 NeedConn(28)，蓝图粘贴放过 NotEnoughItem(2)，铺设类一个都不放过。
        /// BuildTool_Addon / BuildTool_Inserter 的返回值不经局部变量，没法照此确认，
        /// 而且采矿机也不走那两条路，就不碰了。
        /// </summary>
        private const EBuildCondition ClickTolerated = EBuildCondition.NeedConn,
                                      PasteTolerated = EBuildCondition.NotEnoughItem,
                                      PathTolerated  = EBuildCondition.Ok;

        /// <summary>每种条件只报一次。EBuildCondition 最大 202，开 256 够用，且零分配。</summary>
        private static readonly bool[] Reported = new bool[256];

        private static int _hookLogged, _logMk2, _logTower, _logCollide, _logCheckBox, _logUncover, _logBuilt, _logOilVein;

        // ── 挂钩 ──────────────────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_Click), nameof(BuildTool_Click.CheckBuildConditions))]
        private static void BuildTool_Click_CheckBuildConditions(BuildTool __instance, ref bool __result)
            => Relax(__instance, ref __result, ClickTolerated);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
        private static void BuildTool_BlueprintPaste_CheckBuildConditions(BuildTool __instance, ref bool __result)
            => Relax(__instance, ref __result, PasteTolerated);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_Path), nameof(BuildTool_Path.CheckBuildConditions))]
        private static void BuildTool_Path_CheckBuildConditions(BuildTool __instance, ref bool __result)
            => Relax(__instance, ref __result, PathTolerated);

        // ── 主体 ──────────────────────────────────────────────

        /// <summary>
        /// 拿着建筑时每帧都会跑，不能有任何分配：下标遍历 List、不取枚举器，
        /// 日志一律先抢占标志位再拼字符串。
        /// </summary>
        private static void Relax(BuildTool tool, ref bool result, EBuildCondition tolerated)
        {
            if (Config == null || !Config.removeBuildDistanceLimit) return;

            List<BuildPreview> previews = tool?.buildPreviews;

            if (previews == null || previews.Count == 0) return;

            if (Interlocked.Exchange(ref _hookLogged, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo("建造条件检查已接管；此后每种「建造被拒」的条件会各报一次");

            var cleared = false;
            var allOk = true;

            for (var i = 0; i < previews.Count; i++)
            {
                BuildPreview preview = previews[i];

                if (preview == null) continue;

                if (ShouldClear(preview))
                {
                    preview.condition = Ok;
                    cleared = true;
                }

                UncoverMiner(preview);

                if (preview.condition == Ok || preview.condition == tolerated) continue;

                allOk = false;

                ReportCondition(preview);
            }

            // 只在真的放行过东西时才动返回值，其余情况保持原样
            if (cleared) result = allOk;
        }

        /// <summary>正在放置的是不是大型采矿机。</summary>
        private static bool IsMiner(BuildPreview preview)
            => preview.desc != null && (preview.desc.veinMiner || preview.desc.isVeinCollector);

        /// <summary>
        /// 拆掉「覆盖 / 重建」标记——这是 condition 之外的<b>第二道、而且是静默的</b>闸门。
        ///
        /// BuildTool_Click.CreatePrebuilds 开头是：
        ///     if (bp.condition != Ok)  continue;
        ///     if (bp.coverObjId != 0)  continue;      ← 不建，也不弹任何提示
        /// 而 coverObjId 恰恰是在那段碰撞循环里、和 Collide 同一处代码写进去的：预览压在
        /// 一个「可被覆盖」的建筑上时，游戏把它当成原地重建，于是 condition 反而是 Ok，
        /// 点下去却什么都不发生。
        ///
        /// 对采矿机清掉这三个字段，落点上无论压着什么都一律走「新建」。
        /// 代价是采矿机没法再原地重建/替换了——但那正是「同一个点摞多台」要的效果。
        /// </summary>
        private static void UncoverMiner(BuildPreview preview)
        {
            if (Config == null || !Config.allowMinerOverlap) return;
            if (preview.coverObjId == 0 && !preview.willRemoveCover && !preview.willReconstructCover) return;
            if (!IsMiner(preview)) return;

            preview.coverObjId = 0;
            preview.willRemoveCover = false;
            preview.willReconstructCover = false;

            if (Interlocked.Exchange(ref _logUncover, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo("已清除大型采矿机预览的覆盖标记（coverObjId），落点一律按新建处理");
        }

        /// <summary>
        /// 确认点击真的产出了预建。CreatePrebuilds 成功后会把 objId 写回预览，
        /// 所以这里能一锤定音地区分「被拦下了」和「建出来了」。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildTool_Click), nameof(BuildTool_Click.CreatePrebuilds))]
        private static void BuildTool_Click_CreatePrebuilds(BuildTool __instance)
        {
            if (Config == null || !Config.allowMinerOverlap) return;
            if (_logBuilt != 0) return;

            List<BuildPreview> previews = __instance?.buildPreviews;

            if (previews == null) return;

            for (var i = 0; i < previews.Count; i++)
            {
                BuildPreview preview = previews[i];

                if (preview == null || preview.objId == 0 || !IsMiner(preview)) continue;

                if (Interlocked.Exchange(ref _logBuilt, 1) == 0)
                    ProjectEdenPlugin.Log.LogInfo($"大型采矿机预建已创建：objId={preview.objId}");

                return;
            }
        }

        // ── 让采矿机能采原油涌泉 ──────────────────────────────
        //
        // 原版在建造时收集矿脉的两个循环里都写着：
        //     if (veinPool[id].type == EVeinType.Oil) continue;
        // 把油井排除在外，于是大型采矿机盖在油井上只会得到 NeedResource。
        //
        // 这是<b>唯一</b>的卡点，后面全程通用，已逐段核对过：
        //   · PlanetFactory.CreateEntityLogicComponents → InitVeinArray 按预建参数建 veins[]，不按类型过滤
        //   · 同一处还有 station.collectionIds[0] = veinPool[miner.veins[0]].productId
        //     ——油井的 productId 就是原油，站点仓位和采集门禁自动对上
        //   · MinerComponent.InternalUpdate 的 Vein 分支只循环 veins[]、读 productId，不看类型
        //   · UpdateVeinCollection 的门禁是 miner.productId == collectionIds[0]，两边同源
        //
        // 做法：把比较用的常量 7 换成一个返回 -1 的调用，让 `type == Oil` 永不成立。
        // 只改 beq 那两处；第三处是 bne.un——那是原油萃取站「必须是油井」的判定，动了就废了。
        //
        // 注意这同时放开了普通采矿机（veinMiner）：两处循环的指令特征完全一样，
        // 靠出现顺序区分太脆，而且普通采矿机采原油同样说得通，就一起放开了。

        private static readonly FieldInfo VeinTypeField = AccessTools.Field(typeof(VeinData), nameof(VeinData.type));

        /// <summary>供 IL 调用：返回参与 `type == ?` 比较的常量。-1 表示永不相等。</summary>
        internal static int OilVeinTypeGate()
        {
            if (Config == null || !Config.allowMinerOnOil) return (int)EVeinType.Oil;

            if (Interlocked.Exchange(ref _logOilVein, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo("采矿机建造时不再排除原油涌泉");

            return -1;
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(BuildTool_Click), nameof(BuildTool_Click.CheckBuildConditions))]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
        private static IEnumerable<CodeInstruction> OilVein_Transpiler(IEnumerable<CodeInstruction> instructions,
                                                                      MethodBase original)
        {
            if (VeinTypeField == null)
            {
                ProjectEdenPlugin.Log.LogError("解析不到 VeinData.type，采矿机仍无法采集原油");

                return instructions;
            }

            var matcher = new CodeMatcher(instructions);
            var replaced = 0;

            while (true)
            {
                matcher.MatchForward(false,
                    new CodeMatch(OpCodes.Ldfld, VeinTypeField),
                    new CodeMatch(OpCodes.Ldc_I4_7),
                    new CodeMatch(i => i.opcode == OpCodes.Beq || i.opcode == OpCodes.Beq_S));

                if (matcher.IsInvalid) break;

                // 原地改 opcode，保留可能落在这条上的跳转标签
                CodeInstruction constant = matcher.Advance(1).Instruction;

                constant.opcode = OpCodes.Call;
                constant.operand = OilVeinGateMethod;

                matcher.Advance(1);

                replaced++;
            }

            if (replaced == 0)
                ProjectEdenPlugin.Log.LogError(
                    $"{original.DeclaringType?.Name}.CheckBuildConditions 没找到原油涌泉的排除判定，采矿机仍无法采原油");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"{original.DeclaringType?.Name}.CheckBuildConditions：已放开原油涌泉的排除判定 {replaced} 处");

            return matcher.InstructionEnumeration();
        }

        private static bool ShouldClear(BuildPreview preview)
        {
            switch (preview.condition)
            {
                // 「别的建筑离已有的大型采矿机太近」。这个分支只在对方是采矿机时才会写，
                // 所以不需要再判一次。
                case EBuildCondition.MK2MinerTooClose:
                    if (Interlocked.Exchange(ref _logMk2, 1) == 0)
                        ProjectEdenPlugin.Log.LogInfo("已放行建造间距限制：MK2MinerTooClose（距离大型采矿机太近）");

                    return true;

                // 「运输站相隔太近」。只在正在放的是采矿机时放行，
                // 普通物流站之间的间距规则保持原样。
                case EBuildCondition.TowerTooClose:
                    if (preview.desc == null || !preview.desc.isVeinCollector) return false;

                    if (Interlocked.Exchange(ref _logTower, 1) == 0)
                        ProjectEdenPlugin.Log.LogInfo("已放行建造间距限制：TowerTooClose（运输站相隔太近）");

                    return true;

                // 「与其他物体碰撞」——诊断实测就是它拦住了重叠放置的大型采矿机，
                // 提示文案「无法与其他大型采矿站建造在同一个位置」对应的正是这一条。
                //
                // Collide 是通用条件，四个赋值点里只有一个是采矿机专属的，
                // 所以这里限定「正在放的是采矿机」才放行。<b>副作用</b>：采矿机也能叠进
                // 别的建筑里了——想只允许采矿机之间重叠，光靠 condition 分不出来，
                // 通用碰撞那个循环没有把撞到了谁记在 BuildPreview 上。
                case EBuildCondition.Collide:
                    if (Config == null || !Config.allowMinerOverlap) return false;
                    if (!IsMiner(preview)) return false;

                    if (Interlocked.Exchange(ref _logCollide, 1) == 0)
                        ProjectEdenPlugin.Log.LogInfo("已放行建造碰撞限制：Collide（与其他物体碰撞），仅对大型采矿机");

                    return true;

                default:
                    return false;
            }
        }

        // ── 采矿机专属的碰撞检测 ──────────────────────────────
        //
        // BuildTool_Click.CheckBuildConditions 里：
        //     if (collided) condition = Collide;                       // 通用碰撞
        //     else if (desc.veinMiner &&
        //              Physics.CheckBox(cd.pos, cd.ext, cd.q, 2048, Ignore))
        //         condition = Collide;                                 // 采矿机专属
        // 那个 CheckBox 在整个方法里<b>只出现一次</b>（蓝图粘贴里也只有一次），
        // 而且必然落在 desc.veinMiner 分支内，所以换掉它就等于只关采矿机这一条，
        // 通用碰撞完好无损——比事后擦 condition 精准得多。

        private static readonly MethodInfo VanillaCheckBox =
                                              AccessTools.Method(typeof(Physics), nameof(Physics.CheckBox),
                                                  new[]
                                                  {
                                                      typeof(Vector3), typeof(Vector3), typeof(Quaternion),
                                                      typeof(int), typeof(QueryTriggerInteraction)
                                                  }),
                                          MinerCheckBoxMethod =
                                              AccessTools.Method(typeof(MinerBuildRulePatches), nameof(MinerCheckBox)),
                                          OilVeinGateMethod =
                                              AccessTools.Method(typeof(MinerBuildRulePatches), nameof(OilVeinTypeGate));

        /// <summary>供 IL 调用，签名必须与 Physics.CheckBox 完全一致。</summary>
        internal static bool MinerCheckBox(Vector3 center, Vector3 halfExtents, Quaternion orientation,
                                           int layerMask, QueryTriggerInteraction queryTriggerInteraction)
        {
            if (Config != null && Config.allowMinerOverlap)
            {
                if (Interlocked.Exchange(ref _logCheckBox, 1) == 0)
                    ProjectEdenPlugin.Log.LogInfo("已跳过大型采矿机专属的碰撞检测（Physics.CheckBox layer 2048）");

                return false;
            }

            return Physics.CheckBox(center, halfExtents, orientation, layerMask, queryTriggerInteraction);
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(BuildTool_Click), nameof(BuildTool_Click.CheckBuildConditions))]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), nameof(BuildTool_BlueprintPaste.CheckBuildConditions))]
        private static IEnumerable<CodeInstruction> CheckBuildConditions_Transpiler(IEnumerable<CodeInstruction> instructions,
                                                                                    MethodBase original)
        {
            // 必须先挡住 null：CodeMatch(Call, null) 会匹配<b>任意</b> call 指令，
            // 一旦重载解析失败就会把整个方法改烂，而且悄无声息。
            if (VanillaCheckBox == null || MinerCheckBoxMethod == null)
            {
                ProjectEdenPlugin.Log.LogError("解析不到 Physics.CheckBox，采矿机碰撞检测未接管");

                return instructions;
            }

            var matcher = new CodeMatcher(instructions);
            var replaced = 0;

            while (true)
            {
                matcher.MatchForward(false, new CodeMatch(OpCodes.Call, VanillaCheckBox));

                if (matcher.IsInvalid) break;

                matcher.Set(OpCodes.Call, MinerCheckBoxMethod);
                matcher.Advance(1);

                replaced++;
            }

            if (replaced == 0)
                ProjectEdenPlugin.Log.LogError(
                    $"{original.DeclaringType?.Name}.CheckBuildConditions 没找到采矿机的 Physics.CheckBox，重叠放置可能仍被拦");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"{original.DeclaringType?.Name}.CheckBuildConditions：已接管采矿机碰撞检测 {replaced} 处");

            return matcher.InstructionEnumeration();
        }

        /// <summary>
        /// 把真实的拒绝条件打进日志，每种只报一次。
        /// 提示文案和 EBuildCondition 的 key 对不上，只能靠这个把两者对起来。
        /// </summary>
        private static void ReportCondition(BuildPreview preview)
        {
            var code = (int)preview.condition;

            if ((uint)code >= 256 || Reported[code]) return;

            Reported[code] = true;

            ItemProto item = preview.item;

            ProjectEdenPlugin.Log.LogInfo(
                $"建造被拒：{preview.condition}（{code}），建筑「{(item != null ? item.name : "?")}」" +
                $"，isVeinCollector={preview.desc != null && preview.desc.isVeinCollector}");
        }
    }
}
