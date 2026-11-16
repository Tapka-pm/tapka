using System.Globalization;
using System.Text.Json;

namespace Tapka;

internal sealed class CleanupJournal(string root, string batch, string[] entries)
{
    private const string Prefix = ".cleanup-";
    private const string Plan = "paths.json";
    private string DirectoryPath => Path.Combine(root, Prefix + batch);
    private string Staging => Path.Combine(DirectoryPath, "files");

    private static IEnumerable<string> Journals(string root) => Directory.EnumerateDirectories(root, Prefix + "*")
        .Where(path => Guid.TryParseExact(Path.GetFileName(path)[Prefix.Length..], "N", out _));

    public static bool HasPending(string root) => Directory.Exists(root) &&
        Journals(root).Any(path => File.Exists(Path.Combine(path, Plan)));

    public static CleanupJournal Create(string root, IEnumerable<string> paths)
    {
        var journal = new CleanupJournal(root, Guid.NewGuid().ToString("N"),
            paths.Select(path => Path.GetRelativePath(root, path)).ToArray());
        Directory.CreateDirectory(journal.Staging);
        var temporary = Path.Combine(journal.DirectoryPath, "paths.tmp");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, journal.entries, PackageJsonContext.Default.StringArray);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, Path.Combine(journal.DirectoryPath, Plan));
        return journal;
    }

    public static async Task<bool> RecoverAsync(InstallScope scope, InstalledPackages inventory, Action<string> diagnostic,
        CancellationToken token)
    {
        var recovered = true;
        foreach (var journal in ReadJournals(scope.Root))
        {
            token.ThrowIfCancellationRequested();
            if (!inventory.WasCleanupCommitted(journal.Batch))
            {
                using var publication = await new PackageShims(scope.Root).LockPublicationAsync(token);
                journal.Restore();
                continue;
            }
            recovered &= journal.Purge(inventory, diagnostic);
        }
        return recovered;
    }

    public static IEnumerable<string> PendingSources(string root, InstalledPackages? inventory)
    {
        foreach (var journal in ReadJournals(root))
        {
            if (inventory?.WasCleanupCommitted(journal.Batch) == true) continue;
            for (var index = 0; index < journal.entries.Length; index++)
            {
                var staged = Path.GetRelativePath(root, journal.Destination(index));
                if (CleanupPackageFiles.CheckPath(root, staged) is not null)
                    yield return Path.Combine(root, journal.Source(index));
            }
        }
    }

    private static IEnumerable<CleanupJournal> ReadJournals(string root)
    {
        foreach (var directory in Journals(root))
        {
            var plan = Path.Combine(directory, Plan);
            if (CleanupPackageFiles.CheckPath(root, Path.GetRelativePath(root, plan)) is null) continue;
            string[] paths;
            try
            {
                paths = JsonSerializer.Deserialize(File.ReadAllBytes(plan), PackageJsonContext.Default.StringArray)
                    ?? throw new InvalidDataException($"Invalid cleanup journal '{plan}'.");
            }
            catch (JsonException error)
            {
                throw new InvalidDataException($"Invalid cleanup journal '{plan}'.", error);
            }
            yield return new CleanupJournal(root, Path.GetFileName(directory)[Prefix.Length..], paths);
        }
    }

    private string Batch => batch;
    private readonly string[] entries = entries;

    public void Stage(int index)
    {
        var relative = Source(index);
        var attributes = CleanupPackageFiles.CheckPath(root, relative)
            ?? throw new IOException($"'{relative}' disappeared during cleanup");
        Move(Path.Combine(root, relative), Destination(index), attributes);
    }

    public void Commit(InstalledPackages inventory, IEnumerable<PackageVersion> versions) => inventory.CommitCleanup(batch, versions);

    public void RestoreIfUncommitted(InstalledPackages inventory)
    {
        if (!inventory.WasCleanupCommitted(batch)) Restore();
    }

    public void Restore()
    {
        for (var index = entries.Length - 1; index >= 0; index--)
        {
            var staged = Destination(index);
            var attributes = CleanupPackageFiles.CheckPath(root, Path.GetRelativePath(root, staged));
            if (attributes is null) continue;
            var relative = Source(index);
            if (CleanupPackageFiles.CheckPath(root, relative) is not null)
                throw new IOException($"Cannot restore '{relative}' because the original path is occupied.");
            Move(staged, Path.Combine(root, relative), attributes.Value);
        }
        DeleteStaging(committed: false);
    }

    public bool Purge(InstalledPackages inventory, Action<string> diagnostic)
    {
        try
        {
            DeleteStaging(committed: true);
            inventory.ForgetCleanup(batch);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            diagnostic($"tapka: could not empty cleanup directory '{DirectoryPath}': {error.Message}");
            return false;
        }
    }

    private void DeleteStaging(bool committed)
    {
        if (CleanupPackageFiles.CheckPath(root, Path.GetRelativePath(root, Staging)) is not null)
        {
            if (committed)
            {
                var files = new List<string>();
                CleanupPackageFiles.EnumerateFiles(Staging, CancellationToken.None, files);
                foreach (var file in files)
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0) CleanupWindows.DeleteReadOnlyFile(file);
                }
            }
            Directory.Delete(Staging, recursive: committed);
        }
        File.Delete(Path.Combine(DirectoryPath, Plan));
        Directory.Delete(DirectoryPath);
    }

    private string Source(int index)
    {
        var path = PackageRepository.RelativePath(entries[index]);
        var parts = path.Split(Path.DirectorySeparatorChar);
        if (parts.Length < 2 || !parts[0].Equals("packages", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid source path in cleanup journal '{path}'.");
        PackageRepository.ValidateId(parts[1]);
        return path;
    }

    private string Destination(int index) => Path.Combine(Staging, index.ToString(CultureInfo.InvariantCulture));

    private static void Move(string source, string destination, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) != 0) Directory.Move(source, destination);
        else File.Move(source, destination);
    }
}
