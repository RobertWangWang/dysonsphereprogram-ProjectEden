#pragma warning disable 649 // 字段由 JSON 反序列化赋值

using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ProjectEden.Utils
{
    /// <summary>
    /// 英文本地化：把本 mod 用到的中文串注册进原版的字符串表，切到英文时显示英文。
    ///
    /// <b>原版是怎么做的（从 IL 读出来的，三件事）。</b>
    /// <list type="number">
    /// <item><c>Localization</c> 持有 <c>namesIndexer</c>（键 → 下标）、
    /// <c>strings[语言][下标]</c> 和当前语言的 <c>currentStrings</c>。</item>
    /// <item><c>Localization.Translate(key)</c> <b>查不到键时原样返回 key</b>
    /// （IL 0024: <c>ldarg.0 ; ret</c>）。</item>
    /// <item><c>ItemProto.name</c> / <c>description</c> 不是字段，是
    /// <c>Name.Translate()</c> / <c>Description.Translate()</c> 的结果，
    /// 由 <c>Preload</c> 和 <c>RefreshTranslation</c> 写进去。</item>
    /// </list>
    ///
    /// 合起来就解释了现状：本 mod 的 <c>Name</c> 写的是中文，它不是任何键，于是
    /// <c>Translate</c> 原样吐回来——中文显示正常，英文也显示中文。
    ///
    /// <b>所以做法是「把中文串本身注册成键」，而不是把 Name 换成别的键。</b>
    /// 换 Name 也能work，但代价大：LDBTool 的 CustomID.cfg / CustomGridIndex.cfg
    /// <b>按 proto 的名字记 ID</b>，改名等于让那些绑定全部失配；而且本仓库里好几处
    /// 自检是拿名字比的。注册中文串做键则一个字段都不用动。
    ///
    /// <b>唯一的风险是撞上原版已有的键</b>——原版的键也是中文串（<c>Translate("风能")</c>
    /// 这种写法到处都是）。撞上了就会把原版那条翻译<b>全局</b>改掉。
    /// 所以注册前先查 <c>namesIndexer</c>，已存在的一律跳过并打 WARNING；
    /// 「铁块」「钢材」这类原版名字也因此不必写进表里。
    ///
    /// 实现思路参考 soarqin/DSP_Mods 的 UXAssist.Common.I18N（MIT）。
    /// 没有用 CommonAPI 的 <c>ProtoRegistry.RegisterString</c>：它的
    /// <c>AddModTranslations</c> 是无条件 <c>strings[lang][indexer[key]] = value</c>，
    /// 对已存在的键照写不误，正好缺了上面那道防撞检查。
    /// </summary>
    [HarmonyPatch]
    internal static class I18N
    {
        /// <summary>简体中文的 LCID。其余语言一律落到英文，所以只需要认这一个</summary>
        private const int LcidZhcn = 2052;

        /// <summary>en-US。<see cref="EnglishOf"/> 用它认出英文那一门语言。</summary>
        private const int LcidEnus = 1033;

        /// <summary>中文 → 英文。来自 data/i18n.json</summary>
        private static Dictionary<string, string> _table;

        /// <summary>已经注册成功的键 → 在 namesIndexer 里的下标</summary>
        private static readonly Dictionary<string, int> Registered = new Dictionary<string, int>();

        /// <summary>撞上原版键、被跳过的那些</summary>
        private static readonly List<string> Skipped = new List<string>();

        private static bool _applied;

        internal static void Load()
        {
            I18NConfig config = JsonHelper.Load<I18NConfig>("i18n");

            _table = config?.strings;

            if (_table == null)
            {
                ProjectEdenPlugin.Log.LogWarning("读不到 i18n.json，英文本地化未启用");

                return;
            }

            // "//" 开头的键是文件里的分节标记，不是要翻译的串
            var keys = new List<string>(_table.Keys);

            foreach (string key in keys)
                if (key.StartsWith("//"))
                    _table.Remove(key);

            ProjectEdenPlugin.Log.LogInfo($"英文本地化表已载入 {_table.Count} 条");
        }

        /// <summary>
        /// 查表。<b>只给本 mod 自己的界面文字用</b>——proto 的名字和描述走原版的
        /// <c>Translate</c>，不经过这里。
        /// </summary>
        internal static string Tr(string zh) => zh == null ? null : zh.Translate();

        /// <summary>英文那一门语言的下标；还没找到就是 −1。</summary>
        private static int _englishIndex = -2;

        /// <summary>
        /// 某个 key 的<b>英文</b>写法，不管当前语言是什么。查不到返回 null。
        ///
        /// <para>给「中文客户端也能用英文搜」用的。两级：</para>
        /// <list type="number">
        /// <item>本 mod 自己那张表——它一直在内存里，和当前语言无关</item>
        /// <item>原版的英文字符串表——铁矿、硅石这些 vanilla 物品不在我们表里，
        /// 只能问原版。<b>英文那门语言是懒加载的</b>，所以这里会在第一次查的时候
        /// 调一次 <c>Localization.LoadLanguage</c> 把它读进来。</item>
        /// </list>
        ///
        /// <para><b>那一调不会改玩家的语言</b>，是查过 IL 的：<c>LoadLanguage(int)</c>
        /// 全身 372 条指令里<b>没有一处 <c>stsfld currentLanguageIndex</c></b>，
        /// 它只读 <c>Languages</c> 和 <c>lcId</c>。否则「搜个矿把界面语言切了」
        /// 会是个极难联想到成因的 bug。</para>
        /// </summary>
        internal static string EnglishOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            // 本 mod 自己的条目：随时可查，且不依赖原版加载了哪门语言
            if (_table != null && _table.TryGetValue(key, out string mine)) return mine;

            int index = EnglishIndex();

            if (index < 0) return null;

            if (Localization.namesIndexer == null
                || !Localization.namesIndexer.TryGetValue(key, out int slot)) return null;

            string[][] strings = Localization.strings;

            if (strings == null || index >= strings.Length) return null;

            string[] table = strings[index];

            if (table == null || slot < 0 || slot >= table.Length) return null;

            return table[slot];
        }

        private static int EnglishIndex()
        {
            if (_englishIndex != -2) return _englishIndex;

            _englishIndex = -1;

            Localization.Language[] languages = Localization.Languages;

            if (languages == null) return -1;

            for (var i = 0; i < languages.Length; i++)
            {
                if (languages[i].lcId != LcidEnus) continue;

                _englishIndex = i;

                // 懒加载：当前是中文时英文表多半还是 null，这里补一次。
                // 只做一次，而且 LoadLanguage 不改 currentLanguageIndex（见上）
                string[][] strings = Localization.strings;

                if (strings != null && i < strings.Length && strings[i] == null)
                {
                    Localization.LoadLanguage(i);

                    ProjectEdenPlugin.Log.LogInfo(
                        "英文名检索：当前不是英文语言，已把原版英文字符串表读进内存"
                        + "（只读一次，不会切换界面语言——LoadLanguage 全程没有写 currentLanguageIndex）");
                }

                break;
            }

            return _englishIndex;
        }

        // ── 注册 ──────────────────────────────────────────────

        /// <summary>
        /// <c>LoadSettings</c> 之后 <c>namesIndexer</c> 才有内容，所以防撞检查只能放在这里。
        /// <c>Priority.Last</c> 让 CommonAPI 那边先注册完，我们看到的才是最终状态。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(Localization), nameof(Localization.LoadSettings))]
        private static void Localization_LoadSettings() => Apply();

        /// <summary>
        /// 语言是按需加载的：切到一门还没读过的语言时 <c>strings[index]</c> 才被填上，
        /// 会把我们写进去的内容冲掉，所以每次加载完都要补一遍。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(Localization), nameof(Localization.LoadLanguage))]
        private static void Localization_LoadLanguage(int index)
        {
            if (!_applied) return;

            ApplyLanguage(index);
        }

        private static void Apply()
        {
            if (_applied || _table == null || _table.Count == 0) return;

            Dictionary<string, int> indexer = Localization.namesIndexer;

            if (indexer == null)
            {
                ProjectEdenPlugin.Log.LogWarning("Localization.namesIndexer 还是空的，英文本地化跳过本次");

                return;
            }

            _applied = true;

            foreach (KeyValuePair<string, string> pair in _table)
            {
                // 原版已经有这个键：跳过。写进去会把原版那条翻译全局改掉，
                // 而且不会报错，只会让某处界面文字莫名其妙变了。
                if (indexer.ContainsKey(pair.Key))
                {
                    Skipped.Add(pair.Key);

                    continue;
                }

                int index = indexer.Count;

                indexer[pair.Key] = index;
                Registered[pair.Key] = index;
            }

            string[][] strings = Localization.strings;

            if (strings != null)
                for (var i = 0; i < strings.Length; i++)
                    ApplyLanguage(i);

            ProjectEdenPlugin.Log.LogInfo(
                $"英文本地化已注册 {Registered.Count} 条" +
                (Skipped.Count > 0 ? $"，跳过 {Skipped.Count} 条（原版已有同名键）" : ""));

            if (Skipped.Count > 0)
                ProjectEdenPlugin.Log.LogWarning(
                    "以下中文串原版字符串表里已经有了，为避免覆盖原版翻译已跳过，" +
                    $"它们在英文下仍显示中文：{string.Join("、", Skipped.ToArray())}");
        }

        /// <summary>
        /// 把译文写进某一门语言的字符串表。
        ///
        /// <c>namesIndexer</c> 变长之后 <c>strings[lang]</c> 还是老长度，
        /// 必须先扩容再写，否则 <c>Translate</c> 会越界。<c>floats</c> 是并列的另一张表，
        /// 长度对不上同样会出事，一起扩。
        /// </summary>
        private static void ApplyLanguage(int index)
        {
            string[][] strings = Localization.strings;

            if (strings == null || index < 0 || index >= strings.Length) return;

            string[] table = strings[index];

            // 这门语言还没加载，等它自己的 LoadLanguage 跑完再说
            if (table == null) return;

            int want = Localization.namesIndexer.Count;

            if (table.Length < want)
            {
                var grown = new string[want];

                Array.Copy(table, grown, table.Length);

                table = grown;
                strings[index] = grown;
            }

            float[][] floats = Localization.floats;

            if (floats != null && index < floats.Length && floats[index] != null && floats[index].Length < want)
            {
                var grown = new float[want];

                Array.Copy(floats[index], grown, floats[index].Length);

                floats[index] = grown;
            }

            Localization.Language[] languages = Localization.Languages;
            int lcid = languages != null && index < languages.Length ? languages[index].lcId : 0;

            foreach (KeyValuePair<string, int> pair in Registered)
            {
                // 中文给原文，其余语言一律给英文——本 mod 只有中英两套文本，
                // 法语/德语/日语等落到英文比落到中文可读得多
                table[pair.Value] = lcid == LcidZhcn ? pair.Key : _table[pair.Key];
            }

            if (index != Localization.currentLanguageIndex) return;

            Localization.currentStrings = strings[index];

            if (floats != null && index < floats.Length) Localization.currentFloats = floats[index];
        }

        // ── 漏译核对 ──────────────────────────────────────────

        /// <summary>
        /// 核对本 mod 注册的每个 proto：名字和描述里带中文、而表里没有对应条目的，各报一次。
        ///
        /// <b>「是不是我们加的」按登记簿判，不按 ID 区间猜</b>——同时装了 GenesisBook 之类的
        /// mod 时，它们的 proto 也落在 6500 以上，按区间猜会把别人家的东西报成本 mod 漏译。
        ///
        /// 这条核对的意义是：以后往 ores.json 里加物品忘了配英文，<b>启动时就会知道</b>，
        /// 而不是等到有人切成英文才发现。
        /// </summary>
        internal static void VerifyCoverage()
        {
            if (_table == null) return;

            var missing = new List<string>();

            foreach (int id in ProtoSlots.OwnItemIds)
            {
                ItemProto item = LDB.items.Select(id);

                if (item == null) continue;

                Check(item.Name, missing);
                Check(item.Description, missing);
            }

            foreach (int id in ProtoSlots.OwnRecipeIds)
            {
                RecipeProto recipe = LDB.recipes.Select(id);

                if (recipe == null) continue;

                Check(recipe.Name, missing);
                Check(recipe.Description, missing);
            }

            foreach (VeinProto vein in LDB.veins.dataArray)
                if (vein != null && OreRegistry.IsCustomVein(vein.ID))
                    Check(vein.Name, missing);

            if (missing.Count == 0)
            {
                ProjectEdenPlugin.Log.LogInfo("英文本地化核对通过：本 mod 的 proto 名称与描述都有英文");

                return;
            }

            ProjectEdenPlugin.Log.LogWarning(
                $"以下 {missing.Count} 处中文没有英文译文，切到英文时会原样显示中文（补进 data/i18n.json 即可）：");

            foreach (string s in missing)
                ProjectEdenPlugin.Log.LogWarning($"    {Shorten(s)}");
        }

        private static void Check(string text, List<string> missing)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!HasChinese(text)) return;
            if (_table.ContainsKey(text)) return;
            if (missing.Contains(text)) return;

            missing.Add(text);
        }

        private static bool HasChinese(string text)
        {
            for (var i = 0; i < text.Length; i++)
                if (text[i] >= 0x4E00 && text[i] <= 0x9FFF)
                    return true;

            return false;
        }

        private static string Shorten(string text)
            => text.Length <= 40 ? text : text.Substring(0, 40) + "…";
    }

    /// <summary>data/i18n.json 的映射类型。</summary>
    [Serializable]
    internal class I18NConfig
    {
        /// <summary>中文原文 → 英文。以「//」开头的键是分节标记，载入时会被丢掉</summary>
        public Dictionary<string, string> strings;
    }
}
