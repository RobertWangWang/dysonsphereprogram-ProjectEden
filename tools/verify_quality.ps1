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

function Invoke1($typeName, $method, $arg) {
  $t = $pre.GetType("ProjectEden.Preloader.$typeName")
  $m = $t.GetMethod($method, [Reflection.BindingFlags]"NonPublic,Static")
  return $m.Invoke($null, @($arg))
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
Write-Host ("  max slots in one method : {0}  ({1})" -f (Field $r "MaxParamSlots"), (Field $r "MaxParamSlotsAt"))
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
Check ($vFound.Count -eq 27) "twin field count is 27 (got $($vFound.Count))"

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

# 5. MOD COMPATIBILITY: no method signature may differ from vanilla.
#
#    This is a standing guard, not a formality. An earlier attempt appended a quality
#    parameter to 90 methods; measuring the profile afterwards showed UXAssist makes 12
#    direct calls into that set and InstantDelivery 2 - and InstantDelivery is a declared
#    dependency in this mod's own manifest. Every such call is a MemberRef compiled
#    against vanilla, so it throws MissingMethodException the first time it runs, in
#    someone else's mod, with no compile-time signal anywhere.
#
#    Quality therefore carries across method boundaries WITHOUT touching signatures.
#    If this check ever fails, that decision has been silently reversed.
function SignatureDiff($origModule, $newModule) {
    # Index the new module once per type; a Where-Object per method is O(n^2) and takes
    # minutes over 25k methods.
    $diff = New-Object System.Collections.Generic.List[string]
    foreach ($t in $origModule.Types) {
        $t2 = $newModule.GetType($t.FullName)
        if ($t2 -eq $null) { continue }
        $have = New-Object System.Collections.Generic.HashSet[string]
        foreach ($m2 in $t2.Methods) { [void]$have.Add($m2.Name + "/" + $m2.Parameters.Count) }
        foreach ($m in $t.Methods) {
            if (-not $have.Contains($m.Name + "/" + $m.Parameters.Count)) {
                $diff.Add($t.FullName + "::" + $m.Name)
            }
        }
    }
    return $diff
}

$origAsm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($target, $rp2)
$sigDiff = SignatureDiff $origAsm.MainModule $mod3
foreach ($d in ($sigDiff | Select-Object -First 5)) { Write-Host ("        signature changed or missing: $d") -ForegroundColor Red }
Check ($sigDiff.Count -eq 0) "no method signature differs from vanilla (mod compatibility preserved)"

$origAsm.Dispose()
$asm3.Dispose()

# ============================================================================
# Stage 1b: synthesize the thread-static register file used to carry quality
# across method boundaries WITHOUT touching any signature.
# ============================================================================

Write-Host ""
Write-Host "=== stage 1b: quality side channel ===" -ForegroundColor Cyan

$out2 = Join-Path $work "Assembly-CSharp.quality1b.dll"
$asm4 = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($out, $rp2)

$chanT  = $pre.GetType("ProjectEden.Preloader.QualityChannelBuilder")
$capply = $chanT.GetMethod("Apply", [Reflection.BindingFlags]"NonPublic,Static")
$cr     = $capply.Invoke($null, @($asm4.MainModule))

foreach ($n in (Field $cr "Notes")) { Write-Host "  note: $n" }

$cBlock = Field $cr "Blockers"
if ($cBlock.Count -gt 0) {
    Write-Host "=== 1b BLOCKERS ($($cBlock.Count)) ===" -ForegroundColor Red
    foreach ($b in $cBlock) { Write-Host "  $b" -ForegroundColor Red }
    $asm4.Dispose()
    exit 1
}

$asm4.Write($out2)
$asm4.Dispose()
Write-Host "  written: $out2"

Write-Host ""
Write-Host "=== 1b re-read assertions ===" -ForegroundColor Cyan
$asm5 = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($out2, $rp2)
$mod5 = $asm5.MainModule

$cverify = $chanT.GetMethod("Verify", [Reflection.BindingFlags]"NonPublic,Static")
$vr2     = $cverify.Invoke($null, @($mod5))
foreach ($b in (Field $vr2 "Blockers")) { Check $false $b }
Check ((Field $vr2 "Blockers").Count -eq 0) `
    ("side channel has $(Field $vr2 'Registers') thread-static Int32 registers after write/re-read")

# The synthesized Split is quality's version of StorageComponent.split_inc - the
# proportional split every extensive quantity needs. Assert it survived the round trip
# with a real body: a method that exists but is empty would fail only at run time.
$chanType = $mod5.GetType("ProjectEdenQualityChannel")
$split = $null
if ($chanType -ne $null) {
    foreach ($sm in $chanType.Methods) { if ($sm.Name -eq "Split") { $split = $sm } }
}
Check ($split -ne $null) "synthesized ProjectEdenQualityChannel.Split exists"
if ($split -ne $null) {
    Check ($split.IsStatic -and $split.Parameters.Count -eq 3 -and $split.Parameters[1].ParameterType.IsByReference) `
        "Split signature is static int Split(int, ref int, int)"
    Check ($split.HasBody -and $split.Body.Instructions.Count -ge 20) `
        "Split has a real body ($($split.Body.Instructions.Count) instructions)"
    $badTarget = 0
    foreach ($ins in $split.Body.Instructions) {
        if ($ins.Operand -is [Mono.Cecil.Cil.Instruction]) { if ($ins.Operand.Offset -lt 0) { $badTarget++ } }
    }
    Check ($badTarget -eq 0) "Split's own branch targets resolve after write/re-read"
}

# 1a's fields must survive 1b
$vr3 = $verify.Invoke($null, @($mod5))
Check ((Field $vr3 "Blockers").Count -eq 0) "1a twin fields still intact after 1b"

# and the compatibility invariant must STILL hold - adding a type must change no signature
$origAsm2 = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($target, $rp2)
$sigDiff2 = SignatureDiff $origAsm2.MainModule $mod5
foreach ($d in ($sigDiff2 | Select-Object -First 5)) { Write-Host ("        signature changed or missing: $d") -ForegroundColor Red }
Check ($sigDiff2.Count -eq 0) "still no signature differs from vanilla after 1b"

$origAsm2.Dispose()
$asm5.Dispose()

# ---------------------------------------------------------------------------
# Stage 1c, end to end: the SAME chain Patcher.Patch runs, written out and read
# back. Everything above checks one stage against a copy prepared by hand; this
# checks the end state of the real pipeline. A stage that passes in isolation and
# breaks in sequence is exactly the failure this repo keeps paying for.
# ---------------------------------------------------------------------------
$copy6 = Join-Path $work "ac-1c-full.dll"
Copy-Item $target $copy6 -Force
$asm6 = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($copy6, $rp2)
$mod6 = $asm6.MainModule

[void](Invoke1 "CargoIncWidener"       "Apply" $mod6)
[void](Invoke1 "QualityFieldAdder"     "Apply" $mod6)
[void](Invoke1 "QualityChannelBuilder" "Apply" $mod6)
$qr = Invoke1 "QualityTransform" "Apply" $mod6

Check ((Field $qr "Blockers").Count -eq 0) "1c reports no blockers in the real chain"
Check ((Field $qr "Unhandled").Count -eq 0) "1c has no unrecognised statement shape left"
Check ((Field $qr "Pending").Count -eq 0) "1c has an emitter for every shape it recognises"
Check ((Field $qr "Twinned") -gt 200) "1c emitted twin statements ($(Field $qr 'Twinned'))"

# The side channel must actually be used. Zero here means quality never crosses a
# method boundary - and that failure is silent: quality stays 0 while every count
# still reports success.
Check ((Field $qr "ChannelUses") -gt 0) "1c uses the side channel ($(Field $qr 'ChannelUses') sites)"

$out6 = Join-Path $work "ac-1c-full-out.dll"
$wrote = $true
try { $asm6.Write($out6) } catch { $wrote = $false; Write-Host "        $($_.Exception.Message)" -ForegroundColor Red }
Check $wrote "the fully transformed assembly still writes"
$asm6.Dispose()

if ($wrote) {
    $asm7 = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($out6, $rp2)
    $bad7 = 0
    foreach ($t in $asm7.MainModule.Types) {
        foreach ($m in $t.Methods) {
            if (-not $m.HasBody) { continue }
            foreach ($i in $m.Body.Instructions) {
                $op = $i.Operand
                if ($op -is [Mono.Cecil.Cil.Instruction]) { if ($op.Offset -lt 0) { $bad7++ } }
                elseif ($op -is [Mono.Cecil.Cil.Instruction[]]) { foreach ($x in $op) { if ($x.Offset -lt 0) { $bad7++ } } }
            }
        }
    }
    Check ($bad7 -eq 0) "all branch targets resolve after the full chain writes and re-reads"

    # Still no signature may differ - that is what keeps other mods loading.
    $origAsm3 = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($target, $rp2)
    $sigDiff3 = SignatureDiff $origAsm3.MainModule $asm7.MainModule
    foreach ($d in ($sigDiff3 | Select-Object -First 5)) { Write-Host ("        signature changed: $d") -ForegroundColor Red }
    Check ($sigDiff3.Count -eq 0) "no signature differs from vanilla after the full chain"
    $origAsm3.Dispose()
    $asm7.Dispose()
}

Write-Host ""
if ($fail -gt 0) {
    Write-Host "$fail assertion(s) FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "stages 1a + 1b + 1c OK - fields, side channel, quality flows, signatures untouched" -ForegroundColor Green
exit 0