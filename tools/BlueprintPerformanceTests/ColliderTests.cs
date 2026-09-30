#pragma warning disable 649 // 测试反射设置组合字段
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ProjectEden.Patches;
class ColliderTests
{
 static void Check(bool v,string m){if(!v)throw new Exception(m);}
 internal static void Run()
 {
  new Harmony("eden.collider.find.test").CreateClassProcessor(typeof(BlueprintColliderFindPatch)).Patch();
  var owner=new NearColliderLogic();
  for(int i=1;i<=20000;i++)owner.colliderObjs.Add(i,new ColliderObject {id=i+50,collider=new UnityEngine.Collider(i)});
  var target=owner.colliderObjs[12345].collider;
  Check(owner.FindColliderId(target)==12395&&owner.Scans==1,"outside scope changed");
  BlueprintColliderLookupPatches.Begin(out var scope);
  for(int i=0;i<1000;i++) {Check(owner.FindColliderId(target)==12395,"hit");Check(owner.FindColliderId(new UnityEngine.Collider(30000))==0,"miss");}
  Check(scope.Builds==1&&scope.Entries==20000&&owner.Scans==1,"repeated full scans");
  owner.colliderObjs[20001]=new ColliderObject{id=42,collider=target};
  Check(owner.FindColliderId(target)==12395&&scope.Builds==2,"duplicate order/count change");
  BlueprintColliderMutationPatch.Prefix(owner);
  owner.colliderObjs[12345].id=222;
  BlueprintColliderMutationPatch.Finalizer(owner);
  Check(owner.FindColliderId(target)==222,"mutation invalidation");
  var replacement=new Dictionary<int,ColliderObject>(owner.colliderObjs);
  owner.colliderObjs=replacement;owner.FindColliderId(target);Check(scope.Builds==4,"table identity");
  var nullCollider=new UnityEngine.Collider(50000){Destroyed=true};
  owner.colliderObjs[20002]=new ColliderObject{id=777,collider=nullCollider};
  Check(owner.FindColliderId(null)==777&&owner.Scans==2,"unity null fallback");
  BlueprintColliderLookupPatches.Begin(out var nested);Check(owner.FindColliderId(target)==222,"nested lookup");
  BlueprintColliderLookupPatches.End(nested);Check(ReferenceEquals(BlueprintColliderLookupPatches.Current,scope),"nested restoration");
  BlueprintColliderLookupPatches.End(scope);Check(BlueprintColliderLookupPatches.Current==null&&scope.Tables.Count==0,"scope released");
  var desc=new PrefabDesc{hasBuildCollider=true,isAssembler=true};
  ProjectEdenPlugin.CheatsConfig.enabled=true;ProjectEdenPlugin.CheatsConfig.noConditionBuild=true;
  Check(!BlueprintAssemblerCollisionSkip.NeedsCollider(desc),"assembler not skipped");
  foreach(string name in new[]{"isBelt","isInserter","multiLevel","veinMiner","oilMiner","isTank","isStorage","isLab","isSplitter"}){
    typeof(PrefabDesc).GetField(name).SetValue(desc,true);Check(BlueprintAssemblerCollisionSkip.NeedsCollider(desc),"special path skipped: "+name);typeof(PrefabDesc).GetField(name).SetValue(desc,false);
  }
  desc.addonType=EAddonType.Belt;Check(BlueprintAssemblerCollisionSkip.NeedsCollider(desc),"addon");desc.addonType=EAddonType.None;
  ProjectEdenPlugin.CheatsConfig.noConditionBuild=false;Check(BlueprintAssemblerCollisionSkip.NeedsCollider(desc),"disabled cheat");
  Console.WriteLine("PASS: 20000 collider entries, 2000 lookups/one build, duplicates, misses, mutations, replacement, Unity-null fallback, scope lifetime and collision guard matrix.");
 }
}
class CheatsConfig{ public bool enabled,noConditionBuild; }
enum EAddonType {None,Belt}
class PrefabDesc{public bool hasBuildCollider,isAssembler,isBelt,isInserter,multiLevel,veinMiner,oilMiner,isTank,isStorage,isLab,isSplitter;public EAddonType addonType;}
class ColliderObject{public int id;public UnityEngine.Collider collider;}
class NearColliderLogic{
 public Dictionary<int,ColliderObject> colliderObjs=new Dictionary<int,ColliderObject>(); public int Scans;
 [MethodImpl(MethodImplOptions.NoInlining)] public int FindColliderId(UnityEngine.Collider cc){Scans++;foreach(var p in colliderObjs)if(p.Value.collider==cc)return p.Value.id;return 0;}
}
namespace UnityEngine{
 class Collider{
  public int Id;public bool Destroyed; public Collider(int id){Id=id;}public int GetInstanceID()=>Id;
  public static bool operator ==(Collider a,Collider b){bool an=ReferenceEquals(a,null)||a.Destroyed,bn=ReferenceEquals(b,null)||b.Destroyed;return an||bn?an&&bn:a.Id==b.Id;}
  public static bool operator !=(Collider a,Collider b)=>!(a==b);
  public override bool Equals(object o)=>ReferenceEquals(this,o);public override int GetHashCode()=>Id;
 }
}
