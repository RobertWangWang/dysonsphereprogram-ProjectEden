param([string]$GameDir = 'G:\SteamLibrary\steamapps\common\Dyson Sphere Program')
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $env:APPDATA 'r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx\core\Mono.Cecil.dll')
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'DSPGAME_Data\Managed\Assembly-CSharp.dll'))
$plugin = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $PSScriptRoot '..\ProjectEden\bin\Debug\ProjectEden.dll'))
try {
    foreach ($signature in @(
        @($game, 'GameSave', 'SaveCurrentGame', 'System.String'),
        @($game, 'GameData', 'Export', 'System.IO.BinaryWriter'),
        @($game, 'PlanetFactory', 'Export', 'System.IO.Stream,System.IO.BinaryWriter'),
        @($plugin, 'ProjectEdenPlugin', 'Export', 'System.IO.BinaryWriter')
    )) {
        $type = $signature[0].MainModule.Types | Where-Object Name -eq $signature[1]
        $method = @($type.Methods | Where-Object { $_.Name -eq $signature[2] -and (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq $signature[3] })
        if ($method.Count -ne 1) { throw "Save probe signature mismatch: $($signature[1]).$($signature[2])($($signature[3]))" }
        Write-Host "PASS: $($signature[1]).$($signature[2])($($signature[3]))"
    }
} finally { $game.Dispose(); $plugin.Dispose() }
