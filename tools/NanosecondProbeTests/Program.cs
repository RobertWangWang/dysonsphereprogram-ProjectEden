using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using ProjectEden.Patches.Diagnostics;
class Program
{
    static void Check(bool ok, string text) { if (!ok) throw new Exception(text); }
    static void Main()
    {
        Check(NanosecondProbe.Ns(Stopwatch.Frequency)==1000000000L, "一秒换算错误");
        Check(NanosecondProbe.Ns(Stopwatch.Frequency*60)==60000000000L, "长时间换算溢出");
        NanosecondProbe.Begin(1,2,3); Check(NanosecondProbe.Now()==0,"关闭后仍计时");
        CpuCostProbe.Config.nanoTiming=true;
        int samples=0;
        for(int i=0;i<640;i++) { NanosecondProbe.Begin(1,2,3); if(NanosecondProbe.Now()!=0) samples++; }
        Check(samples==10,"抽样频率错误");
        Parallel.For(0,40,worker=>
        {
            try { for(int i=0;i<250;i++) NanosecondProbe.Record(0,Stopwatch.Frequency); }
            finally { NanosecondProbe.PublishCurrentThread(); }
            NanosecondProbe.PublishCurrentThread(); // 重复空发布不得重复计数。
        });
        long start=Stopwatch.GetTimestamp(); NanosecondProbe.Flush(start);
        NanosecondProbe.Flush(start+Stopwatch.Frequency*21);
        Check(ProjectEdenPlugin.Log.Lines[0].Contains("次数=10000 总计ns=10000000000000 均值ns=1000000000 最大ns=1000000000"),"并发累计丢失或单位错误");
        ProjectEdenPlugin.Log.Lines.Clear();
        for(int i=0;i<100;i++) NanosecondProbe.Event("测试",start,start+Stopwatch.Frequency);
        NanosecondProbe.Flush(start+Stopwatch.Frequency*42);
        Check(ProjectEdenPlugin.Log.Lines.Count==65,"事件队列上限错误");
        Check(ProjectEdenPlugin.Log.Lines[64].Contains("事件丢弃=36"),"溢出未报告");
        Check(ProjectEdenPlugin.Log.Lines[64].Contains("次数=0 总计ns=0"),"窗口未清零");
        ProjectEdenPlugin.Log.Lines.Clear();
        NanosecondProbe.Event("存档完成",start,start+Stopwatch.Frequency,true);
        Check(ProjectEdenPlugin.Log.Lines.Count==1 && ProjectEdenPlugin.Log.Lines[0].Contains("耗时ns=1000000000"),"存档结束仍依赖UI刷新");
        var targets=(IEnumerable<MethodBase>)typeof(ProductionEventProbe).GetMethod("Targets",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null);
        int count=0; foreach(var method in targets) { Check(method!=null,"空补丁目标");count++; }
        Check(count==13,"补丁目标数量错误");
        ProjectEdenPlugin.Log.Lines.Clear();
        // 自动满批发布与方法退出尾批必须守恒，重复发布不得多算。
        for(int i=0;i<300;i++) NanosecondProbe.Record(16,Stopwatch.Frequency);
        NanosecondProbe.PublishCurrentThread(); NanosecondProbe.PublishCurrentThread();
        NanosecondProbe.Flush(start+Stopwatch.Frequency*63);
        Check(ProjectEdenPlugin.Log.Lines[0].Contains("增产批量提交（结算子集） 次数=300 总计ns=300000000000"),"满批与尾批丢失");
        Check(ProjectEdenPlugin.Log.Lines[0].Contains("汇总批次=2"),"应为256条满批加44条尾批");
        ProjectEdenPlugin.Log.Lines.Clear();
        var tasks=new Task[8];
        for(int worker=0;worker<tasks.Length;worker++) tasks[worker]=Task.Run(()=>
        {
            try { for(int i=0;i<777;i++) NanosecondProbe.Record(14,Stopwatch.Frequency); throw new InvalidOperationException(); }
            catch(InvalidOperationException) { }
            finally { NanosecondProbe.PublishCurrentThread(); }
        });
        long window=start+Stopwatch.Frequency*84;
        while(!Task.WaitAll(tasks,1)) { NanosecondProbe.Flush(window); window+=Stopwatch.Frequency*21; }
        NanosecondProbe.Flush(window);
        long recorded=0;
        foreach(string line in ProjectEdenPlugin.Log.Lines)
            foreach(System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(line,@"增产批量规划（结算子集） 次数=(\d+)"))
                recorded+=long.Parse(match.Groups[1].Value);
        Check(recorded==6216,"并发窗口切换或异常尾批丢失");
        ProjectEdenPlugin.Log.Lines.Clear();
        var save=typeof(GameSave).GetMethod("SaveCurrentGame");
        var export=typeof(PlanetFactory).GetMethod("Export");
        SaveBreakdownProbe.Before(export,out long ignored); Check(ignored==0,"非保存时仍计时导出");
        SaveBreakdownProbe.Before(save,out long saveStart);
        SaveBreakdownProbe.Before(export,out long exportStart);
        SaveBreakdownProbe.After(export,exportStart,null);
        SaveBreakdownProbe.After(save,saveStart,new InvalidOperationException());
        Check(ProjectEdenPlugin.Log.Lines.Count==1 && ProjectEdenPlugin.Log.Lines[0].Contains("工厂数=1") && ProjectEdenPlugin.Log.Lines[0].Contains("异常=InvalidOperationException"),"存档分段缺失");
        SaveBreakdownProbe.Before(export,out ignored); Check(ignored==0,"存档异常后未清理");
        int saveTargets=0;foreach(var method in SaveBreakdownProbe.Targets()) {Check(method!=null,"存档目标缺失");saveTargets++;}
        Check(saveTargets==4,"存档目标数量错误");
        Console.WriteLine("PASS: 存档4个目标、子集计时、异常清理及立即输出，探针满批/尾批守恒。");
        Console.WriteLine("PASS: 纳秒换算、开关、抽样、并发聚合、窗口清零、事件限流、13个目标绑定。");
    }
}
namespace ProjectEden.Patches.Diagnostics { class CpuCostProbe { internal static ConfigData Config=new ConfigData(); } class ConfigData { public bool nanoTiming; public float perfProbeSeconds=20; } }
class ProjectEdenPlugin { public void Export(System.IO.BinaryWriter w){} internal static Logger Log=new Logger(); }
class Logger { internal List<string> Lines=new List<string>(); internal void LogInfo(string text)=>Lines.Add(text); }
class PlanetFactory { public int planetId=1; public void Export(System.IO.Stream s,System.IO.BinaryWriter w){} }
class GameData { public void Export(System.IO.BinaryWriter w){} }
class FactorySystem { public PlanetFactory factory=new PlanetFactory(); public void GameTick(long time,bool active){} public void GameTickLabProduceMode(long time,bool active){} public void GameTickLabResearchMode(long time,bool active){} }
class GameSave { public static bool SaveCurrentGame(string name)=>true; public static bool AutoSave()=>true; }
class BuildTool_BlueprintPaste { public bool CheckBuildConditions()=>true; public void CreatePrebuilds(){} }
class GameLogic {
public void _miner_parallel(int a,int b,int c,int d){}
public void _assembler_parallel(int a,int b,int c,int d){}
public void _fractionator_parallel(int a,int b,int c,int d){}
public void _ejector_parallel(int a,int b,int c,int d){}
public void _silo_parallel(int a,int b,int c,int d){}
public void _lab_produce_parallel(int a,int b,int c,int d){}
}

// 离线测试仅替代 Harmony 壳层，不在 .NET 8 中加载游戏的 Mono 补丁运行时。
namespace HarmonyLib {
 [AttributeUsage(AttributeTargets.Class|AttributeTargets.Method)] class HarmonyPatch:Attribute {}
 class HarmonyTargetMethods:Attribute {} class HarmonyPrefix:Attribute {} class HarmonyFinalizer:Attribute {}
 class HarmonyPriority:Attribute { public HarmonyPriority(int priority){} }
 static class Priority { public const int First=800, Last=0; }
 static class AccessTools { public static MethodInfo Method(Type type,string name,Type[] args)=>type.GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static,null,args,null); }
}
