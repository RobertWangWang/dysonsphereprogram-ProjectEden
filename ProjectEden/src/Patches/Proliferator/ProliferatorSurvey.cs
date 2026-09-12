using System;
using System.Collections.Generic;
using System.Text;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把原版增产剂的参数和喷涂机认可的物品清单打进启动日志。
    ///
    /// <b>为什么必须是运行时 dump。</b> <c>ItemProto.Ability</c> / <c>HpMax</c> 和
    /// <c>PrefabDesc.incItemId</c> 都在 <c>resources.assets</c> 里，反编译读不到——
    /// 和星球主题表、燃料类型位是同一种情况，做法照抄 <c>OreRegistry.DumpThemes</c>。
    ///
    /// <b>它回答的是「新增产剂的数值拿什么当锚」。</b> 读 IL 已经确认：
    /// <c>SpraycoaterComponent.InternalUpdate</c> 里
    /// <c>incAbility = ItemProto.Ability</c>、<c>incSprayTimes = ItemProto.HpMax</c>，
    /// 而且<b>全方法 518 条指令里没有一处把等级钳在 4</b>
    /// （<c>Cargo.kSprayIncMax = 4</c> 是个 <c>const</c>，没有任何实现——
    /// 和 <c>kMaxCargoFlowSpeedPerSecond = 120</c> 一样是有名无实的常量，仓库为那个吃过一次亏）。
    ///
    /// <b>认定「这是增产剂」靠的是喷涂机 prefab 上的 <c>incItemId[]</c> 数组</b>
    /// （IL 0111 起的循环拿 <c>Cargo.item</c> 逐个比）。所以加一种新增产剂＝往那个数组里
    /// 追加一个 ID，和本仓库已经做过的 <c>ItemProto.fluids</c> / <c>turretNeeds</c> /
    /// <c>UIEntityBriefInfo.icons</c> 是同一个动作；而且它每 tick 现读 prefab，
    /// <b>不进存档，已建成的喷涂机立刻生效</b>。
    ///
    /// <b>真正的天花板不是表长 11，是 Cargo.inc。</b>
    /// 喷涂那一步是 <c>cargo.inc = stack × incAbility</c>（IL 03C1–03CB），
    /// 而本 mod 的 preloader 把 <c>Cargo.inc</c> 加宽成了 Int16，于是
    /// <c>stack × Ability ≤ 32767</c>。<b>集装层数和增产剂等级是同一个预算里的两笔开销</b>，
    /// 这个上限由本类按当前配置算出来打进日志，不写死。
    /// </summary>
    internal static class ProliferatorSurvey
    {
        /// <summary>增产剂各表的长度都是 11，即等级 0~10。</summary>
        private const int LevelMax = 10;

        private static bool _done;

        internal static void OnPostAddData()
        {
            if (_done) return;

            _done = true;

            ItemProto[] items = LDB.items?.dataArray;

            if (items == null)
            {
                ProjectEdenPlugin.Log.LogWarning("增产剂普查：LDB.items 还没建好，跳过");
                return;
            }

            DumpSprayers(items);
            DumpTables();
            DumpCeiling();
        }

        /// <summary>
        /// 找出所有 <c>incItemId</c> 非空的建筑（就是喷涂机那一类），报它认可哪些物品。
        ///
        /// <b>用发现而不是写死 ID。</b> 喷涂机的物品号同样读不到，而且哪天原版再加一台
        /// 同类建筑，写死就漏了。
        /// </summary>
        private static void DumpSprayers(ItemProto[] items)
        {
            var found = 0;

            foreach (ItemProto proto in items)
            {
                int[] list = proto?.prefabDesc?.incItemId;

                if (list == null || list.Length == 0) continue;

                found++;

                ProjectEdenPlugin.Log.LogInfo(
                    $"── 增产剂：{proto.Name}({proto.ID}) 认可 {list.Length} 种 "
                    + "（加新增产剂＝往这个数组追加 ID）──");

                foreach (int id in list)
                {
                    ItemProto inc = LDB.items.Select(id);

                    if (inc == null)
                    {
                        ProjectEdenPlugin.Log.LogWarning($"  {id}：这个 ID 在 LDB 里没有对应物品");
                        continue;
                    }

                    // Ability 是喷出来的等级，HpMax 是一份能喷多少件 —— 两个都是
                    // SpraycoaterComponent.InternalUpdate 直接读的，没有中间层
                    ProjectEdenPlugin.Log.LogInfo(
                        $"  {inc.Name}({inc.ID})  等级 Ability={inc.Ability}"
                        + $"  喷数 HpMax={inc.HpMax}"
                        + $"  {Describe(inc.Ability)}");
                }
            }

            if (found == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    "增产剂普查：没找到任何带 incItemId 的建筑 —— 这不正常，原版至少有喷涂机");
        }

        /// <summary>一句话说清某个等级值多少，免得看日志的人还要去翻表。</summary>
        private static string Describe(int level)
        {
            if (level <= 0 || level > LevelMax) return "（等级超出表范围 0~10）";

            return $"增产 +{Cargo.incTableMilli[level] * 100:0.#}%"
                   + $" / 加速 +{Cargo.accTableMilli[level] * 100:0.#}%"
                   + $" / 耗电 ×{Cargo.powerTableRatio[level]:0.##}";
        }

        /// <summary>
        /// 把整张表打出来。<b>原版只用到 4 级，5~10 级是填好了没人用的。</b>
        /// 打出来是为了确认这一版游戏的表和离线读到的一致——换了游戏版本这几行会自己说话。
        /// </summary>
        private static void DumpTables()
        {
            var sb = new StringBuilder("── 增产剂等级表（原版只用到 4 级，往上是留好的）──");

            for (var level = 0; level <= LevelMax; level++)
            {
                if (level >= Cargo.incTableMilli.Length) break;

                sb.Append($"\n  {level,2} 级：增产 +{Cargo.incTableMilli[level] * 100,5:0.#}%")
                  .Append($"  加速 +{Cargo.accTableMilli[level] * 100,6:0.#}%")
                  .Append($"  耗电 ×{Cargo.powerTableRatio[level],4:0.##}")
                  .Append($"  箭头 {Cargo.fastIncArrowTable[level]}");
            }

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>
        /// 算出当前配置下等级的实际上限，并说明它为什么是这个数。
        ///
        /// <c>cargo.inc = stack × Ability</c>，而 <c>Cargo.inc</c> 的位宽决定了乘积的上限。
        /// </summary>
        private static void DumpCeiling()
        {
            int piler = ProjectEdenPlugin.StationsConfig?.stationPilerLevel ?? 0;

            if (piler <= 0)
            {
                ProjectEdenPlugin.Log.LogInfo("增产剂等级上限：读不到集装层数配置，跳过推算");
                return;
            }

            // 位宽读的是<b>结果</b>——preloader 到底有没有生效，按 Cargo.inc 的真实类型判断，
            // 不按「我们以为装了没装」。这是仓库里 CargoWidening.IsActive 的同一条规矩。
            bool wide = CargoWidening.IsActive;
            int budget = wide ? short.MaxValue : byte.MaxValue;
            int cap = budget / piler;

            if (cap > LevelMax) cap = LevelMax;

            string line =
                $"增产剂等级上限：{cap} 级"
                + $"（cargo.inc = stack × Ability，inc 是 {(wide ? "Int16" : "Byte")}，"
                + $"预算 {budget} ÷ 集装 {piler} 层 = {budget / piler}，表最高 {LevelMax}）";

            if (cap <= 0)
                ProjectEdenPlugin.Log.LogWarning(
                    line + " —— 集装层数已经吃满整个预算，连 1 级增产剂都挂不住");
            else if (cap < LevelMax)
                ProjectEdenPlugin.Log.LogInfo(
                    line + $" —— 想用到 {LevelMax} 级得把集装降到 {budget / LevelMax} 层以下；"
                         + "这两者是同一个预算里的两笔开销");
            else
                ProjectEdenPlugin.Log.LogInfo(line);
        }
    }
}
