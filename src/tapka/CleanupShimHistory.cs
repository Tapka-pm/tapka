using System.Text.Json;

namespace Tapka;

internal sealed class CleanupShimHistory
{
    private sealed record Route(string Target, string? Prefix, string? Runtime, string? Generation);
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly string root;
    private readonly string shimRoot;
    private readonly string publicRoot;
    private readonly string generationsRoot;
    private readonly Dictionary<string, CleanupPackageFiles> owners;
    private readonly Dictionary<string, Dictionary<string, ShimGenerationPackage>> graphs = new(PathComparer);
    private readonly Dictionary<string, List<Route>> publicRoutes = new(PathComparer);
    private readonly Dictionary<string, List<Route>> oldTargets = new(PathComparer);
    private readonly Dictionary<string, List<Route>> oldPrefixes = new(PathComparer);
    private readonly HashSet<string> oldKnownPackages = new(PathComparer);
    private bool incomplete;

    public CleanupShimHistory(string root, IEnumerable<CleanupPackageFiles> versions, CancellationToken token)
    {
        this.root = root;
        shimRoot = Path.Combine(root, "shims");
        publicRoot = Path.Combine(shimRoot, "public");
        generationsRoot = Path.Combine(shimRoot, "generations");
        owners = versions.Where(version => version.DirectoryPath is not null)
            .GroupBy(version => version.DirectoryPath!, PathComparer).ToDictionary(group => group.Key, group => group.First(), PathComparer);

        TryRead(() =>
        {
            foreach (var directory in Directories(generationsRoot))
            {
                token.ThrowIfCancellationRequested();
                var generation = Path.GetFileName(directory);
                if (!Guid.TryParseExact(generation, "N", out _)) continue;

                TryRead(() => ReadGeneration(directory, generation, token));
            }
            ReadOldShims(publicRoot, addPublicRoutes: true, token);
            ReadPrivateShims(Path.Combine(shimRoot, "deps"), token);
        });
    }

    public CleanupPackageFiles? Owner(string path)
    {
        if (!CleanupPackageFiles.Contains(Path.Combine(root, "packages"), path)) return null;
        // A migrated package can contain a separately registered version directory.
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (owners.TryGetValue(current, out var owner)) return owner;
            if (!CleanupPackageFiles.Contains(root, current)) break;
        }
        return null;
    }

    public void ProtectProcesses(IReadOnlyList<CleanupProcess> processes, IReadOnlyList<CleanupPackageFiles> versions,
        Action<CleanupPackageFiles, string> keep)
    {
        var self = processes.FirstOrDefault(process => process.Id == Environment.ProcessId);
        foreach (var process in processes)
        {
            if (process.Id == self?.Id)
            {
                ProtectSelf(keep);
                continue;
            }

            // The shim that started this cleanup invocation is already represented by its child.
            if (process.Id == self?.ParentId && self.Image is { } image && Owner(image)?.Package.Id == "tapka" &&
                process.Image is { } parent && PathComparer.Equals(Path.GetDirectoryName(parent), publicRoot) &&
                PathComparer.Equals(process.Name, "tapka.exe")) continue;

            if (process.Image is null)
            {
                foreach (var version in versions.Where(version => version.Files.Any(file => PathComparer.Equals(Path.GetFileName(file), process.Name))))
                    ProtectPackage(version, "could not inspect a matching process", keep);
                ProtectPublicCommand(process.Name, keep);
            }
            else if (Owner(process.Image) is { } owner)
            {
                ProtectPackage(owner, "running process", keep);
            }
            else if (CleanupPackageFiles.Contains(shimRoot, process.Image))
            {
                ProtectLauncher(process.Image, keep);
            }
        }
    }

    public void ProtectPackage(CleanupPackageFiles version, string reason, Action<CleanupPackageFiles, string> keep)
    {
        keep(version, reason);
        foreach (var graph in graphs.Values)
            ProtectGraph(graph, version, keep);
        if (version.DirectoryPath is { } path && oldTargets.TryGetValue(path, out var routes))
        {
            foreach (var route in routes) ProtectRoute(route, keep);
        }
        if (incomplete) ProtectUncertain(keep, all: true);
    }

    private void ProtectSelf(Action<CleanupPackageFiles, string> keep)
    {
        var owner = Environment.ProcessPath is { } image ? Owner(image) : null;
        if (owner is not null) keep(owner, "running cleanup process");
        var hasGeneration = false;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = entry.Trim().Trim('"');
            if (!CleanupPackageFiles.Contains(shimRoot, path)) continue;
            if (owner is not null && GenerationOf(path) is { } generation && graphs.TryGetValue(generation, out var graph))
                hasGeneration |= ProtectGraph(graph, owner, keep);
            if (oldPrefixes.ContainsKey(path))
            {
                hasGeneration = true;
                ProtectPrefix(path, keep);
            }
        }
        foreach (var variable in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { } path && Owner(Path.TrimEndingDirectorySeparator(path)) is { } runtime)
                keep(runtime, "runtime of the cleanup process");
        }
        if (owner is not null && !hasGeneration && incomplete) ProtectUncertain(keep, all: true);
    }

    private void ProtectLauncher(string image, Action<CleanupPackageFiles, string> keep)
    {
        TryRead(() =>
        {
            if (ReadLauncher(image) is not { } settings) return;
            if (PathComparer.Equals(settings.Target, ShimBinary.ActivePointer))
                ProtectPublicCommand(Path.GetFileName(image), keep);
            else
                ProtectRoute(CreateRoute(settings, GenerationOf(image)), keep);
        });
        if (incomplete) ProtectUncertain(keep, all: true);
    }

    private void ProtectPublicCommand(string command, Action<CleanupPackageFiles, string> keep)
    {
        if (!publicRoutes.TryGetValue(command, out var routes)) return;
        foreach (var route in routes) ProtectRoute(route, keep);
        if (incomplete) ProtectUncertain(keep, all: true);
    }

    private void ProtectRoute(Route route, Action<CleanupPackageFiles, string> keep)
    {
        if (Owner(route.Target) is not { } owner) return;
        keep(owner, "running or launching through a shim");
        if (route.Runtime is { } path && Owner(path) is { } runtime)
            keep(runtime, "runtime used by a running command");
        if (route.Generation is { } generation && graphs.TryGetValue(generation, out var graph) && ProtectGraph(graph, owner, keep))
            return;
        if (route.Prefix is { } prefix) ProtectPrefix(prefix, keep);
        ProtectUncertain(keep, all: false);
    }

    private void ProtectPrefix(string directory, Action<CleanupPackageFiles, string> keep)
    {
        var visited = new HashSet<string>(PathComparer);
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var prefix))
        {
            if (!visited.Add(prefix)) continue;
            if (!oldPrefixes.TryGetValue(prefix, out var routes))
            {
                ProtectUncertain(keep, all: true);
                return;
            }
            foreach (var route in routes)
            {
                if (Owner(route.Target) is { } target) keep(target, "dependency of a running command");
                if (route.Runtime is { } runtimePath && Owner(runtimePath) is { } runtime)
                    keep(runtime, "runtime used by a running command");
                if (route.Prefix is { } nested) pending.Push(nested);
            }
        }
    }

    private bool ProtectGraph(Dictionary<string, ShimGenerationPackage> graph, CleanupPackageFiles version,
        Action<CleanupPackageFiles, string> keep)
    {
        if (!graph.TryGetValue(version.Package.Id, out var start) || !PathComparer.Equals(start.Version, version.Package.Version) ||
            !PathComparer.Equals(start.Path, version.DirectoryPath)) return false;

        var visited = new HashSet<string>(StringComparer.Ordinal) { start.Id };
        var pending = new Stack<string>(start.Dependencies);
        while (pending.TryPop(out var id))
        {
            if (!visited.Add(id)) continue;
            var dependency = graph[id];
            if (owners.TryGetValue(dependency.Path, out var found) && PathComparer.Equals(dependency.Version, found.Package.Version))
                keep(found, "dependency of a running command");
            foreach (var child in dependency.Dependencies) pending.Push(child);
        }
        return true;
    }

    private void ProtectUncertain(Action<CleanupPackageFiles, string> keep, bool all)
    {
        // Old configurations describe executable targets. They cannot describe commandless dependencies.
        foreach (var owner in owners.Values)
        {
            if (all || !oldKnownPackages.Contains(owner.DirectoryPath!))
                keep(owner, "incomplete dependency information for a running shim");
        }
    }

    private void ReadGeneration(string directory, string generation, CancellationToken token)
    {
        var graphFile = Path.Combine(directory, PackageShims.GenerationPackagesFile);
        if (Checked(graphFile))
        {
            var packages = JsonSerializer.Deserialize(File.ReadAllBytes(graphFile), PackageJsonContext.Default.ShimGenerationPackageArray)
                ?? throw new InvalidDataException($"Empty shim package graph '{graphFile}'.");
            if (packages.Any(package => package is null || package.Dependencies is null || string.IsNullOrEmpty(package.Version)))
                throw new InvalidDataException($"Invalid shim package graph '{graphFile}'.");
            var graph = packages.ToDictionary(package => package.Id, package => package with { Path = Absolute(package.Path) });
            if (graph.Values.Any(package => package.Dependencies.Any(id => !graph.ContainsKey(id))))
                throw new InvalidDataException($"Incomplete shim package graph '{graphFile}'.");
            graphs.Add(generation, graph);
        }

        var configurations = Path.Combine(directory, "public");
        foreach (var file in Files(configurations, "*.cfg"))
        {
            token.ThrowIfCancellationRequested();
            var settings = ShimBinary.ReadSettings(File.ReadAllBytes(file))
                ?? throw new InvalidDataException($"Invalid shim settings '{file}'.");
            var route = CreateRoute(settings, generation);
            Add(publicRoutes, Path.GetFileNameWithoutExtension(file), route);
            if (!graphs.ContainsKey(generation)) AddOldRoute(route);
        }
        if (!graphs.ContainsKey(generation)) ReadPrivateShims(Path.Combine(directory, "deps"), token);
        ReadOldShims(Path.Combine(directory, "legacy"), addPublicRoutes: true, token);
    }

    private void ReadPrivateShims(string directory, CancellationToken token)
    {
        foreach (var prefix in Directories(directory))
        {
            var routes = ReadOldShims(prefix, addPublicRoutes: false, token);
            if (routes.Count > 0) oldPrefixes.Add(prefix, routes);
        }
    }

    private List<Route> ReadOldShims(string directory, bool addPublicRoutes, CancellationToken token)
    {
        var routes = new List<Route>();
        foreach (var file in Files(directory, "*.exe"))
        {
            token.ThrowIfCancellationRequested();
            if (ReadLauncher(file) is not { } settings || PathComparer.Equals(settings.Target, ShimBinary.ActivePointer)) continue;
            var route = CreateRoute(settings, null);
            if (addPublicRoutes) Add(publicRoutes, Path.GetFileName(file), route);
            AddOldRoute(route);
            routes.Add(route);
        }
        return routes;
    }

    private ShimSettings? ReadLauncher(string file) => Checked(file) ? ShimBinary.ReadLauncher(File.ReadAllBytes(file)) : null;

    private Route CreateRoute(ShimSettings settings, string? generation) => new(Absolute(settings.Target),
        settings.Prefix.Length == 0 ? null : Absolute(settings.Prefix), settings.Runtime.Length == 0 ? null : Absolute(settings.Runtime), generation);

    private void AddOldRoute(Route route)
    {
        if (Owner(route.Target) is { DirectoryPath: { } path })
        {
            oldKnownPackages.Add(path);
            Add(oldTargets, path, route);
        }
        if (route.Runtime is { } runtimePath && Owner(runtimePath) is { DirectoryPath: { } runtime })
            oldKnownPackages.Add(runtime);
    }

    private static void Add(Dictionary<string, List<Route>> lookup, string key, Route route)
    {
        if (!lookup.TryGetValue(key, out var existing)) lookup.Add(key, existing = []);
        existing.Add(route);
    }

    private string? GenerationOf(string path) => CleanupPackageFiles.Contains(generationsRoot, path)
        ? Path.GetRelativePath(generationsRoot, path).Split(Path.DirectorySeparatorChar, 2)[0] : null;

    private bool Checked(string path) => CleanupPackageFiles.CheckPath(root, Path.GetRelativePath(root, path)) is not null;
    private IEnumerable<string> Directories(string path) => Checked(path) ? Directory.EnumerateDirectories(path) : [];
    private IEnumerable<string> Files(string path, string pattern) => Checked(path)
        ? Directory.EnumerateFiles(path, pattern).Where(Checked) : [];

    private string Absolute(string path)
    {
        var fullPath = Path.GetFullPath(path, root);
        if (Path.IsPathRooted(path) || !CleanupPackageFiles.Contains(root, fullPath))
            throw new InvalidDataException($"Invalid shim path '{path}'.");
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private void TryRead(Action read)
    {
        try { read(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            incomplete = true;
        }
    }
}
