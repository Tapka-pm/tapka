using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace Tapka;

internal sealed class PackageInstaller(InstallScope scope, PackageRepository repository, HttpClient http, string? systemPowerShell,
    CatalogCache? catalog = null, bool refreshCatalog = false)
{
    public Task InstallAsync(string id, string version, string architecture, CancellationToken cancellationToken,
        bool verifyTapka = false, bool trackLatest = false) =>
        ApplyAsync([id], version, architecture, cancellationToken, verifyTapka, trackLatest, null);

    public Task UpgradeAsync(string[] ids, string architecture, CancellationToken cancellationToken, string? sourceOverride = null) =>
        ApplyAsync(ids, null, architecture, cancellationToken, false, false, sourceOverride);

    private async Task ApplyAsync(string[] ids, string? version, string architecture, CancellationToken cancellationToken,
        bool verifyTapka, bool trackLatest, string? sourceOverride)
    {
        using var installLock = await scope.AcquireInstallLockAsync(cancellationToken);
        using var inventory = InstalledPackages.OpenForInstall(scope, cancellationToken);
        Directory.CreateDirectory(scope.PackagesDirectory);
        var upgrade = version is null;
        var installed = inventory.List().ToDictionary(package => package.Id);
        var currentManifests = inventory.ReadManifests(installed.Keys);
        var manifests = new Dictionary<string, PackageManifest>(currentManifests);
        var selected = new Dictionary<string, InstalledPackage>(installed);
        var shims = new PackageShims(scope.Root);
        var plan = new List<(PackageRelease Release, PackageRepository Repository)>();
        var downloads = new HashSet<string>();
        var pinned = new SortedSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>();
        var resolved = new HashSet<string>();
        var repositories = new Dictionary<string, PackageRepository>(StringComparer.Ordinal);

        string PackageDirectory(InstalledPackage package) => Path.Combine(scope.Root, package.PackagePath);

        async Task<PackageRepository> ReadRepositoryAsync(string source)
        {
            if (!repositories.TryGetValue(source, out var result))
            {
                result = catalog is null
                    ? new PackageRepository(http, source)
                    : await catalog.OpenAsync(source, refreshCatalog, cancellationToken);
                repositories.Add(source, result);
            }
            return result;
        }

        string? ExistingPowerShell() => selected.TryGetValue("powershell", out var package) &&
            manifests["powershell"].Commands.TryGetValue("pwsh.exe", out var target) &&
            File.Exists(Path.Combine(PackageDirectory(package), target))
                ? Path.Combine(PackageDirectory(package), target) : systemPowerShell;

        var powerShell = ExistingPowerShell();

        async Task ResolveAsync(string id, string? requestedVersion, string? parentSource)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolved.Contains(id)) return;
            if (!visiting.Add(id)) throw new IOException($"Dependency cycle at package '{id}'.");

            var previous = installed.GetValueOrDefault(id);
            var source = upgrade ? sourceOverride ?? previous?.Repository ?? (previous is null ? parentSource : null)
                : repository.Source;
            if (previous is { IsPinned: true } &&
                (requestedVersion is null || !refreshCatalog && requestedVersion == previous.Version))
            {
                var manifest = currentManifests[id];
                CheckInstalled(previous, manifest);
                if (upgrade) pinned.Add(id);
                foreach (var dependency in manifest.Dependencies)
                {
                    await ResolveAsync(dependency.Id, null, source);
                }
            }
            else
            {
                if (source is null) throw new IOException($"The repository for '{id}' is unknown. Specify --repository.");
                var origin = await ReadRepositoryAsync(source);
                var release = await origin.ResolveAsync(id, requestedVersion ?? previous?.Selector ?? "latest",
                    architecture, cancellationToken);
                var manifest = new PackageManifest(release.Dependencies, release.Commands);
                var existing = previous?.Version == release.Version ? previous : inventory.FindVersion(id, release.Version);
                var relativePath = Path.Combine("packages", id, release.Version);
                if (existing is not null)
                {
                    if (existing.Version != release.Version ||
                        existing.Architecture is not null && existing.Architecture != architecture ||
                        existing.Repository is not null && existing.Repository != origin.Source &&
                            !string.Equals(existing.Checksum, release.Checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException($"The saved installation of '{id}' {release.Version} conflicts with the requested release.");
                    }
                    CheckInstalled(existing, manifest);
                }
                else if (Directory.Exists(Path.Combine(scope.Root, relativePath)) || File.Exists(Path.Combine(scope.Root, relativePath)))
                {
                    throw new IOException($"Package directory '{relativePath}' already exists without a registered installation.");
                }
                else
                {
                    downloads.Add(id);
                }

                var selector = requestedVersion is null ? previous?.Selector :
                    trackLatest && previous is not { IsPinned: true } ? previous?.Selector ?? "latest" : requestedVersion;
                var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var package = existing ?? new InstalledPackage(id, release.Version, selector, architecture, origin.Source,
                    release.DownloadUrl.AbsoluteUri, release.Checksum, now, now, relativePath);
                selected[id] = package with
                {
                    Selector = selector,
                    InstalledAt = previous is null ? now : previous.InstalledAt,
                    VersionChangedAt = previous?.Version == package.Version ? previous.VersionChangedAt : now
                };
                manifests[id] = manifest;

                foreach (var dependency in release.Dependencies)
                {
                    await ResolveAsync(dependency.Id, null, origin.Source);
                }

                if (downloads.Contains(id))
                {
                    if (PackageArchive.Needs7Zip(release.Archive))
                        await ResolveAsync(PackageArchive.ExtractorPackage, null, origin.Source);
                    if (PackageArchive.NeedsBootstrap(release))
                        await ResolveAsync(PackageArchive.BootstrapPackage, null, origin.Source);
                    if (release.Script is not null && powerShell is null)
                    {
                        if (visiting.Contains("powershell"))
                            throw new IOException($"Installing '{id}' requires an existing PowerShell 7 installation.");
                        await ResolveAsync("powershell", null, origin.Source);
                        if (!manifests["powershell"].Commands.ContainsKey("pwsh.exe"))
                            throw new IOException("The PowerShell package must provide 'pwsh.exe' to run installation scripts.");
                    }
                }
                plan.Add((release, origin));
            }

            visiting.Remove(id);
            resolved.Add(id);
        }

        void CheckInstalled(InstalledPackage package, PackageManifest manifest)
        {
            var directory = PackageDirectory(package);
            if (!Directory.Exists(directory) ||
                !manifest.Commands.Values.All(command => File.Exists(Path.Combine(directory, command))) ||
                !PackageArchive.HasToolFiles(directory, package.Id, manifest.Commands))
            {
                throw new IOException($"Saved installation of '{package.Id}' {package.Version} at '{directory}' is incomplete.");
            }
        }

        if (upgrade)
        {
            ids = ids.Length == 0 ? installed.Keys.Order(StringComparer.Ordinal).ToArray() : ids.Distinct().ToArray();
            foreach (var id in ids)
            {
                if (!installed.ContainsKey(id)) throw new IOException($"Package '{id}' is not installed.");
            }
        }
        foreach (var id in ids) await ResolveAsync(id, version, null);
        cancellationToken.ThrowIfCancellationRequested();

        var explicitIds = selected.Values.Where(package => package.Selector is not null).Select(package => package.Id);
        var publicCommands = PackageShims.PublicCommands(manifests, explicitIds);
        var dependencyCommands = manifests.Keys.ToDictionary(id => id, id => PackageShims.DependencyCommands(manifests, id));
        PackageShims.CheckUnmanagedCommands(shims.PublicDirectory, publicCommands.Keys);

        var changes = plan.Select(entry => selected[entry.Release.Id]).Where(package =>
            installed.GetValueOrDefault(package.Id) != package ||
            !SameManifest(currentManifests[package.Id], manifests[package.Id])).ToArray();
        var generation = Guid.NewGuid().ToString("N");
        var publishShims = changes.Length > 0 || (!upgrade && !shims.HasPublicGeneration(publicCommands.Keys));
        using var transaction = new InstallTransaction(scope.Root);
        if (publishShims)
        {
            foreach (var (id, commands) in dependencyCommands)
                shims.WriteDependencies(generation, id, commands, dependencyCommands, selected, transaction);
        }

        string GetTool(string id) => PackageArchive.GetTool(PackageDirectory(selected[id]), id, manifests[id].Commands);
        foreach (var (release, origin) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (downloads.Contains(release.Id))
            {
                var staged = await StagePackageAsync(release, origin, transaction.CreateStagingDirectory(release.Id),
                    dependencyCommands[release.Id].Count == 0 ? null : shims.DependencyDirectory(generation, release.Id),
                    powerShell, PackageArchive.NeedsBootstrap(release) ? GetTool(PackageArchive.BootstrapPackage) : null,
                    PackageArchive.Needs7Zip(release.Archive) ? GetTool(PackageArchive.ExtractorPackage) : null, cancellationToken);
                transaction.PublishPackage(staged, PackageDirectory(selected[release.Id]));
            }
            if (release.Id == "powershell") powerShell = ExistingPowerShell();
        }

        if (verifyTapka || selected.ContainsKey("tapka") && changes.Any(package => package.Id is "tapka" or "dotnet-runtime-11"))
        {
            if (!manifests["tapka"].Commands.TryGetValue("tapka.exe", out var command))
                throw new IOException("The Tapka package must provide 'tapka.exe'.");
            var tapka = PackageShims.GetTarget(manifests, "tapka", command);
            var (executable, prefix, runtime) = shims.Settings(generation, tapka, dependencyCommands, selected);
            var start = new ProcessStartInfo(Path.Combine(scope.Root, executable));
            if (runtime.Length != 0)
            {
                start.Environment["DOTNET_ROOT"] = Path.Combine(scope.Root, runtime);
                start.Environment["DOTNET_ROOT_X64"] = Path.Combine(scope.Root, runtime);
                start.Environment["DOTNET_ROOT_ARM64"] = Path.Combine(scope.Root, runtime);
            }
            if (publishShims && prefix.Length != 0)
                start.Environment["PATH"] = Path.Combine(scope.Root, prefix) + ";" + start.Environment["PATH"];
            start.ArgumentList.Add("--help");
            await InstallProcess.RunAsync(start, "installed tapka", cancellationToken,
                async (process, token) => { await process.StandardOutput.ReadToEndAsync(token); }, TimeSpan.FromSeconds(30));
        }

        foreach (var id in pinned) Console.WriteLine($"Skipping pinned package {id} {installed[id].Version}.");
        if (publishShims)
        {
            using var publication = await shims.LockPublicationAsync(cancellationToken);
            List<string> warnings;
            try
            {
                shims.PreparePublic(generation, publicCommands, dependencyCommands, selected, transaction);
                using var database = inventory.BeginWrite();
                foreach (var package in changes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    inventory.Save(package, manifests[package.Id], database);
                }
                warnings = publicCommands.Count == 0 ? [] : scope.RegisterShims(shims.PublicDirectory,
                    changes.Where(package => package.Selector is not null).SelectMany(package => manifests[package.Id].Commands.Keys), transaction);
                cancellationToken.ThrowIfCancellationRequested();
                shims.Activate(generation, transaction);
                database.Commit();
                transaction.Commit();
            }
            catch
            {
                // A new launcher cannot read the candidate paths until the registry has committed.
                transaction.Dispose();
                throw;
            }
            foreach (var warning in warnings) Console.Error.WriteLine(warning);
        }
        shims.RemoveRetired(publicCommands);
        if (upgrade)
        {
            var updated = changes.Where(package => installed.GetValueOrDefault(package.Id)?.Version != package.Version).ToArray();
            foreach (var package in updated)
            {
                var previous = installed.GetValueOrDefault(package.Id)?.Version;
                Console.WriteLine(previous is null ? $"Installed {package.Id} {package.Version}."
                    : $"{package.Id}: {previous} → {package.Version}");
            }
            Console.WriteLine(updated.Length == 0 ? "All selected packages are up to date." : $"Updated {updated.Length} package(s).");
        }
        else
        {
            if (!plan.Any(entry => entry.Release.Id == ids[0]))
            {
                var package = selected[ids[0]];
                Console.WriteLine($"{package.Id}@{package.Selector} {package.Version} is already installed at \"{PackageDirectory(package)}\".");
            }
            foreach (var (release, _) in plan)
            {
                var package = selected[release.Id];
                var name = package.Selector is null or "latest" ? package.Id : $"{package.Id}@{package.Selector}";
                Console.WriteLine(downloads.Contains(release.Id)
                    ? $"Installed {name} {package.Version} at \"{PackageDirectory(package)}\"."
                    : $"{name} {package.Version} is already installed at \"{PackageDirectory(package)}\".");
            }
        }
    }

    private static bool SameManifest(PackageManifest left, PackageManifest right) =>
        SameEntries(left.Commands, right.Commands) && left.Dependencies.Length == right.Dependencies.Length &&
        left.Dependencies.All(a => right.Dependencies.Any(b => a.Id == b.Id && SameEntries(a.Rename, b.Rename)));

    private static bool SameEntries(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);

    private async Task<string> StagePackageAsync(PackageRelease release, PackageRepository origin, string staging, string? dependencies,
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
            await File.WriteAllBytesAsync(script, await origin.ReadScriptAsync(release, cancellationToken), cancellationToken);
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
