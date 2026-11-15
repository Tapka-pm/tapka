using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Tapka;

internal sealed class InstallScope(string root, bool isTest)
{
    private static readonly IntPtr BroadcastWindow = new(0xffff);
    private const uint SettingChange = 0x001a;
    private const uint AbortIfHung = 0x0002;

    public string Root { get; } = root;
    public string PackagesDirectory => Path.Combine(Root, "packages");

    public Task<FileStream> AcquireInstallLockAsync(CancellationToken cancellationToken) =>
        AcquireLockAsync(Path.Combine(Root, ".install.lock"), TimeSpan.FromMinutes(10), cancellationToken);

    public static async Task<FileStream> AcquireLockAsync(string lockFile, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return File.Open(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                if (elapsed.Elapsed >= timeout)
                {
                    throw new IOException($"Timed out waiting for Tapka to release '{Path.GetFileName(lockFile)}'.", error);
                }
                await Task.Delay(50, cancellationToken);
            }
        }
    }

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

    public List<string> RegisterShims(string shims, IEnumerable<string> commands, InstallTransaction transaction)
    {
        if (isTest)
        {
            var pathFile = Path.Combine(Root, "user-path.txt");
            var currentPath = File.Exists(pathFile) ? File.ReadAllText(pathFile) : "";
            var updatedPath = AddShims(currentPath, shims);
            var warnings = GetWarnings(Environment.GetEnvironmentVariable("Path"), updatedPath, shims, commands);
            if (currentPath != updatedPath)
            {
                transaction.WriteFile(pathFile, Encoding.UTF8.GetBytes(updatedPath));
            }

            return warnings;
        }

        using var environment = Registry.CurrentUser.CreateSubKey("Environment", writable: true);
        var value = environment.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var userPath = value switch
        {
            null => "",
            string path => path,
            _ => throw new InvalidOperationException("The user PATH has an unsupported registry value type.")
        };
        var kind = value is null ? RegistryValueKind.ExpandString : environment.GetValueKind("Path");
        var expandVariables = kind == RegistryValueKind.ExpandString;
        var updatedUserPath = AddShims(userPath, shims, expandVariables);
        var effectivePath = expandVariables ? Environment.ExpandEnvironmentVariables(updatedUserPath) : updatedUserPath;
        var userWarnings = GetWarnings(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine),
            effectivePath, shims, commands);
        if (userPath != updatedUserPath)
        {
            environment.SetValue("Path", updatedUserPath, kind);
            transaction.OnRollback(() =>
            {
                using var key = Registry.CurrentUser.CreateSubKey("Environment", writable: true);
                if (!Equals(key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames), updatedUserPath) ||
                    key.GetValueKind("Path") != kind)
                {
                    return;
                }
                if (value is null)
                {
                    key.DeleteValue("Path", throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue("Path", value, kind);
                }
                BroadcastEnvironmentChange();
            });
            BroadcastEnvironmentChange();
        }
        return userWarnings;
    }

    private static void BroadcastEnvironmentChange() =>
        SendMessageTimeout(BroadcastWindow, SettingChange, UIntPtr.Zero, "Environment", AbortIfHung, 1000, out _);

    private static List<string> GetWarnings(string? precedingPath, string updatedPath, string shims, IEnumerable<string> commands)
    {
        var warnings = new List<string>();
        foreach (var command in commands)
        {
            var resolved = FindCommand(precedingPath + ";" + updatedPath, command);
            if (resolved is not null && !resolved.Equals(Path.Combine(shims, command), StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"tapka: warning: PATH resolves {command} to \"{resolved}\" before the tapka shim.");
            }
        }
        return warnings;
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
