[CmdletBinding(DefaultParameterSetName = 'Full')]
param(
    [string] $Repository = (Join-Path $PSScriptRoot '../packages'),
    [string] $Assets = (Join-Path $PSScriptRoot 'artifacts/archive-upstream'),
    [string] $DotNet = 'dotnet',
    [Parameter(ParameterSetName = 'Offline')]
    [switch] $Offline,
    [Parameter(ParameterSetName = 'BuiltIn')]
    [switch] $BuiltInOnly
)

$ErrorActionPreference = 'Stop'
$localDotNet = Join-Path $PSScriptRoot 'artifacts/tools/dotnet/dotnet.exe'
if (!$PSBoundParameters.ContainsKey('DotNet') -and (Test-Path -LiteralPath $localDotNet -PathType Leaf)) {
    $DotNet = $localDotNet
}

$mode = if ($BuiltInOnly) { 'built-in' } elseif ($Offline) { 'offline' } else { 'full' }
$binary = Join-Path $PSScriptRoot 'artifacts/test/tapka.exe'
$project = Join-Path $PSScriptRoot 'tests/Dependencies'
$Repository = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Repository)
$Assets = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Assets)

& (Join-Path $PSScriptRoot 'build.ps1') -Test -DotNet $DotNet
& $DotNet run --project $project -- --archives $binary $Repository $Assets $mode
if ($LASTEXITCODE -ne 0) {
    throw "Archive integration tests failed with exit code $LASTEXITCODE."
}
