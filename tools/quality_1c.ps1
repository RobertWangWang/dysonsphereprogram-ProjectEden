# Pure ASCII only.
#
# Item quality, stage 1c: run the transform against a copy that already has 1a (fields)
# and 1b (side channel) applied, and report shape coverage.
#
# The transform is two-pass: if ANY statement shape is not in its table, it records that
# and changes nothing. So partial shape coverage means the transform does not apply at
# all - the game stays identical. There is no half-applied state. This script prints how
# far the table has got and exactly which shapes are still missing.
#
#   powershell -ExecutionPolicy Bypass -File tools\quality_1c.ps1

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

[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes("$BepInExDir\core\Mono.Cecil.dll"))
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes("$BepInExDir\core\Mono.Cecil.Rocks.dll"))
$pre = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($preload))

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($managed)
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver

function Field($o, $n) {
  $f = $o.GetType().GetField($n, [Reflection.BindingFlags]"NonPublic,Instance,Public")
  return $f.GetValue($o)
}
function Invoke1($typeName, $method, $arg) {
  $t = $pre.GetType("ProjectEden.Preloader.$typeName")
  $m = $t.GetMethod($method, [Reflection.BindingFlags]"NonPublic,Static")
  return $m.Invoke($null, @($arg))
}

$work = Join-Path $env:TEMP "projecteden-quality"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$copy = Join-Path $work "ac-1c.dll"
Copy-Item $target $copy -Force

$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($copy, $rp)
$mod = $asm.MainModule

# 1a then 1b, so 1c sees the state it will actually see at runtime
$a = Invoke1 "QualityFieldAdder"    "Apply" $mod
$b = Invoke1 "QualityChannelBuilder" "Apply" $mod
foreach ($x in @($a, $b)) {
    foreach ($bl in (Field $x "Blockers")) { Write-Host "  prereq BLOCKER: $bl" -ForegroundColor Red }
}
Write-Host "prereqs: 1a fields + 1b channel applied to the copy" -ForegroundColor DarkGray
Write-Host ""

$r = Invoke1 "QualityTransform" "Apply" $mod

foreach ($n in (Field $r "Notes")) { Write-Host $n -ForegroundColor Cyan }

$blockers = Field $r "Blockers"
if ($blockers.Count -gt 0) {
    Write-Host ""
    Write-Host "=== BLOCKERS ===" -ForegroundColor Red
    foreach ($b2 in $blockers) { Write-Host "  $b2" -ForegroundColor Red }
    $asm.Dispose()
    exit 1
}

$un   = Field $r "Unhandled"
$unE  = Field $r "UnhandledExample"
$done = Field $r "Twinned"

$missing = 0
foreach ($k in $un.Keys) { $missing += $un[$k] }
$done = (Field $r "Twinned") + (Field $r "NoTwinNeeded") + (Field $r "Dropped")
$totalStmts = $done + $missing

Write-Host ""
Write-Host ("=== shape coverage: {0}/{1} statements = {2:P1} ===" -f $done, $totalStmts, ($done / [double]$totalStmts)) -ForegroundColor Green
Write-Host ("    twinned {0}, no-twin-needed {1}, dropped {2}, methods {3}, twin locals {4}" -f (Field $r "Twinned"), (Field $r "NoTwinNeeded"), (Field $r "Dropped"), (Field $r "Methods"), (Field $r "TwinLocals"))

if ($un.Count -gt 0) {
    Write-Host ""
    Write-Host "=== shapes still missing from the table ($($un.Count) kinds, $missing statements) ===" -ForegroundColor Yellow
    foreach ($k in ($un.Keys | Sort-Object { -$un[$_] })) {
        Write-Host ("{0,4}x  {1}" -f $un[$k], $k) -ForegroundColor Yellow
        Write-Host ("        e.g. {0}" -f $unE[$k]) -ForegroundColor DarkGray
    }
    Write-Host ""
    Write-Host "transform does NOT apply while any shape is missing - the game is unchanged." -ForegroundColor Yellow
    $asm.Dispose()
    exit 0
}

Write-Host ""
Write-Host "every shape is handled - transform would apply" -ForegroundColor Green
$asm.Dispose()
exit 0
