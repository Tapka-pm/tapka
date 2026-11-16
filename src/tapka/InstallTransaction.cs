namespace Tapka;

internal sealed class InstallTransaction(string root, Action<string>? diagnostic = null) : IDisposable
{
    private readonly string workDirectory = Path.Combine(root, $".install-{Guid.NewGuid():N}");
    private readonly List<Action> undo = [];
    private bool committed;
    private bool disposed;

    public string CreateStagingDirectory(string id) =>
        Directory.CreateDirectory(Path.Combine(workDirectory, id)).FullName;

    public void PublishPackage(string staged, string destination)
    {
        EnsureDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(staged, destination);
        undo.Add(() => Directory.Move(destination, staged));
    }

    public void MoveFile(string source, string destination)
    {
        EnsureDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);
        undo.Add(() => File.Move(destination, source));
    }

    public void EnsureDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }
        EnsureDirectory(Path.GetDirectoryName(directory)!);
        Directory.CreateDirectory(directory);
        undo.Add(() => Directory.Delete(directory));
    }

    public void WriteFile(string path, byte[]? contents)
    {
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if ((before is null) == (contents is null) && before.AsSpan().SequenceEqual(contents))
        {
            return;
        }
        UpdateFile(path, contents);
        undo.Add(() => UpdateFile(path, before));
    }

    public void Commit() => committed = true;

    public void OnRollback(Action action) => undo.Add(action);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var restored = true;
        if (!committed)
        {
            for (var index = undo.Count - 1; index >= 0; index--)
            {
                try
                {
                    undo[index]();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    restored = false;
                    (diagnostic ?? Console.Error.WriteLine)($"tapka: rollback failed: {error.Message}");
                }
            }
        }

        if (restored && Directory.Exists(workDirectory))
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                (diagnostic ?? Console.Error.WriteLine)($"tapka: could not clean up \"{workDirectory}\": {error.Message}");
            }
        }
    }

    private static void UpdateFile(string path, byte[]? contents)
    {
        if (contents is null)
        {
            File.Delete(path);
            return;
        }

        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, contents);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
