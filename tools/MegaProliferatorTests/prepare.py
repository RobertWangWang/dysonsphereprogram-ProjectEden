from pathlib import Path
import re
root=Path(__file__).resolve().parents[2]
def method(text,marker):
 start=text.index(marker);brace=text.index('{',start);end=brace+1;depth=1
 while depth:
  depth+=(text[end]=='{')-(text[end]=='}');end+=1
 return text[start:end]
s=(root/'docs/knowledge/generated/full/Assembly-CSharp/source/AssemblerComponent.cs').read_text(encoding='utf-8-sig')
fields=s[s.index('public struct AssemblerComponent'):s.index('public void Export')]
out='using System;\n'+fields+'[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]\n'+method(s,'public uint InternalUpdate(')+'\n'+method(s,'private int split_inc_level(')+'\n}\n'
cargo=(root/'docs/knowledge/generated/full/Assembly-CSharp/source/Cargo.cs').read_text(encoding='utf-8-sig')
out+='public static class Cargo {\n'
for name in ['incTableMilli','accTableMilli','powerTable']:
 out+=re.search(r'public static (?:double|int)\[\] '+name+r' = .*?;',cargo,re.S)[0]+'\n'
out+='}\n'
t=(root/'ProjectEden/src/Patches/MegaAssembler/MegaThrottle.cs').read_text(encoding='utf-8-sig')
out+='namespace ProjectEden.Patches {static partial class MegaThrottle {\n'
for marker in ['internal static void Hold(', 'internal static void Release(', 'private static void RewindExtra(']:out+=method(t,marker)+'\n'
out+='}}\n'
p=root/'tools/out/MegaProliferatorTests/Original.cs';p.parent.mkdir(parents=True,exist_ok=True);p.write_text(out,encoding='utf-8')
print('Prepared actual game method/tables and current Hold/Release. Generated sources remain ignored.')
