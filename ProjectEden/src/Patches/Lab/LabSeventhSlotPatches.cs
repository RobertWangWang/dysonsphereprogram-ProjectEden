using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 给矩阵实验室窗口加出第七个矩阵格，并把五边形改成六边形。
    ///
    /// <b>布局是量出来的，不是设计出来的。</b> 实测六格的位置（<c>LabWindowSurvey</c>）：
    ///
    /// <code>
    /// [0] (95.0, 21.5)   [1] (-59.0, -90.5)  [2] (59.0, -90.5)
    /// [3] (-95.0, 21.5)  [4] (0.0, 90.5)     [5] (0.0, -9.5)     尺寸均为 96×96
    /// </code>
    ///
    /// 以第 5 格 (0, −9.5) 为圆心重算，前五格是<b>半径 100 的正五边形</b>，
    /// 角度正好是 <c>90° − 72°k</c>；第 5 格（宇宙矩阵）在正中。
    ///
    /// <b>所以第七格只能靠「五边形改六边形」，不能塞进空隙。</b>
    /// 五边形最大的空隙在正下方，塞一个进去两边各剩 36°，
    /// 而 36° 在半径 100 上相邻中心距只有 <c>2×100×sin18° ≈ 61.8</c> px，
    /// 控件却有 96 px 宽——<b>会叠</b>。改成 60° 均分则中心距等于半径本身（100 px），
    /// 刚好放得下 96 px 的控件。这两个数是这个方案成立与否的全部依据。
    ///
    /// <b>时机与接线：挂 <c>_OnInit</c> 的后缀，然后什么都不接。</b>
    /// <c>ManualBehaviour._Init</c> 先调 <c>_OnInit</c>（IL 001A）再调
    /// <c>_OnRegEvent</c>（IL 0036），而 <c>_OnRegEvent</c> 的接线循环是
    /// <c>for (i = 0; i &lt; itemButtons.Length; i++) { data = i; onClick += OnItemButtonClick; }</c>，
    /// <b>以 ldlen 为界</b>（IL 0036）。所以在 <c>_OnInit</c> 后缀里把数组扩好，
    /// 第七个按钮由原版自己接线；自己再挂一次就是双重订阅，点一下搬两次。
    /// 同理 <c>matrixProtos</c> / <c>matrixRecipes</c> 也不用管——
    /// <c>_OnInit</c> 本体（IL 000B–0063）已经按 <c>matrixIds.Length</c> 开好并填满了。
    ///
    /// <b>克隆只克隆根。</b> 一格有八个控件（按钮、图标、进度、数字、锁、三个增产剂箭头），
    /// 它们多半互相嵌套；逐个克隆会得到两份，写数据的是其中一个、画在上面的是另一个——
    /// 那正是 <see cref="MultiProductUIPatches"/> 那次「slot 3 显示 slot 2 的图标配 slot 3 的数字」
    /// 的成因。这里先算出这八个里<b>谁不被其余任何一个包含</b>（根），只克隆根，
    /// 其余全部按相对路径从根的克隆体里取。
    /// </summary>
    [HarmonyPatch]
    internal static class LabSeventhSlotPatches
    {
        /// <summary>一格拥有的控件个数：按钮 + 图标 + 进度 + 数字 + 锁 + 三个增产剂箭头。</summary>
        private const int PartsPerSlot = 8;

        /// <summary>每格的增产剂箭头个数，即 <c>itemIncs</c> 相对其余五个数组的步长。</summary>
        private const int IncsPerSlot = 3;

        /// <summary>环上格子的下标，顺时针，<b>含新加的那个</b>（插在正下方）。</summary>
        private static readonly List<int> RingOrder = new List<int>();

        /// <summary>装饰线分组，每组记着它<b>原始</b>的半径、步长与相位。</summary>
        private static readonly List<LineGroup> LineGroups = new List<LineGroup>();

        private static Vector2 _center;
        private static float _cellRadius;
        private static int _extraIndex = -1;
        private static int _appliedRing = -1;

        /// <summary>哪些格数已经报过了（按位）。这条日志由模式切换触发，不收就会刷屏。</summary>
        private static int _loggedRings;
        private static bool _logged;
        private static bool _hideLogged;

        /// <summary>第七格克隆出来的那些根节点，合成模式下整格收起来时用。</summary>
        private static readonly List<Transform> ExtraRoots = new List<Transform>();

        /// <summary>一组同款装饰线。几何参数记的是<b>原始值</b>，每次重排都从它推，不叠加。</summary>
        private sealed class LineGroup
        {
            internal readonly List<RectTransform> Members = new List<RectTransform>();
            internal float Radius;
            internal float Step;
            internal float Phase;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UILabWindow), "_OnInit")]
        private static void UILabWindow_OnInit(UILabWindow __instance)
        {
            if (!BioMatrixPatches.Ready) return;

            int want = LabComponent.matrixIds?.Length ?? 0;
            UIButton[] buttons = __instance.itemButtons;

            if (buttons == null || buttons.Length == 0 || want <= buttons.Length) return;

            int had = buttons.Length;
            var sb = new StringBuilder($"矩阵实验室窗口：控件 {had} 格 → {want} 格");

            for (int i = had; i < want; i++)
                if (!AddSlot(__instance, sb))
                    return;

            Measure(__instance, had, sb);

            if (!_logged)
            {
                _logged = true;

                DumpRing(__instance, __instance.itemButtons[0].transform.parent);
                ProjectEdenPlugin.Log.LogInfo(sb.ToString());
            }
        }

        /// <summary>照第 0 格克隆出一整套控件，并把六个数组各扩一格。</summary>
        private static bool AddSlot(UILabWindow w, StringBuilder sb)
        {
            // 顺序固定，下面按下标取回
            var src = new Component[PartsPerSlot];
            src[0] = Get(w.itemButtons, 0);
            src[1] = Get(w.itemIcons, 0);
            src[2] = Get(w.itemPercents, 0);
            src[3] = Get(w.itemCountTexts, 0);
            src[4] = Get(w.itemLocks, 0);

            for (var k = 0; k < IncsPerSlot; k++) src[5 + k] = Get(w.itemIncs, k);

            if (src[0] == null)
            {
                ProjectEdenPlugin.Log.LogError("矩阵实验室窗口：第 0 格没有按钮，放弃加第七格");

                return false;
            }

            Transform parent = src[0].transform.parent;

            if (parent == null)
            {
                ProjectEdenPlugin.Log.LogError("矩阵实验室窗口：第 0 格的按钮没有父节点，放弃加第七格");

                return false;
            }

            // 谁是根：不被这八个里的任何其它一个包含的那些。
            // 只克隆根，其余按相对路径从根的克隆体里取——见类注释。
            var owner = new int[PartsPerSlot];
            var clones = new Transform[PartsPerSlot];

            for (var i = 0; i < PartsPerSlot; i++) owner[i] = OwnerOf(src, i);

            var roots = 0;

            for (var i = 0; i < PartsPerSlot; i++)
            {
                if (src[i] == null || owner[i] != i) continue;

                roots++;
                clones[i] = CloneUnder(src[i].transform, parent);
            }

            var parts = new Component[PartsPerSlot];

            for (var i = 0; i < PartsPerSlot; i++)
            {
                if (src[i] == null) continue;

                Transform root = clones[owner[i]];

                if (root == null) continue;

                string path = RelPath(src[owner[i]].transform, src[i].transform);
                Transform found = path.Length == 0 ? root : root.Find(path);

                if (found != null) parts[i] = found.GetComponent(src[i].GetType());
            }

            // <b>先全部取齐，再动数组——取不齐就整个回滚。</b>
            // 数组一旦扩到 7，原版就当第七格是真的了：itemButtons[6] 为 null 会让
            // _OnRegEvent 的接线循环当场 NRE，itemIcons[6] 为 null 会让 _OnUpdate 每帧 NRE。
            // 那种坏法比「没有第七格」难查得多，所以宁可留在 6 格。
            var missing = 0;

            for (var i = 0; i < PartsPerSlot; i++)
                if (src[i] != null && parts[i] == null)
                    missing++;

            if (missing > 0)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"矩阵实验室窗口：克隆第七格时有 {missing} 个控件没取到，已整个撤回，界面保持 6 格。"
                    + "（多半是 prefab 的层级变了，克隆体里按相对路径找不到对应节点）");

                for (var i = 0; i < PartsPerSlot; i++)
                    if (clones[i] != null)
                        Object.Destroy(clones[i].gameObject);

                return false;
            }

            w.itemButtons = Append(w.itemButtons, parts[0] as UIButton);
            w.itemIcons = Append(w.itemIcons, parts[1] as Image);
            w.itemPercents = Append(w.itemPercents, parts[2] as Image);
            w.itemCountTexts = Append(w.itemCountTexts, parts[3] as Text);
            w.itemLocks = Append(w.itemLocks, parts[4] as Image);

            // itemIncs 的步长是 3（每格三个增产剂箭头），所以一次追加三个
            for (var k = 0; k < IncsPerSlot; k++) w.itemIncs = Append(w.itemIncs, parts[5 + k] as Image);

            // 记住克隆出来的根，合成模式下要把整格收起来（见 UILabWindow_OnUpdate）。
            // 必须记根而不是只记按钮：五个控件不一定都挂在按钮底下，
            // 只关按钮会留下几个孤零零的图标浮在那儿。
            foreach (Transform t in clones)
                if (t != null)
                    ExtraRoots.Add(t);

            sb.Append($"\n  照第 0 格克隆：{PartsPerSlot} 个控件里有 {roots} 个是根（只克隆这些），"
                      + "其余按相对路径从根的克隆体里取，全部取齐 ✓");

            return true;
        }

        /// <summary>
        /// 这八个控件里，包含 <paramref name="i"/> 的最外层的那一个（可能就是它自己）。
        /// </summary>
        private static int OwnerOf(Component[] src, int i)
        {
            if (src[i] == null) return i;

            int best = i;
            int bestDepth = Depth(src[i].transform);

            for (var j = 0; j < src.Length; j++)
            {
                if (j == i || src[j] == null) continue;
                if (RelPath(src[j].transform, src[i].transform) == null) continue;

                int d = Depth(src[j].transform);

                if (d < bestDepth)
                {
                    bestDepth = d;
                    best = j;
                }
            }

            return best;
        }

        private static int Depth(Transform t)
        {
            var d = 0;

            for (Transform p = t; p != null; p = p.parent) d++;

            return d;
        }

        /// <summary>
        /// 克隆一棵子树挂到 <paramref name="parent"/> 下。
        ///
        /// <b>必须用三参重载并传 false。</b> <c>Instantiate(obj, parent)</c> 默认
        /// <c>worldPositionStays = true</c>，它会为了保住世界坐标去反算局部变换——
        /// 对 RectTransform 就是把 <c>anchoredPosition</c> 改掉。这里要的恰恰是照搬局部变换。
        /// 后面再把 RectTransform 的几个字段显式抄一遍，免得依赖 Instantiate 的细节。
        /// </summary>
        private static Transform CloneUnder(Transform src, Transform parent)
        {
            GameObject clone = Object.Instantiate(src.gameObject, parent, false);
            clone.name = "projecteden-" + src.name;

            if (src is RectTransform from && clone.transform is RectTransform to)
            {
                to.anchorMin = from.anchorMin;
                to.anchorMax = from.anchorMax;
                to.pivot = from.pivot;
                to.sizeDelta = from.sizeDelta;
                to.anchoredPosition = from.anchoredPosition;
                to.localScale = from.localScale;
                to.localRotation = from.localRotation;
            }

            clone.SetActive(src.gameObject.activeSelf);

            return clone.transform;
        }

        /// <summary>child 相对 root 的路径（自己则为空串）；不是后代则返回 null。</summary>
        private static string RelPath(Transform root, Transform child)
        {
            if (child == root) return "";

            var parts = new List<string>();

            for (Transform t = child; t != null; t = t.parent)
            {
                if (t == root)
                {
                    parts.Reverse();

                    return string.Join("/", parts.ToArray());
                }

                parts.Add(t.name);
            }

            return null;
        }

        private static T Get<T>(T[] array, int i) where T : Component
            => array != null && i < array.Length ? array[i] : null;

        private static T[] Append<T>(T[] array, T item)
        {
            var next = new T[array.Length + 1];
            System.Array.Copy(array, next, array.Length);
            next[array.Length] = item;

            return next;
        }

        /// <summary>
        /// 量出这一圈的几何：圆心、半径、顺时针次序，以及装饰线分组。<b>只量，不摆。</b>
        ///
        /// 一个数都不写死：圆心取离原点最近的那格（中心那格实测半径 9.5，其余都在 90 以上），
        /// 半径取环上原有各格到圆心距离的平均，次序按各格现在的顺时针角排——
        /// 这样原版那个五角星式的排布得以保留，不会因为重排就换了位置。
        /// 原版哪天挪了布局，这里跟着挪。
        ///
        /// 新格子插在<b>正下方</b>（顺时针角跨过 180° 处），那是五边形最大的空隙，
        /// 于是切到六边形时现有五格各自只挪 12° 以内。
        /// </summary>
        private static void Measure(UILabWindow w, int had, StringBuilder sb)
        {
            RingOrder.Clear();
            LineGroups.Clear();
            _appliedRing = -1;

            UIButton[] buttons = w.itemButtons;
            int n = buttons.Length;

            if (n < 4) return;

            var centerIdx = -1;
            float best = float.MaxValue;

            for (var i = 0; i < had; i++)
            {
                RectTransform t = Rect(buttons, i);

                if (t == null) continue;

                float d = t.anchoredPosition.magnitude;

                if (d < best)
                {
                    best = d;
                    centerIdx = i;
                }
            }

            if (centerIdx < 0) return;

            _center = Rect(buttons, centerIdx).anchoredPosition;

            var ring = new List<int>();
            var radius = 0f;

            for (var i = 0; i < had; i++)
            {
                if (i == centerIdx || Rect(buttons, i) == null) continue;

                ring.Add(i);
                radius += (Rect(buttons, i).anchoredPosition - _center).magnitude;
            }

            if (ring.Count < 3) return;

            _cellRadius = radius / ring.Count;

            ring.Sort((a, b) => Clockwise(Rect(buttons, a), _center).CompareTo(Clockwise(Rect(buttons, b), _center)));

            var at = ring.Count;

            for (var k = 0; k < ring.Count; k++)
                if (Clockwise(Rect(buttons, ring[k]), _center) > 180f)
                {
                    at = k;

                    break;
                }

            int oldRing = ring.Count;

            for (int i = had; i < n; i++)
                if (Rect(buttons, i) != null)
                {
                    _extraIndex = i;
                    ring.Insert(at++, i);
                }

            RingOrder.AddRange(ring);

            MeasureLines(buttons, Rect(buttons, ring[0]).parent, oldRing, sb);

            sb.Append($"\n  环已量好：圆心 {_center}，半径 {_cellRadius:0.0}，"
                      + $"新格插在顺时针第 {RingOrder.IndexOf(_extraIndex)} 位（正下方）");

            // 先按原版那一档摆一次，并把第七格收起来。
            // _OnInit 在界面构造时就跑，而 _OnUpdate 要等窗口真被打开——中间那段时间里，
            // 克隆出来的第七格本来是贴在第 0 格上的（CloneUnder 照抄了源的可见性）。
            // 玩家看不到，但「第一帧状态未定义」不该留着：默认值就该是原版的样子。
            foreach (Transform t in ExtraRoots)
                if (t != null)
                    t.gameObject.SetActive(false);

            ApplyRing(w, RingOrder.Count - 1);
        }

        /// <summary>
        /// 按当前环上格数重排：<b>5 格摆成正五边形，6 格摆成正六边形。</b>装饰线一并跟上。
        ///
        /// <b>5 格那一档精确还原原版</b>——环上原有五格的顺时针次序没变，起始角还是 90°，
        /// 步长 72°，所以 <c>90 − 72k</c> 把它们逐个放回本来的位置。
        ///
        /// 只在「第七格显示与否」之间切，<b>不跟着解锁状态走</b>：原版 <c>_OnUpdate</c> IL 00D9
        /// 会按 <c>RecipeUnlocked</c> 隐藏还没解锁的矩阵，前期环上本来就是缺口，
        /// 那是原版行为，不该被这里顺手改掉。
        /// </summary>
        private static void ApplyRing(UILabWindow w, int ring)
        {
            UIButton[] buttons = w.itemButtons;

            if (buttons == null || ring < 3 || RingOrder.Count == 0) return;

            bool full = ring >= RingOrder.Count;
            float step = 360f / ring;
            var k = 0;

            foreach (int idx in RingOrder)
            {
                // 只摆到 ring 个为止；被跳过的必然是新加的那个（它排在正下方）
                if (!full && idx == _extraIndex) continue;

                RectTransform t = Rect(buttons, idx);

                if (t == null) continue;

                float rad = (90f - step * k) * Mathf.Deg2Rad;

                t.anchoredPosition = _center + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * _cellRadius;
                k++;
            }

            foreach (LineGroup g in LineGroups) ApplyLines(g, ring);

            _appliedRing = ring;

            float gap = 2f * _cellRadius * Mathf.Sin(step * 0.5f * Mathf.Deg2Rad);
            float width = Rect(buttons, RingOrder[0])?.sizeDelta.x ?? 0f;

            // 每种格数只报一次：这条是随模式切换触发的，来回切几次就会刷屏
            if ((_loggedRings & (1 << ring)) != 0) return;

            _loggedRings |= 1 << ring;

            ProjectEdenPlugin.Log.LogInfo(
                $"矩阵实验室窗口：环上 {ring} 格 → 正 {ring} 边形，每格 {step:0.0}°，"
                + $"相邻中心距 {gap:0.0} px，控件宽 {width:0.0} px"
                + (gap < width ? "　**会叠**" : $"（留 {gap - width:0.0} px 缝）"));
        }

        /// <summary>
        /// 量出那圈装饰线的分组与几何，并给每组补一根备用的。
        ///
        /// <b>这些线是点名点出来的，不是猜的。</b> <c>matrix</c> 下除了七个格子还有个
        /// <c>lines</c> 节点（自己就在环心），底下 10 根线分成两组、每组 5 根——
        /// 正好是原版环上格子数：
        ///
        /// <code>
        /// 辐条  尺寸 2×9.6   半径 50     角度 90+72k    旋转 0+72k    （对准每个格子）
        /// 短划  尺寸 28×2    半径 80.7   角度 126+72k   旋转 36+72k   （每条边的中点）
        /// </code>
        ///
        /// 两组都满足 <c>旋转 = 角度 − 90</c>，而 <c>80.7 ≈ 100·cos(36°)</c>
        /// 正是五边形边中点的半径。于是规则可以全部反推出来，一个数都不用写死。
        ///
        /// <b>隐藏的那组也要管。</b> 点名是在合成模式下取的，辐条当时是隐藏的；
        /// 但科研模式很可能把它们点亮，到那时错位的就轮到它们了。
        /// 「现在看不见」不是「可以不管」。
        /// </summary>
        private static void MeasureLines(UIButton[] buttons, Transform parent, int oldRing, StringBuilder sb)
        {
            if (parent == null || oldRing < 3) return;

            float oldStep = 360f / oldRing;

            for (var i = 0; i < parent.childCount; i++)
            {
                Transform node = parent.GetChild(i);

                if (IsSlot(buttons, node)) continue;

                // 按尺寸分组：一组的根数应当等于原版环上的格子数
                var groups = new List<List<RectTransform>>();

                for (var k = 0; k < node.childCount; k++)
                {
                    var line = node.GetChild(k) as RectTransform;

                    if (line == null) continue;

                    List<RectTransform> g = null;

                    foreach (List<RectTransform> c in groups)
                        if ((c[0].sizeDelta - line.sizeDelta).sqrMagnitude < 0.01f)
                        {
                            g = c;

                            break;
                        }

                    if (g == null) groups.Add(g = new List<RectTransform>());

                    g.Add(line);
                }

                foreach (List<RectTransform> g in groups)
                {
                    if (g.Count != oldRing) continue;

                    var group = new LineGroup { Step = oldStep, Phase = Phase(g, oldStep) };

                    foreach (RectTransform t in g) group.Radius += t.anchoredPosition.magnitude;

                    group.Radius /= g.Count;
                    group.Members.AddRange(g);

                    // 补一根备用的，切到六边形才够用；平时由 ApplyLines 关着
                    RectTransform first = g[0];
                    GameObject clone = Object.Instantiate(first.gameObject, first.parent, false);
                    clone.name = "projecteden-" + first.name;

                    var rt = clone.transform as RectTransform;

                    if (rt != null)
                    {
                        rt.sizeDelta = first.sizeDelta;
                        group.Members.Add(rt);
                    }

                    LineGroups.Add(group);

                    sb.Append($"\n  装饰线一组 {first.sizeDelta}：原始 {g.Count} 根，半径 {group.Radius:0.0}，"
                              + $"相位 {group.Phase * oldStep:0.0}°（已补 1 根备用）");
                }
            }
        }

        /// <summary>把一组装饰线摆成 <paramref name="ring"/> 边形。</summary>
        private static void ApplyLines(LineGroup g, int ring)
        {
            if (g.Members.Count == 0) return;

            float step = 360f / ring;

            // 边中点那组的半径跟着边数走（R·cos(半步长)）；辐条那组相位 0，自然不变
            float radius = g.Radius
                           * Mathf.Cos(step * g.Phase * Mathf.Deg2Rad)
                           / Mathf.Cos(g.Step * g.Phase * Mathf.Deg2Rad);

            int last = g.Members.Count - 1; // 我们补的那根

            for (var k = 0; k < g.Members.Count; k++)
            {
                RectTransform t = g.Members[k];

                if (t == null) continue;

                if (k >= ring)
                {
                    // 多出来的只会是我们补的那根；原版那几根的显隐留给原版自己管
                    if (k == last && t.gameObject.activeSelf) t.gameObject.SetActive(false);

                    continue;
                }

                float deg = 90f + step * (k + g.Phase);
                float rad = deg * Mathf.Deg2Rad;

                t.anchoredPosition = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * radius;
                t.localEulerAngles = new Vector3(0f, 0f, deg - 90f);

                // 我们补的那根照抄同组第一根的显隐，免得凭空多出一根原版不想显示的线
                if (k == last && g.Members[0] != null
                              && t.gameObject.activeSelf != g.Members[0].gameObject.activeSelf)
                    t.gameObject.SetActive(g.Members[0].gameObject.activeSelf);
            }
        }

        private static bool IsSlot(UIButton[] buttons, Transform node)
        {
            foreach (UIButton b in buttons)
                if (b != null && b.transform == node)
                    return true;

            return false;
        }

        /// <summary>
        /// 一组线相对正上方的相位，单位是「几分之一步长」。辐条是 0，边中点那组是 ±0.5。
        ///
        /// <b>不能只取第一根、也不能直接 <c>Mathf.Repeat(x, 1)</c>。</b>
        /// 第一根辐条在 (0, 50)，<c>Atan2</c> 给的是 89.99999 而不是 90，于是
        /// <c>(角度 − 90) / 步长</c> 是个极小的<b>负数</b>，<c>Repeat</c> 把它卷到 ≈1.0——
        /// 相位 0 被当成了相位 1，半径于是按 <c>cos(60°)/cos(72°)</c> 算，50 被拉到 80.9。
        /// 日志里 <c>半径 50.0 → 80.9，相位 60.0°</c> 就是这么来的。
        ///
        /// 相位本身是个<b>圆上的量</b>（模 1），所以取平均也得在圆上取：
        /// 把每根的相位映到单位圆上求和再取角度。这样既不怕 0/1 边界，
        /// 又把全组的测量噪声一起平掉，比取任何单独一根都稳。
        /// </summary>
        private static float Phase(List<RectTransform> g, float oldStep)
        {
            var sx = 0f;
            var sy = 0f;

            foreach (RectTransform t in g)
            {
                float turns = (Angle(t.anchoredPosition) - 90f) / oldStep; // 单位：步长
                float rad = turns * 2f * Mathf.PI;

                sx += Mathf.Cos(rad);
                sy += Mathf.Sin(rad);
            }

            return Mathf.Atan2(sy, sx) / (2f * Mathf.PI); // 落在 [-0.5, 0.5]
        }

        /// <summary>向量的极角（度）。</summary>
        private static float Angle(Vector2 v) => Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;

        private static RectTransform Rect(UIButton[] buttons, int i)
            => i >= 0 && i < buttons.Length ? buttons[i]?.transform as RectTransform : null;

        /// <summary>
        /// <b>合成模式下把第七格收起来。</b>
        ///
        /// 这七个格子在合成模式下有两种含义，都不该轮到生物矩阵：
        ///
        /// <list type="number">
        /// <item><b>已选配方时它们是投料格</b>（<c>recipeExecuteData.requires</c> 的下标）。
        /// 矩阵配方只有两三样原料，所以第七格落在
        /// <c>if (requires.Length &lt;= index) return;</c>（IL 0380–0387）上，静默返回——
        /// 这正是「点了没反应」的由来。</item>
        ///
        /// <item><b>未选配方时它们是「造哪种矩阵」的选择钮</b>，点下去就是
        /// <c>SetFunction(false, matrixRecipes[index].ID, …)</c>（IL 063F–067A）。
        /// 而 <c>matrixRecipes[6]</c> 是 6644，也就是<b>生物温室</b>那条 <c>ERecipeType 11</c> 的配方，
        /// 并且 <c>SetFunction</c> 和 <c>AssemblerComponent.SetRecipe</c> 一样<b>根本不校验配方类型</b>。
        /// 也就是说这一格一旦点得动，就是让矩阵研究站去跑生物温室的配方——
        /// 绕开了「生物矩阵只能养出来」这条机制本身。</item>
        /// </list>
        ///
        /// <b>现在挡着它的是解锁状态，那不算数。</b> <c>_OnUpdate</c> IL 00D9 的
        /// <c>RecipeUnlocked(matrixRecipes[i].ID)</c> 驱动 <c>SetActive</c>，所以眼下那格是暗的；
        /// 但锁是会开的，开了这个口子就露出来了。所以按<b>模式</b>收，不靠锁。
        ///
        /// 科研模式下这一格必须在——<c>matrixServed[6]</c> 就是生物矩阵的科研投料格，
        /// 那 32 个科技全指着它。
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UILabWindow), "_OnUpdate")]
        private static void UILabWindow_OnUpdate(UILabWindow __instance)
        {
            if (!BioMatrixPatches.Ready || ExtraRoots.Count == 0 || RingOrder.Count == 0) return;

            bool show = ShouldShow(__instance);

            for (var i = 0; i < ExtraRoots.Count; i++)
            {
                Transform t = ExtraRoots[i];

                // 每帧都会进来，所以先比再写，别无谓地翻 SetActive
                if (t != null && t.gameObject.activeSelf != show) t.gameObject.SetActive(show);
            }

            // 环上该有几格：显示第七格就是 6，否则 5。只有变化时才重摆。
            int ring = show ? RingOrder.Count : RingOrder.Count - 1;

            if (ring != _appliedRing) ApplyRing(__instance, ring);

            if (_hideLogged || show) return;

            _hideLogged = true;

            ProjectEdenPlugin.Log.LogInfo(
                "矩阵实验室窗口：这一刻收起了第七格。三种状态三个答案——科研模式显示（它是生物矩阵的"
                + "科研投料格）；合成模式已选配方时按配方原料数决定（宇宙矩阵七样原料，第七格就是"
                + "生物矩阵那一格，会显示）；合成模式未选配方时收起（那时格子是「造哪种矩阵」的选择钮，"
                + "而 matrixRecipes[6] 是生物温室的配方，SetFunction 又不校验类型）。");
        }

        /// <summary>
        /// 第七格该不该显示。<b>三种状态，三个答案。</b>
        ///
        /// <list type="bullet">
        /// <item><b>科研模式 → 显示。</b> 它是生物矩阵的科研投料格，
        /// <c>matrixServed[6]</c>，那 32 个科技全指着它。</item>
        ///
        /// <item><b>合成模式 + 已选配方 → 看配方有几样原料。</b>
        /// 这时七个格子是<c>recipeExecuteData.requires</c> 的投料格；
        /// 宇宙矩阵现在是七样原料，第七格就是生物矩阵那一格，<b>必须显示</b>。
        /// 别的矩阵只有两三样原料，第七格没有对应原料，收起来。</item>
        ///
        /// <item><b>合成模式 + 未选配方 → 收起。</b> 这时七个格子是「造哪种矩阵」的选择钮，
        /// 而 <c>matrixRecipes[6]</c> 指向生物温室那条 <c>ERecipeType 11</c> 的配方，
        /// <c>SetFunction</c> 又不校验配方类型——点得动就等于让研究站去跑温室的配方。</item>
        /// </list>
        ///
        /// <b>头一版只分了「科研 / 非科研」两种，于是把第二种也一起收了</b>，
        /// 结果生产宇宙矩阵时生物矩阵那一格根本不显示。那条规则是在加配方<b>之前</b>写的，
        /// 当时合成模式下第七格确实没有任何正当用途；配方一改，前提就变了。
        /// </summary>
        private static bool ShouldShow(UILabWindow w)
        {
            int id = w.labId;
            LabComponent[] pool = w.factorySystem?.labPool;

            if (id <= 0 || pool == null || id >= pool.Length) return false;

            LabComponent lab = pool[id];

            if (lab.id != id) return false;
            // 科研模式：只有真的有科技要它才画。默认配置下没有任何科技直接要生物矩阵，
            // 那一格就永远不会被消耗，画出来只会让人以为该往里投料。
            if (lab.researchMode) return BioMatrixPatches.UsedByTechs;

            // 未选配方：这时格子是选择钮，不是投料格
            if (lab.recipeId <= 0) return false;

            int[] requires = lab.recipeExecuteData?.requires;

            return requires != null && BioMatrixPatches.Slot < requires.Length;
        }

        /// <summary>
        /// 一次性点名：矩阵环那一圈里，除了七格自己的控件，还有些什么。
        ///
        /// <b>因为屏幕上多出来一根连不到任何东西的短线。</b> 它不是克隆出来的——
        /// 克隆的八个控件都随根节点一起关掉了——所以多半是 prefab 里
        /// <b>按五边形画死的装饰线</b>：环上五格被挪到了六边形位置，线没跟着动。
        ///
        /// 但「多半是」不是证据，而 prefab 在 <c>resources.assets</c> 里，离线读不到。
        /// 本仓库为这种情况定过规矩：<b>当问题变成「这到底是哪个对象」，就去枚举对象</b>
        /// （<see cref="MultiProductUIPatches"/> 的 DumpBox 那次）。所以这里不猜，先点名。
        ///
        /// 位置、尺寸、<b>旋转</b>都要打——一根连接线是靠旋转和长度架在两点之间的，
        /// 只看 anchoredPosition 认不出它连的是谁。
        /// </summary>
        private static void DumpRing(UILabWindow w, Transform parent)
        {
            var mine = new HashSet<Transform>();

            Collect(mine, w.itemButtons);
            Collect(mine, w.itemIcons);
            Collect(mine, w.itemPercents);
            Collect(mine, w.itemCountTexts);
            Collect(mine, w.itemLocks);
            Collect(mine, w.itemIncs);

            var sb = new StringBuilder(
                $"── 矩阵环点名（只打一次）：父节点 {parent.name}，共 {parent.childCount} 个直接子节点 ──");

            for (var i = 0; i < parent.childCount; i++)
            {
                Transform t = parent.GetChild(i);
                var rt = t as RectTransform;

                // 七格自己的控件只报一行，不展开——要找的是「除它们之外还有什么」
                if (mine.Contains(t))
                {
                    sb.Append($"\n  [{i}] {t.name}  ←（七格控件）");

                    continue;
                }

                sb.Append($"\n  [{i}] {t.name}  {Kinds(t)}"
                          + (rt == null
                              ? "  （非 RectTransform）"
                              : $"  pos={rt.anchoredPosition} 尺寸={rt.sizeDelta} 旋转={t.localEulerAngles.z:0.0}°")
                          + $"  显示={t.gameObject.activeSelf}");

                // 线很可能嵌在一层壳里，往下再看一层
                for (var k = 0; k < t.childCount && k < 12; k++)
                {
                    Transform c = t.GetChild(k);
                    var crt = c as RectTransform;

                    if (mine.Contains(c)) continue;

                    sb.Append($"\n        └ {c.name}  {Kinds(c)}"
                              + (crt == null
                                  ? ""
                                  : $"  pos={crt.anchoredPosition} 尺寸={crt.sizeDelta} 旋转={c.localEulerAngles.z:0.0}°")
                              + $"  显示={c.gameObject.activeSelf}");
                }
            }

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }

        private static void Collect<T>(HashSet<Transform> into, T[] array) where T : Component
        {
            if (array == null) return;

            foreach (T c in array)
            {
                if (c == null) continue;

                // 控件本身，以及它到父节点之间的每一层，都算「七格自己的」
                for (Transform t = c.transform; t != null; t = t.parent) into.Add(t);
            }
        }

        /// <summary>这个节点上挂着哪些看得见的组件。</summary>
        private static string Kinds(Transform t)
        {
            var sb = new StringBuilder();

            foreach (Component c in t.GetComponents<Component>())
            {
                if (c == null || c is RectTransform) continue;

                if (sb.Length > 0) sb.Append('+');

                sb.Append(c.GetType().Name);
            }

            return sb.Length == 0 ? "(无组件)" : sb.ToString();
        }

        /// <summary>从正上方起算的顺时针角度（0–360），排序用。</summary>
        private static float Clockwise(RectTransform t, Vector2 center)
        {
            if (t == null) return 0f;

            Vector2 d = t.anchoredPosition - center;
            float deg = 90f - Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;

            while (deg < 0f) deg += 360f;
            while (deg >= 360f) deg -= 360f;

            return deg;
        }
    }
}
