# Offline simulation of the transpiler's anchor predicate.
# PURE ASCII ONLY -- Windows PowerShell reads .ps1 as ANSI.
$prof = "$env:APPDATA\r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx"
Add-Type -Path "$prof\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed\Assembly-CSharp.dll")
$m = ($asm.MainModule.GetType("BuildTool_BlueprintPaste").Methods | Where-Object { $_.Name -eq "CheckBuildConditions" })
$ins = @($m.Body.Instructions)

$anchors = @()
for ($k = 0; $k -lt $ins.Count - 1; $k++) {
  if ($ins[$k].OpCode.Name -eq "ldfld" -and "$($ins[$k].Operand)" -match "PrefabDesc::isStation" `
      -and $ins[$k+1].OpCode.Name -like "brfalse*") { $anchors += $k }
}

"anchors: " + $anchors.Count + " at " + (($anchors | ForEach-Object { "{0:X4}" -f $ins[$_].Offset }) -join ", ")

$outer = @()
foreach ($a in $anchors) {
  $t = $ins[$a+1].Operand
  $target = [array]::IndexOf($ins, $t)
  $skipsAll = $true
  $contains = $false
  foreach ($o in $anchors) {
    if ($o -eq $a) { continue }
    if ($o -ge $target) { $skipsAll = $false; break }
    if ($o -gt $a) { $contains = $true }
  }
  $mark = ""
  if ($skipsAll -and $contains) { $mark = "   <== OUTER"; $outer += $a }
  "  {0:X4}: brfalse -> {1:X4}   skipsAll={2} contains={3}{4}" -f $ins[$a].Offset, $t.Offset, $skipsAll, $contains, $mark
}

"matching both conditions: " + $outer.Count + " (expected 1)"
if ($outer.Count -ne 1) { exit 1 }
exit 0
