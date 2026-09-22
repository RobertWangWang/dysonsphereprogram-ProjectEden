#pragma warning disable 649 // 字段由 JsonUtility 反射赋值

using System;

namespace ProjectEden.Utils
{
    /// <summary>data/megabuildings.json 的映射类型。JsonUtility 要求公开字段 + [Serializable]。</summary>
    [Serializable]
    internal class MegaBuildingsConfig
    {
        /// <summary>CommonAPI TabSystem 的分页标识，需全局唯一</summary>
        public string tabId;

        public string tabName;
        public string tabIconPath;

        /// <summary>
        /// 底部建造栏的分类号。原版只用 1~9，UIBuildMenu.StaticLoad 本身接受到 15，
        /// 但按钮要自己建（见 BuildMenuCategoryPatches）。12 与 ProjectGenesis 一致。
        /// </summary>
        public int buildCategory;

        /// <summary>分类按钮的水平微调（局部坐标）。位置本身由实测间距算出，这里只做补偿。</summary>
        public float categoryButtonNudgeX;

        /// <summary>从哪个原版建筑取物品模板（类型、建造模式等）。2318 = 制造台 Mk.IV</summary>
        public int copyFromItemId;

        /// <summary>
        /// 从哪个原版模型克隆外观。49 = 物流运输站。
        /// 必须选一个带传送带接口（PrefabDesc.portPoses）的模型，否则传送带无法直连——原版组装机没有。注意 PrefabDesc.slotPoses 是分拣器口，名字和 SlotConfig 里刚好反过来。
        /// </summary>
        public int copyFromModelId;

        /// <summary>
        /// 用程序化生成的几何替掉克隆来的原版模型。
        ///
        /// 关掉则五座建筑退回「同一个模型 + 不同颜色」——
        /// 因为 <see cref="copyFromModelId"/> 是全局的，五座本来就只克隆一个模型。
        /// </summary>
        public bool proceduralModels;

        /// <summary>
        /// 连金属度/光滑度贴图（<c>_MS_Tex</c>）一起换成常量图。
        /// <b>默认关</b>：试过一次，结果是整座建筑完全不可见。
        /// </summary>
        public bool overrideMetalSmoothTex;

        /// <summary>
        /// 把主贴图（<c>_MainTex</c>）换成自绘图集。关掉就沉用原版贴图——
        /// 那张图是按原版网格 UV 排的，配新几何会花，但至少能验证“是不是贴图把它弄没了”。
        /// </summary>
        public bool overrideMainTex;

        /// <summary>
        /// 程序化模型相对原版包围盒的缩放。1 = 撑满整个盒子。
        /// <b>包围盒不是占地面积</b>：原版物流运输站是细塔配细腿，
        /// 盒子里绝大部分是空的，撑满会显得大一大圈。默认 0.6。
        /// </summary>
        public float modelScale;

        /// <summary>
        /// prefabDesc.assemblerSpeed，10000 = 1 倍速。
        ///
        /// 真正的吞吐上限是每 tick 一个配方周期（60 周期/秒），与本值无关：
        /// AssemblerComponent.InternalUpdate 里 time 只在 time &lt; timeSpend 时累加，
        /// 所以 speed 一旦达到配方的 timeSpend 就已经跑满，再高没有收益。
        /// 技术上限是 time（Int32）的溢出线，约 21.47 亿。
        /// </summary>
        public int assemblerSpeed;

        /// <summary>
        /// 判定「巨型建筑」的速度阈值，和 assemblerSpeed 解耦，
        /// 免得调整速度时连带改变识别逻辑。
        /// </summary>
        public int megaSpeedThreshold;

        /// <summary>
        /// 每 tick 结算多少个配方周期。原版固定为 1（即 60 周期/秒的引擎上限）。
        /// 实现方式是同一 tick 内多跑几遍原版 InternalUpdate，原料不足会自动停。
        /// </summary>
        public int cyclesPerTick;

        /// <summary>
        /// <b>全局分频：每 G 个 tick 才摸一次这座建筑，轮到时跑 G 倍周期。</b>
        /// 1 = 关（默认）。<b>吞吐不变</b>，变的只是每帧要碰多少台。
        ///
        /// <para><b>为什么吞吐不变（推导，不是估计）。</b> 分频器变成 <c>每座 × G</c>
        /// （<see cref="Patches.MegaThrottle.Decide"/>），周期数变成 <c>基准 × G</c>
        /// （<see cref="Patches.MegaThrottle.CyclesFor"/>），于是</para>
        /// <code>吞吐 = 周期 / 分频 = (基准 × G) / (每座 × G) = 基准 / 每座</code>
        /// <para>和 G 无关。这对反物质那四座（<c>cyclesPerTick 1 / tickDivider 70</c>）
        /// 一样成立，所以不需要为它们开特例。相位靠 <c>entityId % divider</c> 错开，
        /// 和原版错开物流站派机是同一个手法。</para>
        ///
        /// <para><b>它想治的是什么：单次 InternalUpdate 的成本随「这颗星球有多少台」超线性增长。</b>
        /// 同一份代码，实测（一局，逐星球）：</para>
        /// <code>
        ///    70 台/tick -&gt;     95 ns/次
        /// 1,354 台/tick -&gt;    544 ns/次
        /// 4,948 台/tick -&gt; 15,543 ns/次
        /// </code>
        /// <para>164 倍的落差，指令数完全相同——所以瓶颈是访存/缓存，不是算术。
        /// 分频把「每帧摸到的台数」按 G 缩小，工作集也跟着缩小。</para>
        ///
        /// <para><b>代价要说在前面：延迟。</b> G = 2 时一台建筑最长 2 tick（33 ms）才轮到一次，
        /// 单次产出翻倍。对生产线没影响（下游是缓冲区），对「盯着面板看数字跳」有影响。
        /// 另外 <see cref="Patches.MegaOutputGatePatches"/> 的产出闸要跟着放大 G 倍，
        /// 否则闸会把那 G 倍周期卡回 1 倍，表现成「开了分频产能掉成 1/G」。</para>
        /// </summary>
        public int globalTickDivider;

        /// <summary>
        /// 电力不足时按供电率线性降速。<b>默认开。</b>
        ///
        /// <b>不开的话巨型建筑对缺电几乎免疫，然后一头撞死</b>——这不是设计，是
        /// 「10000 倍」撞上原版公式的副作用：
        /// <code>
        /// InternalUpdate  IL 0000: if (power &lt; 0.1f) return 0;
        ///                 IL 0576: time += (int)(power * speedOverride);
        /// </code>
        /// 原版 1 倍机器的 <c>speedOverride</c> 是 10000，<c>time</c> 涨得慢一半产量就慢一半，
        /// <b>产出对供电率是线性的</b>。可巨型建筑的 <c>speedOverride</c> 是 1e8，
        /// 单次调用加的 <c>time</c> 比任何 <c>timeSpend</c> 都大一两个数量级——
        /// 供电率 0.11 和 1.00 结算出来一模一样，掉到 0.1 以下则整台停摆。
        ///
        /// 所以真正的节流阀是<b>每 tick 跑几个周期</b>（<see cref="cyclesPerTick"/>），
        /// 和生物温室按日照缩放是同一个旋钮。缩放曲线<b>照抄原版自己的那条</b>：
        /// 线性。不自己发明曲线，这样两边在边界上不会各说各话。
        /// </summary>
        public bool powerScalesCycles;

        /// <summary>
        /// 把 <c>MegaAssemblerPatches.MegaTick</c> 每 tick 的耗时按阶段拆开，每 60 秒报一行。
        ///
        /// <b>它存在的理由是一个分不开的问题</b>：性能面板的「生产设施」一项里，
        /// 原版的配方结算和<b>本 mod 自己插进去的那些每建筑每 tick 的活</b>
        /// （储物格同步要扫 30 格、传送带槽位 12 个、催化剂床、燃烧厂）是混在一起的——
        /// 因为 MegaTick 就挂在装配 tick 里。两种情况该动的地方完全不同，而没有这行日志
        /// 就只能猜。
        ///
        /// 代价是每台每 tick 多读四次 <c>Stopwatch.GetTimestamp()</c>（Windows 上就是
        /// QueryPerformanceCounter，几十纳秒一次），量级在 1% 以内。**定了方向之后关掉即可。**
        /// </summary>
        public bool phaseTiming;

        /// <summary>
        /// 批量结算：把一个 tick 里的 N 个配方周期，从「调 N 遍原版」变成
        /// 「调一遍原版 + 一次乘法」。见 <c>MegaBatchSettle</c> 的类注释。
        ///
        /// <b>它带着一道复现的闸</b>（输出闸那张按 recipeType 的 100 / ×9 / ×19 表），
        /// 所以配了回放自检（<c>MegaBatchAudit</c>）：定期在副本上跑一遍原版，
        /// 对不上就<b>整局自动关掉批量、退回逐次</b>并报 ERROR。
        /// 关掉这个开关等价于永远走逐次——产能和正确性完全一样，只是慢。
        /// </summary>
        public bool batchSettle;

        public int stackSize;
        public int hpMax;

        // ── 物流站能力：让巨型建筑同时作为行星内物流站工作 ──
        public bool stationEnabled;

        public int stationMaxItemCount;
        public int stationMaxItemKinds;
        public int stationMaxDroneCount;
        public long stationMaxEnergyAcc;

        /// <summary>
        /// 虚拟行星物流：直接在储物格之间搬货，不让无人机真的飞（省渲染）。
        /// 必须双向，只做入库的话别的站仍会派车来取产物。
        /// </summary>
        public bool virtualLogistics;

        /// <summary>虚拟搬运的间隔 tick 数。0 = 10</summary>
        public int virtualIntervalTicks;

        /// <summary>虚拟入库时每个储物格囤到多少（不超过格位上限）。0 = 100000</summary>
        public int virtualStockPerSlot;

        /// <summary>每种原料在储物格里备多少份配方用量</summary>
        public int requireStockMultiplier;

        /// <summary>运输机运送量百分比（面板上的「运送量」）</summary>
        public int deliveryDronePercent;

        /// <summary>所有巨型建筑共用同一份配方原料（当前为 1 铁块 + 1 铜块）</summary>
        public int recipeTimeSpend;

        public int[] recipeItems;
        public int[] recipeItemCounts;

        public MegaBuildingEntry[] buildings;

        /// <summary>合成器横向翻页的总页数，每页 14 列。1 = 维持原版、不加滚动条</summary>
        public int replicatorPages;

        /// <summary>
        /// 把本 mod 默认落在<b>第 1 页</b>的物品与配方，整体搬到本 mod 自己的分页上。
        ///
        /// <b>为什么要搬。</b> 原版物品第 1 页实测 111/112 格已占，
        /// 所以本 mod 的物品只能往第 14 列之外溢出——而画物品格的四个窗口都硬裁
        /// <c>col &gt;= 14</c>，落到扩展列就等于这件物品<b>在掉落过滤和信号窗口里不存在</b>，
        /// 在物品选取窗口里也得靠横向翻页或搜索才捞得回来。配方那边同理。
        ///
        /// <b>为什么第 3 页是安全的。</b> 页号就是标签页号，搬到一个不存在的标签
        /// 等于让东西彻底消失。本 mod 已经通过 CommonAPI 的 TabSystem 注册了自己的分页
        /// （<see cref="ProjectEden.MegaBuildingRegistry.TabIndex"/>，注释里就写着「它同时就是
        /// GridIndex 的页号」），而 CommonAPI 的 <c>TabSystem.SetHooks</c> 同时挂了
        /// <c>UIReplicatorPatch</c>、<c>UIRecipePickerPatch</c> 和 <b><c>UIItemPickerPatch</c></b>
        /// ——三个窗口都会多出这一页，所以物品和配方都够得到。
        ///
        /// <b>只搬第 1 页。</b> 建筑类物品本来就落在第 2 页（建筑标签），
        /// 那是它们该待的地方，实测也没有溢出到第 14 列之外，不动它。
        ///
        /// 关掉就退回原来的行为（第 1 页 + 扩展列 + 横向翻页）。
        /// </summary>
        public bool ownTabForModProtos;
    }

    [Serializable]
    internal class MegaBuildingEntry
    {
        public int itemId;

        /// <summary>期望的模型 ID，实际值可能被 ResolveModelId 下调</summary>
        public int modelId;

        public int recipeId;

        public string displayName;
        public string description;

        /// <summary>assets/icons/&lt;iconName&gt;.png</summary>
        public string iconName;

        /// <summary>
        /// ERecipeType。原版：1=Smelt 2=Chemical 3=Refine 4=Assemble 5=Particle；
        /// <b>9~14 是本 mod 的自定义区间</b>（9 电化学、10 氧化还原、11 生化培养）。
        /// 借原版类型意味着原版机器也做得了那些配方；用自定义类型则是这一座的专属。
        /// </summary>
        public int recipeType;

        /// <summary>
        /// 物品提示栏「类型」那一行的文字。<b>只在 <see cref="recipeType"/> 是自定义类型时才需要</b>：
        /// 原版 <c>ItemProto.typeString</c> 是 <c>assemblerRecipeType - 1</c> 的跳转表，
        /// 借原版类型的建筑自己就能查到，自定义类型会落到 default 显示成不相干的词。
        /// 留空则退回建筑名。
        /// </summary>
        public string machineTypeName;

        /// <summary>
        /// 整座建筑的产能随日照变化：<b>满日照 = 配置的满速，零日照 = 停工</b>，
        /// 它跑的所有配方一起停。算法见 <see cref="Patches.MegaLightPatches"/>，
        /// 与太阳能板（<c>PowerGeneratorComponent.EnergyCap_PV</c>）完全一致。
        ///
        /// <b>按建筑而不是按配方。</b> 早先按配方判定过（只有光合育林晒太阳），
        /// 按所有者的要求改成整座建筑；<c>ores.json</c> 里那个同名的配方开关已删除。
        /// </summary>
        public bool lightDependent;

        /// <summary>合成器面板里的位置（行、列）。页号取自分页索引，运行时才确定。</summary>
        public int gridRow;

        public int gridCol;

        /// <summary>在本 mod 分页里的建造栏格位（1 起）</summary>
        public int slot;

        public long idleEnergyPerTick;
        public long workEnergyPerTick;

        /// <summary>
        /// 这一座每 tick 结算几个配方周期。**0 = 跟顶层那个全局值**。
        ///
        /// <b>它不是「倍速」旋钮，填 1 也不是 1 倍速。</b> <c>speed</c> 是 1e8，
        /// 比任何配方的 <c>timeSpend</c> 大一两个数量级，所以一个 tick 就能把计时器填满——
        /// 填 1 就是 60 次/秒，对一条 35 秒的配方而言是 <b>2100 倍</b>。
        /// 真要慢下来看 <see cref="tickDivider"/>。
        /// </summary>
        public int cyclesPerTick;

        /// <summary>
        /// 几个 tick 才让这一座结算一次。**0 或 1 = 每 tick 都结算**。
        ///
        /// 这才是真正能把巨型建筑拉慢的那个旋钮：<c>cyclesPerTick</c> 的下界是 1，
        /// 而 1 已经是 60 次/秒；要更慢只能让它**有些 tick 干脆不产**。
        /// 实现复用 <see cref="MegaLightPatches.Suppress"/>（生物温室晚上停产那一套），
        /// 带 <c>entityId</c> 错帧，缺电时自动拉长——见 <see cref="MegaThrottle"/>。
        ///
        /// 换算：某配方 <c>t</c> 秒，想要 <c>n</c> 倍速 → <c>tickDivider ≈ 60t / n</c>。
        /// 例：35 秒配方要 20 倍速 → 60×35/20 = <b>105</b>。
        /// </summary>
        public int tickDivider;

        /// <summary>
        /// 建在这几类恒星的星系里就有加成。取值是 <c>EStarType</c> 的名字
        /// （<c>BlackHole</c> / <c>NeutronStar</c> / <c>WhiteDwarf</c> / <c>GiantStar</c> /
        /// <c>MainSeqStar</c>），写法和 <c>ores.json</c> 里 <c>placement.starTypes</c> 一致。
        /// 留空 = 建在哪都一样。
        ///
        /// <b>这是「就地生产」的奖励，驱动的是玩家真的在做的那个决策。</b>
        /// 反物质线的三种原料只在黑洞／中子星系产，而**黑洞系只有 1 颗行星**
        /// （<c>StarGen.CreateStarPlanets</c> @009B–00A5，写死无随机）——那颗行星既放不下
        /// 整条产线，也没有任何扩张余地。于是玩家要在「把矿运出去、在别处敞开了建」
        /// 和「挤在那一颗星球上换加成」之间分配，而配套（蓄能柜、碳化钨、活性复合材）
        /// 全得反向运进去。
        ///
        /// <b>刻意不挂 <c>star.mass</c>。</b> 量过：黑洞质量是
        /// <c>18 + (r1 × r2) × 30</c>（<c>StarGen.CreateStar</c> @024F–0265），
        /// 两个随机数相乘、极度偏左（中位 23.6，85% 低于 33）。而**默认 64 星的一局只有
        /// 1 个黑洞**，所以质量对一个存档而言是个常数——挂在它上面，玩家不是在做决策，
        /// 是在抽种子，而且八成抽到个小数。同一族的教训见合金那条「单轴阈值会让第二个
        /// 自由度变成摆设」。
        /// </summary>
        public string[] bonusStarTypes;

        /// <summary>
        /// 在 <see cref="bonusStarTypes"/> 那几类星系里的提速倍数。&lt;= 1 或没配 = 无加成。
        ///
        /// 作用在 <see cref="tickDivider"/> 上（除以它），**不动产量、不克隆配方**——
        /// 合金那套逐建筑改 <c>recipeExecuteData</c> 的重机器这里一点都用不上。
        /// 而且和缺电缩放叠在同一个量上，两者不会打架。
        ///
        /// <b>代价要说明白：面板上看不出来。</b>「制造速度」那一行读的是 <c>speed</c>，
        /// 而 <c>speed</c> 永远是 1e8、绝不能动（动了这台建筑就不再被 <c>MegaTick</c> 接管）。
        /// 和生物温室的日照是同一个坑，只能靠文档和日志说。
        /// </summary>
        public float bonusSpeedup;

        /// <summary>
        /// 发电段。配了这一段，这座巨型建筑就<b>同时</b>是一台发电机。
        ///
        /// <b>这不是在绕过组件模型，是组件模型本来就允许。</b>
        /// <c>PlanetFactory.CreateEntityLogicComponents</c> 里 <c>isPowerGen</c>（IL 059E）
        /// 和 <c>isAssembler</c>（IL 1122）是两个独立的顺序 if，<c>EntityData</c> 也有
        /// 各自的 <c>powerGenId</c> / <c>assemblerId</c> / <c>stationId</c> / <c>powerConId</c>。
        /// 同一台实体挂四个组件，先例是综合物流枢纽（station + dispenser）。
        ///
        /// 留空（null）就是普通的巨型建筑，一点发电逻辑都不会挂上去。
        /// </summary>
        public MegaGeneratorEntry generator;

        /// <summary>
        /// 能量枢纽段。配了这一段，这座巨型建筑就<b>同时</b>是一台能量枢纽——
        /// 给蓄电器充电、或者把充满的蓄电器放回电网。
        ///
        /// <b>为什么是枢纽而不是发电机。</b> 发电机会把燃料**吃掉**；枢纽放完电
        /// 把空壳还给你。一座每分钟吃 20 个壳、一个不还的电厂不是电池，是材料黑洞。
        /// 原版把「充电 → 搬走 → 放电」这件事建模成枢纽，不是发电机，这里照它走。
        ///
        /// 组件模型允许同一台实体挂多个：<c>PlanetFactory.CreateEntityLogicComponents</c>
        /// 里 <c>isPowerExchanger</c>（IL 08A9）和 <c>isPowerGen</c>（059E）、
        /// <c>isAssembler</c>（1122）、<c>isStation</c>（14FE）都是独立的顺序 if。
        ///
        /// 留空（null）就是普通的巨型建筑。
        /// </summary>
        public MegaExchangerEntry exchanger;

        public float tintR;
        public float tintG;
        public float tintB;

        /// <summary>
        /// 这台机器<b>额外</b>接受的配方类型。留空 = 只跑 <see cref="recipeType"/> 那一种（原版行为）。
        ///
        /// <b>原版是「一台机器一种类型」，而且这句话是硬的</b>——配方选择器按
        /// <c>filter != recipe.Type</c> 过滤，蓝图与复制粘贴那一族处处比
        /// <c>BuildingParameters.recipeType == prefabDesc.assemblerRecipeType</c>。
        /// 填了这个字段就会由 <see cref="Patches.RecipeTypeCompatPatches"/> 把那八处闸门
        /// 换成一次查表，代价与理由都写在那个文件里。
        ///
        /// <b>方向是单向的</b>：综合化学厂（16）接受化学（2），但化工厂不会因此接受 16。
        /// </summary>
        public int[] acceptsRecipeTypes;

        /// <summary>
        /// 单座建筑的建造配方，覆盖顶层那份全局的。留空则沿用全局值。
        ///
        /// <b>为什么要有</b>：顶层 <c>recipeItems</c> 是八座共用的一份（铁块 ×1 + 铜块 ×1），
        /// 而有些建筑就该贵——比如把前一代整台吃进去的那种。
        ///
        /// <b>这里必须是自己的数组</b>，不能改动全局那份：<c>RecipeProto.Items</c> 是按引用挂上去的，
        /// 就地改会同时改掉其余几座（数组是我们的、数组里的内容不是，同一族的坑见 CLAUDE.md）。
        /// </summary>
        public int[] recipeItems;

        public int[] recipeItemCounts;

        /// <summary>单座建筑的建造耗时（帧）。0 = 沿用全局</summary>
        public int recipeTimeSpend;

        /// <summary>
        /// 这一座的体量缩放，覆盖顶层的 <c>modelScale</c>。0 = 沿用全局。
        ///
        /// <b>它和下面那个高度倍率一起，是「九座长得都差不多」的解药。</b>
        /// <c>MeshKit.Place</c> 会把每座都缩放到填满原版占地，所以体量本来是被归一化的——
        /// 细节画得再不同，一归一化就全抹平了。
        /// </summary>
        public float modelScale;

        /// <summary>
        /// 允许这一座长到原版包围盒高度的几倍。0 = 1 倍（原版行为）。
        ///
        /// 精馏塔、提升管那种就该细高（1.4~1.5），对撞机、温室那种就该矮宽（0.75~0.85）。
        /// 不给这个旋钮的话，细高的设计会被高度封顶连带把占地一起压小，
        /// 最后和矮胖的设计落到同一个体量上。
        /// </summary>
        public float modelHeightScale;
    }

    /// <summary>
    /// 一座巨型建筑的发电段。
    ///
    /// <b>写的是绝对值，不是倍率</b>——和 machines.json 的 generator 段刚好相反。
    /// 那边是整台克隆原版电厂，真值在 <c>resources.assets</c> 的 prefab 里、离线读不到，
    /// 所以只能给倍率；这边的 prefab 是物流运输站，根本没有发电字段可乘，
    /// 只能直接写。注册时会把换算成 MW 的结果打进日志。
    /// </summary>
    /// <summary>
    /// 巨型建筑的能量枢纽段：它服务哪一对空/满蓄电器，以及充放功率。
    ///
    /// <b>一台枢纽天然只服务一对。</b> <c>PowerExchangerComponent.emptyId / fullId</c>
    /// 直接来自 <c>PrefabDesc</c>，整个组件（皮带进出、状态机、能量结算）从头到尾
    /// 只认这两个 ID——想服务多对就得把 <c>InternalUpdate</c> 里每一处都接管掉，不划算。
    /// 再建一座正是原版建模这件事的方式。
    /// </summary>
    [Serializable]
    internal class MegaExchangerEntry
    {
        /// <summary>
        /// 它服务的那台蓄电器在 <c>machines.json</c> 里的 key。
        ///
        /// <b>不写物品号。</b> 蓄电器是 <c>MachineRegistry</c> 注册的，而巨型建筑注册在它<b>之前</b>
        /// ——那一刻蓄电器的 ID 还没分配。所以这一段整个在 <c>PostAddDataAction</c> 才落地，
        /// 到那时按 key 反查得到真实 ID。写死号在这里尤其危险：本 mod 的物品 ID 撞车会顺延。
        /// </summary>
        public string vaultMachineKey;

        /// <summary>
        /// 这台枢纽<b>还能改去服务</b>的其它蓄电器（machines.json 的 key）。
        /// 留空就只服务 <see cref="vaultMachineKey"/> 那一对。
        ///
        /// <b>原版一台枢纽只服务一对，而这条能成立是量出来的：</b>
        /// <c>PowerExchangerComponent.emptyId / fullId</c> 虽然来自 <c>PrefabDesc</c>，
        /// 但它们是<b>逐组件字段，而且进存档</b>（<c>Export</c> @00B2/@00BE、
        /// <c>Import</c> @00CA/@00D6）。所以逐台改写之后，**原版自己就把选择存下来了**
        /// ——不需要 IModCanSave，也不需要像合金配比那样自建存储。
        ///
        /// 切换入口不是新加的界面：枢纽窗口里那两个柜位图标的点击处理
        /// （<c>OnEmptyOrFullUIButtonClick</c>）本来就读 <c>player.inhandItemId</c>，
        /// 所以「手上拿着另一档的柜子去点柜位」这个最自然的动作，正好可以当作切档。
        /// </summary>
        public string[] alsoServes;

        /// <summary>
        /// 充放功率，单位是**每 tick 的焦耳**（一秒 60 tick）。
        /// 60 GW 就是 1000000000。<c>PrefabDesc.exchangeEnergyPerTick</c> 是 Int64，够用。
        /// </summary>
        public long energyPerTick;
    }

    [Serializable]
    internal class MegaGeneratorEntry
    {
        /// <summary>每 tick 的发电上限（焦耳）。×60 就是瓦。</summary>
        public long genEnergyPerTick;

        /// <summary>
        /// 每 tick 消耗的<b>燃料能量</b>（焦耳）。
        /// 能量利用率 η = <c>genEnergyPerTick / useFuelPerTick</c>，
        /// 所以这个数必须大于上面那个，否则就是永动机。注册时会核对并报错。
        /// </summary>
        public long useFuelPerTick;

        /// <summary>
        /// 燃料掩码。32 = 药柱专用位（原版占 15，可燃液体占 16）。
        /// 注意<b>烧的时候不查它</b>——<c>EnergyCap_Fuel</c> 只看 <c>fuelCount &gt; 0</c>；
        /// 掩码管的是传送带和手动塞料时哪些东西进得来。
        /// </summary>
        public int fuelMask;

        /// <summary>
        /// 从哪座原版发电建筑身上量「怎么接电网」的参数（默认 2204 = 火力发电厂）。
        ///
        /// <b>为什么非量不可：发电机自己必须也是一个电力节点。</b>
        /// 全汇编里只有 <c>PowerSystem.OnNodeAdded</c> 往 <c>PowerNetwork.generators</c> 里加东西，
        /// 而它加的是 <c>PowerNetworkStructures.Node.genId</c>——也就是<b>节点自己那台发电机</b>。
        /// 对照一下就看得很清楚：<c>NewConsumerComponent</c> 会调 <c>OnConsumerAdded</c> 把耗电体
        /// 挂进覆盖它的电网，而 <c>NewGeneratorComponent</c> <b>一个后续调用都没有</b>。
        /// 所以 <c>isPowerGen</c> 只是「它能发电」，<c>isPowerNode</c> 才是「它接得上电网」——
        /// 原版每座电厂脚下那根连接线就是这件事。
        ///
        /// <b>值要量不要猜</b>：connectDistance / coverRadius 存在 resources.assets 的 prefab 里，
        /// 离线读不到也反编译不出来，写死就是凭记忆猜（蓄电器和发电机倍率那两处已经为同一个理由
        /// 只收倍率不收绝对值）。注册时会把量到的数打进日志。
        /// </summary>
        public int connectFromItemId;
    }
}
