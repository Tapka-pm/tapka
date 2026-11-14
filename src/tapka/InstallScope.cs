using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Tapka;

internal sealed class InstallScope(string root, bool isTest)
{
    private static readonly IntPtr BroadcastWindow = new(0xffff);
    private const uint SettingChange = 0x001a;
    private const uint AbortIfHung = 0x0002;

    public string Root { get; } = root;

    public static InstallScope Create(string? testRoot = null)
    {
#if TAPKA_TEST_BUILD
        if (string.IsNullOrWhiteSpace(testRoot) || !Path.IsPathFullyQualified(testRoot) || testRoot.Contains(';'))
        {
            throw new InvalidOperationException("--root must be an absolute test directory without semicolons.");
        }

        var fullTestRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(testRoot));
#endif
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            throw new InvalidOperationException("Could not locate LocalAppData for the current user.");
        }

        var userRoot = Path.Combine(localAppData, "tapka");
#if TAPKA_TEST_BUILD
        if (fullTestRoot.Equals(userRoot, StringComparison.OrdinalIgnoreCase) ||
            fullTestRoot.StartsWith(userRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullTestRoot.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullTestRoot)!), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("--root must be a separate directory outside the normal tapka installation.");
        }

        return new InstallScope(fullTestRoot, isTest: true);
#else
        if (userRoot.Contains(';'))
        {
            throw new InvalidOperationException("The tapka installation path contains a semicolon and cannot be added to PATH.");
        }

        return new InstallScope(userRoot, isTest: false);
#endif
    }

    public void RegisterShims(string shims, IEnumerable<string> commands)
    {
        string updatedPath;
        string? precedingPath;
        if (isTest)
        {
            var pathFile = Path.Combine(Root, "user-path.txt");
            var currentPath = File.Exists(pathFile) ? File.ReadAllText(pathFile) : "";
            updatedPath = AddShims(currentPath, shims);
            if (currentPath != updatedPath)
            {
                File.WriteAllText(pathFile, updatedPath);
            }

            precedingPath = Environment.GetEnvironmentVariable("Path");
        }
        else
        {
            using var environment = Registry.CurrentUser.CreateSubKey("Environment", writable: true);
            var value = environment.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var currentPath = value switch
            {
                null => "",
                string path => path,
                _ => throw new InvalidOperationException("The user PATH has an unsupported registry value type.")
            };
            var kind = value is null ? RegistryValueKind.ExpandString : environment.GetValueKind("Path");
            var expandVariables = kind == RegistryValueKind.ExpandString;
            updatedPath = AddShims(currentPath, shims, expandVariables);
            if (currentPath != updatedPath)
            {
                environment.SetValue("Path", updatedPath, kind);
                SendMessageTimeout(BroadcastWindow, SettingChange, UIntPtr.Zero, "Environment", AbortIfHung, 1000, out _);
            }

            if (expandVariables)
            {
                updatedPath = Environment.ExpandEnvironmentVariables(updatedPath);
            }

            precedingPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
        }

        foreach (var command in commands)
        {
            var resolved = FindCommand(precedingPath + ";" + updatedPath, command);
            if (resolved is not null && !resolved.Equals(Path.Combine(shims, command), StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"tapka: warning: PATH resolves {command} to \"{resolved}\" before the tapka shim.");
            }
        }
    }

    private static string AddShims(string currentPath, string shims, bool expandVariables = false)
    {
        List<string> entries = currentPath.Length == 0 ? [] : currentPath.Split(';').ToList();
        var found = false;
        entries.RemoveAll(entry =>
        {
            var effectiveEntry = expandVariables ? Environment.ExpandEnvironmentVariables(entry) : entry;
            if (!string.Equals(Normalize(effectiveEntry), shims, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var duplicate = found;
            found = true;
            return duplicate;
        });

        if (!found)
        {
            entries.Add(shims);
        }

        return string.Join(';', entries);
    }

    private static string? FindCommand(string path, string command)
    {
        foreach (var entry in path.Split(';'))
        {
            if (Normalize(entry) is { } directory && File.Exists(Path.Combine(directory, command)))
            {
                return Path.Combine(directory, command);
            }
        }

        return null;
    }

    private static string? Normalize(string entry)
    {
        var path = entry.Trim().Trim('"');
        if (path.Length == 0)
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
