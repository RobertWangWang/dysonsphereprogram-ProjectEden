# check_ship_bank.ps1 -- offline assertions behind StationShipBank / StationShipExpandPatches.
#
# PURE ASCII ONLY. Windows PowerShell reads .ps1 as ANSI, so one non-ASCII character
# turns the whole file into parser errors that look nothing like an encoding problem.
#
# What this guards, in the order the feature depends on it:
#
#   1. the two ship bitmaps are still UInt64 fields on StationComponent
#   2. nothing outside the known 13 methods touches them
#   3. the 8 methods we replace wholesale are still PURE bit flips
#      (their only stfld targets are those two fields) and are not overloaded
#   4. ShipRenderersOnTick still holds exactly 2 INLINE bit reads matching the
#      exact 9-instruction shape the transpiler rewrites -- simulated here, so a
#      game update fails offline instead of silently rewriting half of it
#   5. the reconciler is still there (reads idleShipCount, ends in Assert.Zero),
#      because the idle bits are derived from it rather than saved
#   6. the ship array length still round-trips through the vanilla save
#      (Export writes workShipDatas.Length, Import reads it and newarr's)
#   7. ShipData.shipIndex is still written on dispatch and read back on load,
#      because the work bits are derived from it
#   8. there is still no PatchShipArray, i.e. the expand patch is still needed

$ErrorActionPreference = "Stop"

$profileDir = Join-Path $env:APPDATA "r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx"
$gameAsm = "G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed\Assembly-CSharp.dll"

Add-Type -Path (Join-Path $profileDir "core\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAsm)
$mod = $asm.MainModule
$sc = $mod.GetType("StationComponent")

$fails = 0
function Ok($m) { Write-Host "  OK   $m" }
function Fail($m) { Write-Host "  FAIL $m" -ForegroundColor Red; $script:fails++ }

# --- 1. field types -------------------------------------------------------
foreach ($n in @("idleShipIndices", "workShipIndices")) {
  $f = $sc.Fields | Where-Object { $_.Name -eq $n } | Select-Object -First 1
  if ($null -eq $f) { Fail "field $n is gone" }
  elseif ($f.FieldType.FullName -ne "System.UInt64") { Fail "$n is $($f.FieldType.FullName), expected System.UInt64" }
  else { Ok "$n is UInt64" }
}

# --- 2. who touches them --------------------------------------------------
$expected = @("Init", "Reset", "IdleShipGetToWork", "WorkShipBackToIdle", "AddIdleShip",
  "RemoveIdleShip", "HasWorkShipIndex", "HasIdleShipIndex", "HasShipIndex",
  "QueryIdleShip", "ShipRenderersOnTick", "Export", "Import")

$touch = New-Object System.Collections.Generic.List[string]
foreach ($t in $mod.GetTypes()) {
  foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    foreach ($i in $m.Body.Instructions) {
      if ($null -ne $i.Operand -and "$($i.Operand)" -match "ShipIndices") {
        $key = "$($t.Name)::$($m.Name)"
        if (-not $touch.Contains($key)) { $touch.Add($key) | Out-Null }
        break
      }
    }
  }
}
$foreign = $touch | Where-Object { $_ -notlike "StationComponent::*" }
if ($foreign) { Fail "bitmaps touched outside StationComponent: $($foreign -join ', ')" }
else { Ok "bitmaps touched only inside StationComponent" }

$names = $touch | ForEach-Object { $_ -replace "StationComponent::", "" }
$extra = $names | Where-Object { $expected -notcontains $_ }
if ($extra) { Fail "new methods touch the bitmaps: $($extra -join ', ')" }
else { Ok "touching methods are the known $($expected.Count)" }

# --- 3. the 8 replaced methods are pure bit flips -------------------------
$replaced = @("IdleShipGetToWork", "WorkShipBackToIdle", "AddIdleShip", "RemoveIdleShip",
  "HasWorkShipIndex", "HasIdleShipIndex", "HasShipIndex", "QueryIdleShip")
foreach ($n in $replaced) {
  $ms = $sc.Methods | Where-Object { $_.Name -eq $n }
  if ($ms.Count -ne 1) { Fail "$n has $($ms.Count) overloads, prefix patching binds by name"; continue }
  $m = $ms[0]
  $bad = @()
  foreach ($i in $m.Body.Instructions) {
    if ($i.OpCode.Name -eq "stfld" -and "$($i.Operand)" -notmatch "ShipIndices") { $bad += "$($i.Operand)" }
    if ($i.OpCode.Name -eq "call" -or $i.OpCode.Name -eq "callvirt") { $bad += "calls $($i.Operand)" }
  }
  if ($bad.Count -gt 0) { Fail "$n is no longer a pure bit flip: $($bad -join '; ')" }
  else { Ok "$n is a pure bit flip ($($m.Body.Instructions.Count) instr)" }
}

# --- 4. simulate the transpiler ------------------------------------------
$r = $sc.Methods | Where-Object { $_.Name -eq "ShipRenderersOnTick" } | Select-Object -First 1
$ins = @($r.Body.Instructions)
$hits = 0
$offs = @()
for ($k = 0; $k -lt $ins.Count - 8; $k++) {
  if ($ins[$k].OpCode.Name -ne "ldfld") { continue }
  if ("$($ins[$k].Operand)" -notmatch "idleShipIndices") { continue }
  if ($ins[$k + 1].OpCode.Name -ne "ldc.i4.1") { continue }
  if ($ins[$k + 2].OpCode.Name -ne "conv.i8") { continue }
  if ($ins[$k + 4].OpCode.Name -ne "ldc.i4.s") { continue }
  if ([int]$ins[$k + 4].Operand -ne 63) { continue }
  if ($ins[$k + 5].OpCode.Name -ne "and") { continue }
  if ($ins[$k + 6].OpCode.Name -ne "shl") { continue }
  if ($ins[$k + 7].OpCode.Name -ne "and") { continue }
  $hits++
  $offs += ("{0:X4}" -f $ins[$k].Offset)
}
if ($hits -ne 2) { Fail "ShipRenderersOnTick inline bit reads: found $hits, expected 2" }
else { Ok "ShipRenderersOnTick inline bit reads: 2 at $($offs -join ', ')" }

# the instruction the transpiler keeps must be the slot index (a load), or the
# rewritten call gets the wrong argument
for ($k = 0; $k -lt $ins.Count - 8; $k++) {
  if ($ins[$k].OpCode.Name -ne "ldfld") { continue }
  if ("$($ins[$k].Operand)" -notmatch "idleShipIndices") { continue }
  if ($ins[$k + 3].OpCode.Name -notlike "ldloc*") {
    Fail "slot index at +3 is $($ins[$k+3].OpCode.Name), expected an ldloc"
  }
}
Ok "slot index sits at +3 and is an ldloc in both sites"

# --- 5. the reconciler ----------------------------------------------------
$readsIdleCount = $false
$assertsZero = $false
foreach ($i in $ins) {
  if ($i.OpCode.Name -eq "ldfld" -and "$($i.Operand)" -match "idleShipCount") { $readsIdleCount = $true }
  if ("$($i.Operand)" -match "Assert::Zero") { $assertsZero = $true }
}
if (-not $readsIdleCount) { Fail "ShipRenderersOnTick no longer reads idleShipCount -- idle bits are not derived any more" }
else { Ok "reconciler still reads idleShipCount" }
if (-not $assertsZero) { Fail "ShipRenderersOnTick no longer ends in Assert.Zero" }
else { Ok "reconciler still asserts it converged" }

# it must be reached from InternalTickRemote, or remote planets never reconcile
$tick = $sc.Methods | Where-Object { $_.Name -eq "InternalTickRemote" } | Select-Object -First 1
$calls = @($tick.Body.Instructions | Where-Object { "$($_.Operand)" -match "ShipRenderersOnTick" })
if ($calls.Count -lt 1) { Fail "InternalTickRemote no longer calls ShipRenderersOnTick" }
else { Ok "InternalTickRemote calls ShipRenderersOnTick ($($calls.Count) site)" }

# --- 6. the save round-trips the array length -----------------------------
$ex = $sc.Methods | Where-Object { $_.Name -eq "Export" } | Select-Object -First 1
$exi = @($ex.Body.Instructions)
$wroteLen = $false
for ($k = 0; $k -lt $exi.Count - 3; $k++) {
  if ("$($exi[$k].Operand)" -match "workShipDatas" -and $exi[$k + 1].OpCode.Name -eq "ldlen" `
      -and "$($exi[$k + 3].Operand)" -match "Write") { $wroteLen = $true }
}
if (-not $wroteLen) { Fail "Export no longer writes workShipDatas.Length -- longer ship arrays would not round-trip" }
else { Ok "Export writes workShipDatas.Length" }

$im = $sc.Methods | Where-Object { $_.Name -eq "Import" } | Select-Object -First 1
$imi = @($im.Body.Instructions)
# The count does not feed newarr directly: it is
#   ReadInt32 ; stloc ; ldarg.0 ; ldloc ; newarr ShipData
# so scan a small window forward instead of pinning an offset. Pinning +3 is what
# made this check report a false FAIL the first time it ran.
$readLen = $false
for ($k = 0; $k -lt $imi.Count - 8; $k++) {
  if ("$($imi[$k].Operand)" -notmatch "ReadInt32") { continue }
  for ($j = $k + 1; $j -le $k + 6 -and $j -lt $imi.Count; $j++) {
    if ($imi[$j].OpCode.Name -eq "newarr" -and "$($imi[$j].Operand)" -match "ShipData") { $readLen = $true }
  }
}
if (-not $readLen) { Fail "Import no longer sizes workShipDatas from the stream" }
else { Ok "Import sizes workShipDatas from the stream" }

# --- 7. work bits are derivable from ShipData.shipIndex -------------------
$writers = New-Object System.Collections.Generic.List[string]
$readers = New-Object System.Collections.Generic.List[string]
foreach ($t in $mod.GetTypes()) {
  foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    foreach ($i in $m.Body.Instructions) {
      if ("$($i.Operand)" -notmatch "ShipData::shipIndex") { continue }
      if ($i.OpCode.Name -eq "stfld") { $writers.Add("$($t.Name)::$($m.Name)") | Out-Null }
      if ($i.OpCode.Name -eq "ldfld") { $readers.Add("$($t.Name)::$($m.Name)") | Out-Null }
    }
  }
}
if (-not ($writers -contains "ShipData::Import")) { Fail "ShipData.shipIndex is not read back from the save -- work bits are not derivable" }
else { Ok "ShipData.shipIndex is restored by ShipData.Import" }
foreach ($d in @("StationComponent::DispatchSupplyShip", "StationComponent::DispatchDemandShip")) {
  if (-not ($writers -contains $d)) { Fail "$d no longer stamps shipIndex" } else { Ok "$d stamps shipIndex" }
}
if (-not ($readers -contains "StationComponent::InternalTickRemote")) {
  Fail "InternalTickRemote no longer reads shipIndex when a ship docks"
} else { Ok "InternalTickRemote reads shipIndex on dock" }

# --- 8. vanilla still has no PatchShipArray -------------------------------
$patchers = @()
foreach ($t in $mod.GetTypes()) {
  foreach ($m in $t.Methods) { if ($m.Name -like "Patch*Array") { $patchers += "$($t.Name)::$($m.Name)" } }
}
if ($patchers -contains "StationComponent::PatchShipArray") {
  Fail "vanilla now has PatchShipArray -- StationShipExpandPatches is probably redundant, re-read it"
} else { Ok "still no PatchShipArray (expand patch is still needed); found: $($patchers -join ', ')" }

Write-Host ""
if ($fails -gt 0) { Write-Host "$fails check(s) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "all checks passed"
