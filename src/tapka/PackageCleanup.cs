using Tapka.Terminal;

namespace Tapka;

internal sealed class PackageCleanup(InstallScope scope, ITerminal terminal)
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public async Task<int> RunAsync(bool dryRun, CancellationToken token)
    {
        if (!Directory.Exists(scope.Root))
        {
            terminal.Out.WriteLine("No unused packages to remove.");
            return 0;
        }

        using var installLock = await scope.AcquireInstallLockAsync(token);
        using var inventory = dryRun ? InstalledPackages.OpenSnapshot(scope) : InstalledPackages.OpenForInstall(scope, token);
        var recovered = inventory is null || dryRun || await CleanupJournal.RecoverAsync(scope, inventory, terminal.Error.WriteLine, token);

        IReadOnlyList<InstalledPackage> packages;
        IReadOnlyList<InstalledPackage> installedVersions;
        Dictionary<string, PackageManifest> manifests;
        if (inventory is null)
        {
            var legacy = LegacyPackageMetadata.Read(scope.PackagesDirectory, token);
            installedVersions = packages = legacy.Values.Select(entry => entry.Package).ToArray();
            manifests = legacy.ToDictionary(pair => pair.Key, pair => pair.Value.Manifest);
        }
        else
        {
            packages = inventory.List();
            installedVersions = inventory.ListVersions();
            manifests = inventory.ReadManifests(packages.Select(package => package.Id));
        }

        var versions = installedVersions.Select(package => new CleanupPackageFiles(scope.Root, package)).ToArray();
        foreach (var version in versions) version.Resolve();
        var byId = versions.ToLookup(version => version.Package.Id);
        var active = packages.ToDictionary(package => package.Id, package => byId[package.Id].SingleOrDefault(version =>
            PathComparer.Equals(version.Package.Version, package.Version)) ?? throw new IOException($"Missing version record for '{package.Id}'."));
        var paths = versions.Where(version => version.DirectoryPath is not null).Select(version => version.DirectoryPath!).ToHashSet(PathComparer);
        var retained = new Dictionary<CleanupPackageFiles, string>();

        void Keep(CleanupPackageFiles version, string reason)
        {
            if (!retained.TryAdd(version, reason)) return;
            if (active.GetValueOrDefault(version.Package.Id) != version) return;
            var pending = new Stack<CleanupPackageFiles>();
            pending.Push(version);
            while (pending.TryPop(out var package))
            {
                foreach (var dependency in manifests[package.Package.Id].Dependencies)
                {
                    if (!active.TryGetValue(dependency.Id, out var required))
                        throw new IOException($"Missing active dependency '{dependency.Id}' of '{package.Package.Id}'.");
                    if (retained.TryAdd(required, $"required by {package.Package.Id}@{package.Package.Version}")) pending.Push(required);
                }
            }
        }

        foreach (var (id, version) in active)
        {
            var reason = id == "tapka" ? "active Tapka" : version.Package.Selector is not null ? "explicitly installed" :
                id is "powershell" or PackageArchive.ExtractorPackage or PackageArchive.BootstrapPackage ? "installation tool" : null;
            if (reason is not null) Keep(version, reason);
        }
        var roots = retained.Keys.ToHashSet();

        if (dryRun)
        {
            foreach (var pending in CleanupJournal.PendingSources(scope.Root, inventory))
            {
                var owner = versions.Where(version => version.DirectoryPath is { } path &&
                    (PathComparer.Equals(path, pending) || CleanupPackageFiles.Contains(path, pending)))
                    .MaxBy(version => version.DirectoryPath!.Length);
                if (owner is not null) Keep(owner, "previous cleanup awaiting recovery");
            }
        }

        foreach (var group in versions.Where(version => version.DirectoryPath is not null).GroupBy(version => version.DirectoryPath, PathComparer))
        {
            if (!group.Skip(1).Any()) continue;
            foreach (var version in group) Keep(version, "more than one version owns this path");
        }
        foreach (var version in versions.Where(version => !retained.ContainsKey(version)))
        {
            version.Inspect(paths, token);
            if (version.Issue is { } issue) Keep(version, issue);
        }

        var shims = new PackageShims(scope.Root);
        if (versions.Any(version => !retained.ContainsKey(version)))
        {
            var history = new CleanupShimHistory(scope.Root, versions, token);
            history.ProtectProcesses(CleanupWindows.Processes(token), versions, Keep);
            foreach (var version in versions.Where(version => !retained.ContainsKey(version)))
            {
                if (CleanupWindows.FileUse(version.Files, token) is { } reason)
                    history.ProtectPackage(version, reason, Keep);
            }

            if (!dryRun && versions.Any(version => !retained.ContainsKey(version)))
            {
                CleanupJournal? committed = null;
                using (await shims.LockPublicationAsync(token))
                {
                    history.ProtectProcesses(CleanupWindows.Processes(token), versions, Keep);
                    foreach (var version in versions.Where(version => !retained.ContainsKey(version)))
                    {
                        if (CleanupWindows.CheckSharing(version.Files, token) is { } reason)
                            history.ProtectPackage(version, reason, Keep);
                    }

                    while (versions.Where(version => !retained.ContainsKey(version)).ToArray() is { Length: > 0 } candidates)
                    {
                        var journal = CleanupJournal.Create(scope.Root, candidates.SelectMany(version => version.Entries));
                        var index = 0;
                        CleanupPackageFiles? staging = null;
                        try
                        {
                            foreach (var version in candidates)
                            {
                                staging = version;
                                foreach (var entry in version.Entries)
                                {
                                    CleanupPackageFiles.EnumerateFiles(entry, token);
                                    journal.Stage(index++);
                                }
                            }
                            staging = null;
                            token.ThrowIfCancellationRequested();
                            journal.Commit(inventory!, candidates.Select(version => version.Version));
                            committed = journal;
                            break;
                        }
                        catch (Exception error) when (staging is not null && error is IOException or UnauthorizedAccessException)
                        {
                            journal.Restore();
                            history.ProtectPackage(staging, $"could not move files ({error.Message})", Keep);
                        }
                        catch
                        {
                            journal.RestoreIfUncommitted(inventory!);
                            throw;
                        }
                    }
                }
                if (committed is not null) recovered &= committed.Purge(inventory!, terminal.Error.WriteLine);
            }
        }

        var count = 0;
        foreach (var version in versions.OrderBy(version => version.Package.Id, StringComparer.Ordinal)
            .ThenBy(version => version.Package.Version, PathComparer))
        {
            var label = $"{version.Package.Id}@{version.Package.Version}";
            if (retained.TryGetValue(version, out var reason))
            {
                if (dryRun || !roots.Contains(version)) terminal.Out.WriteLine($"Keeping {label} ({reason}).");
            }
            else
            {
                count++;
                terminal.Out.WriteLine(dryRun ? $"Would remove {label}." : $"Removed {label}.");
            }
        }
        terminal.Out.WriteLine(count == 0 ? "No unused packages to remove." : dryRun ? $"Would remove {count} package version(s)." :
            $"Removed {count} package version(s).");
        return recovered ? 0 : 1;
    }
}
