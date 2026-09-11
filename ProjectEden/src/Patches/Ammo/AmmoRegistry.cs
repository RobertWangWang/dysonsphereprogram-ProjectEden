using System;
using System.Collections.Generic;
using ProjectEden.Utils;
using xiaoye97;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 合金弹药：拿两种合金做子弹，<b>组合决定伤害档位和产量，而配方不公开</b>。
    ///
    /// <b>为什么伤害必须分档。</b> 伤害是 <c>ItemProto.Ability</c>，按 proto 存——
    /// <c>TurretComponent.SetNewItem</c> 里 <c>bulletDamage = proto.Ability</c>，
    /// <c>Mecha.get_ammoDamage</c> 也直接读它再乘各种加成。一个伤害值就得有一个物品，
    /// 和四维属性撞的是同一堵墙（DSP 里没有任何按堆/按件的元数据槽位）。
    /// 所以档位要少——5 档，物品格位才吃得消；<b>产量</b>则走 <c>productCounts[0]</c>，
    /// 连续，一个物品都不占。
    ///
    /// <b>进不了炮塔的那个坑在 Plugin 里补。</b> 弹药能不能被炮塔捡起来取决于
    /// <c>ItemProto.turretNeeds</c>，那是预加载线程建的静态表（<c>InitTurretNeeds</c>
    /// 在 <c>VFPreload.PreloadThread</c> 的 IL 0x08AB，和流体白名单 0x08B0 紧挨着），
    /// <b>LDBTool 没有替我们重跑</b>。见 <c>ProjectEdenPlugin.RefreshTurretNeeds</c>。
    ///
    /// <b>已知限制：炮塔窗口的「一键装填」只有 3 个控件</b>
    /// （<c>handFillAmmoIcon0/1/2</c>），原版三种子弹已经占满，所以这 5 档不会出现在那里。
    /// 传送带喂弹（<c>TurretComponent.BeltUpdate</c>）和 Shift 点击塞进炮塔
    /// （<c>PlanetFactory.EntityFastFillIn</c>）都正常——那两条路是把整张
    /// <c>turretNeeds[类型]</c> 当过滤数组、按 <c>Length</c> 遍历的。
    /// </summary>
    internal static class AmmoRegistry
    {
        /// <summary>一档弹药的运行时状态。</summary>
        internal class Tier
        {
            internal AmmoTierEntry Entry;
            internal int ItemId;
            internal int Grid;

            /// <summary>乘出来的真值，日志和面板都用它</summary>
            internal int Damage;
            internal int Rounds;
        }

        internal static AmmoConfig Config { get; private set; }

        internal static readonly List<Tier> Tiers = new List<Tier>();

        /// <summary>可以拿来做弹药的合金物品 ID，顺序即面板上的循环顺序</summary>
        internal static readonly List<int> Candidates = new List<int>();

        internal static int RecipeId { get; private set; }

        internal static bool Ready => Tiers.Count > 0 && Candidates.Count >= 2 && RecipeId > 0;

        internal static void Load()
        {
            Config = JsonHelper.Load<AmmoConfig>("ammo");

            if (Config == null) ProjectEdenPlugin.Log.LogWarning("读不到 ammo.json，合金弹药未启用");
            else if (!Config.enabled) ProjectEdenPlugin.Log.LogInfo("合金弹药已在配置里关闭");
        }

        // ── 注册 ──────────────────────────────────────────────

        internal static void OnPreAddData()
        {
            Tiers.Clear();

            if (Config == null || !Config.enabled || Config.tiers == null || Config.tiers.Length == 0) return;

            ItemProto anchor = ResolveAnchor();

            if (anchor == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"找不到可当锚点的原版弹药（AmmoType {(EAmmoType)Config.ammoType}），合金弹药未注册");

                return;
            }

            ProjectEdenPlugin.Log.LogInfo(
                $"合金弹药的数值锚点：「{anchor.name}」({anchor.ID})，伤害 {anchor.Ability}、每箱 {anchor.HpMax} 发");

            foreach (AmmoTierEntry entry in Config.tiers)
            {
                if (entry == null) continue;

                var tier = new Tier { Entry = entry };

                tier.ItemId = ProtoSlots.ResolveItemId(entry.itemId, entry.name);
                tier.Grid = ProtoSlots.ResolveGridIndex(0, entry.name, ProtoSlots.GridKind.Item,
                    g => Pending(g, ProtoSlots.GridKind.Item));

                tier.Damage = Mul(anchor.Ability, entry.damage);
                tier.Rounds = Mul(anchor.HpMax, entry.rounds);

                AddItem(tier, anchor);

                ProtoSlots.ReserveItemId(tier.ItemId);
                ProtoSlots.ReserveGrid(tier.Grid, ProtoSlots.GridKind.Item);

                Tiers.Add(tier);
            }

            AddRecipe(anchor);
        }

        /// <summary>
        /// 锚点：配了就用配的，没配就取「同类型、ID 最小」的那个原版弹药。
        ///
        /// <b>倍率而不是绝对值</b>，和蓄电器同理：原版弹药的 Ability / HpMax 存在
        /// resources.assets 的 proto 里，离线看不到、反编译也查不到，写死就是凭记忆猜。
        /// </summary>
        private static ItemProto ResolveAnchor()
        {
            if (Config.anchorItemId > 0) return LDB.items.Select(Config.anchorItemId);

            ItemProto best = null;

            foreach (ItemProto item in LDB.items.dataArray)
            {
                if (item == null || (int)item.AmmoType != Config.ammoType) continue;
                if (item.Ability <= 0) continue;
                if (best == null || item.ID < best.ID) best = item;
            }

            return best;
        }

        private static int Mul(int baseValue, float factor)
        {
            var v = (int)Math.Round(baseValue * (factor > 0f ? factor : 1f));

            return v < 1 ? 1 : v;
        }

        private static bool Pending(int grid, ProtoSlots.GridKind kind)
        {
            for (var i = 0; i < Tiers.Count; i++)
                if (kind == ProtoSlots.GridKind.Item && Tiers[i].Grid == grid)
                    return true;

            return false;
        }

        private static void AddItem(Tier tier, ItemProto anchor)
        {
            var item = new ItemProto
            {
                ID = tier.ItemId,
                Name = tier.Entry.name,
                Description = tier.Entry.description,
                Type = anchor.Type,
                GridIndex = tier.Grid,
                StackSize = anchor.StackSize,
                // 先指向锚点弹药的原图，PostAddData 里再换成按档位改色的版本。
                // **不能指向一个不存在的自制图标**——Resources.Load 返回 null 时
                // Preload 读不出图，格子就是个白块，而且不报错
                IconPath = anchor.IconPath,
                IsFluid = false,
                CanBuild = false,
                BuildIndex = 0,
                // 这三项就是「它是一发什么样的子弹」的全部：
                //   AmmoType 决定哪种炮塔吃它，Ability 是伤害，HpMax 是一箱多少发
                AmmoType = (EAmmoType)Config.ammoType,
                Ability = tier.Damage,
                HpMax = tier.Rounds,
                Grade = 0,
                Upgrades = new int[0],
                DescFields = anchor.DescFields,
                // 坑 5：新开局会清空 recipeUnlocked，-1 让 ItemUnlocked 直接返回 true
                UnlockKey = -1,
                PreTechOverride = 0,
                prefabDesc = PrefabDesc.none,
            };

            item.name = tier.Entry.name;

            LDBTool.PreAddProto(item);
        }

        /// <summary>
        /// <b>只注册一条配方。</b> 28 种组合不各自成为配方——那样合成面板里就把答案全列出来了，
        /// 而且 28 个配方格也放不下。两个原料槽指向哪种合金是<b>逐建筑</b>改的
        /// （<c>requires[0]</c> / <c>requires[1]</c>，改值不改长度，存档安全）。
        /// </summary>
        private static void AddRecipe(ItemProto anchor)
        {
            if (Tiers.Count == 0) return;

            RecipeId = ProtoSlots.ResolveRecipeId(Config.recipeId, "合金弹药");

            int grid = ProtoSlots.ResolveGridIndex(0, "合金弹药（配方）", ProtoSlots.GridKind.Recipe);

            // 原料槽先占位：真正吃哪两种合金由面板逐建筑决定，
            // 这里放两个一定存在的物品，保证配方本身是合法的
            int fallback = anchor.ID;

            var recipe = new RecipeProto
            {
                ID = RecipeId,
                Name = "合金弹药",
                Description = "把两种合金压制成弹芯。用哪两种、配出什么档位，得自己试。",
                Type = (ERecipeType)Config.recipeType,
                Handcraft = false,
                Explicit = true,
                TimeSpend = Config.timeSpend > 0 ? Config.timeSpend : 120,
                Items = new[] { fallback, fallback },
                ItemCounts = new[] { Config.partsA > 0 ? Config.partsA : 2, Config.partsB > 0 ? Config.partsB : 2 },
                Results = new[] { Tiers[0].ItemId },
                ResultCounts = new[] { Config.baseYield > 0 ? Config.baseYield : 12 },
                GridIndex = grid,
                IconPath = anchor.IconPath,
                preTech = null,
            };

            recipe.name = recipe.Name;

            LDBTool.PreAddProto(recipe);

            MegaBuildingRegistry.RecipeIds.Add(RecipeId);

            ProtoSlots.ReserveRecipeId(RecipeId);
            ProtoSlots.ReserveGrid(grid, ProtoSlots.GridKind.Recipe);
        }

        // ── LDB 建好之后：解析可用合金，把对照表打进日志 ──────

        internal static void OnPostAddData()
        {
            Candidates.Clear();

            if (Config == null || !Config.enabled || Tiers.Count == 0) return;

            var names = new List<string>();

            if (Config.candidates != null && Config.candidates.Length > 0)
            {
                foreach (string r in Config.candidates)
                {
                    int id = OreRegistry.FindItemIdByRef(r);

                    if (id > 0) Candidates.Add(id);
                    else ProjectEdenPlugin.Log.LogError($"合金弹药：候选合金「{r}」解析不出物品");
                }
            }
            else
            {
                // 留空就取 alloys.json 里全部——那份表本来就是「可调配比的合金」
                AlloysConfig alloys = ProjectEdenPlugin.AlloysConfig;

                if (alloys?.alloys != null)
                    foreach (AlloyEntry e in alloys.alloys)
                    {
                        RecipeProto proto = LDB.recipes.Select(e.recipeId);

                        if (proto?.Results != null && proto.Results.Length > 0)
                            Candidates.Add(proto.Results[0]);
                    }
            }

            for (var i = 0; i < Candidates.Count; i++)
                names.Add(LDB.items.Select(Candidates[i])?.name ?? Candidates[i].ToString());

            if (Candidates.Count < 2)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"合金弹药：可用合金只有 {Candidates.Count} 种，配不出组合，功能停用");

                return;
            }

            // 把配方的默认两槽换成真正的合金，别让它一直指着锚点弹药
            RecipeProto recipe = LDB.recipes.Select(RecipeId);

            if (recipe?.Items != null && recipe.Items.Length >= 2)
            {
                recipe.Items[0] = Candidates[0];
                recipe.Items[1] = Candidates[1];
            }

            var tierText = new List<string>();

            foreach (Tier t in Tiers)
                tierText.Add($"{t.Entry.name} 伤害 {t.Damage}/每箱 {t.Rounds} 发（分≥{t.Entry.minScore:0.#}）");

            ProjectEdenPlugin.Log.LogInfo(
                $"合金弹药已注册：{Tiers.Count} 档，可用合金 {Candidates.Count} 种（{string.Join("、", names.ToArray())}），" +
                $"共 {Candidates.Count * (Candidates.Count + 1) / 2} 种组合");

            ProjectEdenPlugin.Log.LogInfo($"  档位：{string.Join(" / ", tierText.ToArray())}");

            TintIcons();
        }

        /// <summary>
        /// 五档共用原版弹药的图标，按档位改色区分——和新建筑那套一样，不需要美术资源。
        /// 色相从冷到暖走一圈（I 青 → V 红），越靠后越「烫」，和伤害递增对得上。
        /// </summary>
        private static void TintIcons()
        {
            ItemProto anchor = ResolveAnchor();

            if (anchor?._iconSprite == null)
            {
                ProjectEdenPlugin.Log.LogWarning("合金弹药：锚点弹药没有图标，五档只能沿用原图");

                return;
            }

            // 档位越高越暖：青 → 绿 → 黄 → 橙 → 红
            float[] hues = { 190f, 140f, 55f, 25f, 0f };

            for (var i = 0; i < Tiers.Count; i++)
            {
                float hue = hues[i < hues.Length ? i : hues.Length - 1];

                UnityEngine.Sprite icon = IconTinter.Tint(anchor._iconSprite, hue, 1.35f, 0.2f, 1f + i * 0.03f);

                if (icon == null) continue;

                ItemProto item = LDB.items.Select(Tiers[i].ItemId);
                RecipeProto recipe = i == 0 && RecipeId > 0 ? LDB.recipes.Select(RecipeId) : null;

                if (item != null) item._iconSprite = icon;
                if (recipe != null) recipe._iconSprite = icon;
            }

            ProjectEdenPlugin.Log.LogInfo($"合金弹药的图标已由「{anchor.name}」按档位改色生成（色相 190°→0°）");
        }
    }
}
