[CmdletBinding()]
param(
    [string] $Binary = (Join-Path $PSScriptRoot 'artifacts/test/tapka.exe'),
    [string] $Repository
)

$ErrorActionPreference = 'Stop'
$binaryPath = (Resolve-Path -LiteralPath $Binary).Path
$testParent = (New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'artifacts/test-data') -Force).FullName
$testRoot = Join-Path $testParent "smoke $([Guid]::NewGuid().ToString('N'))"
$shimDirectory = Join-Path $testRoot 'shims'
$savedTestRoot = $env:TAPKA_TEST_ROOT
$savedPath = $env:Path
$savedUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')

function Invoke-Checked {
    param([string] $Executable, [string[]] $Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

try {
    Invoke-Checked $binaryPath @('--help')
    $installOptions = @('--root', $testRoot)
    if ($Repository) { $installOptions += @('--repository', $Repository) }
    Invoke-Checked $binaryPath (@('install', 'git') + $installOptions)
    $marker = Join-Path $testRoot 'packages/git/.tapka-version'
    $installedAt = (Get-Item -LiteralPath $marker).LastWriteTimeUtc
    $version = Get-Content -LiteralPath $marker -Raw
    Invoke-Checked $binaryPath (@('install', "git@$version") + $installOptions)

    $testPath = Get-Content -LiteralPath (Join-Path $testRoot 'user-path.txt') -Raw
    if ($testPath -ne $shimDirectory -or (Get-Item -LiteralPath $marker).LastWriteTimeUtc -ne $installedAt) {
        throw 'The repeated install changed the package or duplicated the shim directory in PATH.'
    }
    if ([Environment]::GetEnvironmentVariable('Path', 'User') -ne $savedUserPath) {
        throw 'The test binary changed the real user PATH.'
    }

    $env:TAPKA_TEST_ROOT = $testRoot
    $env:Path = "$testPath;$savedPath"
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $commands = @'
$ErrorActionPreference = 'Stop'
$expected = Join-Path $env:TAPKA_TEST_ROOT 'shims\git.exe'
if ((Get-Command git.exe).Source -ne $expected) { throw 'PowerShell selected a different git.exe.' }
git --version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
git init 'repository with spaces'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Set-Location -LiteralPath 'repository with spaces'
git status --short
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$value = git -c 'tapka.test=value with spaces' config --get tapka.test
if ($LASTEXITCODE -ne 0 -or $value -cne 'value with spaces') { throw 'Git received different arguments.' }
git diff --quiet --no-index -- file-a file-b
if ($LASTEXITCODE -ne 1) { throw 'The shim returned a different exit code.' }
exit 0
'@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($commands))
    $powerShellRoot = (New-Item -ItemType Directory -Path (Join-Path $testRoot 'powershell')).FullName
    $repo = (New-Item -ItemType Directory -Path (Join-Path $powerShellRoot 'repository with spaces')).FullName
    Set-Content -LiteralPath (Join-Path $repo 'file-a') -Value 'a'
    Set-Content -LiteralPath (Join-Path $repo 'file-b') -Value 'b'
    Push-Location -LiteralPath $powerShellRoot
    try {
        Invoke-Checked $windowsPowerShell @('-NoProfile', '-EncodedCommand', $encoded)
        if (!(Test-Path -LiteralPath (Join-Path $repo '.git'))) {
            throw 'Git did not preserve the PowerShell working directory.'
        }
    }
    finally {
        Pop-Location
    }

    $cmdRoot = (New-Item -ItemType Directory -Path (Join-Path $testRoot 'cmd')).FullName
    Push-Location -LiteralPath $cmdRoot
    try {
        $firstGit = & $env:ComSpec /d /c 'where git.exe' | Select-Object -First 1
        if ($firstGit -ne (Join-Path $shimDirectory 'git.exe')) { throw 'cmd.exe selected a different git.exe.' }
        Invoke-Checked $env:ComSpec @('/d', '/c', 'git --version && git init && git status')
    }
    finally {
        Pop-Location
    }

    Write-Host 'Smoke test passed.'
}
finally {
    $env:TAPKA_TEST_ROOT = $savedTestRoot
    $env:Path = $savedPath
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = (Resolve-Path -LiteralPath $testRoot).ProviderPath
        if ($resolved.StartsWith($testParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
