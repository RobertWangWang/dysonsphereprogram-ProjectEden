using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ProjectEden.Patches;
using ProjectEden.Patches.Diagnostics;
static class ParallelIndexTests
{
    static void Check(bool value,string label){if(!value)throw new Exception(label);}
    static PlanetTransport World(int seed)
    {
        var r=new Random(seed);var pool=new StationComponent[2054];
        for(int id=1;id<pool.Length;id++){
            if(id%17==0)continue;
            var storage=new StationStore[id%3==0?40:30];
            for(int j=0;j<storage.Length;j++)storage[j]=new StationStore {itemId=r.Next(9,15),count=r.Next(100),max=r.Next(100),inc=r.Next(300),qua=r.Next(500),localOrder=r.Next(-20,20),remoteOrder=r.Next(-20,20),localLogic=(ELogisticStorage)r.Next(3)};
            pool[id]=new StationComponent{id=id%19==0?0:id,storage=storage};
        }
        var excs=new PowerExchangerComponent[101];var entities=new Entity[101];
        for(int i=1;i<excs.Length;i++){entities[i].protoId=1;excs[i]=new PowerExchangerComponent{id=i,entityId=i,emptyId=10+i%2*2,fullId=11+i%2*2,targetState=i%3-1,emptyCount=(short)r.Next(21),fullCount=(short)r.Next(21),emptyInc=(short)r.Next(100),fullInc=(short)r.Next(100)};}
        return new PlanetTransport {stationPool=pool,stationCursor=pool.Length,factory=new PlanetFactory{entityPool=entities,powerSystem=new PowerSystem{excPool=excs,excCursor=excs.Length}}};
    }
    static void Compare(int seed)
    {
        var a=World(seed);var b=World(seed);var list=new List<ParallelExchangerIndex.Candidate>();var wanted=new HashSet<int>{10,11,12,13};
        long time=seed*10;
        if(!ParallelExchangerIndex.TryBuild(a,wanted,time,list))throw new Exception("parallel path not used");
        var expected=new List<(StationStore[],int)>();int count=a.stationCursor-1,start=(int)(time%count);
        for(int k=0;k<count;k++){int id=1+(start+k)%count;var st=a.stationPool[id];if(st==null||st.id!=id)continue;var storage=st.storage;
            for(int j=0;j<storage.Length;j++)if(wanted.Contains(storage[j].itemId)&&storage[j].localLogic!=ELogisticStorage.None)expected.Add((storage,j));}
        Check(list.Count==expected.Count,"candidate count");for(int j=0;j<list.Count;j++)Check(ReferenceEquals(list[j].Storage,expected[j].Item1)&&list[j].Slot==expected[j].Item2,"candidate order");
        var current=typeof(MegaExchangerLogisticsPatches).GetMethod("Supply",BindingFlags.Static|BindingFlags.NonPublic);
        var reference=typeof(ExchangerReference).GetMethod("Supply",BindingFlags.Static|BindingFlags.NonPublic);
        for(int step=0;step<3;step++){
            if(step==1){a.stationPool[2].storage=new[]{new StationStore{itemId=10,count=123,inc=200,qua=321,localLogic=ELogisticStorage.Supply}};b.stationPool[2].storage=(StationStore[])a.stationPool[2].storage.Clone();}
            current.Invoke(null,new object[]{a,time+step*10});reference.Invoke(null,new object[]{b,time+step*10});
            for(int i=1;i<a.stationCursor;i++){if(a.stationPool[i]==null)continue;Check(a.stationPool[i].storage.SequenceEqual(b.stationPool[i].storage),"inventory/inc/quality/order mismatch");}
            Check(a.factory.powerSystem.excPool.SequenceEqual(b.factory.powerSystem.excPool),"exchanger result mismatch");
        }
    }
    internal static void Run()
    {
        CpuCostProbe.Config.parallelExchangerIndex=true;
        for(int seed=0;seed<100;seed++)Compare(seed);
        var world=World(123);var wanted=new HashSet<int>{10,11};var output=new List<ParallelExchangerIndex.Candidate>();
        CpuCostProbe.Config.parallelExchangerIndex=false;Check(!ParallelExchangerIndex.TryBuild(world,wanted,0,output),"disabled");CpuCostProbe.Config.parallelExchangerIndex=true;
        int original=world.stationCursor;world.stationCursor=100;Check(!ParallelExchangerIndex.TryBuild(world,wanted,0,output),"small world");world.stationCursor=original;
        // 锁住第一站，使一个任务保持运行，另一星球必须立即走串行。
        var storage=world.stationPool[1].storage;Task<bool> work;
        lock(storage){work=Task.Run(()=>ParallelExchangerIndex.TryBuild(world,wanted,0,new List<ParallelExchangerIndex.Candidate>()));
            var busy=typeof(ParallelExchangerIndex).GetField("_busy",BindingFlags.Static|BindingFlags.NonPublic);
            Check(SpinWait.SpinUntil(()=>(int)busy.GetValue(null)==1,5000),"worker start timeout");
            Check(!ParallelExchangerIndex.TryBuild(World(124),wanted,0,output),"busy fallback");}
        Check(work.GetAwaiter().GetResult(),"worker completion");
        var recorder=new RecordingComparer();var tracked=new HashSet<int>(recorder){10,11};recorder.Threads.Clear();
        Check(ParallelExchangerIndex.TryBuild(world,tracked,0,output), "recorded run");
        Check(recorder.Threads.Count==3,"caller plus two helper threads not observed");
        output.Clear();
        var comparer=new ThrowComparer();var invalid=new HashSet<int>(comparer){10};comparer.Throw=true;
        try{ParallelExchangerIndex.TryBuild(world,invalid,0,output);throw new Exception("exception swallowed");}catch(InvalidOperationException){}
        Check(!ParallelExchangerIndex.TryBuild(world,wanted,0,output),"session failure fallback");
        CpuCostProbe.Config.parallelExchangerIndex=false;
        Console.WriteLine("PASS: 100 large worlds x 3 rounds match frozen serial implementation; exact candidate order, 2053 stations, 30/40 slots, gaps, modes, reservations, quality, replacement, busy/off/small and exception barrier/fallback.");
    }
    sealed class RecordingComparer:IEqualityComparer<int>{internal readonly System.Collections.Concurrent.ConcurrentDictionary<int,byte> Threads=new System.Collections.Concurrent.ConcurrentDictionary<int,byte>();public bool Equals(int a,int b)=>a==b;public int GetHashCode(int a){Threads.TryAdd(Thread.CurrentThread.ManagedThreadId,0);return a;}}
    sealed class ThrowComparer:IEqualityComparer<int>{internal bool Throw;public bool Equals(int a,int b)=>a==b;public int GetHashCode(int a){if(Throw)throw new InvalidOperationException();return a;}}
}
