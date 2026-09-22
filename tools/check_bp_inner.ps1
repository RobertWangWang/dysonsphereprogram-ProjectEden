# For each O(previews^2) loop nested in CheckBuildConditions' outer preview loop,
# list every field it writes. If a loop writes nothing but BuildPreview::condition,
# its entire output is discarded by the "build without condition" cheat and the
# loop can be skipped wholesale.
# PURE ASCII ONLY.
$prof = "$env:APPDATA\r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx"
Add-Type -Path "$prof\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed\Assembly-CSharp.dll")
$m = ($asm.MainModule.GetType("BuildTool_BlueprintPaste").Methods | Where-Object { $_.Name -eq "CheckBuildConditions" })
$ins = @($m.Body.Instructions)

$loops = @()
foreach ($i in $ins) {
  if (-not ($i.Operand -is [Mono.Cecil.Cil.Instruction])) { continue }
  if ($i.Operand.Offset -ge $i.Offset) { continue }
  $loops += [pscustomobject]@{ Head = $i.Operand.Offset; Tail = $i.Offset }
}
$outer = $loops | Sort-Object { $_.Tail - $_.Head } -Descending | Select-Object -First 1

$bad = 0
foreach ($L in $loops) {
  if ($L.Head -le $outer.Head -or $L.Tail -ge $outer.Tail) { continue }
  $readsCursor = $false
  foreach ($i in $ins) {
    if ($i.Offset -lt $L.Head -or $i.Offset -gt $L.Tail) { continue }
    if ("$($i.Operand)" -match "bpCursor") { $readsCursor = $true; break }
  }
  if (-not $readsCursor) { continue }

  $fields = @{}
  $calls = @{}
  foreach ($i in $ins) {
    if ($i.Offset -lt $L.Head -or $i.Offset -gt $L.Tail) { continue }
    $n = $i.OpCode.Name
    if ($n -eq "stfld" -or $n -eq "stsfld") {
      $f = ("$($i.Operand)" -replace '^[^ ]+ ','')
      $fields[$f] = 1
    }
    if ($n -like "call*") {
      $s = ("$($i.Operand)" -replace '^[^ ]+ ','')
      if ($s -notmatch "UnityEngine|System\.") { $calls[$s] = 1 }
    }
  }
  $fl = ($fields.Keys | Sort-Object) -join ", "
  $cl = ($calls.Keys | Sort-Object) -join ", "
  $onlyCond = $true
  foreach ($f in $fields.Keys) { if ($f -ne "BuildPreview::condition") { $onlyCond = $false } }
  $verdict = "SKIPPABLE"
  if (-not $onlyCond) { $verdict = "*** WRITES MORE ***"; $bad++ }
  ""
  "loop {0:X4}..{1:X4}   {2}" -f $L.Head, $L.Tail, $verdict
  "   writes: $fl"
  "   calls : $cl"
}
""
"loops writing more than condition: $bad (expected 0 for the wholesale skip to be safe)"
