using System;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ProjectEden.Patches.Station;
class Program
{
    static void Check(bool v, string m) { if (!v) throw new Exception(m); }
    static System.Collections.Generic.IEnumerable<CodeInstruction> GameTranspiler(System.Collections.Generic.IEnumerable<CodeInstruction> instructions, ILGenerator generator, System.Reflection.MethodBase __originalMethod)
        => IdleRemoteTickPatches.Rewrite(instructions, generator, __originalMethod.DeclaringType);
    static void Main()
    {
        new Harmony("eden.idleremote.test").CreateClassProcessor(typeof(IdleRemoteTickPatches)).Patch();
        Check(ProjectEdenPlugin.Log.Installed, "补丁未接入");
        foreach (int ships in new[] { 0, 1, 16, 256 })
        foreach (int warpers in new[] { 0, 10 })
        foreach (bool fail in new[] { false, true })
        {
            var s = new StationComponent { workShipCount = ships, warperCount = warpers, Fail = fail };
            int pos = 0, rot = 0;
            try { s.InternalTickRemote(0,0,0,0,0,null,null,ref pos,ref rot,false,null); Check(!fail, "吞掉异常"); }
            catch (InvalidOperationException) { Check(fail, "意外异常"); }
            Check(s.warperCount == (warpers == 0 ? 1 : 10), "曲速器未补充");
            Check(s.RenderCalls == 1 && pos == 1 && rot == 2, "尾部/引用参数改变");
            Check(s.PriorityTicks == (fail ? 10 : 9), "优先级锁更新改变");
            Check(s.FlightCount == ships && s.ExpensiveCalls == (ships == 0 ? 0 : 1), "空转未跳过或飞行改变");
            Check(s.Stock == (warpers == 0 ? 9 : 10) && s.Inc == (warpers == 0 ? 27 : 30), "库存或增产不守恒");
        }
        var a = new[] { new CodeInstruction(OpCodes.Ret) };
        Check(IdleRemoteTickPatches.Transpile(a, new DynamicMethod("fallback", typeof(void), Type.EmptyTypes).GetILGenerator()).Single() == a[0], "回退改变指令");
        string managed = @"G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed";
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
            string path = System.IO.Path.Combine(managed, new System.Reflection.AssemblyName(args.Name).Name + ".dll");
            return System.IO.File.Exists(path) ? System.Reflection.Assembly.LoadFrom(path) : null;
        };
        var game = System.Reflection.Assembly.LoadFrom(System.IO.Path.Combine(managed, "Assembly-CSharp.dll"));
        var method = AccessTools.Method(game.GetType("StationComponent"), "InternalTickRemote");
        ProjectEdenPlugin.Log.Installed = false;
        new Harmony("eden.idleremote.gameil").Patch(method, transpiler: new HarmonyMethod(typeof(Program), nameof(GameTranspiler)));
        Check(ProjectEdenPlugin.Log.Installed, "真实游戏IL形状不匹配");
        Console.WriteLine("PASS: actual Assembly-CSharp InternalTickRemote rewritten and Harmony dynamic method compiled.");
        Console.WriteLine("PASS: actual Harmony, idle/active ships, warper refill/full, inventory quality, ref arguments, renderer exception, priority countdown and shape fallback.");
    }
}
class StationComponent
{
    public int warperCount, warperMaxCount = 10, workShipCount, Stock = 10, Inc = 30;
    public int ExpensiveCalls, FlightCount, RenderCalls, PriorityTicks = 10;
    public bool Fail;
    object storage = new object();
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void InternalTickRemote(int factory, int time, float speed, float warp, int carry, object stations, object astros, ref int pos, ref int rot, bool map, int[] consume)
    {
        if (warperCount < warperMaxCount)
        {
            lock(storage) { int quality = Inc / Stock; Stock--; Inc -= quality; warperCount++; }
        }
        int n = 0;
        ExpensiveCalls++;
        for (; n < workShipCount; n++) FlightCount++;
        ShipRenderersOnTick(astros, ref pos, ref rot);
        PriorityTicks--;
    }
    public void ShipRenderersOnTick(object astros, ref int pos, ref int rot)
    { RenderCalls++; pos++; rot += 2; if(Fail) throw new InvalidOperationException(); }
}
class ProjectEdenPlugin { public static LogStub Log = new LogStub(); }
class LogStub { public bool Installed; public void LogInfo(string text) { Installed = true; } public void LogWarning(string text) { Console.WriteLine(text); } }
