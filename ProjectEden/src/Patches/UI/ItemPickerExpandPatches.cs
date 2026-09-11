using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using ProjectEden.Utils;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 给「物品选取」窗口（UIItemPicker）加横向翻页。
    ///
    /// <b>为什么需要。</b> 物品格和配方格是两张互不相干的网格，
    /// 而物品格第 1 页（8 行 × 14 列 = 112 格）原版就几乎排满。
    /// <c>UIItemPicker.RefreshIcons</c> 里有一句硬裁：
    /// <code>
    ///   if (row &lt; 0 || col &lt; 0 || row &gt;= 8 || col &gt;= 14) continue;
    /// </code>
    /// 排不进去的物品就<b>彻底不存在</b>——分拣器过滤、物流站槽位、储物箱过滤里都选不到。
    /// 症状是「配方在合成面板里好端端的，物品却哪儿都找不到」，很容易误判成图标没做出来。
    ///
    /// <b>注入点和另外两个窗口完全相同</b>（ReplicatorExpandPatches / RecipePickerExpandPatches）：
    /// 在 <c>col = GridIndex % 100 - 1</c> 之后再减一次列偏移，越界的由原版自己那句判断裁掉。
    /// 命中测试（TestMouseIndex）拿的是鼠标在网格里的<b>可见坐标</b>
    /// （position → index → protoArray[index]），和写入用的是同一套坐标系，一个字都不用改。
    ///
    /// <b>没有物品溢出时这个补丁完全不启用</b>：列页数按 LDB 里实际最大列号算，
    /// 算出来只有一页就不吃滚轮、偏移恒为 0，翻页按钮也不显示。
    ///
    /// <b>翻页的入口不在这里。</b> 早先这个类自己建过一根 12px 的 <c>Scrollbar</c>，
    /// 又细又不起眼，而且是在和 Unity 控件抢「当前页」这个值的所有权——
    /// 建造栏那根滚动条已经为同一个错误付过一次代价了。现在页码和 ◀ ▶ 按钮
    /// 统一放在 <see cref="ItemPickerSearchPatches"/> 那条底部工具条上，
    /// <c>_page</c> 在这里是唯一事实来源，外面只通过 <see cref="SetPage"/> 改它。
    /// </summary>
    [HarmonyPatch]
    internal static class ItemPickerExpandPatches
    {
        /// <summary>原版一页的列数，格位映射与命中测试里写死的那个 14。</summary>
        private const int ColsPerPage = ProtoSlots.VisibleCols;

        private static int _page;
        private static int _maxPage;

        /// <summary>标签页 → 列页数。LDB 建好之后就不再变，算一次存着。</summary>
        private static readonly Dictionary<int, int> PagesByType = new Dictionary<int, int>();

        /// <summary>当前列页（从 0 起）。底部工具条拿它显示页码。</summary>
        public static int Page => _page;

        /// <summary>
        /// 当前列偏移。RefreshIcons 注入的那次减法用的就是它。
        /// <b>搜索时恒为 0</b>：搜索模式下格位是顺序码放的，列偏移没有意义。
        /// （其实搜索时原版 RefreshIcons 整个被前缀跳过，这里走不到；
        /// 留着是因为一个会被别处状态改变含义的公开函数，不该依赖调用方永远不调它。）
        /// </summary>
        public static int ColOffset() => ItemPickerSearchPatches.IsFiltering ? 0 : _page * ColsPerPage;

        /// <summary>这个标签页要几列页。给底部工具条显示「1/3」用。</summary>
        public static int PageCount(int type) => PagesOf(type);

        // ── 一、格位映射加上列偏移 ────────────────────────────

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(UIItemPicker), "RefreshIcons")]
        private static IEnumerable<CodeInstruction> RefreshIcons_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo offset = AccessTools.Method(typeof(ItemPickerExpandPatches), nameof(ColOffset));

            var code = new List<CodeInstruction>(instructions);

            if (offset == null)
            {
                ProjectEdenPlugin.Log.LogError("物品选取横向翻页：ColOffset 没解析出来，补丁未生效");

                return code;
            }

            var count = 0;

            // 找列号那四条：ldc.i4.s 100 / rem / ldc.i4.1 / sub
            // 行号用的是 div 不是 rem，所以这个特征只会命中列
            for (var i = 0; i < code.Count - 3; i++)
            {
                if (!code[i].LoadsConstant(100)) continue;
                if (code[i + 1].opcode != OpCodes.Rem) continue;
                if (!code[i + 2].LoadsConstant(1)) continue;
                if (code[i + 3].opcode != OpCodes.Sub) continue;

                code.InsertRange(i + 4, new[]
                {
                    new CodeInstruction(OpCodes.Call, offset),
                    new CodeInstruction(OpCodes.Sub),
                });

                count++;
                i += 5;
            }

            if (count == 0)
                ProjectEdenPlugin.Log.LogError("物品选取横向翻页：没找到列号的计算（GridIndex % 100 - 1），补丁未生效");
            else
                ProjectEdenPlugin.Log.LogInfo($"物品选取横向翻页：列号计算已接管 {count} 处");

            return code;
        }

        // ── 二、翻页控制 ──────────────────────────────────────

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIItemPicker), "_OnUpdate")]
        private static void UIItemPicker_OnUpdate(UIItemPicker __instance)
        {
            // 搜索时不存在「列页」这回事，连页码都不该动：
            // 清空搜索框之后要能回到原来翻到的那一页
            if (ItemPickerSearchPatches.IsFiltering) return;

            int pages = PagesOf(__instance.currentType);

            // 这一页没有溢出的物品：确保偏移归零
            if (pages <= 1)
            {
                if (_page == 0) return;

                _page = 0;

                __instance.RefreshIcons();

                return;
            }

            _maxPage = pages - 1;

            if (_page > _maxPage)
            {
                _page = _maxPage;

                __instance.RefreshIcons();
            }

            // 鼠标在网格里时滚轮翻页。按钮是给人找得到的，滚轮是给用顺手的
            if (!__instance.mouseInBox) return;

            float wheel = Input.GetAxis("Mouse ScrollWheel");

            if (wheel > 0.01f) SetPage(__instance, _page - 1);
            else if (wheel < -0.01f) SetPage(__instance, _page + 1);
        }

        /// <summary>换标签时回到第一列页，否则会停在一个空页上。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIItemPicker), "OnTypeButtonClick")]
        private static void UIItemPicker_OnTypeButtonClick(UIItemPicker __instance)
        {
            if (_page == 0) return;

            _page = 0;

            __instance.RefreshIcons();
        }

        public static void SetPage(UIItemPicker picker, int page)
        {
            if (picker == null) return;

            _maxPage = PagesOf(picker.currentType) - 1;

            if (_maxPage < 0) _maxPage = 0;

            page = Mathf.Clamp(page, 0, _maxPage);

            if (page == _page) return;

            _page = page;

            picker.RefreshIcons();
        }

        /// <summary>这个标签页要几列页。按 LDB 里该页物品的最大列号算。</summary>
        private static int PagesOf(int type)
        {
            if (PagesByType.TryGetValue(type, out int cached)) return cached;

            var maxCol = 0;

            foreach (ItemProto item in LDB.items.dataArray)
            {
                if (item == null || item.GridIndex < 1101) continue;
                if (item.GridIndex / 1000 != type) continue;

                int col = item.GridIndex % 100;

                if (col > maxCol) maxCol = col;
            }

            int pages = maxCol <= ColsPerPage ? 1 : (maxCol + ColsPerPage - 1) / ColsPerPage;

            PagesByType[type] = pages;

            if (pages > 1)
                ProjectEdenPlugin.Log.LogInfo(
                    $"物品选取：第 {type} 页最大列号 {maxCol}，分成 {pages} 个列页（滚轮或工具条上的 ◀ ▶ 翻页）");

            return pages;
        }
    }
}
