using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 让「制造台窗口」和「合成面板的制造树」能显示<b>两个以上</b>的产物。
    ///
    /// <b>原版是完全展开的两套控件，没有循环。</b> <c>UIAssemblerWindow</c> 只有
    /// <c>productIcon0/1</c>、<c>productCountText0/1</c>、<c>productProgress0/1</c>、
    /// <c>extraProductProgress0/1</c>、<c>productButton0/1</c>；
    /// <c>UIReplicatorWindow</c> 只有 <c>treeMainIcon0/1</c>、<c>treeMainCountText0/1</c>。
    /// 本 mod 有 <b>9 条三产物配方和 1 条四产物配方</b>（碳热还原那一族、熔盐电解锂、铬铁法、钨酸解），
    /// 第三个产物因此根本没有控件可画。
    ///
    /// <b>而且它不只是「少画一个」，还会把整个窗口排错位。</b> 制造台窗口的三个横向位置是
    /// 按产物数写死的常量，而原版走的是 <c>products.Length &gt; 1</c> 这一个分支——
    /// 三产物配方会拿到<b>两产物的排版</b>：产物区只有 128 宽、速度箭头钉在 x=144、
    /// 原料框钉在 x=224。所以玩家看到的是「第三个产物不见了，而且速率文字和原料框的位置不对」。
    /// 这正是熔盐电解锂那张截图的成因。
    ///
    /// <b>排版规律是线性的，可以精确外推</b>（从 IL 里两个分支的常量读出来，不是估的）：
    ///
    /// | 产物数 | productGroup 宽 | speedGroup.x | servingGroup.x |
    /// |---|---|---|---|
    /// | 1 | 64 | 80 | 160 |
    /// | 2 | 128 | 144 | 224 |
    /// | N | 64N | 16+64N | 96+64N |
    ///
    /// 制造树同理：<c>treeMainBox</c> 宽 <c>64 + 50(N-1)</c>，
    /// 图标 <c>x = (i - (N-1)/2) × 50</c>，数量文字在图标的 <c>(-24, -11)</c> 处。
    /// N=1 和 N=2 代回去正好是原版那两组常量（64/114、0/-25、-24/-49），所以这不是另起一套排版，
    /// 而是把原版自己的规律往下续。
    ///
    /// <b>下游分支不用管，原版自己就不画。</b> <c>OnSelectedRecipeChange</c> 在 IL 05C4–05CB 处
    /// 用 <c>产物0 != null &amp;&amp; 产物1 == null</c> 卡住单产物分支，081D 处用
    /// <c>两个都非空</c> 卡住双产物分支——三产物两边都不进，<c>treeUpList</c> 直接是空的。
    /// 所以「中间那个产物不显示下游」是免费的，不需要为它写任何代码。
    /// </summary>
    [HarmonyPatch]
    internal static class MultiProductUIPatches
    {
        /// <summary>产物槽上限。四产物（铬块·烃热还原）是目前的最大值，留一点余量。</summary>
        private const int MaxSlots = 6;

        // ── 通用：克隆与路径解析 ──────────────────────────────

        private static bool IsAncestorOf(Transform a, Transform t)
        {
            for (Transform p = t; p != null; p = p.parent)
                if (p == a)
                    return true;

            return false;
        }

        /// <summary>找到同时包含这几个控件的最近祖先，也就是「一个产物槽」的根节点。</summary>
        private static Transform CommonAncestor(params Transform[] parts)
        {
            if (parts == null || parts.Length == 0 || parts[0] == null) return null;

            for (Transform c = parts[0]; c != null; c = c.parent)
            {
                var all = true;

                for (var i = 1; i < parts.Length; i++)
                {
                    if (parts[i] != null && IsAncestorOf(c, parts[i])) continue;

                    all = false;

                    break;
                }

                if (all) return c;
            }

            return null;
        }

        private static string PathOf(Transform root, Transform t)
        {
            if (t == null || t == root) return "";

            var parts = new List<string>();

            for (Transform p = t; p != null && p != root; p = p.parent) parts.Add(p.name);

            parts.Reverse();

            return string.Join("/", parts.ToArray());
        }

        private static T Resolve<T>(Transform root, string path) where T : Component
        {
            Transform t = path.Length == 0 ? root : root.Find(path);

            return t == null ? null : t.GetComponent<T>();
        }

        private static void Show(Component c, bool on)
        {
            if (c != null) c.gameObject.SetActive(on);
        }

        // ══ 一、制造台窗口 ════════════════════════════════════

        private sealed class AsmSlot
        {
            /// <summary>显隐用。整槽克隆时只有容器一个，分件克隆时是五个控件。</summary>
            public Component[] Parts;

            public Image Icon;
            public Image Progress;
            public Image ExtraProgress;
            public Text Count;
            public UIButton Button;
        }

        private static void ShowSlot(AsmSlot slot, bool on)
        {
            if (slot?.Parts == null) return;

            for (var i = 0; i < slot.Parts.Length; i++) Show(slot.Parts[i], on);
        }

        private static readonly List<AsmSlot> AsmSlots = new List<AsmSlot>();

        private static RectTransform _asmRoot0;
        private static RectTransform _asmRoot1;
        private static Vector2 _asmPos0;
        private static Vector2 _asmPos1;
        private static string _pIcon, _pProg, _pExtra, _pCount, _pButton;
        private static bool _asmReported;
        private static bool _asmProbed;
        private static bool _asmPieces;
        private static bool _asmSpacingReported;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIAssemblerWindow), "_OnUpdate")]
        private static void UIAssemblerWindow_OnUpdate(UIAssemblerWindow __instance)
        {
            int n = AsmProductCount(__instance, out AssemblerComponent assembler, out int[] products);

            ProbeAsm(__instance, n);

            // 两个及以下原版自己画得好好的，只要把我们多出来的槽收起来
            if (n <= 2)
            {
                for (var i = 0; i < AsmSlots.Count; i++) ShowSlot(AsmSlots[i], false);

                return;
            }

            // 按「实际画得出来的槽数」排版，而不是按配方声明的产物数：
            // 两者只在超过 MaxSlots 时才不同，但那时按 n 排会留下一段空白
            int shown = Mathf.Min(n, MaxSlots);

            if (!EnsureAsmSlots(__instance, n)) return;

            LayoutAsm(__instance, shown);

            FillAsm(__instance, assembler, products, shown);
        }

        /// <summary>
        /// 把制造台窗口这一帧看到的东西<b>无条件</b>报一次。
        /// 之前这里只在「扩展成功 / 放弃扩展」时才打日志，于是 n≤2 这条路整个是哑的——
        /// 日志里没有任何一行，既可能是「窗口没开过」，也可能是「开了但产物数读成了 2」。
        /// 这是本仓库已经付过好几次学费的同一个形状：<b>连无聊的状态也要报</b>。
        /// </summary>
        private static void ProbeAsm(UIAssemblerWindow w, int n)
        {
            if (_asmProbed) return;

            if (w.assemblerId <= 0) return;

            _asmProbed = true;

            AssemblerComponent a = w.factorySystem != null && w.factorySystem.assemblerPool != null
                                   && w.assemblerId < w.factorySystem.assemblerPool.Length
                ? w.factorySystem.assemblerPool[w.assemblerId]
                : default;

            RecipeProto proto = a.recipeId != 0 ? LDB.recipes.Select(a.recipeId) : null;

            ProjectEdenPlugin.Log.LogInfo(
                $"多产物界面·制造台窗口首次刷新：assemblerId={w.assemblerId}，" +
                $"factorySystem={(w.factorySystem == null ? "null" : "有")}，recipeId={a.recipeId}" +
                $"（{proto?.name ?? "无"}），配方原型产物数={proto?.Results?.Length ?? -1}，" +
                $"recipeExecuteData 产物数={n}");
        }

        private static int AsmProductCount(UIAssemblerWindow w, out AssemblerComponent assembler, out int[] products)
        {
            assembler = default;
            products = null;

            if (w.assemblerId <= 0 || w.factorySystem?.assemblerPool == null) return 0;

            if (w.assemblerId >= w.factorySystem.assemblerPool.Length) return 0;

            assembler = w.factorySystem.assemblerPool[w.assemblerId];

            if (assembler.id != w.assemblerId || assembler.recipeId == 0) return 0;

            // 取 recipeExecuteData 而不是配方原型：硬质合金那套「按建筑改配方」
            // 会把这里换成本建筑自己的副本，跟着它才不会和面板显示打架
            products = assembler.recipeExecuteData?.products;

            return products?.Length ?? 0;
        }

        /// <summary>
        /// 排版。三个常量全部按上面那张表外推，<b>顺带修好了原版的错位</b>——
        /// 原版对任何多产物配方都套用两产物的排版，速度箭头和原料框会压在产物区上。
        /// </summary>
        private static void LayoutAsm(UIAssemblerWindow w, int n)
        {
            if (w.productGroup != null)
                w.productGroup.sizeDelta = new Vector2(64f * n, 64f);

            if (w.speedGroup != null)
                w.speedGroup.anchoredPosition = new Vector2(16f + 64f * n, w.speedGroup.anchoredPosition.y);

            if (w.servingGroup != null)
                w.servingGroup.anchoredPosition = new Vector2(96f + 64f * n, w.speedGroup.anchoredPosition.y);
        }

        private static bool EnsureAsmSlots(UIAssemblerWindow w, int n)
        {
            if (!_asmPieces && (_asmRoot0 == null || _asmRoot1 == null))
            {
                if (!ResolveAsmTemplate(w)) return false;
            }

            int want = Mathf.Min(n, MaxSlots) - 2;

            while (AsmSlots.Count < want)
            {
                int index = AsmSlots.Count + 2;

                AsmSlot slot = _asmPieces ? CloneAsmPieces(w, index) : CloneAsmContainer(index);

                if (slot.Button != null)
                {
                    int captured = index;

                    // onClick 是 C# 事件，不随 GameObject 克隆走，所以这里是往空事件上绑；
                    // 直接写 += 会撞上 UIButton 上同名的字段与事件（CS0229），用仓库里既有的绑定辅助
                    slot.Button.BindOnClickSafe(_ => TakeProduct(captured));
                }

                AsmSlots.Add(slot);
            }

            return true;
        }

        /// <summary>产物槽是独立节点：整个克隆，位移取两个槽根的实测间距。</summary>
        private static AsmSlot CloneAsmContainer(int index)
        {
            GameObject clone = Object.Instantiate(_asmRoot1.gameObject, _asmRoot1.parent, false);

            clone.name = $"projecteden-product-slot{index}";

            var root = (RectTransform)clone.transform;

            root.anchoredPosition = _asmPos1 + (_asmPos1 - _asmPos0) * (index - 1);

            return new AsmSlot
            {
                Parts = new Component[] { root },
                Icon = Resolve<Image>(root, _pIcon),
                Progress = Resolve<Image>(root, _pProg),
                ExtraProgress = Resolve<Image>(root, _pExtra),
                Count = Resolve<Text>(root, _pCount),
                Button = Resolve<UIButton>(root, _pButton),
            };
        }

        /// <summary>
        /// 产物槽是平铺的（日志已证实本版本就是这样）。
        ///
        /// <b>不能把五个控件都克隆一遍。</b>它们平铺是指「共同祖先包着槽0」，
        /// 不代表五个之间没有嵌套：按钮很可能包着图标。那样按钮克隆会带出一张图标，
        /// 图标自己再克隆一张，<b>两张叠在一起而我们只改得到下面那张</b>——
        /// 表现就是第三格显示成了第二个产物的图标（克隆时带的那张），而数字是对的。
        ///
        /// 所以先求出「根」集合：没有被同组其它控件包着的那几个。只克隆根，
        /// 其余的按相对路径从根的克隆里解析出来。
        /// </summary>
        private static AsmSlot CloneAsmPieces(UIAssemblerWindow w, int index)
        {
            Component[] ones =
            {
                w.productIcon1, w.productProgress1, w.extraProductProgress1, w.productCountText1, w.productButton1,
            };

            Component[] zeros =
            {
                w.productIcon0, w.productProgress0, w.extraProductProgress0, w.productCountText0, w.productButton0,
            };

            Vector2 spacing = BiggestSpacing(w);

            var roots = new List<int>();

            for (var a = 0; a < ones.Length; a++)
            {
                if (ones[a] == null) continue;

                var nested = false;

                for (var b = 0; b < ones.Length; b++)
                {
                    if (a == b || ones[b] == null) continue;

                    // 两个组件在同一个节点上：只留下标小的那个当根
                    if (ones[b].transform == ones[a].transform)
                    {
                        if (b >= a) continue;

                        nested = true;

                        break;
                    }

                    if (!IsAncestorOf(ones[b].transform, ones[a].transform)) continue;

                    nested = true;

                    break;
                }

                if (!nested) roots.Add(a);
            }

            var rootClones = new Dictionary<int, Transform>();
            var parts = new List<Component>();

            for (var r = 0; r < roots.Count; r++)
            {
                int k = roots[r];

                GameObject clone = Object.Instantiate(ones[k].gameObject, ones[k].transform.parent, false);

                clone.name = $"projecteden-product-part{index}-{k}";

                var trs = clone.transform as RectTransform;
                var oneTrs = ones[k].transform as RectTransform;
                var zeroTrs = zeros[k] != null ? zeros[k].transform as RectTransform : null;

                if (trs != null && oneTrs != null)
                {
                    Vector2 own = zeroTrs != null
                        ? oneTrs.anchoredPosition - zeroTrs.anchoredPosition
                        : Vector2.zero;

                    // 自己算不出间距（槽0/槽1 局部坐标相同）就用全体最大的那个
                    if (own.sqrMagnitude < 1f) own = spacing;

                    trs.anchoredPosition = oneTrs.anchoredPosition + own * (index - 1);
                }

                rootClones[k] = clone.transform;

                parts.Add(clone.transform);
            }

            T Pick<T>(int k) where T : Component
            {
                if (ones[k] == null) return null;

                foreach (KeyValuePair<int, Transform> pair in rootClones)
                {
                    if (pair.Key != k && !IsAncestorOf(ones[pair.Key].transform, ones[k].transform)) continue;

                    return Resolve<T>(pair.Value, PathOf(ones[pair.Key].transform, ones[k].transform));
                }

                return null;
            }

            var slot = new AsmSlot
            {
                Parts = parts.ToArray(),
                Icon = Pick<Image>(0),
                Progress = Pick<Image>(1),
                ExtraProgress = Pick<Image>(2),
                Count = Pick<Text>(3),
                Button = Pick<UIButton>(4),
            };

            if (!_asmSpacingReported)
            {
                _asmSpacingReported = true;

                ProjectEdenPlugin.Log.LogInfo(
                    $"多产物界面：制造台分件克隆已建立，槽间距={spacing}，" +
                    $"根控件 {roots.Count} 个（共 5 个）；" +
                    $"图标={(slot.Icon == null ? "没解析到" : slot.Icon.name)}，" +
                    $"数量={(slot.Count == null ? "没解析到" : slot.Count.name)}，" +
                    $"进度={(slot.Progress == null ? "没解析到" : slot.Progress.name)}，" +
                    $"按钮={(slot.Button == null ? "没解析到" : slot.Button.name)}");
            }

            return slot;
        }

        /// <summary>五对控件里位移最大的那一个，当作“一个槽”的实际宽度。</summary>
        private static Vector2 BiggestSpacing(UIAssemblerWindow w)
        {
            Vector2 best = Vector2.zero;

            void Consider(Component one, Component zero)
            {
                var a = one?.transform as RectTransform;
                var b = zero?.transform as RectTransform;

                if (a == null || b == null) return;

                Vector2 d = a.anchoredPosition - b.anchoredPosition;

                if (d.sqrMagnitude > best.sqrMagnitude) best = d;
            }

            Consider(w.productIcon1, w.productIcon0);
            Consider(w.productProgress1, w.productProgress0);
            Consider(w.extraProductProgress1, w.extraProductProgress0);
            Consider(w.productCountText1, w.productCountText0);
            Consider(w.productButton1, w.productButton0);

            return best;
        }

        private static bool ResolveAsmTemplate(UIAssemblerWindow w)
        {
            Transform root1 = CommonAncestor(
                w.productIcon1?.transform, w.productCountText1?.transform,
                w.productProgress1?.transform, w.extraProductProgress1?.transform,
                w.productButton1?.transform);

            Transform root0 = CommonAncestor(
                w.productIcon0?.transform, w.productCountText0?.transform,
                w.productProgress0?.transform, w.extraProductProgress0?.transform,
                w.productButton0?.transform);

            // 关键的一道闸：如果「槽1 的根」同时也包着槽 0，那它就不是一个产物槽，
            // 而是整个产物区——克隆它会把两个槽一起复制出来。
            // 这种情况下宁可不做，也不要把界面搞乱。
            bool perSlot = root0 != null && root1 != null && root0 != root1
                           && !IsAncestorOf(root1, w.productIcon0.transform)
                           && root0 is RectTransform && root1 is RectTransform;

            // 日志已经证实本版本走的是这条：五个控件平铺在同一个父节点下。
            // 上一版到这里就放弃了，所以第三个产物一直没画出来。
            if (!perSlot)
            {
                _asmPieces = true;

                ProjectEdenPlugin.Log.LogInfo(
                    "多产物界面：制造台窗口的产物槽是平铺结构（槽0 与槽1 共用父节点），改用分件克隆");

                return true;
            }

            _asmRoot0 = (RectTransform)root0;
            _asmRoot1 = (RectTransform)root1;
            _asmPos0 = _asmRoot0.anchoredPosition;
            _asmPos1 = _asmRoot1.anchoredPosition;

            _pIcon = PathOf(root1, w.productIcon1.transform);
            _pProg = PathOf(root1, w.productProgress1.transform);
            _pExtra = PathOf(root1, w.extraProductProgress1.transform);
            _pCount = PathOf(root1, w.productCountText1.transform);
            _pButton = PathOf(root1, w.productButton1.transform);

            if (_asmReported) return true;

            _asmReported = true;

            ProjectEdenPlugin.Log.LogInfo(
                $"多产物界面：制造台窗口的产物槽模板已解析（槽间距 {_asmPos1 - _asmPos0}），最多扩到 {MaxSlots} 个产物");

            return true;
        }

        private static void FillAsm(UIAssemblerWindow w, AssemblerComponent assembler, int[] products, int n)
        {
            // 进度条直接抄槽 0 这一帧的结果：它是原版刚算好的 time/timeSpend，
            // 自己再算一遍只会多一处可能和原版对不上的地方
            float fill = w.productProgress0 != null ? w.productProgress0.fillAmount : 0f;
            float extraFill = w.extraProductProgress0 != null ? w.extraProductProgress0.fillAmount : 0f;

            int[] produced = assembler.produced;

            for (var s = 0; s < AsmSlots.Count; s++)
            {
                AsmSlot slot = AsmSlots[s];
                int i = s + 2;

                if (i >= n)
                {
                    ShowSlot(slot, false);

                    continue;
                }

                ShowSlot(slot, true);

                ItemProto proto = LDB.items.Select(products[i]);

                if (slot.Icon != null)
                {
                    slot.Icon.sprite = proto?.iconSprite;

                    Show(slot.Icon, true);
                }

                int count = produced != null && i < produced.Length ? produced[i] : 0;

                if (slot.Count != null)
                {
                    slot.Count.text = count.ToString();

                    Show(slot.Count, true);
                }

                if (slot.Progress != null)
                {
                    Show(slot.Progress, true);

                    slot.Progress.fillAmount = fill;
                }

                if (slot.ExtraProgress != null)
                {
                    Show(slot.ExtraProgress, true);

                    slot.ExtraProgress.fillAmount = extraFill;
                }

                if (slot.Button == null) continue;

                slot.Button.tips.itemId = products[i];
                slot.Button.tips.itemInc = 0;
                slot.Button.tips.itemCount = 0;
                slot.Button.tips.type = UIButton.ItemTipType.Item;
                slot.Button.tips.corner = 2;
                slot.Button.tips.delay = 0.2f;

                if (slot.Button.button != null) slot.Button.button.interactable = count > 0;
            }
        }

        /// <summary>
        /// 点产物图标取货，照抄 <c>OnProductIcon1Click</c>，但<b>只扣真正装进背包的那部分</b>。
        /// 原版那两个是无条件 <c>produced[i] = 0</c>：背包满的时候
        /// <c>TryAddItemToPackage</c> 返回 0，而产物照样被清空——东西就这么没了。
        /// 这是原版的缺陷，没有理由在新槽位上复制它。
        /// </summary>
        private static void TakeProduct(int index)
        {
            UIAssemblerWindow w = UIRoot.instance?.uiGame?.assemblerWindow;

            if (w == null || w.assemblerId <= 0 || w.factory == null || w.factorySystem?.assemblerPool == null) return;

            AssemblerComponent assembler = w.factorySystem.assemblerPool[w.assemblerId];

            if (assembler.id != w.assemblerId || assembler.recipeId == 0) return;

            int[] products = assembler.recipeExecuteData?.products;

            if (products == null || index >= products.Length) return;

            int have = assembler.produced[index];

            if (have <= 0) return;

            if (w.player == null) return;

            if (w.player.inhandItemId > 0)
            {
                // 键原封不动地照抄原版 OnProductIcon1Click，它是原版自己的本地化键，
                // 所以不能也不该往 i18n.json 里加条目（会覆盖原版翻译）
                UIRealtimeTip.Popup("不能手动放入物品".Translate(), true, 0);

                return;
            }

            int added = w.player.TryAddItemToPackage(products[index], have, 0, false, 0, false);

            w.factorySystem.assemblerPool[w.assemblerId].produced[index] = have - added;

            if (added > 0) UIItemup.Up(products[index], added);
        }

        // ══ 二、合成面板的制造树 ══════════════════════

        private sealed class TreeSlot
        {
            /// <summary>
            /// 显隐用。<b>分件克隆时图标和文字是两个独立节点</b>，
            /// 只藏图标会把克隆出来的「x N」永远留在屏幕上。
            /// </summary>
            public Component[] Parts;

            /// <summary>整槽克隆时的容器；分件克隆时为 null。</summary>
            public RectTransform Container;

            public Image Icon;
            public Text Count;
            public UIButton Button;
        }

        private static void ShowTreeSlot(TreeSlot slot, bool on)
        {
            if (slot?.Parts == null) return;

            for (var i = 0; i < slot.Parts.Length; i++) Show(slot.Parts[i], on);
        }

        private static readonly List<TreeSlot> TreeSlots = new List<TreeSlot>();

        /// <summary>数量文字相对图标的偏移，从原版控件上实测，不写死。</summary>
        private static Vector2 _treeCountOffset = new Vector2(-24f, -11f);

        /// <summary>
        /// 同一个偏移的<b>世界坐标</b>版本。数量文字最终用它摆，而不是用
        /// anchoredPosition 加减——因为 anchoredPosition 的含义取决于锚点怎么配：
        /// 图标是居中锚点而文字是拉伸或靠左锚点的话，把主框从 114 撑到 164
        /// 会让两者同一个数字落在不同地方，表现就是「图标对了、数字撑开了」。
        /// 世界坐标没有这个歧义。
        /// </summary>
        private static Vector3 _treeCountWorldDelta;

        /// <summary>槽 1 在预制体里的原位置。两产物配方要靠它复原。</summary>
        private static Vector2 _icon1Home;

        private static Vector2 _count1Home;

        private static bool _treeMeasured;
        private static bool _treeGaveUp;
        private static bool _treeReported;
        private static bool _treePerSlot;
        private static int _treeProbed;
        private static int _boxDumped;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIReplicatorWindow), "OnSelectedRecipeChange")]
        private static void UIReplicatorWindow_OnSelectedRecipeChange(UIReplicatorWindow __instance)
        {
            MeasureTree(__instance);

            RecipeProto recipe = __instance.selectedRecipe;

            int n = recipe?.Results?.Length ?? 0;

            // 先把所有扩展槽收起来，再按需放出。上一版是「从第 shown-2 个开始隐藏」，
            // 算对了也只是刚好对；先全隐藏再显示是<b>结构上</b>不可能残留旧图标，
            // 而残留正是「换了一条配方，框里多出一个不相干的东西」的成因。
            for (var k = 0; k < TreeSlots.Count; k++) ShowTreeSlot(TreeSlots[k], false);

            ProbeTree(__instance, recipe, n);

            if (n <= 2)
            {
                RestoreSlot1(__instance);

                return;
            }

            if (!EnsureTreeSlots(__instance, n)) return;

            HideDownstream(__instance);

            LayoutTree(__instance, recipe, n);

            DumpBox(__instance, recipe);
        }

        /// <summary>
        /// 数量文字相对图标的偏移，<b>在我们动任何东西之前</b>从槽 1 实测一次。
        /// 原版只给槽 0 写位置，槽 1 用的是预制体里的值；写死 (-24,-11) 只是从原版那
        /// 两组常量反推出来的，<b>推得对不等于预制体就是这么摆的</b>。
        /// </summary>
        private static void MeasureTree(UIReplicatorWindow w)
        {
            if (_treeMeasured) return;

            if (w.treeMainIcon1 == null || w.treeMainCountText1 == null) return;

            _treeMeasured = true;

            _treeCountOffset = w.treeMainCountText1.rectTransform.anchoredPosition
                               - w.treeMainIcon1.rectTransform.anchoredPosition;

            _treeCountWorldDelta = w.treeMainCountText1.rectTransform.position
                                   - w.treeMainIcon1.rectTransform.position;

            _icon1Home = w.treeMainIcon1.rectTransform.anchoredPosition;
            _count1Home = w.treeMainCountText1.rectTransform.anchoredPosition;
        }

        /// <summary>选中多产物配方的头几次把真实坐标报出来，换一次往返就能定位。</summary>
        private static void ProbeTree(UIReplicatorWindow w, RecipeProto recipe, int n)
        {
            if (_treeProbed >= 3 || recipe == null || n < 3) return;

            _treeProbed++;

            ProjectEdenPlugin.Log.LogInfo(
                $"多产物界面·制造树：「{recipe.name}」产物数 {n}，" +
                $"数量文字偏移实测={_treeCountOffset}，" +
                $"图标0 父节点={w.treeMainIcon0?.transform.parent?.name}，" +
                $"文字0 父节点={w.treeMainCountText0?.transform.parent?.name}，" +
                $"主框={w.treeMainBox?.name} {w.treeMainBox?.sizeDelta}，" +
                $"扩展槽 {TreeSlots.Count} 个（{(_treePerSlot ? "整槽克隆" : "分件克隆")}）；" +
                $"图标0 {Rect(w.treeMainIcon0)}，文字0 {Rect(w.treeMainCountText0)}，" +
                $"图标1 {Rect(w.treeMainIcon1)}，文字1 {Rect(w.treeMainCountText1)}");
        }

        /// <summary>
        /// 把槽 1 的图标和数量文字放回预制体里的位置。
        ///
        /// <b>原版每次只写槽 0 的坐标</b>（<c>icon0.anchoredPosition = 产物数 &gt; 1 ? -25 : 0</c>），
        /// 槽 1 用的是预制体常量——它<b>假设那个值永远不会变</b>。
        /// 我们为了三产物把槽 1 挑到过中间（日志上的 <c>icon 2@(0,0)</c>），
        /// 原版不会帮忙挑回来，于是下一条两产物配方的两个图标挤在 -25 和 0 上。
        ///
        /// 这和建造栏那条「空出来的槽位不会被清理」是同一个形状：
        /// <b>原版只写它需要变的那部分，其余当常量；一旦被改过就再也回不去。</b>
        /// 主框尺寸和槽 0 的坐标不在此列，因为原版每次都重写。
        /// </summary>
        private static void RestoreSlot1(UIReplicatorWindow w)
        {
            if (!_treeMeasured) return;

            if (w.treeMainIcon1 != null) w.treeMainIcon1.rectTransform.anchoredPosition = _icon1Home;

            if (w.treeMainCountText1 != null) w.treeMainCountText1.rectTransform.anchoredPosition = _count1Home;
        }

        /// <summary>
        /// 三产物及以上时收起下游分支。
        ///
        /// <b>之前说「原版自己就不画下游」是读错了分支。</b>
        /// <c>OnSelectedRecipeChange</c> 在 IL 05C4–05CB 卡的是<b>单产物</b>那条路，
        /// 081D 处还有一条<b>双产物</b>的路——三产物会走进去，
        /// 按产物0/产物1 各建一排消费者节点，位置写死在 x=±(90 + 46k)、y=52，
        /// 那是按「图标0 在 -25、图标1 在 +25」算的。我们把图标改成了 -50/0/+50，
        /// 连线和节点就对不上了。用户已明确说多产物时不必显示下游，
        /// 所以这里直接收起，而不是去重算一套几何。
        /// </summary>
        private static void HideDownstream(UIReplicatorWindow w)
        {
            Show(w.treeMainLineL, false);
            Show(w.treeMainLineR, false);

            if (w.treeUpList == null) return;

            for (var i = 0; i < w.treeUpList.Count; i++) Show(w.treeUpList[i], false);
        }

        /// <summary>
        /// 把主框里的<b>所有子节点</b>列出来。截图上看到一个多余的图标时，
        /// 它到底是残留的克隆、原版的节点，还是半透明面板透出来的背景格子，
        /// 像素上是分不出来的——这一行能。
        /// </summary>
        private static void DumpBox(UIReplicatorWindow w, RecipeProto recipe)
        {
            if (_boxDumped >= 2 || w.treeMainBox == null) return;

            _boxDumped++;

            var sb = new System.Text.StringBuilder();

            sb.Append("\u591a\u4ea7\u7269\u754c\u9762\u00b7\u4e3b\u6846\u6e05\u5355\u300c").Append(recipe.name).Append("\u300d\uff1a");

            for (var i = 0; i < w.treeMainBox.childCount; i++)
            {
                Transform c = w.treeMainBox.GetChild(i);
                var rt = c as RectTransform;

                sb.Append(c.gameObject.activeSelf ? "" : "[\u9690]")
                  .Append(c.name)
                  .Append('@')
                  .Append(rt == null ? "?" : rt.anchoredPosition.ToString())
                  .Append("  ");
            }

            sb.Append(" ‖ 下游节点：");

            if (w.treeUpList == null || w.treeUpList.Count == 0)
            {
                sb.Append("无");
            }
            else
            {
                for (var i = 0; i < w.treeUpList.Count; i++)
                {
                    UIButton b = w.treeUpList[i];

                    if (b == null) continue;

                    var rt = b.transform as RectTransform;

                    sb.Append(b.gameObject.activeSelf ? "" : "[隐]")
                      .Append(rt == null ? "?" : rt.anchoredPosition.ToString())
                      .Append("  ");
                }
            }

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>把一个控件的锚点/轴心/尺寸写成一行，出了事时一眼能看出是不是锚点不一致。</summary>
        private static string Rect(Graphic g)
        {
            if (g == null) return "无";

            RectTransform t = g.rectTransform;

            return $"[锚{t.anchorMin}-{t.anchorMax} 轴{t.pivot} 尺{t.sizeDelta} 位{t.anchoredPosition}]";
        }

        private static bool EnsureTreeSlots(UIReplicatorWindow w, int n)
        {
            if (_treeGaveUp) return false;

            if (w.treeMainIcon1 == null || w.treeMainCountText1 == null)
            {
                _treeGaveUp = true;

                ProjectEdenPlugin.Log.LogWarning("多产物界面：制造树缺少第 2 个产物控件，无法扩展");

                return false;
            }

            int want = Mathf.Min(n, MaxSlots) - 2;

            if (TreeSlots.Count >= want) return true;

            // 图标和数量文字有没有共同的「槽」容器，是预制体的事，离线看不到。
            // 有就整个克隆，没有就各克隆各的。判据和制造台窗口那边一样：
            // 共同祖先<b>不能把槽 0 也包进去</b>，否则克隆的是整个产物区。
            Transform shared = CommonAncestor(w.treeMainIcon1.transform, w.treeMainCountText1.transform);

            _treePerSlot = shared is RectTransform
                           && w.treeMainIcon0 != null
                           && !IsAncestorOf(shared, w.treeMainIcon0.transform);

            while (TreeSlots.Count < want)
            {
                int index = TreeSlots.Count + 2;

                TreeSlots.Add(_treePerSlot
                    ? CloneTreeContainer((RectTransform)shared, w, index)
                    : CloneTreePieces(w, index));
            }

            if (_treeReported) return true;

            _treeReported = true;

            ProjectEdenPlugin.Log.LogInfo(
                "多产物界面：制造树的产物槽已扩展（"
                + (_treePerSlot ? "整槽克隆：" + shared.name : "图标与文字分别克隆")
                + $"），最多 {MaxSlots} 个产物");

            return true;
        }

        private static TreeSlot CloneTreeContainer(RectTransform template, UIReplicatorWindow w, int index)
        {
            GameObject clone = Object.Instantiate(template.gameObject, template.parent, false);

            clone.name = $"projecteden-tree-slot{index}";

            var root = (RectTransform)clone.transform;

            return new TreeSlot
            {
                Parts = new Component[] { root },
                Container = root,
                Icon = Resolve<Image>(root, PathOf(template, w.treeMainIcon1.transform)),
                Count = Resolve<Text>(root, PathOf(template, w.treeMainCountText1.transform)),
                Button = w.treeMainButton1 == null
                    ? null
                    : Resolve<UIButton>(root, PathOf(template, w.treeMainButton1.transform)),
            };
        }

        private static TreeSlot CloneTreePieces(UIReplicatorWindow w, int index)
        {
            GameObject icon = Object.Instantiate(w.treeMainIcon1.gameObject, w.treeMainIcon1.transform.parent, false);
            GameObject count = Object.Instantiate(w.treeMainCountText1.gameObject, w.treeMainCountText1.transform.parent, false);

            icon.name = $"projecteden-tree-icon{index}";
            count.name = $"projecteden-tree-count{index}";

            return new TreeSlot
            {
                Parts = new Component[] { icon.transform, count.transform },
                Container = null,
                Icon = icon.GetComponent<Image>(),
                Count = count.GetComponent<Text>(),
                Button = icon.GetComponent<UIButton>(),
            };
        }

        /// <summary>
        /// 重排主框。<b>槽 0 和槽 1 也要一起重排</b>——原版把它们按两产物摆好了，
        /// 三产物时整排要重新居中，只摆新槽会左右不对称。
        /// </summary>
        private static void LayoutTree(UIReplicatorWindow w, RecipeProto recipe, int n)
        {
            int shown = Mathf.Min(n, MaxSlots);

            if (w.treeMainBox != null)
                w.treeMainBox.sizeDelta = new Vector2(64f + 50f * (shown - 1), 64f);

            for (var i = 0; i < shown; i++)
            {
                float x = (i - (shown - 1) * 0.5f) * 50f;

                ItemProto proto = LDB.items.Select(recipe.Results[i]);
                int amount = recipe.ResultCounts[i];

                Image icon;
                Text count;
                UIButton button;
                RectTransform container = null;

                if (i == 0)
                {
                    icon = w.treeMainIcon0;
                    count = w.treeMainCountText0;
                    button = w.treeMainButton0;
                }
                else if (i == 1)
                {
                    icon = w.treeMainIcon1;
                    count = w.treeMainCountText1;
                    button = w.treeMainButton1;
                }
                else
                {
                    TreeSlot slot = TreeSlots[i - 2];

                    icon = slot.Icon;
                    count = slot.Count;
                    button = slot.Button;
                    container = slot.Container;

                    ShowTreeSlot(slot, true);
                }

                if (icon == null) continue;

                if (container != null)
                {
                    container.anchoredPosition = new Vector2(x, 0f);
                }
                else
                {
                    icon.rectTransform.anchoredPosition = new Vector2(x, 0f);
                }

                icon.sprite = proto?.iconSprite;

                Show(icon, true);

                if (count != null)
                {
                    // 整槽克隆时文字已经跟着容器走了，不能再摆一次
                    if (container == null)
                        count.rectTransform.position = icon.rectTransform.position + _treeCountWorldDelta;

                    count.text = amount == 1 ? "" : "x " + amount;

                    Show(count, true);
                }

                if (button != null)
                {
                    button.tips.itemId = proto?.ID ?? 0;
                    button.tips.type = UIButton.ItemTipType.Item;
                }

                if (_treeProbed <= 3 && _laidOut < 24)
                {
                    _laidOut++;

                    ProjectEdenPlugin.Log.LogInfo(
                        $"多产物界面·排完：产物{i}「{proto?.name}」目标x={x}，" +
                        $"图标位={icon.rectTransform.anchoredPosition}/世界{icon.rectTransform.position}，" +
                        $"文字位={(count == null ? "无" : count.rectTransform.anchoredPosition.ToString())}" +
                        $"/世界{(count == null ? "无" : count.rectTransform.position.ToString())}，" +
                        $"主框={w.treeMainBox?.sizeDelta}");
                }
            }
        }

        private static int _laidOut;
    }
}
