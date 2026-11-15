[CmdletBinding()]
param(
    [switch] $Test,
    [string] $DotNet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$flavor = if ($Test) { 'test' } else { 'release' }
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$localDotNet = Join-Path $artifacts 'tools/dotnet/dotnet.exe'
if (!$PSBoundParameters.ContainsKey('DotNet') -and (Test-Path -LiteralPath $localDotNet -PathType Leaf)) {
    $DotNet = $localDotNet
}
$output = Join-Path $artifacts $flavor
$build = Join-Path $artifacts "build/$flavor"
$shim = Join-Path $artifacts 'build/shim/Release/x64/shim.exe'
$testBuild = if ($Test) { 'true' } else { 'false' }

& $DotNet msbuild (Join-Path $PSScriptRoot 'src/shim/shim.proj') -nologo -p:Configuration=Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "Shim build failed with exit code $LASTEXITCODE."
}

$publish = @(
    'publish', (Join-Path $PSScriptRoot 'src/tapka/tapka.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', '--artifacts-path', $build, '-o', $output,
    "-p:TapkaShimPath=$shim", "-p:TapkaTestBuild=$testBuild"
)
& $DotNet @publish
if ($LASTEXITCODE -ne 0) {
    throw "Tapka publication failed with exit code $LASTEXITCODE."
}

Write-Host (Join-Path $output 'tapka.exe')
