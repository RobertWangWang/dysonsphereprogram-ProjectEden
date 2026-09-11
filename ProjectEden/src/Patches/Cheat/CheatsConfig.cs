#pragma warning disable 649 // 字段由 JSON 反序列化赋值

using System;

namespace ProjectEden.Patches
{
    /// <summary>
    /// data/cheats.json 的映射类型：一组作弊开关，<b>默认全部关闭</b>。
    ///
    /// 这一组和本 mod 其余部分的性质不同：其它模块是「把后期产线拉满」的内容改动，
    /// 这里几条是<b>直接绕过游戏规则</b>。所以它们单独一个配置文件、单独一个总开关，
    /// 并且每一条开启时都会在日志里留一行，免得以后排查别的问题时把作弊当成 bug。
    ///
    /// 思路参考 soarqin/DSP_Mods 的 CheatEnabler（MIT）。实现没有照抄：
    /// CheatEnabler 整体替换了若干方法体，本仓库的规矩是「能用前置/后置就别转译」，
    /// 所以这里全部落在 BuildPreview.condition 的后置擦除和原版自己的批量建造入口上。
    /// </summary>
    [Serializable]
    internal class CheatsConfig
    {
        /// <summary>总开关。关掉则下面每一条都不生效，补丁也不做任何事</summary>
        public bool enabled;

        /// <summary>
        /// 建造秒完成：预建物一放下就直接建好，不等建设机器人飞过去。
        ///
        /// <b>材料照扣</b>，从机甲背包里拿——这条只省时间，不省料。
        /// 拿不出料的预建物会原样留着，交回给原版的机器人去建。
        /// </summary>
        public bool instantBuild;

        /// <summary>
        /// 每次结算最多秒建几个。留 0 用默认值 100。
        ///
        /// 原版沙盒模式的同一条路（<c>ConstructionSystem.FastBuild</c>）也有这个上限，
        /// 取自 <c>GamePrefsData.fastBuildBatchSize</c>——一整张大蓝图一口气建完会卡一帧，
        /// 分几帧建完既看不出来也不卡。
        /// </summary>
        public int instantBuildPerTick;

        /// <summary>
        /// 无条件建造：建造预览的<b>所有</b>拒绝条件一律放行，覆盖/重建标记也一并清掉。
        ///
        /// <b>采矿机除外</b>（<c>veinMiner</c> / <c>oilMiner</c>）：它们的建造判定同时在
        /// 确认「范围内有没有矿」，强行放行会造出一台没有矿脉数组的采矿机。
        /// 采矿机想重叠放置请用 advancedminer.json 里的 <c>allowMinerOverlap</c>。
        ///
        /// 注意这<b>不等于免费建造</b>：原版放下的是预建物，材料是建设机器人事后取的，
        /// 所以放行 NotEnoughItem 只是让你先摆着，摆完照样要有料才建得起来。
        /// </summary>
        public bool noConditionBuild;

        /// <summary>
        /// 无碰撞：建筑可以互相重叠。做两件事，<b>缺一不可</b>。
        ///
        /// 一是放行建造判定里的 <c>Collide(34)</c> 结论，连同和它同一段循环写出来的
        /// 「覆盖/重建」标记（<c>coverObjId</c>）一起清——后者是 condition 之外的
        /// 第二道、而且是静默的闸门，只清前者的表现是「不报错了，但点下去还是没反应」。
        ///
        /// 二是把「Planet Collider Pool」这个对象关掉，它是原版给行星上所有建筑发放
        /// Unity 碰撞体的对象池（见 <c>NoCollisionPatches</c>）。
        ///
        /// <b>第一版只做了第二件，不够。</b> 建造判定走的是
        /// <c>Physics.OverlapBoxNonAlloc</c> → <c>PlanetPhysics.GetColliderData</c>，
        /// 中途还会撞上别的建造预览自己的模型，池子关了照样判得出碰撞。
        ///
        /// 副作用：机甲也会穿过建筑，那是同一批碰撞体，分不开。
        /// 建筑的点选/拆除不受影响——那条走 <c>PlanetPhysics.raycastLogic</c>，
        /// 已核对过它<b>完全不用 UnityEngine.Physics</b>，是游戏自己实现的一套。
        /// </summary>
        public bool noCollision;

        /// <summary>
        /// 额外再把行星碰撞体对象池整个关掉。<b>默认关，而且不建议开。</b>
        /// 重叠建造<b>不需要</b>它——那是靠放行建造判定做到的。
        /// 关掉池子会连带废掉所有 <c>Physics.Raycast</c>，代价见 cheats.json 的说明。
        /// </summary>
        public bool noCollisionPhysics;

        /// <summary>
        /// 发电建筑无间距限制：清掉 PowerTooClose / WindTooClose / GeothermalTooClose 三条。
        ///
        /// 原版的间距规则是三档，从 IL 里读出来的（距离都是平方值）：
        ///   · 12.25  → 3.5 米，<b>任意两台发电建筑</b>之间（太阳能板、火力发电厂都吃这条）
        ///   · 110.25 → 10.5 米，风力涡轮机
        ///   · 144    → 12 米，地热发电站
        /// 三条是同一段循环里算出来的，分不开，所以这一个开关三条一起放行。
        ///
        /// <b>不要去改那几个常量</b>：110.25 在蓝图粘贴的判定里还被<b>炮塔间距</b>用着，
        /// 一律替换会把炮塔的规则一起废掉（CLAUDE.md 第 2 号坑：同一个常量两种含义）。
        /// 按 <c>BuildPreview.condition</c> 擦除就没有这个问题。
        /// </summary>
        public bool powerNoSpacing;

        /// <summary>
        /// 平地抽水：放行 NeedWater，抽水站可以建在陆地上。
        ///
        /// <b>星球得有水。</b> 抽水站的产物取自 <c>PlanetData.waterItemId</c>，
        /// 无水星球上照样抽不出东西——这条只解除「必须建在水面上」的位置限制。
        ///
        /// 地热发电站的 NeedGeothermalResource 是另一条，不在这个开关里。
        /// </summary>
        public bool waterPumpAnywhere;
    }
}
