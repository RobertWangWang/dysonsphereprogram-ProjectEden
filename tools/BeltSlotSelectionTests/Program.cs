using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ProjectEden.Patches;
class Program
{
    static void Check(bool v) { if(!v) throw new Exception("Slot selection mismatch"); }
    static void Compare(SlotData[] slots)
    {
        var original=(SlotData[])slots.Clone(); var expected=new List<int>();var actual=new List<int>();
        bool input=false, output=false;
        // 冻结旧双循环的选择、清理和先输出后输入顺序。
        for(int pass=0;pass<2;pass++) for(int i=0;i<original.Length;i++)
        {
            IODir direction=pass==0?IODir.Output:IODir.Input;
            if(original[i].dir!=direction) {
                if(original[i].dir!=IODir.Input&&original[i].dir!=IODir.Output){original[i].beltId=0;original[i].counter=0;}
                continue;
            }
            if(original[i].beltId<=0)continue;
            expected.Add(pass*1000+i);if(pass==0)output=true;else input=true;
        }
        var selection=BeltSlotSelection.Prepare(slots);
        if(selection.HasOutput)foreach(int i in selection.Outputs)actual.Add(i);
        if(selection.HasInput)foreach(int i in selection.Inputs)actual.Add(1000+i);
        Check(actual.SequenceEqual(expected)&&selection.HasInput==input&&selection.HasOutput==output);
        for(int i=0;i<slots.Length;i++)Check(slots[i].Equals(original[i]));
    }
    static void Main()
    {
        SlotDataStoreTests.Run();
        Parallel.For(0,100000,seed=>{
            var r=new Random(seed);int length=new[]{0,1,12,31,32,33,64,80}[seed%8];var slots=new SlotData[length];
            for(int i=0;i<length;i++)slots[i]=new SlotData {dir=(IODir)r.Next(-1,4),beltId=r.Next(-1,4),counter=r.Next(),storageIdx=r.Next(8)};
            Compare(slots);
            if(length>0){slots[length-1].dir=IODir.Output;slots[length-1].beltId=5;Compare(slots);slots[length-1].dir=IODir.Input;Compare(slots);slots[length-1].dir=IODir.None;Compare(slots);}
        });
        // 覆盖每个bit和32位最高位，避免符号位或移位漏掉末格。
        for(int i=0;i<32;i++){var a=new SlotData[32];a[i].dir=IODir.Output;a[i].beltId=1;Compare(a);}
        Console.WriteLine("PASS: 100000 concurrent layouts, empty/sparse/dense/32-bit boundary/long arrays, connect/reverse/disconnect immediately, cleanup and output-before-input order match old loops.");
    }
}
public enum IODir {None,Input,Output}
public struct SlotData {public IODir dir;public int beltId,storageIdx,counter;}
