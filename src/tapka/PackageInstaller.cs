using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Tapka;

internal sealed class PackageInstaller(InstallScope scope, PackageRepository repository, HttpClient http)
{
    private const string InstallMarker = ".tapka-version";

    public async Task InstallAsync(string id, string version, string architecture, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(scope.Root);
        using var installLock = await AcquireLockAsync(cancellationToken);
        var packages = Directory.CreateDirectory(Path.Combine(scope.Root, "packages")).FullName;
        var shims = new PackageShims(Path.Combine(scope.Root, "shims"));
        var powerShell = shims.FindPowerShell() ?? FindSystemPowerShell();
        var plan = new List<PackageRelease>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal);

        async Task ResolveAsync(string package, string requestedVersion)
        {
            if (resolved.Contains(package))
            {
                return;
            }

            if (!visiting.Add(package))
            {
                throw new IOException($"Dependency cycle at package '{package}'.");
            }

            var release = await repository.ResolveAsync(package, requestedVersion, architecture, cancellationToken);
            foreach (var dependency in release.Dependencies)
            {
                await ResolveAsync(dependency, "latest");
            }

            if (release.Script is not null && !IsInstalled(packages, release) && powerShell is null &&
                !plan.Any(candidate => candidate.Id == "powershell" && candidate.Commands.ContainsKey("pwsh.exe")))
            {
                if (visiting.Contains("powershell"))
                {
                    throw new IOException($"Installing '{package}' requires an existing PowerShell 7 installation.");
                }

                await ResolveAsync("powershell", "latest");
                if (!plan.Any(candidate => candidate.Id == "powershell" && candidate.Commands.ContainsKey("pwsh.exe")))
                {
                    throw new IOException("The PowerShell package must provide 'pwsh.exe' to run installation scripts.");
                }
            }

            visiting.Remove(package);
            resolved.Add(package);
            plan.Add(release);
        }

        await ResolveAsync(id, version);
        shims.CheckConflicts(plan, packages);
        foreach (var release in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(packages, release.Id);
            var installed = IsInstalled(packages, release);
            if (installed)
            {
                shims.Publish(release);
            }
            else
            {
                await InstallPackageAsync(release, packages, destination, shims, powerShell, cancellationToken);
            }

            powerShell = shims.FindPowerShell() ?? powerShell;
            Console.WriteLine(installed
                ? $"{release.Id} {release.Version} is already installed at \"{destination}\"."
                : $"Installed {release.Id} {release.Version} at \"{destination}\".");
        }

        var commands = plan.SelectMany(release => release.Commands.Keys).ToArray();
        if (commands.Length > 0)
        {
            scope.RegisterShims(shims.DirectoryPath, commands);
        }
    }

    private static bool IsInstalled(string packages, PackageRelease release)
    {
        var destination = Path.Combine(packages, release.Id);
        var marker = Path.Combine(destination, InstallMarker);
        return File.Exists(marker) && File.ReadAllText(marker) == release.Version &&
            release.Commands.Values.All(command => File.Exists(Path.Combine(destination, command)));
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var lockFile = Path.Combine(scope.Root, ".install.lock");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return File.Open(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                if (elapsed.Elapsed >= TimeSpan.FromMinutes(10))
                {
                    throw new IOException("Timed out waiting for another tapka installation to finish.", error);
                }

                await Task.Delay(200, cancellationToken);
            }
        }
    }

    private async Task InstallPackageAsync(PackageRelease release, string packages, string destination,
        PackageShims shims, string? powerShell, CancellationToken cancellationToken)
    {
        // Stage beside the package so publishing and restoring it are same-volume renames.
        var staging = Directory.CreateDirectory(Path.Combine(packages, $".{release.Id}-{Guid.NewGuid():N}")).FullName;
        var previous = Path.Combine(staging, "previous");
        var completed = false;
        try
        {
            var installerDirectory = Directory.CreateDirectory(Path.Combine(staging, "download")).FullName;
            var installer = Path.Combine(installerDirectory, PackageRepository.GetDownloadFilename(release.DownloadUrl));
            var stagedPackage = Directory.CreateDirectory(Path.Combine(staging, "package")).FullName;
            await DownloadAsync(release, installer, cancellationToken);

            if (release.Script is null)
            {
                ZipFile.ExtractToDirectory(installer, stagedPackage);
            }
            else
            {
                var script = Path.Combine(staging, "install.ps1");
                await File.WriteAllBytesAsync(script, await repository.ReadScriptAsync(release, cancellationToken), cancellationToken);
                await RunScriptAsync(powerShell!, script, installer, stagedPackage, shims.DirectoryPath, cancellationToken);
            }

            foreach (var (command, target) in release.Commands)
            {
                if (!File.Exists(Path.Combine(stagedPackage, target)))
                {
                    throw new IOException($"Package '{release.Id}' did not provide '{target}' for '{command}'.");
                }
            }

            await File.WriteAllTextAsync(Path.Combine(stagedPackage, InstallMarker), release.Version, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, previous);
            }

            var published = false;
            try
            {
                Directory.Move(stagedPackage, destination);
                published = true;
                shims.Publish(release);
            }
            catch
            {
                if (published)
                {
                    Directory.Move(destination, stagedPackage);
                }

                if (Directory.Exists(previous))
                {
                    Directory.Move(previous, destination);
                }

                throw;
            }

            completed = true;
        }
        finally
        {
            // Keep the backup recoverable if restoring the previous package failed.
            if (completed || !Directory.Exists(previous))
            {
                Cleanup(staging);
            }
        }
    }

    private async Task DownloadAsync(PackageRelease release, string installer, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var sha256 = release.Checksum.Length == 64;
        Console.WriteLine($"Downloading {release.Id} {release.Version}...");
        try
        {
            using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await response.Content.CopyToAsync(output, deadline.Token);
            }

            await using var input = File.OpenRead(installer);
            var hash = Convert.ToHexString(sha256 ? await SHA256.HashDataAsync(input, deadline.Token)
                : await SHA512.HashDataAsync(input, deadline.Token));
            if (!hash.Equals(release.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{release.Id} SHA-{(sha256 ? 256 : 512)} mismatch. Expected {release.Checksum}, got {hash}.");
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"{release.Id} download timed out.", error);
        }
        catch (HttpRequestException error)
        {
            throw new IOException($"Could not download '{release.Id}': {error.Message}", error);
        }
    }

    private static string? FindSystemPowerShell()
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var directory = entry.Trim().Trim('"');
            if (Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, "pwsh.exe")))
            {
                return Path.Combine(directory, "pwsh.exe");
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var standardLocation = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        return programFiles.Length > 0 && File.Exists(standardLocation) ? standardLocation : null;
    }

    private static async Task RunScriptAsync(string powerShell, string script, string installer, string destination,
        string shims, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(powerShell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script)!
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-Installer", installer, "-Destination", destination })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PATH"] = shims + ";" + Environment.GetEnvironmentVariable("PATH");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var process = Process.Start(start) ?? throw new IOException("Could not start PowerShell.");
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException error)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // PowerShell already exited.
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("The PowerShell installation script timed out.", error);
        }

        if (process.ExitCode != 0)
        {
            throw new IOException($"The PowerShell installation script failed (exit code {process.ExitCode}).");
        }
    }

    private static void Cleanup(string staging)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"tapka: could not clean up \"{staging}\": {error.Message}");
        }
    }
}
