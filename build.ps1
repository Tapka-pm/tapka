[CmdletBinding()]
param(
    [switch] $Test,
    [string] $DotNet = 'dotnet',
    [ValidatePattern('\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z')]
    [string] $Version = '0.1.0',
    [ValidateSet('All', 'Client', 'Setup')]
    [string] $Target
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
if (!$Target) { $Target = if ($Test) { 'Client' } else { 'All' } }

& $DotNet msbuild (Join-Path $PSScriptRoot 'src/shim/shim.proj') -nologo -p:Configuration=Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "Shim build failed with exit code $LASTEXITCODE."
}

function Publish-Tapka([string] $Project, [string] $Directory, [bool] $SelfContained) {
    & $DotNet publish (Join-Path $PSScriptRoot $Project) -c Release -r win-x64 `
        --self-contained $SelfContained.ToString().ToLowerInvariant() --artifacts-path $build -o $Directory `
        "-p:TapkaShimPath=$shim" "-p:TapkaTestBuild=$testBuild" "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "Publication of $Project failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$staging = (New-Item -ItemType Directory -Path (Join-Path $build ([Guid]::NewGuid().ToString('N')))).FullName
try {
    if ($Target -in 'All', 'Client') {
        $client = Join-Path $staging 'client'
        Publish-Tapka 'src/tapka/tapka.csproj' $client $Test.IsPresent
        if ($Test) {
            Copy-Item -LiteralPath (Join-Path $client 'tapka.exe') -Destination $output -Force
            Write-Host (Join-Path $output 'tapka.exe')
        } else {
            $zip = Join-Path $staging 'tapka-win-x64.zip'
            [IO.Compression.ZipFile]::CreateFromDirectory($client, $zip)
            Move-Item -LiteralPath $zip -Destination $output -Force
            Write-Host (Join-Path $output 'tapka-win-x64.zip')
        }
    }
    if ($Target -in 'All', 'Setup') {
        $setup = Join-Path $staging 'setup'
        Publish-Tapka 'src/setup/setup.csproj' $setup $true
        $exe = 'tapka-setup-win-x64.exe'
        Copy-Item -LiteralPath (Join-Path $setup $exe) -Destination $output -Force
        Write-Host (Join-Path $output $exe)
    }
} finally {
    $buildPrefix = [IO.Path]::GetFullPath($build) + [IO.Path]::DirectorySeparatorChar
    if ($staging.StartsWith($buildPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
