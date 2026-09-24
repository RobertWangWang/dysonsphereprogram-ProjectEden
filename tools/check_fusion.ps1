# check_fusion.ps1 -- offline premises of the fusion line (see FusionPatches.cs)
#
# WHY THIS EXISTS. The 0.10.35 update broke exactly the two features that had no offline
# checker, and broke nothing that had one. The fusion line rests on four measured facts
# about vanilla, none of which is enforced by the compiler:
#
#   1. AssemblerComponent.SetPCState computes the permillage as (1000 + extraPowerRatio).
#      Our postfix re-runs that formula scaled by 100. If vanilla changes the formula, the
#      postfix keeps compiling and silently charges the WRONG power -- no exception, no log.
#   2. PowerConsumerComponent.SetRequiredEnergy has three overloads. Resolving it by name
#      throws AmbiguousMatchException out of PatchAll, which takes the whole mod down. We
#      call (bool,int) explicitly; this asserts that overload still exists and still ASSIGNS
#      (not accumulates), because calling it a second time has to overwrite.
#   3. requiredEnergy / workEnergyPerTick are Int64, so 100x cannot wrap.
#   4. The generator half copies the Redox plant: SetNewFuel is public and fills fuelHeat
#      itself, EnergyCap_Fuel gates on fuelCount only (NOT fuelMask), and fuelCount is Int16
#      -- which is why FuelTarget must stay far below 32767.
#
# ASCII ONLY. Windows PowerShell reads .ps1 as ANSI; one non-ASCII character turns the whole
# file into parser errors that look nothing like an encoding problem.

param(
    [string]$Game = "G:\SteamLibrary\steamapps\common\Dyson Sphere Program"
)

$ErrorActionPreference = "Stop"

$profileDir = Join-Path $env:APPDATA "r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx"
Add-Type -Path (Join-Path $profileDir "core\Mono.Cecil.dll")

$asmPath = Join-Path $Game "DSPGAME_Data\Managed\Assembly-CSharp.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asmPath)
$mod = $asm.MainModule

$fail = 0

function Check([bool]$ok, [string]$msg) {
    if ($ok) { Write-Host "  OK   $msg" -ForegroundColor Green }
    else { Write-Host "  FAIL $msg" -ForegroundColor Red; $script:fail++ }
}

function FieldType($typeName, $fieldName) {
    $t = $mod.GetType($typeName)
    if ($null -eq $t) { return $null }
    $f = $t.Fields | Where-Object { $_.Name -eq $fieldName -and -not $_.IsStatic }
    if ($null -eq $f) { return $null }
    return $f.FieldType.Name
}

Write-Host ""
Write-Host "=== collider power: AssemblerComponent.SetPCState ===" -ForegroundColor Cyan

$ac = $mod.GetType("AssemblerComponent")
$setPc = @($ac.Methods | Where-Object { $_.Name -eq "SetPCState" })

Check ($setPc.Count -eq 1) "SetPCState resolves uniquely ($($setPc.Count) overload(s))"

if ($setPc.Count -eq 1) {
    $ins = @($setPc[0].Body.Instructions)
    $text = ($ins | ForEach-Object { "$($_.OpCode.Name) $($_.Operand)" }) -join " | "

    # the whole method is: pcPool[pcId].SetRequiredEnergy(replicating, 1000 + extraPowerRatio)
    Check ($text -match "ldfld .*AssemblerComponent::pcId") "indexes pcPool by pcId"
    Check ($text -match "ldfld .*AssemblerComponent::replicating") "passes replicating as the working flag"
    Check ($text -match "ldfld .*AssemblerComponent::extraPowerRatio") "folds in extraPowerRatio"

    # the literal 1000 is the base permillage our postfix multiplies
    $hasBase = $false
    foreach ($i in $ins) {
        if ($i.OpCode.Name -like "ldc.i4*" -and $null -ne $i.Operand) {
            if ([Convert]::ToInt32($i.Operand) -eq 1000) { $hasBase = $true }
        }
    }
    Check $hasBase "base permillage is still the literal 1000 (our x100 scales this)"

    $calls = @($ins | Where-Object { $_.OpCode.Name -eq "call" -or $_.OpCode.Name -eq "callvirt" })
    Check ($calls.Count -eq 1 -and $calls[0].Operand.Name -eq "SetRequiredEnergy") `
          "its only call is SetRequiredEnergy (so a postfix re-call is the whole fix)"
}

Write-Host ""
Write-Host "=== PowerConsumerComponent.SetRequiredEnergy ===" -ForegroundColor Cyan

$pc = $mod.GetType("PowerConsumerComponent")
$sre = @($pc.Methods | Where-Object { $_.Name -eq "SetRequiredEnergy" })

Check ($sre.Count -ge 2) "it is overloaded ($($sre.Count)) -- name-only resolution would be ambiguous"

$boolInt = $sre | Where-Object {
    $_.Parameters.Count -eq 2 -and
    $_.Parameters[0].ParameterType.Name -eq "Boolean" -and
    $_.Parameters[1].ParameterType.Name -eq "Int32"
}
Check ($null -ne $boolInt) "the (Boolean, Int32) overload we bind to still exists"

if ($null -ne $boolInt) {
    $b = ($boolInt.Body.Instructions | ForEach-Object { "$($_.OpCode.Name) $($_.Operand)" }) -join " | "
    Check ($b -match "stfld .*requiredEnergy") "it ASSIGNS requiredEnergy (so a second call overwrites)"

    # Match PER INSTRUCTION, not on the joined string: "ldfld .*requiredEnergy" run over
    # "ldfld ...idleEnergyPerTick | ... | stfld ...requiredEnergy" matches across the join and
    # reports a read that is not there. That false FAIL is what this comment is paying for.
    $readsSelf = $false
    foreach ($i in $boolInt.Body.Instructions) {
        if ($i.OpCode.Name -eq "ldfld" -and "$($i.Operand)" -match "requiredEnergy") { $readsSelf = $true }
    }
    Check (-not $readsSelf) "it does not read requiredEnergy first (no accumulation)"
    Check ($b -match "ldfld .*idleEnergyPerTick") "the not-working branch takes idleEnergyPerTick"
    Check ($b -match "div") "the working branch divides by the permillage base"
}

# every overload must assign, or 'call it again' stops being idempotent
$allAssign = $true
foreach ($m in $sre) {
    $t2 = ($m.Body.Instructions | ForEach-Object { $_.OpCode.Name }) -join " "
    if ($t2 -notmatch "stfld") { $allAssign = $false }
}
Check $allAssign "every SetRequiredEnergy overload assigns rather than accumulates"

Write-Host ""
Write-Host "=== widths: 100x must not wrap ===" -ForegroundColor Cyan

Check ((FieldType "PowerConsumerComponent" "requiredEnergy") -eq "Int64") "requiredEnergy is Int64"
Check ((FieldType "PowerConsumerComponent" "workEnergyPerTick") -eq "Int64") "workEnergyPerTick is Int64"

Write-Host ""
Write-Host "=== generator half (mirrors the Redox plant) ===" -ForegroundColor Cyan

$pg = $mod.GetType("PowerGeneratorComponent")
$snf = @($pg.Methods | Where-Object { $_.Name -eq "SetNewFuel" })

Check ($snf.Count -eq 1) "SetNewFuel resolves uniquely"

if ($snf.Count -eq 1) {
    Check ($snf[0].IsPublic) "SetNewFuel is public (no reflection needed)"
    $s = ($snf[0].Body.Instructions | ForEach-Object { "$($_.OpCode.Name) $($_.Operand)" }) -join " | "
    Check ($s -match "HeatValue") "SetNewFuel still fills fuelHeat from ItemProto.HeatValue itself"
}

# fuelCount is Int16 -- this is why FuelTarget is 3000 and not 30000
Check ((FieldType "PowerGeneratorComponent" "fuelCount") -eq "Int16") `
      "fuelCount is Int16 (FuelTarget must stay far below 32767)"

$ecf = $pg.Methods | Where-Object { $_.Name -eq "EnergyCap_Fuel" }
if ($null -ne $ecf) {
    $ei = @($ecf.Body.Instructions)

    # The BURN GATE is the first thing the method does: fuelCount > 0. That is the fact the
    # code-fed design rests on.
    $firstRead = $ei | Where-Object { $_.OpCode.Name -eq "ldfld" } | Select-Object -First 1
    Check ("$($firstRead.Operand)" -match "fuelCount") "the burn gate is the FIRST read, and it is fuelCount"

    # CLAUDE.md says "fuelMask is not consulted at burn time". Measured, that is imprecise:
    # the field IS read once, at ~008D, but only as `fuelMask == 4` selecting the artificial
    # star's boost branch -- it never decides whether the fuel burns. Assert that shape rather
    # than absence, because asserting absence produces a FAIL on a vanilla that is behaving
    # exactly as the design assumes. Our plant uses mask 128, so it skips that branch entirely.
    $maskReads = @()
    for ($i = 0; $i -lt $ei.Count; $i++) {
        if ($ei[$i].OpCode.Name -eq "ldfld" -and "$($ei[$i].Operand)" -match "fuelMask") { $maskReads += $i }
    }

    Check ($maskReads.Count -le 1) "fuelMask is read at most once ($($maskReads.Count))"

    if ($maskReads.Count -eq 1) {
        $n = $ei[$maskReads[0] + 1]
        $cmp = $ei[$maskReads[0] + 2]
        $isStarBranch = ($n.OpCode.Name -like "ldc.i4*") -and ($cmp.OpCode.Name -like "bne.un*")
        $val = if ($null -ne $n.Operand) { [Convert]::ToInt32($n.Operand) } else { [int]($n.OpCode.Name -replace '.*\.', '') }
        Check ($isStarBranch -and $val -eq 4) `
              "the only fuelMask read is the artificial-star special case (== 4), not a burn gate"
    }
} else {
    Check $false "EnergyCap_Fuel not found"
}

Write-Host ""
if ($fail -eq 0) {
    Write-Host "all checks passed" -ForegroundColor Green
    exit 0
}

Write-Host "$fail assertion(s) FAILED" -ForegroundColor Red
exit 1
