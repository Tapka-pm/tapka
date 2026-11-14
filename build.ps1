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
$shim = Join-Path $build 'shim.exe'
$testBuild = if ($Test) { 'true' } else { 'false' }

$restoreEnvironment = $false
$savedEnvironment = @{}
try {
    $compiler = Get-Command cl.exe -ErrorAction SilentlyContinue
    if (!$compiler) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        $buildToolsUrl = 'https://visualstudio.microsoft.com/visual-cpp-build-tools/'
        if (!(Test-Path -LiteralPath $vswhere -PathType Leaf)) {
            throw "MSVC cl.exe was not found in PATH and vswhere.exe is unavailable. Install Visual Studio Build Tools with the Desktop development with C++ workload: $buildToolsUrl"
        }

        $installationPath = & $vswhere '-latest' '-products' '*' '-requires' 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' '-property' 'installationPath'
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installationPath)) {
            throw "No Visual Studio installation with the MSVC x64 tools was found. Install Visual Studio Build Tools with the Desktop development with C++ workload: $buildToolsUrl"
        }
        $vcvarsall = Join-Path $installationPath.Trim() 'VC\Auxiliary\Build\vcvarsall.bat'
        if (!(Test-Path -LiteralPath $vcvarsall -PathType Leaf)) {
            throw "MSVC environment setup script was not found at '$vcvarsall'. Repair Visual Studio Build Tools with the Desktop development with C++ workload."
        }

        foreach ($variable in Get-ChildItem Env:) {
            $savedEnvironment[$variable.Name] = $variable.Value
        }
        $restoreEnvironment = $true
        $environmentOutput = & $env:ComSpec /d /c "`"$vcvarsall`" x64 >nul && set"
        if ($LASTEXITCODE -ne 0) {
            throw "MSVC environment initialization failed with exit code $LASTEXITCODE."
        }
        foreach ($line in $environmentOutput) {
            $separator = $line.IndexOf('=')
            if ($separator -gt 0) {
                [Environment]::SetEnvironmentVariable($line.Substring(0, $separator), $line.Substring($separator + 1), 'Process')
            }
        }
        $compiler = Get-Command cl.exe -ErrorAction SilentlyContinue
        if (!$compiler) {
            throw 'MSVC environment initialization succeeded, but cl.exe was not found.'
        }
    }

    New-Item -ItemType Directory -Path $build -Force | Out-Null
    $source = Join-Path $PSScriptRoot 'src/shim/launcher.c'
    & $compiler.Source /nologo /std:c17 /O2 /W4 /WX /MT /GS /DUNICODE /D_UNICODE /D_CRT_SECURE_NO_WARNINGS "/Fo$build\" $source /link /SUBSYSTEM:CONSOLE "/OUT:$shim"
    if ($LASTEXITCODE -ne 0) { throw "Shim compilation failed with exit code $LASTEXITCODE." }

    $publish = @(
        'publish', (Join-Path $PSScriptRoot 'src/tapka/tapka.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', '--artifacts-path', $build, '-o', $output,
        "-p:TapkaShimPath=$shim", "-p:TapkaTestBuild=$testBuild"
    )
    & $DotNet @publish
    if ($LASTEXITCODE -ne 0) { throw "Tapka publication failed with exit code $LASTEXITCODE." }

    Write-Host (Join-Path $output 'tapka.exe')
}
finally {
    if ($restoreEnvironment) {
        foreach ($variable in Get-ChildItem Env:) {
            if (!$savedEnvironment.ContainsKey($variable.Name)) {
                [Environment]::SetEnvironmentVariable($variable.Name, $null, 'Process')
            }
        }
        foreach ($name in $savedEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
    }
}
