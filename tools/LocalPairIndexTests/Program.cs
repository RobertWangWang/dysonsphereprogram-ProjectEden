using System;
using System.Collections.Concurrent;
using ProjectEden.Patches;
class Program {
 static ConcurrentDictionary<int,byte> Keys(params int[] ids){var k=new ConcurrentDictionary<int,byte>();foreach(int id in ids)k[id]=0;return k;}
 static void Main(){
  var t=new PlanetTransport {stationCursor=4,stationPool=new StationComponent[4],factory=new Factory{planetId=6401}};
  t.stationPool[1]=new StationComponent {id=1,storage=new[]{new Store{itemId=1001,localLogic=ELogisticStorage.Supply}}};
  t.stationPool[2]=new StationComponent {id=2,storage=new[]{new Store{itemId=1001,localLogic=ELogisticStorage.Demand}}};
  LocalPairIndex.Rebuild(t,Keys(1),false);
  // 199次增量后，在触发对账的这一批切换物品。旧实现比的是变更前与变更后的表。
  for(int i=0;i<199;i++)LocalPairIndex.Rebuild(t,Keys(2));
  t.stationPool[2].storage[0].itemId=1002;
  LocalPairIndex.Rebuild(t,Keys(2));
  if(!LocalPairIndex.IncrementalEnabled)throw new Exception("正常槽位变更在对账边界错误关闭了增量维护");
  if(t.stationPool[1].localPairCount!=0 || t.stationPool[2].localPairCount!=0)throw new Exception("残留旧配对");
  for(int scenario=0;scenario<4;scenario++){
   var w=World(6500+scenario);LocalPairIndex.Rebuild(w,Keys(1),false);
   for(int i=0;i<199;i++)LocalPairIndex.Rebuild(w,Keys(2));
   var keys=Keys(2);
   if(scenario==0){w.stationPool[5]=new StationComponent{id=5,storage=new[]{new Store{itemId=1001,localLogic=ELogisticStorage.Demand}}};w.stationCursor=6;keys=Keys(5);}
   if(scenario==1){LocalPairIndex.DetachOne(w,2);w.stationPool[2].id=0;w.stationPool[2].storage=null;}
   if(scenario==2){w.stationPool[1].storage[0].localLogic=ELogisticStorage.Demand;w.stationPool[2].storage[0].localLogic=ELogisticStorage.Supply;keys=Keys(1,2);}
   if(scenario==3){w.stationPool[4].storage[0].itemId=1001;}
   LocalPairIndex.Rebuild(w,keys,scenario!=3);
   if(!LocalPairIndex.IncrementalEnabled)throw new Exception("正常边界关闭增量 scenario="+scenario);
   if(scenario==3)for(int i=0;i<200;i++)LocalPairIndex.Rebuild(w,Keys(2));
  }
  var bad=World(6600);LocalPairIndex.Rebuild(bad,Keys(1),false);
  for(int i=0;i<199;i++)LocalPairIndex.Rebuild(bad,Keys(2));
  // 在不属于本批变更的两站中注入真实错误，确认保护机制没有被放宽。
  bad.stationPool[1].AddLocalPair(1,0,3,0);bad.stationPool[3].AddLocalPair(1,0,3,0);
  LocalPairIndex.Rebuild(bad,Keys(2));
  if(LocalPairIndex.IncrementalEnabled)throw new Exception("真实错误未触发回退");
  if(bad.stationPool[1].localPairCount!=1||bad.stationPool[3].localPairCount!=1)throw new Exception("错误表未被全量修复");
  if(!ProjectEdenPlugin.Log.LastError.Contains("差异站点=2") || !ProjectEdenPlugin.Log.LastError.Contains("站=1"))throw new Exception("差异诊断缺失");
  Console.WriteLine("PASS: 改槽、新增、拆除、批内双向变更、不完整delta重建；真实错误仍回退且输出差异站点。");
 }
 static PlanetTransport World(int planet){
  var t=new PlanetTransport{factory=new Factory{planetId=planet},stationCursor=5,stationPool=new StationComponent[8]};
  for(int i=1;i<=4;i++)t.stationPool[i]=new StationComponent{id=i,storage=new[]{new Store{itemId=i<=2?1001:1002,localLogic=i%2==1?ELogisticStorage.Supply:ELogisticStorage.Demand}}};
  return t;
 }
}
public enum ELogisticStorage{Storage,Supply,Demand}
public struct Store{public int itemId;public ELogisticStorage localLogic;}
public struct Pair{public int supplyId,supplyIndex,demandId,demandIndex;}
public class StationComponent{
 public int id,localPairCount;public Store[] storage;public Pair[] localPairs=new Pair[4];
 public void ClearLocalPairs(){localPairCount=0;}
 public void AddLocalPair(int a,int b,int c,int d){if(localPairCount==localPairs.Length)Array.Resize(ref localPairs,localPairs.Length*2);localPairs[localPairCount++]=new Pair{supplyId=a,supplyIndex=b,demandId=c,demandIndex=d};}
 public void RematchLocalPairs(StationComponent[] pool,int cursor,int key,int carries){
  for(int other=id+1;other<cursor;other++){var o=pool[other];if(o==null||o.id!=other)continue;
   for(int i=0;i<storage.Length;i++)for(int j=0;j<o.storage.Length;j++){
    var a=storage[i];var b=o.storage[j];if(a.itemId<=0||a.itemId!=b.itemId)continue;
    if(a.localLogic==ELogisticStorage.Supply && b.localLogic==ELogisticStorage.Demand){AddLocalPair(id,i,other,j);o.AddLocalPair(id,i,other,j);}
    if(a.localLogic==ELogisticStorage.Demand && b.localLogic==ELogisticStorage.Supply){AddLocalPair(other,j,id,i);o.AddLocalPair(other,j,id,i);}
   }
  }
 }
}
public class PlanetTransport{public StationComponent[] stationPool;public int stationCursor;public Factory factory;}
public class Factory{public int planetId;}
public static class GameMain{public static History history=new History();}
public class History{public int logisticDroneCarries=100;}
public static class ProjectEdenPlugin{public static Logger Log=new Logger();}
public class Logger{public string LastError="";public void LogInfo(string s)=>Console.WriteLine(s);public void LogWarning(string s)=>Console.WriteLine(s);public void LogError(string s){LastError=s;Console.WriteLine(s);}}
