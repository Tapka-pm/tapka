using System.Text;

namespace Tapka;

internal sealed record PackageManifest(PackageDependency[] Dependencies, Dictionary<string, string> Commands);
internal sealed record ShimTarget(string Package, string Executable, string DotNetRoot);

internal sealed class PackageShims(string root)
{
    private readonly Lazy<byte[]> shim = new(() =>
    {
        using var resource = typeof(PackageShims).Assembly.GetManifestResourceStream("tapka.shim.exe")
            ?? throw new InvalidOperationException("The tapka binary is missing its native command shim.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return buffer.ToArray();
    });

    public string PublicDirectory => Path.Combine(root, "shims", "public");
    public string GenerationDirectory(string generation) => Path.Combine(root, "shims", "generations", generation);
    public string DependencyDirectory(string generation, string id) => Path.Combine(GenerationDirectory(generation), "deps", id);
    private string Pointer => Path.Combine(root, ShimBinary.ActivePointer);

    public Task<FileStream> LockPublicationAsync(CancellationToken token) => InstallScope.AcquireLockAsync(
        Path.Combine(root, "shims", "activation.lock"), TimeSpan.FromSeconds(10), token);

    public string? ActiveGeneration => File.Exists(Pointer) ? File.ReadAllText(Pointer) : null;

    public bool HasPublicGeneration(IEnumerable<string> names) =>
        ActiveGeneration is { } generation && Guid.TryParseExact(generation, "N", out _) &&
        names.All(name => File.Exists(Path.Combine(GenerationDirectory(generation), "public", name + ".cfg")) &&
            File.Exists(Path.Combine(PublicDirectory, name)) && ShimBinary.IsPublic(File.ReadAllBytes(Path.Combine(PublicDirectory, name))));

    public static Dictionary<string, ShimTarget> PublicCommands(
        IReadOnlyDictionary<string, PackageManifest> packages, IEnumerable<string> explicitIds)
    {
        var commands = new Dictionary<string, ShimTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in explicitIds)
        {
            foreach (var (name, executable) in packages[id].Commands)
            {
                AddCommand(commands, name, GetTarget(packages, id, executable), "the public PATH");
            }
        }
        return commands;
    }

    public static Dictionary<string, ShimTarget> DependencyCommands(
        IReadOnlyDictionary<string, PackageManifest> packages, string consumer)
    {
        var direct = packages[consumer].Dependencies.ToDictionary(dependency => dependency.Id);
        var commands = new Dictionary<string, ShimTarget>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.Ordinal) { consumer };
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void AddDependency(string id)
        {
            if (visiting.Contains(id))
            {
                throw new IOException($"Dependency cycle at package '{id}'.");
            }
            if (!visited.Add(id))
            {
                return;
            }
            if (!packages.TryGetValue(id, out var package))
            {
                throw new IOException($"Missing dependency '{id}' for '{consumer}'.");
            }

            var rename = direct.GetValueOrDefault(id)?.Rename;
            if (rename is not null)
            {
                foreach (var original in rename.Keys)
                {
                    if (!package.Commands.ContainsKey(original))
                    {
                        throw new IOException($"Cannot rename command '{original}': dependency '{id}' of '{consumer}' does not provide it.");
                    }
                }
            }
            foreach (var (original, executable) in package.Commands)
            {
                var name = rename?.GetValueOrDefault(original) ?? original;
                AddCommand(commands, name, GetTarget(packages, id, executable), $"dependencies of '{consumer}'");
            }

            visiting.Add(id);
            foreach (var dependency in package.Dependencies)
            {
                AddDependency(dependency.Id);
            }
            visiting.Remove(id);
        }

        foreach (var id in direct.Keys)
        {
            AddDependency(id);
        }
        return commands;
    }

    public static void CheckUnmanagedCommands(string directory, IEnumerable<string> commands)
    {
        foreach (var command in commands)
        {
            var path = Path.Combine(directory, command);
            if (Directory.Exists(path) || File.Exists(path) && !ShimBinary.IsManaged(File.ReadAllBytes(path)))
            {
                throw new IOException($"Command '{command}' conflicts with an existing executable in '{directory}'.");
            }
        }
    }

    public void WriteDependencies(string generation, string id, IReadOnlyDictionary<string, ShimTarget> commands,
        IReadOnlyDictionary<string, Dictionary<string, ShimTarget>> dependencies,
        IReadOnlyDictionary<string, InstalledPackage> packages, InstallTransaction transaction)
    {
        if (commands.Count == 0) return;
        var directory = DependencyDirectory(generation, id);
        transaction.EnsureDirectory(directory);
        foreach (var (command, target) in commands)
        {
            var (executable, prefix, runtime) = Settings(generation, target, dependencies, packages);
            transaction.WriteFile(Path.Combine(directory, command),
                ShimBinary.Create(shim.Value, Path.GetRelativePath(directory, root), executable, prefix, runtime));
        }
    }

    public void PreparePublic(string generation, IReadOnlyDictionary<string, ShimTarget> commands,
        IReadOnlyDictionary<string, Dictionary<string, ShimTarget>> dependencies,
        IReadOnlyDictionary<string, InstalledPackage> packages, InstallTransaction transaction)
    {
        var configuration = Path.Combine(GenerationDirectory(generation), "public");
        transaction.EnsureDirectory(configuration);
        foreach (var (command, target) in commands)
        {
            var (executable, prefix, runtime) = Settings(generation, target, dependencies, packages);
            transaction.WriteFile(Path.Combine(configuration, command + ".cfg"), ShimBinary.CreateSettings(executable, prefix, runtime));
        }

        if (commands.Count > 0) transaction.EnsureDirectory(PublicDirectory);
        var archive = Path.Combine(GenerationDirectory(generation), "legacy");
        foreach (var path in Directory.Exists(PublicDirectory) ? Directory.GetFiles(PublicDirectory, "*.exe") : [])
        {
            var name = Path.GetFileName(path);
            var binary = File.ReadAllBytes(path);
            if (ShimBinary.IsManaged(binary) && !ShimBinary.IsPublic(binary))
            {
                try
                {
                    transaction.MoveFile(path, Path.Combine(archive, name));
                }
                catch (IOException error)
                {
                    throw new IOException($"Could not migrate the shim '{name}'. Close running processes started through it and retry.", error);
                }
            }
        }

        foreach (var command in commands.Keys)
        {
            var path = Path.Combine(PublicDirectory, command);
            if (!File.Exists(path))
            {
                transaction.WriteFile(path, ShimBinary.Create(shim.Value, Path.GetRelativePath(PublicDirectory, root),
                    ShimBinary.ActivePointer, ""));
            }
        }
    }

    public void Activate(string generation, InstallTransaction transaction) =>
        transaction.WriteFile(Pointer, Encoding.ASCII.GetBytes(generation));

    public void RemoveRetired(IReadOnlyDictionary<string, ShimTarget> commands)
    {
        if (!Directory.Exists(PublicDirectory)) return;
        foreach (var path in Directory.GetFiles(PublicDirectory, "*.exe"))
        {
            if (commands.ContainsKey(Path.GetFileName(path))) continue;
            try
            {
                if (ShimBinary.IsPublic(File.ReadAllBytes(path))) File.Delete(path);
            }
            catch (IOException) { } // A running launcher can be removed on a later operation.
            catch (UnauthorizedAccessException) { }
        }
    }

    public (string Executable, string Prefix, string Runtime) Settings(string generation, ShimTarget target,
        IReadOnlyDictionary<string, Dictionary<string, ShimTarget>> dependencies,
        IReadOnlyDictionary<string, InstalledPackage> packages)
    {
        var executable = Path.Combine(packages[target.Package].PackagePath, target.Executable);
        var prefix = dependencies[target.Package].Count == 0 ? "" : Path.GetRelativePath(root,
            DependencyDirectory(generation, target.Package));
        var runtime = target.DotNetRoot.Length == 0 ? "" : Path.Combine(packages["dotnet-runtime-11"].PackagePath, target.DotNetRoot);
        return (executable, prefix, runtime);
    }

    public static ShimTarget GetTarget(IReadOnlyDictionary<string, PackageManifest> packages, string id, string executable)
    {
        const string runtime = "dotnet-runtime-11";
        if (id == "tapka" && packages[id].Dependencies.Any(dependency => dependency.Id == runtime))
        {
            if (!packages.TryGetValue(runtime, out var manifest) ||
                !manifest.Commands.TryGetValue("dotnet.exe", out var host))
            {
                throw new IOException("The .NET runtime package must provide 'dotnet.exe' to run tapka.");
            }
            return new ShimTarget(id, executable, Path.GetDirectoryName(host) is { Length: > 0 } directory ? directory : ".");
        }

        return new ShimTarget(id, executable, "");
    }

    private static void AddCommand(Dictionary<string, ShimTarget> commands, string name, ShimTarget target, string scope)
    {
        if (!commands.TryAdd(name, target))
        {
            throw new IOException(
                $"Command '{name}' in {scope} is provided by both '{commands[name].Package}' and '{target.Package}'.");
        }
    }
}
