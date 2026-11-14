using System.Text;

namespace Tapka;

internal sealed class PackageShims(string directory)
{
    private const string TargetSuffix = ".target";
    private readonly Lazy<byte[]> shim = new(() =>
    {
        using var resource = typeof(PackageShims).Assembly.GetManifestResourceStream("tapka.shim.exe")
            ?? throw new InvalidOperationException("The tapka binary is missing its native command shim.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        return buffer.ToArray();
    });

    public string DirectoryPath => directory;

    public string? FindPowerShell()
    {
        var target = ReadTarget("pwsh.exe");
        if (target is null || Owner(target) != "powershell")
        {
            return null;
        }

        var path = Path.Combine(Path.GetDirectoryName(directory)!, "packages", target);
        return File.Exists(path) ? path : null;
    }

    public void CheckConflicts(IEnumerable<PackageRelease> plan, string packages)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var release in plan)
        {
            foreach (var command in release.Commands.Keys)
            {
                var target = ReadTarget(command);
                var existingOwner = target is null ? null : Owner(target);
                if (existingOwner is null && command.Equals("git.exe", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(packages, "git", ".tapka-version")))
                {
                    // The original Git-only client shipped a shim with its target built in.
                    existingOwner = "git";
                }

                if ((existingOwner is not null && existingOwner != release.Id) ||
                    (existingOwner is null && File.Exists(Path.Combine(directory, command))) ||
                    !owners.TryAdd(command, release.Id))
                {
                    throw new IOException($"Command '{command}' for '{release.Id}' conflicts with an existing command.");
                }
            }
        }
    }

    public void Publish(PackageRelease release)
    {
        var changes = new List<(string Path, byte[]? Before, byte[]? After)>();
        void AddChange(string path, byte[]? contents)
        {
            var previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if ((previous is null) != (contents is null) || !previous.AsSpan().SequenceEqual(contents))
            {
                changes.Add((path, previous, contents));
            }
        }

        if (release.Commands.Count > 0)
        {
            Directory.CreateDirectory(directory);
            foreach (var (command, target) in release.Commands)
            {
                var executable = Path.Combine(directory, command);
                AddChange(executable + TargetSuffix, Encoding.Unicode.GetBytes(Path.Combine(release.Id, target)));
                AddChange(executable, shim.Value);
            }
        }

        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.exe" + TargetSuffix))
            {
                var command = Path.GetFileName(path)[..^TargetSuffix.Length];
                if (!release.Commands.ContainsKey(command) && Owner(File.ReadAllText(path, Encoding.Unicode)) == release.Id)
                {
                    AddChange(Path.Combine(directory, command), null);
                    AddChange(path, null);
                }
            }
        }

        var applied = 0;
        try
        {
            foreach (var (path, _, after) in changes)
            {
                Update(path, after);
                applied++;
            }
        }
        catch
        {
            for (var index = applied - 1; index >= 0; index--)
            {
                var (path, before, _) = changes[index];
                try
                {
                    Update(path, before);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"tapka: could not restore \"{path}\": {error.Message}");
                }
            }

            throw;
        }
    }

    private string? ReadTarget(string command)
    {
        var path = Path.Combine(directory, command + TargetSuffix);
        return File.Exists(path) ? File.ReadAllText(path, Encoding.Unicode) : null;
    }

    private static string Owner(string target) => target.Split('\\', 2)[0];

    private static void Update(string path, byte[]? contents)
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
