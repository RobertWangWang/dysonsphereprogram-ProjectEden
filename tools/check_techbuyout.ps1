# check_techbuyout.ps1 -- offline premises of the tech buyout cascade (TechBuyoutCascadePatches)
#
# WHY THIS EXISTS. The 0.10.35 update broke exactly the two features that had no offline checker
# and broke nothing that had one. This feature rests on four measured facts, none of which the
# compiler enforces:
#
#   1. HasPreTechUnlocked consults BOTH TechProto.PreTechs and TechProto.PreTechsImplicit
#      (a for (j = 0; j < 2; j++) that swaps the array on j == 1). Our closure walk copies that.
#      Walk only PreTechs and the cascade silently misses implicit prerequisites -- vanilla's own
#      gate then still refuses, and the symptom is "the button does nothing".
#   2. UITechNode.OnBuyoutButtonClick calls HasPreTechUnlocked exactly ONCE and
#      CheckPropertyAdequateForBuyout exactly ONCE. The transpiler swaps one operand each and
#      refuses to apply on any other count.
#   3. Both swapped methods are instance (GameHistoryData, int) -> bool, so replacing the operand
#      with a static (GameHistoryData, int) -> bool leaves the stack shape and every branch label
#      untouched. Different arity would corrupt the body.
#   4. BuyoutTech still gates on HasPreTechUnlocked -- that gate is what the prefix satisfies by
#      buying the prerequisites first, rather than by bypassing anything.
#
# ASCII ONLY. Windows PowerShell reads .ps1 as ANSI; one non-ASCII character turns the whole file
# into parser errors that look nothing like an encoding problem.

param(
    [string]$Game = "G:\SteamLibrary\steamapps\common\Dyson Sphere Program"
)

$ErrorActionPreference = "Stop"

$profileDir = Join-Path $env:APPDATA "r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx"
Add-Type -Path (Join-Path $profileDir "core\Mono.Cecil.dll")

$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $Game "DSPGAME_Data\Managed\Assembly-CSharp.dll"))
$mod = $asm.MainModule

$fail = 0

function Check([bool]$ok, [string]$msg) {
    if ($ok) { Write-Host "  OK   $msg" -ForegroundColor Green }
    else { Write-Host "  FAIL $msg" -ForegroundColor Red; $script:fail++ }
}

function Uniq($type, $name) {
    $t = $mod.GetType($type)
    if ($null -eq $t) { return $null }
    $m = @($t.Methods | Where-Object { $_.Name -eq $name })
    if ($m.Count -ne 1) { return $null }
    return $m[0]
}

Write-Host ""
Write-Host "=== data shape ===" -ForegroundColor Cyan

$tp = $mod.GetType("TechProto")
foreach ($f in @("PreTechs", "PreTechsImplicit")) {
    $fd = $tp.Fields | Where-Object { $_.Name -eq $f }
    Check ($null -ne $fd -and $fd.FieldType.FullName -eq "System.Int32[]") "TechProto.$f is Int32[]"
}

$ts = $mod.GetType("TechState")
$un = $ts.Fields | Where-Object { $_.Name -eq "unlocked" }
Check ($null -ne $un -and $un.FieldType.Name -eq "Boolean") "TechState.unlocked is Boolean"

Write-Host ""
Write-Host "=== HasPreTechUnlocked: the predicate we copy ===" -ForegroundColor Cyan

$hp = Uniq "GameHistoryData" "HasPreTechUnlocked"
Check ($null -ne $hp) "GameHistoryData.HasPreTechUnlocked resolves uniquely"

if ($null -ne $hp) {
    Check ($hp.Parameters.Count -eq 1 -and $hp.Parameters[0].ParameterType.Name -eq "Int32" `
           -and $hp.ReturnType.Name -eq "Boolean" -and -not $hp.IsStatic) `
          "it is instance (Int32) -> Boolean (so our static (GameHistoryData, Int32) -> Boolean matches the stack)"

    $reads = @($hp.Body.Instructions | Where-Object { $_.OpCode.Name -eq "ldfld" } |
               ForEach-Object { "$($_.Operand)" })

    Check (($reads | Where-Object { $_ -match "TechProto::PreTechs$" }).Count -ge 1) `
          "it reads TechProto.PreTechs"
    Check (($reads | Where-Object { $_ -match "PreTechsImplicit" }).Count -ge 1) `
          "it reads TechProto.PreTechsImplicit -- BOTH tables, which is what the closure must walk"
    Check (($reads | Where-Object { $_ -match "TechState::unlocked" }).Count -ge 1) `
          "it tests TechState.unlocked"

    # the two-table sweep is a `for (j = 0; j < 2; j++)`; the literal 2 is its bound
    $two = $false
    foreach ($i in $hp.Body.Instructions) {
        if ($i.OpCode.Name -eq "ldc.i4.2") { $two = $true }
    }
    Check $two "the two-table sweep bound (ldc.i4.2) is still there"
}

Write-Host ""
Write-Host "=== BuyoutTech: the engine-side gate the prefix satisfies ===" -ForegroundColor Cyan

$bt = Uniq "GameHistoryData" "BuyoutTech"
Check ($null -ne $bt) "GameHistoryData.BuyoutTech resolves uniquely"

if ($null -ne $bt) {
    $calls = @($bt.Body.Instructions | Where-Object { $_.Operand -ne $null } |
               ForEach-Object { "$($_.Operand)" })

    Check (($calls | Where-Object { $_ -match "HasPreTechUnlocked" }).Count -eq 1) `
          "it still gates on HasPreTechUnlocked exactly once (the prefix satisfies it, never bypasses it)"
    Check (($calls | Where-Object { $_ -match "CheckPropertyAdequateForBuyout" }).Count -eq 1) `
          "it still checks CheckPropertyAdequateForBuyout once"
    Check (($calls | Where-Object { $_ -match "UnlockTechUnlimited\(" }).Count -ge 1) `
          "it still ends by unlocking the tech"
}

Write-Host ""
Write-Host "=== OnBuyoutButtonClick: the two operands the transpiler swaps ===" -ForegroundColor Cyan

$ob = Uniq "UITechNode" "OnBuyoutButtonClick"
Check ($null -ne $ob) "UITechNode.OnBuyoutButtonClick resolves uniquely"

if ($null -ne $ob) {
    $pre = 0
    $cost = 0

    foreach ($i in $ob.Body.Instructions) {
        if ($null -eq $i.Operand) { continue }
        $o = "$($i.Operand)"
        if ($o -match "GameHistoryData::HasPreTechUnlocked") { $pre++ }
        if ($o -match "GameHistoryData::CheckPropertyAdequateForBuyout") { $cost++ }
    }

    Check ($pre -eq 1) "exactly 1 call to HasPreTechUnlocked (transpiler expects 1, got $pre)"
    Check ($cost -eq 1) "exactly 1 call to CheckPropertyAdequateForBuyout (transpiler expects 1, got $cost)"

    # it must still be the thing that refuses, i.e. the popup is still there -- if vanilla ever
    # stops refusing on its own, this whole feature is unnecessary and should be re-read
    $ldstr = @($ob.Body.Instructions | Where-Object { $_.OpCode.Name -eq "ldstr" }).Count
    Check ($ldstr -ge 2) "it still pops localized refusals ($ldstr ldstr) -- vanilla still gates here"
}

$cp = Uniq "GameHistoryData" "CheckPropertyAdequateForBuyout"
Check ($null -ne $cp) "GameHistoryData.CheckPropertyAdequateForBuyout resolves uniquely"

if ($null -ne $cp) {
    Check ($cp.Parameters.Count -eq 1 -and $cp.Parameters[0].ParameterType.Name -eq "Int32" `
           -and $cp.ReturnType.Name -eq "Boolean" -and -not $cp.IsStatic) `
          "it is instance (Int32) -> Boolean, same shape as our replacement"
}

Write-Host ""
if ($fail -eq 0) {
    Write-Host "all checks passed" -ForegroundColor Green
    exit 0
}

Write-Host "$fail assertion(s) FAILED" -ForegroundColor Red
exit 1
