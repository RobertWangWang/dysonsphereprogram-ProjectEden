# 从本机已反编译代码提取真实方法，生成内容仅留在被忽略的 tools/out。
from pathlib import Path
import re
root=Path(__file__).resolve().parents[2]
s=(root/'docs/knowledge/generated/full/Assembly-CSharp/source/AssemblerComponent.cs').read_text(encoding='utf-8-sig')
def method(text, marker):
 start=text.index(marker); brace=text.index('{',start); depth=1; end=brace+1
 while depth:
  depth += (text[end]=='{')-(text[end]=='}');end+=1
 return text[start:end]
fields=s[s.index('public struct AssemblerComponent'):s.index('public void Export')]
body=method(s,'public uint InternalUpdate(')
body,n=re.subn(r'(productCounts2\[[^]]+\]) \* (9|19)\b',r'\1 * ProjectEden.Patches.MegaOutputGatePatches.Scale(\2, ref this)',body)
assert n==7,n
out='using System;\n'+fields+body+'\n'+method(s,'private int split_inc_level(')+'\n}\n'
cargo=(root/'docs/knowledge/generated/full/Assembly-CSharp/source/Cargo.cs').read_text(encoding='utf-8-sig')
out+='public static class Cargo {\n'
for name in ['incTableMilli','accTableMilli','powerTable']:
 out+=re.search(r'public static (?:double|int)\[\] '+name+r' = .*?;',cargo,re.S)[0]+'\n'
out+='}\n'
# 编排代码也直接提取，避免只测试计划计算而漏掉周期预算或 settled 计数错误。
patch=(root/'ProjectEden/src/Patches/MegaAssembler/MegaAssemblerPatches.cs').read_text(encoding='utf-8-sig')
start=patch.index('            // 原本那次调用紧随其后，所以这里只补差额')
end=patch.index('            return settled;',start)+len('            return settled;')
out+='namespace ProjectEden.Patches { static class Pipeline {\nstatic void Tally(int a,int b){}\nstatic long ProducedSum(int[] a) {long n=0;foreach(int v in a)n+=v;return n;}\npublic static int Run(ref AssemblerComponent component, int cycles,float power,int[] productRegister,int[] consumeRegister) {\nint[] produced=component.produced;bool watch=produced.Length>0;int last=watch?produced[0]:0;\nint settled=0,ran=0,skipped=0;long prevSum=ProducedSum(produced);int prevTime=component.time,prevExtra=component.extraTime;\n'
out+=patch[start:end]+'\n}}}\n'
legacy=(root/'ProjectEden/src/Patches/MegaAssembler/MegaBatchSettle.cs').read_text(encoding='utf-8-sig')
legacy=legacy[legacy.index('namespace ProjectEden.Patches'):].replace('MegaBatchSettle','LegacyBatchReference').replace('Interlocked.','System.Threading.Interlocked.').replace('Volatile.','System.Threading.Volatile.')
out+='\n'+legacy
start=patch.index('            if (component.speed < MegaBuildingRegistry.MegaSpeedThreshold)')
end=patch.index('            // 抽样判定',start)
gate=patch[start:end].replace('return;', 'return false;')
out+='\nnamespace ProjectEden.Patches { static class SpeedGate { static bool _reportedSpeed; public static bool Check(PlanetFactory factory, ref AssemblerComponent component) {\n'+gate+'return true;}\n'+method(patch,'private static void ApplySpeed(')+'\n}}\n'
p=root/'tools/out/ProliferatorBatchTests/Original.cs';p.parent.mkdir(parents=True,exist_ok=True);p.write_text(out,encoding='utf-8')
print('已提取真实 InternalUpdate/split_inc_level、7处产物闸、当前补跑编排及原版增产表。')
