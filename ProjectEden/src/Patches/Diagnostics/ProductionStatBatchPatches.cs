using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches.Diagnostics
{
    [HarmonyPatch]
    internal static class ProductionStatBatchPatches
    {
        [ThreadStatic] private static ProductionStatBuffer _buffer;
        private static long _calls, _arrays, _items, _ticks;

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(GameLogic), "_assembler_parallel");
            yield return AccessTools.Method(typeof(GameLogic), "_lab_produce_parallel");
        }

        [HarmonyPrefix]
        private static void Before(out bool __state)
        {
            __state = false;
            if (CpuCostProbe.Config?.batchProductionStatistics == false) return;
            if (_buffer == null) _buffer = new ProductionStatBuffer();
            __state = _buffer.Begin();
        }

        internal static int[] Redirect(int[] target) => _buffer?.Redirect(target) ?? target;

        [HarmonyFinalizer]
        private static void After(bool __state)
        {
            if (!__state) return;
            bool measure = CpuCostProbe.Config?.systemTiming == true;
            long start = measure ? Stopwatch.GetTimestamp() : 0;
            // 正常返回和异常路径都提交已发生的统计；不吞掉游戏异常。
            _buffer.Flush(out int arrays, out int items);
            if (!measure) return;
            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _arrays, arrays);
            Interlocked.Add(ref _items, items);
            Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - start);
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var codes = new List<CodeInstruction>(instructions);
            FieldInfo product = AccessTools.Field(typeof(FactoryProductionStat), "productRegister");
            FieldInfo consume = AccessTools.Field(typeof(FactoryProductionStat), "consumeRegister");
            int products = 0, consumes = 0;
            foreach (CodeInstruction code in codes)
            {
                if (code.LoadsField(product)) products++;
                if (code.LoadsField(consume)) consumes++;
            }
            // 游戏升级或其他转译器改变入口时整条回退，不做部分替换。
            if (products != 1 || consumes != 1)
            {
                ProjectEdenPlugin.Log.LogWarning($"生产统计批合并：{__originalMethod.Name} 入口不匹配（{products}/{consumes}），保持共享统计原路径。");
                return codes;
            }
            var result = new List<CodeInstruction>();
            foreach (CodeInstruction code in codes)
            {
                result.Add(code);
                if (code.LoadsField(product) || code.LoadsField(consume))
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ProductionStatBatchPatches), nameof(Redirect))));
            }
            ProjectEdenPlugin.Log.LogInfo($"生产统计批合并：{__originalMethod.Name} 已接管生产/消耗数组，工作线程返回前合并；配方结算不变。");
            return result;
        }

        internal static void Report()
        {
            long calls = Interlocked.Exchange(ref _calls, 0);
            long arrays = Interlocked.Exchange(ref _arrays, 0);
            long items = Interlocked.Exchange(ref _items, 0);
            long ticks = Interlocked.Exchange(ref _ticks, 0);
            ProjectEdenPlugin.Log.LogInfo($"[生产统计批合并] 开关={CpuCostProbe.Config?.batchProductionStatistics != false} 调用={calls} 共享锁合并次数={arrays} 非零项={items} 合并累计ns={NanosecondProbe.Ns(ticks)}（包含私有数组扫描及锁等待，线程累计，不能视为单帧耗时）。");
        }
    }
}
