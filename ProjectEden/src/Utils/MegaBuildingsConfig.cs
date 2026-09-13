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
        /// 必须选一个带 slotPoses 的模型，否则传送带无法直连——原版组装机没有槽位。
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
}
