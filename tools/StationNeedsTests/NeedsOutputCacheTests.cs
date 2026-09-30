using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using ProjectEden.Patches;

static class NeedsOutputCacheTests
{
    static Func<StationComponent,bool> Bind(Type t) => (Func<StationComponent,bool>)t.GetMethod("UpdateNeeds_Prefix",BindingFlags.NonPublic|BindingFlags.Static).CreateDelegate(typeof(Func<StationComponent,bool>));
    static readonly Func<StationComponent,bool> Current=Bind(typeof(StationBeltInputPatches)), Previous=Bind(typeof(BeforeOutputCacheReference));
    internal static void Run()
    {
        var rng=new Random(30930);
        var s=new StationComponent {id=7,storage=new StationStore[30],needs=new int[6]};
        for(int i=0;i<30;i++) s.storage[i]=new StationStore {itemId=i+1,count=0,max=100};
        // 同一tick多次直接写库存、容量、已配置格物品号及白名单，不能依赖脏通知。
        for(int step=0;step<50000;step++)
        {
            GameMain.gameTick=step/10;
            int i=rng.Next(30);
            s.storage[i].itemId=rng.Next(1,100);
            s.storage[i].max=rng.Next(101);
            s.storage[i].count=rng.Next(101);
            if(step%7==0) s.needs[rng.Next(s.needs.Length)]=rng.Next(999);
            if(step%101==0) s.needs=new int[step%202==0?6:8];
            s.isStellar=step%3==0; s.warperCount=step%51; s.warperMaxCount=50;
            var expected=new StationComponent {id=s.id,storage=s.storage,needs=(int[])s.needs.Clone(),isStellar=s.isStellar,warperCount=s.warperCount,warperMaxCount=s.warperMaxCount};
            Previous(expected); Current(s);
            if(!s.needs.SequenceEqual(expected.needs)) throw new Exception("需求刷新与原实现不一致");
        }
        Console.WriteLine("PASS: 需求刷新回归5万次无通知库存/容量/换货/白名单改写及数组形状切换，逐次等价。");
    }
}
