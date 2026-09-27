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
    # Offsets of the gate sites, needed by the pending-cycle checks further down
    # (those ask "is this before or after the gate", so a count is not enough).
    $gateOffsets = New-Object System.Collections.ArrayList

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
        if ($isMul) { $tag = "GATE-MUL     (rewritten)"; $mul++; [void]$gateOffsets.Add([int]$ins[$k].Offset) }
        elseif ($isAdd) { $tag = "GATE-ADD     (Smelt, left alone)"; $add++; [void]$gateOffsets.Add([int]$ins[$k].Offset) }
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

    # ------------------------------------------------------------------
    # The pending-cycle hole that MegaOutputGatePatches.SettleRefused covers.
    #
    # Vanilla clears `replicating` BEFORE it consults the gate, and the gate's
    # refusal path is a bare `ldc.i4.0 ; ret` that restores neither `replicating`
    # nor `time`. Vanilla is self-consistent only because a refused machine keeps
    # `time >= timeSpend` and therefore re-enters the settle block forever,
    # never reaching the `if (replicating)` that guards input consumption.
    # MegaThrottle.Hold forces `time` negative, which IS the route to that guard.
    #
    # Four facts the fix rests on. If a game update moves any of them, the
    # discriminator (`!replicating && time >= timeSpend`) stops meaning
    # "the gate refused" and the fix silently stops working -- or worse,
    # starts suppressing a legitimate input charge.
    $ins = @($me.Body.Instructions)

    # (1) the settle entry: `ldfld time ; ldloc.0 ; blt`
    $settleEntry = -1
    # (2) `stfld replicating` with a preceding ldc.i4.0, before the first gate
    $clearRep = -1
    # (3) `ldfld replicating ; brtrue` -- the guard in front of the input block
    $repGuard = -1
    # (4) `stfld replicating` with a preceding ldc.i4.1 -- end of the input block
    $setRep = -1
    # (5) `time -= timeSpend`, which must sit AFTER the gate
    $timeDec = -1

    for ($i = 0; $i -lt $ins.Count; $i++) {
        $op = $ins[$i].OpCode.Name
        $od = $ins[$i].Operand

        if ($null -eq $od) { continue }
        $nm = $od.ToString()

        if ($op -eq "stfld" -and $nm -match "AssemblerComponent::replicating") {
            $prev = $ins[$i - 1].OpCode.Name
            if ($prev -eq "ldc.i4.0" -and $clearRep -lt 0) { $clearRep = $ins[$i].Offset }
            if ($prev -eq "ldc.i4.1") { $setRep = $ins[$i].Offset }
        }

        if ($op -eq "ldfld" -and $nm -match "AssemblerComponent::replicating") {
            if ($ins[$i + 1].OpCode.Name -like "brtrue*" -and $repGuard -lt 0) {
                $repGuard = $ins[$i].Offset
            }
        }

        if ($op -eq "ldfld" -and $nm -match "AssemblerComponent::time" -and $settleEntry -lt 0) {
            if ($ins[$i + 2].OpCode.Name -like "blt*") { $settleEntry = $ins[$i].Offset }
        }

        if ($op -eq "stfld" -and $nm -match "AssemblerComponent::time" -and $timeDec -lt 0) {
            if ($ins[$i - 1].OpCode.Name -eq "sub") { $timeDec = $ins[$i].Offset }
        }
    }

    # the first gate site, whichever kind it is
    $firstGate = -1
    if ($gateOffsets.Count -gt 0) {
        # Measure-Object hands back a Double, and "{0:X4}" cannot format one.
        $firstGate = [int]($gateOffsets | Measure-Object -Minimum).Minimum
    }

    Write-Output ""
    Write-Output "  pending-cycle invariant (MegaOutputGatePatches.SettleRefused):"
    Write-Output ("    settle entry  (time < timeSpend -> skip)  {0:X4}" -f $settleEntry)
    Write-Output ("    replicating = false                       {0:X4}" -f $clearRep)
    Write-Output ("    first gate site                           {0:X4}" -f $firstGate)
    Write-Output ("    time -= timeSpend                         {0:X4}" -f $timeDec)
    Write-Output ("    if (replicating) -> skip input block      {0:X4}" -f $repGuard)
    Write-Output ("    replicating = true                        {0:X4}" -f $setRep)
    Write-Output ""

    foreach ($p in @(
        @{ n = "settle entry";       v = $settleEntry },
        @{ n = "replicating=false";  v = $clearRep },
        @{ n = "time -= timeSpend";  v = $timeDec },
        @{ n = "if (replicating)";   v = $repGuard },
        @{ n = "replicating=true";   v = $setRep })) {
        if ($p.v -lt 0) {
            Write-Output ("FAIL: could not locate " + $p.n + " -- SettleRefused's discriminator is no longer verifiable.")
            $bad = $true
        }
    }

    if (-not $bad -and $firstGate -ge 0) {
        if ($clearRep -ge $firstGate) {
            Write-Output 'PASS-CHANGED: vanilla now clears "replicating" AFTER the gate. The hole is gone;'
            Write-Output '      SettleRefused is then dead code and should be re-read before removal.'
        }
        if ($timeDec -lt $firstGate) {
            Write-Output 'FAIL: "time -= timeSpend" now runs BEFORE the gate. A refused settle would then'
            Write-Output '      leave time < timeSpend, so "!replicating && time >= timeSpend" no longer'
            Write-Output '      identifies a refusal -- SettleRefused must be re-derived.'
            $bad = $true
        }
        if ($repGuard -lt $firstGate) {
            Write-Output 'FAIL: the "if (replicating)" guard now sits BEFORE the gate. Re-read MegaTick.'
            $bad = $true
        }
    }

    # The refusal path must not restore "replicating" -- that is what makes the
    # stale flag reachable at all. Walk each gate's own `ret` and assert nothing
    # writes replicating between the gate constant and it.
    $restored = 0

    foreach ($go in $gateOffsets) {
        for ($i = 0; $i -lt $ins.Count; $i++) {
            if ([int]$ins[$i].Offset -ne $go) { continue }

            # gate shape: ldc {9|19|100} ; mul/nothing ; ble -> pass ; ldc.i4.0 ; ret
            for ($j = $i; $j -lt [Math]::Min($i + 6, $ins.Count); $j++) {
                if ($ins[$j].OpCode.Name -ne "ret") { continue }

                for ($k = $i; $k -lt $j; $k++) {
                    $o = $ins[$k].Operand

                    if ($ins[$k].OpCode.Name -eq "stfld" -and $null -ne $o `
                        -and $o.ToString() -match "AssemblerComponent::replicating") {
                        $restored++
                    }
                }

                break
            }

            break
        }
    }

    if ($restored -gt 0) {
        Write-Output ("PASS-CHANGED: {0} gate refusal path(s) now restore replicating themselves." -f $restored)
        Write-Output "      The hole is closed upstream; re-read SettleRefused before keeping it."
    }

    if ($bad) { exit 1 }

    Write-Output "OK: gate sites match what MegaOutputGatePatches expects."
    Write-Output "OK: the pending-cycle hole is still shaped the way SettleRefused assumes."
}

if (-not $found) {
    Write-Output "FAIL: AssemblerComponent::InternalUpdate not found."
    exit 1
}
