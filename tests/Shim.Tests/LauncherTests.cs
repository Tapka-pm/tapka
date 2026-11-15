using System.Diagnostics;
using System.Globalization;
using Tapka;

public sealed class LauncherTests : IDisposable
{
    private readonly string temporary = Directory.CreateTempSubdirectory("tapka shim ").FullName;
    private static string Probe => Path.Combine(AppContext.BaseDirectory, "shim-probe.exe");

    [Theory]
    [InlineData(null, @"shims\deps\пример")]
    [InlineData("", @"shims\deps\пример")]
    [InlineData(@"C:\fallback;C:\second", @"shims\deps\пример")]
    [InlineData(@"C:\fallback;C:\second", "")]
    public async Task PreservesProcessBehavior(string? inheritedPath, string prefix)
    {
        var root = Path.Combine(temporary, "дерево Tapka");
        var cwd = Directory.CreateDirectory(Path.Combine(temporary, "рабочий каталог")).FullName;
        const string target = @"packages\пример\имя команды.exe";
        var shim = CreateShim(root, @"shims\public\alias.exe", @"..\..", target, prefix);
        var arguments = new[] { "", "two words", "a\"b", "trailing slash \\", "two \\\\", "Привет 🌍" };
        var parentPath = Environment.GetEnvironmentVariable("PATH");

        var result = await Run(shim, cwd, inheritedPath, ["0x81234567", .. arguments], input: "probe input");

        Assert.Equal(unchecked((int)0x81234567), result.ExitCode);
        Assert.Equal("probe stderr", result.Error.Trim());
        Assert.Contains("stdin=probe input", result.Output.Split('\n', StringSplitOptions.TrimEntries));
        Assert.Equal(Path.Combine(root, target), Assert.Single(ReadValues(result.Output, "module")));
        Assert.Equal(cwd, Assert.Single(ReadValues(result.Output, "cwd")));
        Assert.Equal(arguments, ReadValues(result.Output, "arg"));
        var expectedPath = prefix == "" ? inheritedPath ?? "" : Path.Combine(root, prefix) +
            (string.IsNullOrEmpty(inheritedPath) ? "" : ";" + inheritedPath);
        Assert.Equal(expectedPath, Assert.Single(ReadValues(result.Output, "path")));
        Assert.Equal(parentPath, Environment.GetEnvironmentVariable("PATH"));
        Assert.False(File.Exists(shim + ".target"));
    }

    [Fact]
    public async Task PublicAndDependencyShimsFollowTheMovedTree()
    {
        var previous = Path.Combine(temporary, "before");
        var moved = Path.Combine(temporary, "после переноса");
        const string publicShim = @"shims\public\public.exe";
        const string privateShim = @"shims\deps\a\alias.exe";
        const string publicTarget = @"packages\a\tool.exe";
        const string privateTarget = @"packages\b\tool.exe";
        const string prefix = @"shims\deps\a";
        CreateShim(previous, publicShim, @"..\..", publicTarget, prefix);
        CreateShim(previous, privateShim, @"..\..\..", privateTarget, "");

        Directory.Move(previous, moved);
        var first = await Run(Path.Combine(moved, publicShim), temporary, null, ["0"]);
        var second = await Run(Path.Combine(moved, privateShim), temporary,
            Assert.Single(ReadValues(first.Output, "path")), ["0"]);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(Path.Combine(moved, publicTarget), Assert.Single(ReadValues(first.Output, "module")));
        Assert.Equal(Path.Combine(moved, privateTarget), Assert.Single(ReadValues(second.Output, "module")));
        Assert.Equal(Path.Combine(moved, prefix), Assert.Single(ReadValues(second.Output, "path")));
        Assert.Equal(temporary, Assert.Single(ReadValues(second.Output, "cwd")));
    }

    [Fact]
    public async Task ReportsMissingTargetAndUnpatchedSettings()
    {
        const string target = @"packages\a\tool.exe";
        var shim = CreateShim(temporary, @"shims\public\alias.exe", @"..\..", target, "");
        File.Delete(Path.Combine(temporary, target));
        var missing = await Run(shim, temporary, null, []);
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("cannot launch the installed command", missing.Error);

        File.WriteAllBytes(shim, ShimBinaryTests.ReadTemplate());
        var unpatched = await Run(shim, temporary, null, []);
        Assert.NotEqual(0, unpatched.ExitCode);
        Assert.Contains("invalid embedded shim settings", unpatched.Error);
    }

    [Theory]
    [InlineData("--ctrl-c", 41)]
    [InlineData("--ctrl-break", 42)]
    public async Task PreservesChildExitCodeOnConsoleInterrupt(string argument, int exitCode)
    {
        var shim = CreateShim(temporary, @"shims\public\alias.exe", @"..\..", @"packages\a\tool.exe", "");

        var result = await Run(Probe, temporary, null, ["--new-console", shim, argument]);

        Assert.Equal(exitCode, result.ExitCode);
    }

    private static string CreateShim(string root, string shimPath, string rootRelative, string target, string prefix)
    {
        var shim = Path.Combine(root, shimPath);
        var executable = Path.Combine(root, target);
        Directory.CreateDirectory(Path.GetDirectoryName(shim)!);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.Copy(Probe, executable);
        File.WriteAllBytes(shim, ShimBinary.Create(ShimBinaryTests.ReadTemplate(), rootRelative, target, prefix));
        return shim;
    }

    private static string[] ReadValues(string output, string name)
    {
        var prefix = name + "=";
        return output.Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => string.Create((line.Length - prefix.Length) / 4, line, (chars, value) =>
            {
                for (var index = 0; index < chars.Length; index++)
                {
                    chars[index] = (char)ushort.Parse(value.AsSpan(prefix.Length + index * 4, 4),
                        NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
            })).ToArray();
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(string exe, string cwd,
        string? inheritedPath, string[] arguments, string? input = null)
    {
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (inheritedPath is null)
        {
            start.Environment.Remove("PATH");
        }
        else
        {
            start.Environment["PATH"] = inheritedPath;
        }
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteLineAsync(input);
            }
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    public void Dispose() => Directory.Delete(temporary, recursive: true);
}
