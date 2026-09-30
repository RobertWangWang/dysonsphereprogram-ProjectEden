using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using ProjectEden.Patches.Diagnostics;
class Program
{
    static void Check(bool v,string m){if(!v)throw new Exception(m);}
    static void Main()
    {
        new Harmony("eden.supplyindex.test").CreateClassProcessor(typeof(SupplyIndexProbe)).Patch();
        TransportSplitProbe.Armed=true;
        Parallel.For(0,8,worker=>{for(int i=0;i<2000;i++){ProjectEden.Patches.Fusion.FusionFuelLogisticsPatches.Invoke(1);ProjectEden.Patches.MegaExchangerLogisticsPatches.Invoke(1);}});
        var t=SupplyIndexProbe.Take();foreach(int i in new[]{0,5})Check(t[i]==16000&&t[i+2]==16000&&t[i+1]>=t[i+3]&&t[i+4]==0,"parallel totals");
        ProjectEden.Patches.Fusion.FusionFuelLogisticsPatches.Invoke(0);
        t=SupplyIndexProbe.Take();Check(t[0]==1&&t[2]==0,"early return");
        ProjectEden.Patches.Fusion.FusionFuelLogisticsPatches.Invoke(2);
        t=SupplyIndexProbe.Take();Check(t[0]==1&&t[2]==2,"two builds");
        try{ProjectEden.Patches.Fusion.FusionFuelLogisticsPatches.Invoke(3);throw new Exception("swallowed");}catch(InvalidOperationException){}
        ProjectEden.Patches.Fusion.FusionFuelLogisticsPatches.Invoke(1);
        t=SupplyIndexProbe.Take();Check(t[0]==2&&t[2]==2&&t[4]==1&&t[1]>=t[3],"exception recovery");
        ProjectEden.Patches.Fusion.FusionFuelLogisticsPatches.Invoke(4);
        t=SupplyIndexProbe.Take();Check(t[0]==1&&t[2]==2&&t[5]==1&&t[7]==1,"nested state restoration");
        TransportSplitProbe.Armed=false;ProjectEden.Patches.Fusion.FusionFuelLogisticsPatches.Invoke(4);Check(SupplyIndexProbe.Take().All(x=>x==0),"disabled");
        Console.WriteLine("PASS: real Harmony 32000 concurrent rounds, totals/build time containment, early return, multiple builds, original exception propagation, nested restoration and probe off.");
    }
}
namespace ProjectEden.Patches.Fusion
{
    static class FusionFuelLogisticsPatches
    {
        internal static void Invoke(int mode)=>Supply(mode);
        [MethodImpl(MethodImplOptions.NoInlining)]static void Supply(int mode)
        {if(mode==0)return;BuildIndex(mode==3);if(mode==4)MegaExchangerLogisticsPatches.Invoke(1);if(mode==2||mode==4)BuildIndex(false);}
        [MethodImpl(MethodImplOptions.NoInlining)]static void BuildIndex(bool fail){if(fail)throw new InvalidOperationException();System.Threading.Thread.SpinWait(10);}
    }
}
namespace ProjectEden.Patches
{
    static class MegaExchangerLogisticsPatches
    {
        internal static void Invoke(int mode)=>Supply(mode);
        [MethodImpl(MethodImplOptions.NoInlining)]static void Supply(int mode){BuildIndex();}
        [MethodImpl(MethodImplOptions.NoInlining)]static void BuildIndex(){System.Threading.Thread.SpinWait(10);}
    }
}
namespace ProjectEden.Patches.Diagnostics {static class TransportSplitProbe {internal static bool Armed;}}
static class ProjectEdenPlugin {internal static LogStub Log=new LogStub();}
class LogStub {internal void LogInfo(string s){}}
