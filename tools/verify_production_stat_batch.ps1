# 核验当前游戏的真实 IL，避免仅用测试替身证明转译器入口。
param([string]$GameDir = 'G:\SteamLibrary\steamapps\common\Dyson Sphere Program')
$ErrorActionPreference = 'Stop'
$probeCore = Join-Path $env:APPDATA 'r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx\core\Mono.Cecil.dll'
Add-Type -Path $probeCore
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'DSPGAME_Data\Managed\Assembly-CSharp.dll'))
try {
    $logic = $assembly.MainModule.Types | Where-Object Name -eq 'GameLogic'
    foreach ($name in @('_assembler_parallel', '_lab_produce_parallel')) {
        $methods = @($logic.Methods | Where-Object Name -eq $name)
        if ($methods.Count -ne 1) { throw "$name method count mismatch" }
        $method = $methods[0]
        if ($method.Parameters.Count -ne 4 -or @($method.Parameters | Where-Object { $_.ParameterType.FullName -ne 'System.Int32' }).Count -ne 0) { throw "$name signature mismatch" }
        foreach ($field in @('productRegister', 'consumeRegister')) {
            $loads = @($method.Body.Instructions | Where-Object {
                $_.OpCode.Code.ToString() -eq 'Ldfld' -and $_.Operand.DeclaringType.FullName -eq 'FactoryProductionStat' -and $_.Operand.Name -eq $field
            })
            if ($loads.Count -ne 1 -or $loads[0].Next.OpCode.Code.ToString() -notmatch '^Stloc') { throw "$name $field IL mismatch" }
        }
        Write-Host "PASS: $name has exactly one production and consumption field load, each stored in a local."
    }
} finally { $assembly.Dispose() }
