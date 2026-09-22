# Which backward branches are nested inside the outer per-preview loop,
# and which of those also iterate bpCursor (=> O(previews^2))?
# PURE ASCII ONLY.
$prof = "$env:APPDATA\r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx"
Add-Type -Path "$prof\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed\Assembly-CSharp.dll")
$m = ($asm.MainModule.GetType("BuildTool_BlueprintPaste").Methods | Where-Object { $_.Name -eq "CheckBuildConditions" })
$ins = @($m.Body.Instructions)

# every backward branch = (head, tail)
$loops = @()
foreach ($i in $ins) {
  if (-not ($i.Operand -is [Mono.Cecil.Cil.Instruction])) { continue }
  if ($i.Operand.Offset -ge $i.Offset) { continue }
  $loops += [pscustomobject]@{ Head = $i.Operand.Offset; Tail = $i.Offset }
}

# outer loop = widest span
$outer = $loops | Sort-Object { $_.Tail - $_.Head } -Descending | Select-Object -First 1
"outer loop: {0:X4}..{1:X4}" -f $outer.Head, $outer.Tail

"--- loops nested inside it that read bpCursor within their own body ---"
foreach ($L in $loops) {
  if ($L.Head -le $outer.Head -or $L.Tail -ge $outer.Tail) { continue }
  $readsCursor = $false
  $reads = @()
  foreach ($i in $ins) {
    if ($i.Offset -lt $L.Head -or $i.Offset -gt $L.Tail) { continue }
    $o = "$($i.Operand)"
    if ($o -match "bpCursor") { $readsCursor = $true }
    if ($i.OpCode.Name -eq "ldfld" -and $o -match "BuildTool_BlueprintPaste::") { $reads += $o.Split(':')[-1] }
  }
  if (-not $readsCursor) { continue }
  $inner = ($loops | Where-Object { $_.Head -gt $L.Head -and $_.Tail -lt $L.Tail }).Count
  "  {0:X4}..{1:X4}  size={2,-6} nested-loops-inside={3}" -f $L.Head, $L.Tail, ($L.Tail - $L.Head), $inner
}
