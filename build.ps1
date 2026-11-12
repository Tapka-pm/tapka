[CmdletBinding()]
param(
    [switch] $Test,
    [string] $DotNet = 'dotnet',
    [string] $Zig = 'zig'
)

$ErrorActionPreference = 'Stop'
$flavor = if ($Test) { 'test' } else { 'release' }
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$localDotNet = Join-Path $artifacts 'tools/dotnet/dotnet.exe'
$localZig = Join-Path $artifacts 'tools/zig/zig.exe'
if (!$PSBoundParameters.ContainsKey('DotNet') -and (Test-Path -LiteralPath $localDotNet -PathType Leaf)) {
    $DotNet = $localDotNet
}
if (!$PSBoundParameters.ContainsKey('Zig') -and (Test-Path -LiteralPath $localZig -PathType Leaf)) {
    $Zig = $localZig
}
$output = Join-Path $artifacts $flavor
$build = Join-Path $artifacts "build/$flavor"
$shim = Join-Path $build 'git.exe'
$testBuild = if ($Test) { 'true' } else { 'false' }

New-Item -ItemType Directory -Path $build -Force | Out-Null
$source = Join-Path $PSScriptRoot 'src/shim/git.c'
& $Zig cc -target x86_64-windows-gnu -municode -O2 -s -Wall -Wextra -Werror $source -o $shim
if ($LASTEXITCODE -ne 0) { throw "Shim compilation failed with exit code $LASTEXITCODE." }

$publish = @(
    'publish', (Join-Path $PSScriptRoot 'src/tapka/tapka.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', '--artifacts-path', $build, '-o', $output,
    "-p:TapkaShimPath=$shim", "-p:TapkaTestBuild=$testBuild"
)
& $DotNet @publish
if ($LASTEXITCODE -ne 0) { throw "Tapka publication failed with exit code $LASTEXITCODE." }

Write-Host (Join-Path $output 'tapka.exe')
