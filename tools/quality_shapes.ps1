# Pure ASCII only.
#
# Item quality, stage 1c preparation: census of the IL STATEMENT SHAPES that payload
# accesses live in. Read only - changes nothing.
#
# Why this exists: twinning a payload access is not duplicating one instruction. The twin
# has to mirror the whole expression the value flows through. If the shapes that actually
# occur are a short list, 1c is pattern matching plus a loud failure on anything
# unrecognized. If they are wide open, 1c needs real dataflow analysis. This decides which.
#
#   powershell -ExecutionPolicy Bypass -File tools\quality_shapes.ps1
#   powershell -ExecutionPolicy Bypass -File tools\quality_shapes.ps1 -Mainline
#
# -Mainline restricts the census to the four payloads stage 1c actually covers
# (Cargo, StorageComponent.GRID, StationStore, AssemblerComponent.incServed). That is the
# number that decides how many shapes the transform must handle.

param(
    [int]$Top = 25,
    [switch]$Mainline,
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

$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($target, $rp)

$t = $pre.GetType("ProjectEden.Preloader.QualityShapeCensus")
$run = $null
foreach ($m in $t.GetMethods([Reflection.BindingFlags]"NonPublic,Static")) {
    if ($m.Name -eq "Run" -and $m.GetParameters().Count -eq 2) { $run = $m }
}
$r = $run.Invoke($null, @($asm.MainModule, [bool]$Mainline))

function Field($o, $n) {
  $f = $o.GetType().GetField($n, [Reflection.BindingFlags]"NonPublic,Instance,Public")
  return $f.GetValue($o)
}

foreach ($n in (Field $r "Notes")) { Write-Host $n -ForegroundColor Cyan }

$shapes  = Field $r "Shapes"
$example = Field $r "Example"

$total = 0
foreach ($k in $shapes.Keys) { $total += $shapes[$k] }

Write-Host ""
Write-Host ("=== full shapes: $($shapes.Count) distinct over $total statements ===") -ForegroundColor Cyan

$rank = 0
foreach ($k in ($shapes.Keys | Sort-Object { -$shapes[$_] })) {
    $rank++
    if ($rank -le $Top) {
        Write-Host ("{0,4}x  {1}" -f $shapes[$k], $k)
        Write-Host ("        e.g. {0}" -f $example[$k]) -ForegroundColor DarkGray
    }
}

# ---- the measurement that decides the design ----
#
# Full shapes diverge on the ADDRESSING PREFIX (how you reach the object), not on what is
# done to the payload. Addressing instructions are pure and re-executable, so the twin just
# replays them - it never has to understand which path they took. Strip them and what
# remains is the real set of shapes the transform must handle.
$core   = Field $r "Core"
$coreE  = Field $r "CoreExample"
$ctotal = 0
foreach ($k in $core.Keys) { $ctotal += $core[$k] }

Write-Host ""
Write-Host "=== CORE shapes, addressing stripped: $($core.Count) distinct ===" -ForegroundColor Green
$rank = 0
$cov = @{}
$acc = 0
foreach ($k in ($core.Keys | Sort-Object { -$core[$_] })) {
    $rank++
    $acc += $core[$k]
    $cov[$rank] = $acc
    if ($rank -le $Top) {
        Write-Host ("{0,4}x  {1}" -f $core[$k], $k)
        Write-Host ("        e.g. {0}" -f $coreE[$k]) -ForegroundColor DarkGray
    }
}

Write-Host ""
foreach ($n in @(5, 10, 15, 20, 30)) {
    if ($cov.ContainsKey($n)) {
        Write-Host ("core top {0,2} cover {1,4}/{2} = {3:P1}" -f $n, $cov[$n], $ctotal, ($cov[$n] / $ctotal)) -ForegroundColor Green
    }
}
Write-Host ("core ALL {0,2} cover {1,4}/{2} = 100%" -f $core.Count, $ctotal, $ctotal) -ForegroundColor Green

$asm.Dispose()
