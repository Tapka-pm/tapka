[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Output
)

$ErrorActionPreference = 'Stop'
$compiler = Get-Command cl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (!$compiler) {
    $vswhere = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'MSVC cl.exe and vswhere.exe were not found. Install Visual Studio Build Tools with the Desktop development with C++ workload.'
    }

    $installationPath = & $vswhere '-latest' '-products' '*' '-requires' 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' '-property' 'installationPath'
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installationPath)) {
        throw 'No Visual Studio installation with the MSVC x64 tools was found.'
    }
    $vcvarsall = Join-Path $installationPath.Trim() 'VC\Auxiliary\Build\vcvarsall.bat'
    if (!(Test-Path -LiteralPath $vcvarsall -PathType Leaf)) {
        throw "MSVC environment setup script was not found at '$vcvarsall'."
    }

    $environmentOutput = & $env:ComSpec /d /c ('"{0}" x64 >nul && set' -f $vcvarsall)
    if ($LASTEXITCODE -ne 0) {
        throw "MSVC environment initialization failed with exit code $LASTEXITCODE."
    }
    foreach ($line in $environmentOutput) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) {
            [Environment]::SetEnvironmentVariable($line.Substring(0, $separator), $line.Substring($separator + 1), 'Process')
        }
    }
    $compiler = Get-Command cl.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
}

$outputPath = [IO.Path]::GetFullPath($Output)
$sourcePath = [IO.Path]::GetFullPath($Source)
$objectPath = [IO.Path]::ChangeExtension($outputPath, '.obj')
& $compiler.Source /nologo /std:c17 /O2 /W4 /WX /MT /GS /DUNICODE /D_UNICODE /D_CRT_SECURE_NO_WARNINGS "/Fo$objectPath" $sourcePath /link /SUBSYSTEM:CONSOLE /MACHINE:X64 "/OUT:$outputPath"
if ($LASTEXITCODE -ne 0) {
    throw "Native compilation of '$sourcePath' failed with exit code $LASTEXITCODE."
}
