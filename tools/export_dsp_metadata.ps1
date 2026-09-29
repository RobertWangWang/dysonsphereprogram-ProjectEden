# Export selected types with Cecil; never load/execute or modify the game assembly.
param(
    [Parameter(Mandatory=$true)][string]$Assembly,
    [Parameter(Mandatory=$true)][string]$Cecil,
    [Parameter(Mandatory=$true)][string]$OutputDir,
    [string]$TypesFile,
    [switch]$AllTypes
)
$ErrorActionPreference = 'Stop'
Add-Type -Path $Cecil
$module = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Assembly)
try {
    function Get-AllTypes($types) {
        foreach ($entry in $types) {
            $entry
            if ($entry.HasNestedTypes) { Get-AllTypes $entry.NestedTypes }
        }
    }
    if ($AllTypes) {
        $selectedTypes = @(Get-AllTypes $module.MainModule.Types)
    } else {
        $typeNames = Get-Content -LiteralPath $TypesFile -Encoding UTF8 | ConvertFrom-Json
        $selectedTypes = @($typeNames | ForEach-Object {
            $resolved = $module.MainModule.GetType($_)
            if ($null -eq $resolved) { throw "Missing type: $_" }
            $resolved
        })
    }
    $records = @()
    foreach ($type in $selectedTypes) {
        $name = $type.FullName
        $lines = New-Object 'System.Collections.Generic.List[string]'
        $methods = @()
        foreach ($method in $type.Methods) {
            $lines.Add("// $($method.FullName) [token $($method.MetadataToken)]")
            $calls = @()
            if ($method.HasBody) {
                foreach ($local in $method.Body.Variables) { $lines.Add("// local $($local.Index): $($local.VariableType.FullName)") }
                foreach ($ins in $method.Body.Instructions) {
                    $lines.Add($ins.ToString())
                    if ($ins.Operand -is [Mono.Cecil.MethodReference]) { $calls += $ins.Operand.FullName }
                }
                foreach ($handler in $method.Body.ExceptionHandlers) {
                    $lines.Add("// exception: $($handler.HandlerType) try=$($handler.TryStart) handler=$($handler.HandlerStart)")
                }
            }
            $lines.Add('')
            $methods += [ordered]@{ name=$method.Name; signature=$method.FullName; token=$method.MetadataToken.ToString(); has_body=$method.HasBody; calls=@($calls | Sort-Object -Unique) }
        }
        $fields = @($type.Fields | ForEach-Object { [ordered]@{ name=$_.Name; type=$_.FieldType.FullName; constant=if ($_.HasConstant) { $_.Constant } else { $null } } })
        $ilName = if ($AllTypes) { ('type-{0:X8}.il' -f $type.MetadataToken.ToUInt32()) } else { "$name.il" }
        [IO.File]::WriteAllLines((Join-Path $OutputDir $ilName), $lines, (New-Object Text.UTF8Encoding($false)))
        $records += [ordered]@{ name=$name; base_type=if ($type.BaseType) { $type.BaseType.FullName } else { $null }; il_file=$ilName; fields=$fields; methods=$methods }
    }
    $result = [ordered]@{ assembly=$module.Name.FullName; mvid=$module.MainModule.Mvid.ToString(); types=$records }
    [IO.File]::WriteAllText((Join-Path $OutputDir 'symbols.json'), ($result | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
} finally { $module.Dispose() }
