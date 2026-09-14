using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 给 <c>CargoPath.Update</c> 里那个<b>没有下界保护的回扫循环</b>补一个边界检查。
    /// 这是把传送带速度提到原版 5 以上的<b>唯一前置</b>。
    ///
    /// <b>原版长这样</b>（IL_03AF ~ IL_03D6）：
    /// <code>
    /// for (int i = 0; i &lt; limit; i++) {      // limit 正比于传送带速度
    ///     int at = pos - 1 - i;
    ///     if (buffer[at] != 0) break;          // ← at 变成负数就在这里抛 IndexOutOfRange
    ///     free++;
    /// }
    /// </code>
    /// 它从当前位置往回数空位，<b>每单位速度多扫一格</b>。原版极速带 speed 5 只回扫 5 格，
    /// 路径起点前的余量够用，所以永远不触发；把速度提到 20 之后，在短路径的起点附近
    /// 就会扫穿数组头，表现是 <c>FactoryCargoPath</c> 并行阶段抛 <c>IndexOutOfRangeException</c>。
    /// 而且它<b>要跑一会儿才炸</b>（实测第 48476 帧），因为得等某堆货正好走到那个位置。
    ///
    /// <b>改法：把那一条 <c>ldelem.u1</c> 换成 <see cref="ReadOrOccupied"/>。</b>
    /// 取值时栈上正好是 <c>[byte[], int]</c>，和这个静态方法的签名天生吻合，
    /// 所以是<b>一条指令的原地替换</b>，不动任何跳转、不加局部变量。
    ///
    /// 越界时返回<b>非 0</b>——原版这个循环本来就靠「读到非 0」停下，
    /// 而「扫到路径头部之外」的正确语义恰恰就是「前面没有更多空位了」。
    /// 所以这不是把错误吞掉，是把原版漏写的那个终止条件补上。
    ///
    /// <b>匹配特征是唯一的</b>：<c>Update</c> 里有 10 处 <c>ldelem.u1</c>，但
    /// 「<c>A - 1 - B</c> 存进局部量、紧接着拿它读 buffer」这个形状只出现一次。
    /// 匹配数不是 1 会打 ERROR，不会静默失效。
    ///
    /// <b>这不保证任意速度都安全。</b> 它只修掉了实测炸过的那一处；
    /// 另外 9 处 <c>ldelem.u1</c> 有没有类似问题没有逐个验证过。
    /// 继续往上调仍然要实测。
    /// </summary>
    [HarmonyPatch]
    internal static class CargoPathBoundsPatches
    {
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(CargoPath), nameof(CargoPath.Update))]
        private static IEnumerable<CodeInstruction> Update_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var safeRead = AccessTools.Method(typeof(CargoPathBoundsPatches), nameof(ReadOrOccupied));
            var buffer = AccessTools.Field(typeof(CargoPath), nameof(CargoPath.buffer));

            if (safeRead == null || buffer == null)
            {
                ProjectEdenPlugin.Log.LogError(
                    "传送带回扫越界保护：解析不出目标方法或字段，未改写。" +
                    "传送带速度不要超过原版的 5，否则 CargoPath.Update 会抛 IndexOutOfRange");

                return code;
            }

            var hits = 0;

            // 形状： ldc.i4.1 / sub / ldloc B / sub / stloc C / ldarg.0 / ldfld buffer / ldloc C / ldelem.u1
            //        ↑ 也就是 at = pos - 1 - i; 然后立刻 buffer[at]
            for (var i = 0; i + 8 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldc_I4_1) continue;
                if (code[i + 1].opcode != OpCodes.Sub) continue;
                if (!code[i + 2].IsLdloc()) continue;
                if (code[i + 3].opcode != OpCodes.Sub) continue;
                if (!code[i + 4].IsStloc()) continue;
                if (code[i + 5].opcode != OpCodes.Ldarg_0) continue;
                if (code[i + 6].opcode != OpCodes.Ldfld || !Equals(code[i + 6].operand, buffer)) continue;
                if (!code[i + 7].IsLdloc()) continue;
                if (code[i + 8].opcode != OpCodes.Ldelem_U1) continue;

                // 就地改这一条指令，不要换成新对象——挂在它上面的跳转标签会丢
                code[i + 8].opcode = OpCodes.Call;
                code[i + 8].operand = safeRead;

                hits++;
            }

            // 第二处：缓冲区整体前移那一次 Array.Copy，长度可能是负数。
            var copies = 0;
            var safeCopy = AccessTools.Method(typeof(CargoPathBoundsPatches), nameof(SafeCopy));

            if (safeCopy != null)
                for (var i = 0; i < code.Count; i++)
                {
                    if (code[i].opcode != OpCodes.Call) continue;
                    if (!(code[i].operand is System.Reflection.MethodInfo mi)) continue;
                    if (mi.DeclaringType != typeof(System.Array) || mi.Name != nameof(System.Array.Copy)) continue;
                    if (mi.GetParameters().Length != 5) continue;

                    // 就地改，不换对象——标签会丢
                    code[i].operand = safeCopy;
                    copies++;
                }

            if (hits != 1 || copies != 1)
            {
                ProjectEdenPlugin.Log.LogError(
                    $"传送带越界保护：回扫命中 {hits} 处（期望 1）、Array.Copy 命中 {copies} 处（期望 1）。" +
                    "原版 IL 形状可能变了。在确认之前，请把 belts.json 里的速度调回 5 以内");

                return code;
            }

            ProjectEdenPlugin.Log.LogInfo(
                "传送带越界保护已生效（CargoPath.Update：回扫 1 处 + 缓冲区前移 1 处），高速传送带可用");

            return code;
        }

        /// <summary>
        /// 读缓冲区，<b>下标越界时报「有货」</b>。
        ///
        /// 调用方那个循环靠「读到非 0」跳出，所以越界返回非 0 正好等于
        /// 「已经扫到路径头部之外，前面没有更多空位了」——这是原版漏写的终止条件。
        ///
        /// 用 <c>(uint)</c> 比较一次判掉负数和超长两种越界，比写两个判断快一点；
        /// 这里在并行的货物 tick 上，每单位速度调一次。
        /// </summary>
        internal static int ReadOrOccupied(byte[] buffer, int index) =>
            (uint)index < (uint)buffer.Length ? buffer[index] : 1;

        private static int _clamped;

        /// <summary>
        /// 缓冲区整体前移那一次拷贝，<b>长度可能是负数</b>。
        ///
        /// 原版（IL 03F5）是
        /// <code>
        /// Array.Copy(buffer, at, buffer, at + shift, size - shift);
        /// </code>
        /// 前面只判过 <c>shift &gt; 0</c>，<b>没有任何地方保证 shift &lt;= size</b>。
        /// <c>shift</c> 正比于传送带速度，所以原版速度 ≤ 5 时这个不变量永远成立；
        /// 提速之后它会破，表现是并行的 <c>FactoryCargoPath</c> 阶段抛
        /// <c>ArgumentOutOfRangeException: Value has to be &gt;= 0. Parameter name: length</c>。
        /// 和上面那处回扫是同一个形状：<b>原版靠速度小让漏写的边界检查永远碰不到</b>。
        ///
        /// <b>钳到 0 是正确语义，不是把错误吞掉</b>：移动距离比待移动区还大，
        /// 意味着这一段里没有任何东西需要留下来——要拷的就是 0 字节。
        ///
        /// 真的钳过一次就报一行。这条不是常设机制而是速度调过头的信号，
        /// 说明 <c>belts.json</c> 的速度已经顶到原版这块代码的假设之外了。
        /// </summary>
        internal static void SafeCopy(System.Array src, int srcIndex, System.Array dst, int dstIndex, int length)
        {
            if (src == null || dst == null || srcIndex < 0 || dstIndex < 0) return;

            if (srcIndex + length > src.Length) length = src.Length - srcIndex;
            if (dstIndex + length > dst.Length) length = dst.Length - dstIndex;

            if (length <= 0)
            {
                // 抢占要在拼字符串之前：这条路跑在 _cargo_path_parallel 的工作线程上
                if (System.Threading.Interlocked.Exchange(ref _clamped, 1) != 0) return;

                ProjectEdenPlugin.Log.LogWarning(
                    "传送带缓冲区前移的拷贝长度算出来是负数，已按 0 处理（正确语义：没有东西要移）。" +
                    "这说明传送带速度已经顶到原版这块代码的假设之外——功能正常，但 belts.json 的速度" +
                    "再往上调要实测。这一行整局只打一次。");

                return;
            }

            System.Array.Copy(src, srcIndex, dst, dstIndex, length);
        }
    }
}
