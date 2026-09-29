using System;
using System.Reflection;
using ProjectEden.Patches;
namespace HarmonyLib {public class HarmonyPrefix:Attribute{} public class HarmonyPatch:Attribute {public HarmonyPatch(){} public HarmonyPatch(Type t,string n,Type[] a){}}}
namespace ProjectEden {public static class MegaBuildingRegistry {public static Config Config=new Config();} public class Config {public Entry[] buildings;} public class Entry {public int itemId;public object exchanger;public string displayName;}}
public struct Entity {public int protoId;}
public class Factory {public Entity[] entityPool;}
public struct Belt {public int segPathId;}
public class CargoPath {public bool blocked;public int count,inc;}
public class CargoTraffic {public Factory factory;public Belt[] beltPool=new Belt[2];public CargoPath path=new CargoPath();public CargoPath GetCargoPath(int id)=>path;}
public static class GameMain {public static History history=new History();}
public class History {public int stationPilerLevel=4;}
public struct PowerExchangerComponent {public int entityId,fullId;public short fullCount,fullInc,emptyCount;public bool InsertItemToBelt(CargoTraffic t,int b,bool e)=>false;public bool InsertItemToBelt(CargoTraffic t,int b)=>false;}
namespace ProjectEden.Patches {public static class CargoWidening {public static bool StackIsWide=true;public static int IncMax=32767;public static bool InsertAtHead(CargoPath p,int id,int n,int inc){if(p.blocked)return false;p.count+=n;p.inc+=inc;return true;}}}
class Program
{
 static void Check(bool ok){if(!ok)throw new Exception("stack regression");}
 static void Main(){
  Check(MegaExchangerStackPatches.StackCount(20,80,4,255,255)==4);
  Check(MegaExchangerStackPatches.StackCount(3,12,4,255,255)==3);
  Check(MegaExchangerStackPatches.StackCount(20,80,5000,32767,32767)==20);
  Check(MegaExchangerStackPatches.StackCount(100,1000,100,255,255)==25);
  for(int n=1;n<=100;n++)for(int inc=0;inc<=n*10;inc++){
   int k=Math.Min(4,n);int remaining=n,points=inc,total=0;
   for(int j=0;j<k;j++){int part=points/remaining;total+=part;points-=part;remaining--;}
   Check(MegaExchangerStackPatches.SplitPoints(n,inc,k)==total);
  }
  var traffic=new CargoTraffic();
  var method=typeof(MegaExchangerStackPatches).GetMethod("InsertFull",BindingFlags.Static|BindingFlags.NonPublic);
  object[] args={new PowerExchangerComponent{fullId=1,fullCount=20,fullInc=80},traffic,1};
  traffic.path.blocked=true;Check(!(bool)method.Invoke(null,args));Check(((PowerExchangerComponent)args[0]).fullCount==20 && traffic.path.count==0);
  traffic.path.blocked=false;Check((bool)method.Invoke(null,args));Check(((PowerExchangerComponent)args[0]).fullCount==16 && ((PowerExchangerComponent)args[0]).fullInc==64 && traffic.path.count==4 && traffic.path.inc==16);
  Console.WriteLine("PASS: tech levels, partial stock, capacity, point conservation, blocked and successful output");
 }
}
