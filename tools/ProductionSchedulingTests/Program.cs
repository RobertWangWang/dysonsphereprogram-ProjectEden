using System;
using System.Linq;
using System.Threading;
using HarmonyLib;
using ProjectEden.Patches;
using ProjectEden.Patches.Diagnostics;
class Program
{
    static void Check(bool v,string m){if(!v)throw new Exception(m);}
    static GameLogic Setup(int total,int workers)
    {
        var ctx=new ScatterTaskContext();ctx.Init(4,workers);ctx.ResetFrame(3,workers);
        ctx.ordinals[1]=total/4;ctx.ordinals[2]=total/2;ctx.ordinals[3]=total;ctx.DetermineThreadTasks();
        return new GameLogic {threadController=new Controller {gameThreadContext=new Context {assembler=ctx}},Counts=new int[total],Batch=new int[workers],Chances=new int[workers]};
    }
    static void Main()
    {
        var h=new Harmony("eden.production.balance.test");h.CreateClassProcessor(typeof(ProductionSchedulingPatches)).Patch();h.CreateClassProcessor(typeof(ProductionRedispatchProbe)).Patch();
        foreach(bool on in new[]{false,true})foreach(int n in new[]{1024,4096,16001})foreach(int workers in new[]{2,4,8})
        {
            CpuCostProbe.Config.balanceProductionWorkers=on;
            var game=Setup(n,workers);using(var gate=new ManualResetEventSlim()){
                var threads=Enumerable.Range(0,workers).Select(id=>new Thread(()=>{gate.Wait();game.Run(id); } )).ToArray();
                foreach(var t in threads)t.Start();gate.Set();foreach(var t in threads)t.Join();
            }
            Check(game.Counts.All(x=>x==1),"duplicate or missing work");bool tuned=on&&n>=4096&&workers>=4;
            Check(game.Batch.All(x=>x==(tuned?16:24+workers))&&game.Chances.All(x=>x==(tuned?8:2)),"parameter policy");
        }
        // 延迟大任务的拥有者，让其他线程实际窃取，不能只证明参数发生变化。
        CpuCostProbe.Config.balanceProductionWorkers=true;ProductionSchedulingPatches.Take();
        var forced=Setup(16000,4);for(int i=1;i<4;i++)forced.Run(i);forced.Run(0);
        Check(forced.Counts.All(x=>x==1),"redispatch lost work");var stats=ProductionSchedulingPatches.Take();Check(stats[1]>0&&stats[2]>0&&stats[4]==0,"no actual redispatch observed");
        var fail=Setup(4096,4);fail.Fail=true;try{fail.Run(0);throw new Exception("swallowed");}catch(InvalidOperationException){}
        Check(ProductionSchedulingPatches.Take()[4]==1,"exception cleanup missing");
        fail.Fail=false;fail.Run(1);Check(ProductionSchedulingPatches.Take()[4]==0,"exception polluted next worker");
        Console.WriteLine("PASS: real Harmony parameter injection, original decompiled Redispatch/SimpleLock, 18 concurrent workloads across 3 factories, exactly-once work, actual stealing, thresholds/off and exception cleanup. Worker body is a scheduling harness, not game production.");
    }
}
class GameLogic
{
    public Controller threadController;public int[] Counts,Batch,Chances;public bool Fail;
    public void Run(int id)=>_assembler_parallel(id,24+Batch.Length,3,2);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    void _assembler_parallel(int threadOrdinal,int workBatchSize,int extraProtectedSize,int maxRedispatchChance)
    {
        Batch[threadOrdinal]=workBatchSize;Chances[threadOrdinal]=maxRedispatchChance;
        if(Fail)throw new InvalidOperationException();
        var ctx=threadController.gameThreadContext.assembler;ref var tc=ref ctx.threadContexts[threadOrdinal];
        int remaining=maxRedispatchChance;bool run=tc.workCount>0;
        while(run){
            while(true){
                tc.occupy.Enter();int begin=tc.ordinalCurrent,end=Math.Min(begin+workBatchSize,tc.ordinalEnd);tc.occupy.Exit();
                if(begin>=end)break;
                for(int i=begin;i<end;i++){Interlocked.Increment(ref Counts[i]);if((i%11)==0)Thread.SpinWait(50);}
                tc.occupy.Enter();tc.ordinalCurrent=end;tc.occupy.Exit();
            }
            run=false;if(remaining!=0){remaining--;run=ctx.Redispatch(threadOrdinal,workBatchSize,extraProtectedSize);}
        }
    }
}
class Controller {public Context gameThreadContext;}
class Context {public ScatterTaskContext assembler;}
public enum DPEntry {Scheduling}
public static class DeepProfiler {public static void BeginSample(DPEntry e,int t,long x){}public static void EndSample(int t,long x){}}
class ProjectEdenPlugin {public static LogStub Log=new LogStub();}
class LogStub {public void LogInfo(string s){}}
namespace ProjectEden.Patches.Diagnostics {static class CpuCostProbe {internal static Config Config=new Config();}class Config {internal bool balanceProductionWorkers,systemTiming=true;}}
