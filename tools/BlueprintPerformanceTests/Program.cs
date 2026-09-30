using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ProjectEden.Patches;
using ProjectEden.Patches.Diagnostics;
class Program
{
    static void Check(bool v, string m) { if (!v) throw new Exception(m); }
    static void Main() { try { Run(); } catch (Exception e) { Console.WriteLine(e.GetType().FullName + ": " + e.Message); Console.WriteLine(e.StackTrace); if(e.InnerException!=null) Console.WriteLine(e.InnerException.GetType().FullName + ": " + e.InnerException.Message); Environment.ExitCode=1; } }
    static System.Collections.Generic.IEnumerable<CodeInstruction> Identity(System.Collections.Generic.IEnumerable<CodeInstruction> instructions) => instructions;
    static void Run()
    {
        ColliderTests.Run();
        var b = new BuildFrameBudget();
        Check(b.Begin(1,0,4,1000), "first"); Check(!b.Expired(3) && b.Expired(4), "deadline");
        b.Finish(9); Check(!b.Begin(1,10,4,1000), "same frame overshoot");
        Check(b.Begin(2,11,4,1000), "next frame"); b.Finish(12);
        Check(b.Begin(2,13,4,1000), "remaining"); Check(!b.Expired(15) && b.Expired(16), "shared time");
        b.Finish(16); Check(b.Begin(2,17,0,1000) && !b.Expired(9999), "disable");
        new Harmony("eden.bpdetail.fixture").CreateClassProcessor(typeof(BlueprintConditionDetailProbe)).Patch();
        for(int n=0;n<40;n++) foreach(bool on in new[]{false,true}) {
            BlueprintPasteProbe.Config.enabled=on;
            var f=new BuildTool_BlueprintPaste{Count=n};
            int expected=0; for(int i=0;i<n;i++) for(int j=0;j<i;j++) { if(j%3==0) continue; expected+=j; }
            Check(f.CheckBuildConditions()==(expected%2==0)&&f.Sum==expected,"loop/branch behavior");
        }
        BlueprintPasteProbe.Config.enabled=true;
        try {new BuildTool_BlueprintPaste{Count=4,Fail=true}.CheckBuildConditions();throw new Exception("swallowed");}catch(InvalidOperationException){}
        Check(typeof(BlueprintConditionDetailProbe).GetField("_current",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null)==null,"context leaked");
        BlueprintConditionDetailProbe.Begin(out var outer); BlueprintConditionDetailProbe.Begin(out var inner);
        BlueprintConditionDetailProbe.End(inner,null); Check(ReferenceEquals(outer,typeof(BlueprintConditionDetailProbe).GetField("_current",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null)),"nested restore");
        BlueprintConditionDetailProbe.End(outer,null);
        string managed=@"G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed";
        AppDomain.CurrentDomain.AssemblyResolve+=(sender,args)=>{var path=System.IO.Path.Combine(managed,new AssemblyName(args.Name).Name+".dll");return System.IO.File.Exists(path)?Assembly.LoadFrom(path):null;};
        var game=Assembly.LoadFrom(System.IO.Path.Combine(managed,"Assembly-CSharp.dll"));
        var method=AccessTools.Method(game.GetType("BuildTool_BlueprintPaste"),"CheckBuildConditions");
        var original=PatchProcessor.GetOriginalInstructions(method).ToArray();
        var rewritten=BlueprintConditionDetailProbe.Transpile(original.Select(c=>new CodeInstruction(c))).ToArray();
        var mark=AccessTools.Method(typeof(BlueprintConditionDetailProbe),"Mark");
        int inserted=rewritten.Count(c=>c.Calls(mark)); Check(inserted>20,"actual game not instrumented");
        var stripped=rewritten.Where((c,i)=>!c.Calls(mark)&&!(i+1<rewritten.Length&&rewritten[i+1].Calls(mark))).ToArray();
        Check(stripped.Length==original.Length && stripped.Zip(original,(a,c)=>a.opcode==c.opcode&&Equals(a.operand,c.operand)).All(x=>x),"original instructions changed");
        Console.WriteLine("Original instruction comparison passed; compiling game patch.");
        try { new Harmony("eden.bpdetail.identity").Patch(method,transpiler:new HarmonyMethod(typeof(Program),"Identity")); }
        catch (HarmonyException e) when (e.InnerException is System.Security.SecurityException)
        {
            Console.WriteLine("LIMITATION: even unchanged game method cannot JIT outside Unity (ECall); actual game IL preservation verified, runtime compilation requires game.");
            Console.WriteLine("PASS: budget/shared ticks/overshoot/disable, 80 Harmony fixtures, exception/nesting cleanup, real IL comparison.");
            return;
        }
        new Harmony("eden.bpdetail.game").Patch(method,transpiler:new HarmonyMethod(typeof(BlueprintConditionDetailProbe),"Transpile"));
        Console.WriteLine("PASS: frame budget/overshoot/shared ticks/off; 80 loop fixtures, exception/nesting cleanup; actual game IL preserved and Harmony compiled.");
    }
}
class BuildTool_BlueprintPaste
{
    public int Count,Sum; public bool Fail;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool CheckBuildConditions(){ Sum=0; for(int i=0;i<Count;i++) { for(int j=0;j<i;j++) {if(j%3==0)continue; Sum+=j;} if(Fail)throw new InvalidOperationException();} return Sum%2==0; }
}
static class ProjectEdenPlugin {internal static Logger Log=new Logger(); internal static CheatsConfig CheatsConfig=new CheatsConfig();}
class Logger {public void LogInfo(string s){Console.WriteLine(s);}public void LogWarning(string s){Console.WriteLine(s);}}
namespace ProjectEden.Patches.Diagnostics {
 static class BlueprintPasteProbe {internal static ProbeConfig Config=new ProbeConfig();}
 class ProbeConfig {public bool enabled; internal float SpikeMs()=>20;}
}
