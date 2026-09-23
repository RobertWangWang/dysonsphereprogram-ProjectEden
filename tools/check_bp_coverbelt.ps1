# Project Eden -- offline check for the "cover belt" rebuild gate.
#
# WHY THIS EXISTS
#   BuildTool_BlueprintPaste.CreatePrebuilds has TWO paths for a belt preview whose
#   coverObjId is non-zero:
#
#     (a) register into bpIdWitchWillRebuildCoverBelt  -- built later, in a second loop
#     (b) fall through to `if (coverObjId != 0) continue;` -- silently dropped
#
#   Path (a) requires SIX conditions, four of which are about the DOWNSTREAM belt.
#   BlueprintCoverBeltPatches reproduces those six conditions exactly so it only
#   touches previews vanilla would have dropped.  A hook that runs before vanilla
#   decides must reproduce that decision -- so if the shape below ever moves, the
#   reproduction is wrong and the patch must be re-derived rather than left running.
#
#   This script asserts the shape offline.  Run it after a game update.
#
# ASCII ONLY -- Windows PowerShell reads .ps1 as ANSI and one non-ASCII character
# turns the whole file into parser errors that look nothing like an encoding problem.

$ErrorActionPreference = "Stop"

$managed = $env:PROJECTEDEN_MANAGED
if (-not $managed) {
    $managed = "G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed"
}

$bepinex = $env:PROJECTEDEN_BEPINEX
if (-not $bepinex) {
    $bepinex = Join-Path $env:APPDATA "r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx"
}

$cecil = Join-Path $bepinex "core\Mono.Cecil.dll"
$dll = Join-Path $managed "Assembly-CSharp.dll"

if (-not (Test-Path $cecil)) { throw "Mono.Cecil not found: $cecil (set PROJECTEDEN_BEPINEX)" }
if (-not (Test-Path $dll))   { throw "Assembly-CSharp not found: $dll (set PROJECTEDEN_MANAGED)" }

Add-Type -Path $cecil
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll)
$type = $asm.MainModule.GetType("BuildTool_BlueprintPaste")
if ($null -eq $type) { throw "type BuildTool_BlueprintPaste not found" }

$m = $type.Methods | Where-Object { $_.Name -eq "CreatePrebuilds" }
if ($null -eq $m) { throw "CreatePrebuilds not found" }

$ins = @($m.Body.Instructions)
Write-Host ("CreatePrebuilds: {0} instructions" -f $ins.Count)

$fail = 0
function Bad($msg) { Write-Host ("FAIL: " + $msg); $script:fail++ }
function Good($msg) { Write-Host ("ok: " + $msg) }

# --- 1. classify the three reads of bpIdWitchWillRebuildCoverBelt ---------------
# There are THREE, and telling them apart by ORDER would be wrong -- classify each
# one by what the next few instructions DO with the array:
#   ... stelem.i4  -> the register site   (writes an index into the list)
#   ... ldelem.i4  -> the consume site    (reads an index back out)
#   ... stfld same -> the lazy-init site  (`if (arr == null) arr = new int[...]`)
# The init site is why an "expected exactly 2" check fails here: it is a real read
# that has nothing to do with either path.
$regLoads = @($ins | Where-Object {
    $_.OpCode.Name -eq "ldfld" -and $null -ne $_.Operand -and
    $_.Operand.Name -eq "bpIdWitchWillRebuildCoverBelt"
})

function ClassifyUse($load) {
    $cur = $load.Next
    for ($k = 0; $k -lt 24 -and $null -ne $cur; $k++) {
        switch -Wildcard ($cur.OpCode.Name) {
            "stelem*" { return "register" }
            "ldelem*" { return "consume" }
            "stfld"   { if ($cur.Operand.Name -eq "bpIdWitchWillRebuildCoverBelt") { return "init" } }
        }
        $cur = $cur.Next
    }
    return "unknown"
}

$register = $null
$consume = $null
$kinds = @()

foreach ($r in $regLoads) {
    $kind = ClassifyUse $r
    $kinds += ("{0:X4}={1}" -f $r.Offset, $kind)
    if ($kind -eq "register" -and $null -eq $register) { $register = $r }
    if ($kind -eq "consume") { $consume = $r }
}

if ($null -eq $register) {
    Bad ("no register site (ldfld bpIdWitchWillRebuildCoverBelt ... stelem) found -- sites: " + ($kinds -join ", "))
} elseif ($null -eq $consume) {
    Bad ("no consume site (ldfld bpIdWitchWillRebuildCoverBelt ... ldelem) found -- sites: " + ($kinds -join ", "))
} else {
    Good ("bpIdWitchWillRebuildCoverBelt sites: " + ($kinds -join ", "))
}

# --- 2. the six conditions, in order, immediately before the register site -------
# Expected field-read sequence (see BlueprintCoverBeltPatches.WillRebuild):
#   coverObjId, isBelt, output, output.isBelt, output.coverObjId, output.condition
if ($null -ne $register) {
    # The gate STARTS at its own `ldfld coverObjId`, not at a fixed byte offset.
    # A fixed window drags in the tail of the preceding addonType block, which also
    # reads `condition` -- and then the count check below fires on vanilla code that
    # never moved.  Anchor on the earliest coverObjId read inside a generous window.
    $probe = @($ins | Where-Object {
        $_.Offset -lt $register.Offset -and $_.Offset -ge ($register.Offset - 0x60) -and
        $_.OpCode.Name -eq "ldfld" -and $null -ne $_.Operand -and $_.Operand.Name -eq "coverObjId"
    })

    if ($probe.Count -lt 1) {
        Bad "no coverObjId read in the 0x60 bytes before the register site -- the gate moved"
        $names = @()
    } else {
        $start = ($probe | Sort-Object Offset | Select-Object -First 1).Offset
        $window = @($ins | Where-Object { $_.Offset -ge $start -and $_.Offset -lt $register.Offset })
        $names = @($window | Where-Object { $_.OpCode.Name -eq "ldfld" } | ForEach-Object { $_.Operand.Name })
        Write-Host ("    gate spans {0:X4}..{1:X4}" -f $start, $register.Offset)
    }

    # Measured on 0.10.34.28529:
    #   coverObjId,desc,isBelt,output,output,desc,isBelt,output,coverObjId,output,condition
    # The exact repetition depends on how the compiler reloads `bp.output`, so the
    # assertions below test PRESENCE and the opening field, not the literal sequence.
    $got = ($names -join ",")

    # The exact repetition depends on how the compiler reloads `bp.output`; what must
    # hold is that all five distinct fields appear and coverObjId comes first.
    $needed = @("coverObjId", "isBelt", "output", "condition")
    $missing = @($needed | Where-Object { $names -notcontains $_ })

    if ($missing.Count -gt 0) {
        Bad ("the gate before the register site no longer reads: " + ($missing -join ", ") + " -- actual: " + $got)
    } elseif ($names[0] -ne "coverObjId") {
        Bad ("the gate no longer opens on coverObjId -- actual first read: " + $names[0])
    } else {
        Good ("gate reads coverObjId / isBelt / output / condition before registering: " + $got)
    }

    # how many times `condition` is read there -- vanilla reads the DOWNSTREAM one once
    $condCount = @($names | Where-Object { $_ -eq "condition" }).Count
    if ($condCount -ne 1) {
        Bad ("expected exactly 1 condition read in the gate, got " + $condCount + " -- the downstream test may have changed")
    } else {
        Good "exactly 1 condition read in the gate (the downstream belt's)"
    }
}

# --- 3. the drop site: `if (coverObjId != 0) continue;` --------------------------
# It must sit AFTER the register site, and its branch must jump forward (the continue).
$covReads = @($ins | Where-Object {
    $_.OpCode.Name -eq "ldfld" -and $null -ne $_.Operand -and $_.Operand.Name -eq "coverObjId"
})

if ($covReads.Count -lt 3) {
    Bad ("expected at least 3 coverObjId reads (own, downstream, drop gate), got " + $covReads.Count)
} else {
    Good ("coverObjId read at " + $covReads.Count + " sites: " + (($covReads | ForEach-Object { "{0:X4}" -f $_.Offset }) -join ", "))
}

$drop = $null
foreach ($c in $covReads) {
    if ($null -ne $register -and $c.Offset -le $register.Offset) { continue }
    $next = $c.Next
    if ($null -ne $next -and $next.OpCode.Name -like "brtrue*") { $drop = $c; break }
}

if ($null -eq $drop) {
    Bad "no `ldfld coverObjId ; brtrue` drop gate found after the register site -- the silent-drop path moved"
} else {
    $target = $drop.Next.Operand
    if ($target.Offset -le $drop.Offset) {
        Bad ("the drop gate branches BACKWARD to {0:X4} -- that is a loop, not a continue" -f $target.Offset)
    } else {
        Good ("drop gate at {0:X4}: ldfld coverObjId ; brtrue -> {1:X4} (forward = continue)" -f $drop.Offset, $target.Offset)
    }
}

# --- 4. the consumer loop actually builds a PrebuildData -------------------------
if ($null -ne $consume) {
    $after = @($ins | Where-Object { $_.Offset -gt $consume.Offset -and $_.Offset -lt ($consume.Offset + 0x80) })
    $hasInit = @($after | Where-Object { $_.OpCode.Name -eq "initobj" -and "$($_.Operand)" -match "PrebuildData" }).Count

    if ($hasInit -lt 1) {
        Bad "the second read of bpIdWitchWillRebuildCoverBelt is not followed by `initobj PrebuildData` -- the rebuild path no longer builds anything"
    } else {
        Good ("rebuild path at {0:X4} builds a PrebuildData" -f $consume.Offset)
    }
}

Write-Host ""
if ($fail -gt 0) {
    Write-Host ("$fail check(s) FAILED -- BlueprintCoverBeltPatches.WillRebuild must be re-derived from the IL before trusting it.")
    exit 1
}

Write-Host "all checks passed"
