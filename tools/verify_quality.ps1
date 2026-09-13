# Pure ASCII only. Windows PowerShell reads .ps1 as ANSI, so one non-ASCII character
# turns the whole file into parser errors that look nothing like an encoding problem.
#
# Item quality, stage one: run the ANALYZER against Assembly-CSharp and print what it
# found. It never writes the assembly and never touches the game install.
#
# The counts it prints are the acceptance criteria for the rewrite half: when that lands,
# its hit counts must equal PURE MOVEMENT here, exactly, or nothing is rewritten.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\verify_quality.ps1
#   powershell -ExecutionPolicy Bypass -File tools\verify_quality.ps1 -Config Release

param(
    [string]$Config = "Debug",
    [string]$GameDir = "G:\SteamLibrary\steamapps\common\Dyson Sphere Program",
    [string]$BepInExDir = "$env:APPDATA\r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx"
)

$ErrorActionPreference = "Stop"

$root    = Split-Path -Parent $PSScriptRoot
$preload = Join-Path $root "ProjectEden.Preloader\bin\$Config\ProjectEden.Preloader.dll"
$managed = Join-Path $GameDir "DSPGAME_Data\Managed"
$target  = Join-Path $managed "Assembly-CSharp.dll"

foreach ($p in @($preload, $target)) {
    if (-not (Test-Path $p)) { Write-Host "MISSING: $p" -ForegroundColor Red; exit 1 }
}

# Load by bytes, not Add-Type -Path: that would lock the files and break the next build.
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes("$BepInExDir\core\Mono.Cecil.dll"))
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes("$BepInExDir\core\Mono.Cecil.Rocks.dll"))
$pre = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($preload))

# Cecil must resolve the game's other assemblies (UnityEngine etc.) while reading.
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($managed)
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver

Write-Host "preloader : $preload"
Write-Host "assembly  : $target  (read only)"
Write-Host ""

$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($target, $rp)

$type    = $pre.GetType("ProjectEden.Preloader.QualityFieldAnalyzer")
$analyze = $type.GetMethod("Analyze", [Reflection.BindingFlags]"NonPublic,Static")
$r       = $analyze.Invoke($null, @($asm.MainModule))

function Field($o, $n) {
  $f = $o.GetType().GetField($n, [Reflection.BindingFlags]"NonPublic,Instance,Public")
  return $f.GetValue($o)
}

$notes    = Field $r "Notes"
$blockers = Field $r "Blockers"
$suspects = Field $r "Suspects"
$mixed    = Field $r "MixedFound"

Write-Host "=== notes ===" -ForegroundColor Cyan
foreach ($n in $notes) { Write-Host "  $n" }

Write-Host ""
Write-Host "=== counts ===" -ForegroundColor Cyan
Write-Host ("  payload fields          : {0}" -f (Field $r "PayloadFields"))
Write-Host ("  PURE MOVEMENT           : {0} methods, {1} accesses" -f (Field $r "MoveMethods"), (Field $r "MoveAccesses"))
Write-Host ("  MIXED (hand work)       : {0} methods, {1} accesses" -f (Field $r "MixedMethods"), (Field $r "MixedAccesses"))
Write-Host ("  effect-only (untouched) : {0} methods" -f (Field $r "EffectOnlyMethods"))
Write-Host ("  display only (stage 4)  : {0} methods, {1} accesses" -f (Field $r "UiMethods"), (Field $r "UiAccesses"))
Write-Host ("  twin-parameter seeds    : {0} methods, {1} slots" -f (Field $r "ParamMethods"), (Field $r "ParamSlots"))
Write-Host ("  notify sink (skipped)   : {0} methods" -f (Field $r "SkippedParamMethods"))
Write-Host ("  save streams to branch  : {0}" -f (Field $r "SaveStreams"))

if ($suspects.Count -gt 0) {
    Write-Host ""
    Write-Host "=== suspects: payload-shaped but not on the declared list ===" -ForegroundColor Yellow
    foreach ($s in $suspects) { Write-Host "  $s" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "=== MIXED found ===" -ForegroundColor Cyan
foreach ($x in ($mixed | Sort-Object)) { Write-Host "  $x" }

Write-Host ""
if ($blockers.Count -gt 0) {
    Write-Host "=== BLOCKERS ($($blockers.Count)) ===" -ForegroundColor Red
    foreach ($b in $blockers) { Write-Host "  $b" -ForegroundColor Red }
    Write-Host ""
    Write-Host "analysis FAILED - nothing would be rewritten" -ForegroundColor Red
    exit 1
}

Write-Host "analysis CLEAN - declared mixed set matches the assembly" -ForegroundColor Green
$asm.Dispose()

# ============================================================================
# Stage 1a: drive the field adder against a COPY, write it, then RE-READ the
# written file and assert against that. Asserting against the transform's own
# report would only prove it agrees with itself; Cecil's willingness to write
# the assembly at all is itself part of the check.
# ============================================================================

Write-Host ""
Write-Host "=== stage 1a: add twin fields (on a copy) ===" -ForegroundColor Cyan

$work = Join-Path $env:TEMP "projecteden-quality"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$copy = Join-Path $work "Assembly-CSharp.dll"
$out  = Join-Path $work "Assembly-CSharp.quality.dll"
Copy-Item $target $copy -Force

$rp2 = New-Object Mono.Cecil.ReaderParameters
$rp2.AssemblyResolver = $resolver
$asm2 = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($copy, $rp2)

$adderT = $pre.GetType("ProjectEden.Preloader.QualityFieldAdder")
$apply  = $adderT.GetMethod("Apply", [Reflection.BindingFlags]"NonPublic,Static")
$ar     = $apply.Invoke($null, @($asm2.MainModule))

$aBlockers = Field $ar "Blockers"
$aNotes    = Field $ar "Notes"
$aAdded    = Field $ar "Added"

foreach ($n in $aNotes) { Write-Host "  note: $n" }

if ($aBlockers.Count -gt 0) {
    Write-Host "=== ADDER BLOCKERS ($($aBlockers.Count)) ===" -ForegroundColor Red
    foreach ($b in $aBlockers) { Write-Host "  $b" -ForegroundColor Red }
    $asm2.Dispose()
    exit 1
}

Write-Host ("  added {0} twin fields" -f $aAdded.Count)
$asm2.Write($out)
$asm2.Dispose()
Write-Host "  written: $out"

# ---- re-read and assert on the RESULT ----
Write-Host ""
Write-Host "=== re-read assertions ===" -ForegroundColor Cyan

$asm3 = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($out, $rp2)
$mod3 = $asm3.MainModule
$fail = 0

function Check($ok, $msg) {
    if ($ok) { Write-Host "  PASS  $msg" -ForegroundColor Green }
    else     { Write-Host "  FAIL  $msg" -ForegroundColor Red; $script:fail++ }
}

# 1. Re-derive the expected twins FROM the written module and check each one.
#    Deliberately not a re-read of the transform's own report: that would only prove
#    the transform agrees with itself, and a change to the report's text format would
#    make the check pass silently.
$verify = $adderT.GetMethod("Verify", [Reflection.BindingFlags]"NonPublic,Static")
$vr     = $verify.Invoke($null, @($mod3))
$vBlock = Field $vr "Blockers"
$vFound = Field $vr "Added"

foreach ($b in $vBlock) { Check $false $b }
Check ($vBlock.Count -eq 0) "re-derived twin check reported no problems"
Check ($vFound.Count -eq 30) "twin field count is 30 (got $($vFound.Count))"

# 3. Cargo specifically - this is the one whose struct size the GPU path cares about
$cargo = $mod3.GetType("Cargo")
$cq = $cargo.Fields | Where-Object { $_.Name -eq "qua" -and -not $_.IsStatic }
Check ($cq -ne $null -and $cq.FieldType.MetadataType -eq [Mono.Cecil.MetadataType]::Int32) "Cargo.qua exists and is Int32"
$ci = $cargo.Fields | Where-Object { $_.Name -eq "inc" -and -not $_.IsStatic }
Check ($ci -ne $null) "Cargo.inc still present (quality does not replace proliferation)"

# 4. no method body was touched: every branch/switch target must still resolve.
#    Stage 1a changes no IL at all, so this must hold trivially - it is here because
#    the last preloader's worst bug was exactly a branch target that stopped resolving,
#    and only a re-read of the written file can see that class of damage.
$broken = 0
$checked = 0
foreach ($t in $mod3.Types) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $checked++
        foreach ($i in $m.Body.Instructions) {
            $op = $i.Operand
            if ($op -is [Mono.Cecil.Cil.Instruction]) {
                if ($op.Offset -lt 0) { $broken++ }
            }
        }
    }
}
Check ($broken -eq 0) "all branch targets still resolve across $checked method bodies"

$asm3.Dispose()

# ============================================================================
# Stage 1b: twin parameters + call-site fixups, on top of the 1a result.
# ============================================================================

Write-Host ""
Write-Host "=== stage 1b: twin parameters + call sites ===" -ForegroundColor Cyan

$out2 = Join-Path $work "Assembly-CSharp.quality1b.dll"
$asm4 = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($out, $rp2)

$paramT = $pre.GetType("ProjectEden.Preloader.QualityParamAdder")
$papply = $paramT.GetMethod("Apply", [Reflection.BindingFlags]"NonPublic,Static")
$pr     = $papply.Invoke($null, @($asm4.MainModule))

foreach ($n in (Field $pr "Notes")) { Write-Host "  note: $n" }

$pBlockers = Field $pr "Blockers"
if ($pBlockers.Count -gt 0) {
    Write-Host "=== 1b BLOCKERS ($($pBlockers.Count)) ===" -ForegroundColor Red
    foreach ($b in $pBlockers) { Write-Host "  $b" -ForegroundColor Red }
    $asm4.Dispose()
    exit 1
}

Write-Host ("  methods {0}  slots {1} (byref {2})  call sites {3}  bodies {4}" -f `
    (Field $pr "Methods"), (Field $pr "Slots"), (Field $pr "ByRefSlots"), `
    (Field $pr "CallSites"), (Field $pr "TouchedBodies"))

$asm4.Write($out2)
$asm4.Dispose()
Write-Host "  written: $out2"

Write-Host ""
Write-Host "=== 1b re-read assertions ===" -ForegroundColor Cyan
$asm5 = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($out2, $rp2)
$mod5 = $asm5.MainModule

# The decisive check: after write/re-read, EVERY call site of a widened method must be
# immediately preceded by exactly the quality arguments we push. A missed call site
# leaves the stack one value short, and that is reported by nothing else.
$pverify = $paramT.GetMethod("Verify", [Reflection.BindingFlags]"NonPublic,Static")
$vr2     = $pverify.Invoke($null, @($mod5))
$v2Block = Field $vr2 "Blockers"

foreach ($b in $v2Block) { Check $false $b }
Check ($v2Block.Count -eq 0) "every widened call site is preceded by its quality arguments"
Check ((Field $vr2 "CallSites") -eq (Field $pr "CallSites")) `
    ("call site count survives write/re-read ({0} vs {1})" -f (Field $vr2 "CallSites"), (Field $pr "CallSites"))

# 1a's fields must still be intact after 1b touched ~700 method bodies
$vr3 = $verify.Invoke($null, @($mod5))
Check ((Field $vr3 "Blockers").Count -eq 0) "1a twin fields still intact after 1b"

# branch targets again - 1b DOES change body lengths, so this is no longer trivial
$broken2 = 0; $checked2 = 0
foreach ($t in $mod5.Types) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $checked2++
        foreach ($i in $m.Body.Instructions) {
            $op = $i.Operand
            if ($op -is [Mono.Cecil.Cil.Instruction]) { if ($op.Offset -lt 0) { $broken2++ } }
            elseif ($op -is [Mono.Cecil.Cil.Instruction[]]) {
                foreach ($x in $op) { if ($x.Offset -lt 0) { $broken2++ } }
            }
        }
    }
}
Check ($broken2 -eq 0) "all branch and switch targets resolve after 1b ($checked2 bodies)"

$asm5.Dispose()

Write-Host ""
if ($fail -gt 0) {
    Write-Host "$fail assertion(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "stages 1a + 1b OK - fields and parameters in place, re-read clean" -ForegroundColor Green
exit 0
