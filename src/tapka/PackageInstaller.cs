using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tapka;

internal sealed class PackageInstaller(InstallScope scope, PackageRepository repository, HttpClient http, string? systemPowerShell)
{
    private const string InstallMarker = ".tapka-version";
    private const string InstallSelector = ".tapka-selector";
    private const string ManifestFile = ".tapka-package.json";

    public async Task InstallAsync(string id, string version, string architecture, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(scope.Root);
        using var installLock = await AcquireLockAsync(cancellationToken);
        var packages = Directory.CreateDirectory(Path.Combine(scope.Root, "packages")).FullName;
        var shims = new PackageShims(scope.Root);
        var manifests = ReadManifests(packages);
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

            var selector = requestedVersion ?? ReadSelector(Path.Combine(packages, package));
            var release = await repository.ResolveAsync(package, selector ?? "latest", architecture, cancellationToken);
            foreach (var dependency in release.Dependencies)
            {
                await ResolveAsync(dependency.Id);
            }

            var needsInstallation = !IsInstalled(packages, release);
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
            package == id || ReadSelector(Path.Combine(packages, package)) is not null).ToHashSet();
        var publicCommands = PackageShims.PublicCommands(manifests, explicitIds);
        var dependencyCommands = manifests.Keys.ToDictionary(package => package,
            package => PackageShims.DependencyCommands(manifests, package));
        PackageShims.CheckUnmanagedCommands(shims.PublicDirectory, publicCommands.Keys);
        foreach (var (package, commands) in dependencyCommands)
        {
            PackageShims.CheckUnmanagedCommands(shims.DependencyDirectory(package), commands.Keys);
        }

        using var transaction = new InstallTransaction(scope.Root);
        var messages = new List<string>();
        foreach (var release in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(packages, release.Id);
            var selector = release.Id == id ? version : ReadSelector(destination);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(manifests[release.Id]);
            var dependencies = shims.DependencyDirectory(release.Id);
            var commands = dependencyCommands[release.Id];
            shims.Sync(dependencies, commands, dependencyCommands, transaction);
            var installed = IsInstalled(packages, release);
            if (installed)
            {
                transaction.WriteFile(Path.Combine(destination, ManifestFile), manifest);
                if (selector is not null)
                {
                    transaction.WriteFile(Path.Combine(destination, InstallSelector), Encoding.UTF8.GetBytes(selector));
                }
            }
            else
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
                    commands.Count == 0 ? null : dependencies, powerShell, bootstrap, extractor, selector,
                    manifest, cancellationToken);
                transaction.PublishPackage(staged, destination);
            }

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
        cancellationToken.ThrowIfCancellationRequested();
        List<string> warnings = publicCommands.Count == 0 ? [] : scope.RegisterShims(shims.PublicDirectory,
            plan.Where(release => explicitIds.Contains(release.Id)).SelectMany(release => release.Commands.Keys), transaction);
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

    private static string? ReadSelector(string destination)
    {
        var path = Path.Combine(destination, InstallSelector);
        return File.Exists(Path.Combine(destination, ManifestFile)) && File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static Dictionary<string, PackageManifest> ReadManifests(string packages)
    {
        var manifests = new Dictionary<string, PackageManifest>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(packages))
        {
            var path = Path.Combine(directory, ManifestFile);
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllBytes(path)) ?? throw new JsonException();
                var dependencies = manifest.Dependencies.Select(dependency => new PackageDependency(dependency.Id,
                    new Dictionary<string, string>(dependency.Rename, StringComparer.OrdinalIgnoreCase))).ToArray();
                manifests.Add(Path.GetFileName(directory), new PackageManifest(dependencies,
                    new Dictionary<string, string>(manifest.Commands, StringComparer.OrdinalIgnoreCase)));
            }
            catch (JsonException error)
            {
                throw new IOException($"Invalid package metadata in '{path}'.", error);
            }
        }
        return manifests;
    }

    private static bool IsInstalled(string packages, PackageRelease release)
    {
        var destination = Path.Combine(packages, release.Id);
        var marker = Path.Combine(destination, InstallMarker);
        return File.Exists(Path.Combine(destination, ManifestFile)) &&
            File.Exists(marker) && File.ReadAllText(marker) == release.Version &&
            release.Commands.Values.All(command => File.Exists(Path.Combine(destination, command))) &&
            PackageArchive.HasToolFiles(destination, release.Id, release.Commands);
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

    private async Task<string> StagePackageAsync(PackageRelease release, string staging, string? dependencies,
        string? powerShell, string? bootstrap, string? extractor, string? selector, byte[] manifest,
        CancellationToken cancellationToken)
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

        await File.WriteAllTextAsync(Path.Combine(stagedPackage, InstallMarker), release.Version, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(stagedPackage, ManifestFile), manifest, cancellationToken);
        var selectorPath = Path.Combine(stagedPackage, InstallSelector);
        if (selector is null)
        {
            File.Delete(selectorPath);
        }
        else
        {
            await File.WriteAllTextAsync(selectorPath, selector, cancellationToken);
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
