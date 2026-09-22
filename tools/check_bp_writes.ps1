# What does BuildTool_BlueprintPaste.CheckBuildConditions actually WRITE?
# If everything it produces is discarded when the cheats are on, the whole
# per-preview body can be short-circuited instead of patching six loops.
# PURE ASCII ONLY.
$prof = "$env:APPDATA\r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx"
Add-Type -Path "$prof\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed\Assembly-CSharp.dll")
$m = ($asm.MainModule.GetType("BuildTool_BlueprintPaste").Methods | Where-Object { $_.Name -eq "CheckBuildConditions" })
$ins = @($m.Body.Instructions)

$w = @{}
foreach ($i in $ins) {
  $n = $i.OpCode.Name
  if ($n -ne "stfld" -and $n -ne "stsfld") { continue }
  $f = "$($i.Operand)"
  $f = $f -replace '^[^ ]+ ',''
  if ($w.ContainsKey($f)) { $w[$f]++ } else { $w[$f] = 1 }
}
"--- fields written (count) ---"
$w.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "  {0,4}  {1}" -f $_.Value, $_.Key }

"--- non-void calls to game code (things that could have side effects) ---"
$c = @{}
foreach ($i in $ins) {
  if ($i.OpCode.Name -notlike "call*") { continue }
  $s = "$($i.Operand)"
  if ($s -match "UnityEngine|System\.") { continue }
  $s = $s -replace '^[^ ]+ ',''
  if ($c.ContainsKey($s)) { $c[$s]++ } else { $c[$s] = 1 }
}
$c.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "  {0,4}  {1}" -f $_.Value, $_.Key }
