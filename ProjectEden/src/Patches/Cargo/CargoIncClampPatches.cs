using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace ProjectEden.Patches
{
    /// <summary>
    /// 把传送带货物增产点数的<b>回绕</b>改成<b>饱和</b>。
    ///
    /// <b>问题。</b> <c>Cargo.inc</c> 是一个 Byte，存的是<b>整堆</b>的增产点数
    /// （不是每件的等级——证据是原版分堆时要用 <c>inc / stack</c> 先把等级还原出来）。
    /// 增产剂 Mk.III 每件 4 点，所以 <c>stack × 4 ≤ 255</c> 只到 <b>63 层</b>。
    /// 本 mod 出厂是 255 层。
    ///
    /// <b>原版处处给 stack 上了护栏，却处处漏掉 inc。</b> 两个加法点，同一个毛病：
    /// <code>
    ///   CargoContainer.AddItemStackToCargo        // 传送带插入
    ///     0034: bge  → stack &gt;= maxStack 就返回                  // stack 有护栏
    ///     00AD: stack: dup ldind.u1 ldloc.0 conv.u1 add conv.u1 stind.i1
    ///     00C5: inc  : dup ldind.u1 ldloc.1 conv.u1 add conv.u1 stind.i1   // ← 没有
    ///
    ///   CargoPath.TryUpdateItemAtHeadAndFillBlank // 分馏塔产出 ×4、物流站输出
    ///     0066: stack + 加数 &gt; maxStack → 整笔合并作废                // stack 有护栏
    ///     007E: stack: dup ldind.u1 ldarg.3  add conv.u1 stind.i1
    ///     009F: inc  : dup ldind.u1 ldarg.s  add conv.u1 stind.i1          // ← 没有
    /// </code>
    /// 结尾那个 <c>conv.u1</c> 就是回绕的来源。于是堆到 255 层时 inc 是个不确定的垃圾值——
    /// 不是干净的 0，而是「这一堆到底喷没喷过」彻底失真。
    ///
    /// <b>这个补丁只做一件事：把这两处加法夹在 255。</b> 它<b>不能</b>让 255 层和满级增产同时成立
    /// （一个字节装不下 1020），能做到的是把<b>静默的灾难</b>变成<b>确定的降级</b>——
    /// 255 层喷 Mk.III 之后稳定等效低一级，而不是随机失效。
    ///
    /// <b>为什么值得单独做这一步。</b> 真正的修法有两条（<c>inc</c> 改存每件速率、
    /// 或者加宽成 Int16 走 preloader），两条都要先回答一个离线查不出来的问题：
    /// <b>编译好的货物着色器拿 inc 干什么</b>（传送带上那个增产剂箭头）。
    /// 在那之前，这两处夹取是无风险的安全网，而且和后面两条都不冲突。
    ///
    /// <b>另外两个写入点不用管，但理由不一样，别一概而论：</b>
    /// <c>StorageComponent.AddCargo</c> 把 <c>inc</c> 的<b>地址</b>交给
    /// <c>split_inc(byte&amp;, byte&amp;, byte)</c>——那是往 <c>GRID.inc</c>（Int32）里<b>放血</b>，
    /// 只减不增；<c>PilerComponent</c> 分堆走的是 <c>inc / stack × newStack</c>，结果必然更小。
    /// 真正会涨的只有上面两处。
    ///
    /// 两处的 <c>stack</c> 都<b>不动</b>：它们各自上游都有护栏（一个只加到装满为止，
    /// 一个超了就整笔作废），只要 maxStack ≤ 255 就不可能溢出。
    /// </summary>
    [HarmonyPatch]
    internal static class CargoIncClampPatches
    {
        /// <summary>一个字节能存的上限。<b>这不是可调参数</b>，是 <c>Cargo.inc</c> 的类型决定的</summary>
        private const int ByteMax = 255;

        private static int _logged;

        /// <summary>
        /// 加宽生效后这个补丁必须整个让位，两个理由都是硬的：
        /// 一是它要找的 <c>stind.i1</c> / <c>conv.u1</c> 已经被 preloader 换成了二字节版本，
        /// 匹配不上会按规矩响一声报错；
        /// 二是「夹在 255」正是加宽要消除的东西——再夹一次等于把刚拿到的量程扳回去。
        /// </summary>
        private static bool Prepare() => !CargoWidening.IsActive;

        private static readonly FieldInfo IncField = AccessTools.Field(typeof(Cargo), nameof(Cargo.inc));

        private static readonly MethodInfo ClampAddMethod =
            AccessTools.Method(typeof(CargoIncClampPatches), nameof(ClampAdd));

        /// <summary>
        /// 按「名字 + 声明类型」认字段，不靠 FieldInfo 的引用相等。
        /// 两个目标方法里都还有一处形状一模一样的 <c>Cargo.stack</c> 写回，
        /// 认错了就等于给一个本来就有护栏的地方打补丁。
        /// </summary>
        private static bool IsIncField(object operand) =>
            operand is FieldInfo f && f.Name == IncField.Name && f.DeclaringType == IncField.DeclaringType;

        /// <summary>
        /// 供 IL 调用。两个入参都是 <c>ldind.u1</c> / <c>ldloc</c> / <c>ldarg</c> 压上来的 int32，
        /// 返回值 ≤ 255，所以后面那条 <c>stind.i1</c> 存回去不会再截断。
        /// </summary>
        internal static int ClampAdd(int current, int add)
        {
            int sum = current + add;

            if (sum <= ByteMax) return sum < 0 ? 0 : sum;

            if (Interlocked.Exchange(ref _logged, 1) == 0)
                ProjectEdenPlugin.Log.LogWarning(
                    $"传送带增产点数已达单字节上限：整堆 {sum} 点被夹到 {ByteMax}。" +
                    "这不是故障——Cargo.inc 是一个字节，装不下 255 层 × 每件 4 点。" +
                    "结果是这一堆按较低的增产等级结算，而不是回绕成垃圾值。要两者兼得，" +
                    "得把 inc 改成存每件速率、或加宽成 Int16（见 CLAUDE.md 的集装那一节）；" +
                    "只想要满级增产的话，把 stations.json 的三个集装值调回 63。");

            return ByteMax;
        }

        /// <summary>
        /// 两个目标方法。用 TargetMethods 而不是两条 <c>[HarmonyPatch]</c>，
        /// 是因为改写逻辑一模一样，分成两份就会漏改其中一份。
        /// </summary>
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CargoContainer), nameof(CargoContainer.AddItemStackToCargo));
            yield return AccessTools.Method(typeof(CargoPath), nameof(CargoPath.TryUpdateItemAtHeadAndFillBlank));
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            // 先挡住 null：解析失败还往下走会把方法改烂，而且悄无声息
            if (IncField == null || ClampAddMethod == null)
            {
                ProjectEdenPlugin.Log.LogError("解析不到 Cargo.inc，增产点数回绕未接管");

                return instructions;
            }

            var code = new List<CodeInstruction>(instructions);

            var replaced = 0;

            // 要找的形状（把 inc 写回去的那一段），中间那条 conv.u1 两个方法里一有一无：
            //   ldflda Cargo::inc | dup | ldind.u1 | <压入加数> | [conv.u1] | add | conv.u1 | stind.i1
            //
            // 改法是**原地换操作码，不删指令**——三条都可能落着跳转标签，
            // RemoveRange 会把标签连同指令一起扔掉（本仓库的 IL 规矩，见 CLAUDE.md）。
            for (var i = 0; i + 5 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldflda || !IsIncField(code[i].operand)) continue;
                if (code[i + 1].opcode != OpCodes.Dup) continue;
                if (code[i + 2].opcode != OpCodes.Ldind_U1) continue;

                // i+3 是压加数的那条（ldloc / ldarg），不限定具体形式；
                // 它后面要么直接 add，要么先 conv.u1 再 add
                int add = code[i + 4].opcode == OpCodes.Conv_U1 ? i + 5 : i + 4;

                if (add + 2 >= code.Count) continue;
                if (code[add].opcode != OpCodes.Add) continue;
                if (code[add + 1].opcode != OpCodes.Conv_U1) continue;
                if (code[add + 2].opcode != OpCodes.Stind_I1) continue;

                // 加数前的 conv.u1 必须去掉：AddItemStackToCargo 里加数本身就可能 > 255
                // （每件点数 × 层数），先截断再夹取等于夹一个已经绕过的值
                if (add != i + 4) code[i + 4].opcode = OpCodes.Nop;

                code[add].opcode = OpCodes.Call;
                code[add].operand = ClampAddMethod;

                // 结果已经 ≤ 255，末尾这条截断就是回绕本身，去掉
                code[add + 1].opcode = OpCodes.Nop;

                replaced++;
            }

            if (replaced == 0)
                ProjectEdenPlugin.Log.LogError(
                    $"{original?.DeclaringType?.Name}.{original?.Name} 里没找到 inc 的累加处，" +
                    "传送带堆叠超过 63 层时增产点数仍会回绕成垃圾值");
            else
                ProjectEdenPlugin.Log.LogInfo(
                    $"传送带增产点数改为饱和而非回绕：{original?.DeclaringType?.Name}.{original?.Name}（{replaced} 处）");

            return code;
        }
    }
}
