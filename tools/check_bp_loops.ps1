# Map the loop nesting inside BuildTool_BlueprintPaste.CheckBuildConditions
# and report what each backward branch uses as its loop bound.
# PURE ASCII ONLY -- Windows PowerShell reads .ps1 as ANSI.
$prof = "$env:APPDATA\r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx"
Add-Type -Path "$prof\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed\Assembly-CSharp.dll")
$m = ($asm.MainModule.GetType("BuildTool_BlueprintPaste").Methods | Where-Object { $_.Name -eq "CheckBuildConditions" })
$ins = @($m.Body.Instructions)

"instructions: " + $ins.Count
"--- backward branches with span > 150, and the bound they compare against ---"
for ($k = 0; $k -lt $ins.Count; $k++) {
  $i = $ins[$k]
  if (-not ($i.Operand -is [Mono.Cecil.Cil.Instruction])) { continue }
  if ($i.Operand.Offset -ge $i.Offset) { continue }
  $span = $i.Offset - $i.Operand.Offset
  if ($span -le 150) { continue }
  # look back up to 8 instructions for the bound operand (a field/length load)
  $bound = ""
  for ($j = [Math]::Max(0, $k - 8); $j -lt $k; $j++) {
    $n = $ins[$j].OpCode.Name
    if ($n -eq "ldfld" -or $n -eq "ldlen" -or $n -eq "callvirt" -or $n -eq "call") {
      $bound = $n + " " + $ins[$j].Operand
    }
  }
  "  {0:X4}: {1} -> {2:X4}  span={3,-6} bound: {4}" -f $i.Offset, $i.OpCode.Name, $i.Operand.Offset, $span, $bound
}
