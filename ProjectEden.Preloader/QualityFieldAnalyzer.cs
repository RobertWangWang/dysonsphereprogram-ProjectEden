using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ProjectEden.Preloader
{
    /// <summary>
    /// 物品品质 · 阶段一的<b>分析器</b>。设计稿在仓库根目录的 <c>物品品质.md</c>。
    ///
    /// <b>它现在只看不改。</b> 这不是保守，是 <see cref="CargoIncWidener"/> 用两次线上崩溃换来的规矩：
    /// 改写是边走边改的，走到一半遇见没见过的形状就已经收不回来了，而半改写的
    /// <c>Assembly-CSharp</c> 的表现是 CLR 类型加载失败、报错位置和真正原因毫不相干。
    /// 所以先把「要动哪些地方、各有多少处」量准，数目和设计稿对得上，再让改写那一半落地。
    ///
    /// <b>品质点数就是第二个 <c>inc</c>。</b> 全游戏装着物品的地方结构一律是
    /// <c>(itemId, count, 载荷)</c>，而 <c>inc</c> 能在这 20 多个地方活下来，靠的是它是个
    /// <b>可加量</b>：合并相加、等级 = 载荷 ÷ count、拆分按比例。品质要活下来必须是同一种数学，
    /// 所以搬运层可以照着 <c>inc</c> 机械复制；<b>效果层不能</b>——增产/加速/耗电那三张表是增产剂的。
    ///
    /// 于是全部工作按方法分成三类：
    /// <list type="bullet">
    /// <item><b>纯搬运</b>：方法里只碰载荷字段、不碰任何增产剂效果表 → 可机械孪生。</item>
    /// <item><b>混合</b>：两样都碰 → <b>逐个手工处理</b>，而且必须在 <see cref="DeclaredMixed"/>
    ///       里显式声明。发现集和声明集对不上就是 Blocker——游戏更新长出来的新混合方法
    ///       绝不能被当成纯搬运自动孪生，那会把品质悄悄接到增产剂的效果上。</item>
    /// <item><b>纯效果</b>：只碰表、不碰载荷 → 一个字节都不用动。</item>
    /// </list>
    /// </summary>
    internal static class QualityFieldAnalyzer
    {
        internal class Report
        {
            /// <summary>分析通过、可以进入改写阶段</summary>
            internal bool Clean;

            /// <summary>致命项。非空 = 一个字节都不改。</summary>
            internal readonly List<string> Blockers = new List<string>();

            /// <summary>提示。无论成败都要打，「什么都没发现」也是一条信息。</summary>
            internal readonly List<string> Notes = new List<string>();

            internal int PayloadFields;
            internal int MoveMethods;
            internal int MoveAccesses;
            internal int MixedMethods;
            internal int MixedAccesses;
            internal int EffectOnlyMethods;
            internal int UiMethods;
            internal int UiAccesses;
            internal int ParamMethods;
            internal int ParamSlots;

            /// <summary>通知汇里被跳过的方法数</summary>
            internal int SkippedParamMethods;

            /// <summary>单个方法里最多有几个载荷参数位——决定侧信道要几个寄存器</summary>
            internal int MaxParamSlots;

            /// <summary>槽位最多的那个方法，报出来便于核对</summary>
            internal string MaxParamSlotsAt = "-";
            internal int SaveStreams;

            /// <summary>按访问数排序的纯搬运方法，用来核对改写阶段的命中数</summary>
            internal readonly List<string> MoveDetail = new List<string>();

            /// <summary>实际发现的混合方法</summary>
            internal readonly List<string> MixedFound = new List<string>();

            /// <summary>清单里没有、但形状像载荷字段的——游戏更新加了新缓冲区的信号</summary>
            internal readonly List<string> Suspects = new List<string>();
        }

        /// <summary>
        /// 载荷字段清单：<c>类型::字段</c>。
        ///
        /// <b>为什么是显式清单而不是发现式。</b> <see cref="CargoIncWidener"/> 能靠
        /// 「被 <c>Cargo.inc</c> 赋值过的字节字段」发现，是因为那里有一条现成的数据流可跟；
        /// 品质是<b>新加</b>的字段，一条数据流都没有，种子只能点名。
        ///
        /// 代价是清单会随游戏版本过期，所以 <see cref="FindSuspects"/> 反过来再搜一遍
        /// ——**核对的是结果，不是我自己写了什么**，这是气体白名单那一课。
        /// </summary>
        private static readonly string[] DeclaredPayload =
        {
            // ── 传送带与仓储 ──
            "Cargo::inc",
            "StorageComponent/GRID::inc",
            "StationStore::inc",
            "DeliveryPackage/GRID::inc",
            "DispenserStore::inc",

            // ── 搬运途中 ──
            "InserterComponent::itemInc",
            "ShipData::inc",

            // ── 生产 ──
            "AssemblerComponent::incServed",
            "LabComponent::incServed",
            "FractionatorComponent::fluidInputInc",
            "FractionatorComponent::fluidOutputInc",
            "TankComponent::fluidInc",

            // ── 终端消耗（仍要孪生，因为品质要在这里被「读走并丢弃」） ──
            "PowerGeneratorComponent::fuelInc",
            "PowerExchangerComponent::emptyInc",
            "PowerExchangerComponent::fullInc",
            "PowerExchangerComponent::poolInc",
            "TurretComponent::itemInc",
            "TurretComponent::currentBulletInc",
            "EjectorComponent::bulletInc",
            "SiloComponent::bulletInc",
            "Mecha::reactorItemInc",
            "Mecha::ammoInc",

            // ── 零散容器 ──
            "ItemPackage::inc",
            "CargoView::inc",
            "CountInc::inc",

            // 「id + 件数 + 点数」的临时元组。第一版清单漏了这两个，是 FindSuspects 反查出来的
            // ——这正是那段反查存在的理由：漏一个缓冲区，品质会在那里静默蒸发，而所有计数都正常。
            "IDCNTINC::inc",
            "IDCNTMAX::inc",
        };

        /// <summary>
        /// <b>主干道：品质真正会流动的那四个载荷。</b>
        ///
        /// 阶段 1c 的范围。所有者决定把 1c 砍到一个最小闭环，理由是形状普查的结果：
        /// 全部 558 处访问剥掉寻址之后仍有 74 种形状、20 个模式只覆盖 78.5%，
        /// 「孪生全部访问」是一趟真正的编译器 pass（建值图、复制子图、处理分支汇合），
        /// 而那是一次性的大爆炸式落地，中间没有可验证的中途状态。
        ///
        /// 这四个是品质从矿走到建筑的整条主干道：
        /// 传送带 → 箱子/背包 → 物流站槽 → 生产投入。其余载荷
        /// （炮塔、弹射器、发射井、机甲、分馏塔、能量交换器……）明确列为
        /// <b>「品质在此丢弃」</b>，和燃料、弹药同属设计稿 B 组的终端消耗。
        ///
        /// <b>这是范围收窄，不是偷工减料</b>——「品质只在主干道上流动」是一条
        /// 能对玩家讲清楚的规则，而且它保留了 preloader 路线的正确性。
        /// </summary>
        internal static readonly string[] MainlinePayload =
        {
            "Cargo::inc",
            "StorageComponent/GRID::inc",
            "StationStore::inc",
            "AssemblerComponent::incServed",

            // 这两个不是「又一个载荷」，是**主干道内部的临时元组**：
            // StorageComponent::Sort 把格子归并整理时，点数先落到 IDCNTINC 再写回格子。
            // 不把它们算进主干道，Sort 就会用一个没有孪生的临时值覆盖 grids[i].inc，
            // 而 grids[i].qua 留着上一次的旧值——不报错，整理一次品质就串一次。
            "IDCNTINC::inc",
            "IDCNTMAX::inc",
        };

        /// <summary>
        /// <b>会被原样上传到 GPU 的载荷结构体——暂时不加孪生字段。</b>
        ///
        /// <b>这是实测撞出来的，而且是我漏了一整族。</b> 给 <c>TrashObject</c> 加了一个 Int32
        /// 之后它从 44 涨到 48 字节，而 <c>TrashContainer.Draw</c> 的 <c>ComputeBuffer</c>
        /// stride 是写死的 44，游戏一开就崩：
        /// <code>
        ///   SetData(): One of C# data stride (48 bytes) and Buffer stride (44 bytes)
        ///   should be multiple of other.
        /// </code>
        ///
        /// <b>教训是本仓库自己那条规矩的原样重演：把一族枚举一遍。</b>
        /// 我枚举了「装着物品的结构体」，却没枚举「哪些结构体要原样喂给 GPU」——
        /// 而后者在 CLAUDE.md 里为 <c>Cargo</c> 写了整整一节，我却默认了 <c>Cargo</c> 是唯一一个。
        /// 全模块扫 <c>ComputeBuffer.SetData</c> 的实参类型，答案是<b>四个</b>：
        /// <list type="bullet">
        /// <item><c>Cargo</c> — <c>CargoContainer.Draw</c>。**已经有对策**：
        ///       <c>CargoWidening.UploadRepacked</c> 重打包成 32 字节，所以它不在这份名单里。</item>
        /// <item><c>TrashObject</c> — <c>TrashContainer.Draw</c>。</item>
        /// <item><c>DroneData</c> — <c>LogisticDroneRenderer.Update</c>。</item>
        /// <item><c>CourierData</c> — <c>LogisticCourierRenderer.Update</c>。</item>
        /// </list>
        /// （<c>ShipData</c> 安全：运输船上传的是另一个结构体 <c>ShipRenderingData</c>。）
        ///
        /// <b>摘掉它们的代价不一样，必须说清楚：</b>
        /// <c>TrashObject</c> 是扔在地上的东西，丢掉品质完全可以接受。
        /// 但 <c>DroneData</c> / <c>CourierData</c> 是<b>运输机在飞的那一趟货</b>——
        /// 那是站与站之间的主干道，品质在那里蒸发是个真缺口，**1c 必须解决**。
        /// 可选路线记在设计稿里：给这三个也做重打包（因为新字段追加在末尾，
        /// 原布局就是新结构体的前缀，按 <c>原 stride</c> 盲拷即可，不用逐字段写渲染副本），
        /// 或者给 <c>StationComponent</c> 加一条和 <c>workDroneDatas</c> 平行的品质数组。
        /// </summary>
        private static readonly string[] GpuUploaded =
        {
            "TrashObject::inc",
            "DroneData::inc",
            "CourierData::inc",
        };

        /// <summary>
        /// 反查会命中、但<b>确认不是</b>逐堆载荷的字段。记在这里而不是让它们每次都进 Notes，
        /// 是为了让反查的输出「非空即有新情况」——否则十几条固定噪声会把真正的新发现盖掉。
        ///
        /// 三类：
        /// <list type="bullet">
        /// <item><c>SpraycoaterComponent::inc*</c> 是喷涂机的<b>配置</b>（接哪条带、用哪种增产剂、
        ///       一份喷几件、机内还剩几件），不是某一堆货带着的点数。机内那几件增产剂的品质
        ///       进了喷涂机就丢弃——和燃料一样，见设计稿 B 组。</item>
        /// <item><c>PrefabDesc::inc*</c> 是预制体上的常量，逐堆载荷谈不上。</item>
        /// <item>剩下的是名字误伤：<c>incoming*</c>、以及 <c>*Inc</c> 当「增幅」讲的那些
        ///       （黑雾炮塔的伤害增幅、冷却增幅）。</item>
        /// </list>
        /// </summary>
        private static readonly string[] NotPayload =
        {
            "SpraycoaterComponent::incBeltId",
            "SpraycoaterComponent::incItemId",
            "SpraycoaterComponent::incAbility",
            "SpraycoaterComponent::incSprayTimes",
            "SpraycoaterComponent::incCount",
            "SpraycoaterComponent::incCapacity",
            "PrefabDesc::incCapacity",
            "PrefabDesc::incItemId",
            "PrefabDesc::dfTurretAttackDamageInc",
            "PrefabDesc::dfTurretColdSpeedInc",
            "PrefabDesc::unitColdSpeedInc",
            "DFGBaseComponent::incomingSkillsCursor",
            "DefenseSystem::incomingSupernovaTime",
        };

        /// <summary>
        /// <b>通知汇：品质不往这里流，按设计丢弃。</b>
        ///
        /// 这一族是「玩家背包收到东西了」的事件广播，下游是成就、统计和机甲，
        /// 没有一个需要知道品质——和燃料、弹药一样属于设计稿 B 组的「终端消耗」。
        ///
        /// 而摘掉它还顺带消掉了整个 1b 里最麻烦的一个形状：<c>Player/DItemNotify</c>
        /// 是个<b>委托类型</b>，它的 <c>Invoke</c> / <c>BeginInvoke</c> 带着 inc 参数，
        /// 还有两个处理器（<c>GameHistoryData::OnPlayerPackageAddItem</c> 和
        /// <c>Mecha::OnPlayerPackageAddItem</c>）是靠 <c>ldftn</c> 绑上去的。
        /// 要给它加参数，就得同时改委托的两个虚方法签名和两个处理器，
        /// 而委托签名一旦和处理器差一个参数，绑定会在**运行时**才炸。
        /// 实测过这一族是整个目标集里<b>唯一</b>的虚方法与 <c>ldftn</c> 来源，
        /// 摘掉之后剩下的全是普通实例/静态方法，加参数是纯机械操作。
        /// </summary>
        private static readonly string[] NotifySink =
        {
            "Player/DItemNotify::Invoke",
            "Player/DItemNotify::BeginInvoke",
            "Player::NotifyPackageAddItem",
            "Player::NotifyDeliveryPackageAddItem",
            "GameHistoryData::OnPlayerPackageAddItem",
            "Mecha::OnPlayerPackageAddItem",
        };

        /// <summary>
        /// 增产剂<b>效果</b>表。方法一旦碰到它们，里面的载荷访问就不能机械孪生——
        /// 那是「点数换成增产多少 / 加速多少 / 耗电多少」的地方，品质有自己的效果层。
        /// </summary>
        private static readonly string[] EffectTables =
        {
            "incTable", "accTable", "incTableMilli", "accTableMilli",
            "powerTable", "powerTableRatio", "fastIncArrowTable",
            "incFastDivisionNumerator", "incFastDivisionDenominator",
            "kSprayIncMax", "kIncLevelMax",
        };

        /// <summary>
        /// 要逐个手工处理的方法。设计稿第四节第三小节逐条写了每个要做什么。
        ///
        /// <b>这份清单就是定义，启发式只是探测器。</b> 第一版把两者搞反了，代价立刻显形：
        /// <c>StorageComponent::TakeTailItemsWithIncTable</c> 引用的 <c>incTable</c> 是个<b>参数</b>
        /// 而不是字段，启发式（只看字段引用）认不出来，于是它差点被当成纯搬运自动孪生
        /// ——而它做的事是<b>按增产等级分桶取货</b>，语义上是彻头彻尾的混合。
        ///
        /// 所以判定是这样的，两条方向相反、各自独立：
        /// <list type="number">
        /// <item>在这份清单里 → 手工，<b>无论启发式怎么说</b>。</item>
        /// <item>启发式认定混合（同时碰载荷和效果表）却<b>不在</b>清单里 → Blocker。
        ///       游戏更新长出来的新形状绝不能被当成纯搬运，那会把品质接到增产剂的效果上。</item>
        /// <item>在清单里却根本不碰载荷 → Blocker。我们在对着一个不存在的形状写处理代码。</item>
        /// </list>
        ///
        /// <c>UI*</c> 不在这里：它们只画箭头和数字、不搬运东西，所以既不孪生也不手工，
        /// 到阶段四要显示品质时再单独追加。
        /// </summary>
        private static readonly string[] DeclaredMixed =
        {
            "AssemblerComponent::InternalUpdate",
            "LabComponent::InternalUpdateAssemble",
            "FractionatorComponent::InternalUpdate",
            "FractionatorComponent::get_extraIncProduceProb",
            "EjectorComponent::InternalUpdate",
            "SiloComponent::InternalUpdate",
            "TurretComponent::LoadAmmo",
            "PlanetFactory::EntityFastFillIn",
            "PowerGeneratorComponent::GenEnergyByFuel",
            "PowerExchangerComponent::CalculateActualEnergyPerTick",
            "SpraycoaterComponent::InternalUpdate",
            "StorageComponent::TakeTailItemsWithIncTable",
            "MechaForge::GameTick",
            "Mecha::GenerateEnergy",
            "Mecha::LoadAmmo",
            "Mecha::get_reactorPowerGenRatio",
            "Mecha::get_reactorPowerGenEnhanced",
            "Mecha::get_reactorPowerForWeaponEnhanced",
            "CountInc::get_incArrows",
        };

        /// <summary>
        /// 把 <see cref="DeclaredPayload"/> 解析成真实字段。
        ///
        /// <b>分析器和改写器共用这一个方法，不是为了省代码。</b> 两边各抄一份清单，
        /// 迟早会有一边先改；那时分析报的是一套字段、改写动的是另一套，
        /// 而两边都会报「全过」——这正是 <c>inc</c> 加宽那次「校验脚本和变换共用同一条
        /// 名字规则，所以它也跟着报了全过」的翻版，只是换了个方向。
        /// </summary>
        internal static void ResolvePayload(ModuleDefinition module,
            IDictionary<string, FieldDefinition> into, ICollection<string> blockers)
        {
            foreach (string spec in DeclaredPayload)
            {
                string[] parts = spec.Split(new[] { "::" }, StringSplitOptions.None);

                TypeDefinition owner = module.GetType(parts[0]);

                if (owner == null)
                {
                    blockers.Add($"找不到类型 {parts[0]}（清单项 {spec}）");

                    continue;
                }

                FieldDefinition f = owner.Fields.FirstOrDefault(x => x.Name == parts[1] && !x.IsStatic);

                if (f == null)
                {
                    blockers.Add($"找不到字段 {spec}");

                    continue;
                }

                if (!IsIntegerPayload(f.FieldType))
                {
                    blockers.Add($"{spec} 的类型是 {f.FieldType.FullName}，不是预期的整数或整数数组");

                    continue;
                }

                into[Key(f)] = f;
            }
        }

        internal static Report Analyze(ModuleDefinition module)
        {
            var r = new Report();

            // ── 1. 载荷字段：清单里的都要在，且都得是整数 ──
            var payload = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);

            ResolvePayload(module, payload, r.Blockers);

            r.PayloadFields = payload.Count;

            if (r.Blockers.Count > 0) return r;

            FindSuspects(module, payload, r);
            CheckGpuUploads(module, payload, r);

            // ── 2. 按方法分三类。声明是定义，启发式只用来抓「清单漏了的新形状」 ──
            var declared = new HashSet<string>(DeclaredMixed, StringComparer.Ordinal);
            var declaredSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (TypeDefinition t in AllTypes(module))
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;

                int hits = 0, fx = 0;

                foreach (Instruction ins in m.Body.Instructions)
                {
                    if (ins.Operand is FieldReference fr)
                    {
                        if (payload.ContainsKey(Key(fr))) hits++;

                        if (EffectTables.Contains(fr.Name)) fx++;
                    }
                }

                string name = t.FullName + "::" + m.Name;

                if (hits == 0)
                {
                    if (fx > 0) r.EffectOnlyMethods++;

                    // 声明成手工、却一处载荷都不碰 —— 我们在对着不存在的形状写代码
                    if (declared.Contains(name))
                        r.Blockers.Add(
                            $"声明的手工方法 {name} 一处载荷字段都不碰——" +
                            "游戏版本可能变了，对照 IL 重新确认后再改 DeclaredMixed");

                    continue;
                }

                // UI 只负责画，既不孪生也不手工；到阶段四再单独追加
                if (IsDisplayOnly(t))
                {
                    r.UiMethods++;
                    r.UiAccesses += hits;

                    continue;
                }

                if (declared.Contains(name))
                {
                    declaredSeen.Add(name);

                    r.MixedMethods++;
                    r.MixedAccesses += hits;
                    r.MixedFound.Add($"{name}  载荷 {hits} / 效果表 {fx}");

                    continue;
                }

                // 没被声明，但启发式看出它同时碰效果表 —— 这是「清单该长一条」的信号，
                // 绝不能当纯搬运自动孪生
                if (fx > 0)
                {
                    r.Blockers.Add(
                        $"发现未声明的混合方法 {name}（载荷 {hits} / 效果表 {fx}）——" +
                        "它同时碰载荷和增产剂效果表，不能当纯搬运自动孪生。" +
                        "先决定它该怎么处理，再加进 DeclaredMixed");

                    continue;
                }

                r.MoveMethods++;
                r.MoveAccesses += hits;
                r.MoveDetail.Add($"{name}  {hits}");
            }

            foreach (string gone in declared.Except(declaredSeen).OrderBy(x => x, StringComparer.Ordinal))
                if (!r.Blockers.Any(b => b.Contains(gone)))
                    r.Blockers.Add($"声明的手工方法 {gone} 在程序集里找不到——对照 IL 重新确认");

            // ── 4. 需要长出孪生参数的方法 ──
            CountParams(module, payload, r);

            // ── 5. 需要加存档版本分支的流 ──
            CountSaveStreams(module, payload, r);

            r.Clean = r.Blockers.Count == 0;

            return r;
        }

        /// <summary>
        /// 反过来找：形状像载荷、却不在清单里的字段。
        ///
        /// 判据是「名字像 inc + 所属类型同时还有一个装件数的字段」。这会有误报，
        /// 所以它只进 Notes 不进 Blockers——但<b>漏报才是要命的</b>：
        /// 少括一个缓冲区，品质会在那里静默蒸发，而所有计数看起来都正常。
        /// </summary>
        private static void FindSuspects(ModuleDefinition module,
            IDictionary<string, FieldDefinition> payload, Report r)
        {
            var known = new HashSet<string>(NotPayload, StringComparer.Ordinal);

            // GPU 上传那三个是**故意摘掉的载荷**，不是「不是载荷」。放进 known 只是
            // 为了不让它们混在噪声里，下面会单独报一条，因为那是一个已知缺口而不是结论。
            foreach (string g in GpuUploaded) known.Add(g);

            var stale = new HashSet<string>(known, StringComparer.Ordinal);

            foreach (TypeDefinition t in AllTypes(module))
            {
                if (IsDisplayOnly(t)) continue;

                bool hasCount = t.Fields.Any(f => !f.IsStatic && LooksLikeCount(f));

                if (!hasCount) continue;

                foreach (FieldDefinition f in t.Fields)
                {
                    if (f.IsStatic) continue;
                    if (!LooksLikeInc(f.Name)) continue;
                    if (!IsIntegerPayload(f.FieldType)) continue;

                    string key = Key(f);

                    if (payload.ContainsKey(key)) continue;

                    if (known.Contains(key)) { stale.Remove(key); continue; }

                    r.Suspects.Add($"{t.FullName}::{f.Name} ({f.FieldType.Name})");
                }
            }

            // 排除表里写着、程序集里却找不到的 —— 说明那条排除理由已经过期
            foreach (string s in stale.OrderBy(x => x, StringComparer.Ordinal))
                r.Notes.Add($"排除表里的 {s} 在程序集里不存在了，该清掉这一条");

            r.Notes.Add(r.Suspects.Count == 0
                ? $"反查：清单外没有形状像载荷的新字段（{known.Count - stale.Count} 个已知非载荷已排除）"
                : $"反查：{r.Suspects.Count} 个字段形状像载荷、既不在清单也不在排除表里，逐个确认");

            // 已知缺口必须每次都说出来。**沉默的缺口等于没记住的缺口**——
            // 尤其 DroneData / CourierData 是站与站之间的主干道，品质在那儿蒸发是真问题。
            r.Notes.Add(
                $"**已知缺口**：{GpuUploaded.Length} 个载荷字段因为所属结构体要原样喂给 GPU 而暂时没加孪生（" +
                string.Join("、", GpuUploaded) +
                "）。TrashObject 无所谓；DroneData / CourierData 是运输机在飞的那趟货，1c 必须解决。");
        }

        /// <summary>
        /// 需要长出孪生参数的方法。种子按名字取，<b>但名字只是起点</b>——
        /// 真正的判据是数据流（一个参数被当实参传给了已孪生的参数，它自己也得孪生），
        /// 那一步属于改写阶段，这里先把种子数量报出来当基线。
        /// </summary>
        /// <summary>
        /// <b>有没有哪个要加字段的结构体，会被原样喂给 GPU。</b>
        ///
        /// 这一项是<b>进游戏崩了一次之后补的</b>：给结构体加 4 个字节，它的
        /// <c>ComputeBuffer</c> stride 就对不上了，而那个 stride 在游戏代码里是写死的字面量。
        /// 崩的形式是启动即 <c>ArgumentException</c>，栈里只有 Unity 和渲染方法，
        /// 指不到「你加了一个字段」。
        ///
        /// 判据是<b>结果而不是我的记忆</b>：全模块找 <c>ComputeBuffer.SetData</c>，
        /// 往回看它拿到的数组字段是什么元素类型，命中载荷清单就是 Blocker。
        /// <c>Cargo</c> 例外，因为那一路已经有 <c>CargoWidening.UploadRepacked</c>
        /// 在运行时重打包。
        /// </summary>
        private static void CheckGpuUploads(ModuleDefinition module,
            IDictionary<string, FieldDefinition> payload, Report r)
        {
            // 运行时已经有重打包对策的，不算问题
            var handled = new HashSet<string>(new[] { "Cargo" }, StringComparer.Ordinal);

            var owners = new HashSet<string>(
                payload.Values.Select(f => f.DeclaringType.FullName), StringComparer.Ordinal);

            var hits = new HashSet<string>(StringComparer.Ordinal);

            foreach (TypeDefinition t in AllTypes(module))
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;

                var code = m.Body.Instructions;

                for (var i = 0; i < code.Count; i++)
                {
                    var mr = code[i].Operand as MethodReference;

                    if (mr == null || mr.Name != "SetData") continue;
                    if (mr.DeclaringType.Name != "ComputeBuffer") continue;

                    // 往回找最近的一个「数组字段」装载，那就是喂进去的东西
                    for (int j = i - 1; j >= 0 && j >= i - 8; j--)
                    {
                        if (!(code[j].Operand is FieldReference fr)) continue;
                        if (!(fr.FieldType is ArrayType at)) continue;

                        string elem = at.ElementType.FullName;

                        if (owners.Contains(elem) && !handled.Contains(elem))
                            hits.Add($"{elem}（{t.FullName}::{m.Name}）");

                        break;
                    }
                }
            }

            foreach (string h in hits.OrderBy(x => x, StringComparer.Ordinal))
                r.Blockers.Add(
                    $"载荷结构体 {h} 会被原样上传到 ComputeBuffer——" +
                    "加字段会让它的 stride 和游戏里写死的那个对不上，表现是启动即 ArgumentException，" +
                    "栈里指不到你加的字段。要么把它放进 GpuUploaded 暂时摘掉，" +
                    "要么照 CargoWidening.UploadRepacked 的做法给它加重打包");

            r.Notes.Add(hits.Count == 0
                ? "GPU 上传核对：清单里的结构体没有一个被原样喂给 ComputeBuffer"
                : $"GPU 上传核对：{hits.Count} 个结构体会被喂给 GPU，见 Blockers");
        }

        /// <summary>
        /// 要长孪生参数的方法，以及各自哪几个参数位。
        ///
        /// <b>分析器和改写器共用这一个方法。</b> 理由同 <see cref="ResolvePayload"/>：
        /// 两边各写一份选取规则，迟早有一边先改，而那时两边都会报「全过」。
        /// </summary>
        internal static Dictionary<MethodDefinition, List<int>> SelectTwinParams(
            ModuleDefinition module, out int skipped)
        {
            var skip = new HashSet<string>(NotifySink, StringComparer.Ordinal);
            var found = new Dictionary<MethodDefinition, List<int>>();

            skipped = 0;

            foreach (TypeDefinition t in AllTypes(module))
            {
                if (IsDisplayOnly(t)) continue;

                foreach (MethodDefinition m in t.Methods)
                {
                    List<int> idx = null;

                    for (var i = 0; i < m.Parameters.Count; i++)
                    {
                        ParameterDefinition p = m.Parameters[i];

                        if (!LooksLikeInc(p.Name) || !IsIntegerPayload(p.ParameterType)) continue;

                        (idx ?? (idx = new List<int>())).Add(i);
                    }

                    if (idx == null) continue;

                    if (skip.Contains(t.FullName + "::" + m.Name)) { skipped++; continue; }

                    found[m] = idx;
                }
            }

            return found;
        }

        private static void CountParams(ModuleDefinition module,
            IDictionary<string, FieldDefinition> payload, Report r)
        {
            Dictionary<MethodDefinition, List<int>> sel = SelectTwinParams(module, out int skipped);

            r.SkippedParamMethods = skipped;
            r.ParamMethods = sel.Count;
            r.ParamSlots = sel.Values.Sum(v => v.Count);

            // 侧信道的寄存器数量由「单个方法最多几个载荷参数位」决定：
            // 跨边界传递时每个槽位要一个独立寄存器，否则同一次调用里两个品质会互相覆盖。
            foreach (KeyValuePair<MethodDefinition, List<int>> kv in sel)
            {
                if (kv.Value.Count <= r.MaxParamSlots) continue;

                r.MaxParamSlots = kv.Value.Count;
                r.MaxParamSlotsAt = kv.Key.FullName;
            }
        }

        /// <summary>
        /// 载荷字段所在的类型里，有几条 <c>Export</c>/<c>Import</c> 流要加版本分支。
        /// 那些流是<b>位置相关</b>的：多读一个 int 会把它后面的一切错位。
        /// </summary>
        private static void CountSaveStreams(ModuleDefinition module,
            IDictionary<string, FieldDefinition> payload, Report r)
        {
            var owners = new HashSet<string>(
                payload.Values.Select(f => f.DeclaringType.FullName), StringComparer.Ordinal);

            foreach (TypeDefinition t in AllTypes(module))
            {
                if (!owners.Contains(t.FullName)) continue;

                bool hasExport = t.Methods.Any(m => m.Name == "Export" && m.HasBody);
                bool hasImport = t.Methods.Any(m => m.Name == "Import" && m.HasBody);

                if (hasExport && hasImport) r.SaveStreams++;
                else if (hasExport || hasImport)
                    r.Notes.Add($"{t.FullName} 只有 Export / Import 其中一半，存档分支要单独想");
            }
        }

        // ── 小工具 ──────────────────────────────────────────────

        /// <summary>
        /// 整数、整数数组，或者<b>整数的引用</b>。
        ///
        /// 最后那一条是补上的，而它漏掉时的表现正是这个仓库最怕的那种：
        /// <c>Int32&amp;</c> 的 <c>MetadataType</c> 是 <c>ByReference</c> 而不是 <c>Int32</c>，
        /// 所以第一版把<b>每一个 out / ref 的 inc 参数都判成了「不是载荷」</b>——
        /// 参数种子数报 55，真实是 83，而报告上一切正常。
        /// out 参数恰恰是「取货时把点数带出来」那一侧，漏掉它等于品质只进不出。
        /// </summary>
        private static bool IsIntegerPayload(TypeReference t)
        {
            if (t is ArrayType arr) return IsIntegerPayload(arr.ElementType);

            if (t is ByReferenceType byRef) return IsIntegerPayload(byRef.ElementType);

            switch (t.MetadataType)
            {
                case MetadataType.Byte:
                case MetadataType.SByte:
                case MetadataType.Int16:
                case MetadataType.UInt16:
                case MetadataType.Int32:
                case MetadataType.UInt32:
                    return true;
                default:
                    return false;
            }
        }

        private static bool LooksLikeInc(string n) =>
            n == "inc" || n == "_inc" || n.EndsWith("Inc", StringComparison.Ordinal)
            || n.StartsWith("inc", StringComparison.Ordinal);

        private static bool LooksLikeCount(FieldDefinition f) =>
            f.Name == "count" || f.Name == "stack" || f.Name == "itemCount"
            || f.Name.EndsWith("Count", StringComparison.Ordinal);

        /// <summary>
        /// 只负责画的类型。它们碰载荷字段是为了显示，既不孪生也不手工处理。
        /// <b>按名字前缀判定是够的</b>：DSP 的界面类全部以 UI 开头，而这个判断
        /// 一旦判错方向（把真正搬运的类当成 UI）会让品质静默蒸发——
        /// 所以判错的那一侧会在 Suspects 里露出来。
        /// </summary>
        private static bool IsDisplayOnly(TypeDefinition t) =>
            t.Name.StartsWith("UI", StringComparison.Ordinal)
            || t.FullName.StartsWith("UI", StringComparison.Ordinal);

        private static string Key(FieldReference f) => f.DeclaringType.FullName + "::" + f.Name;

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module) =>
            module.Types.SelectMany(WithNested);

        private static IEnumerable<TypeDefinition> WithNested(TypeDefinition t)
        {
            yield return t;

            foreach (TypeDefinition n in t.NestedTypes)
            foreach (TypeDefinition x in WithNested(n))
                yield return x;
        }
    }
}
