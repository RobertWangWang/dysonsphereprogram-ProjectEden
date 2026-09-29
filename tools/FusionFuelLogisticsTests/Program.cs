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
public class Items {public ItemProto[] dataArray=>new System.Collections.Generic.List<ItemProto>(map.Values).ToArray();public Dictionary<int,ItemProto> map=new Dictionary<int,ItemProto>(); public ItemProto Select(int id)=>map.TryGetValue(id,out var p)?p:null;}
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
  Console.WriteLine("PASS: transfer conservation, buffer limit, supply only, orders, switch, timing, fuel compatibility, no mixing");
 }
}
