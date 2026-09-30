using System;
using System.Linq;
using System.Threading.Tasks;
using ProjectEden.Patches;
class Program
{
    static void Check(bool v, string m) { if (!v) throw new Exception(m); }
    static void Main()
    {
        TestSprayedFeed();
        Parallel.For(0, 10000, Compare);
        var s = Enumerable.Range(0,30).Select(i => new StationStore { itemId = i < 3 ? i+1 : 0, max=100 }).ToArray();
        Check(MegaStationPatches.HasDirectLayout(s,new[]{1,2},new[]{3}), "稳定布局未启用");
        s[2].itemId=1;
        Check(!MegaStationPatches.HasDirectLayout(s,new[]{1,2},new[]{1}), "同物进出未回退");
        Console.WriteLine("PASS: 10000 parallel worlds x 12 rounds match frozen production-storage implementation, including recipe/slot changes, leftovers, mode/capacity changes, duplicate items, catalyst fallback, quality and stock conservation.");
        Benchmark();
    }
    static PlanetFactory Factory(StationStore[] storage, bool reactor) => new PlanetFactory {
        entityPool = new[]{new Entity(),new Entity {stationId=1,protoId=42}},
        transport=new PlanetTransport {stationPool=new[]{null,new StationComponent {id=1,entityId=1,storage=storage,workDroneDatas=new DroneData[10],energyMax=1000}}},Reactor=reactor };
    static AssemblerComponent Component(int[] req, int[] prod) => new AssemblerComponent { entityId=1,recipeId=1,
        recipeExecuteData=new RecipeExecuteData {requires=req,products=prod,requireCounts=req.Select(x=>2).ToArray()},
        served=new int[req.Length],incServed=new int[req.Length],produced=prod.Select(x=>11).ToArray(),quaProduced=prod.Select(x=>37).ToArray() };
    static void Compare(int seed)
    {
        var r=new Random(seed); int length=new[]{5,12,30,40}[seed%4];
        int[] req=seed%7==0?new[]{1,1}:new[]{1,2}; int[] prod=seed%5==0?new[]{1,3}:new[]{3,4};
        var storage=new StationStore[length];
        for(int i=0;i<length;i++) storage[i]=new StationStore {itemId=i<4?(i<2?req[i]:prod[i-2]):0,count=r.Next(100),inc=0,qua=r.Next(500),max=100,localLogic=(ELogisticStorage)r.Next(3),remoteLogic=(ELogisticStorage)r.Next(3)};
        if(seed%3==0) for(int i=0;i<length;i++){storage[i].itemId=r.Next(7);storage[i].max=r.Next(2)==0?0:100;storage[i].count=r.Next(2)==0?0:storage[i].count;}
        var a=Factory((StationStore[])storage.Clone(),seed%11==0);var b=Factory((StationStore[])storage.Clone(),seed%11==0);
        var ca=Component((int[])req.Clone(),(int[])prod.Clone()); var cb=Component((int[])req.Clone(),(int[])prod.Clone());
        for(int tick=0;tick<12;tick++)
        {
            if(tick==3){ca.recipeExecuteData.requires[0]=cb.recipeExecuteData.requires[0]=5;}
            if(tick==4){a.transport.stationPool[1].storage=(StationStore[])a.transport.stationPool[1].storage.Clone(); b.transport.stationPool[1].storage=(StationStore[])b.transport.stationPool[1].storage.Clone();}
            int slot=r.Next(length); var sa=a.transport.stationPool[1].storage; var sb=b.transport.stationPool[1].storage;
            if(tick==5) sa[slot].itemId=sb[slot].itemId=6;
            if(tick==6) sa[slot].localLogic=sb[slot].localLogic=ELogisticStorage.Demand;
            if(tick==7) sa[slot].max=sb[slot].max=0;
            if(tick==8) for(int i=0;i<length;i++) sa[i].count=sb[i].count=0;
            if(tick==9) ca.recipeExecuteData.products[0]=cb.recipeExecuteData.products[0]=2;
            for(int i=0;i<ca.produced.Length;i++){ca.produced[i]+=3;cb.produced[i]+=3;ca.quaProduced[i]+=7;cb.quaProduced[i]+=7;}
            MegaStationPatches.UpdateStationStorage(a,ref ca);MegaStationReference.UpdateStationStorage(b,ref cb);
            for(int i=0;i<length;i++)Check(sa[i].Equals(sb[i]),$"storage seed={seed} tick={tick} slot={i}");
            Check(ca.served.SequenceEqual(cb.served)&&ca.produced.SequenceEqual(cb.produced)&&ca.quaProduced.SequenceEqual(cb.quaProduced),$"buffers seed={seed} tick={tick}");
            Check(a.Dirty==b.Dirty,$"dirty seed={seed} tick={tick}");
        }
    }
    static void TestSprayedFeed()
    {
        foreach(bool fallback in new[]{false,true})
        {
            var storage=new StationStore[30];
            storage[0]=new StationStore{itemId=1,count=1000,inc=4000,max=10000,localLogic=ELogisticStorage.Demand};
            storage[1]=new StationStore{itemId=2,max=10000,localLogic=ELogisticStorage.Supply};
            if(fallback){storage[4]=storage[0];storage[0]=new StationStore();}
            var f=Factory(storage,false);var c=Component(new[]{1},new[]{2});
            MegaStationPatches.UpdateStationStorage(f,ref c);
            Check(c.served[0]==240 && c.incServed[0]==960,"station spraying did not reach assembler");
            int slot=fallback?4:0;
            Check(storage[slot].count==760 && storage[slot].inc==3040,"station points not debited");
            MegaStationPatches.UpdateStationStorage(f,ref c);
            Check(c.incServed[0]==960 && storage[slot].inc==3040,"full buffer duplicated points");
        }
        var random=new Random(1001);
        for(int n=0;n<10000;n++)
        {
            int count=random.Next(1,1000000),take=random.Next(1,count+1),points=random.Next(0,count*10+1);
            var source=new StationStore{count=count,inc=points};var c=Component(new[]{1},new[]{2});
            MegaInputTransfer.Move(ref source,ref c,0,take);
            Check((long)source.inc+c.incServed[0]==points && source.count+c.served[0]==count,"transfer conservation");
            if(source.count>0)MegaInputTransfer.Move(ref source,ref c,0,source.count);
            Check(source.inc==0 && c.incServed[0]==points,"drain lost remainder");
        }
        var large=new StationStore{count=1000000000,inc=2000000000};var target=Component(new[]{1},new[]{2});
        MegaInputTransfer.Move(ref large,ref target,0,500000000);
        Check(large.inc==1000000000 && target.incServed[0]==1000000000,"intermediate multiplication overflow");
        Console.WriteLine("PASS: actual station direct/fallback feeding carries MkIII points; partial/full/zero/mixed/large transfers conserve all points.");
    }
    static void Benchmark()
    {
        var storage=new StationStore[30];for(int i=0;i<4;i++)storage[i]=new StationStore {itemId=i+1,max=10000000,count=100000,localLogic=i<2?ELogisticStorage.Demand:ELogisticStorage.Supply};
        var a=Factory((StationStore[])storage.Clone(),false);var b=Factory((StationStore[])storage.Clone(),false);var ca=Component(new[]{1,2},new[]{3,4});var cb=Component(new[]{1,2},new[]{3,4});
        for(int i=0;i<20000;i++){MegaStationPatches.UpdateStationStorage(a,ref ca);MegaStationReference.UpdateStationStorage(b,ref cb);}
        for(int pass=0;pass<4;pass++){
            var watch=System.Diagnostics.Stopwatch.StartNew();for(int i=0;i<300000;i++)MegaStationReference.UpdateStationStorage(b,ref cb);double old=watch.Elapsed.TotalMilliseconds;
            watch.Restart();for(int i=0;i<300000;i++)MegaStationPatches.UpdateStationStorage(a,ref ca);double now=watch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Offline steady layout 300000 calls: old={old:F1}ms new={now:F1}ms (not Unity FPS).");
        }
    }
}
public enum ELogisticStorage {None, Supply, Demand}
public struct StationStore {public int itemId,count,max,inc,qua;public ELogisticStorage localLogic,remoteLogic;}
public class DroneData {}
public class StationComponent {public int id,entityId,workDroneCount,idleDroneCount,deliveryDrones,localPairCount;public long energy,energyMax;public bool droneAutoReplenish;public DroneData[] workDroneDatas;public StationStore[] storage;}
public struct Entity {public int stationId,protoId;}
public class PlanetFactory {public Entity[] entityPool;public PlanetTransport transport;public bool Reactor;public int Dirty;}
public class PlanetTransport {public StationComponent[] stationPool;}
public class RecipeExecuteData {public int[] requires,requireCounts,products;}
public struct AssemblerComponent {public int entityId,recipeId;public RecipeExecuteData recipeExecuteData;public int[] served,incServed,produced,quaProduced;}
public class ItemProto {public string name="test";}
public class ItemSet {public ItemProto Select(int id)=>new ItemProto();}
public static class LDB {public static ItemSet items=new ItemSet();}
public static class ProjectEdenPlugin {public static Logger Log=new Logger();}
public class Logger {public void LogInfo(string s){}public void LogWarning(string s){}}
namespace ProjectEden {
public class MegaBuildingsConfig {public bool stationEnabled=true;public int stationMaxItemCount=10000000,deliveryDronePercent=100,requireStockMultiplier=5,cyclesPerTick=60;}
public static class MegaBuildingRegistry {public static MegaBuildingsConfig Config=new MegaBuildingsConfig();}}
namespace ProjectEden.Patches {
public static class StationExpandPatches {public static int ExpandedIconKinds=30;}
public static class MegaThrottle {public static int CyclesFor(PlanetFactory f,int id,int n)=>n*2;}
public static class StationTrafficCoalescer {public static void MarkDirty(PlanetFactory f){f.Dirty++;}}
public static class StationConfiguredSlots {public static void Invalidate(StationStore[] s){}}
public static class QualityCraftOut {public static int DrainSlot(ref AssemblerComponent a,int i,int give,int count){int q=(int)((long)a.quaProduced[i]*give/count);a.quaProduced[i]-=q;return q;}}
public static class QualityRefineryPatches {public static void OnProduced(ref StationStore s,int recipe,int give){s.qua+=give;}}
public static class CatalystBedPatches {
public static bool IsReactor(PlanetFactory f,int id)=>f.Reactor;
public static bool ClaimSlots(StationComponent s,int length,ref long claimed){bool changed=false;for(int i=0;i<length;i++)if((claimed&(1L<<i))==0&&s.storage[i].count<=0){s.storage[i].itemId=99;s.storage[i].localLogic=ELogisticStorage.Demand;claimed|=1L<<i;changed=true;break;}return changed;}}
}
namespace ProjectEden.Utils {public static class QualityAccess {public static bool Ready=true;public static int GetStationQua(ref StationStore s)=>s.qua;public static void SetStationQua(ref StationStore s,int q){s.qua=q;}public static void GiveStationQua(ref StationStore s,int q){s.qua+=q;}}}

public static class MegaProliferatorTiming {public static int CapacityCycles(int n)=>n;}
