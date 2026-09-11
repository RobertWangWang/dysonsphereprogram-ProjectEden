using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ProjectEden.Compatibility
{
    /// <summary>
    /// 让本 mod 新增的矿脉出现在「矿脉分布」的常驻标签里。
    ///
    /// <b>拦住它的是银河尺度，不是原版。</b> GS2 的 PatchOnUIVeinDetail.SetInspectPlanet 是一个
    /// <b>返回 false 的前置</b>——它整个替换掉了原版 UIVeinDetail.SetInspectPlanet，
    /// 替换实现里多了一层按矿种的开关：
    /// <code>
    ///   var veinTips = GS2.Config.VeinTips;                       // Dictionary&lt;int, bool&gt;
    ///   for (i = 1; i &lt; factory.veinGroups.Length; i++) {
    ///       var type = factory.veinGroups[i].type;
    ///       bool show = veinTips.ContainsKey(type) ? veinTips[type] : false;   // 查不到 = false
    ///       if (show) CreateOrOpenATip(factory, i);
    ///   }
    /// </code>
    /// 新矿种不在它的字典里，于是<b>连节点都不会创建</b>。
    ///
    /// 这解释了之前所有对不上的现象：鼠标悬停能显示（cursorNode 是另一条路，没有这层过滤）、
    /// 簇里<b>只</b>缺新矿种那些、而原版 DoVeinGroupsRecalculate / CreateOrOpenATip
    /// 从头到尾都没有按矿种的过滤——因为过滤根本不在原版代码里。
    ///
    /// 修法就是把新矿种的开关塞进那个字典。挂在 UIVeinDetail.SetInspectPlanet 的前置上、
    /// 优先级拉到最高，保证在 GS2 那个替换前置之前把键补好；GS2 的配置是在插件加载之后
    /// 才从磁盘读的，所以不能在 Awake 里一次性写完。
    /// </summary>
    internal static class OreVeinTipCompat
    {
        private static FieldInfo _configField;
        private static MethodInfo _veinTipsGetter;
        private static bool _resolved;
        private static bool _logged;

        internal static void ApplyPatches(Harmony harmony)
        {
            if (harmony == null || !GalacticScaleCompat.Installed || !OreRegistry.Enabled) return;

            MethodInfo target = AccessTools.Method(typeof(UIVeinDetail), nameof(UIVeinDetail.SetInspectPlanet));

            if (target == null)
            {
                ProjectEdenPlugin.Log.LogWarning("找不到 UIVeinDetail.SetInspectPlanet，新矿脉不会出现在矿脉分布标签里");

                return;
            }

            var prefix = new HarmonyMethod(AccessTools.Method(typeof(OreVeinTipCompat), nameof(EnsureVeinTip)))
            {
                // 必须排在 GS2 那个「返回 false」的前置之前，否则它已经按旧字典跑完了
                priority = Priority.First,
            };

            harmony.Patch(target, prefix: prefix);

            ProjectEdenPlugin.Log.LogInfo("已接管银河尺度的矿脉标签开关：新矿脉会被加进 GS2.Config.VeinTips");
        }

        public static void EnsureVeinTip()
        {
            if (!_resolved)
            {
                _resolved = true;

                System.Type gs2 = AccessTools.TypeByName("GalacticScale.GS2");
                System.Type settings = AccessTools.TypeByName("GalacticScale.GS2MainSettings");

                _configField = gs2 == null ? null : AccessTools.Field(gs2, "Config");
                _veinTipsGetter = settings == null ? null : AccessTools.PropertyGetter(settings, "VeinTips");

                if (_configField == null || _veinTipsGetter == null)
                    ProjectEdenPlugin.Log.LogWarning("解析不到 GS2.Config.VeinTips，新矿脉的常驻标签开关没能补上");
            }

            if (_configField == null || _veinTipsGetter == null) return;

            object config = _configField.GetValue(null);

            if (config == null) return;

            if (!(_veinTipsGetter.Invoke(config, null) is Dictionary<int, bool> tips)) return;

            var added = 0;

            foreach (OreRegistry.Ore ore in OreRegistry.Ores)
            {
                if (tips.TryGetValue(ore.VeinId, out bool on) && on) continue;

                tips[ore.VeinId] = true;
                added++;
            }

            if (added == 0 || _logged) return;

            _logged = true;

            ProjectEdenPlugin.Log.LogInfo(
                $"已把 {added} 个自定义矿种加进 GS2 的矿脉标签开关（原本不在字典里，所以一直不建标签节点）");
        }
    }
}
