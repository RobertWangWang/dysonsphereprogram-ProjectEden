using System.Collections.Generic;
using System.Text;
using System.Threading;
using HarmonyLib;
using ProjectEden.Utils;
using UnityEngine;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 普查：此刻这颗星球上<b>到底在画哪些模型、各画了多少个实例</b>。
    ///
    /// <b>为什么需要它。</b> 「多台小型速采机叠在一起会刺眼」这个问题已经查了八轮，
    /// 前七轮全是「猜一个可能的成因 → 压它 → 没用」。而每一次「压了、生效了、没用」
    /// 都说明<b>压的东西不在那条渲染路径上</b>——问题从来不是剂量不够，是找错了对象。
    ///
    /// 到目前为止已经用测量排除掉的：建筑自己那四份材质（全部属性归零，玩家确认
    /// 玻璃不反光了）、矿脉的开采显示模型（底座与圆环整套关掉）、
    /// <c>VeinProto.MiningEffect</c>（只有玩家手动采集和地形改造读它）、
    /// <c>_miningFlag</c> / <c>_veinMiningFlag</c>（位掩码，不是计数，消费者只有
    /// 玩家手动采集和两个成就）、以及 <c>MinerComponent.InternalUpdate</c> 本身
    /// （全部调用列出来看过，一个渲染或特效调用都没有）。
    ///
    /// <b>所以停止猜测，改为枚举。</b> 玩家说「叠得越多越刺眼」——那么画那片光的模型，
    /// 它的实例数必然随台数一起涨。把每个模型的实例数排出来，那一行自己会跳出来，
    /// 而且顺带给出它的着色器，直接就能填进 <c>materialScales</c>。
    ///
    /// 这是本仓库的老规矩用在渲染上：<b>当一个界面/画面「不可能这样」时，
    /// 先把对象枚举出来，别再读一遍你以为的那条路。</b>
    /// （<c>MultiProductUIPatches</c> 的 <c>DumpBox</c> 就是这么一次把两个悬案一起结掉的。）
    /// </summary>
    [HarmonyPatch]
    internal static class ModelRenderCensus
    {
        private static AdvancedMinerConfig Config => ProjectEdenPlugin.MinerConfig;

        private static int _entered;

        private static float _next;

        private static int _runs;

        /// <summary>报告过的最大实例数，用来只在「又长了」时再报一次</summary>
        private static int _peak;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UIGame), "_OnUpdate")]
        private static void UIGame_OnUpdate()
        {
            if (Config == null || !Config.renderCensus) return;

            // 入口无条件报一行。这个仓库为「探针静默」付过不止一次账：
            // 没有输出时，「开关没开」「补丁没挂上」「条件没满足」长得一模一样
            if (Interlocked.Exchange(ref _entered, 1) == 0)
                ProjectEdenPlugin.Log.LogInfo(
                    "模型普查：UIGame._OnUpdate 的后置已跑到（补丁确实挂上了）。" +
                    "站到那片速采机旁边，10 秒后开始逐次普查，只在实例数创新高时打印。");

            float now = Time.realtimeSinceStartup;

            if (now < _next) return;

            _next = now + 10f;

            Run();
        }

        private static void Run()
        {
            GPUInstancingManager gpui = GameMain.gpuiManager;

            if (gpui?.objectRenderers == null)
            {
                ProjectEdenPlugin.Log.LogWarning("模型普查：gpuiManager 或 objectRenderers 为空，这一次跳过");
                return;
            }

            var rows = new List<KeyValuePair<int, int>>();
            var top = 0;

            for (var i = 0; i < gpui.objectRenderers.Length; i++)
            {
                ObjectRenderer renderer = gpui.objectRenderers[i];

                if (renderer == null) continue;

                // instCursor 是实例池的游标，也就是「这个模型现在画了几个」
                int count = renderer.instCursor;

                if (count <= 0) continue;

                rows.Add(new KeyValuePair<int, int>(i, count));

                if (count > top) top = count;
            }

            _runs++;

            // 只在创新高时打印：玩家要走到那片速采机旁边，而每 10 秒刷一大段日志
            // 会把有用的那一次淹掉
            if (top <= _peak && _runs > 1) return;

            _peak = top;

            rows.Sort((a, b) => b.Value.CompareTo(a.Value));

            var sb = new StringBuilder();

            sb.Append($"模型普查（第 {_runs} 次，实例数创新高 {top}）：正在绘制的模型共 {rows.Count} 种，按实例数排序取前 25——");

            var shown = 0;

            foreach (KeyValuePair<int, int> row in rows)
            {
                if (shown++ >= 25) break;

                ModelProto proto = LDB.models.Select(row.Key);
                string shaders = Shaders(proto);

                sb.Append($"\n    模型 {row.Key,-4} × {row.Value,-6} {proto?.Name ?? "（查不到）"}  {shaders}");
            }

            sb.Append("\n  找那一行：画那片光的模型，实例数会跟着速采机台数一起涨。" +
                      "认出来之后把它的着色器属性填进 machines.json 的 materialScales，或者直接不画它。");

            ProjectEdenPlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>把一个模型用到的着色器名列出来——加法混合那种一眼就能认出。</summary>
        private static string Shaders(ModelProto proto)
        {
            if (proto?.prefabDesc?.lodMaterials == null) return "（无材质）";

            var names = new List<string>();

            foreach (Material[] lod in proto.prefabDesc.lodMaterials)
            {
                if (lod == null) continue;

                foreach (Material material in lod)
                {
                    if (material?.shader == null) continue;

                    string n = material.shader.name;

                    if (!names.Contains(n)) names.Add(n);
                }
            }

            return names.Count == 0 ? "（无材质）" : "[" + string.Join(" | ", names.ToArray()) + "]";
        }

        /// <summary>开机状态行，无条件打印。</summary>
        internal static void Report()
        {
            bool on = Config != null && Config.renderCensus;

            ProjectEdenPlugin.Log.LogInfo(
                on
                    ? "模型普查：已开启（advancedminer.json 的 renderCensus）。进游戏后每 10 秒查一次，" +
                      "只在实例数创新高时打印。查完把它关掉。"
                    : "模型普查：未开启（advancedminer.json 的 renderCensus = false）。" +
                      "要弄清「这片画面里到底在画什么」时打开它。");
        }
    }
}
