# Offline check for the two Harmony annotation mistakes that take the WHOLE mod down.
#
# Both are invisible to the compiler and both throw inside Harmony.PatchAll, which runs in
# ProjectEdenPlugin.Awake - so the failure is not "one patch did nothing", it is
# "not a single patch was applied and the mod is effectively uninstalled".
#
#   1. TargetMethod(s) mixed with individual [HarmonyPatch] annotations in the SAME class
#      -> ArgumentException: You cannot combine TargetMethod, TargetMethods or PatchAll
#         with individual annotations
#
#   2. [HarmonyPatch(typeof(T), "name")] naming an OVERLOADED game method without a
#      parameter-type array
#      -> AmbiguousMatchException from Type.GetMethod(name, flags)
#
# Both have actually happened here: (2) on PlanetFactory.InsertInto, then (1) and (2) again
# in the same feature (the quality effect layer). Hence this script.
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\verify_harmony.ps1 [-Config Debug]

param([string]$Config = "Debug")

$ErrorActionPreference = "Stop"

$root    = Split-Path -Parent $PSScriptRoot
$bepinex = if ($env:PROJECTEDEN_BEPINEX) { $env:PROJECTEDEN_BEPINEX }
           else { "$env:APPDATA\r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx" }
$managed = if ($env:PROJECTEDEN_MANAGED) { $env:PROJECTEDEN_MANAGED }
           else { "G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed" }

$plugin = Join-Path $root "ProjectEden\bin\$Config\ProjectEden.dll"
$target = Join-Path $managed "Assembly-CSharp.dll"

foreach ($p in @("$bepinex\core\Mono.Cecil.dll", $plugin, $target)) {
    if (-not (Test-Path $p)) { Write-Host "missing: $p" -ForegroundColor Red; exit 1 }
}

Add-Type -Path "$bepinex\core\Mono.Cecil.dll"
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($target)
$ours = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($plugin)

$bad = 0

# --- 1. TargetMethods mixed with individual annotations -----------------------------
foreach ($t in $ours.MainModule.Types) {
    $selector  = $false
    $annotated = @()

    foreach ($m in $t.Methods) {
        foreach ($a in $m.CustomAttributes) {
            $n = $a.AttributeType.Name
            if ($n -eq "HarmonyTargetMethods" -or $n -eq "HarmonyTargetMethod") { $selector = $true }
            if ($n -eq "HarmonyPatch") { $annotated += $m.Name }
        }
    }

    if ($selector -and $annotated.Count -gt 0) {
        Write-Host ("  MIXED  {0}: TargetMethod(s) plus individual [HarmonyPatch] on {1}" -f `
                    $t.Name, (($annotated | Sort-Object -Unique) -join ", ")) -ForegroundColor Red
        $bad++
    }
}

# --- 2. bare-name patches on overloaded game methods --------------------------------
foreach ($t in $ours.MainModule.Types) {
    foreach ($m in $t.Methods) {
        # a parameter-type array in ANY HarmonyPatch attribute on this method disambiguates it
        $disambiguated = $false
        foreach ($a in $m.CustomAttributes) {
            if ($a.AttributeType.Name -ne "HarmonyPatch") { continue }
            foreach ($arg in $a.ConstructorArguments) {
                if ("$($arg.Type)".Contains("Type[]")) { $disambiguated = $true }
            }
        }
        if ($disambiguated) { continue }

        foreach ($a in $m.CustomAttributes) {
            if ($a.AttributeType.Name -ne "HarmonyPatch") { continue }
            if ($a.ConstructorArguments.Count -ne 2) { continue }

            $declaring = "$($a.ConstructorArguments[0].Value)"
            $method    = "$($a.ConstructorArguments[1].Value)"

            if ([string]::IsNullOrEmpty($declaring) -or [string]::IsNullOrEmpty($method)) { continue }

            $short = ($declaring -split '\.')[-1]
            $gt = $game.MainModule.GetType($short)
            if ($gt -eq $null) { continue }

            $n = 0
            foreach ($gm in $gt.Methods) { if ($gm.Name -eq $method) { $n++ } }

            if ($n -gt 1) {
                Write-Host ("  OVERLOADED  {0}::{1} patches {2}::{3}, which has {4} overloads" -f `
                            $t.Name, $m.Name, $short, $method, $n) -ForegroundColor Red
                $bad++
            }
        }
    }
}

if ($bad -gt 0) {
    Write-Host ""
    Write-Host "$bad problem(s). Each one throws inside Harmony.PatchAll, which means" -ForegroundColor Red
    Write-Host "NOT ONE patch gets applied - the mod is effectively uninstalled." -ForegroundColor Red
    Write-Host "Fix: give the overloaded one a TargetMethods class of its own (a selector" -ForegroundColor Red
    Write-Host "cannot share a class with individual annotations)." -ForegroundColor Red
    exit 1
}

Write-Host "OK: no TargetMethods/annotation mixing, no bare-name patches on overloaded methods" -ForegroundColor Green
