[CmdletBinding()]
param(
    [string] $Version = '0.1.0',
    [string] $Repository = (Join-Path $PSScriptRoot '../packages'),
    [string] $Assets
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$base = Join-Path $PSScriptRoot 'artifacts/test-data'
$work = (New-Item -ItemType Directory -Path (Join-Path $base "setup $([Guid]::NewGuid().ToString('N'))") -Force).FullName
$repo = Join-Path $work 'catalog'
$downloads = Join-Path $work 'downloads'
$setup = Join-Path $PSScriptRoot 'artifacts/test/tapka-setup-win-x64.exe'
$client = Join-Path $PSScriptRoot 'artifacts/test/tapka.exe'
$zipName = 'tapka-win-x64.zip'
$zip = Join-Path $PSScriptRoot "artifacts/release/$zipName"

$assetVersions = @{}
foreach ($id in 'dotnet-runtime-11', 'powershell') {
    $config = Get-Content -LiteralPath (Join-Path $Repository "packages/$id/config.json") -Raw | ConvertFrom-Json -AsHashtable
    $release = $config.versions[$config.versions.latest.to]
    $assetVersions[$id] = $config.versions.latest.to
    $target = $release.architectures.x64
    $name = [IO.Path]::GetFileName(([Uri]$target.download.url).AbsolutePath)
    $directory = (New-Item -ItemType Directory -Path (Join-Path $downloads $id) -Force).FullName
    $archive = Join-Path $directory $name
    if ($Assets) {
        Copy-Item -LiteralPath (Join-Path $Assets $name) -Destination $archive
    } else {
        Invoke-WebRequest -Uri $target.download.url -OutFile $archive -TimeoutSec 180
    }
    $target.download.url = "https://fixtures.tapka.invalid/$id/$name"
    $directory = (New-Item -ItemType Directory -Path (Join-Path $repo "packages/$id") -Force).FullName
    $config | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $directory 'config.json')
}

$directory = (New-Item -ItemType Directory -Path (Join-Path $downloads 'tapka') -Force).FullName
Copy-Item -LiteralPath $zip -Destination $directory
Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries = @($archive.Entries.FullName)
    foreach ($expected in 'tapka.exe', 'tapka.dll', 'tapka.runtimeconfig.json', 'tapka.deps.json', 'e_sqlite3.dll') {
        if ($expected -notin $entries) { throw "The release ZIP is missing $expected." }
    }
    if ($entries | Where-Object { $_ -match '(^|/)(dotnet|pwsh)\.exe$|(^|/)coreclr\.dll$' }) {
        throw 'The release ZIP contains a runtime.'
    }
} finally {
    $archive.Dispose()
}
$config = @{
    id = 'tapka'
    versions = @{
        # Leave latest unresolvable so the test requires the embedded exact version.
        latest = @{ type = 'alias'; to = 'unpublished' }
        $Version = @{
            type = 'package'
            dependencies = @('dotnet-runtime-11', 'powershell')
            architectures = @{
                x64 = @{
                    download = @{
                        url = "https://fixtures.tapka.invalid/tapka/$zipName"
                        sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
                    }
                    install = @{ type = 'portable'; archive = 'zip' }
                    commands = @{ tapka = 'tapka.exe' }
                }
            }
        }
    }
}
$recipe = Join-Path (New-Item -ItemType Directory -Path (Join-Path $repo 'packages/tapka') -Force).FullName 'config.json'
$config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $recipe

$oldDotNet = $env:DOTNET_ROOT
$oldDotNetX64 = $env:DOTNET_ROOT_X64
$oldUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
try {
    $env:DOTNET_ROOT = Join-Path $work 'missing runtime'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $root = Join-Path $work 'install root'
    $options = @('--no-pause', '--repository', $repo, '--fixture-downloads', $downloads, '--root', $root)
    & $setup @options
    if ($LASTEXITCODE) { throw "Setup failed with exit code $LASTEXITCODE." }
    $public = Join-Path $root 'shims/public'
    $shim = Join-Path $public 'tapka.exe'
    & $shim --help
    if ($LASTEXITCODE) { throw 'The installed shim could not start tapka with its private runtime.' }
    $installed = & $client list --root $root
    if ($LASTEXITCODE -or "tapka`t$Version`tlatest`texplicit" -cnotin $installed -or
        !($installed -cmatch '^dotnet-runtime-11\t[^\t]+\t-\tautomatic$') -or
        !($installed -cmatch '^powershell\t[^\t]+\t-\tautomatic$')) {
        throw 'The client could not read the expected packages from the setup database.'
    }
    if ((Get-ChildItem -LiteralPath $public -Name) -ne 'tapka.exe' -or
        !(Test-Path -LiteralPath (Join-Path $root "packages/powershell/$($assetVersions.powershell)/pwsh.exe"))) {
        throw 'Setup did not install the expected version and private dependencies.'
    }
    & $setup @options
    if ($LASTEXITCODE -or (Get-Content -LiteralPath (Join-Path $root 'user-path.txt') -Raw) -ne $public) {
        throw 'Repeated setup failed or duplicated PATH.'
    }
    $config.versions[$Version].architectures.x64.download.sha256 = '0' * 64
    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $recipe
    $failedRoot = Join-Path $work 'failed install'
    $options[-1] = $failedRoot
    & $setup @options
    if ($LASTEXITCODE -ne 1 -or (Get-ChildItem -LiteralPath (Join-Path $failedRoot 'packages')) -or
        (Test-Path -LiteralPath (Join-Path $failedRoot 'user-path.txt'))) {
        throw 'A bad client hash did not roll back installation.'
    }
    $failedInventory = & $client list --root $failedRoot
    if ($LASTEXITCODE -or $failedInventory) { throw 'The failed setup left packages in the database.' }
    if ([Environment]::GetEnvironmentVariable('Path', 'User') -ne $oldUserPath) {
        throw 'Test setup altered the real user PATH.'
    }
    Write-Host 'Setup integration checks passed.'
} finally {
    $env:DOTNET_ROOT = $oldDotNet
    $env:DOTNET_ROOT_X64 = $oldDotNetX64
    if ($work.StartsWith([IO.Path]::GetFullPath($base) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}

# The expected checksum rejection leaves the native exit code at 1.
exit 0
