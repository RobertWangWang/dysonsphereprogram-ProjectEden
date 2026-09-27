# Re-derive every IL premise that PlanetSprayPatches rests on.
#
# Run this after any game update, BEFORE trusting the feature. The whole design is
# "a cargo's inc is established when it is born", and that claim is only as good as
# the call-site census behind it -- if a game update adds a creation path this script
# is what says so, instead of the feature silently missing that path's cargo.
#
# ASCII ONLY. Windows PowerShell reads .ps1 as ANSI, so one non-ASCII character turns
# the whole file into parser errors that look nothing like an encoding problem.

param(
    [string]$GameDir = "G:\SteamLibrary\steamapps\common\Dyson Sphere Program"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$cecil = Join-Path $env:APPDATA "r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx\core\Mono.Cecil.dll"
Add-Type -Path $cecil

$m = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir "DSPGAME_Data\Managed\Assembly-CSharp.dll")).MainModule

$bad = $false

function Walk($t) {
    $out = @($t)
    foreach ($n in $t.NestedTypes) { $out += Walk $n }
    return $out
}

$types = @()
foreach ($t in $m.Types) { $types += Walk $t }

# --- 1. the spray model: three numbers, all read from ItemProto ----------------------
#
# SpraycoaterComponent.InternalUpdate is where vanilla defines what a proliferator IS.
# PlanetSprayPatches copies that model rather than inventing one, so these three
# reads are the contract.
Write-Output "1) vanilla spray model (SpraycoaterComponent.InternalUpdate)"

$sc = $m.GetType("SpraycoaterComponent")
$su = $null
foreach ($x in $sc.Methods) { if ($x.Name -eq "InternalUpdate") { $su = $x } }

if ($null -eq $su) {
    Write-Output "   FAIL: SpraycoaterComponent.InternalUpdate not found."
    exit 1
}

$ability = 0; $hpmax = 0; $incWrite = 0

foreach ($i in $su.Body.Instructions) {
    if ($null -eq $i.Operand) { continue }
    $n = $i.Operand.ToString()
    if ($i.OpCode.Name -eq "ldfld" -and $n -match "ItemProto::Ability") { $ability++ }
    if ($i.OpCode.Name -eq "ldfld" -and $n -match "ItemProto::HpMax") { $hpmax++ }
    if ($i.OpCode.Name -eq "stfld" -and $n -match "Cargo::inc") { $incWrite++ }
}

Write-Output ("   ItemProto.Ability read {0}x, ItemProto.HpMax read {1}x, Cargo::inc written {2}x" -f $ability, $hpmax, $incWrite)

if ($ability -lt 1 -or $hpmax -lt 1) {
    Write-Output "   FAIL: the coater no longer reads Ability / HpMax off ItemProto."
    Write-Output "         PlanetSprayPatches derives level and sprays-per-item from exactly those two."
    $bad = $true
}
if ($incWrite -lt 1) {
    Write-Output "   FAIL: the coater no longer writes Cargo::inc -- re-read the whole model."
    $bad = $true
}

# --- 2. the creation-point census ----------------------------------------------------
#
# The design premise: a cargo's inc is established at CargoContainer.AddCargo, and
# belt-to-belt movement inside a path does NOT go through it. If a new caller appears,
# it is a new creation path and the feature covers it for free -- but if the COUNT
# drops, some path stopped going through AddCargo and its cargo is now unsprayed.
Write-Output ""
Write-Output "2) CargoContainer.AddCargo census"

$cc = $m.GetType("CargoContainer")
$overloads = @()
foreach ($x in $cc.Methods) { if ($x.Name -eq "AddCargo") { $overloads += $x } }

Write-Output ("   overloads: {0}" -f $overloads.Count)

foreach ($x in $overloads) {
    $ps = @()
    foreach ($p in $x.Parameters) { $ps += $p.ParameterType.Name }
    Write-Output ("     returns {0}({1})" -f $x.ReturnType.Name, ($ps -join ","))

    if ($x.ReturnType.Name -ne "Int32") {
        Write-Output "   FAIL: an AddCargo overload no longer returns the cargo id."
        Write-Output "         The postfix reads __result as the id; a void overload breaks it silently."
        $bad = $true
    }
}

if ($overloads.Count -ne 2) {
    Write-Output "   FAIL: expected exactly 2 AddCargo overloads (the postfix selects both by name)."
    $bad = $true
}

$callers = @{}
$total = 0

foreach ($t in $types) {
    foreach ($x in $t.Methods) {
        if (-not $x.HasBody) { continue }
        foreach ($i in $x.Body.Instructions) {
            if ($i.OpCode.Name -ne "call" -and $i.OpCode.Name -ne "callvirt") { continue }
            if ($null -eq $i.Operand) { continue }
            if ($i.Operand.Name -ne "AddCargo") { continue }
            if ($i.Operand.DeclaringType.Name -ne "CargoContainer") { continue }
            $key = "{0}::{1}" -f $t.Name, $x.Name
            $callers[$key] = 1 + $(if ($callers.ContainsKey($key)) { $callers[$key] } else { 0 })
            $total++
        }
    }
}

foreach ($k in ($callers.Keys | Sort-Object)) {
    Write-Output ("     {0,-46} x{1}" -f $k, $callers[$k])
}
Write-Output ("   total call sites: {0} (expected 18 at 0.10.35)" -f $total)

if ($total -lt 18) {
    Write-Output "   WARN: fewer call sites than recorded. A creation path may have moved off"
    Write-Output "         AddCargo, and cargo born there would be left unsprayed -- silently."
}

# --- 3. CargoContainer cannot reach its planet ---------------------------------------
#
# This is why the container -> planet map exists at all. If a back-reference ever
# appears, the two cold hooks on PlanetFactory.Init / Import become unnecessary.
Write-Output ""
Write-Output "3) does CargoContainer know its planet?"

$back = @()
foreach ($f in $cc.Fields) {
    if ($f.FieldType.Name -eq "PlanetFactory" -or $f.FieldType.Name -eq "PlanetData" -or
        $f.FieldType.Name -eq "CargoTraffic") { $back += ("{0} {1}" -f $f.FieldType.Name, $f.Name) }
}

if ($back.Count -eq 0) {
    Write-Output "   no back-reference (as assumed) -- the container->planet map is required."
} else {
    Write-Output ("   PASS-CHANGED: CargoContainer now has {0}." -f ($back -join ", "))
    Write-Output "         The map and its two cold hooks could be replaced by reading that field."
}

# the two construction sites that the map's hooks rely on
$ctors = @()
foreach ($t in $types) {
    foreach ($x in $t.Methods) {
        if (-not $x.HasBody) { continue }
        foreach ($i in $x.Body.Instructions) {
            if ($i.OpCode.Name -ne "newobj") { continue }
            if ($null -eq $i.Operand) { continue }
            if ($i.Operand.DeclaringType.Name -ne "CargoContainer") { continue }
            $ctors += ("{0}::{1}" -f $t.Name, $x.Name)
        }
    }
}

Write-Output ("   constructed in: {0}" -f (($ctors | Sort-Object -Unique) -join ", "))

foreach ($need in @("PlanetFactory::Init", "PlanetFactory::Import")) {
    if ($ctors -notcontains $need) {
        Write-Output ("   FAIL: {0} no longer constructs a CargoContainer -- a planet would never" -f $need)
        Write-Output "         be registered, and every cargo on it would go unsprayed with no error."
        $bad = $true
    }
}

if (($ctors | Sort-Object -Unique).Count -gt 2) {
    Write-Output "   FAIL: a third construction site appeared. The map's hooks miss it, so that"
    Write-Output "         planet's cargo is silently unsprayed. Add a hook there."
    $bad = $true
}

# --- 4. the widened fields the accessors are emitted against ------------------------
Write-Output ""
Write-Output "4) Cargo.inc / Cargo.stack (the emitted accessors bind to these at runtime)"

$cargo = $m.GetType("Cargo")
foreach ($name in @("inc", "stack")) {
    $f = $null
    foreach ($x in $cargo.Fields) { if ($x.Name -eq $name) { $f = $x } }
    if ($null -eq $f) {
        Write-Output ("   FAIL: Cargo.{0} not found -- the accessor cannot be emitted." -f $name)
        $bad = $true
    } else {
        Write-Output ("   Cargo.{0} is {1} in the SHIPPED assembly (the preloader widens it to Int16)" -f $name, $f.FieldType.Name)
    }
}

Write-Output ""

if ($bad) {
    Write-Output "FAIL: at least one premise of PlanetSprayPatches no longer holds."
    exit 1
}

Write-Output "OK: every IL premise behind the planetary spray hub still holds."
