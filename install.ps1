& {
    $ErrorActionPreference = 'Stop'
    $tempRoot = [IO.Path]::GetTempPath()
    $directory = Join-Path $tempRoot ('tapka-setup-' + [Guid]::NewGuid().ToString('N'))
    $installer = Join-Path $directory 'tapka-setup-win-x64.exe'
    $oldProtocols = [Net.ServicePointManager]::SecurityProtocol
    try {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        if ($PSVersionTable.PSVersion.Major -eq 5) {
            [Net.ServicePointManager]::SecurityProtocol = $oldProtocols -bor [Net.SecurityProtocolType]::Tls12
        }
        Write-Host 'Downloading the tapka installer...'
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -UseBasicParsing -TimeoutSec 120 -OutFile $installer -Uri (
            'https://github.com/Tapka-pm/tapka/releases/latest/download/tapka-setup-win-x64.exe'
        )
        $process = Start-Process -FilePath $installer -ArgumentList '--no-pause' -NoNewWindow -Wait -PassThru
        try {
            if ($process.ExitCode -ne 0) {
                throw "The tapka installer exited with code $($process.ExitCode)."
            }
        } finally {
            $process.Dispose()
        }

        $shims = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'tapka\shims\public'
        $present = ($env:Path -split ';') | Where-Object {
            [Environment]::ExpandEnvironmentVariables($_.Trim().Trim('"')).TrimEnd('\', '/') -eq $shims
        }
        if (!$present) {
            $env:Path = if ($env:Path) { "$shims;$env:Path" } else { $shims }
        }
    } catch {
        throw "tapka: $($_.Exception.Message)"
    } finally {
        if ($PSVersionTable.PSVersion.Major -eq 5) {
            [Net.ServicePointManager]::SecurityProtocol = $oldProtocols
        }
        if ((Split-Path -Parent $installer) -eq $directory -and
            (Split-Path -Parent $directory) -eq $tempRoot.TrimEnd('\', '/')) {
            Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
            if ([IO.Directory]::Exists($directory)) {
                try {
                    [IO.Directory]::Delete($directory)
                } catch [IO.IOException], [UnauthorizedAccessException] {
                    Write-Warning "Could not remove temporary directory '$directory'."
                }
            }
        }
    }
}
