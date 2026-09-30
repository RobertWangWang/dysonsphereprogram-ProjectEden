using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ProjectEden.Patches;
class Program
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Main()
    {
        var gc = AccessTools.Method(typeof(GC), nameof(GC.Collect), Type.EmptyTypes);
        var label = new DynamicMethod("labels", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel();
        var call = new CodeInstruction(OpCodes.Call, gc); call.labels.Add(label);
        var valid = BlueprintPasteGcPatches.Transpile(new[] { call, new CodeInstruction(OpCodes.Ret) }).ToArray();
        Check(valid[0].opcode == OpCodes.Nop && valid[0].labels.Contains(label), "未保留标签");
        foreach (var invalid in new[] {
            new[] { new CodeInstruction(OpCodes.Ret) },
            new[] { new CodeInstruction(OpCodes.Call, gc), new CodeInstruction(OpCodes.Call, gc), new CodeInstruction(OpCodes.Ret) },
            new[] { new CodeInstruction(OpCodes.Call, gc), new CodeInstruction(OpCodes.Ldc_I4_0), new CodeInstruction(OpCodes.Pop), new CodeInstruction(OpCodes.Ret) }
        }) {
            var opcodes = invalid.Select(x => x.opcode).ToArray();
            Check(BlueprintPasteGcPatches.Transpile(invalid).Select(x => x.opcode).SequenceEqual(opcodes), "形状回退改变原逻辑");
        }
        new Harmony("eden.blueprintgc.fixture").CreateClassProcessor(typeof(BlueprintPasteGcPatches)).Patch();
        var fixture = new BuildTool_BlueprintPaste(); int collections = GC.CollectionCount(GC.MaxGeneration);
        fixture.CreatePrebuilds();
        Check(fixture.Created == 1 && fixture.Notified == 1 && fixture.Stock == 9, "落地副作用改变");
        Check(GC.CollectionCount(GC.MaxGeneration) == collections, "仍强制回收");
        string managed = @"G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed";
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
            string path = System.IO.Path.Combine(managed, new AssemblyName(args.Name).Name + ".dll");
            return System.IO.File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var game = Assembly.LoadFrom(System.IO.Path.Combine(managed, "Assembly-CSharp.dll"));
        var method = AccessTools.Method(game.GetType("BuildTool_BlueprintPaste"), "CreatePrebuilds");
        var original = PatchProcessor.GetOriginalInstructions(method).ToArray();
        var rewritten = BlueprintPasteGcPatches.Transpile(original.Select(c => new CodeInstruction(c))).ToArray();
        Check(original.Count(c => c.Calls(gc)) == 1 && rewritten.Count(c => c.Calls(gc)) == 0, "实际游戏 IL 未匹配");
        Check(original.Length == rewritten.Length && original.Zip(rewritten, (a,b) => a.opcode != b.opcode || !Equals(a.operand,b.operand)).Count(x => x) == 1, "改动超出一个 GC 调用");
        new Harmony("eden.blueprintgc.game").Patch(method, transpiler: new HarmonyMethod(typeof(BlueprintPasteGcPatches), "Transpile"));
        Console.WriteLine("PASS: real game IL only one instruction changed; Harmony compilation, fixture side effects, GC removal, labels and fallback verified.");
    }
}
class BuildTool_BlueprintPaste
{
    public int Created, Notified, Stock = 10;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void CreatePrebuilds() { Stock--; Created++; Notified++; GC.Collect(); }
}
static class ProjectEdenPlugin { internal static readonly Logger Log = new Logger(); }
class Logger { public void LogInfo(string s) { Console.WriteLine(s); } public void LogWarning(string s) { Console.WriteLine(s); } }
