[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z')]
    [string] $Tag
)

$ErrorActionPreference = 'Stop'
$repo = 'Tapka-pm/tapka'
$catalog = 'https://api.github.com/repos/Tapka-pm/packages'
$recipePath = 'packages/tapka/config.json'
$branch = 'master'
$version = $Tag.Substring(1)
$files = @('tapka-setup-win-x64.exe', 'tapka-win-x64.zip')
$output = Join-Path $PSScriptRoot '../../artifacts/release'
if ($env:GITHUB_REPOSITORY -ne $repo -or !$env:GH_TOKEN -or !$env:PACKAGES_TOKEN) {
    throw 'Publishing requires the Tapka-pm/tapka workflow and a PACKAGES_TOKEN with write access to Tapka-pm/packages.'
}

function Invoke-Gh {
    $result = & gh @args
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI failed (exit code $LASTEXITCODE)." }
    $result
}

function Get-Releases {
    $json = (Invoke-Gh api --paginate --slurp "repos/$repo/releases?per_page=100") -join "`n"
    foreach ($page in (ConvertFrom-Json -InputObject $json -NoEnumerate)) {
        foreach ($release in $page) { $release }
    }
}

function Read-CatalogFile([string] $Path) {
    Invoke-RestMethod -Uri "$catalog/contents/${Path}?ref=$branch" -Headers $catalogHeaders
}

function Decode-CatalogFile($File) {
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($File.content))
}

function Update-Catalog([switch] $AdvanceLatest) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $file = Read-CatalogFile $recipePath
        $original = Decode-CatalogFile $file
        $recipe = ConvertFrom-Json -InputObject $original -AsHashtable
        if ($recipe.id -ne 'tapka') { throw 'Unexpected recipe ID in the tapka catalog.' }
        $versions = $recipe.versions
        $existing = $versions[$version].architectures.x64.download
        if ($existing) {
            if ($existing.url -cne $zipUrl -or $existing.sha256 -cne $zipHash) {
                throw "The catalog already associates tapka $version with a different ZIP."
            }
        } else {
            $versions[$version] = [ordered] @{
                type = 'package'
                dependencies = @('dotnet-runtime-11', 'powershell')
                architectures = [ordered] @{
                    x64 = [ordered] @{
                        download = [ordered] @{ url = $zipUrl; sha256 = $zipHash }
                        install = [ordered] @{ type = 'portable'; archive = 'zip' }
                        commands = [ordered] @{ tapka = 'tapka.exe' }
                    }
                }
            }
        }
        $current = $versions.latest.to
        if ($AdvanceLatest -and (!$versions[$current].architectures.x64.download -or
            [version]$version -gt [version]$current)) {
            $versions.latest.to = $version
        }
        $json = (ConvertTo-Json -InputObject $recipe -Depth 20) + "`n"
        if (!(Test-Json -Json $json -Schema $schema)) { throw 'Generated tapka recipe failed schema validation.' }
        if ($existing -and $versions.latest.to -eq $current) { return }

        $payload = @{
            message = "Update tapka catalog for $Tag"
            branch = $branch
            sha = $file.sha
            content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
        }
        try {
            Invoke-RestMethod -Method Put -Uri "$catalog/contents/$recipePath" -Headers $catalogHeaders `
                -ContentType 'application/json' -Body (ConvertTo-Json $payload) | Out-Null
            return
        } catch {
            if ([int]$_.Exception.Response.StatusCode -ne 409 -or $attempt -eq 3) { throw }
        }
    }
}

$catalogHeaders = @{
    Authorization = "Bearer $env:PACKAGES_TOKEN"
    Accept = 'application/vnd.github+json'
}
$schema = Decode-CatalogFile (Read-CatalogFile 'package-schema.json')
# Publishing must use an existing Git tag.
Invoke-Gh api "repos/$repo/git/ref/tags/$Tag" | Out-Null
$releases = Get-Releases
$matching = @($releases | Where-Object tag_name -CEQ $Tag)
if ($matching.Count -gt 1) { throw "More than one release matches $Tag." }
$release = $matching | Select-Object -First 1
if (!$release) {
    # Reuse the response instead of waiting for the new draft to appear in the list.
    $release = Invoke-Gh api "repos/$repo/releases" --method POST -f "tag_name=$Tag" -f "name=$Tag" -F draft=true |
        ConvertFrom-Json
    if (!$release.draft -or !$release.id) { throw "GitHub did not return a release draft for $Tag." }
}
$releasePath = "repos/$repo/releases/$($release.id)"
$releaseHeaders = @{
    Authorization = "Bearer $env:GH_TOKEN"
    Accept = 'application/vnd.github+json'
}
Write-Host "Using release $($release.id) for $Tag."
$assets = $release.assets
foreach ($name in $files) {
    $asset = $assets | Where-Object name -CEQ $name
    if ($asset.state -eq 'uploaded') { continue }
    if (!$release.draft) { throw "The published release is missing $name." }
    if ($asset) {
        if ($asset.state -ne 'starter') { throw "Unexpected upload state for $name." }
        Invoke-Gh api --method DELETE "repos/$repo/releases/assets/$($asset.id)" | Out-Null
    }
    $file = Join-Path $output $name
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release file: $file." }
    $uploadUrl = $release.upload_url -replace '\{.*\}$', ''
    if ($uploadUrl -ne "https://uploads.github.com/$releasePath/assets") {
        throw "Unexpected upload URL for release $($release.id)."
    }
    Write-Host "Uploading $name..."
    Invoke-RestMethod -Method Post -Uri "${uploadUrl}?name=$([Uri]::EscapeDataString($name))" `
        -Headers $releaseHeaders -ContentType 'application/octet-stream' -InFile $file -TimeoutSec 120 |
        Out-Null
}
$release = Invoke-Gh api $releasePath | ConvertFrom-Json
$hashes = @{}
foreach ($name in $files) {
    $asset = $release.assets | Where-Object name -CEQ $name
    if ($asset.state -ne 'uploaded' -or $asset.digest -cnotmatch '\Asha256:([a-f0-9]{64})\z') {
        throw "GitHub did not report a SHA-256 digest for $name."
    }
    $hashes[$name] = $Matches[1]
}
$zipUrl = "https://github.com/$repo/releases/download/$Tag/tapka-win-x64.zip"
$zipHash = $hashes['tapka-win-x64.zip']

# Make the exact version resolvable before exposing the installer. Keep the old
# catalog latest usable if the workflow stops while this release is still a draft.
Update-Catalog
if ($release.draft) {
    $newer = $releases | Where-Object {
        !$_.draft -and !$_.prerelease -and $_.tag_name -cmatch '\Av([0-9]+\.[0-9]+\.[0-9]+)\z' -and
        [version]$Matches[1] -gt [version]$version
    } | Select-Object -First 1
    $latest = if ($newer) { 'false' } else { 'true' }
    $notes = @(
        'See the README for the PowerShell and CMD install commands.'
        ''
        'SHA-256'
        foreach ($name in $files) { "- $name $($hashes[$name])" }
    ) -join "`n"
    $published = Invoke-Gh api $releasePath --method PATCH -F draft=false -f "tag_name=$Tag" -f "make_latest=$latest" -f "body=$notes" |
        ConvertFrom-Json
    if ($published.draft -ne $false -or $published.tag_name -cne $Tag) {
        throw "GitHub did not publish release $($release.id) as $Tag."
    }
    Write-Host $published.html_url
}
Update-Catalog -AdvanceLatest
