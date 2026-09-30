using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using ProjectEden.Patches.Diagnostics;
class Program
{
 static void Check(bool v,string m){if(!v)throw new Exception(m);}
 static void Verify(long[] t,long roots,long errors){Check(t[0]==roots&&t[3]==errors,"root/error counts");Check(t.Skip(4).Take(17).All(x=>x>=0),"negative exclusive cost");Check(t.Skip(4).Take(17).Sum()==t[1],"exclusive sum differs from total");}
 static void Main()
 {
  new Harmony("eden.buildfinallyprobe.tests").CreateClassProcessor(typeof(BuildFinallyProbe)).Patch();
  BlueprintPasteProbe.Config.enabled=true;
  new PlanetFactory().FlattenTerrain();Verify(BuildFinallyProbe.Take(),0,0);
  Parallel.For(0,8,w=>{for(int i=0;i<500;i++){var f=new PlanetFactory();f.BuildFinally(0);Check(f.Effects==13,"behavior changed");}});
  var t=BuildFinallyProbe.Take();Verify(t,4000,0);Check(t.Skip(21).All(x=>x==4000),"child calls lost");
  new PlanetFactory().BuildFinally(1);t=BuildFinallyProbe.Take();Verify(t,1,0);Check(t[21]==1&&t.Skip(22).All(x=>x==0),"early return");
  var failed=new PlanetFactory();try{failed.BuildFinally(2);throw new Exception("exception swallowed");}catch(InvalidOperationException){}
  t=BuildFinallyProbe.Take();Verify(t,1,1);Check(failed.Effects==4,"failure side effects");
  new PlanetFactory().BuildFinally(3);t=BuildFinallyProbe.Take();Verify(t,1,0);Check(t[21]==2,"nested root double counted");
  BlueprintPasteProbe.Config.enabled=false;new PlanetFactory().BuildFinally(0);Verify(BuildFinallyProbe.Take(),0,0);
  BlueprintPasteProbe.Config.enabled=true;
  long roots=0;
  Parallel.For(0,1000,i=>{new PlanetFactory().BuildFinally(0);var x=BuildFinallyProbe.Take();Verify(x,x[0],0);System.Threading.Interlocked.Add(ref roots,x[0]);});
  var last=BuildFinallyProbe.Take();Verify(last,last[0],0);roots+=last[0];Check(roots==1000,"concurrent report loss");
  string managed=@"G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed";
  AppDomain.CurrentDomain.AssemblyResolve+=(sender,args)=>{var file=System.IO.Path.Combine(managed,new AssemblyName(args.Name).Name+".dll");return System.IO.File.Exists(file)?Assembly.LoadFrom(file):null;};
  var game=Assembly.LoadFrom(System.IO.Path.Combine(managed,"Assembly-CSharp.dll"));
  for(int i=0;i<17;i++){
    string type=i==14?"PlayerAction_Build":i==15?"GameHistoryData":i==16?"GameScenarioLogic":"PlanetFactory";
    Check(AccessTools.GetDeclaredMethods(game.GetType(type)).Count(m=>m.Name==BuildFinallyProbe.MethodNames[i])==1,"real method target mismatch");
  }
  Console.WriteLine("PASS: 5000 concurrent builds with atomic reports; exclusive sum equals total; early-return/nesting/exception/off/outside scope and unchanged side effects; all 17 actual game method targets verified.");
 }
}
class PlanetFactory
{
 public int Effects;private bool Fail;
 [MethodImpl(MethodImplOptions.NoInlining)]public void BuildFinally(int mode){if(mode==1)return;if(mode==3)BuildFinally(1);Fail=mode==2;FlattenTerrain();AddEntityDataWithComponents();new PlayerAction_Build().NotifyBuilt();RemovePrebuildWithComponents();new GameHistoryData().MarkItemBuilt();OnBeltBuilt();OnInserterBuilt();OnAddonBuilt();OnBuildEntity();OnSinglyBuildEntity();new GameScenarioLogic().NotifyOnBuild();CheckDysonSphereConditionAfterConstruction();}
 [MethodImpl(MethodImplOptions.NoInlining)]public void AddEntityDataWithComponents(){Effects++;AddEntityData();HandleObjectConnChangeWhenBuild();CreateEntityLogicComponents();CreateEntityDisplayComponents();}
 [MethodImpl(MethodImplOptions.NoInlining)]public void CreateEntityLogicComponents(){if(Fail)throw new InvalidOperationException();Effects++;}
 [MethodImpl(MethodImplOptions.NoInlining)]public void FlattenTerrain(){Effects++;}
 [MethodImpl(MethodImplOptions.NoInlining)]public void AddEntityData(){Effects++;}
 [MethodImpl(MethodImplOptions.NoInlining)]public void HandleObjectConnChangeWhenBuild(){Effects++;}
 [MethodImpl(MethodImplOptions.NoInlining)]public void CreateEntityDisplayComponents(){Effects++;}
 [MethodImpl(MethodImplOptions.NoInlining)]public void RemovePrebuildWithComponents(){Effects++;}
 [MethodImpl(MethodImplOptions.NoInlining)]public void OnBeltBuilt(){Effects++;}
 [MethodImpl(MethodImplOptions.NoInlining)]public void OnInserterBuilt(){Effects++;}
 [MethodImpl(MethodImplOptions.NoInlining)]public void OnAddonBuilt(){Effects++;}
 [MethodImpl(MethodImplOptions.NoInlining)]public void OnBuildEntity(){Effects++;}
 [MethodImpl(MethodImplOptions.NoInlining)]public void OnSinglyBuildEntity(){Effects++;}
 [MethodImpl(MethodImplOptions.NoInlining)]public void CheckDysonSphereConditionAfterConstruction(){Effects++;}
}
class PlayerAction_Build{[MethodImpl(MethodImplOptions.NoInlining)]public void NotifyBuilt(){}}
class GameHistoryData{[MethodImpl(MethodImplOptions.NoInlining)]public void MarkItemBuilt(){}}
class GameScenarioLogic{[MethodImpl(MethodImplOptions.NoInlining)]public void NotifyOnBuild(){}}
class UIGame{public void _OnUpdate(){}}
namespace UnityEngine {static class Time {public static float realtimeSinceStartup=>0;}}
static class ProjectEdenPlugin{internal static Logger Log=new Logger();}
class Logger{public void LogWarning(string s){throw new Exception(s);}public void LogInfo(string s){Console.WriteLine(s);}}
namespace ProjectEden.Patches.Diagnostics{static class BlueprintPasteProbe{internal static Config Config=new Config();}class Config{public bool enabled;internal float ReportSeconds()=>5;}}

class FactoryModel {public void RefreshPowerConsumers(){}}
