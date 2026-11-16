namespace Tapka;

internal sealed class CleanupPackageFiles(string root, InstalledPackage package)
{
    public PackageVersion Version => new(package.Id, package.Version);
    public InstalledPackage Package => package;
    public string? DirectoryPath { get; private set; }
    public bool Legacy { get; private set; }
    public string? Issue { get; private set; }
    public string[] Entries { get; private set; } = [];
    public List<string> Files { get; } = [];

    public void Resolve()
    {
        try
        {
            PackageRepository.ValidateId(package.Id);
            var relative = PackageRepository.RelativePath(package.PackagePath);
            var legacy = Path.Combine("packages", package.Id);
            Legacy = relative.Equals(legacy, StringComparison.OrdinalIgnoreCase);
            if (!Legacy && (!PackageRepository.IsPathSegment(package.Version) ||
                !relative.Equals(Path.Combine(legacy, package.Version), StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"unexpected registered path '{relative}'");
            DirectoryPath = Path.Combine(root, relative);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            Issue = error.Message;
        }
    }

    public void Inspect(IReadOnlySet<string> otherVersions, CancellationToken token)
    {
        if (Issue is not null) return;

        try
        {
            var directory = DirectoryPath!;
            var attributes = CheckPath(root, Path.GetRelativePath(root, directory));
            if (attributes is null) return;
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException($"'{directory}' is not a directory");

            if (Legacy && otherVersions.Any(path => Contains(directory, path)))
            {
                var versionFile = Path.Combine(directory, ".tapka-version");
                if (Attributes(versionFile) is not { } metadata || (metadata & FileAttributes.ReparsePoint) != 0 ||
                    File.ReadAllText(versionFile) != package.Version)
                    throw new IOException("could not distinguish the legacy files from other versions");
                Entries = Directory.EnumerateFileSystemEntries(directory).Where(path => !otherVersions.Contains(path)).ToArray();
            }
            else
            {
                Entries = [directory];
            }

            foreach (var entry in Entries) EnumerateFiles(entry, token, Files);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Issue = error.Message;
        }
    }

    public static void EnumerateFiles(string entry, CancellationToken token, List<string>? files = null)
    {
        var pending = new Stack<string>();
        pending.Push(entry);
        while (pending.TryPop(out var path))
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            CheckReparsePoint(path, attributes);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
            }
            else
            {
                files?.Add(path);
            }
        }
    }

    public static bool Contains(string directory, string path) => path.Length > directory.Length &&
        path[directory.Length] == Path.DirectorySeparatorChar && path.StartsWith(directory, StringComparison.OrdinalIgnoreCase);

    public static FileAttributes? CheckPath(string root, string relative)
    {
        FileAttributes? attributes = File.GetAttributes(root);
        CheckReparsePoint(root, attributes.Value);
        var current = root;
        foreach (var part in PackageRepository.RelativePath(relative).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            attributes = Attributes(current);
            if (attributes is null) return null;
            CheckReparsePoint(current, attributes.Value);
        }
        return attributes;
    }

    public static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void CheckReparsePoint(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"the path '{path}' is a reparse point");
    }
}
