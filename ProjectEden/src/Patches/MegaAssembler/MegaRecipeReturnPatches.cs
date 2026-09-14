using System.Text;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 巨型建筑换配方时，把机器里剩下的东西<b>全部交还给玩家</b>——背包装得下的进背包，装不下的掉在脚下。
    ///
    /// 换配方前机器里有三处存货，三处的下场原本都不对：
    /// <list type="number">
    /// <item><b>储物格</b>——原先会被直接贴上新配方的标签，100 个电路板当场变成
    /// 100 个齿轮（<see cref="MegaStationPatches"/> 里的 <c>SetSlot</c>，已另修）；</item>
    /// <item><b><c>served[]</c></b>（已经喂进机器、还没消耗的原料）——
    /// <c>AssemblerComponent.SetRecipe</c> 把这两个数组整个重新分配掉，东西就没了。
    /// 这是原版行为，但原版的备料只有几个，巨型建筑是 <c>requireCounts × 200</c>，
    /// 量级完全不同；</item>
    /// <item><b><c>produced[]</c></b>（还没排进储物格的产物）——同上。</item>
    /// </list>
    ///
    /// <b>全部交还给玩家，一件不留在机器里——这是所有者拍板的行为。</b>
    /// 实测一次换配方要处理 9 万个铜矿，而机甲背包满打满算约三万六（每堆 300 × 约 120 格），
    /// 所以「都进背包」物理上做不到。<c>TryAddItemToPackage</c> 的 <c>throwTrash</c>
    /// 正是原版为这种情况准备的：背包 → 配送背包 → 掉在玩家脚下，三级之后机器里一定是空的。
    ///
    /// 掉地上既不丢也不卡，两条都读过 IL：<c>TrashSystem.AddTrash</c> 按堆叠上限**循环**下料
    /// （9 万个铜矿约 300 个掉落物，不是 9 万个），<c>TrashContainer.NewTrash</c> 池子满了
    /// **翻倍扩容**，而且这条路给的 <c>life</c> 和 <c>expire</c> 都是 0，老化整段被跳过，
    /// 所以<b>不会过期消失</b>，什么时候去捡都在。
    ///
    /// <b>为什么挂在界面这一层，而不是 <c>SetRecipe</c> 上。</b> 两个理由，缺一不可：
    /// <c>SetRecipe</c> 拿不到 <c>PlanetFactory</c>（本仓库早就记过「要挂就挂调用方」），
    /// 而且它会被 <c>GameData.Import</c> 之后的重建路径调到——那时候往机甲里塞东西是错的。
    /// 界面这一条是**玩家自己点的**，主线程、一次一台、语义明确。
    /// </summary>
    [HarmonyPatch]
    internal static class MegaRecipeReturnPatches
    {
        /// <summary>
        /// 在 <c>SetRecipe</c> 之前跑。<b>前置的位置是这件事的全部要害</b>：
        /// 一旦 <c>SetRecipe</c> 执行完，<c>served</c> / <c>produced</c> 已经是新数组、
        /// <c>recipeExecuteData</c> 已经是新配方，再想知道「刚才那台机器里装的是什么」
        /// 就没有任何依据了。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIAssemblerWindow), nameof(UIAssemblerWindow.OnRecipePickerReturn))]
        private static void OnRecipePickerReturn_Prefix(UIAssemblerWindow __instance, RecipeProto recipe)
            => ReturnAll(__instance.factory, __instance._assemblerId,
                __instance.player ?? GameMain.mainPlayer,
                recipe != null ? recipe.ID : 0, "面板换配方");

        /// <summary>
        /// 复制 / 粘贴按钮，以及蓝图粘贴到一台已经建好的机器上——<b>这是换配方的第二条路</b>，
        /// 它不经过配方选择面板，所以上面那个前置看不见它。
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildingParameters), nameof(BuildingParameters.PasteToFactoryObject))]
        private static void PasteToFactoryObject_Prefix(int objectId, PlanetFactory factory)
        {
            if (objectId <= 0 || factory?.entityPool == null || objectId >= factory.entityPool.Length) return;

            ReturnAll(factory, factory.entityPool[objectId].assemblerId, GameMain.mainPlayer,
                BuildingParameters.clipboard.recipeId, "粘贴参数");
        }

        /// <summary>
        /// 三处存货一并安置。
        ///
        /// <b>无论走到哪个分支都打一行。</b> 上一版只在真的退了东西时才说话，
        /// 结果玩家反馈「没退回背包」时，日志里既没有成功行也没有失败行——
        /// 「补丁没跑」和「跑了但提前 return 了」长得一模一样，一个往返就这么没了。
        /// </summary>
        private static void ReturnAll(PlanetFactory factory, int id, Player player, int nextRecipeId, string via)
        {
            if (factory?.factorySystem?.assemblerPool == null)
            {
                Skip(via, "没有 factory/assemblerPool");

                return;
            }

            if (player?.package == null)
            {
                Skip(via, "拿不到玩家背包");

                return;
            }

            if (id <= 0 || id >= factory.factorySystem.assemblerPool.Length)
            {
                Skip(via, $"assemblerId={id} 不可用（这台建筑不是制造设备？）");

                return;
            }

            ref AssemblerComponent component = ref factory.factorySystem.assemblerPool[id];

            if (component.id != id)
            {
                Skip(via, $"assemblerPool[{id}].id={component.id} 对不上");

                return;
            }

            // 只管巨型建筑。普通制造台换配方的行为是原版的，玩家有预期，不改。
            if (component.speed < MegaBuildingRegistry.MegaSpeedThreshold)
            {
                Skip(via, $"不是巨型建筑（speed={component.speed} < {MegaBuildingRegistry.MegaSpeedThreshold}）");

                return;
            }

            // 配方没变就什么都不做。点开选配方面板又选中同一条是很常见的操作，
            // 那一下不该把整台机器掏空。
            if (component.recipeId == nextRecipeId)
            {
                Skip(via, $"配方没变（还是 {nextRecipeId}）");

                return;
            }

            StationComponent station = FindStation(factory, component.entityId);

            var toMecha = new StringBuilder();
            var toGround = new StringBuilder();
            var had = 0;

            // 先储物格（玩家真正看得见的那一堆），再 served / produced。
            // 三处都会被清空，顺序只影响谁先占用背包里那点空间。
            had += ReturnStorage(station, player, toMecha, toGround);
            had += ReturnServed(ref component, player, toMecha, toGround);
            had += ReturnProduced(ref component, player, toMecha, toGround);

            if (had == 0)
            {
                Skip(via, "机器里本来就是空的");

                return;
            }

            if (toMecha.Length > 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"巨型建筑换配方（{via}）：已退回伊卡洛斯——{toMecha.ToString().TrimEnd('，')}");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"巨型建筑换配方（{via}）：机甲背包一件也装不下（机器里有 {had} 件），全部掉在脚下。");

            if (toGround.Length > 0)
                ProjectEdenPlugin.Log.LogInfo(
                    $"巨型建筑换配方（{via}）：背包装不下的已掉在脚下，走过去捡即可" +
                    $"（按堆叠上限分堆，不会过期消失）——{toGround.ToString().TrimEnd('，')}");
        }

        private static void Skip(string via, string why)
            => ProjectEdenPlugin.Log.LogInfo($"巨型建筑换配方（{via}）：没退货，原因是{why}");

        private static StationComponent FindStation(PlanetFactory factory, int entityId)
        {
            if (entityId <= 0 || entityId >= factory.entityPool.Length) return null;

            int stationId = factory.entityPool[entityId].stationId;

            if (stationId <= 0 || factory.transport?.stationPool == null ||
                stationId >= factory.transport.stationPool.Length) return null;

            StationComponent station = factory.transport.stationPool[stationId];

            return station?.storage != null && station.id == stationId ? station : null;
        }

        /// <summary>
        /// 已经喂进机器、还没消耗的原料。<c>incServed</c> 要跟着一起走，
        /// 否则喷过的增产点数白白蒸发。返回这一处原本有多少件。
        /// </summary>
        private static int ReturnServed(ref AssemblerComponent component,
            Player player, StringBuilder toMecha, StringBuilder toGround)
        {
            RecipeExecuteData data = component.recipeExecuteData;

            if (data?.requires == null || component.served == null) return 0;

            int n = data.requires.Length < component.served.Length
                ? data.requires.Length
                : component.served.Length;
            var had = 0;

            for (var i = 0; i < n; i++)
            {
                int have = component.served[i];

                if (have <= 0) continue;

                had += have;

                int inc = component.incServed != null && i < component.incServed.Length
                    ? component.incServed[i]
                    : 0;

                int moved = GiveBack(player, data.requires[i], have, inc, 0, toMecha, toGround);

                // **整份扣掉。** 开了 throwTrash 之后这一份是全部离开机器了的（背包 /
                // 配送背包 / 地面三选一），而 TryAddItemToPackage 的返回值只报第一级——
                // 按返回值扣等于把掉地上的那部分又在机器里留一份，凭空复制。
                // 这也是为什么 GiveBack 返回的是 count 而不是它的返回值。
                component.served[i] -= moved;

                if (component.incServed != null && i < component.incServed.Length)
                    component.incServed[i] -= (int)((long)inc * moved / have);
            }

            return had;
        }

        /// <summary>还没排进储物格的产物。产物不带增产点数。</summary>
        private static int ReturnProduced(ref AssemblerComponent component,
            Player player, StringBuilder toMecha, StringBuilder toGround)
        {
            RecipeExecuteData data = component.recipeExecuteData;

            if (data?.products == null || component.produced == null) return 0;

            int n = data.products.Length < component.produced.Length
                ? data.products.Length
                : component.produced.Length;
            var had = 0;

            for (var i = 0; i < n; i++)
            {
                int have = component.produced[i];

                if (have <= 0) continue;

                had += have;
                component.produced[i] -= GiveBack(player, data.products[i], have, 0, 0, toMecha, toGround);
            }

            return had;
        }

        /// <summary>
        /// 储物格里的存货——玩家真正看得见的那一堆，「产了 100 个电路板」说的就是它。
        ///
        /// 这里<b>只往机甲里退</b>：退不完的原地留着就行，<c>SetSlot</c> 的搬迁兜底
        /// 会把它挪到空格并挂本地供应，结果和「改放储物格」是同一个。
        ///
        /// 品质点数按件数分摊着一起退，和本仓库每一处搬运的规矩一致：
        /// 每一次 <c>count</c> 的改动都要配一次 <c>qua</c> 的改动，比例取在扣减之前。
        /// </summary>
        private static int ReturnStorage(StationComponent station, Player player,
            StringBuilder toMecha, StringBuilder toGround)
        {
            if (station == null) return 0;

            var had = 0;

            lock (station.storage)
            {
                for (var i = 0; i < station.storage.Length; i++)
                {
                    int have = station.storage[i].count;

                    if (have <= 0 || station.storage[i].itemId <= 0) continue;

                    had += have;

                    int inc = station.storage[i].inc;
                    int qua = QualityAccess.Ready
                        ? QualityAccess.GetStationQua(ref station.storage[i])
                        : 0;

                    if (GiveBack(player, station.storage[i].itemId, have, inc, qua, toMecha, toGround) <= 0)
                        continue;

                    // **整格清零。** throwTrash 打开之后这一格的货是全部离开了的——
                    // 进背包、进配送背包、或者掉在地上，三条路都不在这台机器里了。
                    // 只按返回值扣，等于把掉地上的那部分又留了一份在格子里：凭空复制。
                    station.storage[i].count = 0;
                    station.storage[i].inc = 0;

                    if (QualityAccess.Ready) QualityAccess.SetStationQua(ref station.storage[i], 0);
                }
            }

            return had;
        }

        /// <summary>
        /// 交还给玩家，返回<b>一共离开这台机器多少件</b>——正常情况下就是全部。
        ///
        /// <b><c>throwTrash: true</c> 改变了返回值的含义，这一点必须写清楚。</b>
        /// 原版 <c>TryAddItemToPackage</c> 的返回值只是<b>进了背包</b>的那部分
        /// （IL 00E8 返回的是 <c>AddItemStacked</c> 的结果），而它内部还有两级去向：
        /// 装不下的先给配送背包，再装不下的走 <c>ThrowTrash</c> 掉在地上。
        /// 也就是说<b>整个 count 都被消费掉了，返回值却只报了第一级</b>。
        /// 按返回值去扣源头，等于把掉地上的那部分又在机器里留了一份——凭空复制。
        /// 所以这里返回 <paramref name="count"/>，调用方整格清零。
        ///
        /// <b>掉地上不会丢，也不会卡。</b> 两条都是读 IL 确认过的：
        /// <c>TrashSystem.AddTrash</c> 是按堆叠上限**循环**下料的
        /// （<c>V_11 = min(count, StackSize)</c> 配 IL 046C 的回跳），
        /// 9 万个铜矿按每堆 300 就是约 300 个掉落物，不是 9 万个；
        /// <c>TrashContainer.NewTrash</c> 池子满了会**翻倍扩容**（IL 0050–005B），
        /// 不会覆盖也不会丢。而且这条路给的 <c>life</c> 是 0、<c>expire</c> 也是 0，
        /// <c>GameTick</c> 的老化整段被 <c>expire &gt; 0</c> 挡在外面，所以<b>不会过期消失</b>。
        ///
        /// <b>调用前必须写侧信道。</b> preloader 把 <c>TryAddItemToPackage</c> 改写成了
        /// 「从 <c>ProjectEdenQualityChannel</c> 读品质」，协议是调用方在调用前写；
        /// 不写的话它消费的是上一个调用者留下的值——**品质会凭空长出来**，
        /// 而且症状出现的地方和病因毫无关系。读完清掉，免得漏给下一个人。
        /// </summary>
        ///
        /// <b>调用前必须写侧信道。</b> preloader 把 <c>TryAddItemToPackage</c> 改写成了
        /// 「从 <c>ProjectEdenQualityChannel</c> 读品质」，协议是调用方在调用前写；
        /// 不写的话它消费的是上一个调用者留下的值——**品质会凭空长出来**，
        /// 而且症状出现的地方和病因毫无关系。这正是把每件 100 分涨到 7600 分的那个洞。
        /// 读完清掉，免得漏给下一个人。
        ///
        /// <c>throwTrash: false</c>：装不下就不塞，绝不往地上扔。
        /// </summary>
        private static int GiveBack(Player player, int itemId, int count, int inc, int qua,
            StringBuilder toMecha, StringBuilder toGround)
        {
            if (itemId <= 0 || count <= 0) return 0;

            if (QualityAccess.SetChannel0 != null) QualityAccess.SetChannel0(qua);

            // throwTrash: true —— 背包和配送背包都装不下的，掉在玩家脚下，
            // 而不是留在这台机器的储物格里。这是所有者定的行为。
            int added = player.TryAddItemToPackage(itemId, count, inc, true, 0, false);

            if (QualityAccess.ChannelClearable) QualityAccess.ClearChannel();

            if (added > 0) Append(toMecha, itemId, added);
            if (count - added > 0) Append(toGround, itemId, count - added);

            return count;
        }

        private static void Append(StringBuilder log, int itemId, int count)
        {
            ItemProto proto = LDB.items.Select(itemId);

            log.Append(proto != null ? proto.name : itemId.ToString()).Append(' ').Append(count).Append('，');
        }
    }
}
