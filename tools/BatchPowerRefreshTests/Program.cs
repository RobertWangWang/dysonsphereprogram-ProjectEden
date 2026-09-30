using System;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ProjectEden.Patches;
class Program
{
 static void Check(bool x,string m){if(!x)throw new Exception(m);}
 static void Main()
 {
  new Harmony("eden.batchpower.tests").CreateClassProcessor(typeof(BatchPowerConsumerRefresh)).Patch();
  ProjectEden.Patches.Diagnostics.BlueprintPasteProbe.Config.enabled=true;
  var f=new PlanetFactory();f.CreateEntityLogicComponents();Check(f.Model.Refreshes==1&&f.Model.Published==1,"single build");
  for(int w=0;w<250;w++){
   f=new PlanetFactory();using(BatchPowerConsumerRefresh.Begin(f.Model)){for(int i=0;i<100;i++)f.CreateEntityLogicComponents();Check(f.Model.Refreshes==0&&f.Model.Components==100,"components deferred");}
   Check(f.Model.Refreshes==1&&f.Model.Published==100,"batch result");
  }
  var totals=BatchPowerConsumerRefresh.Take();Check(totals[0]==250&&totals[1]==25000&&totals[2]==250,"merge counters");
  f=new PlanetFactory();using(BatchPowerConsumerRefresh.Begin(f.Model)){}Check(f.Model.Refreshes==0,"empty batch");
  try{using(BatchPowerConsumerRefresh.Begin(f.Model)){f.CreateEntityLogicComponents();throw new InvalidOperationException();}}catch(InvalidOperationException){}
  Check(f.Model.Refreshes==1&&f.Model.Published==1&&BatchPowerConsumerRefresh.Current==null,"exception flush/cleanup");
  var outer=new FactoryModel();var inner=new FactoryModel();var foreign=new FactoryModel();
  using(BatchPowerConsumerRefresh.Begin(outer)){
   BatchPowerConsumerRefresh.Request(outer);
   using(BatchPowerConsumerRefresh.Begin(inner)){BatchPowerConsumerRefresh.Request(inner);BatchPowerConsumerRefresh.Request(foreign);}
   Check(inner.Refreshes==1&&foreign.Refreshes==1&&outer.Refreshes==0,"nested/foreign scope");
  }Check(outer.Refreshes==1,"outer restoration");
  var broken=new FactoryModel{Fail=true};try{using(BatchPowerConsumerRefresh.Begin(broken)){BatchPowerConsumerRefresh.Request(broken);}}catch(InvalidOperationException){}
  Check(BatchPowerConsumerRefresh.Current==null,"flush failure scope leaked");
  totals=BatchPowerConsumerRefresh.Take();Check(totals[4]==1,"failure count");
  var plain=new FactoryModel();var scope=BatchPowerConsumerRefresh.Begin(plain);BatchPowerConsumerRefresh.Request(plain);scope.Dispose();scope.Dispose();Check(plain.Refreshes==1,"double dispose");
  BatchPowerConsumerRefresh.Take();ProjectEden.Patches.Diagnostics.BlueprintPasteProbe.Config.enabled=false;
  using(BatchPowerConsumerRefresh.Begin(plain)){BatchPowerConsumerRefresh.Request(plain);BatchPowerConsumerRefresh.Request(plain);}
  Check(plain.Refreshes==2&&BatchPowerConsumerRefresh.Take().All(x=>x==0),"probe off disabled optimization");
  var unknown=new[]{new CodeInstruction(OpCodes.Ret)};Check(BatchPowerConsumerRefresh.Transpile(unknown).Single().opcode==OpCodes.Ret,"fallback");
  // 真实游戏程序集只核验调用形状；独立进程不执行Unity建造。
  using(var asm=Mono.Cecil.AssemblyDefinition.ReadAssembly(@"G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed\Assembly-CSharp.dll")){
   var method=asm.MainModule.GetType("PlanetFactory").Methods.Single(m=>m.Name=="CreateEntityLogicComponents");
   Check(method.Body.Instructions.Count(i=>i.Operand is Mono.Cecil.MethodReference mr&&mr.DeclaringType.Name=="FactoryModel"&&mr.Name=="RefreshPowerConsumers"&&mr.Parameters.Count==0)==1,"actual call mismatch");
  }
  Console.WriteLine("PASS: real Harmony 25000 builds/250 refreshes; single/empty/nested/foreign batches, failure cleanup, disabled diagnostics, shape fallback and actual game IL anchor.");
 }
}
class FactoryModel{
 public int Components,Published,Refreshes;public bool Fail;
 [MethodImpl(MethodImplOptions.NoInlining)]public void RefreshPowerConsumers(){Refreshes++;if(Fail)throw new InvalidOperationException();Published=Components;}
}
class PlanetFactory{
 public FactoryModel Model=new FactoryModel();
 [MethodImpl(MethodImplOptions.NoInlining)]public void CreateEntityLogicComponents(){Model.Components++;Model.RefreshPowerConsumers();}
}
static class ProjectEdenPlugin{internal static Logger Log=new Logger();}
class Logger{public void LogInfo(string s){Console.WriteLine(s);}public void LogWarning(string s){Console.WriteLine(s);}}
namespace ProjectEden.Patches.Diagnostics{static class BlueprintPasteProbe{internal static Config Config=new Config();}class Config{public bool enabled;}}
