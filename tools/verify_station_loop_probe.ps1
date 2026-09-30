param([string]$GameDir = 'G:\SteamLibrary\steamapps\common\Dyson Sphere Program')
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $env:APPDATA 'r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx\core\Mono.Cecil.dll')
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'DSPGAME_Data\Managed\Assembly-CSharp.dll'))
try {
    $type = $assembly.MainModule.Types | Where-Object Name -eq 'PlanetTransport'
    $method = $type.Methods | Where-Object Name -eq 'GameTick'
    foreach ($name in @('InternalTickLocal', 'InternalTickRemote', 'UpdateCollection', 'UpdateVeinCollection', 'SetPCState', 'GameTick_SandboxMode', 'GameTick_UpdateNeeds')) {
        $owner = if ($name.StartsWith('GameTick')) { 'PlanetTransport' } else { 'StationComponent' }
        $calls = @($method.Body.Instructions | Where-Object { $_.OpCode.Code.ToString() -match '^Call' -and $_.Operand.DeclaringType.Name -eq $owner -and $_.Operand.Name -eq $name })
        if ($calls.Count -ne 1) { throw "$owner.$name call count mismatch: $($calls.Count)" }
        Write-Host "PASS: $owner.$name exactly one call in game IL."
    }
} finally { $assembly.Dispose() }
