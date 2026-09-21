# Re-derive the output-gate sites that MegaOutputGatePatches rewrites.
#
# Run this after any game update, BEFORE trusting the transpiler.
# It reproduces the transpiler's exact match rule against the shipped assembly
# and asserts the expected count, so a moved constant is caught offline
# instead of as a loud-fail in game.
#
# ASCII only. Windows PowerShell reads .ps1 as ANSI, so one non-ASCII character
# turns the whole file into parser errors that look nothing like an encoding problem.

param(
    [string]$GameDir = "G:\SteamLibrary\steamapps\common\Dyson Sphere Program"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$cecil = Join-Path $env:APPDATA "r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx\core\Mono.Cecil.dll"
Add-Type -Path $cecil

$asm = Join-Path $GameDir "DSPGAME_Data\Managed\Assembly-CSharp.dll"
$m = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($asm).MainModule

$EXPECT_MUL = 7
$EXPECT_ADD = 2

$t = $m.GetType("AssemblerComponent")
$found = $false

foreach ($me in $t.Methods) {
    if ($me.Name -ne "InternalUpdate") { continue }
    $found = $true
    $ins = $me.Body.Instructions

    Write-Output ("AssemblerComponent::InternalUpdate  [" + $ins.Count + " instructions]")
    Write-Output ""

    $mul = 0
    $add = 0
    $other = 0

    for ($k = 0; $k -lt $ins.Count; $k++) {
        $op = $ins[$k].OpCode.Name
        if ($op -ne "ldc.i4.s" -and $op -ne "ldc.i4") { continue }
        $val = [int]$ins[$k].Operand
        if ($val -ne 9 -and $val -ne 19 -and $val -ne 100) { continue }

        $prev = ""
        if ($k -ge 1) { $prev = $ins[$k-1].OpCode.Name }
        $nx1 = ""
        if ($k + 1 -lt $ins.Count) { $nx1 = $ins[$k+1].OpCode.Name }
        $nx2 = ""
        if ($k + 2 -lt $ins.Count) { $nx2 = $ins[$k+2].OpCode.Name }

        # This is the transpiler's rule, verbatim:
        #   ldelem.i4 ; ldc.i4.s {9|19} ; mul ; ble*
        $isMul = ($prev -eq "ldelem.i4") -and ($nx1 -eq "mul") -and ($nx2 -like "ble*") `
                 -and ($val -eq 9 -or $val -eq 19)
        # The Smelt tier, deliberately NOT rewritten:
        #   add ; ldc.i4.s 100 ; ble*
        $isAdd = ($prev -eq "add") -and ($nx1 -like "ble*") -and ($val -eq 100)

        $tag = "OTHER-USE (not a gate - investigate)"
        if ($isMul) { $tag = "GATE-MUL     (rewritten)"; $mul++ }
        elseif ($isAdd) { $tag = "GATE-ADD     (Smelt, left alone)"; $add++ }
        else { $other++ }

        Write-Output ("  {0:X4}  ldc {1,3}   prev={2,-10} next={3},{4}   {5}" -f `
            $ins[$k].Offset, $val, $prev, $nx1, $nx2, $tag)
    }

    Write-Output ""
    Write-Output ("  GATE-MUL = {0} (expect {1})   GATE-ADD = {2} (expect {3})   OTHER-USE = {4} (expect 0)" -f `
        $mul, $EXPECT_MUL, $add, $EXPECT_ADD, $other)
    Write-Output ""

    $bad = $false
    if ($mul -ne $EXPECT_MUL) {
        Write-Output "FAIL: multiplicative gate count moved. MegaOutputGatePatches.ExpectedSites must be updated,"
        Write-Output "      and the new sites re-read by hand before trusting the new number."
        $bad = $true
    }
    if ($other -ne 0) {
        Write-Output "FAIL: a 9/19/100 in this method is NOT a gate. The shape test used to have zero false"
        Write-Output "      positives; matching on the constant alone would now rewrite unrelated code."
        $bad = $true
    }
    if ($add -ne $EXPECT_ADD) {
        Write-Output "WARN: the Smelt additive gate count moved. It is not rewritten, but the census"
        Write-Output "      (PlanetCensus.SumMegaCycles) reproduces its 100/counts arithmetic."
        # not fatal - nothing rewrites it
    }

    if ($bad) { exit 1 }

    Write-Output "OK: gate sites match what MegaOutputGatePatches expects."
}

if (-not $found) {
    Write-Output "FAIL: AssemblerComponent::InternalUpdate not found."
    exit 1
}
