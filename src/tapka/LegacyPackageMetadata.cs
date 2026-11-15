using System.Text.Json;

namespace Tapka;

internal static class LegacyPackageMetadata
{
    private const string VersionFile = ".tapka-version";
    private const string SelectorFile = ".tapka-selector";
    public const string ManifestFile = ".tapka-package.json";

    public static Dictionary<string, (InstalledPackage Package, PackageManifest Manifest)> Read(
        string packagesDirectory, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (InstalledPackage, PackageManifest)>();
        if (!Directory.Exists(packagesDirectory))
        {
            return result;
        }

        foreach (var directory in Directory.EnumerateDirectories(packagesDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(directory, ManifestFile);
            var versionPath = Path.Combine(directory, VersionFile);
            var selectorPath = Path.Combine(directory, SelectorFile);
            var hasSelector = File.Exists(selectorPath);
            if (!File.Exists(manifestPath) && !File.Exists(versionPath) && !hasSelector)
            {
                continue;
            }

            var id = Path.GetFileName(directory);
            var manifest = ReadManifest(manifestPath, id);
            var version = ReadValue(versionPath);
            var selector = hasSelector ? ReadValue(selectorPath) : null;
            result.Add(id, (new InstalledPackage(id, version, selector, null, null, null, null, null, null), manifest));
        }
        return result;
    }

    private static string ReadValue(string path)
    {
        var value = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Empty package metadata in '{path}'.")
            : value;
    }

    private static PackageManifest ReadManifest(string path, string id)
    {
        try
        {
            PackageRepository.ValidateId(id);
            var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(path), PackageJsonContext.Default.PackageManifest);
            if (manifest?.Commands is null || manifest.Dependencies is null)
            {
                throw new InvalidDataException("Expected commands and dependencies.");
            }

            var commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, target) in manifest.Commands)
            {
                commands.Add(PackageRepository.NormalizeCommand(name), PackageRepository.RelativePath(target));
            }

            var dependencies = new List<PackageDependency>();
            var ids = new HashSet<string>();
            foreach (var dependency in manifest.Dependencies)
            {
                if (dependency?.Rename is null || !ids.Add(PackageRepository.ValidateId(dependency.Id)))
                {
                    throw new InvalidDataException("Invalid or duplicate dependency.");
                }
                var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, target) in dependency.Rename)
                {
                    rename.Add(PackageRepository.NormalizeCommand(name), PackageRepository.NormalizeCommand(target));
                }
                dependencies.Add(new PackageDependency(dependency.Id, rename));
            }
            return new PackageManifest(dependencies.ToArray(), commands);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException($"Invalid package metadata in '{path}': {error.Message}", error);
        }
    }
}
