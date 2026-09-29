using System;
using ProjectEden.Patches;
class Program
{
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);}
 static void Main(){
  var r=new Random(927);int accepted=0;
  for(int test=0;test<40000;test++){
   int inputs=r.Next(1,5),outputs=r.Next(1,4),level=r.Next(11);
   var c=new AssemblerComponent {speed=100000000,recipeType=(ERecipeType)r.Next(1,6),forceAccMode=r.Next(2)==0,incUsed=r.Next(2)==0,replicating=true,served=new int[inputs],incServed=new int[inputs],produced=new int[outputs],needs=new int[6],recipeExecuteData=new RecipeExecuteData{requireCounts=new int[inputs],requires=new int[inputs],productCounts=new int[outputs],products=new int[outputs],timeSpend=r.Next(1,100)*10000,extraTimeSpend=r.Next(1,200)*10000,productive=r.Next(2)==0}};
   for(int i=0;i<inputs;i++){c.served[i]=r.Next(1,5000);c.incServed[i]=c.served[i]*(test%3==0?r.Next(11):level);c.recipeExecuteData.requires[i]=i+1;c.recipeExecuteData.requireCounts[i]=r.Next(1,6);}
   for(int i=0;i<outputs;i++){c.produced[i]=r.Next(0,130);c.recipeExecuteData.products[i]=i+10;c.recipeExecuteData.productCounts[i]=r.Next(1,5);}
   c.time=r.Next(-100000,3000000);c.extraTime=r.Next(-100000,5000000);
   float power=new[]{0.05f,0.1f,0.3f,0.7f,1f}[r.Next(5)];
   // 真正跑过一次，获取当前模式的速度/功耗；不人为伪造稳态。
   c.InternalUpdate(power,new int[32],new int[32]);
   int budget=r.Next(2,240);
   var plan=MegaProliferatorBatch.MakePlan(ref c,budget,power);
   if(plan.Count>0){accepted++;Check(MegaProliferatorBatch.Verify(ref c,plan,power,32,32),"回放不一致 test="+test);}
   var baseline=MegaProliferatorBatch.Clone(c);var optimized=MegaProliferatorBatch.Clone(c);
   var pr=new int[32];var cr=new int[32];var bp=new int[32];var bc=new int[32];
   int count=0;
   for(int k=0;k<budget-1;k++){int before=baseline.produced[0];baseline.InternalUpdate(power,pr,cr);if(baseline.produced[0]!=before)count++;}
   int got=Pipeline.Run(ref optimized,budget,power,bp,bc);
   Check(count==got,"settled错误 "+test);
   Check(baseline.time==optimized.time && baseline.extraTime==optimized.extraTime && baseline.cycleCount==optimized.cycleCount && baseline.extraCycleCount==optimized.extraCycleCount && baseline.incUsed==optimized.incUsed && baseline.replicating==optimized.replicating && baseline.speedOverride==optimized.speedOverride && baseline.extraSpeed==optimized.extraSpeed && baseline.extraPowerRatio==optimized.extraPowerRatio && MegaProliferatorBatch.Equal(baseline.served,optimized.served) && MegaProliferatorBatch.Equal(baseline.incServed,optimized.incServed) && MegaProliferatorBatch.Equal(baseline.produced,optimized.produced) && MegaProliferatorBatch.Equal(pr,bp) && MegaProliferatorBatch.Equal(cr,bc),"编排状态不一致 "+test);
  }
  var edge=new AssemblerComponent {speed=100000000,recipeType=ERecipeType.Assemble,replicating=true,time=10000,served=new[]{10000},incServed=new[]{40000},produced=new[]{0},needs=new int[6],recipeExecuteData=new RecipeExecuteData{requires=new[]{1},requireCounts=new[]{1},products=new[]{10},productCounts=new[]{1},timeSpend=10000,extraTimeSpend=40000,productive=true}};
  edge.InternalUpdate(1,new int[32],new int[32]);
  var valid=MegaProliferatorBatch.MakePlan(ref edge,60,1);
  Check(valid.Count>0,"边界样本没有命中");
  var seed=MegaProliferatorBatch.Clone(edge);
  System.Threading.Tasks.Parallel.For(0,1000,k=>{
   var local=MegaProliferatorBatch.Clone(seed);
   int n=MegaProliferatorBatch.TryApply(ref local,60,1,new int[32],new int[32]);
   Check(n==valid.Count && local.time==valid.Time && local.extraTime==valid.ExtraTime,"多线程批量状态串扰");
  });
  var corrupt=valid;corrupt.Time++;
  Check(!MegaProliferatorBatch.Verify(ref edge,corrupt,1,32,32),"自检未发现进度错误");
  edge.incServed[0]++;
  Check(MegaProliferatorBatch.MakePlan(ref edge,60,1).Count==0,"混合点数未回退");edge.incServed[0]--;
  QualityAccess.CraftReady=true;
  Check(MegaProliferatorBatch.MakePlan(ref edge,60,1).Count==0,"品质未回退");QualityAccess.CraftReady=false;
  edge.recipeType=(ERecipeType)9;
  Check(MegaProliferatorBatch.MakePlan(ref edge,60,1).Count==0,"特殊配方未回退");edge.recipeType=ERecipeType.Assemble;
  edge.produced[0]=int.MaxValue;
  Check(MegaProliferatorBatch.MakePlan(ref edge,60,1).Count==0,"溢出边界未回退");
  // 四条真实配方的时间与产物数；以满供料/满供电、G=2的120周期预算验证旧批量路径。
  for(int type=19;type<=22;type++){
   int[] amounts=type==19?new[]{8,4}:type==20?new[]{2,6}:type==21?new[]{2}:new[]{10,1};
   var line=new AssemblerComponent{speed=100000000,speedOverride=100000000,recipeType=(ERecipeType)type,replicating=true,served=new[]{1000000},incServed=new[]{0},produced=new int[amounts.Length],needs=new int[6],recipeExecuteData=new RecipeExecuteData{requires=new[]{1},requireCounts=new[]{1},products=amounts.Length==1?new[]{10}:new[]{10,11},productCounts=amounts,timeSpend=(type==19?2100:type==20?600:480)*10000}};
   line.recipeExecuteData.extraTimeSpend=line.recipeExecuteData.timeSpend*10;
   var pr=new int[32];var cr=new int[32];
   for(int frame=0;frame<1800;frame++){
    Array.Clear(line.produced);line.time=line.recipeExecuteData.timeSpend;
    var before=(int[])line.served.Clone();var products=(int[])line.produced.Clone();int cycles=line.cycleCount,extras=line.extraCycleCount;
    line.InternalUpdate(1,pr,cr);
    Check(LegacyBatchReference.IsSteadyUnit(ref line,before,products,cycles,extras),"反物质配方不稳态");
    int n=LegacyBatchReference.BatchSize(ref line,118);
    Check(n==118,"反物质批量预算被截断");
    LegacyBatchReference.Apply(ref line,n,pr,cr);line.InternalUpdate(1,pr,cr);
   }
   Check(line.cycleCount==216000 && pr[10]==216000*amounts[0],"反物质一分钟产量不对");
  }
  var factory=new PlanetFactory{entityPool=new[]{new EntityData(),new EntityData{protoId=6683}}};
  var oldMachine=new AssemblerComponent{entityId=1,speed=10000};
  Check(SpeedGate.Check(factory,ref oldMachine)&&oldMachine.speed==100000000,"旧存档巨型建筑未恢复速度");
  factory.entityPool[1].protoId=2303;oldMachine.speed=10000;
  Check(!SpeedGate.Check(factory,ref oldMachine)&&oldMachine.speed==10000,"普通建筑被错误提速");
  Console.WriteLine("PASS: 反物质四配方满载每逻辑分钟216000周期；旧速度恢复且普通建筑不变。");
  Check(accepted>1000,"没有覆盖足够批量样本");
  Console.WriteLine($"PASS: 40000组原版逐次对照，{accepted}组可批量；含真实补跑编排/settled、库存/增产点/两个计时器/统计/模式。");
 }
}
public class RecipeExecuteData {public int[] requires,requireCounts,products,productCounts;public int timeSpend,extraTimeSpend;public bool productive;}
public enum ERecipeType {None,Smelt,Chemical,Refine,Assemble,Particle}
static class ProjectEdenPlugin {public static Logger Log=new Logger();}
class Logger {public void LogInfo(string s){} public void LogError(string s)=>throw new Exception(s);}
namespace ProjectEden.Patches {
 static class MegaOutputGatePatches {internal static int Scale(int x,ref AssemblerComponent c)=>119;}
 static class MegaBatchSettle {internal static bool Enabled=true;internal static void DisableByAudit()=>Enabled=false;internal static void CountBatched(int n){} internal static void CountStepped(int n){} }
 static class QualityAccess {internal static bool CraftReady=false;internal static int GetQuaPending(ref AssemblerComponent c)=>0;internal static int GetQuaPendingItems(ref AssemblerComponent c)=>0;internal static int[] GetQuaServed(ref AssemblerComponent c)=>new[]{1};internal static int[] GetQuaProduced(ref AssemblerComponent c)=>null;}
 static class MegaTickProfiler {internal static long Now()=>0;internal static void AddInner(long t){} internal static void AddCalls(int n){}}
}
namespace ProjectEden.Patches.Diagnostics {static class NanosecondProbe {internal static long Now()=>0;internal static void End(int i,long t){}}}

public class PlanetFactory {public EntityData[] entityPool;}
public struct EntityData {public int protoId;}
public static class LDB {public static ItemSet items=new ItemSet();}
public class ItemSet {public Item Select(int id)=>new Item{prefabDesc=new Prefab{assemblerSpeed=id==6683?100000000:10000}};}
public class Item {public Prefab prefabDesc;}
public class Prefab {public int assemblerSpeed;}
namespace ProjectEden.Patches {
 static class MegaBuildingRegistry {internal static Settings Config=new Settings();internal static int MegaSpeedThreshold=100000000;}
 class Settings {public int assemblerSpeed=100000000;public bool batchSettle=true;}
}
