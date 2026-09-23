#pragma warning disable 649 // 字段由 JSON 反序列化赋值

using System;

namespace ProjectEden.Utils
{
    /// <summary>data/ores.json 的映射类型：自定义矿脉的总表。</summary>
    [Serializable]
    internal class OreConfig
    {
        /// <summary>总开关。关掉之后所有自定义矿脉和额外物品都不注册</summary>
        public bool enabled;

        /// <summary>不属于任何矿脉的额外物品，比如配方的副产物。先于矿脉注册，好让配方能引用</summary>
        public ExtraItemEntry[] items;

        /// <summary>逐个矿种的定义</summary>
        public OreEntry[] ores;

        /// <summary>
        /// 不属于任何矿种的配方，比如「电解水」。
        /// 和矿种下挂的配方是同一个结构，区别只是 <c>ref</c> 不能写
        /// <c>ore</c> / <c>ingot</c>（没有「本矿种」可言），只能引 items 段的 key
        /// 或别的矿种的 <c>key.ore</c> / <c>key.ingot</c>。
        /// </summary>
        public OreRecipeEntry[] recipes;

        /// <summary>投放到气态巨星、由轨道采集器收集的气体</summary>
        public GasEntry[] gases;

        /// <summary>
        /// 改**原版物品**的热值。本 mod 的热值全部锚在煤上（393.5 kJ/mol ↔ 2.7 MJ），
        /// 而原版自己并不自洽——它的氢按摩尔算比煤慷慨约 4 倍，这一条 CLAUDE.md 早就记着。
        ///
        /// 以前这只是个不整齐，直到本 mod 加了一次出 8 份氢的蒸汽重整：
        /// 一条 4 秒的配方凭空多出约 50 MJ 可燃热值，而万倍速建筑的耗电被摊薄到近乎为零，
        /// 于是它成了真正的永动机。
        /// </summary>
        public VanillaHeatConfig vanillaHeat;
    }

    [Serializable]
    internal class VanillaHeatConfig
    {
        public bool enabled;

        public VanillaHeatEntry[] items;
    }

    [Serializable]
    internal class VanillaHeatEntry
    {
        public int id;

        /// <summary>原版那个物品的中文名。**和 ID 交叉核对**——写错号会静默改掉别的物品。</summary>
        public string name;

        public long heatValue;

        /// <summary>
        /// 可选：顺带改燃料位。
        ///
        /// 留 0 就只改热值、不动 <c>FuelType</c>（氢那一条就是这样——它本来就是化学燃料，
        /// 要改的只是数值）。而给一个原版<b>根本没当燃料</b>的东西加热值时必须填它，
        /// 否则那是「半对燃料」：有热值、没有任何一种发电机认它，烧出来是 0 电。
        /// </summary>
        public int fuelType;
    }

    /// <summary>
    /// 一个额外物品：本 mod 新增、但不是矿石也不是锭的东西（二氧化碳这类副产物）。
    /// 图标同样由某个原版物品的图标改色而来，不需要美术资源。
    /// </summary>
    [Serializable]
    internal class ExtraItemEntry
    {
        /// <summary>配方里用 <c>ref</c> 引用的名字，不进存档</summary>
        public string key;

        public bool enabled;

        public string name;
        public string description;

        /// <summary>物品 ID。<b>进存档</b>，定下来别改</summary>
        public int itemId;

        /// <summary>合成面板格位的<b>起点</b>，被占用时自动往后找</summary>
        public int gridIndex;

        public int stackSize;

        /// <summary>「制造于」那一栏的文字</summary>
        public string produceFrom;

        /// <summary>
        /// 「采集自」那一栏的文字。挖出来 / 抽上来的东西填这个，
        /// 合成出来的填 <see cref="produceFrom"/>——原版就是这么分的（水是「采集自 海洋」）。
        /// </summary>
        public string miningFrom;

        /// <summary>
        /// 气体/液体。<b>决定能不能进储液罐</b>——不只是提示文字：原版空罐从皮带取货时，
        /// 直接把流体白名单当过滤数组传进去（见 ProjectEdenPlugin.RefreshFluidList）。
        /// 传送带和物流站则一视同仁，不受影响。
        /// </summary>
        public bool isFluid;

        /// <summary>
        /// 燃料类型，位掩码。发电建筑按 <c>prefabDesc.fuelMask &amp; FuelType</c> 判收不收。
        /// 填 0 表示不是燃料。
        ///
        /// <b>实测占用情况（FuelSurvey 在游戏里读的，不是凭记忆）：</b>
        /// 1 化学燃料（26 种，火力发电厂与机甲反应堆）／2 氘核燃料棒／
        /// 4 反物质与金色燃料棒／8 蓄电器（满）／<b>16 本 mod 的可燃液体</b>／
        /// <b>32 本 mod 的药柱与金属燃料</b>。
        /// <c>ItemProto.fuelNeeds</c> 长 64 且按掩码值索引，所以合法位只有 bit 0~5，
        /// 而 32 已经被药柱那条线占掉——<b>六位全满（掩码 63），一个空位都没有了</b>。
        ///
        /// <b>但「位满了」多数时候不是障碍，因为新燃料本来就该共用一位。</b>
        /// 原版氢燃料棒自己就是 1：它是化学燃料，烧它的是火力发电厂和机甲反应堆。
        /// 只有当新燃料<b>不该被现有建筑烧</b>时才需要独占一位——可燃液体要 16、
        /// 药柱要 32，都是这个理由。
        /// </summary>
        public int fuelType;

        /// <summary>
        /// 热值，单位<b>焦耳</b>。原版参照：煤矿 2.7e6、原油 4.05e6、可燃冰 4.8e6、
        /// 氢 <b>9e6</b>（实测；这里以前写的 8e6 是凭记忆写的）。
        /// 配了 fuelType 就必须配它，否则烧起来是 0 电。
        /// </summary>
        public long heatValue;

        /// <summary>
        /// 机甲反应堆的**功率**加成，写进 <c>ItemProto.ReactorInc</c>。
        /// <c>Mecha.GenerateEnergy</c> 算 <c>ratio = ReactorInc + 1</c> 再乘到
        /// <c>reactorPowerGen</c> 上，所以 1.0 = 功率 ×2。留 0 就是 ×1。
        ///
        /// <b>它只改放电速率，不改总能量</b>——总量是 <see cref="heatValue"/>。
        /// 所以高倍率意味着同一份燃料烧得更快、撑得更短，两者是独立的两条轴。
        ///
        /// <b>实测的原版阶梯（FuelSurvey 读出来的，不是凭记忆）：</b>
        /// 原油 0.2（×1.2）／精炼油 0.3（×1.3）／蓄电器（满）1.0（×2）／
        /// <b>氢燃料棒 2.0（×3）</b>／氘核燃料棒 3.0（×4）／反物质燃料棒 5.0（×6）／
        /// 金色燃料棒 11.0（×12）。
        /// 本文件和 machines.json 以前写的那份表有四条是错的（原油连符号都反了），
        /// 别再照那份抄。
        /// </summary>
        public float reactorInc;

        /// <summary>
        /// 增产剂等级，写进 <c>ItemProto.Ability</c>。填 0 表示这不是增产剂。
        ///
        /// <b>喷涂机直接读它当等级</b>（<c>SpraycoaterComponent.InternalUpdate</c>：
        /// <c>incAbility = ItemProto.Ability</c>），全方法没有一处把它钳在 4——
        /// <c>Cargo.kSprayIncMax = 4</c> 是个没有任何实现的 <c>const</c>。
        /// 各张增产剂表都填到 10 级，原版只用到 4。
        ///
        /// <b>但真正的上限是 <c>Cargo.inc</c>。</b> 喷涂那一步是
        /// <c>cargo.inc = stack × Ability</c>，preloader 把 inc 加宽成 Int16 之后
        /// 上限是 <c>32767 / 集装层数</c>——集装和等级是同一笔预算里的两项开销，
        /// <c>ProliferatorSurvey</c> 每次启动会把当前上限算出来。
        /// </summary>
        public int ability;

        /// <summary>
        /// 一份增产剂能喷多少件，写进 <c>ItemProto.HpMax</c>
        /// （<c>incSprayTimes = ItemProto.HpMax</c>）。
        ///
        /// 原版三档给出的规律是 <c>HpMax = k × Ability</c>，k 走 12 / 12 / 15。
        /// </summary>
        public int hpMax;

        /// <summary>
        /// 自制图标：assets/icons/&lt;icon&gt;.png（80×80，透明底）。
        /// 填了就直接用这张，下面那组改色参数全部忽略。
        /// </summary>
        public string icon;

        /// <summary>没配 icon 时，图标从哪个<b>原版物品</b>的图标改色而来</summary>
        public int iconFrom;

        /// <summary>
        /// <see cref="iconFrom"/> 的按名字版本：填原版物品的 <c>ItemProto.Name</c>，
        /// 注册时反查成 ID。<b>只在 <see cref="iconFrom"/> 没填时才看它。</b>
        ///
        /// 存在的理由和配方里的 <c>ref: "vanilla:…"</c> 完全一样：<b>原版 proto 在
        /// resources.assets 里，离线枚举不出来</b>，常用的几个（水 1000、煤矿 1006……）是记住的，
        /// 引力透镜、卡西米尔晶体这些不是——而写错一个号不会报错，只会静默拿另一件物品当模板，
        /// 连图标带 DescFields 一起错。
        ///
        /// 比的是 <c>Proto.Name</c>（原始键）而不是 <c>proto.name</c>（翻译过的），
        /// 否则英文客户端上必然匹配失败——这条错本仓库已经犯过一次，记在
        /// CLAUDE.md 的 English localization 一节。
        /// </summary>
        public string iconFromName;

        public float iconHue;
        public float iconSaturationScale;
        public float iconMinSaturation;
        public float iconValueScale;
    }

    /// <summary>
    /// 一个矿种的<b>投放规则</b>：铺到哪些星球主题上、按普通矿脉位还是稀有槽、母星系刷不刷。
    ///
    /// 留空（矿种里不写 <c>placement</c>）就是老行为：<b>凡是产铁的主题都按 veinRarity 铺普通矿脉位</b>。
    /// </summary>
    [Serializable]
    internal class PlacementEntry
    {
        /// <summary>
        /// <c>normal</c>（默认）= 占普通矿脉位（<c>ThemeProto.VeinSpot</c>，密度按 veinRarity 乘铁矿）；
        /// <c>rare</c> = 占<b>稀有槽</b>（<c>ThemeProto.RareVeins</c>，像金伯利矿那样按概率整颗星出现）。
        /// </summary>
        public string mode;

        /// <summary>
        /// 只铺到这些主题上，按 <c>ThemeProto.DisplayName</c> 做<b>包含匹配</b>（写「熔岩」能同时命中「熔岩」和「潮汐锁定熔岩」）。
        /// 留空 = 不限主题（normal 模式下仍然只挑产铁的主题）。
        ///
        /// <b>主题表在 resources.assets 里，离线看不到</b>——开局日志会把实际的主题名全打一遍，
        /// 照着改就行。匹配不到任何主题会报 ERROR，不会静默失效。
        /// </summary>
        public string[] themes;

        /// <summary>
        /// 母星系（<c>star.index == 0</c>）刷不刷。<b>false = 母星系一颗都没有</b>。
        ///
        /// 稀有槽本来就有「母星系专用概率」这一档（<c>RareSettings[i*4+1]</c>），
        /// 所以这是原版就支持的事，填 0 即可；普通矿脉位没有这一档，
        /// 要排除母星系得靠生成时拦截，见 OreBirthSystemPatches。
        ///
        /// <b>可空是刻意的：<c>null</c>（没写这个字段）和显式写 <c>false</c> 必须分得开。</b>
        /// 之前是裸 <c>bool</c>，于是「没配」默认成了 false，而 normal 模式那条
        /// 「配了 false 但这一档不存在」的提醒就对**每一个**普通矿脉位的矿都要打一遍——
        /// 冰矿脉是第一个 normal 模式的矿，所以它第一次暴露出来。行为一直是对的，
        /// 报的那句话是假的，而一句假的提醒比没有提醒更贵。
        /// 和 megabuildings.json 里那几个亮度旋钮改成 <c>float?</c> 是同一族：
        /// <b>哨兵值不能和合法取值撞车</b>。
        /// </summary>
        public bool? birthSystem;

        /// <summary>rare 模式：非母星系里，一颗星球出现这种矿的概率。原版稀有矿大致 0.03 ~ 0.6</summary>
        public float chance;

        /// <summary>rare 模式：出现之后，每再追加一个矿脉位的概率（原版最多连滚 11 次）</summary>
        public float extraChance;

        /// <summary>rare 模式：矿脉的储量/浓度系数</summary>
        public float richness;

        /// <summary>
        /// <b>star 模式</b>：按<b>星体类型</b>投放，而不是按星球主题。
        /// 取值是 <c>EStarType</c> 的名字：<c>BlackHole</c> / <c>NeutronStar</c> /
        /// <c>WhiteDwarf</c> / <c>GiantStar</c> / <c>MainSeqStar</c>。写错会在注册时报错。
        ///
        /// <b>为什么需要这一档：单极磁石根本不走主题表。</b> 实测——主题表 25 张里
        /// 矿种 14 出现 0 次，而 <c>PlanetAlgorithm.GenerateVeins</c> IL 016A 读
        /// <c>planet.star.type</c>、随后直接 <c>veinSpots[14]++</c>。所以黑洞矿只能
        /// 沿着同一条路走，见 <c>StarVeinPatches</c>。
        /// </summary>
        public string[] starTypes;

        /// <summary>star 模式：命中之后放几处矿脉簇</summary>
        public int spots;

        /// <summary>star 模式：每簇的矿脉数（对应 <c>ThemeProto.VeinCount</c>）</summary>
        public float count;

        /// <summary>star 模式：储量浓度（对应 <c>ThemeProto.VeinOpacity</c>）</summary>
        public float opacity;

        /// <summary>
        /// star 模式：该星系的**第一颗行星保底出一处**。
        ///
        /// 黑洞星系本来就少，纯概率会让「极稀有」和「整局没有」在玩家那里
        /// 变成同一件事——莫桑石为这条付过一次账（chance 0.02 时期望不到一颗，
        /// 玩家扫完整个星区报「没找到」，而注册、主题、矿表全是对的）。
        /// </summary>
        public bool guarantee;
    }

    /// <summary>
    /// 一种投放到气态巨星的气体。
    ///
    /// <b>气体的生成和矿脉是同一个套路，都在 ThemeProto 上。</b>
    /// 矿脉走 <c>VeinSpot / VeinCount / VeinOpacity</c>，气体走
    /// <c>GasItems / GasSpeeds</c>；<c>PlanetGen.SetPlanetTheme</c> 里
    /// <b>种类是原样照抄主题的，一点随机都没有</b>，随机只作用在速率上（×0.909~1.100），
    /// 之后再乘全局的 <c>gasCoef</c> 和 <c>star.resourceCoef^0.3</c>。
    ///
    /// 只对<b>还没生成过的星球</b>生效，和矿脉一样。
    /// </summary>
    [Serializable]
    internal class GasEntry
    {
        public bool enabled;

        /// <summary>
        /// 投放哪个物品。写 items 段某条的 key（或「矿种key.ore」这类全名）。
        /// <b>不写死 ID</b>：新物品 ID 撞车时会顺延，写死的数字会指到别人家去。
        /// </summary>
        public string @ref;

        /// <summary>
        /// 速率，<b>相对于该主题里现有气体的最高速率</b>的倍数。
        ///
        /// 用倍数而不是绝对值，是因为各个气巨主题的基准速率写在
        /// <c>resources.assets</c> 的 ThemeProto 里，离线看不到——
        /// 硬写绝对值就是猜。开局日志会把算出来的真值打出来。
        /// </summary>
        public float speedRatio;
    }

    /// <summary>
    /// 一个自定义矿种。
    ///
    /// <b>矿种编号必须从 15 起连续排。</b> 原版 1~14，EVeinType.Max = 15。
    /// 中间不能留洞：UIPlanetDetail.OnPlanetDataSet 的矿种循环里，原版是
    /// 先 <c>vp.MiningItem</c> 解引用、之后才判 <c>vp == null</c>，
    /// 空号会当场空引用把星球面板打崩（见 CLAUDE.md，实测过）。
    /// </summary>
    [Serializable]
    internal class OreEntry
    {
        /// <summary>日志用的短名，随便起，不进存档</summary>
        public string key;

        public bool enabled;

        // ── 矿石物品 ─────────────────────────────────────────

        public string oreName;
        public string oreDescription;

        /// <summary>矿石的物品 ID。<b>进存档</b>，定下来别改</summary>
        public int oreItemId;

        /// <summary>合成面板格位的<b>起点</b>，被占用时自动往后找</summary>
        public int oreGridIndex;

        /// <summary>
        /// 矿石的自制图标：assets/icons/&lt;oreIcon&gt;.png（80×80，透明底）。
        /// 填了就直接用这张，不再拿铁矿石的图标改色。
        /// </summary>
        public string oreIcon;

        /// <summary>
        /// 让这条矿脉<b>直接产出一个已经存在的物品</b>，而不是新注册一个矿石。
        /// 写 <c>vanilla:名字</c>（例如 <c>vanilla:水</c>）。
        ///
        /// <para><b>为什么要有它：有些矿脉挖出来的东西游戏里本来就有。</b>
        /// 冰矿脉挖出来就该是水，而不是「冰矿石」再加一条「冰 → 水」的配方——
        /// 那条配方除了多占一个物品格位和一次点击，什么也没提供。</para>
        ///
        /// <para>填了它就<b>不注册任何新物品</b>：不占物品 ID、不占合成面板格位、
        /// 不参与图标改色（改色会把<b>原版那个物品</b>的图标也换掉，这一点必须挡住）。
        /// 矿脉的 <c>MiningItem</c> 直接指向解析出来的那个号。</para>
        ///
        /// <para>按<b>名字</b>解析而不是写号：原版 proto 全在 <c>resources.assets</c> 里、
        /// 离线枚举不了，手写号码写错不报错、只会安静地让矿脉产出别的东西。
        /// 解析到的号会打进日志。</para>
        ///
        /// <para>和 <see cref="hasIngot"/> 互斥（指向原版物品就谈不上「它的锭」），
        /// 配在一起会大声失败。</para>
        /// </summary>
        public string oreVanillaRef;

        public int stackSize;

        // ── 矿脉 ─────────────────────────────────────────────

        public string veinName;

        /// <summary>
        /// 矿脉的自制图标：<c>assets/icons/&lt;veinIcon&gt;.png</c>（<b>480×480</b>，透明底）。
        /// 填了就直接用这张，不再拿铁矿脉的图标改色。
        ///
        /// <para><b>注意尺寸和矿石图标不是一回事。</b> 矿脉那张是 480×480 的<b>矿簇图</b>
        /// （在地面标签和行星面板里用），矿石是 80×80 的物品图标；混用会在面板里对不齐。</para>
        /// </summary>
        public string veinIcon;

        /// <summary>矿脉编号，同时是 VeinData.type。必须连续，见类注释</summary>
        public int veinId;

        /// <summary>专属模型 ID（克隆铁矿脉的模型再染色）。<b>进存档</b></summary>
        public int veinModelId;

        /// <summary>矿脉材质的染色系数，乘到每个颜色属性上。R/G/B 三个倍率</summary>
        public float[] veinTint;

        /// <summary>「矿脉分布」图表里色块的颜色，RGB 0~1</summary>
        public float[] veinColor;

        /// <summary>矿脉簇数量相对铁矿脉的倍率</summary>
        /// <summary>投放规则：铺到哪些主题、普通位还是稀有槽、母星系刷不刷。留空 = 老行为（凡产铁的主题都铺）</summary>
        public PlacementEntry placement;

        public float veinRarity;

        /// <summary>单簇储量相对铁矿脉的倍率</summary>
        public float veinAmountScale;

        // ── 图标染色（由铁的对应图标改色而来）─────────────────

        public float iconHue;
        public float iconSaturationScale;
        public float iconMinSaturation;
        public float iconValueScale;

        // ── 锭 ───────────────────────────────────────────────

        /// <summary>false 时只注册矿石，不注册锭（配方也就无从谈起）</summary>
        public bool hasIngot;

        public string ingotName;
        public string ingotDescription;

        /// <summary>「制造于」那一栏的文字。留空则按第一条配方的类型自动填</summary>
        public string ingotProduceFrom;

        public int ingotItemId;
        public int ingotGridIndex;

        /// <summary>
        /// 锭的自制图标：assets/icons/&lt;ingotIcon&gt;.png（80×80，透明底）。
        /// 填了就直接用这张，不再拿铁块的图标改色。矿石图标和矿脉图标不受影响。
        /// </summary>
        public string ingotIcon;

        /// <summary>
        /// 锭的燃料位与热值。两个都要给，只给一半会警告并忽略（半对燃料烧出来是 0 电）。
        ///
        /// <b>为什么锭也需要这一对。</b> 金属粉在氧化剂里是真烧得起来的，而且
        /// <b>它的耗氧量只有含碳燃料的一半</b>——Huggett 常数（每 MJ 约耗 13.1 MJ/kg 氧）
        /// 是对含碳燃料成立的经验律，金属不含碳，本来就跳出那条线。
        ///
        /// <b>给 32 而不给 1 是有意的：</b> bit 1 是火力发电厂和机甲反应堆吃的那一位，
        /// 而一块实心金属锭扔进燃煤锅炉不会烧——要先磨粉、还要配氧化剂。
        /// 所以它只在氧化还原燃烧厂里是燃料。
        /// </summary>
        public int ingotFuelType;

        public long ingotHeatValue;

        /// <summary>
        /// <b>矿石</b>的燃料位与热值，规则同上面那一对（只给一半会警告并忽略）。
        ///
        /// <b>存在的理由是能量审计要的是一条完整的链。</b> 核燃料这一族的能量来自
        /// 质量亏损，不是化学键——如果只给末端的燃料棒配热值，那么「矿石 → 浓缩物」
        /// 这一级就成了凭空造能量，审计会在那里炸；而给整条链都配上，每一级就都守恒，
        /// <b>一条豁免都不需要</b>。挖矿本身不是配方、不进审计，所以链的起点在矿石上，
        /// 和煤矿带着 2.7 MJ 出土是同一回事。
        ///
        /// <b>热值不等于「有发电厂烧得了它」。</b> 铀矿石和浓缩铀给的是一个没有任何
        /// 发电厂持有的位（bit 10）：反应堆吃的是燃料组件，不是粉末。这不是半对燃料——
        /// 半对燃料是「有热值没有位」，那种烧出来是 0 电；这是「有位但世上没有那种炉子」。
        /// </summary>
        public int oreFuelType;

        public long oreHeatValue;

        // ── 配方 ─────────────────────────────────────────────

        /// <summary>可以有任意条，也可以一条都没有</summary>
        public OreRecipeEntry[] recipes;
    }

    /// <summary>一条配方。原料和产物都可以混用原版物品与本 mod 的新物品。</summary>
    [Serializable]
    internal class OreRecipeEntry
    {
        /// <summary>
        /// 能量审计的豁免理由。<b>有值即豁免，而值本身就是它为什么该被豁免。</b>
        ///
        /// 审计查的是「产出可燃热值 &gt; 投入可燃热值」，因为万倍速建筑把耗电摊薄到近乎为零，
        /// 任何这样的配方都是一台发电机。但有三类是正当的，必须能声明出来，
        /// 否则审计每次启动都在报同样几条，真出了新洞反而淹没在噪声里：
        ///
        /// <list type="bullet">
        /// <item><b>真实吸热</b> —— 蒸汽重整、蒸汽裂解、水煤气，现实中就要外部供热</item>
        /// <item><b>阳光</b> —— 生物温室的零原料配方，能量来自恒星，而那座建筑本来就受日照约束</item>
        /// <item><b>刻意不给热值的中间体</b> —— 甲醛、聚丙烯腈按 fuelType 判据不给热值，
        ///       于是吃它们的配方看着在造能量，而整条链是负的</item>
        /// </list>
        ///
        /// <b>不要拿它去盖真正的洞。</b> 写理由的时候如果写不出上面三类之一，那就是个洞。
        /// </summary>
        public string energyNote;

        public bool enabled;

        /// <summary>配方名，同时是 LDBTool 记 ID 用的键——<b>改名等于换一条新配方</b></summary>
        public string name;

        public string description;

        /// <summary>配方 ID。<b>进存档</b></summary>
        public int recipeId;

        /// <summary>ERecipeType：1 熔炉 / 2 化工 / 3 精炼 / 4 组装 / 5 粒子</summary>
        public int type;

        /// <summary>耗时，单位 tick（60 = 1 秒）</summary>
        public int timeSpend;

        /// <summary>合成面板格位的<b>起点</b>，被占用时自动往后找</summary>
        public int gridIndex;

        /// <summary>自制图标：assets/icons/&lt;icon&gt;.png。留空则跟随本矿种的锭图标</summary>
        public string icon;

        /// <summary>图标从哪个<b>原版物品</b>改色而来。填 0 且没配 icon 则用本矿种的锭图标</summary>
        public int iconFrom;

        /// <summary>
        /// 这条配方的产物<b>每件带多少品质分</b>（0 = 不带，绝大多数配方都是 0）。
        ///
        /// 品质的唯一来源是提纯工序，所以只有提纯配方会填它。注入发生在产物落进提纯厂
        /// 自己的物流站槽位时——见 <c>QualityRefineryPatches</c>，不在这里。
        ///
        /// <b>写「每件多少分」而不是「一炉注入多少点」</b>：后者是中间量，
        /// 要跟着产量一起改；前者就是玩家最终看到的那个数，也是效果层直接插值的那个数
        /// （满分 100 对应顶尖 +30%）。
        /// </summary>
        public int quality;

        /// <summary>
        /// 提纯配方的<b>收率</b>，0~1，相对<b>原版换算</b>而不是相对矿数。
        ///
        /// 提纯是万用模板：吃什么矿由每台建筑自己选，产物推导出来
        /// （见 <c>QualityRefineryRegistry</c>），所以「一炉出几件」不能写死在配置里，
        /// 只能写一个比例，由运行时按那种矿在原版里的换算比算出来。
        ///
        /// <b>为什么是相对原版而不是相对矿数。</b> 写成相对矿数会在 2 进 1 出的矿上失真：
        /// 硅石原版就是 2 换 1，「100 矿出 40 块」看着像 40% 收率，实际是原版的 80%——
        /// 于是第二级和第一级一样慷慨，品质阶梯断在这里，而配置上看不出来。
        ///
        /// 只有提纯配方（<c>quality</c> &gt; 0）读这个字段。
        /// </summary>
        public double yield;

        public RecipeItemEntry[] items;
        public RecipeItemEntry[] results;
    }

    /// <summary>
    /// 配方里的一格原料或产物。
    ///
    /// <c>id</c> 是原版物品 ID；<c>ref</c> 是本 mod 物品的引用名，
    /// 取值为 <c>ore</c>（本矿种的矿石）、<c>ingot</c>（本矿种的锭），
    /// 或 ores.json 里 <c>items</c> 段某个条目的 <c>key</c>。
    /// 用 <c>ref</c> 而不是写死 ID，是因为新物品的 ID 撞车时会自动顺延。
    /// </summary>
    [Serializable]
    internal class RecipeItemEntry
    {
        public int id;

        public string @ref;

        public int count;
    }
}
