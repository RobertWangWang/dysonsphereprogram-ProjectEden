using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让一台机器吃多种 <c>ERecipeType</c>。
    ///
    /// <b>原版是「一台机器一种类型」，而且这句话是硬的。</b> 配方选择器的过滤是
    /// <c>filter != recipe.Type → 跳过</c>，而 <c>filter</c> 就是机器的
    /// <c>prefabDesc.assemblerRecipeType</c>；蓝图与复制粘贴那一族则处处比
    /// <c>BuildingParameters.recipeType == prefabDesc.assemblerRecipeType</c>。
    /// 所以「这台机器能跑 A 和 B 两类配方」在原版里表达不出来。
    ///
    /// <b>但代价比 CLAUDE.md 当初估的小得多，因为闸门只有八处、而且全是同一个形状。</b>
    /// 全仓库 15 处读 <c>assemblerRecipeType</c>，逐个查过之后分成三类：
    ///
    /// <list type="bullet">
    /// <item>真闸门 8 处 —— 本文件处理的就是它们，见 <see cref="Sites"/></item>
    /// <item>写入 3 处 —— <c>PrefabDesc.ReadPrefab</c>，不是闸门</item>
    /// <item>假警报 4 处 —— <c>FactorySystem.Import</c> 比的是 <c>== 4</c>，只用来挑动画长度；
    ///       <c>ItemProto.typeString</c> 与 <c>UIInserterBuildTip</c> 是纯显示</item>
    /// </list>
    ///
    /// <b>三件事是白送的，它们才是这个做法成立的原因：</b>
    ///
    /// <list type="number">
    /// <item><c>AssemblerComponent.SetRecipe</c> 完全不校验类型（IL 只有
    ///       <c>recipeType = recipe.Type</c> 一句 stfld），所以生产逻辑一个字都不用改。</item>
    /// <item><c>AssemblerComponent.recipeType</c> 存的是<b>配方</b>的类型，不是机器的。
    ///       <c>InternalUpdate</c> 里那六处读取是按 1/2/3/4/5 挑音效和动画的——
    ///       于是化工配方跑在这台机器上，音效动画自动还是化工厂那一套。</item>
    /// <item>那个字段虽然进存档，但全仓库 18 个读写点<b>没有任何一处</b>拿它和 prefab 对过，
    ///       所以不存在「读档发现类型对不上、把配方清掉」。</item>
    /// </list>
    /// </summary>
    [HarmonyPatch]
    internal static class RecipeTypeCompatPatches
    {
        /// <summary>机器类型 → 它额外接受的配方类型。只增不减：本类型永远接受自己。</summary>
        private static readonly Dictionary<int, int[]> Table = new Dictionary<int, int[]>();

        /// <summary>
        /// 每个方法<b>最近一次</b>改写了几处。键是方法，不是累加器——
        /// <b>同一个方法会被 transpile 不止一次</b>（别的补丁挂到同一个方法上时 Harmony 会重跑），
        /// 用累加器的话总数就会翻倍，而每一次其实都是对的。
        ///
        /// 实测就撞上了这个：逐方法全部命中、一条 ERROR 都没有，汇总行却报
        /// 「共改写 13 处（对不上 8）」——<b>一个正常工作的功能被自己的自检说成坏了</b>。
        /// 这正是流体白名单那次的教训：<b>核最终状态，别核自己干了几次</b>。
        /// 顺带那个写死的 8 也是陈的，Sites 加起来是 9。
        /// </summary>
        private static readonly Dictionary<string, int> Hits = new Dictionary<string, int>();

        internal static bool Enabled => Table.Count > 0;

        /// <summary>
        /// 注册一台「吃多种类型」的机器。由 <c>MegaBuildingRegistry</c> 在读配置时调用。
        /// </summary>
        internal static void Register(int machineType, int[] accepted)
        {
            if (machineType <= 0 || accepted == null || accepted.Length == 0) return;

            Table[machineType] = accepted;

            ProjectEdenPlugin.Log.LogInfo(
                $"配方类型兼容：类型 {machineType} 额外接受 {string.Join("、", Array.ConvertAll(accepted, x => x.ToString()))}");
        }

        /// <summary>
        /// 机器类型 <paramref name="machine"/> 能不能跑配方类型 <paramref name="recipe"/>。
        ///
        /// <b>方向是有意义的</b>：综合化学厂（16）接受化学（2），但化工厂（2）不接受 16。
        /// 所以下面两个转译器入口按栈上的先后各用一个，不能合并成一个对称判断。
        /// </summary>
        internal static bool AcceptsMR(ERecipeType machine, ERecipeType recipe)
        {
            if (machine == recipe) return true;

            return Table.TryGetValue((int)machine, out int[] ok) && Array.IndexOf(ok, (int)recipe) >= 0;
        }

        /// <summary>参数反过来的那一版。栈上先压配方类型、后压机器类型的站点用它。</summary>
        internal static bool AcceptsRM(ERecipeType recipe, ERecipeType machine) => AcceptsMR(machine, recipe);

        /// <summary>配方类型 → 除了它本家之外，还有哪些机器做得了它（中文名，未翻译）。</summary>
        private static readonly Dictionary<int, string> AlsoIn = new Dictionary<int, string>();

        /// <summary>
        /// 「制造于」那一行要补的字。
        ///
        /// <b>这是这个功能「被发现」的那一半。</b> 一条化学配方的 <c>madeFromString</c>
        /// 永远写着「化工厂」，不会提到综合化学厂——玩家从配方上根本看不出那台机器做得了它，
        /// 而这个 mod 里那台正是唯一的 10000 倍速选项。闸门改了却没人知道，等于没改。
        /// </summary>
        internal static string AlsoMadeIn(int recipeType)
            => AlsoIn.TryGetValue(recipeType, out string s) ? s : null;

        // ── 闸门清单 ────────────────────────────────────────
        //
        // 每一项是「类型名 / 方法名 / 期望改写几处」。数目是从游戏程序集里数出来的，
        // 对不上就大声失败——本仓库的规矩：转译器要报匹配数，匹配不上要响。
        private static readonly Tuple<Type, string, int>[] Sites =
        {
            Tuple.Create(typeof(UIRecipePicker), "RefreshIcons", 1),
            Tuple.Create(typeof(BuildingParameters), "CanPasteToFactoryObject", 2),
            Tuple.Create(typeof(BuildingParameters), "PasteToFactoryObject", 3),
            Tuple.Create(typeof(BuildingParameters), "ApplyPrebuildParametersToEntity", 1),
            Tuple.Create(typeof(BuildingParameters), "CopyFromFactoryObject", 1),
            Tuple.Create(typeof(BuildingParameters), "CopyFromBuildPreview", 1),
        };

        /// <summary>
        /// 配置里没有任何建筑登记多类型，就整个不打这八处补丁——
        /// 不是为了省那点开销，是<b>能不改原版 IL 就不改</b>。
        ///
        /// 表在这里建而不是等 <c>OnPreAddData</c>，因为那是 <c>PatchAll</c> 之后的事；
        /// 配置在 <c>Awake</c> 开头就载好了，这时读得到。
        /// </summary>
        private static bool Prepare()
        {
            if (Table.Count == 0) LoadFromConfig();

            return Enabled;
        }

        private static void LoadFromConfig()
        {
            MegaBuildingsConfig cfg = MegaBuildingRegistry.Config;

            if (cfg?.buildings == null) return;

            foreach (MegaBuildingEntry b in cfg.buildings)
            {
                if (b?.acceptsRecipeTypes == null || b.acceptsRecipeTypes.Length == 0) continue;

                Register(b.recipeType, b.acceptsRecipeTypes);

                foreach (int t in b.acceptsRecipeTypes)
                    AlsoIn[t] = AlsoIn.TryGetValue(t, out string had) ? had + " / " + b.displayName : b.displayName;
            }
        }

        /// <summary>
        /// 按<b>名字</b>选目标方法，不写签名。
        ///
        /// 理由记在 CLAUDE.md：<c>[HarmonyPatch(typeof(T), nameof(T.M))]</c> 碰上重载会在
        /// <c>PatchAll</c> 阶段抛 <c>AmbiguousMatchException</c>，而那会把整个 mod 带下水；
        /// 写死参数类型又会在别的补丁改了签名之后静默失配。
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Tuple<Type, string, int> site in Sites)
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(site.Item1))
                    if (m.Name == site.Item2)
                        yield return m;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = new List<CodeInstruction>(instructions);

            // 先解析出来再进循环，解析不到就整条不改——**绝不能把 null 当操作数发出去**，
            // 那会一路活到 ILManipulator.WriteTo 才炸，栈顶指向 Harmony 的写入器而不是这里
            MethodInfo mr = AccessTools.Method(typeof(RecipeTypeCompatPatches), nameof(AcceptsMR));
            MethodInfo rm = AccessTools.Method(typeof(RecipeTypeCompatPatches), nameof(AcceptsRM));

            if (mr == null || rm == null)
            {
                ProjectEdenPlugin.Log.LogError("配方类型兼容：解析不到 Accepts 助手，本方法不改");

                return code;
            }

            FieldInfo machineField = AccessTools.Field(typeof(PrefabDesc), nameof(PrefabDesc.assemblerRecipeType));
            FieldInfo filterField = AccessTools.Field(typeof(UIRecipePicker), "filter");
            FieldInfo paramField = AccessTools.Field(typeof(BuildingParameters), nameof(BuildingParameters.recipeType));
            FieldInfo protoField = AccessTools.Field(typeof(RecipeProto), nameof(RecipeProto.Type));

            int hits = 0;

            for (int i = 0; i < code.Count; i++)
            {
                // 机器类型那一侧：配方选择器里是 filter，其余全是 prefabDesc.assemblerRecipeType
                if (code[i].opcode != OpCodes.Ldfld) continue;
                if (!ReferenceEquals(code[i].operand, machineField) && !ReferenceEquals(code[i].operand, filterField))
                    continue;

                // 往后最多八条找比较跳转，顺便确认另一侧确实在窗口里——
                // 只认「机器类型」一侧会匹配到别的用途（比如 Import 里那个 == 4）
                int br = -1;
                bool sawOther = false;

                for (int j = i + 1; j < code.Count && j <= i + 8; j++)
                {
                    if (code[j].opcode == OpCodes.Ldfld &&
                        (ReferenceEquals(code[j].operand, paramField) || ReferenceEquals(code[j].operand, protoField)))
                        sawOther = true;

                    if (code[j].opcode == OpCodes.Beq || code[j].opcode == OpCodes.Beq_S ||
                        code[j].opcode == OpCodes.Bne_Un || code[j].opcode == OpCodes.Bne_Un_S)
                    {
                        br = j;

                        break;
                    }
                }

                if (br < 0) continue;

                // 另一侧也可能在机器类型之前压栈（CanPaste @016B、Paste @0844 是这种），
                // 所以往前再看一小段
                if (!sawOther)
                    for (int j = Math.Max(0, i - 6); j < i; j++)
                        if (code[j].opcode == OpCodes.Ldfld && ReferenceEquals(code[j].operand, paramField))
                            sawOther = true;

                // ApplyPrebuildParametersToEntity 那一处配方类型来自局部变量，没有 ldfld 可认，
                // 但它紧挨着 assemblerRecipeType 且窗口里没有别的候选，按位置认下来
                if (!sawOther && br == i + 1) sawOther = true;

                if (!sawOther) continue;

                // **栈上谁在前，决定用哪个助手。** 判据只要看跳转前一条：
                // 它若是 assemblerRecipeType，说明机器类型后压 →（配方，机器）
                bool machineLast = ReferenceEquals(code[br - 1].operand, machineField);

                bool equalMeansSkip = code[br].opcode == OpCodes.Beq || code[br].opcode == OpCodes.Beq_S;

                // 把跳转本身改成 call，再在它后面插一条新跳转。
                // 这样原跳转上挂着的标签留在 call 上——跳到这里的人应该先执行判断，语义正确；
                // 换成「先插 call 再改跳转」则会把标签落在 call 后面，跳过判断。
                object target = code[br].operand;

                code[br].opcode = OpCodes.Call;
                code[br].operand = machineLast ? rm : mr;

                code.Insert(br + 1, new CodeInstruction(equalMeansSkip ? OpCodes.Brtrue : OpCodes.Brfalse, target));

                hits++;
                i = br + 1;
            }

            int want = 0;

            foreach (Tuple<Type, string, int> site in Sites)
                if (site.Item1 == original.DeclaringType && site.Item2 == original.Name)
                    want = site.Item3;

            if (hits != want)
                ProjectEdenPlugin.Log.LogError(
                    $"配方类型兼容：{original.DeclaringType?.Name}.{original.Name} 期望改写 {want} 处，实际 {hits} 处——" +
                    "游戏更新过的话这里要重新读 IL。多机型配方在这条路径上不生效");
            else
                Hits[original.DeclaringType?.Name + "." + original.Name] = hits;

            return code;
        }

        /// <summary>关着也要打一行——沉默的诊断分不出「没配」和「没跑」。</summary>
        internal static void Report()
        {
            if (!Enabled)
            {
                ProjectEdenPlugin.Log.LogInfo("配方类型兼容：没有建筑登记多类型，八处闸门维持原版行为");

                return;
            }

            var want = 0;

            foreach (Tuple<Type, string, int> site in Sites) want += site.Item3;

            var got = 0;

            foreach (KeyValuePair<string, int> kv in Hits) got += kv.Value;

            var where = new List<string>();

            foreach (KeyValuePair<string, int> kv in Hits) where.Add($"{kv.Key} {kv.Value}");

            where.Sort();

            string detail = string.Join("、", where.ToArray());

            if (got == want && Hits.Count == Sites.Length)
                ProjectEdenPlugin.Log.LogInfo(
                    $"配方类型兼容：{Table.Count} 台机器登记了多类型，" +
                    $"{Sites.Length} 个方法共 {want} 处闸门全部改写（{detail}）");
            else
                ProjectEdenPlugin.Log.LogError(
                    $"配方类型兼容：{Sites.Length} 个方法应共改写 {want} 处，实际 {Hits.Count} 个方法 {got} 处" +
                    $"（{detail}）——差的那些方法上多类型不生效，游戏更新过的话要重新读 IL");
        }
    }
}
