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
exit 0
