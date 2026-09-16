using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 效果层第一刀：<b>建造时把材料的品质截下来，落到这座建筑的耗电上</b>。
    ///
    /// 材料在建造那一刻就被消耗了，之后建筑身上没有任何地方还记得它是用什么料造的——
    /// 品质是一格货的属性，货没了属性就没了。所以只能在扣料那一刻截。
    ///
    /// <b>扣料收敛到七个方法</b>（实测，不是照抄设计稿）：五把建造工具的
    /// <c>CreatePrebuilds</c>、<c>PlayerAction_Build.DoUpgradeObject</c>、
    /// <c>ConstructionModuleComponent.PlaceItems</c>。它们底下都走
    /// <c>StorageComponent.TakeTailItems</c>（背包）和 <c>Player.UseHandItems</c>（手上那一摞）。
    ///
    /// <b>而这两条路只有一条带得动品质，这一点设计稿写错了，实测纠正：</b>
    /// <list type="bullet">
    /// <item><c>TakeTailItems</c> 两个重载都<b>写 Q0</b>——品质原样吐回来，白拿；</item>
    /// <item><c>Player.UseHandItems</c> <b>一个寄存器都不写</b>，而且
    /// <c>Player.inhandItemInc</c> 压根没有孪生字段。<b>手上那一摞货没有品质槽位</b>，
    /// 品质在货进手的那一刻就已经没了。</item>
    /// </list>
    /// 所以手上那部分按 <b>0 分</b>计入平均。这是<b>有界的失败方向</b>——只会把品质算低，
    /// 绝不会凭空发明，和忘传一个参数的后果一致。要根治就得给 <c>Player</c> 加一个孪生
    /// 字段并孪生 <c>UseHandItems</c>，那是 preloader 的活，不在这一刀里。
    /// 一次性日志会把两条路各占多少件打出来，好判断值不值得去补。
    ///
    /// <b>效果只挂耗电这一条轴（顶尖 −30%，按品质线性插值）。</b>
    /// 几乎每座建筑都有 <c>PowerConsumerComponent</c>，所以「用好料造的建筑更省电」
    /// 一处实现就覆盖全部。本职轴（制造速度、采矿速度、炮塔伤害……）每类建筑一条，
    /// 是后面的事。
    ///
    /// <b>耗电<u>只在建造那一刻写一次</u>，绝不每 tick 强制。</b> 物流站面板的
    /// 「最大充能功率」滑条写的是同一个字段，每 tick 压它滑条就会弹回去——
    /// <c>StationCapacityPatches</c> 为这件事专门写过一条注释。而且这个字段进存档，
    /// 读档时再乘一次就是复利，所以读档路径上什么都不做。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityBuildPatches
    {
        /// <summary>顶尖品质省多少电。幅度是旋钮不是推导：这条轴铺满全图，所以必须小。</summary>
        private const double MaxPowerCut = 0.30;

        // ── 扣料期间的累计 ──────────────────────────────────────

        /// <summary>
        /// 建造调用的嵌套深度。<b>必须有这个闸</b>：<c>TakeTailItems</c> 全游戏有十几个
        /// 调用者（机甲烧燃料、战斗模块补弹、铺地基……），不圈定范围的话，
        /// 烧一根燃料棒也会被算成「建造材料」。
        /// </summary>
        [ThreadStatic] internal static int _depth;

        [ThreadStatic] internal static int _items;
        [ThreadStatic] internal static long _points;
        [ThreadStatic] internal static int _handItems;
        [ThreadStatic] internal static List<(int PlanetId, int PrebuildId)> _prebuilds;

        /// <summary>
        /// 手上那一摞用掉了多少，由 <see cref="QualityHandUsePatches"/> 报进来。
        ///
        /// <b>这里曾经把手上那部分按 0 分计入平均，理由是「手上那一格没有品质槽位」。
        /// 那个理由已经过时，而结论碰巧还对了一阵子——两者都得改。</b>
        /// 手上那一格现在有孪生字段（<c>&lt;inhandItemQua&gt;k__BackingField</c>），
        /// <c>UseHandItems</c> 也确实把品质算了出来，只是**在返回前自己擦掉了**，
        /// 所以从这边看仍然是 0。详见 <see cref="QualityHandUsePatches"/> 的类注释。
        ///
        /// <b>为什么改由那边报进来，而不是在这里再挂一个后置：</b>
        /// 同一个方法上两个后置的先后顺序不保证，而这里要读的正是那边刚修正完的值。
        /// 顺序不保证的依赖，出错时不报错——合并成一处就没有这个问题。
        /// </summary>
        internal static void NoteHandUse(int items, int qua)
        {
            if (_depth <= 0 || items <= 0) return;

            _items += items;
            _handItems += items;
            _points += qua;
        }

        // ── 蓝图桩 → 实体 ───────────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.AddPrebuildData))]
        [HarmonyPatch(new[] { typeof(PrebuildData) })]
        private static void AddPrebuildData_Postfix(PlanetFactory __instance, int __result)
            => RememberPrebuild(__instance, __result);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.AddPrebuildDataWithComponents))]
        private static void AddPrebuildDataWithComponents_Postfix(PlanetFactory __instance, int __result)
            => RememberPrebuild(__instance, __result);

        private static void RememberPrebuild(PlanetFactory factory, int prebuildId)
        {
            if (_depth <= 0 || prebuildId <= 0 || factory?.planet == null) return;

            // [ThreadStatic] 的初始化式只在第一个线程上跑，所以要惰性建。
            if (_prebuilds == null) _prebuilds = new List<(int, int)>();

            _prebuilds.Add((factory.planet.id, prebuildId));
        }

        /// <summary>
        /// 桩变实体的那一刻：把品质搬过去，并<b>立刻把耗电折扣写进去</b>。
        ///
        /// 这里也是唯一该写耗电的时机——<c>PowerConsumerComponent</c> 此时刚从
        /// <c>prefabDesc</c> 抄完那个值，还没有任何人改过它。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.AddEntityDataWithComponents))]
        private static void AddEntityDataWithComponents_Postfix(PlanetFactory __instance, int prebuildId, int __result)
        {
            if (__instance?.planet == null || __result <= 0) return;

            int planetId = __instance.planet.id;

            // 没有桩的路（原地升级、建设机组直接放件）就用本次扣料算出来的那个数
            if (prebuildId > 0)
            {
                if (!QualityBuildStore.Promote(planetId, prebuildId, __result)) return;
            }
            else
            {
                int perItem = _depth > 0 && _items > 0 ? (int)(_points / _items) : 0;

                if (perItem <= 0) return;

                QualityBuildStore.Set(planetId, __result, perItem);
            }

            ApplyPowerCut(__instance, __result, planetId);
        }

        /// <summary>拆掉就忘掉，免得实体号被回收之后新建筑白捡一份品质。</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.RemoveEntityWithComponents))]
        private static void RemoveEntityWithComponents_Prefix(PlanetFactory __instance, int id)
        {
            if (__instance?.planet != null) QualityBuildStore.Remove(__instance.planet.id, id);
        }

        // ── 通用轴：耗电 ────────────────────────────────────────

        private static int _powerReported;

        private static void ApplyPowerCut(PlanetFactory factory, int entityId, int planetId)
        {
            // **「没有记录」和「记录了，是 0 分」是两件事，这里只能处理后者。**
            // TryGet 为假 = 这座建筑不是本 mod 看着造的（装 mod 之前就在、
            // 或者记录丢了），它的 workEnergyPerTick 已经是玩家的东西，不碰。
            if (!QualityBuildStore.TryGet(planetId, entityId, out int perItem) || perItem <= 0) return;


            if (entityId >= factory.entityPool.Length) return;

            int pcId = factory.entityPool[entityId].powerConId;
            PowerConsumerComponent[] pool = factory.powerSystem?.consumerPool;

            if (pcId <= 0 || pool == null || pcId >= pool.Length) return;

            long before = pool[pcId].workEnergyPerTick;

            if (before <= 0) return;

            double cut = MaxPowerCut * perItem / QualityRefineryPatches.MaxPerItem;
            var after = (long)(before * (1.0 - cut));

            if (after < 1) after = 1;

            pool[pcId].workEnergyPerTick = after;

            if (System.Threading.Interlocked.Exchange(ref _powerReported, 1) != 0) return;

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质·效果层：第一座用带品质的材料造出来的建筑——实体 {entityId}，" +
                $"材料每件 {perItem} 分，工作功率 {before * 60 / 1000.0:0.#} kW → {after * 60 / 1000.0:0.#} kW" +
                $"（省 {cut * 100:0.#}%）。这个值只在建造这一刻写一次，之后归玩家和面板。");
        }

        // ── 诊断 ────────────────────────────────────────────────

        private static int _reported;

        /// <summary>
        /// <b>开机状态行。这一条是补上的，而它的缺席让一个直接的问题变成了读 IL。</b>
        ///
        /// 玩家问「建筑按分数省电了吗」，日志里 <c>建造扣料</c> 和 <c>效果层</c> 各 0 行
        /// ——而那**分不开**「补丁没生效」和「这局没建东西」，只能回去逐条读
        /// <c>UseHandItems</c> 的 61 句 IL 才答得上。本仓库记过六次的同一条，这是第七次：
        /// <b>状态行回答「接上了没有」，事件行回答「它决定了什么」，谁也替代不了谁。</b>
        /// </summary>
        internal static void Report()
        {
            if (QualityAccess.GetInhandQua == null || !QualityAccess.GridReady)
            {
                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质·效果层（建筑省电）：**没接上**——手上那一格或储物格的访问器缺一个。"
                    + "这种情况下用好料造的建筑和普通料一样耗电，而且不报错。");

                return;
            }

            // **报「真的打上了几个」，不是「我调了 PatchAll 没报错」。**
            // 扣料作用域是七个方法，少打上一个，那条路上的材料就白扣——而那在日志上
            // 和「玩家没走那条路」长得一模一样。所以这里读 Harmony 自己的补丁表。
            var scoped = 0;

            foreach (System.Reflection.MethodBase m in Harmony.GetAllPatchedMethods())
                if (m.Name == "CreatePrebuilds" || m.Name == "DoUpgradeObject" || m.Name == "PlaceItems")
                    scoped++;

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质·效果层（建筑省电）：已接线，顶尖品质省 {MaxPowerCut * 100:0.#}% 工作功率，"
                + "按材料的每件平均分线性插值，**只在建造那一刻写一次**（每 tick 压会和物流站"
                + "充能滑条打架，而且那个字段进存档、读档再乘一次就是复利）。"
                + $"扣料作用域实际打上 {scoped} 个方法（五把建造工具的 CreatePrebuilds + "
                + "DoUpgradeObject + PlaceItems，**应当是 7**）。"
                + "背包和手上两条路都带品质。第一次真的造出带品质的建筑时会再报一行。");

            if (scoped < 7)
                ProjectEdenPlugin.Log.LogWarning(
                    $"物品品质·效果层：扣料作用域只打上了 {scoped} 个方法，少于应有的 7 个"
                    + "——没打上的那条路上，材料的品质会被白扣掉，而且不报错。");
        }

        /// <summary>
        /// 两条路各占多少件。手上那一摞曾经按 0 分计，所以这行原本是用来判断
        /// 「值不值得回去给 <c>Player</c> 补孪生字段」的；现在两条都带品质了，
        /// 它留下来是为了让「材料平均分为什么这么低」有据可查。
        /// </summary>
        private static int _reportedEmpty;

        // 注：这里曾经有个 TraceTake——**故意不看 _depth** 的扣料追踪器，
        // 它是连着四轮「猜挂点 → 挂上去 → 发现没进来」之后换的思路，一轮就定位了
        // （手上那条深度 = 1、建造栏那条前五次深度 = 0 是建造栏在往手上补货）。
        // 问题查清就撤掉了，**做法本身记在 CLAUDE.md 里**：一个诊断连续两轮只能证伪时，
        // 该换的是工具的方向，不是下一个猜测。

        internal static void ReportOnce(int perItem)
        {
            // **两个额度必须分开，这一条是花了一轮才买到的。**
            //
            // `CreatePrebuilds` 每帧都在跑（建造预览也走它），绝大多数调用一件料都不扣。
            // 上一版两种情况共用一个 3 次的额度，于是**额度全被空调用花光**，
            // 真正扣料的那一次永远排不上——日志看起来像「扣料从来没发生过」，
            // 而实际上只是没被采样到。
            //
            // 这是本仓库记过的「预算型探针必须把预算花在有信息量的样本上」，
            // 而且和「日志要打无聊状态」是**两条不同的规矩**：空样本留一条就够证明
            // 作用域进来过，有料的样本才需要多留几条。
            if (_items <= 0)
            {
                if (_reportedEmpty >= 1) return;

                _reportedEmpty++;

                ProjectEdenPlugin.Log.LogWarning(
                    "物品品质·建造扣料：**作用域跑到了，但这一次一件料都没数到**。" +
                    "建造预览每帧都会走 CreatePrebuilds，所以这一行本身很正常——" +
                    "**真正扣料那一次会另外打一行**。整局只报一次这种空样本。");

                return;
            }

            if (_reported >= 3) return;

            _reported++;

            ProjectEdenPlugin.Log.LogInfo(
                $"物品品质·建造扣料 #{_reported}：共 {_items} 件（背包 {_items - _handItems} 件、" +
                $"手上 {_handItems} 件，**两条路现在都带品质**），合计 {_points} 分，" +
                $"平均每件 {perItem} 分。");
        }
    }

    /// <summary>
    /// 扣料范围的那一对前后置，<b>单独成一个类</b>。
    ///
    /// <c>[HarmonyTargetMethods]</c> 是<b>整个补丁类</b>的目标选择器，
    /// 和同一个类里其它方法上的单独 <c>[HarmonyPatch(...)]</c> <b>不能共存</b>——
    /// 混在一起 <c>PatchAll</c> 会抛
    /// 「You cannot combine TargetMethod, TargetMethods or PatchAll with individual annotations」，
    /// 而那是在 <c>Awake</c> 里抛的：<b>整个 mod 一个补丁都打不上</b>。
    /// 这和本仓库记过的「重载方法上写 nameof 会 AmbiguousMatchException 把整个 mod 带下去」
    /// 是同一类事故——注解层面的错误不会只毁掉它自己那一个补丁。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityBuildScopePatches
    {
        /// <summary>
        /// 五把建造工具的 <c>CreatePrebuilds</c> —— 名字相同、类不同，用
        /// <c>TargetMethods</c> 一次收齐。<b>不写参数签名</b>：本仓库记过，
        /// 按类型名写签名的补丁在 preloader 改过宽度之后会静默匹配不上。
        /// </summary>
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> ConsumeSites()
        {
            foreach (string tool in new[]
                     {
                         nameof(BuildTool_Click), nameof(BuildTool_Path), nameof(BuildTool_Inserter),
                         nameof(BuildTool_Addon), nameof(BuildTool_BlueprintPaste),
                     })
            {
                Type t = AccessTools.TypeByName(tool);
                MethodInfo m = t == null ? null : AccessTools.Method(t, "CreatePrebuilds");

                if (m != null) yield return m;
            }

            MethodInfo upgrade = AccessTools.Method(typeof(PlayerAction_Build), nameof(PlayerAction_Build.DoUpgradeObject));

            if (upgrade != null) yield return upgrade;

            MethodInfo place = AccessTools.Method(typeof(ConstructionModuleComponent),
                nameof(ConstructionModuleComponent.PlaceItems));

            if (place != null) yield return place;
        }

        [HarmonyPrefix]
        private static void ConsumeScope_Prefix()
        {
            if (QualityBuildPatches._depth++ > 0) return;

            QualityBuildPatches._items = 0;
            QualityBuildPatches._points = 0;
            QualityBuildPatches._handItems = 0;
            QualityBuildPatches._prebuilds?.Clear();
        }

        [HarmonyPostfix]
        private static void ConsumeScope_Postfix()
        {
            if (--QualityBuildPatches._depth > 0) return;
            if (QualityBuildPatches._depth < 0) QualityBuildPatches._depth = 0;

            int perItem = QualityBuildPatches._items > 0 ? (int)(QualityBuildPatches._points / QualityBuildPatches._items) : 0;

            if (QualityBuildPatches._prebuilds != null)
                foreach ((int planetId, int prebuildId) in QualityBuildPatches._prebuilds)
                    QualityBuildStore.SetPending(planetId, prebuildId, perItem);

            QualityBuildPatches.ReportOnce(perItem);

            QualityBuildPatches._prebuilds?.Clear();
        }

    }

    /// <summary>
    /// 背包扣料。<b>单独成类，因为 <c>TakeTailItems</c> 是重载的。</b>
    ///
    /// <c>[HarmonyPatch(typeof(T), nameof(T.M))]</c> 打在重载方法上会抛
    /// <c>AmbiguousMatchException</c>——<c>AccessTools.DeclaredMethod</c> 最后落到
    /// <c>Type.GetMethod(name, flags)</c>，重载就是歧义。而它是在 <c>PatchAll</c> 里抛的，
    /// <b>整个 mod 一个补丁都打不上</b>。本仓库为这一条记过一次账（<c>PlanetFactory.InsertInto</c>），
    /// 这次我照抄了错误写法，是签名对照表在进游戏之前拦下来的。
    ///
    /// 而按名字选目标的 <c>TargetMethods</c> 又不能和单独注解共存（上一个类刚栽过），
    /// 所以只能再拆一层。<b>两个重载都要打</b>：一个返回 void、一个返回 bool，
    /// 建造路径两个都会走到，只打一个就是「有时候算得出品质、有时候算不出」。
    /// </summary>
    [HarmonyPatch]
    internal static class QualityBuildTakePatches
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(StorageComponent)))
                if (m.Name == nameof(StorageComponent.TakeTailItems))
                    yield return m;
        }

        /// <summary>
        /// <b>后置时 <c>count</c> 已经是「真的拿到了几件」</b>（它是 in/out），
        /// 而品质在 Q0 里等着——<c>TakeTailItems</c> 被 preloader 改写成了会写这个寄存器。
        ///
        /// <b>读而不清。</b> 游戏自己的调用点后面还有一次 preloader 插进去的读，
        /// 清掉的话那一次就读到 0，品质在原版那条路上凭空少掉一截。
        /// </summary>
        [HarmonyPostfix]
        private static void TakeTailItems_Postfix(ref int count)
        {
            if (QualityBuildPatches._depth <= 0 || count <= 0) return;

            QualityBuildPatches._items += count;

            if (QualityAccess.ChannelReady) QualityBuildPatches._points += QualityAccess.GetChannel0();
        }
    }
}
