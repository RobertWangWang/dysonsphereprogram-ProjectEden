# check_modplane.ps1 -- offline assertions behind PlanetModPlanePatches (foundation
# base plane on enlarged planets).
#
# PURE ASCII ONLY. Windows PowerShell reads .ps1 as ANSI.
#
# WHY THIS EXISTS: game 0.10.35 split PlanetData.UpdateDirtyMesh and moved the
# GetModPlane call into a new UpdateDirtyMeshVertices. The patch kept pointing at
# the old name, rewrote 2 of 3 consumers, and foundations on enlarged planets went
# back to pulling the ground to 200.2 -- the bug that once took seven rounds to find.
# It was caught only at launch, because this feature had no checker. This is it.
#
# The invariants, in the order the patch depends on them:
#
#   1. GetModPlane still returns Int16 -- that is WHY the fix is applied to the
#      three consumers rather than to GetModPlane itself (the correct value 40020
#      does not fit in an Int16)
#   2. there are exactly 3 consumers, and they are the 3 the patch targets
#   3. each consumer's call site is followed by conv.r4, which is the instruction
#      the transpiler rewrites
#   4. each target method still resolves with the exact signature TargetMethods uses
#   5. the 20020 literal is still inside GetModPlane, i.e. the offset really is
#      baked at radius 200

$ErrorActionPreference = "Stop"

$profileDir = Join-Path $env:APPDATA "r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx"
$gameAsm = "G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed\Assembly-CSharp.dll"

Add-Type -Path (Join-Path $profileDir "core\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAsm)
$mod = $asm.MainModule

$fails = 0
function Ok($m) { Write-Host "  OK   $m" }
function Fail($m) { Write-Host "  FAIL $m" -ForegroundColor Red; $script:fails++ }

# --- 1. GetModPlane's return type ----------------------------------------
$gmp = $null
foreach ($t in $mod.GetTypes()) {
  $c = $t.Methods | Where-Object { $_.Name -eq "GetModPlane" } | Select-Object -First 1
  if ($c) { $gmp = $c; $gmpType = $t.Name }
}
if ($null -eq $gmp) { Fail "GetModPlane is gone" }
elseif ($gmp.ReturnType.Name -ne "Int16") {
  Fail "GetModPlane returns $($gmp.ReturnType.Name), expected Int16 -- if it widened, fix IT instead of its consumers"
} else { Ok "$gmpType.GetModPlane returns Int16 (so the fix belongs on the consumers)" }

# --- 5. the 20020 literal ------------------------------------------------
if ($gmp) {
  $lit = @($gmp.Body.Instructions | Where-Object {
      $_.OpCode.Name -like "ldc.i4*" -and $null -ne $_.Operand -and [int]$_.Operand -eq 20020 })
  if ($lit.Count -ge 1) { Ok "GetModPlane still carries the 20020 literal ((200 + 0.2) * 100)" }
  else { Fail "20020 is gone from GetModPlane -- the whole premise (a radius-200 baked offset) needs re-reading" }
}

# --- 2/3. the consumers --------------------------------------------------
$expected = @(
  "PlanetModelingManager::ModelingPlanetMain",
  "PlanetData::UpdateDirtyMeshVertices",
  "PlanetRawData::QueryModifiedHeight"
)

$found = @()
foreach ($t in $mod.GetTypes()) {
  foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    $ins = @($m.Body.Instructions)
    for ($k = 0; $k -lt $ins.Count; $k++) {
      if ("$($ins[$k].Operand)" -notmatch "GetModPlane") { continue }
      $key = "$($t.Name)::$($m.Name)"
      $found += $key
      if ($k + 1 -lt $ins.Count -and $ins[$k + 1].OpCode.Name -eq "conv.r4") {
        Ok "$key @$('{0:X4}' -f $ins[$k].Offset) is followed by conv.r4 (the rewrite site)"
      } else {
        Fail "$key @$('{0:X4}' -f $ins[$k].Offset) is NOT followed by conv.r4 -- the transpiler anchor is gone"
      }
    }
  }
}

$missing = $expected | Where-Object { $found -notcontains $_ }
$extra = $found | Where-Object { $expected -notcontains $_ }
if ($missing) { Fail "consumers the patch targets but the game no longer has: $($missing -join ', ')" }
if ($extra) { Fail "NEW GetModPlane consumers the patch does not target: $($extra -join ', ') -- foundations will be wrong there" }
if (-not $missing -and -not $extra) { Ok "exactly the 3 expected consumers ($($found.Count) call sites)" }

# --- 4. the signatures TargetMethods uses --------------------------------
function Sig($typeName, $methodName, $paramTypes) {
  $t = $mod.GetType($typeName)
  if ($null -eq $t) { Fail "type $typeName is gone"; return }
  $cands = @($t.Methods | Where-Object { $_.Name -eq $methodName })
  if ($cands.Count -eq 0) { Fail "$typeName.$methodName is gone -- TargetMethods would resolve to null"; return }
  $match = @($cands | Where-Object {
      $ps = @($_.Parameters | ForEach-Object { $_.ParameterType.Name })
      ($ps -join ",") -eq ($paramTypes -join ",") })
  if ($match.Count -eq 1) { Ok "$typeName.$methodName($($paramTypes -join ', ')) resolves uniquely" }
  else { Fail "$typeName.$methodName($($paramTypes -join ', ')): $($match.Count) matches of $($cands.Count) overloads" }
}

Sig "PlanetModelingManager" "ModelingPlanetMain" @("PlanetData")
Sig "PlanetData" "UpdateDirtyMeshVertices" @("Int32")
Sig "PlanetRawData" "QueryModifiedHeight" @("Vector3")

Write-Host ""
if ($fails -gt 0) { Write-Host "$fails check(s) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "all checks passed"
