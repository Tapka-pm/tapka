using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace Tapka;

internal sealed class PackageInstaller(InstallScope scope, PackageRepository repository, HttpClient http, string? systemPowerShell)
{
    public async Task InstallAsync(string id, string version, string architecture, CancellationToken cancellationToken,
        bool verifyTapka = false)
    {
        using var installLock = await scope.AcquireInstallLockAsync(cancellationToken);
        using var inventory = InstalledPackages.OpenForInstall(scope, cancellationToken);
        var packages = Directory.CreateDirectory(scope.PackagesDirectory).FullName;
        var shims = new PackageShims(scope.Root);
        var installedPackages = inventory.List().ToDictionary(package => package.Id);
        var manifests = inventory.ReadManifests(installedPackages.Keys);
        var powerShell = FindTapkaPowerShell(packages, manifests.GetValueOrDefault("powershell")) ?? systemPowerShell;
        var plan = new List<PackageRelease>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal);

        async Task ResolveAsync(string package, string? requestedVersion = null)
        {
            if (resolved.Contains(package))
            {
                return;
            }

            if (!visiting.Add(package))
            {
                throw new IOException($"Dependency cycle at package '{package}'.");
            }

            var previous = installedPackages.GetValueOrDefault(package);
            var selector = requestedVersion ?? previous?.Selector;
            var release = await repository.ResolveAsync(package, selector ?? "latest", architecture, cancellationToken);
            foreach (var dependency in release.Dependencies)
            {
                await ResolveAsync(dependency.Id);
            }

            var needsInstallation = !IsInstalled(packages, release, previous, architecture);
            if (needsInstallation && PackageArchive.Needs7Zip(release.Archive))
            {
                await ResolveAsync(PackageArchive.ExtractorPackage);
            }
            if (needsInstallation && PackageArchive.NeedsBootstrap(release))
            {
                await ResolveAsync(PackageArchive.BootstrapPackage);
            }

            if (needsInstallation && release.Script is not null && powerShell is null &&
                !plan.Any(candidate => candidate.Id == "powershell" && candidate.Commands.ContainsKey("pwsh.exe")))
            {
                if (visiting.Contains("powershell"))
                {
                    throw new IOException($"Installing '{package}' requires an existing PowerShell 7 installation.");
                }

                await ResolveAsync("powershell");
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
        foreach (var release in plan)
        {
            manifests[release.Id] = new PackageManifest(release.Dependencies, release.Commands);
        }

        var explicitIds = manifests.Keys.Where(package =>
            package == id || installedPackages.GetValueOrDefault(package)?.Selector is not null).ToHashSet();
        var publicCommands = PackageShims.PublicCommands(manifests, explicitIds);
        var dependencyCommands = manifests.Keys.ToDictionary(package => package,
            package => PackageShims.DependencyCommands(manifests, package));
        PackageShims.CheckUnmanagedCommands(shims.PublicDirectory, publicCommands.Keys);
        foreach (var (package, commands) in dependencyCommands)
        {
            PackageShims.CheckUnmanagedCommands(shims.DependencyDirectory(package), commands.Keys);
        }

        using var transaction = new InstallTransaction(scope.Root);
        var changes = new List<InstalledPackage>();
        var messages = new List<string>();
        foreach (var release in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(packages, release.Id);
            var previous = installedPackages.GetValueOrDefault(release.Id);
            var selector = release.Id == id ? version : previous?.Selector;
            var dependencies = shims.DependencyDirectory(release.Id);
            var commands = dependencyCommands[release.Id];
            shims.Sync(dependencies, commands, dependencyCommands, transaction);
            var installed = IsInstalled(packages, release, previous, architecture);
            if (!installed)
            {
                var bootstrap = PackageArchive.NeedsBootstrap(release)
                    ? PackageArchive.GetTool(packages, PackageArchive.BootstrapPackage,
                        manifests[PackageArchive.BootstrapPackage].Commands)
                    : null;
                var extractor = PackageArchive.Needs7Zip(release.Archive)
                    ? PackageArchive.GetTool(packages, PackageArchive.ExtractorPackage,
                        manifests[PackageArchive.ExtractorPackage].Commands)
                    : null;
                var staged = await StagePackageAsync(release, transaction.CreateStagingDirectory(release.Id),
                    commands.Count == 0 ? null : dependencies, powerShell, bootstrap, extractor, cancellationToken);
                transaction.PublishPackage(staged, destination);
            }

            // A reused payload keeps its provenance even if the recipe has since changed.
            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            changes.Add(installed && previous is not null ? previous with { Selector = selector } :
                new InstalledPackage(release.Id, release.Version, selector, architecture, repository.Source,
                    release.DownloadUrl.AbsoluteUri, release.Checksum, previous is null ? now : previous.InstalledAt,
                    previous?.Version == release.Version ? previous.VersionChangedAt : now));

            if (release.Id == "powershell")
            {
                powerShell = FindTapkaPowerShell(packages, manifests[release.Id]) ?? systemPowerShell;
            }
            var name = selector is null or "latest" ? release.Id : $"{release.Id}@{selector}";
            messages.Add(installed
                ? $"{name} {release.Version} is already installed at \"{destination}\"."
                : $"Installed {name} {release.Version} at \"{destination}\".");
        }

        foreach (var (package, commands) in dependencyCommands)
        {
            if (!resolved.Contains(package))
            {
                shims.Sync(shims.DependencyDirectory(package), commands, dependencyCommands, transaction);
            }
        }
        shims.Sync(shims.PublicDirectory, publicCommands, dependencyCommands, transaction);
        if (verifyTapka)
        {
            var start = new ProcessStartInfo(Path.Combine(shims.PublicDirectory, "tapka.exe"));
            start.ArgumentList.Add("--help");
            await InstallProcess.RunAsync(start, "installed tapka", cancellationToken,
                async (process, token) => { await process.StandardOutput.ReadToEndAsync(token); },
                TimeSpan.FromSeconds(30));
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var databaseTransaction = inventory.BeginWrite();
        foreach (var package in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inventory.Save(package, manifests[package.Id], databaseTransaction);
        }
        List<string> warnings = publicCommands.Count == 0 ? [] : scope.RegisterShims(shims.PublicDirectory,
            plan.Where(release => explicitIds.Contains(release.Id)).SelectMany(release => release.Commands.Keys), transaction);
        cancellationToken.ThrowIfCancellationRequested();
        databaseTransaction.Commit();
        transaction.Commit();
        foreach (var message in messages)
        {
            Console.WriteLine(message);
        }
        foreach (var warning in warnings)
        {
            Console.Error.WriteLine(warning);
        }
    }

    private static bool IsInstalled(string packages, PackageRelease release, InstalledPackage? previous, string architecture)
    {
        var destination = Path.Combine(packages, release.Id);
        return previous is not null && previous.Version == release.Version &&
            (previous.Architecture is null || previous.Architecture == architecture) && Directory.Exists(destination) &&
            release.Commands.Values.All(command => File.Exists(Path.Combine(destination, command))) &&
            PackageArchive.HasToolFiles(destination, release.Id, release.Commands);
    }

    private async Task<string> StagePackageAsync(PackageRelease release, string staging, string? dependencies,
        string? powerShell, string? bootstrap, string? extractor, CancellationToken cancellationToken)
    {
        var installerDirectory = Directory.CreateDirectory(Path.Combine(staging, "download")).FullName;
        var installer = Path.Combine(installerDirectory, PackageRepository.GetDownloadFilename(release.DownloadUrl));
        var stagedPackage = Directory.CreateDirectory(Path.Combine(staging, "package")).FullName;
        await DownloadAsync(release, installer, cancellationToken);
        if (release.Archive is { } archive)
        {
            await PackageArchive.ExtractAsync(archive, installer, stagedPackage, extractor, cancellationToken);
        }
        if (release.Script is not null)
        {
            var script = Path.Combine(staging, "install.ps1");
            await File.WriteAllBytesAsync(script, await repository.ReadScriptAsync(release, cancellationToken), cancellationToken);
            await RunScriptAsync(powerShell ?? throw new IOException("An existing PowerShell 7 installation is required."),
                script, installer, stagedPackage, dependencies, bootstrap, cancellationToken);
        }

        foreach (var (command, target) in release.Commands)
        {
            if (!File.Exists(Path.Combine(stagedPackage, target)))
            {
                throw new IOException($"Package '{release.Id}' did not provide '{target}' for '{command}'.");
            }
        }

        if (!PackageArchive.HasToolFiles(stagedPackage, release.Id, release.Commands))
        {
            throw new IOException($"Package '{release.Id}' did not provide the required extractor files.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return stagedPackage;
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

    private static string? FindTapkaPowerShell(string packages, PackageManifest? manifest)
    {
        if (manifest?.Commands.GetValueOrDefault("pwsh.exe") is not { } target)
        {
            return null;
        }
        var path = Path.Combine(packages, "powershell", target);
        return File.Exists(path) ? path : null;
    }

    public static string? FindSystemPowerShell()
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

    private static Task RunScriptAsync(string powerShell, string script, string installer, string destination,
        string? dependencies, string? bootstrap, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(powerShell)
        {
            WorkingDirectory = Path.GetDirectoryName(script)!
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-Installer", installer, "-Destination", destination })
        {
            start.ArgumentList.Add(argument);
        }

        if (bootstrap is not null)
        {
            // PowerShell may prepend its own directory to PATH during startup.
            start.Environment["TAPKA_7ZR"] = bootstrap;
            var directory = Path.GetDirectoryName(bootstrap)!;
            dependencies = dependencies is null ? directory : $"{directory};{dependencies}";
        }
        if (dependencies is not null)
        {
            var inheritedPath = Environment.GetEnvironmentVariable("PATH");
            start.Environment["PATH"] = string.IsNullOrEmpty(inheritedPath) ? dependencies : $"{dependencies};{inheritedPath}";
        }

        return InstallProcess.RunAsync(start, "PowerShell installation script", cancellationToken);
    }
}
