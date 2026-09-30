using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using ProjectEden.Patches;
static class SlotDataStoreTests
{
    static void Check(bool condition) { if(!condition) throw new Exception("槽位索引回归"); }
    internal static void Run()
    {
        SlotDataStore.Clear();
        var shared=SlotDataStore.GetSlots(6401,17);
        Parallel.For(0,10000,i=>{
            Check(ReferenceEquals(shared,SlotDataStore.GetSlots(6401,17)));
            var entry=SlotDataStore.GetSlots(6401,i+100);
            entry[0].counter=i;
        });
        for(int i=0;i<10000;i++) Check(SlotDataStore.GetSlots(6401,i+100)[0].counter==i);
        shared[0].beltId=99;
        Check(SlotDataStore.GetSlots(6401,17)[0].beltId==99);
        SlotDataStore.Remove(6401,17);
        Check(!ReferenceEquals(shared,SlotDataStore.GetSlots(6401,17)));
        Check(SlotDataStore.GetSlots(6401,17)[0].beltId==0);
        SlotDataStore.Clear();
        Check(SlotDataStore.GetSlots(6401,100)[0].counter==0);
        Parallel.For(0,1000,i=>{
            SlotData[] first=null;
            Parallel.For(0,4,j=>{
                var item=SlotDataStore.GetSlots(42,i);
                var old=System.Threading.Interlocked.CompareExchange(ref first,item,null);
                if(old!=null) Check(ReferenceEquals(old,item));
            });
        });
        // 使用旧字节布局导入，包含空数组、长数组和超界编号字典回退。
        var ms=new MemoryStream();var w=new BinaryWriter(ms);
        int[] planets={6401,65535,65536,-1,1};int[] entities={1,1048575,1048576,-2,99};
        w.Write(5);
        for(int k=0;k<5;k++) {w.Write(planets[k]);w.Write(entities[k]);w.Write(k*10);
            for(int j=0;j<k*10;j++){w.Write(j%3);w.Write(j+1);w.Write(j+2);w.Write(j+3);}}
        w.Flush();ms.Position=0;SlotDataStore.Import(new BinaryReader(ms));
        for(int k=0;k<5;k++) {var item=SlotDataStore.GetSlots(planets[k],entities[k]);Check(item.Length==k*10);
            for(int j=0;j<item.Length;j++) Check((int)item[j].dir==j%3&&item[j].beltId==j+1&&item[j].storageIdx==j+2&&item[j].counter==j+3);}
        ms=new MemoryStream();SlotDataStore.Export(new BinaryWriter(ms));ms.Position=0;
        SlotDataStoreReference.Import(new BinaryReader(ms));
        for(int k=0;k<5;k++) {var a=SlotDataStore.GetSlots(planets[k],entities[k]);var b=SlotDataStoreReference.GetSlots(planets[k],entities[k]);Check(a.Length==b.Length);
            for(int j=0;j<a.Length;j++)Check(a[j].Equals(b[j]));}
        Console.WriteLine("PASS: 并发首次创建/扩容/原数组可见、拆除重建、清空、旧格式导入导出、空/长数组和超界回退。");
        SlotDataStore.Clear();SlotDataStoreReference.Clear();
        for(int i=0;i<40000;i++){SlotDataStore.GetSlots(6401,i);SlotDataStoreReference.GetSlots(6401,i);}
        var oldTimes=new double[5];var newTimes=new double[5];
        for(int round=0;round<5;round++)for(int order=0;order<2;order++){
            bool current=(round+order)%2==0;var timer=Stopwatch.StartNew();long sum=0;
            for(int i=0;i<2000000;i++)sum+=(current?SlotDataStore.GetSlots(6401,i%40000):SlotDataStoreReference.GetSlots(6401,i%40000)).Length;
            Check(sum==24000000);(current?newTimes:oldTimes)[round]=timer.Elapsed.TotalMilliseconds;
        }
        Array.Sort(oldTimes);Array.Sort(newTimes);
        Console.WriteLine($"net472/4万建筑/200万次查询中位数：旧字典 {oldTimes[2]:F1}ms，直接索引 {newTimes[2]:F1}ms；非Unity帧率。");
    }
}
