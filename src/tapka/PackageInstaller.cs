using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Tapka.Progress;
using Tapka.Terminal;

namespace Tapka;

internal sealed class PackageInstaller(InstallScope scope, PackageRepository repository, HttpClient http, ITerminal terminal, string? systemPowerShell,
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
        if (CleanupJournal.HasPending(scope.Root))
            await CleanupJournal.RecoverAsync(scope, inventory, terminal.Error.WriteLine, cancellationToken);
        Directory.CreateDirectory(scope.PackagesDirectory);
        var upgrade = version is null;
        var installed = inventory.List().ToDictionary(package => package.Id);
        if (upgrade)
        {
            ids = ids.Length == 0 ? installed.Keys.Order(StringComparer.Ordinal).ToArray() : ids.Distinct().ToArray();
            foreach (var id in ids)
            {
                if (!installed.ContainsKey(id)) throw new IOException($"Package '{id}' is not installed.");
            }
        }

        var deps = ids.ToDictionary(id => id, id => new Dep { Name = id }, StringComparer.Ordinal);
        var updated = 0;
        await Dep.RunAsync(terminal, ids.Select(id => deps[id]).ToArray(), ApplyPackagesAsync, cancellationToken);
        if (upgrade)
            terminal.Out.WriteLine(updated == 0 ? "All selected packages are up to date." : $"Updated {updated} package(s).");

        async Task ApplyPackagesAsync(ITerminal output)
        {
            var currentManifests = inventory.ReadManifests(installed.Keys);
            var manifests = new Dictionary<string, PackageManifest>(currentManifests);
            var selected = new Dictionary<string, InstalledPackage>(installed);
            var shims = new PackageShims(scope.Root);
            var plan = new List<(PackageRelease Release, PackageRepository Repository)>();
            var installationTools = new List<(InstalledPackage Package, InstalledPackage Tool)>();
            var downloads = new HashSet<string>();
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
                        : await catalog.OpenAsync(source, refreshCatalog, cancellationToken, output);
                    repositories.Add(source, result);
                }
                return result;
            }

            (string? Path, InstalledPackage? Package) ExistingPowerShell()
            {
                if (selected.TryGetValue("powershell", out var package) &&
                    manifests["powershell"].Commands.TryGetValue("pwsh.exe", out var target))
                {
                    var path = Path.Combine(PackageDirectory(package), target);
                    if (File.Exists(path)) return (path, package);
                }
                return (systemPowerShell, null);
            }

            var powerShell = ExistingPowerShell();

            async Task ResolveAsync(string id, string? requestedVersion, string? parentSource, Dep? parent = null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var alreadyResolved = resolved.Contains(id);
                if (!alreadyResolved && !visiting.Add(id)) throw new IOException($"Dependency cycle at package '{id}'.");

                if (!deps.TryGetValue(id, out var dep))
                {
                    dep = new Dep { Name = id };
                    deps.Add(id, dep);
                }
                parent?.Add(dep);
                if (alreadyResolved) return;
                dep.SetStatus(DepStatus.Awaiting, detail: "Resolving");

                var previous = installed.GetValueOrDefault(id);
                var source = upgrade
                    ? sourceOverride ?? previous?.Repository ?? (previous is null ? parentSource : null) ?? repository.Source
                    : repository.Source;
                if (previous is { IsPinned: true } &&
                    (requestedVersion is null || !refreshCatalog && requestedVersion == previous.Version))
                {
                    var manifest = currentManifests[id];
                    CheckInstalled(previous, manifest);
                    dep.Name = $"{id} {previous.Version}";
                    dep.SetStatus(DepStatus.Installed, detail: "Pinned");
                    foreach (var dependency in manifest.Dependencies)
                    {
                        await ResolveAsync(dependency.Id, null, source, dep);
                    }
                }
                else
                {
                    var origin = await ReadRepositoryAsync(source);
                    var release = await origin.ResolveAsync(id, requestedVersion ?? previous?.Selector ?? "latest",
                        architecture, cancellationToken);
                    dep.Name = $"{id} {release.Version}";
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
                        await ResolveAsync(dependency.Id, null, origin.Source, dep);
                    }

                    if (downloads.Contains(id))
                    {
                        if (PackageArchive.Needs7Zip(release.Archive))
                            await ResolveAsync(PackageArchive.ExtractorPackage, null, origin.Source, dep);
                        if (PackageArchive.NeedsBootstrap(release))
                            await ResolveAsync(PackageArchive.BootstrapPackage, null, origin.Source, dep);
                        if (release.Script is not null && powerShell.Path is null)
                        {
                            if (visiting.Contains("powershell"))
                                throw new IOException($"Installing '{id}' requires an existing PowerShell 7 installation.");
                            await ResolveAsync("powershell", null, origin.Source, dep);
                            if (!manifests["powershell"].Commands.ContainsKey("pwsh.exe"))
                                throw new IOException("The PowerShell package must provide 'pwsh.exe' to run installation scripts.");
                        }
                    }
                    plan.Add((release, origin));
                    dep.SetStatus(DepStatus.Awaiting);
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
            using var transaction = new InstallTransaction(scope.Root, output.Error.WriteLine);
            if (publishShims)
            {
                foreach (var (id, commands) in dependencyCommands)
                    shims.WriteDependencies(generation, id, commands, dependencyCommands, selected, transaction);
            }

            string? GetTool(InstalledPackage? tool) => tool is null ? null :
                PackageArchive.GetTool(PackageDirectory(tool), tool.Id, manifests[tool.Id].Commands);
            foreach (var (release, origin) in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dep = deps[release.Id];
                if (downloads.Contains(release.Id))
                {
                    var package = selected[release.Id];
                    var extractor = PackageArchive.Needs7Zip(release.Archive) ? selected[PackageArchive.ExtractorPackage] : null;
                    var bootstrap = PackageArchive.NeedsBootstrap(release) ? selected[PackageArchive.BootstrapPackage] : null;
                    var staged = await StagePackageAsync(release, origin, transaction.CreateStagingDirectory(release.Id),
                        dependencyCommands[release.Id].Count == 0 ? null : shims.DependencyDirectory(generation, release.Id),
                        powerShell.Path, GetTool(bootstrap), GetTool(extractor), dep, output, cancellationToken);
                    transaction.PublishPackage(staged, PackageDirectory(package));
                    foreach (var tool in new[] { extractor, bootstrap, release.Script is null ? null : powerShell.Package })
                    {
                        if (tool is not null) installationTools.Add((package, tool));
                    }
                    dep.SetStatus(DepStatus.Installing, detail: "Awaiting commit");
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
                await InstallProcess.RunAsync(start, "installed tapka", output, cancellationToken,
                    async (process, token) => { await process.StandardOutput.ReadToEndAsync(token); }, TimeSpan.FromSeconds(30), output.Out.WriteLine);
            }

            if (publishShims)
            {
                using var publication = await shims.LockPublicationAsync(cancellationToken);
                List<string> warnings;
                try
                {
                    shims.PreparePublic(generation, publicCommands, dependencyCommands, selected, transaction);
                    shims.WriteGenerationPackages(generation, selected, manifests, transaction);
                    using var database = inventory.BeginWrite();
                    foreach (var package in changes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        inventory.Save(package, manifests[package.Id], database);
                    }
                    inventory.SaveInstallationTools(installationTools, database);
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
                foreach (var warning in warnings) output.Error.WriteLine(warning);
            }
            foreach (var (release, _) in plan)
            {
                var previous = installed.GetValueOrDefault(release.Id)?.Version;
                var detail = downloads.Contains(release.Id)
                    ? previous is null ? null : $"Updated {previous} -> {release.Version}"
                    : "Already installed";
                deps[release.Id].SetStatus(DepStatus.Installed, detail: detail);
            }
            shims.RemoveRetired(publicCommands);
            if (upgrade)
                updated = changes.Count(package => installed.GetValueOrDefault(package.Id)?.Version != package.Version);
        }
    }

    private static bool SameManifest(PackageManifest left, PackageManifest right) =>
        SameEntries(left.Commands, right.Commands) && left.Dependencies.Length == right.Dependencies.Length &&
        left.Dependencies.All(a => right.Dependencies.Any(b => a.Id == b.Id && SameEntries(a.Rename, b.Rename)));

    private static bool SameEntries(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);

    private async Task<string> StagePackageAsync(PackageRelease release, PackageRepository origin, string staging, string? dependencies,
        string? powerShell, string? bootstrap, string? extractor, Dep dep, ITerminal output, CancellationToken cancellationToken)
    {
        var installerDirectory = Directory.CreateDirectory(Path.Combine(staging, "download")).FullName;
        var installer = Path.Combine(installerDirectory, PackageRepository.GetDownloadFilename(release.DownloadUrl));
        var stagedPackage = Directory.CreateDirectory(Path.Combine(staging, "package")).FullName;
        await DownloadAsync(release, installer, dep, cancellationToken);

        void WriteDiagnostic(string line) => output.Out.WriteLine($"{release.Id}: {line}");

        if (release.Archive is { } archive)
        {
            dep.SetStatus(DepStatus.Installing, detail: "Extracting");
            await PackageArchive.ExtractAsync(archive, installer, stagedPackage, extractor, output, cancellationToken, WriteDiagnostic);
        }
        if (release.Script is not null)
        {
            dep.SetStatus(DepStatus.Installing, detail: "Running install script");
            var script = Path.Combine(staging, "install.ps1");
            await File.WriteAllBytesAsync(script, await origin.ReadScriptAsync(release, cancellationToken), cancellationToken);
            await RunScriptAsync(powerShell ?? throw new IOException("An existing PowerShell 7 installation is required."),
                script, installer, stagedPackage, dependencies, bootstrap, output, cancellationToken, WriteDiagnostic);
        }

        dep.SetStatus(DepStatus.Installing, detail: "Checking package files");
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

    private async Task DownloadAsync(PackageRelease release, string installer, Dep dep, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var sha256 = release.Checksum.Length == 64;
        dep.SetStatus(DepStatus.Downloading);
        try
        {
            using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
                var total = response.Content.Headers.ContentLength;
                var buffer = new byte[81920];
                long downloaded = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    downloaded += count;
                    dep.SetStatus(DepStatus.Downloading,
                        total is > 0 ? Math.Min(1, (double)downloaded / total.Value) : null, $"{downloaded:N0} bytes");
                }
            }

            dep.SetStatus(DepStatus.Downloading, detail: "Verifying checksum");
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

        dep.SetStatus(DepStatus.Downloaded);
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
        string? dependencies, string? bootstrap, ITerminal output, CancellationToken cancellationToken, Action<string> diagnostic)
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

        return InstallProcess.RunAsync(start, "PowerShell installation script", output, cancellationToken, diagnostic: diagnostic);
    }
}
