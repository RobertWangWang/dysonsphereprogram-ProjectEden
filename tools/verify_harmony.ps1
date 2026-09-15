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

# --- 3. prefix/postfix parameter names that do not exist in the target ----------------
#
# Harmony injects by PARAMETER NAME. A name the target does not have throws
#   System.Exception: Parameter "x" not found in method ...
# from HarmonyManipulator.EmitCallParameter, which is rethrown as HarmonyException and
# propagates out of Harmony.PatchAll - so EVERY patch class after it is silently skipped.
#
# This has happened here: a probe used (EBuildCondition type, BuildPreview preview) while the
# game declares (EBuildCondition _bdCondition, BuildPreview _bp). The visible symptom was NOT
# "the probe did nothing" - it was an unrelated vanilla crash coming back, because the vein
# array fix sat in a class that never got patched.
#
# Names starting with __ are Harmony's own (__instance, __result, __state, ___privateField,
# __args, __originalMethod, __runOriginal) and are not looked up in the target.

$specialPrefixes = @("__")

foreach ($t in $ours.MainModule.Types) {
    # class-level [HarmonyPatch(typeof(T), "name")] applies to every patch method in the class
    $classDeclaring = $null
    $classMethod    = $null
    foreach ($a in $t.CustomAttributes) {
        if ($a.AttributeType.Name -ne "HarmonyPatch") { continue }
        if ($a.ConstructorArguments.Count -ne 2) { continue }
        $classDeclaring = "$($a.ConstructorArguments[0].Value)"
        $classMethod    = "$($a.ConstructorArguments[1].Value)"
    }

    foreach ($m in $t.Methods) {
        $isPatch = $false
        foreach ($a in $m.CustomAttributes) {
            $n = $a.AttributeType.Name
            if ($n -eq "HarmonyPrefix" -or $n -eq "HarmonyPostfix" -or $n -eq "HarmonyFinalizer") { $isPatch = $true }
        }
        if (-not $isPatch) { continue }

        $declaring = $classDeclaring
        $method    = $classMethod
        foreach ($a in $m.CustomAttributes) {
            if ($a.AttributeType.Name -ne "HarmonyPatch") { continue }
            if ($a.ConstructorArguments.Count -ne 2) { continue }
            $declaring = "$($a.ConstructorArguments[0].Value)"
            $method    = "$($a.ConstructorArguments[1].Value)"
        }

        if ([string]::IsNullOrEmpty($declaring) -or [string]::IsNullOrEmpty($method)) { continue }

        $short = ($declaring -split '\.')[-1]
        $gt = $game.MainModule.GetType($short)
        if ($gt -eq $null) { continue }

        $targets = @()
        foreach ($gm in $gt.Methods) { if ($gm.Name -eq $method) { $targets += $gm } }
        if ($targets.Count -ne 1) { continue }   # 0 = not found, >1 = check 2 already reports it

        $have = @()
        foreach ($gp in $targets[0].Parameters) { $have += $gp.Name }

        foreach ($p in $m.Parameters) {
            $pn = $p.Name
            $skip = $false
            foreach ($sp in $specialPrefixes) { if ($pn.StartsWith($sp)) { $skip = $true } }
            if ($skip) { continue }
            if ($have -contains $pn) { continue }

            Write-Host ("  BADPARAM  {0}::{1} takes '{2}', but {3}::{4} declares ({5})" -f `
                        $t.Name, $m.Name, $pn, $short, $method, ($have -join ", ")) -ForegroundColor Red
            $bad++
        }
    }
}

if ($bad -gt 0) {
    Write-Host ""
    Write-Host "$bad problem(s). Every one of them throws out of Harmony.PatchAll." -ForegroundColor Red
    Write-Host "" -ForegroundColor Red
    Write-Host "MIXED / OVERLOADED throw while the class list is being built, so NOT ONE patch" -ForegroundColor Red
    Write-Host "  gets applied and the mod is effectively uninstalled." -ForegroundColor Red
    Write-Host "  Fix: give the overloaded one a TargetMethods class of its own (a selector" -ForegroundColor Red
    Write-Host "  cannot share a class with individual annotations)." -ForegroundColor Red
    Write-Host "" -ForegroundColor Red
    Write-Host "BADPARAM throws while that one class is being patched, so classes patched BEFORE" -ForegroundColor Red
    Write-Host "  it survive and EVERY CLASS AFTER IT IS SILENTLY SKIPPED. That is worse than a" -ForegroundColor Red
    Write-Host "  clean failure: the symptom is some unrelated feature missing, or a vanilla" -ForegroundColor Red
    Write-Host "  crash coming back with no (wrapper dynamic-method) frame in the stack." -ForegroundColor Red
    Write-Host "  Fix: copy the parameter name from the game method verbatim. Harmony injects by" -ForegroundColor Red
    Write-Host "  NAME, not by position; only names starting with __ are Harmony's own." -ForegroundColor Red
    exit 1
}

Write-Host "OK: no TargetMethods/annotation mixing, no bare-name patches on overloaded methods," -ForegroundColor Green
Write-Host "    no prefix/postfix parameter names the target method does not declare" -ForegroundColor Green
