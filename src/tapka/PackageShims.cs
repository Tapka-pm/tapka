namespace Tapka;

internal sealed record PackageManifest(PackageDependency[] Dependencies, Dictionary<string, string> Commands);
internal sealed record ShimTarget(string Package, string Executable);

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
    public string DependencyDirectory(string id) => Path.Combine(root, "shims", "deps", id);

    public static Dictionary<string, ShimTarget> PublicCommands(
        IReadOnlyDictionary<string, PackageManifest> packages, IEnumerable<string> explicitIds)
    {
        var commands = new Dictionary<string, ShimTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in explicitIds)
        {
            foreach (var (name, executable) in packages[id].Commands)
            {
                AddCommand(commands, name, new ShimTarget(id, executable), "the public PATH");
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
                AddCommand(commands, name, new ShimTarget(id, executable), $"dependencies of '{consumer}'");
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
            if (File.Exists(path) && !ShimBinary.IsManaged(File.ReadAllBytes(path)))
            {
                throw new IOException($"Command '{command}' conflicts with an existing executable in '{directory}'.");
            }
        }
    }

    public void Sync(string directory, IReadOnlyDictionary<string, ShimTarget> commands,
        IReadOnlyDictionary<string, Dictionary<string, ShimTarget>> dependencyCommands, InstallTransaction transaction)
    {
        if (commands.Count > 0)
        {
            transaction.EnsureDirectory(directory);
            var rootRelative = Path.GetRelativePath(directory, root);
            foreach (var (command, target) in commands)
            {
                var executable = Path.Combine(directory, command);
                var targetPath = Path.Combine("packages", target.Package, target.Executable);
                var prefix = dependencyCommands[target.Package].Count == 0 ? "" : Path.Combine("shims", "deps", target.Package);
                transaction.WriteFile(executable, ShimBinary.Create(shim.Value, rootRelative, targetPath, prefix));
            }
        }

        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(directory, "*.exe"))
        {
            if (!commands.ContainsKey(Path.GetFileName(path)) && ShimBinary.IsManaged(File.ReadAllBytes(path)))
            {
                transaction.WriteFile(path, null);
            }
        }
        transaction.RemoveEmptyDirectory(directory);
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
