using System.Collections.Generic;
using BepInEx.Logging;
using Mono.Cecil;

namespace ProjectEden.Preloader
{
    /// <summary>
    /// BepInEx 的 patcher 入口：CLR 装载游戏程序集<b>之前</b>跑，拿到的是 Cecil 的元数据视图。
    ///
    /// 整个接口就两个成员（<see cref="TargetDLLs"/> + <see cref="Patch"/>），BepInEx 靠签名发现，
    /// 所以这个类不需要继承任何东西，也没有 <c>[BepInPlugin]</c>。
    ///
    /// <b>这里的日志比平时更重要。</b> preloader 跑的时候没有 <c>LDB</c>、没有游戏状态、没有活类型，
    /// 本仓库平时那套排查手段（读 IL、报匹配数、匹配为零就响）一样都用不上；
    /// 出错的典型表现是游戏根本起不来，报一个指不到你代码的 CLR 类型加载异常。
    /// 所以无论成功失败都把计数打全，失败时把每一条 Blocker 单独打出来。
    /// </summary>
    public static class Patcher
    {
        private static readonly ManualLogSource Log =
            Logger.CreateLogSource("ProjectEden.Preloader");

        public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

        public static void Patch(AssemblyDefinition assembly)
        {
            CargoIncWidener.Report r = CargoIncWidener.Apply(assembly.MainModule);

            foreach (string n in r.Notes) Log.LogInfo(n);

            if (!r.Applied)
            {
                Log.LogError(
                    $"Cargo.inc 加宽**未执行**，共 {r.Blockers.Count} 条阻塞项（程序集保持原样，游戏照常跑）：");

                foreach (string b in r.Blockers) Log.LogError("  " + b);

                Log.LogError(
                    "传送带集装超过 63 层时，增产点数仍然只能按一个字节结算——" +
                    "运行时的 CargoIncClampPatches 会把它夹在 255，是确定的降级，不是失真。");

                return;
            }

            if (r.Propagated.Count > 0)
                Log.LogInfo(
                    $"数据流传播另外认出 {r.Propagated.Count} 个字节参数（名字上看不出来）：" +
                    string.Join("、", r.Propagated.ToArray()));

            Log.LogWarning(
                $"Cargo.inc 已加宽为 Int16：签名 {r.WidenedParams} 处、局部变量 {r.WidenedLocals} 个、" +
                $"间接读写 {r.FixedIndirect} 处、截断指令 {r.FixedConv} 处，覆盖 {r.CallSites} 个调用点。" +
                "存档从此绑定本 mod。");

            AddQualityFields(assembly);
        }

        /// <summary>
        /// 物品品质 · 阶段 1a：给 30 个载荷字段各加一个 Int32 孪生。
        ///
        /// <b>和 Cargo.inc 加宽互相独立</b>——它只加字段、不碰任何方法体，所以
        /// 前面那一步失败与否都不影响它的正确性。之所以仍然排在后面跑，是因为加宽
        /// 会对几个方法做 SimplifyMacros/OptimizeMacros，让它在一个稳定的状态上落子更好排查。
        ///
        /// 此刻这些字段恒为 0 / null，<b>游戏行为零变化</b>；写它们的代码在 1b / 1c。
        /// 失败不影响游戏启动：什么都不加，品质功能就当不存在。
        /// </summary>
        private static void AddQualityFields(AssemblyDefinition assembly)
        {
            QualityFieldAdder.Report q = QualityFieldAdder.Apply(assembly.MainModule);

            foreach (string n in q.Notes) Log.LogInfo(n);

            if (!q.Applied)
            {
                Log.LogError($"物品品质：孪生字段**未添加**，共 {q.Blockers.Count} 条阻塞项（程序集这部分保持原样）：");

                foreach (string b in q.Blockers) Log.LogError("  " + b);

                return;
            }

            Log.LogWarning($"物品品质：已添加 {q.Added.Count} 个孪生字段（阶段 1a）。");

            BuildQualityChannel(assembly);
        }

        /// <summary>
        /// 物品品质 · 阶段 1b：合成线程静态寄存器组，让品质能跨方法边界传递而<b>不动任何签名</b>。
        ///
        /// 失败不影响游戏：没有寄存器，品质就只能停在各自的字段里，其余一切照常。
        /// </summary>
        private static void BuildQualityChannel(AssemblyDefinition assembly)
        {
            QualityChannelBuilder.Report c = QualityChannelBuilder.Apply(assembly.MainModule);

            foreach (string n in c.Notes) Log.LogInfo(n);

            if (!c.Applied)
            {
                Log.LogError($"物品品质：侧信道**未合成**，共 {c.Blockers.Count} 条阻塞项：");

                foreach (string b in c.Blockers) Log.LogError("  " + b);

                return;
            }

            Log.LogInfo($"物品品质：侧信道已就位（{c.Registers} 个线程静态寄存器）。");

            RewriteQualityFlow(assembly);
        }

        /// <summary>
        /// 阶段 1c：把主干道上每一处载荷访问都配一条<b>孪生语句</b>，品质从此真的会流动。
        ///
        /// <b>它要么全做要么不做。</b> 变换先只分析一遍，只要还有一种语句形状没有发射器,
        /// 它就什么都不改并把缺口报出来——半改一半的 Assembly-CSharp 是救不回来的,
        /// 而「品质在某几条路径上悄悄变成 0」比「品质恒为 0」难查得多。
        ///
        /// 所以这里**不因为失败而中止启动**：前面三步（加宽、孪生字段、侧信道）都已完成,
        /// 品质只是不流动，游戏行为和 1b 那一版一致。
        /// </summary>
        private static void RewriteQualityFlow(AssemblyDefinition assembly)
        {
            QualityTransform.Report q = QualityTransform.Apply(assembly.MainModule);

            foreach (string n in q.Notes) Log.LogInfo(n);

            if (q.Blockers.Count > 0 || q.Unhandled.Count > 0 || q.Pending.Count > 0)
            {
                Log.LogError(
                    $"物品品质：**品质流动未启用**——阻塞 {q.Blockers.Count} 条、" +
                    $"未识别形状 {q.Unhandled.Count} 种、认得但没发射 {q.Pending.Count} 种。" +
                    "字段和侧信道仍在，品质恒为 0，游戏行为与不加时一致。");

                foreach (string b in q.Blockers) Log.LogError("  " + b);

                foreach (string k in q.Unhandled.Keys) Log.LogError($"  未识别形状：{k}");

                foreach (string k in q.Pending.Keys) Log.LogError($"  没有发射器：{k}");

                return;
            }

            Log.LogWarning(
                $"物品品质：品质流动已启用——{q.Twinned} 条孪生语句、{q.TwinLocals} 个孪生局部、" +
                $"{q.ChannelUses} 处走侧信道，涉及 {q.Methods} 个方法体。" +
                $"品质**还没进存档**（{q.SaveSkipped} 处），读档归零；" +
                $"另有 {q.Dropped} 处明确丢弃。");
        }
    }
}
