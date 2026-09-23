# check_lens.ps1 -- offline assertions behind LensPatches (the living lens).
#
# PURE ASCII ONLY. Windows PowerShell reads .ps1 as ANSI; one non-ASCII character
# turns the file into parser errors that look nothing like an encoding problem.
#
# WHY THIS EXISTS: game 0.10.35 rebuilt the ray receiver's catalyst slot into a
# fuel-like data system, and this feature had NO offline checker -- so it was only
# caught at launch, by the rewrite counter refusing to apply (5 of 7 sites).
# Everything with a checker survived the same update. This closes that gap.
#
# The feature now rests on two separate things, and both are asserted here:
#
#   DATA (no transpiler any more)
#     1. ItemProto.CatalystType / Ability / catalystAbilityById / catalystNeeds exist
#     2. InitCatalystAbilityById still computes Ability * 0.01 and still gates on
#        CatalystType > 0  -- if that changes, our Ability value means something else
#     3. both builders still run from VFPreload.PreloadThread, i.e. BEFORE LDBTool,
#        so re-running them on PostAddDataAction is still required (trap 4b)
#     4. the generator still reads catalystAbilityById in all three power methods
#
#   TRANSPILERS (the three that remain)
#     5. GameTick_Gamma still has exactly 1 powerProductHeat read (photon divisor)
#     6. GameTick_Gamma still has exactly 2 PickFrom belt ports
#     7. EntityFastFillIn now consults catalystNeeds -- proof the old single-id
#        transpiler is genuinely obsolete rather than merely broken

$ErrorActionPreference = "Stop"

$profileDir = Join-Path $env:APPDATA "r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx"
$gameAsm = "G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed\Assembly-CSharp.dll"

Add-Type -Path (Join-Path $profileDir "core\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($gameAsm)
$mod = $asm.MainModule

$fails = 0
function Ok($m) { Write-Host "  OK   $m" }
function Fail($m) { Write-Host "  FAIL $m" -ForegroundColor Red; $script:fails++ }

$ip = $mod.GetType("ItemProto")
$pg = $mod.GetType("PowerGeneratorComponent")

# --- 1. the data members exist -------------------------------------------
foreach ($n in @("CatalystType", "Ability")) {
  if ($ip.Fields | Where-Object { $_.Name -eq $n }) { Ok "ItemProto.$n exists" }
  else { Fail "ItemProto.$n is gone -- the multiplier is no longer data" }
}
foreach ($n in @("catalystAbilityById", "catalystNeeds")) {
  if ($ip.Fields | Where-Object { $_.Name -eq $n }) { Ok "ItemProto.$n exists" }
  else { Fail "ItemProto.$n is gone" }
}

# --- 2. Ability * 0.01, gated on CatalystType > 0 -------------------------
$b = $ip.Methods | Where-Object { $_.Name -eq "InitCatalystAbilityById" } | Select-Object -First 1
if ($null -eq $b) { Fail "ItemProto.InitCatalystAbilityById is gone" }
else {
  $ins = @($b.Body.Instructions)
  $gate = $false; $scale = $false
  for ($k = 0; $k -lt $ins.Count; $k++) {
    if ("$($ins[$k].Operand)" -match "ItemProto::CatalystType") { $gate = $true }
    if ($ins[$k].OpCode.Name -eq "ldc.r4" -and [math]::Abs([double]$ins[$k].Operand - 0.01) -lt 1e-9) {
      if ($k + 1 -lt $ins.Count -and $ins[$k + 1].OpCode.Name -eq "mul") { $scale = $true }
    }
  }
  if ($gate) { Ok "InitCatalystAbilityById still gates on CatalystType" }
  else { Fail "InitCatalystAbilityById no longer reads CatalystType -- our CatalystType write may be pointless" }
  if ($scale) { Ok "InitCatalystAbilityById still computes Ability * 0.01" }
  else { Fail "the 0.01 scale is gone -- our Ability value now means something else (x5 would not be x5)" }
}

# --- 3. both builders still run at preload, before LDBTool ---------------
$pre = $null
foreach ($t in $mod.GetTypes()) {
  foreach ($n in $t.NestedTypes) {
    if ($n.Name -like "*PreloadThread*") { $pre = $n }
  }
  if ($t.Name -like "*PreloadThread*") { $pre = $t }
}
if ($null -eq $pre) { Fail "cannot find VFPreload's PreloadThread state machine" }
else {
  $mm = $pre.Methods | Where-Object { $_.Name -eq "MoveNext" } | Select-Object -First 1
  $found = @()
  foreach ($i in $mm.Body.Instructions) {
    if ("$($i.Operand)" -match "InitCatalystNeeds") { $found += "InitCatalystNeeds@{0:X4}" -f $i.Offset }
    if ("$($i.Operand)" -match "InitCatalystAbilityById") { $found += "InitCatalystAbilityById@{0:X4}" -f $i.Offset }
  }
  if ($found.Count -eq 2) { Ok "both builders run at preload ($($found -join ', ')) -- the PostAddData rerun is still required" }
  else { Fail "expected both catalyst builders in PreloadThread, found: $($found -join ', ')" }
}

# --- 4. the three power methods read the ability table --------------------
foreach ($n in @("EnergyCap_Gamma_Req", "MaxOutputCurrent_Gamma", "RequiresCurrent_Gamma")) {
  $m = $pg.Methods | Where-Object { $_.Name -eq $n } | Select-Object -First 1
  if ($null -eq $m) { Fail "$n is gone"; continue }
  $uses = @($m.Body.Instructions | Where-Object { "$($_.Operand)" -match "catalystAbilityById" })
  if ($uses.Count -ge 1) { Ok "$n reads catalystAbilityById ($($uses.Count) site)" }
  else { Fail "$n no longer reads catalystAbilityById -- the multiplier is not data any more, a transpiler may be needed again" }
  # and the old hardcoded 2 must NOT be back
  $two = @($m.Body.Instructions | Where-Object {
      $_.OpCode.Name -eq "ldc.r4" -and [math]::Abs([double]$_.Operand - 2.0) -lt 1e-6 })
  if ($two.Count -gt 0) { Fail "$n has a literal 2.0 again ($($two.Count)) -- re-read the IL, the old model may be back" }
}

# --- 5/6. the transpilers that remain ------------------------------------
$g = $pg.Methods | Where-Object { $_.Name -eq "GameTick_Gamma" } | Select-Object -First 1
if ($null -eq $g) { Fail "GameTick_Gamma is gone" }
else {
  # The divisor is PowerGeneratorComponent.productHeat, NOT PrefabDesc.powerProductHeat.
  # Searching the prefab's name finds nothing and reports a false FAIL -- which is
  # exactly what this check did on its first run, while the game log showed the
  # transpiler applying cleanly. Confirm a failure is real before believing it.
  $heat = @($g.Body.Instructions | Where-Object { "$($_.Operand)" -match "PowerGeneratorComponent::productHeat" })
  if ($heat.Count -eq 1) { Ok "GameTick_Gamma has exactly 1 productHeat read (photon divisor)" }
  else { Fail "productHeat sites: $($heat.Count), expected 1 -- photon multiplier will loud-fail" }

  # There are FOUR PickFrom calls, and only two are catalyst ports: those are the
  # ones whose filter argument is `ldfld catalystId`. The other two pass a local.
  # Counting all four is the second false FAIL this check produced -- the real
  # transpiler discriminates the same way, which is why it reported 2 and was right.
  $ins2 = @($g.Body.Instructions)
  $cata = 0
  $total = 0
  for ($k = 0; $k -lt $ins2.Count; $k++) {
    if ("$($ins2[$k].Operand)" -notmatch "::PickFrom") { continue }
    $total++
    for ($j = [math]::Max(0, $k - 6); $j -lt $k; $j++) {
      if ("$($ins2[$j].Operand)" -match "PowerGeneratorComponent::catalystId") { $cata++; break }
    }
  }
  if ($cata -eq 2) { Ok "GameTick_Gamma has exactly 2 catalyst PickFrom ports (of $total PickFrom calls)" }
  else { Fail "catalyst PickFrom ports: $cata of $total, expected 2 -- belt feeding will loud-fail" }
}

# --- 7. insertion now goes through the whitelist -------------------------
$ff = $mod.GetType("PlanetFactory").Methods | Where-Object { $_.Name -eq "EntityFastFillIn" } | Select-Object -First 1
if ($null -eq $ff) { Fail "PlanetFactory.EntityFastFillIn is gone" }
else {
  $needs = @($ff.Body.Instructions | Where-Object { "$($_.Operand)" -match "catalystNeeds" })
  if ($needs.Count -ge 1) {
    Ok "EntityFastFillIn consults catalystNeeds -- the old single-id transpiler is obsolete, not broken"
  } else {
    Fail "EntityFastFillIn no longer consults catalystNeeds -- shift-click insertion may need a patch again"
  }
}

Write-Host ""
if ($fails -gt 0) { Write-Host "$fails check(s) FAILED" -ForegroundColor Red; exit 1 }
Write-Host "all checks passed"
