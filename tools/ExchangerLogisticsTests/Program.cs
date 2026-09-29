using System;
using System.Reflection;
using ProjectEden.Patches;
namespace HarmonyLib {public class HarmonyPostfix:Attribute{} public class HarmonyPatch:Attribute {public HarmonyPatch(){} public HarmonyPatch(Type t,string s){}}}
namespace ProjectEden {public static class MegaBuildingRegistry {public static Config Config=new Config();} public class Config {public bool virtualLogistics=true;public int virtualIntervalTicks=10;public Entry[] buildings=new[]{new Entry()};} public class Entry {public int itemId=1;public object exchanger=new object();public string displayName="奇点储能厂";}}
namespace ProjectEden.Utils {public static class QualityAccess {public static bool Ready=true;public static int GetStationQua(ref StationStore s)=>s.qua;public static void SetStationQua(ref StationStore s,int n)=>s.qua=n;}}
public enum ELogisticStorage {None,Supply,Demand}
public struct StationStore {public int itemId,count,inc,qua,localOrder,remoteOrder,max;public ELogisticStorage localLogic;}
public class StationComponent {public int id=1;private StationStore[] _storage;public static int Reads;public StationStore[] storage {get{System.Threading.Interlocked.Increment(ref Reads);return _storage;}set{_storage=value;}}}
public class PlanetTransport {public PlanetFactory factory;public int stationCursor=2;public StationComponent[] stationPool;public void GameTick(){}}
public class PlanetFactory {public PowerSystem powerSystem;public Entity[] entityPool=new[]{default(Entity),new Entity{protoId=1}};}
public struct Entity {public int protoId;}
public class PowerSystem {public int excCursor=2;public PowerExchangerComponent[] excPool;}
public struct PowerExchangerComponent {public int id,entityId,emptyId,fullId;public float targetState;public short emptyCount,fullCount,emptyInc,fullInc;}
class Program {
 static void Check(bool ok){if(!ok)throw new Exception("exchanger logistics regression");}
 static void Main(){
  var e=new PowerExchangerComponent{id=1,entityId=1,emptyId=10,fullId=11,targetState=1};
  var power=new PowerSystem{excPool=new[]{default(PowerExchangerComponent),e}};
  var slots=new[]{new StationStore{itemId=10,count=100,inc=400,qua=500,localLogic=ELogisticStorage.Supply},new StationStore{itemId=11,count=100,inc=400,localLogic=ELogisticStorage.Supply},new StationStore{itemId=12,count=100,localLogic=ELogisticStorage.Supply}};
  var t=new PlanetTransport{factory=new PlanetFactory{powerSystem=power},stationPool=new[]{null,new StationComponent{storage=slots}}};
  var m=typeof(MegaExchangerLogisticsPatches).GetMethod("Supply",BindingFlags.Static|BindingFlags.NonPublic);
  void Tick(long time=10)=>m.Invoke(null,new object[]{t,time});
  Tick();Check(power.excPool[1].emptyCount==20 && power.excPool[1].emptyInc==80 && slots[0].count==80 && slots[0].inc==320 && slots[0].qua==400 && slots[1].count==100);
  Tick();Check(slots[0].count==80);
  power.excPool[1]=e;power.excPool[1].targetState=-1;Tick();Check(power.excPool[1].fullCount==20 && slots[1].count==80);
  power.excPool[1]=e;power.excPool[1].emptyId=12;Tick();Check(slots[2].count==80 && slots[0].count==80);
  power.excPool[1]=e;power.excPool[1].targetState=0;Tick();Check(power.excPool[1].emptyCount==0);
  power.excPool[1]=e;slots[0].localOrder=-75;Tick();Check(power.excPool[1].emptyCount==5 && slots[0].count==75);
  power.excPool[1]=e;slots[0].localOrder=0;slots[0].localLogic=ELogisticStorage.Demand;Tick();Check(power.excPool[1].emptyCount==0);
  slots[0].localLogic=ELogisticStorage.Supply;ProjectEden.MegaBuildingRegistry.Config.virtualLogistics=false;Tick();Check(power.excPool[1].emptyCount==0);
  ProjectEden.MegaBuildingRegistry.Config.virtualLogistics=true;Tick(11);Check(power.excPool[1].emptyCount==0);
  t.factory.entityPool[1].protoId=2;Tick();Check(power.excPool[1].emptyCount==0);
  t.factory.entityPool[1].protoId=1;
  power.excPool[1]=e;power.excPool[1].emptyCount=20;power.excPool[1].fullCount=20;power.excPool[1].fullInc=80;
  slots[1]=new StationStore{itemId=11,count=90,max=100,inc=360,localOrder=4,remoteOrder=2,localLogic=ELogisticStorage.Demand};
  Tick();Check(slots[1].count==94 && slots[1].inc==376 && power.excPool[1].fullCount==16 && power.excPool[1].fullInc==64);
  Tick();Check(slots[1].count==94 && power.excPool[1].fullCount==16);
  slots[1].localOrder=0;slots[1].remoteOrder=0;slots[1].max=200;
  Tick();Check(slots[1].count==110 && slots[1].inc==440 && power.excPool[1].fullCount==0 && power.excPool[1].fullInc==0);
  Tick();Check(slots[1].count==110);
  power.excPool[1]=e;power.excPool[1].targetState=-1;power.excPool[1].emptyCount=10;power.excPool[1].emptyInc=40;
  slots[0]=new StationStore{itemId=10,max=100,localLogic=ELogisticStorage.Demand};
  Tick();Check(slots[0].count==10 && slots[0].inc==40 && power.excPool[1].emptyCount==0 && power.excPool[1].fullCount==0);
  power.excPool[1]=e;power.excPool[1].fullCount=7;power.excPool[1].fullInc=28;
  slots[1].localLogic=ELogisticStorage.Supply;Tick();Check(power.excPool[1].fullCount==7);
  slots[1].localLogic=ELogisticStorage.Demand;ProjectEden.MegaBuildingRegistry.Config.virtualLogistics=false;Tick();Check(power.excPool[1].fullCount==7);
  ProjectEden.MegaBuildingRegistry.Config.virtualLogistics=true;power.excPool[1].targetState=0;
  power.excPool[1].emptyCount=3;power.excPool[1].emptyInc=12;Tick();Check(power.excPool[1].emptyCount==0 && power.excPool[1].fullCount==0 && slots[0].count==13 && slots[1].count==117);
  // 100 座储能厂、1000 个物流站：全站槽位读取次数不应随储能厂数量相乘。
  PlanetTransport LargeWorld(int stations, int exchangers) {
   var entities=new Entity[exchangers+1];var excs=new PowerExchangerComponent[exchangers+1];
   for(int i=1;i<=exchangers;i++){entities[i].protoId=1;excs[i]=new PowerExchangerComponent{id=i,entityId=i,emptyId=10,fullId=11,targetState=1,fullCount=20,fullInc=80};}
   var pool=new StationComponent[stations+1];
   for(int i=1;i<=stations;i++)pool[i]=new StationComponent{id=i,storage=new StationStore[30]};
   pool[stations].storage[0]=new StationStore{itemId=10,count=exchangers*20,inc=exchangers*80,localLogic=ELogisticStorage.Supply};
   pool[stations].storage[1]=new StationStore{itemId=11,max=exchangers*20,localLogic=ELogisticStorage.Demand};
   return new PlanetTransport{stationCursor=pool.Length,stationPool=pool,factory=new PlanetFactory{entityPool=entities,powerSystem=new PowerSystem{excCursor=excs.Length,excPool=excs}}};
  }
  void VerifyWorld(PlanetTransport w,int n){
   m.Invoke(null,new object[]{w,10L});
   for(int i=1;i<=n;i++)Check(w.factory.powerSystem.excPool[i].emptyCount==20 && w.factory.powerSystem.excPool[i].emptyInc==80 && w.factory.powerSystem.excPool[i].fullCount==0);
   var last=w.stationPool[w.stationCursor-1].storage;Check(last[0].count==0 && last[0].inc==0 && last[1].count==n*20 && last[1].inc==n*80);
  }
  var large=LargeWorld(1000,100);StationComponent.Reads=0;
  VerifyWorld(large,100);Check(StationComponent.Reads<=3001);
  Console.WriteLine("PASS indexed scale: 100 exchangers / 1000 stations / 30000 slots, storage reads="+StationComponent.Reads);
  // 上一轮已经取空；重新配槽后必须重建索引，而不是留着旧槽位或游标。
  large.stationPool[1000].storage=new[]{new StationStore{itemId=12,count=100,localLogic=ELogisticStorage.Supply}};
  large.factory.powerSystem.excPool[1].emptyId=12;large.factory.powerSystem.excPool[1].emptyCount=0;
  m.Invoke(null,new object[]{large,20L});Check(large.factory.powerSystem.excPool[1].emptyCount==20 && large.stationPool[1000].storage[0].count==80);
  System.Threading.Tasks.Parallel.For(0,16,i=>VerifyWorld(LargeWorld(20,10),10));
  Console.WriteLine("PASS per-round rebuild, tier change, concurrent planets and no stock leakage");
  Console.WriteLine("PASS: modes, tier, buffer, item/inc conservation, order reservation, switch, interval vanilla exclusion, output conservation, incoming reservations, blocked output, discharge return and idle draining");
 }
}

namespace ProjectEden.Patches.Diagnostics { internal static class TransportSplitProbe { internal static void Phase(string name) { } } }
