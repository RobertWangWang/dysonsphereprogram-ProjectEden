using System;
using System.Reflection;
using System.Collections.Generic;
using ProjectEden.Patches.Fusion;
namespace HarmonyLib { public class HarmonyPatch:Attribute {public HarmonyPatch(){} public HarmonyPatch(Type t,string s){}} public class HarmonyPostfix:Attribute{} }
namespace ProjectEden { public static class MegaBuildingRegistry {public static Config Config=new Config();} public class Config {public bool virtualLogistics=true;} }
namespace ProjectEden.Utils { public static class QualityAccess {public static bool Ready=true; public static int GetStationQua(ref StationStore s)=>s.qua; public static void SetStationQua(ref StationStore s,int q)=>s.qua=q;} }
public enum ELogisticStorage {None,Supply,Demand}
public class PrefabDesc {public long genEnergyPerTick,useFuelPerTick;}
public class ItemProto {public PrefabDesc prefabDesc=new PrefabDesc();public string Name; public long HeatValue; public int FuelType;}
public static class LDB {public static Items items=new Items();}
public class Items {public int Reads; public ItemProto[] dataArray=>new System.Collections.Generic.List<ItemProto>(map.Values).ToArray();public Dictionary<int,ItemProto> map=new Dictionary<int,ItemProto>(); public ItemProto Select(int id){Reads++;return map.TryGetValue(id,out var p)?p:null;}}
public struct StationStore {public int itemId,count,inc,qua,localOrder,remoteOrder; public ELogisticStorage localLogic;}
public class StationComponent {public int id;public StationStore[] storage;}
public class PlanetTransport {public PlanetFactory factory;public StationComponent[] stationPool;public int stationCursor;public void GameTick(){}}
public class PlanetFactory {public PowerSystem powerSystem;public Entity[] entityPool;}
public struct Entity {public int protoId;}
public class PowerSystem {public PowerGeneratorComponent[] genPool;public int genCursor;}
public struct PowerGeneratorComponent {public long genEnergyPerTick,useFuelPerTick;public int id,entityId;public short fuelCount,fuelId,fuelInc,fuelMask;public void SetNewFuel(int id,short n,short inc){fuelId=(short)id;fuelCount=n;fuelInc=inc;}}
class Program
{
 static void Main()
 {
  LDB.items.map[1]=new ItemProto{Name="微型聚变发电站"};
  LDB.items.map[2]=new ItemProto{Name="聚变燃料",HeatValue=100,FuelType=2};
  var gen=new PowerGeneratorComponent{id=1,entityId=1,fuelMask=2};
  var power=new PowerSystem{genCursor=2,genPool=new[]{default(PowerGeneratorComponent),gen}};
  var source=new StationComponent{id=1,storage=new[]{new StationStore{itemId=2,count=10000,inc=40000,qua=50000,localLogic=ELogisticStorage.Supply}}};
  var t=new PlanetTransport{stationCursor=2,stationPool=new[]{null,source},factory=new PlanetFactory{powerSystem=power,entityPool=new[]{default(Entity),new Entity{protoId=1}}}};
  var method=typeof(FusionFuelLogisticsPatches).GetMethod("Supply",BindingFlags.NonPublic|BindingFlags.Static);
  void Tick(long time=1)=>method.Invoke(null,new object[]{t,time});
  void Check(bool v){if(!v)throw new Exception("自动供料回归失败");}
  FusionFuelLogisticsPatches.ApplyPower();
  Check(LDB.items.map[1].prefabDesc.genEnergyPerTick*60==3000000000L);
  Tick();Check(power.genPool[1].genEnergyPerTick==50000000 && power.genPool[1].useFuelPerTick==50000000);
  Tick();Check(power.genPool[1].fuelCount==5000 && power.genPool[1].fuelInc==20000 && source.storage[0].count==5000 && source.storage[0].inc==20000 && source.storage[0].qua==25000);
  Tick();Check(source.storage[0].count==5000);
  power.genPool[1]=gen;source.storage[0].localLogic=ELogisticStorage.Demand;Tick();Check(power.genPool[1].fuelCount==0);
  source.storage[0].localLogic=ELogisticStorage.Supply;source.storage[0].localOrder=-4995;Tick();Check(power.genPool[1].fuelCount==5 && source.storage[0].count==4995);
  power.genPool[1]=gen;source.storage[0].localOrder=0;ProjectEden.MegaBuildingRegistry.Config.virtualLogistics=false;Tick();Check(power.genPool[1].fuelCount==0);
  ProjectEden.MegaBuildingRegistry.Config.virtualLogistics=true;Tick(2);Check(power.genPool[1].fuelCount==0);
  LDB.items.map[2].FuelType=4;Tick();Check(power.genPool[1].fuelCount==0);
  LDB.items.map[2].FuelType=2;power.genPool[1].fuelCount=1;power.genPool[1].fuelId=3;Tick();Check(power.genPool[1].fuelCount==1);
  power.genPool[1]=gen;source.storage[0].count=10000;source.storage[0].inc=100000;
  Tick();Check(power.genPool[1].fuelCount==3276 && power.genPool[1].fuelInc==32760 && source.storage[0].inc==67240);
  RandomizedAndScale();
  Console.WriteLine("PASS: transfer conservation, buffer limit, supply only, orders, switch, timing, fuel compatibility, no mixing");
 }

 static PlanetTransport Clone(PlanetTransport t)
 {
  var copy=new PlanetTransport{stationCursor=t.stationCursor,stationPool=new StationComponent[t.stationPool.Length],factory=new PlanetFactory{entityPool=(Entity[])t.factory.entityPool.Clone(),powerSystem=new PowerSystem{genCursor=t.factory.powerSystem.genCursor,genPool=(PowerGeneratorComponent[])t.factory.powerSystem.genPool.Clone()}}};
  for(int i=0;i<t.stationPool.Length;i++) if(t.stationPool[i]!=null) copy.stationPool[i]=new StationComponent{id=t.stationPool[i].id,storage=(StationStore[])t.stationPool[i].storage.Clone()};
  return copy;
 }
 static void RandomizedAndScale()
 {
  var current=typeof(FusionFuelLogisticsPatches).GetMethod("Supply",BindingFlags.NonPublic|BindingFlags.Static);
  var original=typeof(FusionFuelReference).GetMethod("Supply",BindingFlags.NonPublic|BindingFlags.Static);
  LDB.items.map[3]=new ItemProto{Name="另一种燃料",HeatValue=200,FuelType=2};
  LDB.items.map[4]=new ItemProto{Name="不匹配燃料",HeatValue=200,FuelType=4};
  LDB.items.map[5]=new ItemProto{Name="非燃料"};
  var random=new Random(6401);
  for(int round=0;round<1000;round++)
  {
   int gens=181, stations=21;
   var t=new PlanetTransport{stationCursor=stations,stationPool=new StationComponent[stations],factory=new PlanetFactory{entityPool=new Entity[gens],powerSystem=new PowerSystem{genCursor=gens,genPool=new PowerGeneratorComponent[gens]}}};
   for(int i=1;i<gens;i++)
   {
    t.factory.entityPool[i].protoId=1;
    t.factory.powerSystem.genPool[i]=new PowerGeneratorComponent{id=random.Next(8)==0?0:i,entityId=i,fuelMask=2,fuelId=(short)random.Next(2,5),fuelCount=(short)random.Next(0,5001),fuelInc=(short)random.Next(0,32768)};
   }
   for(int i=1;i<stations;i++)
   {
    if(random.Next(5)==0)continue;
    var stores=new StationStore[7];
    for(int s=0;s<stores.Length;s++)
    {
     int n=random.Next(0,10001);
     stores[s]=new StationStore{itemId=random.Next(2,6),count=n,inc=n*random.Next(0,11),qua=n*3,localLogic=(ELogisticStorage)random.Next(3),localOrder=-random.Next(n+1),remoteOrder=-random.Next(n+1)};
    }
    t.stationPool[i]=new StationComponent{id=i,storage=stores};
   }
   var expected=Clone(t);
   long time=round%60+60L*(round%4);
   for(int tick=0;tick<3;tick++)
   {
    original.Invoke(null,new object[]{expected,time+tick});current.Invoke(null,new object[]{t,time+tick});
   }
   for(int i=1;i<gens;i++) if(!t.factory.powerSystem.genPool[i].Equals(expected.factory.powerSystem.genPool[i]))throw new Exception("随机发电机状态不等价");
   for(int i=1;i<stations;i++) if(t.stationPool[i]!=null)for(int s=0;s<7;s++)if(!t.stationPool[i].storage[s].Equals(expected.stationPool[i].storage[s]))throw new Exception("随机物流库存/顺序不等价");
  }
  // 同轮100台电站、1000站×30格，无可用燃料：旧路径每台扫一遍，新路径只建一次索引。
  var large=new PlanetTransport{stationCursor=1001,stationPool=new StationComponent[1001],factory=new PlanetFactory{entityPool=new Entity[6002],powerSystem=new PowerSystem{genCursor=6002,genPool=new PowerGeneratorComponent[6002]}}};
  for(int i=1;i<6001;i+=60){large.factory.entityPool[i].protoId=1;large.factory.powerSystem.genPool[i]=new PowerGeneratorComponent{id=i,entityId=i,fuelMask=2};}
  for(int i=1;i<=1000;i++){var stores=new StationStore[30];for(int s=0;s<30;s++)stores[s]=new StationStore{itemId=5,count=100,localLogic=ELogisticStorage.Supply};large.stationPool[i]=new StationComponent{id=i,storage=stores};}
  LDB.items.Reads=0;original.Invoke(null,new object[]{large,1L});int oldReads=LDB.items.Reads;
  LDB.items.Reads=0;current.Invoke(null,new object[]{large,1L});int newReads=LDB.items.Reads;
  if(oldReads!=3000100 || newReads!=101)throw new Exception($"索引重复扫描 {oldReads}/{newReads}");
  // 下一轮切换成有效燃料，验证空索引不会跨轮残留。
  large.stationPool[1].storage[0]=new StationStore{itemId=2,count=5000,localLogic=ELogisticStorage.Supply};
  current.Invoke(null,new object[]{large,61L});
  int total=0;foreach(var g in large.factory.powerSystem.genPool)total+=g.fuelCount;
  if(total!=5000 || large.stationPool[1].storage[0].count!=0)throw new Exception("跨轮重建/竞争守恒失败");
  Console.WriteLine($"PASS: 1000组随机世界×3tick与旧实现逐字段一致；缺料规模测试原型查询 {oldReads} → {newReads}；跨轮补货守恒。");
 }
}

namespace ProjectEden.Patches.Diagnostics { internal static class TransportSplitProbe { internal static bool Armed = true; internal static void Phase(string name) { } } }

public static class GameMain { public static long gameTick; public static object data = new object(); }
namespace HarmonyLib { public class HarmonyPrefix : Attribute {} public class HarmonyFinalizer : Attribute {} }

public static class ProjectEdenPlugin { public static Logger Log = new Logger(); } public class Logger { public void LogInfo(string s) {} }
