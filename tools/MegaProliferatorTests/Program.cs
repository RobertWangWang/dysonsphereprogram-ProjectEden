using System;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using ProjectEden.Patches;
class Program
{
 static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
 static AssemblerComponent Make(int level,bool accel,int type=4,int count=1,int speed=100000000)
 {
  return new AssemblerComponent{entityId=1,speed=speed,speedOverride=speed,forceAccMode=accel,recipeType=(ERecipeType)type,served=new[]{2000000,2000000},incServed=new[]{2000000*level,2000000*level},produced=new[]{0},needs=new int[6],recipeExecuteData=new RecipeExecuteData{timeSpend=600000,extraTimeSpend=6000000,productive=true,requires=new[]{1,2},requireCounts=new[]{1,2},products=new[]{10},productCounts=new[]{count}}};
 }
 static (long product,long consume,int extra,int main) Run(int level,bool accel,int type=4,int count=1,int divider=2,bool batch=true)
 {
  MegaThrottle.GlobalDivider=divider;
  var c=Make(level,accel,type,count);var p=new int[32];var r=new int[32];
  for(int tick=0;tick<1200;tick++)
  {
   Array.Clear(c.produced,0,c.produced.Length);
   if(tick%divider!=c.entityId%divider) MegaThrottle.Hold(ref c);
   else
   {
    if(divider>1)MegaThrottle.Release(null,ref c);
    MegaProliferatorTiming.Repair(ref c);
    int cycles=MegaProliferatorTiming.ScaleCycles(ref c,60*divider,tick);
    for(int j=0;j<cycles-1;j++)
    {
     c.InternalUpdate(1,p,r);
     if(batch) j+=MegaProliferatorBatch.TryApply(ref c,cycles-2-j,1,p,r);
    }
   }
   c.InternalUpdate(1,p,r);
   Check(c.extraTime>=0,"negative extra progress");
  }
  Check(r[2]==r[1]*2,"input conservation");
  Check(p[10]==(c.cycleCount+c.extraCycleCount)*count,"output statistics");
  Check(r[1]==c.cycleCount+(c.replicating?1:0),"one paid in-flight cycle");
  return (p[10],r[1],c.extraCycleCount,c.cycleCount);
 }
 static void Main() {try {RunTests();} catch(Exception e) { Console.WriteLine(e.GetType().Name+": "+e.Message); Environment.ExitCode=1;}}
 static void RunTests()
 {
  var h=new Harmony("eden.tests.mega.proliferator");
  Console.WriteLine("Patching timing");
  h.CreateClassProcessor(typeof(MegaProliferatorTimingPatches)).Patch();
  Console.WriteLine("Patching gates");
  h.CreateClassProcessor(typeof(MegaOutputGatePatches)).Patch();
  Check(MegaProliferatorTiming.Enabled,"timing transpiler did not match");
  // The same transpilers must also match the shipped game's IL, not just the recompiled fixture.
  Console.WriteLine("Checking game IL");
  using(var asm=Mono.Cecil.AssemblyDefinition.ReadAssembly(@"G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed\Assembly-CSharp.dll"))
  {
   var type=System.Linq.Enumerable.First(asm.MainModule.Types,t=>t.Name=="AssemblerComponent");
   var method=System.Linq.Enumerable.First(type.Methods,m=>m.Name=="InternalUpdate");
   var real=method.Body.Instructions;int steps=0,smelt=0,multiply=0;
   for(int i=2;i<real.Count;i++)
   {
    var f=real[i].Operand as Mono.Cecil.FieldReference;
    if(real[i].OpCode.Code==Mono.Cecil.Cil.Code.Stfld && f!=null && (f.Name=="time"||f.Name=="extraTime") && real[i-1].OpCode.Code==Mono.Cecil.Cil.Code.Add && real[i-2].OpCode.Code==Mono.Cecil.Cil.Code.Conv_I4)steps++;
    int value=(real[i].OpCode.Code==Mono.Cecil.Cil.Code.Ldc_I4_S||real[i].OpCode.Code==Mono.Cecil.Cil.Code.Ldc_I4)?Convert.ToInt32(real[i].Operand):-1;
    if(value==100 && real[i-1].OpCode.Code==Mono.Cecil.Cil.Code.Add)smelt++;
    if(i+1<real.Count && (value==9||value==19) && real[i-1].OpCode.Code==Mono.Cecil.Cil.Code.Ldelem_I4 && real[i+1].OpCode.Code==Mono.Cecil.Cil.Code.Mul)multiply++;
   }
   Check(steps==2 && smelt==2 && multiply==7,"actual game IL anchors");
  }
  foreach(int divider in new[]{1,2,4}) foreach(int type in new[]{1,2,3,4,5,9,19,22}) foreach(int count in new[]{1,5,50})
  {
   var plain=Run(0,false,type,count,divider);
   var extra=Run(4,false,type,count,divider);
   var acc=Run(4,true,type,count,divider);
   Check(Math.Abs((double)extra.product/plain.product-1.25)<0.001,"MkIII +25% type="+type);
   Check(Math.Abs((double)acc.product/plain.product-2)<0.001,"MkIII x2 type="+type);
   Check(Math.Abs(extra.consume-plain.consume)<3,"extra mode must not charge extra materials");
   Check(extra.Equals(Run(4,false,type,count,divider,false)),"batch/serial extra mismatch");
   Check(acc.Equals(Run(4,true,type,count,divider,false)),"batch/serial acceleration mismatch");
  }
  for(int level=0;level<=10;level++)
  {
   var a=Run(level,false);var b=Run(level,true);var zero=Run(0,false);
   Check(Math.Abs((double)a.product/zero.product-(1+Cargo.incTableMilli[level]))<0.001,"extra level "+level);
   Check(Math.Abs((double)b.product/zero.product-(1+Cargo.accTableMilli[level]))<0.001,"acc level "+level);
  }
  MegaThrottle.GlobalDivider=1;
  var fractional=Make(1,true);int total=0;
  for(int tick=0;tick<1000;tick++)total+=MegaProliferatorTiming.ScaleCycles(ref fractional,1,tick);
  Check(total==1250,"fractional acceleration lost");
  MegaThrottle.GlobalDivider=2;
  Check(MegaProliferatorTiming.CapacityCycles(120)>=421,"accelerated input buffer too small");
  foreach(bool acceleration in new[]{false,true})
  {
   var fed=Make(0,acceleration);Array.Clear(fed.served,0,fed.served.Length);
   for(int i=0;i<fed.served.Length;i++)
   {
    var source=new StationStore{count=1000,inc=4000};
    MegaInputTransfer.Move(ref source,ref fed,i,500);
    Check(source.inc==2000 && fed.incServed[i]==2000,"logistics feed lost spray");
   }
   fed.InternalUpdate(1,new int[32],new int[32]);
   // 原版 RefreshIncUIs 的两条显示表达式；不伪造面板数值。
   double display=acceleration?(double)fed.speedOverride*100/fed.speed-100:(double)fed.extraSpeed*10/fed.speed;
   Check(Math.Abs(display-(acceleration?100:25))<0.001,"panel still displays zero after sprayed logistics input");
  }
  Console.WriteLine("PASS: logistics-slot feed -> real InternalUpdate -> original panel formula: +25% extra / +100% speed.");
  var normal=Make(4,false,speed:10000);int[] pr=new int[32],cr=new int[32];
  normal.InternalUpdate(1,pr,cr);Check(normal.time==10000 && normal.extraTime==25000,"ordinary machine changed");
  var empty=Make(4,true);empty.served[1]=0;empty.incServed[1]=0;Check(MegaProliferatorTiming.ScaleCycles(ref empty,120,0)==120,"missing input bonus");empty.InternalUpdate(1,pr,cr);Check(cr[1]==1,"empty machine consumed input");
  var mixed=Make(4,true);mixed.incServed[1]=0;Check(MegaProliferatorTiming.ScaleCycles(ref mixed,120,0)==120,"unsprayed input bonus");
  var stopped=Make(4,false);stopped.replicating=true;stopped.time=600000;stopped.extraTime=123456;stopped.extraSpeed=250000000;MegaThrottle.Hold(ref stopped);stopped.InternalUpdate(1,pr,cr);Check(stopped.extraTime==123456,"hold changed earned extra progress");
  stopped.extraTime=-1500000000;MegaThrottle.Release(null,ref stopped);Check(stopped.extraTime==0,"old negative save not repaired");
  var blocked=Make(4,true);blocked.time=600000;blocked.replicating=true;blocked.produced[0]=100000;int before=blocked.served[0];for(int i=0;i<200;i++)blocked.InternalUpdate(1,pr,cr);Check(blocked.served[0]==before,"blocked output consumed materials");
  var off=Make(4,false);before=off.served[0];off.InternalUpdate(0.09f,pr,cr);Check(off.served[0]==before && off.extraTime==0,"unpowered production");
  StablePlanTests.Run();
  Console.WriteLine("PASS: actual Harmony timing + 9 output gates; actual game IL anchors; 8 recipe types x 3 yields x 3 dividers; MkIII +25% / x2, all 11 levels, serial/batch conservation, missing/unsprayed input, blocked output, power, hold, old negative progress and ordinary machines.");
 }
}
public class RecipeExecuteData {public int[] requires,requireCounts,products,productCounts;public int timeSpend,extraTimeSpend;public bool productive;}
public enum ERecipeType {None,Smelt,Chemical,Refine,Assemble,Particle}
class ProjectEdenPlugin {public static Logger Log=new Logger();}
class Logger {public void LogInfo(string s){}public void LogWarning(string s){}public void LogError(string s)=>throw new Exception(s);}
public class PlanetFactory {public EntityData[] entityPool;}
public struct EntityData {public int protoId;}
public static class LDB {public static ItemSet items=new ItemSet();}
public class ItemSet {public Item Select(int id)=>new Item();}
public class Item {public string name="test";}
namespace ProjectEden.Patches {
 static partial class MegaThrottle {internal static int GlobalDivider=2;internal static int DividerFor(PlanetFactory f,int id)=>GlobalDivider;internal static int GateCycles(ERecipeType type)=>60*GlobalDivider;static void ReportOnce(PlanetFactory f,int e,bool r,int t){}}
 static class MegaBuildingRegistry {internal static int MegaSpeedThreshold=100000000;internal static ConfigData Config=new ConfigData();}
 class ConfigData {public int cyclesPerTick=60;}
 static class MegaBatchSettle {internal static bool Enabled=true;internal static void DisableByAudit()=>Enabled=false;internal static void CountBatched(int n){}}
 static class QualityAccess {internal static bool CraftReady=false;internal static int GetQuaPending(ref AssemblerComponent c)=>0;internal static int GetQuaPendingItems(ref AssemblerComponent c)=>0;internal static int[] GetQuaServed(ref AssemblerComponent c)=>null;internal static int[] GetQuaProduced(ref AssemblerComponent c)=>null;}
}

public struct StationStore {public int count,inc;}
