param(
    [Parameter(Mandatory=$true)][string]$Ghidra,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$JavaHome
)
$ErrorActionPreference = 'Stop'
$source = Join-Path (Resolve-Path -LiteralPath $Ghidra) 'Ghidra/Processors/x86/data/languages'
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if ($destination.TrimEnd('\','/') -eq $source.TrimEnd('\','/')) { throw 'Use an independent build directory.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item (Join-Path $source '*.sinc') $destination
Copy-Item (Join-Path $source '*.slaspec') $destination
Copy-Item (Join-Path $PSScriptRoot 'dsp-fma4.sinc') $destination
$spec = Join-Path $destination 'x86-64.slaspec'
Add-Content -LiteralPath $spec -Value "`nwith : lockprefx=0 {`n@include `"dsp-fma4.sinc`"`n}" -Encoding utf8
$sla = Join-Path $destination 'x86-64.sla'
if (Test-Path -LiteralPath $sla) { Remove-Item -LiteralPath $sla }
$priorJava = $env:JAVA_HOME
try {
    $env:JAVA_HOME = (Resolve-Path -LiteralPath $JavaHome).Path
    & (Join-Path $Ghidra 'support/sleigh.bat') $spec $sla
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $sla)) { throw 'SLEIGH compilation failed.' }
} finally { $env:JAVA_HOME = $priorJava }
$manifest = @{
    status = 'experimental-opaque-fma4-language'
    original_sla_sha256 = (Get-FileHash (Join-Path $source 'x86-64.sla')).Hash.ToLowerInvariant()
    sla_sha256 = (Get-FileHash -LiteralPath $sla).Hash.ToLowerInvariant()
    inputs = @{}
}
Get-ChildItem -LiteralPath $destination -File | Where-Object { $_.Extension -in @('.sinc','.slaspec') } | ForEach-Object {
    $manifest.inputs[$_.Name] = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'build-manifest.json') -Encoding utf8
